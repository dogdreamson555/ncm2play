# Rebuilding the LGPL media libraries

The release includes the exact source archives used for the bundled media libraries. Rebuild a modified library from those sources, then publish and package the application against that build.

## FFmpeg, LAME, and dav1d

The source archives are in `third-party/` in `ncm-sources.zip`:

- `ffmpeg-9.0.2.tar.xz`
- `lame-3.100.tar.gz`
- `dav1d-1.5.3.tar.xz`

Copy the three archives into `application/artifacts/native-build/win-x64/sources/` in the working tree, preserving the archive filenames. `tools/Build-NativeMedia.ps1` verifies each archive against the SHA-256 pinned in that script, then extracts it again and replaces the corresponding source tree. To build a modified library, extract the archive in that source directory, make the changes, and repack the modified source using the same top-level directory name and original archive filename, with the matching `.tar.xz` or `.tar.gz` format. Update the matching SHA-256 in both `tools/Build-NativeMedia.ps1` and `tools/Build-SourceArchive.ps1`, then build. Run `tools/Build-NativeMedia.ps1` to rebuild the five FFmpeg DLLs under `artifacts/native/win-x64/`. Use the toolchain prerequisites documented by that script and `tools/native/Build-Ffmpeg.sh`.

## TagLibSharp

The archive contains `third-party/taglib-sharp-2.3.0.tar.gz`, pinned to commit `b5ae84f2e84087bf160bb0471420200dd2b5d809` (`TaglibSharp-2.3.0.0`). Extract it outside the application working tree so its files cannot enter the application source archive or installer by accident. The project file uses `LibTargetFrameworks` for its target frameworks and `ReleaseVersion` for the NuGet version.

In PowerShell, set `$tagLibSource` to the extracted repository directory and `$localFeed` to a new, unique directory outside the application working tree. Then pack the modified source:

```powershell
$tagLibProject = Join-Path $tagLibSource 'src/TaglibSharp/TaglibSharp.csproj'
dotnet pack $tagLibProject --configuration Release `
  -p:LibTargetFrameworks=netstandard2.0 `
  -p:ReleaseVersion=2.3.0-local `
  --output $localFeed
```

Change the `TagLibSharp` package version in `src/Ncm.Media/Ncm.Media.csproj` from `2.3.0` to `2.3.0-local`. Add `$localFeed` as a NuGet package source, alongside the normal package sources, before publishing. For example, add it under a temporary source name with `dotnet nuget add source $localFeed --name NcmTagLibLocal`; remove that source after packaging with `dotnet nuget remove source NcmTagLibLocal`. Do not add a user's signing key to the upstream source tree.

Run `dotnet publish` for `src/Ncm.App/Ncm.App.csproj` and `tools/Build-Installer.ps1` with the local source enabled. The installer build uses the default NativeAOT publish profile. `ncm-sources.zip` is an optional release asset that is provided alongside the release and is not installed with `setup.exe`.

Before distributing a build with modified TagLibSharp, repack the modified source as `third-party/taglib-sharp-2.3.0.tar.gz` (excluding generated `bin`, `obj`, local caches and private files), and place that archive in the application's `artifacts/release-sources/` cache. Update the `$tagLibSource` record in `tools/Build-SourceArchive.ps1`: use the actual source hash and modified version name, and update the URL, commit and tag to describe the modified source. If there is no public URL or commit, leave those fields empty and supply the archive in the cache. Do not label modified source with the original upstream commit. Update the TagLibSharp version and source attribution in `licenses/README.md` to describe the modified library as well. Regenerate `ncm-sources.zip` and distribute it alongside the modified installer.

The LGPL libraries remain replaceable by rebuilding them from the supplied source. No application signing key is needed to modify, rebuild, or debug those libraries.
