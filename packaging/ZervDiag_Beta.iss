#define MyAppName "ZervDiag Beta"
#define MyAppVersion "0.15.0-beta.1"
#define MyAppPublisher "ZervDiag"
#define MyAppExeName "ZervDiag.exe"

[Setup]
AppId={{8E2E47D5-7C72-4BC4-AB8E-4E784A6DC185}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ZervDiag
DefaultGroupName=ZervDiag
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=ZervDiag_Beta_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные значки:"; Flags: unchecked

[Files]
Source: "..\dist\ZervDiag\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ZervDiag"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ZervDiag"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить ZervDiag Beta"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Пользовательская база намеренно НЕ удаляется. Она хранится в
; %LOCALAPPDATA%\ZervDiag и переживает обновление/переустановку программы.
Type: filesandordirs; Name: "{app}"
