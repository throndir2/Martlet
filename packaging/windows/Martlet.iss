#if VER != EncodeVer(7, 1, 0)
  #error Use the pinned Inno Setup 7.1.0 compiler via Build-Installer.ps1.
#endif
#ifndef PayloadRoot
  #error PayloadRoot is required; use Build-Installer.ps1.
#endif
#ifndef PayloadFiles
  #error PayloadFiles is required; use Build-Installer.ps1.
#endif
#ifndef AppVersion
  #error AppVersion is required; use Build-Installer.ps1.
#endif
#ifndef BuildOutput
  #error BuildOutput is required; use Build-Installer.ps1.
#endif

[Setup]
#ifdef PublicRelease
AppId={{7EA5CC4A-8BF4-4412-ABE1-90819303FEAB}
AppName=Martlet
AppVersion={#AppVersion}
AppVerName=Martlet {#AppVersion}
AppPublisher=throndir2
VersionInfoDescription=Martlet
DefaultDirName={localappdata}\Programs\Martlet
DefaultGroupName=Martlet
#else
AppId={{CDFDFAB4-DAF1-4A6D-8823-A55E0A12CD86}
AppName=Martlet (Internal)
AppVersion={#AppVersion}
AppVerName=Martlet {#AppVersion} - INTERNAL DEVELOPMENT ONLY
AppPublisher=Martlet development project (unsigned)
VersionInfoDescription=Martlet - INTERNAL DEVELOPMENT ONLY - UNSIGNED
DefaultDirName={localappdata}\Programs\Martlet Internal
DefaultGroupName=Martlet (Internal)
#endif
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UsePreviousAppDir=no
UsePreviousGroup=no
PrivilegesRequired=lowest
; x64compatible: x64 Windows, and Windows 11 on Arm, which runs the x64 build under its x64 emulation. Setup itself is
; 32-bit so that Windows 10 on Arm (x86 emulation only) can open it and get the clear refusal in [Messages].
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupArchitecture=x86
MinVersion=10.0.19041
; /STAGE=prepare (Martlet's update, while Martlet runs) installs only the files, into {app}.next, and registers nothing.
Uninstallable=not StagePrepare
CreateUninstallRegKey=not StagePrepare
#ifdef PublicRelease
UninstallDisplayName=Martlet
#else
UninstallDisplayName=Martlet (Internal)
#endif
UninstallDisplayIcon={app}\Desktop\Martlet.Desktop.exe
SetupIconFile=..\..\src\Martlet.Desktop\Assets\Martlet.ico
UninstallFilesDir={app}\uninstall
OutputDir={#BuildOutput}
#ifdef PublicRelease
OutputBaseFilename=Martlet-{#AppVersion}-win-x64
InfoBeforeFile={#PayloadRoot}\help\RELEASE.txt
LicenseFile={#PayloadRoot}\help\LICENSE.txt
#else
OutputBaseFilename=Martlet-{#AppVersion}-win-x64-INTERNAL-UNSIGNED
InfoBeforeFile={#PayloadRoot}\help\INTERNAL.txt
#endif
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=yes
SetupLogging=yes
SignedUninstaller=no
TimeStampsInUTC=yes
TouchDate=2026-01-01
TouchTime=00:00

[Files]
; Every entry has Check: InstallPayload, so /STAGE=finish installs no files.
#include PayloadFiles

; A staged update moves a whole new folder into {app}, so the uninstall log can't list every file: remove the folder,
; and the staging folders an update can leave beside it. PrepareToInstall keeps {app} the standard program folder.
[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}.next"
Type: filesandordirs; Name: "{app}.previous"

[Messages]
OnlyOnTheseArchitectures=Martlet needs an x64 PC with Windows 10 version 2004 or newer, or a Windows 11 on Arm PC (Snapdragon X and similar), which runs Martlet under its x64 emulation.%n%nWindows 10 on Arm can't run x64 apps, so Martlet can't be installed on this PC. Update it to Windows 11 to install Martlet.

; Setup asks no feature questions. Prerequisites are never bundled or installed here: Martlet's setup advisor offers
; what the chosen plan needs, and Prerequisites in its Settings or Start > Martlet prerequisites checks and
; installs them any time.
[Run]
Filename: "{app}\Desktop\Martlet.Desktop.exe"; WorkingDir: "{app}\Desktop"; Description: "Start Martlet and finish setting up"; Flags: postinstall nowait skipifsilent

[Icons]
#ifdef PublicRelease
Name: "{group}\Martlet"; Filename: "{app}\Desktop\Martlet.Desktop.exe"; WorkingDir: "{app}\Desktop"; Check: not StagePrepare
Name: "{group}\Martlet Doctor"; Filename: "{cmd}"; Parameters: "/D /K """"{app}\Doctor\Martlet.Doctor.exe"" status"""; WorkingDir: "{app}\Doctor"; IconFilename: "{app}\Doctor\Martlet.Doctor.exe"; Check: not StagePrepare
Name: "{group}\Martlet prerequisites"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\prerequisites\Install-Prerequisites.ps1"""; WorkingDir: "{app}\prerequisites"; IconFilename: "{app}\Desktop\Martlet.Desktop.exe"; Check: not StagePrepare
Name: "{group}\Read me"; Filename: "{app}\help\RELEASE.txt"; Check: not StagePrepare
Name: "{group}\Uninstall Martlet"; Filename: "{uninstallexe}"; Check: not StagePrepare
#else
Name: "{group}\Martlet (Internal)"; Filename: "{app}\Desktop\Martlet.Desktop.exe"; WorkingDir: "{app}\Desktop"; Check: not StagePrepare
Name: "{group}\Martlet Doctor (Internal)"; Filename: "{cmd}"; Parameters: "/D /K """"{app}\Doctor\Martlet.Doctor.exe"" status"""; WorkingDir: "{app}\Doctor"; IconFilename: "{app}\Doctor\Martlet.Doctor.exe"; Check: not StagePrepare
Name: "{group}\Martlet prerequisites (Internal)"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\prerequisites\Install-Prerequisites.ps1"""; WorkingDir: "{app}\prerequisites"; IconFilename: "{app}\Desktop\Martlet.Desktop.exe"; Check: not StagePrepare
Name: "{group}\Read me - Internal build"; Filename: "{app}\help\INTERNAL.txt"; Check: not StagePrepare
Name: "{group}\Uninstall Martlet (Internal)"; Filename: "{uninstallexe}"; Check: not StagePrepare
#endif

[Code]
// Martlet's own updates install in two steps, so Martlet is away only for a restart, not for the whole install:
// /STAGE=prepare /DIR=<program folder>.next installs the files beside the running Martlet and registers nothing; after
// Martlet exits its update helper renames the folders, then /STAGE=finish installs no files and updates the shortcuts,
// the uninstaller and Windows' list of installed apps.
function StageMode: String;
begin
  Result := Lowercase(ExpandConstant('{param:STAGE|}'));
end;

function StagePrepare: Boolean;
begin
  Result := StageMode = 'prepare';
end;

function InstallPayload: Boolean;
begin
  Result := StageMode <> 'finish';
end;

function InitializeSetup: Boolean;
begin
  Result := (StageMode = '') or (StageMode = 'prepare') or (StageMode = 'finish');
  if not Result then
    SuppressibleMsgBox('Unknown /STAGE value: ' + StageMode + '.', mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Expected: String;
begin
  Result := '';
#ifdef PublicRelease
  Expected := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{localappdata}\Programs\Martlet')));
#else
  Expected := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{localappdata}\Programs\Martlet Internal')));
#endif
  if StagePrepare then
    Expected := Expected + '.next';
  // Keep /DIR overrides and old installer registry values away from user data.
  if CompareText(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))), Expected) <> 0 then
#ifdef PublicRelease
    Result := 'Martlet installs to its standard per-user program folder. User settings are stored separately.';
#else
    Result := 'This internal build installs to its standard per-user program folder. User settings are stored separately.';
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  // Settings > Start with Windows points the per-user Run entry at this install; it leaves with the install.
  if (CurUninstallStep = usUninstall) and
     RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Martlet', StartupCommand) and
     (Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(StartupCommand)) > 0) then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Martlet');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'Martlet');
  end;
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox('Martlet was uninstalled. Your settings remain in ' +
      ExpandConstant('{localappdata}\Martlet') + '.', mbInformation, MB_OK);
end;
