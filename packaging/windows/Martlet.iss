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
UsePreviousAppDir=no
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupArchitecture=x64
MinVersion=10.0.26200
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

; Prerequisites are never bundled here: each ticked task downloads from its publisher when setup runs
; prerequisites\Install-Prerequisites.ps1. Tasks appear only when the item is missing on this PC.
[Tasks]
Name: "webview2"; Description: "Microsoft Edge WebView2 Runtime - needed to show the desktop character (downloaded from Microsoft)"; GroupDescription: "Missing on this PC:"; Check: not IsWebView2Installed
Name: "microphone"; Description: "Allow desktop apps to use the microphone - needed for push-to-talk (opens Windows Settings)"; GroupDescription: "Missing on this PC:"; Check: IsMicrophoneBlocked
Name: "speech"; Description: "Windows offline speech recognition and voices for your language (Windows Update; asks for administrator approval)"; GroupDescription: "Optional, installed from the publisher when ticked:"; Flags: unchecked; Check: not IsWindowsSpeechInstalled
Name: "ollama"; Description: "Ollama - run a local LLM on this PC (ollama.com via winget, MIT license; a model is offered afterwards)"; GroupDescription: "Optional, installed from the publisher when ticked:"; Flags: unchecked; Check: not IsOllamaInstalled
Name: "docker"; Description: "WSL 2 and Docker Desktop - host Audio2Face and other GPU roles on this PC (Docker Subscription Service Agreement, free for personal use; asks for administrator approval; a restart may follow)"; GroupDescription: "Optional, installed from the publisher when ticked:"; Flags: unchecked; Check: not IsDockerDesktopInstalled

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\prerequisites\Install-Prerequisites.ps1"" -FromInstaller -Install {code:SelectedPrerequisites}"; WorkingDir: "{app}\prerequisites"; StatusMsg: "Installing the prerequisites you selected (follow the console window)..."; Tasks: webview2 or microphone or speech or ollama or docker

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
const
  WebView2Client = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  MicrophoneConsent = 'Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone';

function HasWebView2Version(RootKey: Integer; SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function IsWebView2Installed: Boolean;
begin
  Result := HasWebView2Version(HKLM32, 'SOFTWARE\' + WebView2Client) or
    HasWebView2Version(HKLM64, 'SOFTWARE\' + WebView2Client) or
    HasWebView2Version(HKCU, 'Software\' + WebView2Client);
end;

function IsConsentDenied(RootKey: Integer; SubKey: String): Boolean;
var
  Value: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'Value', Value) and (CompareText(Value, 'Deny') = 0);
end;

function IsMicrophoneBlocked: Boolean;
begin
  Result := IsConsentDenied(HKLM64, MicrophoneConsent) or IsConsentDenied(HKCU, MicrophoneConsent) or
    IsConsentDenied(HKCU, MicrophoneConsent + '\NonPackaged');
end;

// The SAPI desktop recognizer token for the Windows display language (for example MS-1033-80-DESK for en-US).
function IsWindowsSpeechInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Speech\Recognizers\Tokens\MS-' + IntToStr(GetUILanguage) + '-80-DESK');
end;

function IsOllamaInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Programs\Ollama\ollama.exe'));
end;

function IsDockerDesktopInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\Docker\Docker\Docker Desktop.exe'));
end;

function SelectedPrerequisites(Param: String): String;
begin
  Result := '';
  if WizardIsTaskSelected('webview2') then Result := Result + ',WebView2';
  if WizardIsTaskSelected('microphone') then Result := Result + ',Microphone';
  if WizardIsTaskSelected('speech') then Result := Result + ',WindowsSpeech';
  if WizardIsTaskSelected('ollama') then Result := Result + ',Ollama';
  if WizardIsTaskSelected('docker') then Result := Result + ',DockerDesktop';
  Delete(Result, 1, 1);
  if WizardSilent then Result := Result + ' -NoPrompt';
end;

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
