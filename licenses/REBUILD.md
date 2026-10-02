# Rebuilding the LGPL media libraries

Download `third-party-sources.zip` and the application's automatic **Source code** archive from the same versioned GitHub Release. Extract the application archive to a working tree and run the commands below from its root. The third-party archive includes the native libraries' original sources and the TagLibSharp library sources, without its tests, examples or debug application. Versions, hashes, excluded paths and the native build configuration are recorded under `build/`.

## FFmpeg, LAME, and dav1d

The source archives are in `third-party/` in `third-party-sources.zip`:

- `ffmpeg-9.0.2.tar.xz`
- `lame-3.100.tar.gz`
- `dav1d-1.5.3.tar.xz`

Copy the three archives into `artifacts/native-build/win-x64/sources/` in the application working tree, preserving the archive filenames. `tools/Build-NativeMedia.ps1` verifies each archive against the SHA-256 pinned in that script, then extracts it again and replaces the corresponding source tree. To build a modified library, extract the archive in that source directory, make the changes, and repack the modified source using the same top-level directory name and original archive filename, with the matching `.tar.xz` or `.tar.gz` format. Update the matching SHA-256 in both `tools/Build-NativeMedia.ps1` and `tools/Build-SourceArchive.ps1`, then build. Run `tools/Build-NativeMedia.ps1` to rebuild the five FFmpeg DLLs under `artifacts/native/win-x64/`. Use the toolchain prerequisites in `docs/DEVELOPMENT.md` in that working tree and `tools/native/Build-Ffmpeg.sh`.

## TagLibSharp

The archive contains `third-party/taglib-sharp-2.3.0-source.zip`, derived from commit `b5ae84f2e84087bf160bb0471420200dd2b5d809` (`TaglibSharp-2.3.0.0`). Only files outside the library build were removed; the library sources, build properties and targets, licenses and upstream signing material are retained. Extract it outside the application working tree. The project file uses `LibTargetFrameworks` for its target frameworks and `ReleaseVersion` for the NuGet version.

In PowerShell, set `$tagLibSource` to the extracted repository directory and `$localFeed` to a new, unique directory outside the application working tree. Then pack the modified source:

```powershell
$tagLibProject = Join-Path $tagLibSource 'src/TaglibSharp/TaglibSharp.csproj'
dotnet pack $tagLibProject --configuration Release `
  -p:LibTargetFrameworks=netstandard2.0 `
  -p:ReleaseVersion=2.3.0-local `
  --output $localFeed
```

Change the `TagLibSharp` package version in `src/Ncm.Media/Ncm.Media.csproj` from `2.3.0` to `2.3.0-local`. Add `$localFeed` as a NuGet package source, alongside the normal package sources, before publishing. For example, add it under a temporary source name with `dotnet nuget add source $localFeed --name NcmTagLibLocal`; remove that source after packaging with `dotnet nuget remove source NcmTagLibLocal`. Do not add a user's signing key to the upstream source tree.

Run `tools/Build-Installer.ps1` with the local source enabled; it publishes the application using the default NativeAOT profile and builds the installer. `third-party-sources.zip` is provided alongside the release and is not installed with `NcmConverter-win-x64-setup.exe`.

Before distributing a build with modified TagLibSharp, supply the exact modified source rather than the original upstream source. Repack the modified source as the input `.tar.gz` expected by `$tagLibSource` in `tools/Build-SourceArchive.ps1`, preserving its specified top-level directory, and place it in `artifacts/release-sources/`. Update that source specification with the actual input hash, modified version and provenance; keep the actual distributed ZIP hash separate from the input archive hash. Exclude generated `bin`, `obj`, local caches and private files. Do not label modified library code as the unmodified upstream commit. Update the library version and source attribution in `licenses/README.md`, regenerate `third-party-sources.zip`, and distribute it alongside the modified installer.

The LGPL libraries remain replaceable by rebuilding them from the supplied source. No application signing key is needed to modify, rebuild, or debug those libraries.
