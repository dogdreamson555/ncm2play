#!/usr/bin/env bash
set -Eeuo pipefail

die() {
    printf 'Build-Ffmpeg: %s\n' "$*" >&2
    exit 1
}

if [[ -n "${NATIVE_EXTRA_TOOL_PATH:-}" ]]; then
    extra_path="$(cygpath -u -p "$NATIVE_EXTRA_TOOL_PATH")"
    export PATH="$extra_path:$PATH"
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "$script_dir/../.." && pwd -P)"
artifacts_root="$repo_root/artifacts"
work_root="$artifacts_root/native-build/win-x64"
source_root="$work_root/source"
build_root="$work_root/build"
stage_root="$work_root/stage"
output_root="$artifacts_root/native/win-x64"
jobs="${1:-1}"
[[ "$repo_root" != *[[:space:]]* ]] || die "build in a checkout whose absolute path contains no whitespace"

if [[ ! "$jobs" =~ ^[1-9][0-9]*$ ]]; then
    die "job count must be a positive integer"
fi

if [[ "${MSYSTEM:-}" != "UCRT64" ]]; then
    die "run from an MSYS2 UCRT64 login shell"
fi

required_tools=(gcc make pkgconf ar ranlib nm strip objdump strings nasm meson ninja)
missing_tools=()
for tool in "${required_tools[@]}"; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        missing_tools+=("$tool")
    fi
done
if ((${#missing_tools[@]})); then
    die "missing UCRT64 build tools: ${missing_tools[*]}. Install them in the selected MSYS2 environment, then rerun."
fi

compiler_target="$(gcc -dumpmachine)"
if [[ "$compiler_target" != "x86_64-w64-mingw32" ]]; then
    die "expected the MSYS2 UCRT64 x86_64-w64-mingw32 compiler, got $compiler_target"
fi

ffmpeg_source="$source_root/ffmpeg-9.0.2"
lame_source="$source_root/lame-3.100"
dav1d_source="$source_root/dav1d-1.5.3"
for source in "$ffmpeg_source" "$lame_source" "$dav1d_source"; do
    [[ -d "$source" ]] || die "verified source tree is missing: $source"
done

safe_remove() {
    local target="$1"
    local allowed_parent="$2"
    local resolved_target
    resolved_target="$(realpath -m -- "$target")"
    [[ "$resolved_target" == "$allowed_parent/"* && "$resolved_target" == "$repo_root/"* ]] || die "refusing to remove path outside its build area: $target"
    rm -rf -- "$target"
}

safe_remove "$build_root" "$work_root"
safe_remove "$stage_root" "$work_root"
safe_remove "$output_root" "$artifacts_root/native"
mkdir -p "$build_root" "$stage_root" "$output_root"

gcc_version="$(gcc --version | sed -n '1p')"
nasm_version="$(nasm -v)"
meson_version="$(meson --version)"
ninja_version="$(ninja --version)"
make_version="$(make --version | sed -n '1p')"
pkgconf_version="$(pkgconf --version)"
{
    printf 'gcc=%s\n' "$gcc_version"
    printf 'target=%s\n' "$compiler_target"
    printf 'nasm=%s\n' "$nasm_version"
    printf 'meson=%s\n' "$meson_version"
    printf 'ninja=%s\n' "$ninja_version"
    printf 'make=%s\n' "$make_version"
    printf 'pkgconf=%s\n' "$pkgconf_version"
} > "$build_root/toolchain-versions.txt"

windows_repo_root="$(cygpath -m "$repo_root")"
source_flags="-Os -g0 -ffunction-sections -fdata-sections -ffile-prefix-map=$repo_root=. -fdebug-prefix-map=$repo_root=. -ffile-prefix-map=$windows_repo_root=. -fdebug-prefix-map=$windows_repo_root=."
link_flags="-static-libgcc -Wl,--gc-sections"

(
    cd "$lame_source"
    export CC=gcc AR=ar RANLIB=ranlib
    export CFLAGS="$source_flags"
    export LDFLAGS="$link_flags"
    ./configure \
        --host=x86_64-w64-mingw32 \
        --prefix=/usr \
        --disable-shared \
        --enable-static \
        --disable-frontend \
        --disable-decoder
    make -j "$jobs"
    make DESTDIR="$stage_root" install
)

[[ -f "$stage_root/usr/lib/libmp3lame.a" ]] || die "LAME did not install its static library"
[[ -f "$stage_root/usr/include/lame/lame.h" ]] || die "LAME did not install its headers"

dav1d_build="$build_root/dav1d"
(
    export CC=gcc AR=ar RANLIB=ranlib
    export CFLAGS="$source_flags"
    export LDFLAGS="$link_flags"
    meson setup "$dav1d_build" "$dav1d_source" \
        --prefix=C:/usr \
        --libdir=lib \
        --buildtype=minsize \
        --default-library=static \
        -Denable_asm=true \
        -Denable_tools=false \
        -Denable_tests=false \
        -Denable_examples=false
    meson compile -C "$dav1d_build" -j "$jobs"
    meson install -C "$dav1d_build" --destdir "$stage_root"
)

[[ -f "$stage_root/usr/lib/libdav1d.a" ]] || die "dav1d did not install its static library"
[[ -f "$stage_root/usr/include/dav1d/dav1d.h" ]] || die "dav1d did not install its headers"
[[ -f "$stage_root/usr/lib/pkgconfig/dav1d.pc" ]] || die "dav1d did not install its pkg-config metadata"
sed -i 's|^prefix=.*|prefix=/usr|' "$stage_root/usr/lib/pkgconfig/dav1d.pc"

pkg_config_libdir="$stage_root/usr/lib/pkgconfig"
pkg_config_sysroot="$stage_root"
PKG_CONFIG="pkgconf" \
PKG_CONFIG_LIBDIR="$pkg_config_libdir" \
PKG_CONFIG_SYSROOT_DIR="$pkg_config_sysroot" \
    pkgconf --static --exists 'dav1d >= 1.5.3' || die "pkgconf cannot resolve the staged static dav1d library"

ffmpeg_configure_args=(
    --prefix=/usr
    --bindir=/usr/bin
    --libdir=/usr/lib
    --shlibdir=/usr/bin
    --arch=x86_64
    --target-os=mingw32
    --cc=gcc
    --ar=ar
    --ranlib=ranlib
    --nm=nm
    --strip=strip
    --pkg-config=pkgconf
    --pkg-config-flags=--static
    --enable-shared
    --disable-static
    --disable-programs
    --disable-doc
    --disable-debug
    --enable-stripping
    --enable-small
    --disable-everything
    --disable-autodetect
    --disable-network
    --disable-gpl
    --disable-version3
    --disable-nonfree
    --disable-avdevice
    --disable-avfilter
    --disable-hwaccels
    --disable-pthreads
    --enable-w32threads
    --enable-runtime-cpudetect
    --enable-avcodec
    --enable-avformat
    --enable-avutil
    --enable-swresample
    --enable-swscale
    --enable-demuxer=mp3,flac,mov
    --enable-muxer=mp3,flac
    --enable-protocol=file
    --enable-decoder=mp3,flac,hevc,libdav1d
    --enable-encoder=flac,libmp3lame
    --enable-parser=mpegaudio,flac,hevc,av1
    --enable-libdav1d
    --enable-libmp3lame
    --extra-cflags=-I../../stage/usr/include
    --extra-ldflags=-L../../stage/usr/lib
    "--extra-libs=-Wl,-Bstatic,--whole-archive -lwinpthread -Wl,--no-whole-archive,-Bdynamic"
)
printf '%s\n' "${ffmpeg_configure_args[@]}" > "$build_root/ffmpeg-configure-args.txt"

(
    cd "$ffmpeg_source"
    export CC=gcc AR=ar RANLIB=ranlib NM=nm STRIP=strip
    export CFLAGS="$source_flags"
    export LDFLAGS="$link_flags"
    export PKG_CONFIG=pkgconf
    export PKG_CONFIG_LIBDIR="$pkg_config_libdir"
    export PKG_CONFIG_SYSROOT_DIR="$pkg_config_sysroot"
    ./configure "${ffmpeg_configure_args[@]}"
    make -j "$jobs"
    make DESTDIR="$stage_root" install-libs
)

expected_dlls=(
    avcodec-63.dll
    avformat-63.dll
    avutil-61.dll
    swresample-7.dll
    swscale-10.dll
)
staged_dll_root="$stage_root/usr/bin"
for dll in "${expected_dlls[@]}"; do
    [[ -f "$staged_dll_root/$dll" ]] || die "expected FFmpeg ABI library was not installed: $dll"
done

mapfile -t staged_dlls < <(find "$staged_dll_root" -maxdepth 1 -type f -iname '*.dll' -printf '%f\n' | sort -f)
if ((${#staged_dlls[@]} != ${#expected_dlls[@]})); then
    printf 'Unexpected staged DLL set:\n' >&2
    printf '  %s\n' "${staged_dlls[@]}" >&2
    die "expected only the five requested FFmpeg shared libraries"
fi

is_windows_system_dll() {
    local name="${1,,}"
    case "$name" in
        api-ms-win-*.dll|ext-ms-win-*.dll|advapi32.dll|avrt.dll|bcrypt.dll|bcryptprimitives.dll|cfgmgr32.dll|combase.dll|comctl32.dll|comdlg32.dll|crypt32.dll|d3d9.dll|d3d10.dll|d3d11.dll|d3d12.dll|d3d12core.dll|dxgi.dll|dbghelp.dll|dnsapi.dll|dwmapi.dll|gdi32.dll|imm32.dll|iphlpapi.dll|kernel32.dll|kernelbase.dll|mpr.dll|msvcrt.dll|mswsock.dll|ntdll.dll|ole32.dll|oleaut32.dll|oleacc.dll|powrprof.dll|psapi.dll|rpcrt4.dll|secur32.dll|setupapi.dll|shell32.dll|shlwapi.dll|user32.dll|usp10.dll|uxtheme.dll|version.dll|winhttp.dll|winmm.dll|winspool.drv|ws2_32.dll|wldap32.dll)
            return 0
            ;;
        ucrtbase.dll)
            return 0
            ;;
    esac
    return 1
}

external_imports=()
for dll in "${expected_dlls[@]}"; do
    while IFS= read -r imported; do
        [[ -n "$imported" ]] || continue
        imported_lower="${imported,,}"
        [[ -f "$staged_dll_root/$imported" ]] && continue
        is_windows_system_dll "$imported_lower" && continue
        external_imports+=("$dll -> $imported")
    done < <(objdump -p "$staged_dll_root/$dll" | sed -n 's/^[[:space:]]*DLL Name: //p')
done
printf '%s\n' "${external_imports[@]}" > "$build_root/external-runtime-dlls.txt"
if ((${#external_imports[@]})); then
    printf 'Unbundled non-system runtime imports:\n' >&2
    printf '  %s\n' "${external_imports[@]}" >&2
    die "FFmpeg output has non-system runtime DLL dependencies"
fi

windows_repo_root="$(cygpath -m "$repo_root")"
for dll in "${expected_dlls[@]}"; do
    if strings -a "$staged_dll_root/$dll" | grep -F -i -e "$repo_root" -e "$windows_repo_root" >/dev/null; then
        die "build path leaked into $dll"
    fi
    cp -- "$staged_dll_root/$dll" "$output_root/$dll"
done

printf 'Built five FFmpeg shared libraries in %s\n' "$output_root"
