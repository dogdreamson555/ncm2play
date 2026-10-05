#ifndef AppSourceDir
#define AppSourceDir AddBackslash(SourcePath) + "..\artifacts\installer-publish"
#endif

#ifndef AppVersion
#define AppVersion "1.0.0.0"
#endif

#ifndef FileVersion
#define FileVersion "1.0.0.0"
#endif

#ifndef InstallerOutputDir
#define InstallerOutputDir AddBackslash(SourcePath) + "..\artifacts\installer"
#endif

#ifndef IncludeChineseSimplified
#define IncludeChineseSimplified "no"
#endif

[Setup]
AppId={{A968A488-3763-4A74-9E22-2D1CA0AE7A93}
AppName=NCM 转换器
AppVersion={#AppVersion}
AppVerName=NCM 转换器 {#AppVersion}
AppPublisher=dogdreamson555
AppPublisherURL=https://github.com/dogdreamson555/ncm2play
AppSupportURL=https://github.com/dogdreamson555/ncm2play/issues
DefaultDirName={localappdata}\Programs\NcmConverter
DefaultGroupName=NCM 转换器
OutputDir={#InstallerOutputDir}
OutputBaseFilename=NcmConverter-win-x64-setup
SetupLogging=yes
UninstallDisplayName=NCM 转换器
UninstallDisplayIcon={app}\Ncm.App.exe
LicenseFile={#AppSourceDir}\LICENSE
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
VersionInfoProductName=NCM 转换器
VersionInfoCompany=dogdreamson555
VersionInfoDescription=离线将 NCM 音乐文件转换为 MP3 或 FLAC，并保留歌曲信息和封面。
VersionInfoProductVersion={#FileVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoVersion={#FileVersion}
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.dbg,*.mdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NCM 转换器"; Filename: "{app}\Ncm.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\NCM 转换器"; Filename: "{app}\Ncm.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Ncm.App.exe"; Description: "安装完成后启动 NCM 转换器"; Flags: postinstall nowait skipifsilent

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
#if IncludeChineseSimplified == "yes"
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[LangOptions]
DialogFontName=Microsoft YaHei UI
WelcomeFontName=Microsoft YaHei UI
