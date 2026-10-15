; Inno Setup script for PrivateNetRouting: a per-user install of the self-contained build
; (the .NET runtime is bundled: no framework prerequisite, no elevation). The build is not
; signed — SmartScreen may ask to confirm the first run.
; The artifacts must be built first: packaging/make-release.ps1 fills artifacts/staging (the
; GitHub Actions release workflow runs the same script). The version comes from the command
; line so Directory.Build.props stays the single source: ISCC setup.iss /DAppVersion=x.y.z
; Nothing here touches autostart (rule #4): no Run keys, no scheduled tasks — the tray and
; the background modes start only on an explicit user action inside the application.

#define AppName "PrivateNetRouting"
#define AppExe "PrivateNetRouting.App.exe"

#ifndef AppVersion
#error Pass the version on the command line: ISCC setup.iss /DAppVersion=x.y.z
#endif

[Setup]
AppId={{7D98C8E7-8597-49B0-9AA2-72420C4F6310}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=private
; Per-user: %LOCALAPPDATA%\Programs\PrivateNetRouting, no UAC.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
WizardStyle=modern
OutputDir=..\artifacts
OutputBaseFilename=PrivateNetRouting-{#AppVersion}-setup-win-x64
Compression=lzma2
SolidCompression=yes
; 64-bit Windows only: the bundled runtime is win-x64 (x64compatible also covers Arm64 emulation)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
; A running GUI holds this mutex — the installer offers to close it instead of failing on locked files.
AppMutex=Local\PrivateNetRouting.SingleInstance
CloseApplications=yes

[Files]
Source: "..\artifacts\staging\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
; pnr.exe is a console tool and not on PATH: the shortcut opens a terminal in the install folder (the
; current directory is searched first, so "pnr ..." works there) and prints the command list.
Name: "{group}\{#AppName} CLI"; Filename: "{sys}\cmd.exe"; Parameters: "/k pnr.exe help"; WorkingDir: "{app}"; IconFilename: "{app}\pnr.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; The user config (%APPDATA%\PrivateNetRouting) and the logs (%LOCALAPPDATA%\PrivateNetRouting)
; survive uninstall on purpose — nothing deletes user data.

[Run]
; Nothing starts by itself: the launch checkbox is off by default and silent installs never launch.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
// The background workers (PrivateNetRouting.App.exe with --proxy-worker / --watchdog-worker in
// the command line, and any pnr.exe — the CLI workers and a foreground `pnr watchdog`) have no
// window, so the Restart Manager behind CloseApplications cannot close them and the install stops
// on the "unable to automatically close all applications" error. They are terminated here —
// before the files are copied and before the uninstall — using the same identification the
// application's own supervisors use (the marker in the command line). Only processes whose
// executable lives in the install directory are touched: the workers of a portable copy or a
// dev build keep running. A worker of this very install does not survive an upgrade: its
// executable is about to be replaced (a chain left up needs `pnr chain up` afterwards).
// The GUI (a windowed process) is left to AppMutex and the Restart Manager: it closes
// gracefully on session end and spawns nothing on that path.
procedure TerminateBackgroundProcesses;
var
  appDir, psCommand, params: String;
  resultCode: Integer;
begin
  appDir := ExpandConstant('{app}\');
  StringChangeEx(appDir, '''', '''''', True); // a PowerShell string literal: quotes doubled
  psCommand :=
    '$dir = ''' + appDir + '''; ' +
    'Get-CimInstance Win32_Process | Where-Object { ' +
    '$_.ExecutablePath -and $_.ExecutablePath.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) -and ' +
    '($_.Name -eq ''pnr.exe'' -or ($_.Name -eq ''PrivateNetRouting.App.exe'' -and ' +
    '($_.CommandLine -like ''*--proxy-worker*'' -or $_.CommandLine -like ''*--watchdog-worker*''))) } ' +
    '| ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }';
  params := '-NoProfile -ExecutionPolicy Bypass -Command "' + psCommand + '"';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), params, '',
       SW_HIDE, ewWaitUntilTerminated, resultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  TerminateBackgroundProcesses;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    TerminateBackgroundProcesses;
end;

// The uninstaller reverts nothing outside its own folder: the guards live in the system (the hosts
// block, the name-resolution rule, the firewall rules, the Claude managed policy, the Windows
// system proxy) and in the settings files of the applications, and all of them keep pointing at a
// local proxy that is about to disappear. Reverting them needs the application's own commands
// (the policy and the ban need administrator rights), so the owner is told what to run while
// pnr.exe is still installed and may stop the uninstall to run it. A silent uninstall skips the
// question.
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if UninstallSilent then
    Exit;
  Result := MsgBox(
    'PrivateNetRouting has changed settings outside its own folder, and this uninstaller does not revert them. ' +
    'They keep pointing at a local proxy that will be gone, so Claude and Codex may lose connectivity.' + #13#10 + #13#10 +
    'To turn them off first, cancel this uninstall and run in a terminal (pnr.exe is in ' + ExpandConstant('{app}') + '):' + #13#10 + #13#10 +
    '    pnr guard ban off        (hosts block, name-resolution rule, firewall rules)' + #13#10 +
    '    pnr guard policy off     (Claude managed policy)' + #13#10 +
    '    pnr guard sysproxy off   (Windows system proxy)' + #13#10 +
    '    pnr chain down           (the chain processes)' + #13#10 + #13#10 +
    'The proxy entries in the Claude settings.json, the Codex .env and the WSL startup files have to be removed by hand.' + #13#10 + #13#10 +
    'Continue with the uninstall?',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;
