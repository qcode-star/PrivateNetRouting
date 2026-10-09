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
Name: "{group}\{#AppName} CLI"; Filename: "{app}\pnr.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; The user config (%APPDATA%\PrivateNetRouting) and the logs (%LOCALAPPDATA%\PrivateNetRouting)
; survive uninstall on purpose — nothing deletes user data.

[Run]
; Nothing starts by itself: the launch checkbox is off by default and silent installs never launch.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent unchecked
