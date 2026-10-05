# 开发、构建与发布

工程使用 C#、.NET 10 和 WinUI 3，目标平台为 Windows 10 1809（内部版本 17763）及以上的 x64 Windows，包括 Windows 11。

应用、App 测试项目及安装器的最低系统版本保持一致；编译仍使用 Windows SDK 26100。Mica 背景在不支持的系统上由 WinUI 回退为纯色。发布前需在 Windows 10 和 Windows 11 分别验证最终安装包的安装、启动、文件选择、拖放、单实例、转换、升级和卸载；本机构建与模块测试不能代替跨系统实测。

## 工程结构

```text
src/Ncm.Core/       NCM 解析、解密、输出规划和队列
src/Ncm.Media/      标签、封面和音频转码
src/Ncm.App/        WinUI 3 界面和应用生命周期
tests/             自动化测试与自制音频夹具
licenses/          第三方许可、声明和源码获取信息
tools/             媒体库、安装器和源码附件构建脚本
```

## 构建与运行

使用 [global.json](../global.json) 固定的 .NET 10.0.303 SDK，以及 Visual Studio / Build Tools 的 C++ 桌面开发工具和 Windows SDK。在仓库根目录执行：

```powershell
dotnet build Ncm.sln -c Release -p:Platform=x64
$outputDir = Resolve-Path 'src\Ncm.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64'
Start-Process (Join-Path $outputDir 'Ncm.App.exe') -WorkingDirectory $outputDir
```

程序在 `%LOCALAPPDATA%\NcmConverter` 追加 `startup.log`；存储不可用时仍允许启动。当前尚未保存和恢复界面设置。

## 测试

```powershell
dotnet test tests\Ncm.Core.Tests\Ncm.Core.Tests.csproj -c Release
dotnet test tests\Ncm.Media.Tests\Ncm.Media.Tests.csproj -c Release
dotnet test tests\Ncm.App.Tests\Ncm.App.Tests.csproj -c Release -p:Platform=x64
```

自动化测试使用合成 NCM 数据和[自制音频夹具](../tests/Ncm.Media.Tests/Fixtures/README.md)。

## 制作安装包

除了上述 SDK 和 C++ 工具，还需要 PowerShell 7.4 或更新版本、Inno Setup 7.0.2 或更新版本（Actions 固定为 7.1.0），以及 MSYS2 UCRT64 工具链。构建目录的完整路径不能包含空格或其他空白字符；在 MSYS2 UCRT64 终端安装构建工具：

```bash
pacman -S --needed base-devel mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-meson mingw-w64-ucrt-x86_64-ninja
```

在仓库根目录的 PowerShell 7 中执行：

```powershell
.\tools\Build-NativeMedia.ps1 -Msys2Root C:\msys64
.\tools\Build-Installer.ps1
.\tools\Build-SourceArchive.ps1
```

构建需要联网恢复 NuGet 包和下载固定版本、校验 SHA-256 的源码。ISCC 未加入 PATH 时，可向 `Build-Installer.ps1` 传入 `-IsccPath`；NASM、Meson、Ninja 在其他目录时，可向 `Build-NativeMedia.ps1` 传入 `-ExtraToolPath`。

安装包输出为 `artifacts/installer/NcmConverter-win-x64-setup.exe`。第三方源码输出为 `artifacts/release-sources/third-party-sources.zip`，包含 FFmpeg、LAME、dav1d 原始源码，精简的 TagLibSharp 源码，哈希和构建记录；

发布使用 NativeAOT、按需引用 Windows App SDK，并只部署 MP3/FLAC 音频与 AVIF/HEIC 图片处理所需的五个 FFmpeg 共享库；开发和普通测试使用完整的 NuGet 媒体后端。安装器采用 LZMA2 整体压缩，排除调试符号。

安装包会清除完整后端附带的 `ffmpeg.exe` 和 `ffprobe.exe`。原生构建保留精简编译，同时通过 `HAVE_GNU_WINDRES=yes` 使用 FFmpeg 上游的 Windows 版本资源；构建脚本核对 DLL 的产品名、发布者、版本及 ABI 主版本。GCC 和 winpthreads 运行库按需静态链接，构建参数记录在 `build-metadata.json` 中。

## GitHub Actions 发布

1. 更新 `src/Ncm.App/Ncm.App.csproj` 的 `Version`（例如 `1.0.4`），完成本次改动的检查并推送到 `main`。
2. 在 [Actions](https://github.com/dogdreamson555/ncm2play/actions/workflows/release.yml) 打开 **Publish release**，选择 `main` 并点击 **Run workflow**。
3. 工作流自动运行三个模块的测试、构建媒体库和安装包，发布 `v<Version>` 并标记为 Latest。只上传 `NcmConverter-win-x64-setup.exe` 和 `third-party-sources.zip`，项目源码自动附带。

工作流使用内置 `GITHUB_TOKEN`。应用与安装器使用同一版本；已有同名 Release 时停止，已有同名标签必须对应本次提交。项目初始版本为 `1.0.0`。
