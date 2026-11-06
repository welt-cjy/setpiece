#define AppName "Setpiece"
#define AppVersion "0.1.0"
#define SourceDir "..\dist\payload-loose"

[Setup]
AppId={{7C3E2B8A-5F4D-4A2E-9C1B-3D6E8F0A2B4C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Setpiece
AppPublisherURL=https://github.com/
DefaultDirName={localappdata}\Programs\Setpiece
DefaultGroupName=Setpiece
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Setpiece-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Setpiece.App.exe
CloseApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Setpiece"; Filename: "{app}\Setpiece.App.exe"
Name: "{group}\卸载 Setpiece"; Filename: "{app}\unins000.exe"
Name: "{autodesktop}\Setpiece"; Filename: "{app}\Setpiece.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Setpiece.App.exe"; Description: "启动 Setpiece"; Flags: nowait postinstall skipifsilent
