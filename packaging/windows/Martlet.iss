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
; prerequisites\Install-Prerequisites.ps1. Tasks appear only when the item is missing on this PC. The Recommended setup
; page in [Code] asks how many computers Martlet can use, the graphics card and the goal, then ticks the optional tasks
; that suit the answers; users can change every tick.
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
  DisplayAdapters = 'SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}';
  OnlyThisPc = 0; OneOtherPc = 1; MoreOtherPcs = 2; HelperPc = 3;
  GpuNvidia = 0; GpuNvidiaGames = 1; GpuOther = 2; GpuNone = 3;
  GoalBalanced = 0; GoalSmartest = 1; GoalFastest = 2; GoalPrivate = 3;

var
  AdvicePage: TWizardPage;
  ComputersList, GpuList, GoalList: TNewComboBox;
  AdviceLabel: TNewStaticText;
  OptionalTasksOffered, WantSpeech, WantOllama, WantDocker: Boolean;
  AppliedAnswers, RecommendedTicks: String;

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

function HasNvidiaGpu: Boolean;
var
  Adapters: TArrayOfString;
  Provider: String;
  I: Integer;
begin
  Result := FileExists(ExpandConstant('{sys}\nvidia-smi.exe'));
  if not Result and RegGetSubkeyNames(HKLM64, DisplayAdapters, Adapters) then
    for I := 0 to GetArrayLength(Adapters) - 1 do
      if RegQueryStringValue(HKLM64, DisplayAdapters + '\' + Adapters[I], 'ProviderName', Provider) and
          (Pos('NVIDIA', Uppercase(Provider)) > 0) then
        Result := True;
end;

function JoinItem(List, Item: String): String;
begin
  if List = '' then Result := Item else Result := List + ', ' + Item;
end;

function Bullet(Line: String): String;
begin
  Result := #$2022 + ' ' + Line + '.' + #13#10;
end;

// A short installer version of the setup advisor (src/Martlet.Core/Installation/SetupAdvisor.cs and
// docs/RECOMMENDED_SETUPS.md): it only decides which optional tasks help this PC and says where each part runs.
procedure UpdateAdvice(Sender: TObject);
var
  Pcs, Gpu, Goal: Integer;
  Nvidia, GpuFree, LocalModel: Boolean;
  ThisPc, Others, Online, Present, Advice: String;
begin
  Pcs := ComputersList.ItemIndex;
  Gpu := GpuList.ItemIndex;
  Goal := GoalList.ItemIndex;
  Nvidia := (Gpu = GpuNvidia) or (Gpu = GpuNvidiaGames);
  GpuFree := (Gpu = GpuNvidia) or (Gpu = GpuOther);
  LocalModel := (Goal = GoalFastest) or (Goal = GoalPrivate);
  WantSpeech := False;
  WantOllama := False;
  WantDocker := False;
  Others := '';
  Online := '';

  if Pcs = HelperPc then
  begin
    WantDocker := Nvidia;
    WantOllama := LocalModel and (Gpu <> GpuNone);
    ThisPc := 'a Martlet host for the PC you talk on';
    if WantDocker then ThisPc := ThisPc + ': Audio2Face lip-sync and future GPU voices in Docker';
    if WantOllama and WantDocker then ThisPc := ThisPc + ', plus the conversation model in Ollama';
    if WantOllama and not WantDocker then ThisPc := ThisPc + ': the conversation model in Ollama';
    if WantOllama then ThisPc := ThisPc + ' (served over HTTPS until the planned host LLM role)';
    if not WantDocker and not WantOllama then
      ThisPc := ThisPc + '. Without an NVIDIA card it adds little: Audio2Face and GPU voices need NVIDIA';
    Others := Bullet('Your main PC: Martlet, microphone, speakers and character') +
      Bullet('When Martlet opens here, choose Lend this PC to Martlet, then pair it from your main PC in Devices > Add a computer');
    if not LocalModel then Online := 'the conversation model on OpenRouter or NVIDIA Build, chosen on your main PC';
  end
  else
  begin
    ThisPc := 'Martlet, microphone, speakers and character';
    if not LocalModel then
    begin
      if Goal = GoalSmartest then
        Online := 'the largest conversation model you want to pay for (OpenRouter or NVIDIA Build)'
      else
        Online := 'a large conversation model (OpenRouter or NVIDIA Build)';
    end
    else if Pcs = OnlyThisPc then
    begin
      if (Goal = GoalFastest) and not GpuFree then
        Online := 'a small, fast conversation model (OpenRouter or NVIDIA Build)'
      else
      begin
        WantOllama := True;
        if Goal = GoalFastest then ThisPc := ThisPc + '; a small, fast conversation model in Ollama with the GPU to itself'
        else if Gpu = GpuNone then ThisPc := ThisPc + '; a small conversation model in Ollama (slow without a GPU)'
        else if Gpu = GpuNvidiaGames then ThisPc := ThisPc + '; the conversation model in Ollama (it shares the GPU with games)'
        else ThisPc := ThisPc + '; the conversation model in Ollama';
      end;
    end;

    if Pcs = OnlyThisPc then
    begin
      WantDocker := (Gpu = GpuNvidia) and (Goal <> GoalFastest);
      if WantDocker then ThisPc := ThisPc + '; Audio2Face lip-sync (and later GPU voices) in Docker'
      else ThisPc := ThisPc + '; loudness lip-sync';
    end
    else if LocalModel and (Pcs = OneOtherPc) then
    begin
      WantDocker := Gpu = GpuNvidia;
      if WantDocker then
      begin
        ThisPc := ThisPc + '; Audio2Face lip-sync in Docker';
        Others := Bullet('Computer 2: the conversation model');
      end
      else
        Others := Bullet('Computer 2: the conversation model, Audio2Face lip-sync and GPU voices');
    end
    else if LocalModel then
      Others := Bullet('Computer 2 (largest GPU): the conversation model') +
        Bullet('Computer 3: Audio2Face lip-sync and GPU voices')
    else if Pcs = OneOtherPc then
      Others := Bullet('Computer 2: Audio2Face lip-sync and GPU voices')
    else
      Others := Bullet('Computer 2: Audio2Face lip-sync and GPU voices') +
        Bullet('Other computers: spare for screen understanding or voice training later');
    if Others <> '' then
      Others := Others + Bullet('On each other computer, run this installer and choose the helper option (or use ' +
        'martlet-host on Ubuntu), then pair it in Devices > Add a computer');

    if Goal = GoalPrivate then
    begin
      WantSpeech := True;
      ThisPc := ThisPc + '; Windows offline speech (being built; OpenAI speech works until then)';
    end
    else
      Online := JoinItem(Online, 'OpenAI speech');
  end;

  RecommendedTicks := '';
  Present := '';
  if WantSpeech then
    if IsWindowsSpeechInstalled then Present := JoinItem(Present, 'Windows speech')
    else RecommendedTicks := JoinItem(RecommendedTicks, 'Windows speech');
  if WantOllama then
    if IsOllamaInstalled then Present := JoinItem(Present, 'Ollama')
    else RecommendedTicks := JoinItem(RecommendedTicks, 'Ollama');
  if WantDocker then
    if IsDockerDesktopInstalled then Present := JoinItem(Present, 'Docker Desktop')
    else RecommendedTicks := JoinItem(RecommendedTicks, 'WSL 2 and Docker Desktop');

  Advice := Bullet('This PC: ' + ThisPc) + Others;
  if Online <> '' then Advice := Advice + Bullet('Online: ' + Online);
  if RecommendedTicks <> '' then Advice := Advice + #13#10 + 'Setup ticks on the next page: ' + RecommendedTicks + '.'
  else Advice := Advice + #13#10 + 'Nothing extra to install on this PC.';
  if Present <> '' then Advice := Advice + ' Already installed: ' + Present + '.';
  AdviceLabel.Caption := Advice;
end;

function AddAdviceRow(Top: Integer; Caption: String; Items: array of String): TNewComboBox;
var
  Prompt: TNewStaticText;
  I: Integer;
begin
  Result := TNewComboBox.Create(AdvicePage);
  Result.Style := csDropDownList;
  Result.Top := Top;
  Result.Parent := AdvicePage.Surface;
  for I := 0 to GetArrayLength(Items) - 1 do
    Result.Items.Add(Items[I]);
  Result.ItemIndex := 0;
  Result.OnChange := @UpdateAdvice;
  Prompt := TNewStaticText.Create(AdvicePage);
  Prompt.Caption := Caption;
  Prompt.Top := Top + ScaleY(3);
  Prompt.FocusControl := Result;
  Prompt.Parent := AdvicePage.Surface;
end;

procedure InitializeWizard;
var
  Heading, Note: TNewStaticText;
  Left: Integer;
begin
  OptionalTasksOffered := not IsWindowsSpeechInstalled or not IsOllamaInstalled or not IsDockerDesktopInstalled;
  AdvicePage := CreateCustomPage(wpInfoBefore, 'Recommended setup',
    'Tell Setup how you will use Martlet, and it ticks what this PC needs.');
  ComputersList := AddAdviceRow(0, '&Computers for Martlet:', ['Just this PC',
    'This PC + 1 other computer with a GPU', 'This PC + 2 or more other computers with GPUs',
    'This PC is a helper for Martlet on another PC']);
  GpuList := AddAdviceRow(ComputersList.Top + ComputersList.Height + ScaleY(6), 'This PC''s &graphics card:', [
    'NVIDIA, free for Martlet', 'NVIDIA, but I play games on this PC', 'AMD or Intel', 'None, or not sure']);
  GoalList := AddAdviceRow(GpuList.Top + GpuList.Height + ScaleY(6), '&What matters most:', [
    'Balanced (recommended): smart answers, voice and face', 'Smartest: the biggest online model',
    'Fastest: a local model with its own GPU', 'Private: everything on your own computers']);
  if HasNvidiaGpu then GpuList.ItemIndex := GpuNvidia else GpuList.ItemIndex := GpuNone;
  Left := ScaleX(150);
  ComputersList.Left := Left;
  GpuList.Left := Left;
  GoalList.Left := Left;
  ComputersList.Width := AdvicePage.SurfaceWidth - Left;
  GpuList.Width := ComputersList.Width;
  GoalList.Width := ComputersList.Width;
  ComputersList.Anchors := [akLeft, akTop, akRight];
  GpuList.Anchors := [akLeft, akTop, akRight];
  GoalList.Anchors := [akLeft, akTop, akRight];

  Note := TNewStaticText.Create(AdvicePage);
  Note.AutoSize := False;
  Note.WordWrap := True;
  Note.Width := AdvicePage.SurfaceWidth;
  Note.Caption := 'Not sure? Keep the defaults. Nothing here is final: change any tick on the next page, install ' +
    'these items later from Start > Martlet prerequisites, and change where each part runs at any time in Martlet ' +
    '(Home > Get a recommendation explains the options).';
  Note.Parent := AdvicePage.Surface;
  Note.AdjustHeight;
  Note.Top := AdvicePage.SurfaceHeight - Note.Height;
  Note.Anchors := [akLeft, akRight, akBottom];

  Heading := TNewStaticText.Create(AdvicePage);
  Heading.Caption := 'Recommended for you';
  Heading.Font.Style := [fsBold];
  Heading.Top := GoalList.Top + GoalList.Height + ScaleY(12);
  Heading.Parent := AdvicePage.Surface;

  AdviceLabel := TNewStaticText.Create(AdvicePage);
  AdviceLabel.AutoSize := False;
  AdviceLabel.WordWrap := True;
  AdviceLabel.Top := Heading.Top + Heading.Height + ScaleY(4);
  AdviceLabel.Width := AdvicePage.SurfaceWidth;
  AdviceLabel.Height := Note.Top - AdviceLabel.Top - ScaleY(6);
  AdviceLabel.Anchors := [akLeft, akTop, akRight, akBottom];
  AdviceLabel.Parent := AdvicePage.Surface;
  UpdateAdvice(nil);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = AdvicePage.ID) and not OptionalTasksOffered;
end;

procedure SelectRecommendedTask(Name: String; Offered, Wanted: Boolean);
begin
  if Offered then
    if Wanted then WizardSelectTasks(Name) else WizardSelectTasks('!' + Name);
end;

// Apply the recommendation when the answers changed, so ticks changed by hand survive Back from the Ready page.
procedure CurPageChanged(CurPageID: Integer);
var
  Answers, Caption: String;
  Delta: Integer;
begin
  if (CurPageID <> wpSelectTasks) or not OptionalTasksOffered or WizardSilent then Exit;
  Answers := IntToStr(ComputersList.ItemIndex) + IntToStr(GpuList.ItemIndex) + IntToStr(GoalList.ItemIndex);
  UpdateAdvice(nil);
  if Answers <> AppliedAnswers then
  begin
    AppliedAnswers := Answers;
    SelectRecommendedTask('speech', not IsWindowsSpeechInstalled, WantSpeech);
    SelectRecommendedTask('ollama', not IsOllamaInstalled, WantOllama);
    SelectRecommendedTask('docker', not IsDockerDesktopInstalled, WantDocker);
  end;
  if RecommendedTicks <> '' then Caption := 'Recommended for your answers: ' + RecommendedTicks + '.'
  else Caption := 'Nothing optional below is needed for your answers.';
  WizardForm.SelectTasksLabel.Caption := Caption + ' Press Back to change your answers. You can change any tick, ' +
    'and Start > Martlet prerequisites installs these items later too.';
  Delta := WizardForm.AdjustLabelHeight(WizardForm.SelectTasksLabel);
  WizardForm.TasksList.Top := WizardForm.TasksList.Top + Delta;
  WizardForm.TasksList.Height := WizardForm.TasksList.Height - Delta;
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
