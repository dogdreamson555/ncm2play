# Minimal FFmpeg runtime

This application ships five FFmpeg 9.0.2 shared libraries, built from unmodified upstream sources with GPL, version3 and nonfree disabled. LAME 3.100 and dav1d 1.5.3 are statically linked into the codec library. Compiler runtime code uses the GCC Runtime Library Exception and the MinGW-w64 winpthreads permissive licenses.

The sources and SHA-256 values are fixed in `tools/Build-NativeMedia.ps1`. The configure options are in `tools/native/Build-Ffmpeg.sh` and the installed `build-metadata.json`. The matching release includes `ncm-sources.zip` with the exact source archives and application build instructions. See `../REBUILD.md` for modifying and rebuilding the libraries. No signing key is needed to replace the rebuilt binaries.

The five DLLs provide MP3/FLAC decoding, MP3/FLAC encoding, resampling, AV1/HEVC still-image decoding and pixel conversion. They omit unrelated devices, filters, formats and network protocols.
