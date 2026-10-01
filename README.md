# NCM 转换器

使用 C#、.NET 10 和 WinUI 3 开发的 Windows 桌面应用，可将 NCM 文件转换为 MP3 或 FLAC。

## 功能

- 简单模式保留原音频格式和音频数据，写入可用的标签与原封面，按源文件名输出。
- 高级模式提供 MP3 固定码率、FLAC 压缩级别、批量封面、分类、命名和文件标号。
- 封面支持 JPEG、PNG、WebP、AVIF、HEIC/HEIF、BMP 和 ICO；解码器随应用部署，无需系统图片扩展。带独立透明辅助图的 HEIC/AVIF 当前会明确提示不支持。
- 窗口内容区域支持拖入文件或文件夹，递归扫描并去重。列表可拖动排序，点击“选择”后可移除所选；移除不会删除源文件。
- 支持进度、取消和逐项错误处理；一个文件失败不影响其他文件，输出重名时追加序号，不覆盖已有文件。
- 应用只保留一个主窗口，重复启动会激活已有窗口。

专辑标号使用源音频的碟号和曲目号，并校验专辑身份及曲序；缺失、冲突或有歧义时提示改用列表顺序。列表标号按当前队列跨专辑连续编号，文件名前缀与写入标签一致。MP3 转为 FLAC 不会恢复已经损失的音质。

## 工程结构

```text
src/Ncm.Core/       NCM 解析、解密、输出规划和队列
src/Ncm.Media/      标签、封面和音频转码
src/Ncm.App/        WinUI 3 界面和应用生命周期
tests/             自动化测试与自制音频夹具
licenses/          第三方许可、声明和源码获取信息
```

## 构建与运行

目标平台为 Windows 11 x64，开发需要 .NET 10 SDK。在仓库根目录执行：

```powershell
dotnet build Ncm.sln -c Release -p:Platform=x64
$outputDir = Resolve-Path 'src\Ncm.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64'
Start-Process (Join-Path $outputDir 'Ncm.App.exe') -WorkingDirectory $outputDir
```

测试各模块：

```powershell
dotnet test tests\Ncm.Core.Tests\Ncm.Core.Tests.csproj -c Release
dotnet test tests\Ncm.Media.Tests\Ncm.Media.Tests.csproj -c Release
dotnet test tests\Ncm.App.Tests\Ncm.App.Tests.csproj -c Release -p:Platform=x64
```

自动化测试使用合成 NCM 数据和 [自制音频夹具](tests/Ncm.Media.Tests/Fixtures/README.md)，不依赖真实音乐。用于本机手动验证的真实文件放在被 Git 忽略的 `samples/` 中。

## 安装与发布

面向 Windows 11 x64 发布完整离线的 `setup.exe`。安装到当前用户目录，无需管理员权限，也无需预装 .NET、Windows App SDK 或图片扩展。安装器可创建开始菜单和可选的桌面快捷方式；卸载保留用户数据及转换出的音乐。

程序在 `%LOCALAPPDATA%\NcmConverter` 创建 `settings.json` 占位文件并追加 `startup.log`；存储不可用时仍允许启动。当前尚未保存和恢复界面设置。

### 制作安装包

除 .NET 10 SDK 外，还需要 Visual Studio / Build Tools 的 C++ 桌面开发工具、Windows SDK，以及 Inno Setup 7.0.2 或更新版本。媒体库使用 MSYS2 UCRT64 编译，构建目录的完整路径不能包含空格或其他空白字符；在其 UCRT64 终端安装构建工具：

```bash
pacman -S --needed base-devel mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-meson mingw-w64-ucrt-x86_64-ninja
```

在仓库根目录的 PowerShell 中执行：

```powershell
.\tools\Build-NativeMedia.ps1 -Msys2Root C:\msys64
.\tools\Build-Installer.ps1
.\tools\Build-SourceArchive.ps1
```

构建工具需要联网恢复 NuGet 包和下载已固定版本、校验 SHA-256 的源码；生成的安装包安装和运行均不需要联网。ISCC 未加入 PATH 时，可向 `Build-Installer.ps1` 传入 `-IsccPath`。NASM、Meson、Ninja 在其他目录时，可向 `Build-NativeMedia.ps1` 传入 `-ExtraToolPath`。

安装包输出为 `artifacts/installer/setup.exe`，附有 `setup.exe.sha256`。发布时还需提供 `artifacts/release-sources/ncm-sources.zip` 和对应校验文件，包含本项目、TagLibSharp 和媒体库的对应源码及重建材料；它是单独的开发者下载，不进入安装包。修改 LGPL 库的步骤见 [重建说明](licenses/REBUILD.md)。

发布配置使用 NativeAOT、按需引用 Windows App SDK，并只部署 MP3/FLAC 音频与 AVIF/HEIC 图片处理所需的五个 FFmpeg 共享库；开发和普通测试仍可使用完整的 NuGet 媒体后端。安装器采用 LZMA2 整体压缩，排除调试符号。

GitHub Actions 发布流程尚未实现；干净 Windows 11 x64 环境中的完整部署验证仍待完成。

## 许可与来源

项目保留 Apache-2.0 许可证及 NCM 格式参考来源，见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。第三方组件使用各自许可证，见 [第三方声明](licenses/README.md)。

本仓库不包含旧版反编译资料、原程序、真实音乐、私人配置、工具缓存和构建产物。
