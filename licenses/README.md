# Third-party licenses

- .NET 10.0.11 NativeAOT runtime: MIT, with third-party notices. Source: https://github.com/dotnet/runtime/tree/v10.0.11 . Notices are in `Microsoft/`.
- C#/WinRT runtime and generated projections: MIT. Source: https://github.com/microsoft/CsWinRT . License: `Microsoft/CsWinRT-MIT.txt`.
- Windows App SDK 2.4.0 component suite (WinUI 2.3.6, Foundation 2.3.9, InteractiveExperiences 2.1.6, Runtime 2.4.0): Microsoft Software License Terms, with third-party notices in `Microsoft/`. The component suite includes the upstream license packaging fix: https://github.com/microsoft/WindowsAppSDK/issues/6654 .

- TagLibSharp 2.3.0: LGPL-2.1-only. Source: https://github.com/mono/taglib-sharp at commit `b5ae84f2e84087bf160bb0471420200dd2b5d809` (`TaglibSharp-2.3.0.0`). License: `TagLibSharp-LGPL-2.1.txt`. NativeAOT releases include this library in the application executable; its corresponding library source is supplied in `third-party-sources.zip`, with tests, examples and the debug application removed. The library sources and build materials remain complete. Application source and build scripts are in the same release's automatic Source code downloads. Normal development builds use a separate DLL. See `REBUILD.md` to build against a modified library.
- SkiaSharp 4.152.1 and SkiaSharp.NativeAssets.Win32 4.152.1: MIT. Source: https://github.com/mono/SkiaSharp . License: `SkiaSharp-MIT.txt`. Native dependency notices: `SkiaSharp-Third-Party-Notices.txt`.
- FFmpeg.AutoGen 9.0.1.1: MIT. Source: https://github.com/Ruslan-B/FFmpeg.AutoGen . License: `FFmpeg.AutoGen-MIT.txt`.
- Release FFmpeg 9.0.2 shared libraries: LGPL-2.1-or-later, built by `tools/Build-NativeMedia.ps1` and `tools/native/Build-Ffmpeg.sh` with GPL, version3 and nonfree disabled. The build includes LAME 3.100 (LGPL-2.0-or-later) and dav1d 1.5.3 (BSD-2-Clause) as static dependencies. Notices are in `FFmpeg-minimal/`; exact source archives and configuration are supplied in `third-party-sources.zip`, and build scripts are in the release's automatic Source code downloads. The installed `build-metadata.json` records the source hashes, toolchain and FFmpeg options.
- The release media libraries also use GCC runtime code under GPL-3.0-or-later with GCC Runtime Library Exception 3.1, and MinGW-w64 winpthreads runtime code under its permissive licenses. Their notices are in `FFmpeg-minimal/`.
- Development and normal tests use the complete `DevEnvy.FFmpeg.Binaries.LGPLv2.Runtime.win-x64` NuGet package 9.0.2. Its original license texts, notices and source offer remain in `FFmpeg-9.0.2-LGPLv2/`. These notices refer to the development backend and are not copied into the minimal release installer. Its build recipe is https://github.com/devenvy/ffmpeg/tree/fcd83b13adbbbdf616f3a48a565f1913dbdfa403 .

## Copyright attribution

| Component | Copyright holders |
| --- | --- |
| .NET runtime | .NET Foundation and Contributors; additional notices in `Microsoft/DotNet-Third-Party-Notices.txt` |
| C#/WinRT and Windows App SDK | Microsoft Corporation; additional notices in `Microsoft/WindowsAppSDK-*-NOTICE.txt` |
| TagLibSharp | 2006–2007 Brian Nickel; 2009–2020 Other contributors. The supplied source retains `AUTHORS` and per-file notices. |
| SkiaSharp | 2015–2016 Xamarin, Inc.; 2017–2018 Microsoft Corporation; native dependency notices in `SkiaSharp-Third-Party-Notices.txt` |
| FFmpeg.AutoGen | 2025 Ruslan Balanukhin (Rationale One) |
| FFmpeg and LAME | Their respective contributors; individual copyright notices are retained in the supplied source archives. |
| dav1d | 2018–2025 VideoLAN and dav1d authors |
| GCC runtime and MinGW-w64 winpthreads | Free Software Foundation, Inc. and the respective runtime contributors; see `FFmpeg-minimal/GCC-COPYING*` and `FFmpeg-minimal/winpthreads-COPYING`. |

Download the matching `third-party-sources.zip` and application Source code from the same [versioned release](https://github.com/dogdreamson555/ncm2play/releases). Distribute the matching source archive at the same download location as the installer. Modification and reverse engineering for debugging changes to the LGPL libraries are permitted. The project license does not replace third-party licenses.
