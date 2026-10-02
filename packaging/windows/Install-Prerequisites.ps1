<#
.SYNOPSIS
    Checks, installs and configures the Windows prerequisites for Martlet features.

.DESCRIPTION
    Run without parameters for an interactive checklist. Nothing is installed or changed until you
    choose an item (or pass -Install, which Martlet's setup advisor and Prerequisites checklist do for the items you pick).
    Installers come from their publishers (Microsoft, Ollama, Docker) through Microsoft's signed
    WebView2 bootstrapper or winget, and keep their own license terms. Steps that change Windows
    features ask for administrator approval separately; Martlet itself stays a per-user app.

    IDs for -Install: WebView2, Microphone, WindowsSpeech, Ollama, NvidiaDriver, DockerDesktop.
    Everything Martlet bundles, installs on request or needs from you is listed in
    docs\PREREQUISITES.md in the Martlet repository.

    Runs on Windows PowerShell 5.1 (the Start menu shortcut) and PowerShell 7.

.EXAMPLE
    .\Install-Prerequisites.ps1
.EXAMPLE
    .\Install-Prerequisites.ps1 -Check
.EXAMPLE
    .\Install-Prerequisites.ps1 -Install WebView2,Ollama -OllamaModel gemma4:e4b
#>
[CmdletBinding()]
param(
    [string[]]$Install = @(),
    [switch]$Check,
    [string]$Culture = (Get-UICulture).Name,
    [string]$OllamaModel = '',
    [switch]$NoPrompt,
    [switch]$PauseWhenDone
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Ids = @('WebView2', 'Microphone', 'WindowsSpeech', 'Ollama', 'NvidiaDriver', 'DockerDesktop')
$WebView2Guid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$MicrophoneKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone'
$OllamaEndpoint = 'http://127.0.0.1:11434'
$WindowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

if ($Culture -notmatch '\A[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}\z') { throw "Unsupported culture name: $Culture" }

function Write-Heading([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Magenta
}

function Write-Note([string]$Text) { Write-Host $Text -ForegroundColor Yellow }

function Open-Page([string]$Uri) {
    Write-Host "Opening $Uri"
    try { Start-Process $Uri } catch { Write-Note "Could not open it automatically; open $Uri yourself." }
}

function Invoke-Probe([string]$File, [string]$Arguments, [int]$TimeoutMs = 20000) {
    try {
        $info = New-Object Diagnostics.ProcessStartInfo $File, $Arguments
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.CreateNoWindow = $true
        $process = [Diagnostics.Process]::Start($info)
        $errorTask = $process.StandardError.ReadToEndAsync()
        $output = $process.StandardOutput.ReadToEnd()
        if (-not $process.WaitForExit($TimeoutMs)) {
            try { $process.Kill() } catch { }
            return $null
        }
        $null = $errorTask.Result
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
    }
    catch { return $null }
}

function Get-Winget {
    $command = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $path = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function Install-WithWinget([string]$Id, [string]$Label) {
    $winget = Get-Winget
    if (-not $winget) {
        Write-Note "winget (App Installer from the Microsoft Store) is not available, so $Label cannot be installed automatically."
        return $false
    }
    Write-Host "Installing $Label with winget ($Id). This accepts the winget source agreement and the package's license terms."
    & $winget install --exact --id $Id --source winget --accept-package-agreements --accept-source-agreements
    Write-Host "winget finished with exit code $LASTEXITCODE."
    return $true
}

function Invoke-Elevated([string]$Script, [string]$Purpose) {
    $pause = if ($NoPrompt) { '' } else { "`nWrite-Host ''`nRead-Host 'Finished. Press Enter to close this window' | Out-Null" }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script + $pause))
    Write-Host "Windows asks for administrator approval to $Purpose."
    # Martlet runs this tool with -NoPrompt and shows its output itself, so the elevated step gets no console window either.
    $style = if ($NoPrompt) { 'Hidden' } else { 'Normal' }
    try {
        $process = Start-Process -FilePath $WindowsPowerShell -Verb RunAs -Wait -PassThru -WindowStyle $style `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
        return $process.ExitCode
    }
    catch {
        Write-Note "Administrator approval was declined or failed ($($_.Exception.Message)). Nothing was changed."
        return $null
    }
}

# --- Detection ------------------------------------------------------------------------------

function Get-RegistryValue([string]$Path, [string]$Name) {
    $item = Get-ItemProperty -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties[$Name]) { return [string]$item.$Name }
    return $null
}

function Get-WebView2Version {
    foreach ($key in @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$WebView2Guid",
            "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$WebView2Guid",
            "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$WebView2Guid")) {
        $version = Get-RegistryValue $key 'pv'
        if ($version -and $version -ne '0.0.0.0') { return $version }
    }
    return $null
}

function Get-MicrophoneBlock {
    if ((Get-RegistryValue "HKLM:\$MicrophoneKey" 'Value') -eq 'Deny') { return 'microphone access is off for this device' }
    if ((Get-RegistryValue "HKCU:\$MicrophoneKey" 'Value') -eq 'Deny') { return 'microphone access is off for your account' }
    if ((Get-RegistryValue "HKCU:\$MicrophoneKey\NonPackaged" 'Value') -eq 'Deny') { return 'desktop apps may not use the microphone' }
    return $null
}

function Get-SpeechCounts {
    try {
        Add-Type -AssemblyName System.Speech
        $recognizers = @([System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers() |
            Where-Object { $_.Culture.Name -eq $Culture })
        $synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
        try {
            $voices = @($synthesizer.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq $Culture })
        }
        finally { $synthesizer.Dispose() }
        return [pscustomobject]@{ Known = $true; Recognizers = $recognizers.Count; Voices = $voices.Count }
    }
    catch { return [pscustomobject]@{ Known = $false; Recognizers = 0; Voices = 0 } }
}

function Get-OllamaPath {
    $command = Get-Command ollama.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($path in @((Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'), (Join-Path $env:ProgramFiles 'Ollama\ollama.exe'))) {
        if (Test-Path -LiteralPath $path) { return $path }
    }
    return $null
}

function Get-NvidiaGpus {
    try { return @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -like '*NVIDIA*' }) }
    catch { return @() }
}

function Get-NvidiaSmi {
    $path = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
    if (Test-Path -LiteralPath $path) { return $path }
    $command = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Get-NvidiaDriver {
    $smi = Get-NvidiaSmi
    if (-not $smi) { return $null }
    $result = Invoke-Probe $smi '--query-gpu=name,driver_version,memory.total --format=csv,noheader'
    if (-not $result -or $result.ExitCode -ne 0) { return $null }
    return (($result.Output -split "`r?`n" | Where-Object { $_.Trim() }) -join '; ')
}

function Get-NvidiaVramGiB {
    $smi = Get-NvidiaSmi
    if (-not $smi) { return 0 }
    $result = Invoke-Probe $smi '--query-gpu=memory.total --format=csv,noheader,nounits'
    if (-not $result -or $result.ExitCode -ne 0) { return 0 }
    $largest = 0
    foreach ($line in ($result.Output -split "`r?`n")) {
        $mib = 0
        if ([int]::TryParse($line.Trim(), [ref]$mib) -and $mib -gt $largest) { $largest = $mib }
    }
    return [math]::Floor($largest / 1024)
}

function Test-Wsl {
    $wsl = Join-Path $env:SystemRoot 'System32\wsl.exe'
    if (-not (Test-Path -LiteralPath $wsl)) { return $false }
    $env:WSL_UTF8 = '1'
    $result = Invoke-Probe $wsl '--status'
    return [bool]($result -and $result.ExitCode -eq 0 -and $result.Output -notmatch 'not installed')
}

function Get-DockerDesktopPath {
    $path = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function New-State([string]$State, [string]$Detail) { [pscustomobject]@{ State = $State; Detail = $Detail } }

function Get-PrerequisiteState([string]$Id) {
    switch ($Id) {
        'WebView2' {
            $version = Get-WebView2Version
            if ($version) { return New-State 'OK' "version $version" }
            return New-State 'MISSING' 'needed to show the desktop character'
        }
        'Microphone' {
            $block = Get-MicrophoneBlock
            if ($block) { return New-State 'ACTION' "$block; turn it on in Windows Settings" }
            return New-State 'OK' 'Windows allows desktop apps to use the microphone'
        }
        'WindowsSpeech' {
            $counts = Get-SpeechCounts
            if (-not $counts.Known) { return New-State 'UNKNOWN' 'System.Speech is unavailable here; run this tool from Windows PowerShell' }
            $detail = "$($counts.Recognizers) recognizer(s), $($counts.Voices) voice(s) for $Culture"
            if ($counts.Recognizers -gt 0 -and $counts.Voices -gt 0) { return New-State 'OK' $detail }
            return New-State 'MISSING' $detail
        }
        'Ollama' {
            $path = Get-OllamaPath
            if ($path) { return New-State 'OK' $path }
            return New-State 'OPTIONAL' 'not installed'
        }
        'NvidiaDriver' {
            if (@(Get-NvidiaGpus).Count -eq 0) { return New-State 'N/A' 'no NVIDIA GPU found; APIs and loudness lip-sync need none' }
            $driver = Get-NvidiaDriver
            if ($driver) { return New-State 'OK' $driver }
            return New-State 'MISSING' 'NVIDIA GPU found, but nvidia-smi does not answer; install the current driver'
        }
        'DockerDesktop' {
            $docker = Get-DockerDesktopPath
            $wsl = Test-Wsl
            if ($docker -and -not (Test-VirtualizationAvailable)) { return New-State 'ACTION' 'virtualization is off in the firmware (UEFI/BIOS); Docker Desktop needs it' }
            if ($docker -and $wsl) { return New-State 'OK' 'WSL 2 and Docker Desktop are installed' }
            if ($docker) { return New-State 'OPTIONAL' 'Docker Desktop found, but WSL does not answer' }
            if ($wsl) { return New-State 'OPTIONAL' 'WSL is installed; Docker Desktop is not' }
            return New-State 'OPTIONAL' 'not installed'
        }
    }
}

$Descriptions = @{
    WebView2 = 'Microsoft Edge WebView2 Runtime|Desktop character (Live2D/VRM overlay)'
    Microphone = 'Microphone access for desktop apps|Push-to-talk and audio tests'
    WindowsSpeech = "Windows speech for $Culture|Offline speech recognition and Windows voices"
    Ollama = 'Ollama local LLM server|LLM on this PC instead of an API (optional)'
    NvidiaDriver = 'NVIDIA GPU driver|Local GPU roles: LLM, Audio2Face, voice (optional)'
    DockerDesktop = 'WSL 2 + Docker Desktop|Martlet hosts > This PC: Audio2Face and other GPU roles (optional)'
}

function Show-Status {
    Write-Heading 'Martlet prerequisites on this PC'
    $index = 0
    foreach ($id in $Ids) {
        $index++
        $parts = $Descriptions[$id].Split('|')
        $state = Get-PrerequisiteState $id
        $color = switch ($state.State) { 'OK' { 'Green' } 'N/A' { 'DarkGray' } 'OPTIONAL' { 'Gray' } default { 'Yellow' } }
        Write-Host ('{0}. [{1,-8}] {2}' -f $index, $state.State, $parts[0]) -ForegroundColor $color
        Write-Host ("       For: {0}. {1}." -f $parts[1], $state.Detail) -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host 'Bundled with Martlet (nothing to install): .NET 10 runtime, audio (NAudio/WASAPI, System.Speech),'
    Write-Host '  WebView2 loader, gRPC for Audio2Face, Live2D Cubism Core with the Hiyori character, three-vrm.'
    Write-Host 'You supply: API keys (OpenAI, OpenRouter, NVIDIA Build) in Setup / resume; your own Live2D/VRM'
    Write-Host '  models in Character settings; an NVIDIA NGC API key when a host adds Audio2Face.'
}

# --- Installation and configuration --------------------------------------------------------

function Install-WebView2 {
    if (Get-WebView2Version) { Write-Host 'The WebView2 Runtime is already installed.'; return }
    $path = Join-Path ([IO.Path]::GetTempPath()) ("MicrosoftEdgeWebview2Setup-{0}.exe" -f [guid]::NewGuid().ToString('N'))
    try {
        Write-Host 'Downloading the WebView2 Evergreen bootstrapper from Microsoft...'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $path
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw "The downloaded bootstrapper is not validly signed by Microsoft ($($signature.Status)), so it was not run."
        }
        Write-Host 'Installing the WebView2 Runtime (Microsoft software license terms apply)...'
        $process = Start-Process -FilePath $path -ArgumentList '/silent', '/install' -Wait -PassThru
        Write-Host "The WebView2 installer finished with exit code $($process.ExitCode)."
    }
    catch {
        Write-Note $_.Exception.Message
        $null = Install-WithWinget 'Microsoft.EdgeWebView2Runtime' 'the WebView2 Runtime'
    }
    finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
}

function Set-Microphone {
    $block = Get-MicrophoneBlock
    if (-not $block) { Write-Host 'Windows already lets desktop apps use the microphone.'; return }
    Write-Host "Windows reports that $block."
    Write-Host 'In the Settings page that opens, turn on "Microphone access" and "Let desktop apps access your microphone".'
    Write-Host 'Martlet still asks before every capture; this only lets Windows deliver audio when you press to talk.'
    Open-Page 'ms-settings:privacy-microphone'
}

function Install-WindowsSpeech {
    $counts = Get-SpeechCounts
    if ($counts.Known -and $counts.Recognizers -gt 0 -and $counts.Voices -gt 0) {
        Write-Host "Windows speech for $Culture is already installed."
        return
    }
    $names = @("Language.Speech~~~$Culture~0.0.1.0", "Language.TextToSpeech~~~$Culture~0.0.1.0")
    $list = ($names | ForEach-Object { "'$_'" }) -join ', '
    $script = @"
foreach (`$name in @($list)) {
    `$capability = Get-WindowsCapability -Online -Name `$name -ErrorAction SilentlyContinue
    if (-not `$capability -or -not `$capability.State) { Write-Host "Windows offers no `$name for this language."; continue }
    if ([string]`$capability.State -eq 'Installed') { Write-Host "`$name is already installed."; continue }
    Write-Host "Adding `$name from Windows Update. This can take a few minutes..."
    try { Add-WindowsCapability -Online -Name `$name | Out-Null; Write-Host "Added `$name." -ForegroundColor Green }
    catch { Write-Host "Could not add `$name (`$(`$_.Exception.Message))" -ForegroundColor Yellow }
}
"@
    $null = Invoke-Elevated $script "add Windows speech recognition and voices for $Culture"
    $counts = Get-SpeechCounts
    if ($counts.Known -and ($counts.Recognizers -eq 0 -or $counts.Voices -eq 0)) {
        Write-Note "Windows still reports $($counts.Recognizers) recognizer(s) and $($counts.Voices) voice(s) for $Culture."
        Write-Host 'Add the language with its speech features in Settings > Time & language > Language & region, then Speech.'
        Open-Page 'ms-settings:regionlanguage'
    }
}

function Test-OllamaServer {
    try {
        $null = Invoke-WebRequest -UseBasicParsing -Uri "$OllamaEndpoint/api/version" -TimeoutSec 3
        return $true
    }
    catch { return $false }
}

function Start-OllamaServer([string]$Ollama) {
    if (Test-OllamaServer) { return $true }
    $app = Join-Path (Split-Path -Parent $Ollama) 'ollama app.exe'
    if (Test-Path -LiteralPath $app) { Start-Process -FilePath $app }
    else { Start-Process -FilePath $Ollama -ArgumentList 'serve' -WindowStyle Hidden }
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        if (Test-OllamaServer) { return $true }
    }
    return $false
}

function Get-SuggestedOllamaModel {
    # Every suggestion also sees images (screen watching) and calls tools. A "12 GB" card reports 11 GiB here.
    $vram = Get-NvidiaVramGiB
    if ($vram -ge 23) { return [pscustomobject]@{ Tag = 'gemma4:26b'; Size = 'about 19 GB' } }
    if ($vram -ge 11) { return [pscustomobject]@{ Tag = 'gemma4:12b'; Size = 'about 8 GB' } }
    if ($vram -ge 7) { return [pscustomobject]@{ Tag = 'gemma4:e4b'; Size = 'about 7 GB' } }
    return [pscustomobject]@{ Tag = 'gemma4:e2b'; Size = 'about 5 GB' }
}

function Install-Ollama {
    if (-not (Get-OllamaPath)) {
        Write-Host 'Ollama is MIT-licensed. Models you download keep their own licenses.'
        if (-not (Install-WithWinget 'Ollama.Ollama' 'Ollama')) { Open-Page 'https://ollama.com/download/windows'; return }
    }
    $ollama = Get-OllamaPath
    if (-not $ollama) { Write-Note 'Ollama is not installed yet. Finish its installer, then run this item again to download a model.'; return }
    $model = $OllamaModel.Trim()
    if (-not $model -and -not $NoPrompt) {
        $suggested = Get-SuggestedOllamaModel
        Write-Host "Suggested model for this PC's GPU: $($suggested.Tag) ($($suggested.Size); see docs\RECOMMENDED_SETUPS.md)."
        $answer = (Read-Host "Type y to download $($suggested.Tag), type another Ollama model tag, or press Enter to skip").Trim()
        if ($answer -ieq 'y' -or $answer -ieq 'yes') { $model = $suggested.Tag }
        elseif ($answer) { $model = $answer }
    }
    if ($model) {
        if ($model -notmatch '\A[A-Za-z0-9][A-Za-z0-9._/:-]{0,199}\z') { Write-Note "Not a valid Ollama model tag: $model"; return }
        if (-not (Start-OllamaServer $ollama)) {
            Write-Note "Ollama did not start at $OllamaEndpoint. Start Ollama, then run: ollama pull $model"
            return
        }
        Write-Host "Downloading $model with Ollama..."
        & $ollama pull $model
        if ($LASTEXITCODE -ne 0) { Write-Note "ollama pull finished with exit code $LASTEXITCODE." }
    }
    $shown = if ($model) { $model } else { '<the model tag you pull>' }
    Write-Host ''
    Write-Host 'To use it in Martlet: Setup / resume > Destinations > LLM provider: Custom OpenAI-compatible endpoint,' -ForegroundColor Green
    Write-Host "  base URL $OllamaEndpoint/v1, model $shown, no API key needed." -ForegroundColor Green
}

function Install-NvidiaDriver {
    if (@(Get-NvidiaGpus).Count -eq 0) { Write-Host 'No NVIDIA GPU was found. Martlet works without one (APIs, Windows speech, loudness lip-sync).'; return }
    $driver = Get-NvidiaDriver
    if ($driver) { Write-Host "The NVIDIA driver answers: $driver"; return }
    Write-Host "NVIDIA drivers cannot be redistributed with Martlet. Download the current Game Ready or Studio driver"
    Write-Host 'for your GPU from NVIDIA (or use the NVIDIA App), install it, then run this check again.'
    Open-Page 'https://www.nvidia.com/en-us/drivers/'
}

function Test-VirtualizationAvailable {
    # A running Windows hypervisor hides the firmware flag, so either one means virtualization is available.
    $system = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
    $processor = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $system -and -not $processor) { return $true }
    return [bool]($system.HypervisorPresent -or $processor.VirtualizationFirmwareEnabled)
}

function Install-DockerDesktop {
    Write-Host 'Docker Desktop is free for personal use under the Docker Subscription Service Agreement (docker.com/legal).'
    if (-not (Test-VirtualizationAvailable)) {
        Write-Note "Virtualization is turned off in this PC's firmware (UEFI/BIOS), and Docker Desktop needs it. Restart, open the"
        Write-Note 'firmware settings (usually Del, F2 or F10 while the PC starts), turn on Intel Virtualization Technology (VT-x) or'
        Write-Note 'SVM Mode (AMD), save and exit, then run this item again.'
    }
    # One elevated step turns on everything Docker Desktop's WSL 2 engine needs; Martlet's own Docker Desktop setup does the same.
    $script = @'
$restart = $false
foreach ($name in @('VirtualMachinePlatform', 'Microsoft-Windows-Subsystem-Linux')) {
    $feature = Get-WindowsOptionalFeature -Online -FeatureName $name
    if (-not $feature) { Write-Host "This edition of Windows has no $name feature."; continue }
    $state = [string]$feature.State
    if ($state -eq 'Enabled') { Write-Host "$name is already on."; continue }
    if ($state -eq 'EnablePending') { Write-Host "$name turns on when Windows restarts."; $restart = $true; continue }
    Write-Host "Turning on $name..."
    if ((Enable-WindowsOptionalFeature -Online -FeatureName $name -All -NoRestart).RestartNeeded) { $restart = $true }
}
if ((& bcdedit.exe /enum '{current}' 2>$null | Out-String) -match 'hypervisorlaunchtype\s+Off') {
    & bcdedit.exe /set '{current}' hypervisorlaunchtype auto | Out-Null
    Write-Host 'The Windows hypervisor was set not to start; it now starts with Windows.'
    $restart = $true
}
$env:WSL_UTF8 = '1'
Write-Host 'Installing or updating WSL from Microsoft (wsl --update)...'
& wsl.exe --update
if ($restart) { Write-Host 'Restart Windows to finish turning on virtualization.'; exit 3010 }
'@
    $exit = Invoke-Elevated $script 'turn on Virtual Machine Platform and Windows Subsystem for Linux, and install or update WSL 2'
    if (Get-DockerDesktopPath) { Write-Host 'Docker Desktop is already installed.' }
    elseif (-not (Install-WithWinget 'Docker.DockerDesktop' 'Docker Desktop')) { Open-Page 'https://docs.docker.com/desktop/setup/install/windows-install/' }
    if ($exit -eq 3010) { Write-Note 'Restart Windows to finish turning on virtualization, then start Docker Desktop.' }
    Write-Host 'Then open Martlet > Martlet hosts > This PC and press Set up host. GPU roles also need the NVIDIA driver.'
}

function Invoke-Prerequisite([string]$Id) {
    $parts = $Descriptions[$Id].Split('|')
    Write-Heading $parts[0]
    try {
        switch ($Id) {
            'WebView2' { Install-WebView2 }
            'Microphone' { Set-Microphone }
            'WindowsSpeech' { Install-WindowsSpeech }
            'Ollama' { Install-Ollama }
            'NvidiaDriver' { Install-NvidiaDriver }
            'DockerDesktop' { Install-DockerDesktop }
        }
    }
    catch { Write-Note "$($parts[0]) did not finish: $($_.Exception.Message)" }
}

function Resolve-Ids([string[]]$Values) {
    $resolved = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })) {
        $match = @($Ids | Where-Object { $_ -ieq $value })
        if ($match.Count -ne 1) { throw "Unknown prerequisite '$value'. Use: $($Ids -join ', ')." }
        if (-not $resolved.Contains($match[0])) { $resolved.Add($match[0]) }
    }
    return , $resolved.ToArray()
}

# --- Entry point ---------------------------------------------------------------------------

if ($Check) {
    Show-Status
    $required = @('WebView2', 'Microphone' | Where-Object { (Get-PrerequisiteState $_).State -ne 'OK' })
    exit $(if ($required.Count -eq 0) { 0 } else { 2 })
}

$selected = Resolve-Ids $Install
if ($selected.Count -gt 0) {
    foreach ($id in $selected) { Invoke-Prerequisite $id }
    Show-Status
    Write-Host ''
    Write-Host 'Run "Martlet prerequisites" from the Start menu (or Prerequisites on the Martlet home screen) any time to check again.'
    if ($PauseWhenDone -and -not $NoPrompt) { Read-Host 'Press Enter to close this window and return to Martlet' | Out-Null }
    exit 0
}

if ($NoPrompt) { Show-Status; exit 0 }

while ($true) {
    Show-Status
    Write-Host ''
    $answer = (Read-Host 'Type item numbers to install or configure (for example 3,4), R to recheck, or Q to quit').Trim()
    if ($answer -ieq 'q' -or $answer -ieq 'quit') { break }
    if (-not $answer -or $answer -ieq 'r') { continue }
    foreach ($token in ($answer -split '[,\s]+' | Where-Object { $_ })) {
        $number = 0
        if ([int]::TryParse($token, [ref]$number) -and $number -ge 1 -and $number -le $Ids.Count) { Invoke-Prerequisite $Ids[$number - 1] }
        else { Write-Note "Ignored '$token'; type a number from 1 to $($Ids.Count)." }
    }
}
