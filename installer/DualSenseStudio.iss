; DualSense Studio installer (Inno Setup 6). Build: BUILD_INSTALLER.bat
; Installs the app, then runs Setup.ps1 -DriversOnly to install the signed
; drivers (VB-CABLE, Hi-Fi Cable, ViGEmBus, HidHide) and name the audio devices.

#define AppName "DualSense Studio"
#define AppVersion "1.0.15"
#define AppExe "DualSenseAudioStudio.exe"

[Setup]
AppId={{7D2B4E5A-3C1F-4B8E-9A6D-5E0F2C8B1A47}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=riyanshahlawat
AppPublisherURL=https://github.com/riyanshahlawat/dualsense-studio
AppSupportURL=https://github.com/riyanshahlawat/dualsense-studio/issues
AppUpdatesURL=https://github.com/riyanshahlawat/dualsense-studio/releases
; Same folder as earlier builds, so HidHide's allow-list keeps working.
DefaultDirName={localappdata}\Programs\DualSenseBtAudio
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install (no admin prompt), so in-app updates are silent like Discord.
; Only the one-time driver step asks for admin, and only when a driver is missing.
PrivilegesRequired=lowest
UsedUserAreasWarning=no
OutputDir=..\release
OutputBaseFilename=DualSenseStudio-Setup
SetupIconFile=..\src\DualSenseAudioStudio\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"
Name: "startup"; Description: "Start with Windows (in the tray)"

[Files]
Source: "..\build\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Setup.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DualSenseStudio"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Driver setup: hidden window, elevated, and skipped entirely when all drivers are present.
Filename: "powershell.exe"; Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\setup\Setup.ps1"" -DriversOnly -AppDir ""{app}"""; Verb: runas; StatusMsg: "Installing drivers (VB-CABLE, Hi-Fi Cable, ViGEmBus, HidHide)..."; Flags: shellexec runhidden waituntilterminated; Check: DriversMissing
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; In-app updates run silently: reopen the app when done.
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{localappdata}\DualSenseAudioStudio"

[Code]
function ServiceExists(Name: String): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\' + Name);
end;

// True when any required driver is missing (fresh PC). Updates never need this.
function DriversMissing: Boolean;
begin
  Result := not (ServiceExists('VBAudioVACMME') and ServiceExists('VBAudioHFVAIOMME')
    and ServiceExists('ViGEmBus') and ServiceExists('HidHide'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // Stop the app before files are removed.
  if CurUninstallStep = usUninstall then
    Exec('taskkill.exe', '/F /IM DualSenseAudioStudio.exe /IM DualSenseBtAudio.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
