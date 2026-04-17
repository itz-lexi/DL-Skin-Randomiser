#define MyAppName "Deadlock Skin Randomiser"
#define MyAppExeName "DL-Skin-Randomiser.exe"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

[Setup]
AppId={{7AFCB8D4-4CEB-42E5-A045-D9FA5A3F0612}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher=Lexi
AppPublisherURL=https://github.com/itz-lexi/DL-Skin-Randomiser
AppSupportURL=https://discord.gg/N8JXJqtSbh
AppUpdatesURL=https://github.com/itz-lexi/DL-Skin-Randomiser/releases
SetupIconFile=..\Assets\AppIcon.ico
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=DL-Skin-Randomiser-v{#AppVersion}-win-x64-setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
