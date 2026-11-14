#define MyAppName "清场助手"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "OneClickClose"
#define MyAppExeName "OneClickClose.exe"
#define MyAppId "{{47DB0005-9E8C-4F38-A6FE-1E78DE806028}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\OneClickClose
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UsePreviousGroup=no
PrivilegesRequired=lowest
OutputDir=..\dist\installer
OutputBaseFilename=OneClickClose-Setup-{#MyAppVersion}
SetupIconFile=..\src\OneClickClose.App\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
AppMutex=Local\OneClickClose.Native.SingleInstance
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=1.1.0.0
VersionInfoDescription=清场助手安装程序
VersionInfoProductName=清场助手
VersionInfoCompany=OneClickClose

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked
Name: "startup"; Description: "开机自动启动（隐藏到托盘）"; GroupDescription: "启动选项："; Flags: unchecked

[InstallDelete]
Type: files; Name: "{userprograms}\一键收摊\一键收摊.lnk"
Type: files; Name: "{userprograms}\一键收摊\卸载 一键收摊.lnk"
Type: files; Name: "{userprograms}\一键收摊\清场助手.lnk"
Type: files; Name: "{userprograms}\一键收摊\卸载 清场助手.lnk"
Type: files; Name: "{autodesktop}\一键收摊.lnk"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml,*.log,config.json*"
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--hidden"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
