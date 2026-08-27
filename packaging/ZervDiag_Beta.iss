#define MyAppName "ZervDiag Beta"
#define MyAppVersion "0.15.0-beta.3"
#define MyAppPublisher "ZervDiag"
#define MyAppExeName "ZervDiag.exe"

[Setup]
AppId={{8E2E47D5-7C72-4BC4-AB8E-4E784A6DC185}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Technical Documentation Intelligence
DefaultDirName={autopf}\ZervDiag
DefaultGroupName=ZervDiag
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\dist\installer
OutputBaseFilename=ZervDiag_Beta_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
SetupIconFile=..\build-assets\zervdiag.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion=0.15.0.3
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=ZervDiag Beta Setup
VersionInfoProductName=ZervDiag
VersionInfoProductVersion=0.15.0.3

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.DesktopIcon=Создать ярлык на рабочем столе
english.DesktopIcon=Create a desktop shortcut
russian.AdditionalIcons=Дополнительные значки:
english.AdditionalIcons=Additional icons:
russian.LaunchProgram=Запустить ZervDiag Beta
english.LaunchProgram=Launch ZervDiag Beta

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\ZervDiag\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ZervDiag"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ZervDiag"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Пользовательская база намеренно НЕ удаляется. Она хранится в
; %LOCALAPPDATA%\ZervDiag и переживает обновление/переустановку программы.
Type: filesandordirs; Name: "{app}"
