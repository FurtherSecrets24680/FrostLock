; FrostLock installer script for Inno Setup 6.
; Build with installer\build-installer.cmd, which publishes the app into .\publish first.

#define MyAppName "FrostLock"
; Keep in sync with <Version> in FrostLock.csproj.
#define MyAppVersion "1.0.0"
#define MyAppPublisher "FurtherSecrets"
#define MyAppExeName "FrostLock.exe"

[Setup]
AppId={{A0D39D7C-ABB0-4E90-B07D-040861367521}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user by default (no UAC); the user may choose an all-users install on the
; "Select Setup Install Mode" page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=FrostLock-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\frostlock.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The app's single-instance mutex, so setup/uninstall can detect a running copy.
AppMutex=FrostLock.SingleInstance.v1
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
