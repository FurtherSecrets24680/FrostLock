; FrostLock installer script for Inno Setup 6.
; Build with installer\build-installer.cmd, which publishes the app into .\publish first.

#define MyAppName "FrostLock"
; Keep in sync with <Version> in FrostLock.csproj.
#define MyAppVersion "1.0.0"
#define MyAppPublisher "FurtherSecrets"
#define MyAppExeName "FrostLock.exe"
; Keep in sync with <TargetFramework> in FrostLock.csproj. The app ships
; framework-dependent, so Setup installs this runtime when it is missing.
#define DotNetMajor "8"
; Stable redirect to the latest Windows Desktop Runtime (x64) installer from Microsoft.
#define DotNetRuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

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

[Code]
const
  DotNetMajor = '{#DotNetMajor}';
  DotNetRuntimeUrl = '{#DotNetRuntimeUrl}';

// True if Root contains a folder whose name starts with the required major version,
// e.g. "8.0.32" under ...\shared\Microsoft.WindowsDesktop.App\.
function HasDesktopRuntimeIn(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + DotNetMajor + '.*', FindRec) then
  begin
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;
end;

// Fallback for installs that register the runtime but lay the files out elsewhere.
function HasDesktopRuntimeInRegistry(const RootKey: Integer; const SubKey: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, SubKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(DotNetMajor + '.', Names[I]) = 1 then
      begin
        Result := True;
        Break;
      end;
end;

function IsDotNetDesktopRuntimeInstalled: Boolean;
begin
  Result :=
    HasDesktopRuntimeIn(ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    HasDesktopRuntimeIn(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    HasDesktopRuntimeInRegistry(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App') or
    HasDesktopRuntimeInRegistry(HKCU, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App');
end;

// Downloads Url to DestFile. Prefers curl.exe (ships with Windows 10 1803+ and
// Windows 11) and falls back to PowerShell's Invoke-WebRequest.
function DownloadFile(const Url, DestFile: String): Boolean;
var
  ResultCode: Integer;
begin
  if FileExists(DestFile) then
    DeleteFile(DestFile);

  Result := Exec('curl.exe',
    '-L --fail --silent --show-error -o "' + DestFile + '" "' + Url + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0) and FileExists(DestFile);

  if not Result then
    Result := Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -Command ' +
      '"$ProgressPreference=''SilentlyContinue''; Invoke-WebRequest -Uri ''' + Url +
      ''' -OutFile ''' + DestFile + '''' + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
      and (ResultCode = 0) and FileExists(DestFile);
end;

// Runs the downloaded runtime installer silently. 0 = success, 3010 = success but a
// reboot is pending, 1638 = a matching version was already present.
function RunDotNetInstaller(const ExePath: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExePath, '/install /quiet /norestart', '',
    SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode)
    and ((ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1638));
end;

// Runs after the user clicks Install, before any files are copied.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RuntimeExe: String;
begin
  Result := '';
  if IsDotNetDesktopRuntimeInstalled then
    Exit;

  if MsgBox('FrostLock needs the .NET ' + DotNetMajor + ' Desktop Runtime, which is not installed.' + #13#10#13#10 +
            'Download and install it now? This is a one-time download of about 56 MB from Microsoft and needs an internet connection.',
            mbConfirmation, MB_YESNO) = IDNO then
  begin
    Result := 'FrostLock cannot run without the .NET ' + DotNetMajor + ' Desktop Runtime.' + #13#10 +
              'Install it manually from ' + DotNetRuntimeUrl + ' and run this setup again.';
    Exit;
  end;

  RuntimeExe := ExpandConstant('{tmp}\WindowsDesktopRuntime-win-x64.exe');
  if DownloadFile(DotNetRuntimeUrl, RuntimeExe) and RunDotNetInstaller(RuntimeExe) and IsDotNetDesktopRuntimeInstalled then
    Exit;

  Result := 'The .NET ' + DotNetMajor + ' Desktop Runtime could not be installed automatically.' + #13#10 +
            'Please install it manually from ' + DotNetRuntimeUrl + ' and run this setup again.';
end;
