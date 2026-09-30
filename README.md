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

## 发布目录

```powershell
dotnet publish src\Ncm.App\Ncm.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false -o artifacts\publish\win-x64
```

发布时保留整个目录，包括 `Ncm.App.pri`、`*.xbf`、.NET、Windows App SDK 和媒体运行库文件，以及 `licenses/`。程序在 `%LOCALAPPDATA%\NcmConverter` 创建 `settings.json` 占位文件并追加 `startup.log`；存储不可用时仍允许启动。当前尚未保存和恢复界面设置，干净 Windows 11 x64 环境中的完整部署验证仍待完成。

GitHub Actions 发布流程尚未实现。

## 许可与来源

项目保留 Apache-2.0 许可证及 NCM 格式参考来源，见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。第三方组件使用各自许可证，见 [第三方声明](licenses/README.md)。

本仓库不包含旧版反编译资料、原程序、真实音乐、私人配置、工具缓存和构建产物。
