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
AppId={{CDFDFAB4-DAF1-4A6D-8823-A55E0A12CD86}
AppName=Martlet (Internal)
AppVersion={#AppVersion}
AppVerName=Martlet {#AppVersion} - INTERNAL DEVELOPMENT ONLY
AppPublisher=Martlet development project (unsigned)
VersionInfoDescription=Martlet - INTERNAL DEVELOPMENT ONLY - UNSIGNED
DefaultDirName={localappdata}\Programs\Martlet Internal
DefaultGroupName=Martlet (Internal)
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupArchitecture=x64
MinVersion=10.0.26200
Uninstallable=yes
UninstallDisplayName=Martlet (Internal)
UninstallDisplayIcon={app}\Desktop\Martlet.Desktop.exe
SetupIconFile=..\..\src\Martlet.Desktop\Assets\Martlet.ico
UninstallFilesDir={app}\uninstall
OutputDir={#BuildOutput}
OutputBaseFilename=Martlet-{#AppVersion}-win-x64-INTERNAL-UNSIGNED
InfoBeforeFile={#PayloadRoot}\help\INTERNAL.txt
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

[Icons]
Name: "{group}\Martlet (Internal)"; Filename: "{app}\Desktop\Martlet.Desktop.exe"; WorkingDir: "{app}\Desktop"
Name: "{group}\Martlet Doctor (Internal)"; Filename: "{cmd}"; Parameters: "/D /K """"{app}\Doctor\Martlet.Doctor.exe"" status"""; WorkingDir: "{app}\Doctor"; IconFilename: "{app}\Doctor\Martlet.Doctor.exe"
Name: "{group}\Read me - Internal build"; Filename: "{app}\help\INTERNAL.txt"
Name: "{group}\Uninstall Martlet (Internal)"; Filename: "{uninstallexe}"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Keep /DIR overrides and old installer registry values away from user data.
  if CompareText(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))),
      RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{localappdata}\Programs\Martlet Internal')))) <> 0 then
    Result := 'This internal build uses only the fixed per-user program directory. Remove /DIR overrides. User settings must remain separate.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox('Martlet program files were removed. Settings and other user data remain in ' +
      ExpandConstant('{localappdata}\Martlet') + '. No user data was deleted.', mbInformation, MB_OK);
end;
