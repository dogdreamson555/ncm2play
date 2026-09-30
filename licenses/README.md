# Third-party licenses

- TagLibSharp 2.3.0: LGPL-2.1-only. Source: https://github.com/mono/taglib-sharp . License text: `TagLibSharp-LGPL-2.1.txt`. The application distributes the unmodified library as a separate DLL.
- SkiaSharp 4.152.1 and SkiaSharp.NativeAssets.Win32 4.152.1: MIT. Source: https://github.com/mono/SkiaSharp . License text: `SkiaSharp-MIT.txt`. Native dependency notices: `SkiaSharp-Third-Party-Notices.txt`.
- FFmpeg.AutoGen 9.0.1.1: MIT. Source: https://github.com/Ruslan-B/FFmpeg.AutoGen . License text: `FFmpeg.AutoGen-MIT.txt`.
- FFmpeg native libraries 9.0.2 for win-x64: the package declares LGPL-2.1-or-later, and its build notice identifies the effective native build license as LGPLv2.1. NuGet package: `DevEnvy.FFmpeg.Binaries.LGPLv2.Runtime.win-x64`. The application calls the shared FFmpeg DLLs in process. License texts, bundled-library notices, and the source offer are in `FFmpeg-9.0.2-LGPLv2/`. The published build recipe is https://github.com/devenvy/ffmpeg/tree/fcd83b13adbbbdf616f3a48a565f1913dbdfa403 and the corresponding FFmpeg source release is https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz .
