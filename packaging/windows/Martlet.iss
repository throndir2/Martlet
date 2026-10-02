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
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupArchitecture=x64
MinVersion=10.0.19041
Uninstallable=yes
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
#include PayloadFiles

; Setup asks no feature questions. Prerequisites are never bundled or installed here: Martlet's setup advisor offers
; what the chosen plan needs, and Prerequisites in its Settings or Start > Martlet prerequisites checks and
; installs them any time.
[Run]
Filename: "{app}\Desktop\Martlet.Desktop.exe"; WorkingDir: "{app}\Desktop"; Description: "Start Martlet and finish setting up"; Flags: postinstall nowait skipifsilent

[Icons]
#ifdef PublicRelease
Name: "{group}\Martlet"; Filename: "{app}\Desktop\Martlet.Desktop.exe"; WorkingDir: "{app}\Desktop"
Name: "{group}\Martlet Doctor"; Filename: "{cmd}"; Parameters: "/D /K """"{app}\Doctor\Martlet.Doctor.exe"" status"""; WorkingDir: "{app}\Doctor"; IconFilename: "{app}\Doctor\Martlet.Doctor.exe"
Name: "{group}\Martlet prerequisites"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\prerequisites\Install-Prerequisites.ps1"""; WorkingDir: "{app}\prerequisites"; IconFilename: "{app}\Desktop\Martlet.Desktop.exe"
Name: "{group}\Read me"; Filename: "{app}\help\RELEASE.txt"
Name: "{group}\Uninstall Martlet"; Filename: "{uninstallexe}"
#else
Name: "{group}\Martlet (Internal)"; Filename: "{app}\Desktop\Martlet.Desktop.exe"; WorkingDir: "{app}\Desktop"
Name: "{group}\Martlet Doctor (Internal)"; Filename: "{cmd}"; Parameters: "/D /K """"{app}\Doctor\Martlet.Doctor.exe"" status"""; WorkingDir: "{app}\Doctor"; IconFilename: "{app}\Doctor\Martlet.Doctor.exe"
Name: "{group}\Martlet prerequisites (Internal)"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\prerequisites\Install-Prerequisites.ps1"""; WorkingDir: "{app}\prerequisites"; IconFilename: "{app}\Desktop\Martlet.Desktop.exe"
Name: "{group}\Read me - Internal build"; Filename: "{app}\help\INTERNAL.txt"
Name: "{group}\Uninstall Martlet (Internal)"; Filename: "{uninstallexe}"
#endif

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Keep /DIR overrides and old installer registry values away from user data.
  if CompareText(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))),
#ifdef PublicRelease
      RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{localappdata}\Programs\Martlet')))) <> 0 then
    Result := 'Martlet installs only to its fixed per-user program directory. Remove /DIR overrides. User settings remain separate.';
#else
      RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{localappdata}\Programs\Martlet Internal')))) <> 0 then
    Result := 'This internal build uses only the fixed per-user program directory. Remove /DIR overrides. User settings must remain separate.';
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox('Martlet program files were removed. Settings and other user data remain in ' +
      ExpandConstant('{localappdata}\Martlet') + '. No user data was deleted.', mbInformation, MB_OK);
end;
