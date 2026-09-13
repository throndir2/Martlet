[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The desktop smoke requires Windows and an interactive desktop session.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('MartletDesktopSmokeKeys' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MartletDesktopSmokeKeys {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr window, uint message, UIntPtr key, IntPtr flags);
}
'@
}
$root = Split-Path $PSScriptRoot -Parent
$candidate = if ($ExecutablePath) { $ExecutablePath } else {
    Join-Path $root "src\Martlet.Desktop\bin\$Configuration\net10.0-windows\Martlet.Desktop.dll"
}
if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'Build Desktop first or provide an existing executable path.' }
$resolved = Resolve-Path -LiteralPath $candidate
if ($resolved.Provider.Name -ne 'FileSystem') { throw 'Executable path must be a filesystem file.' }
$binary = $resolved.ProviderPath
$extension = [System.IO.Path]::GetExtension($binary).ToLowerInvariant()
if ($extension -notin @('.dll', '.exe')) { throw 'Executable path must select a .dll or .exe.' }
$program = if ($extension -eq '.dll') { (Get-Command dotnet).Source } else { $binary }
$data = Join-Path ([System.IO.Path]::GetTempPath()) ("Martlet.Desktop.Smoke." + [guid]::NewGuid().ToString('N'))
$settings = Join-Path $data 'settings.json'
$process = $null
$window = $null
$setupSnapshots = @()

function Find-Control([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Read-Value($Control) {
    return $Control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function Wait-Status([string]$Pattern, [string]$DifferentFrom = '') {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:process.HasExited) { throw 'Desktop exited before displaying status.' }
        $script:process.Refresh()
        if ($script:process.MainWindowHandle -ne 0) {
            $script:window = [System.Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle)
            $status = Find-Control 'FoundationStatus'
            if ($null -ne $status) {
                $value = Read-Value $status
                if ($value -like $Pattern -and $value -ne $DifferentFrom) {
                    if ($value.Contains($data) -or $value.Contains('PRIVATE-CANARY')) { throw 'Status leaked private input.' }
                    return $value
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Accessible diagnostic status was not available within 20 seconds.'
}
function Start-Desktop([string]$Directory = $data) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($program)
    $start.UseShellExecute = $false
    if ($extension -eq '.dll') { $start.ArgumentList.Add($binary) }
    $start.ArgumentList.Add('--data-directory')
    $start.ArgumentList.Add($Directory)
    $script:process = [System.Diagnostics.Process]::Start($start)
}
function Close-Desktop {
    if (-not $script:process.CloseMainWindow() -or -not $script:process.WaitForExit(10000)) {
        throw 'Closing the diagnostic window did not exit Martlet within 10 seconds.'
    }
    if ($script:process.ExitCode -ne 0) { throw "Desktop exited with code $($script:process.ExitCode)." }
    $script:process.Dispose()
    $script:process = $null
}
function Invoke-Control([string]$Id) {
    $control = Find-Control $Id
    if ($null -eq $control -or -not $control.Current.IsEnabled -or [string]::IsNullOrWhiteSpace($control.Current.Name)) {
        throw 'Required accessible action is unavailable.'
    }
    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Select-Fixture([string]$Name) {
    $choice = Find-Control 'FixtureScenario'
    $choice.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $item = $choice.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $item) { throw 'Requested accessible fixture scenario was not found.' }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $choice.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
}
function Wait-Fixture([string]$Pattern) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:process.HasExited) { throw 'Desktop exited during fixture.' }
        $control = Find-Control 'FixtureStatus'
        if ($null -ne $control) {
            $value = Read-Value $control
            if ($value -like $Pattern) {
                if (-not $control.Current.IsKeyboardFocusable -or $value -notlike '*FIXTURE - NOT AI*' -or
                    $value.Contains($data) -or $value.Contains('PRIVATE-CANARY')) { throw 'Fixture accessibility/provenance/privacy failed.' }
                return $value
            }
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'Accessible fixture status was not available within 20 seconds.'
}

function Wait-Setup([string]$Pattern, [string]$StatusId = 'SetupStatus') {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:process.HasExited) { throw 'Desktop exited during setup.' }
        $script:process.Refresh()
        if ($script:process.MainWindowHandle -ne 0) {
            # WPF exposes the owned modal setup below its owner in the UI Automation tree.
            $script:window = [Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle)
            $status = Find-Control $StatusId
            if ($null -ne $status -and (Read-Value $status) -like $Pattern) {
                if (-not $status.Current.IsKeyboardFocusable) { throw 'Setup status is not keyboard accessible.' }
                $status.SetFocus()
                if (-not $status.Current.HasKeyboardFocus) { throw 'Setup status did not accept keyboard focus.' }
                return Read-Value $status
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Accessible setup checkpoint was not available within 20 seconds: $Pattern"
}

function Stop-ActiveFixture {
    $script:process.Refresh()
    $handle = $script:process.MainWindowHandle
    if ($script:process.HasExited -or $handle -eq 0 -or $script:window.Current.ProcessId -ne $script:process.Id) {
        throw 'The owned fixture window is no longer available.'
    }
    $stop = Find-Control 'StopFixture'
    if ($null -eq $stop -or $stop.Current.Name -ne 'Stop fixture and tone') {
        throw 'Required accessible fixture Stop action is unavailable.'
    }
    $active = Read-Value (Find-Control 'FixtureStatus')
    if ($active -notlike '*Scenario: slow*Stage: Script*' -or $active -notlike '*fixture.running*' -or -not $stop.Current.IsEnabled) {
        throw 'The selected fixture is not currently active and cancelable; no Stop input was sent.'
    }
    [uint32]$owner = 0
    if ([MartletDesktopSmokeKeys]::GetWindowThreadProcessId($handle, [ref]$owner) -eq 0 -or $owner -ne $script:process.Id) {
        throw 'The fixture HWND no longer belongs to the launched process; no input was sent.'
    }
    # UIA Invoke can spend two seconds in its RPC before reaching this two-second scenario.
    # Post the existing Alt+F access key only to this verified HWND, never global SendInput.
    # WPF's ordinary access-key/enablement path executes Stop; the canceled result is required below.
    if (-not [MartletDesktopSmokeKeys]::PostMessage($handle, 0x0106, [UIntPtr]0x66, [IntPtr]0x20000001)) {
        throw 'The targeted fixture Stop access-key message could not be queued.'
    }
}

try {
    Start-Desktop
    $first = Wait-Status '*First run:*'
    if ($first -notlike '*not ready*') { throw 'First-run diagnostics must remain incomplete.' }
    if (Test-Path -LiteralPath $data) { throw 'Read-only launch unexpectedly created a data directory.' }
    $pipeline = Find-Control 'PipelineStatus'
    if ($null -eq $pipeline -or -not $pipeline.Current.IsKeyboardFocusable) { throw 'Pipeline must support keyboard and accessibility access.' }
    $pipelineText = Read-Value $pipeline
    foreach ($stage in @('Mic', 'VAD', 'STT', 'Policy', 'LLM', 'TTS', 'Playback')) {
        if ($pipelineText -notlike "*${stage}:*NotRun*") { throw 'A pipeline stage lacks truthful accessible state.' }
    }
    $pipeline.SetFocus()
    if (-not $pipeline.Current.HasKeyboardFocus) { throw 'Pipeline did not accept keyboard focus.' }
    Invoke-Control 'StartFixture'
    $complete = Wait-Fixture '*fixture.completed*'
    if ($complete -notlike '*Synthetic text: A synthetic fixture response.*' -or $complete -notlike '*Audio OFF / not run*') {
        throw 'The desktop demo did not render actual fixture text with audio off.'
    }
    if ((Read-Value $pipeline) -notlike '*LLM:*NotRun*' -or (Test-Path -LiteralPath $data)) {
        throw 'Fixture success modified real readiness or persisted a profile.'
    }
    Select-Fixture 'refused-after-partial'
    Invoke-Control 'StartFixture'
    $refused = Wait-Fixture '*fixture.refused*'
    if ($refused -notlike '*partial; not a completed answer*' -or $refused -notlike '*Separate scripted refusal (never read aloud)*') {
        throw 'Refusal was not kept separate from partial synthetic text.'
    }
    Select-Fixture 'slow'
    Invoke-Control 'StartFixture'
    $running = Wait-Fixture '*Scenario: slow*Stage: Script*'
    Stop-ActiveFixture
    $stopped = Wait-Fixture '*Scenario: slow*Stage: Finished*fixture.stopped*'
    if ($stopped -notlike '*Scenario: slow*Stage: Finished*' -or $stopped -notlike '*outcome: Canceled*' -or
        $stopped -notlike '*queued text: 0*' -or $stopped -notmatch '(?m)^Synthetic text: \r?$') {
        throw 'Stop did not finish with an explicit canceled outcome and empty text/queue.'
    }
    Select-Fixture 'streaming'
    Invoke-Control 'StartFixture'
    $streaming = Wait-Fixture '*fixture.completed*'
    if ($streaming -notlike '*Synthetic text: Synthetic text.*' -or $streaming -like '*Partial fixture.*') {
        throw 'New fixture did not replace retired output in order.'
    }
    $oldIds = [regex]::Match($stopped, '(?m)^Session:.*').Value
    $newIds = [regex]::Match($streaming, '(?m)^Session:.*').Value
    if (-not $oldIds -or -not $newIds -or $oldIds -ceq $newIds) {
        throw 'New fixture did not use fresh IDs after Stop; retired output cannot be accepted.'
    }
    $stop = Find-Control 'StopDiagnostics'
    if ($null -eq $stop -or $stop.Current.IsEnabled -or $stop.Current.Name -ne 'Stop diagnostics') {
        throw 'Stop must be exposed and disabled while idle.'
    }
    Invoke-Control 'RefreshDiagnostics'
    $refreshed = Wait-Status '*First run:*' $first
    if ($refreshed -notlike '*settings.first_run*') { throw 'Refresh did not use the shared diagnostic catalog.' }
    Invoke-Control 'OpenAudioSetup'
    $audio = Wait-Setup '*never tested*UNVERIFIED*' 'AudioStatus'
    if ($audio -notlike '*No provider*' -or (Find-Control 'AudioHeard').Current.IsEnabled) {
        throw 'Unrun audio setup must remain local, unverified and not confirmable.'
    }
    if (Test-Path -LiteralPath $data) { throw 'Opening audio setup wrote settings before explicit save.' }
    # Deliberately do not invoke Find, microphone, tone, or any effectful audio action.
    Invoke-Control 'AudioClose'
    $null = Wait-Status '*First run:*'
    Invoke-Control 'OpenLiveConversation'
    $live = Wait-Setup '*REAL API mode; NOT RUN*No effects authorized*Choices loaded*' 'LiveStatus'
    foreach ($id in @('LiveSend', 'LivePtt', 'LiveRelease')) {
        $control = Find-Control $id
        if ($null -eq $control -or $control.Current.IsEnabled) { throw 'Unconfigured live actions must be explicitly gated.' }
    }
    foreach ($id in @('LiveVoice', 'LiveAcceptAction', 'LiveAcceptCapture', 'LiveAcceptUpload')) {
        $control = Find-Control $id
        if ($null -eq $control -or $control.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
            [Windows.Automation.ToggleState]::Off) { throw 'Live output and permission must start OFF.' }
    }
    if (Test-Path -LiteralPath $data) { throw 'Opening live conversation unexpectedly wrote settings.' }
    # Do not accept permission or invoke a live Send/PTT. This is a no-key/no-network/no-device native smoke.
    Invoke-Control 'CloseLive'
    $null = Wait-Status '*First run:*'
    Invoke-Control 'OpenSetup'
    $null = Wait-Setup '*Checkpoint: Choice*'
    if (Test-Path -LiteralPath $data) { throw 'Opening setup wrote files before an explicit save.' }
    Invoke-Control 'SetupClose'
    $null = Wait-Status '*First run:*'
    Invoke-Control 'CreateProfile'
    $saved = Wait-Status '*Profile: NotConfigured;*'
    if ($saved -notlike '*settings.valid*' -or -not [System.IO.File]::Exists($settings)) { throw 'Explicit profile creation failed.' }
    if ((Find-Control 'CreateProfile').Current.IsEnabled) { throw 'Create must be disabled for an existing profile.' }
    $legacyBytes = [IO.File]::ReadAllBytes($settings)
    $legacyId = ([Text.Encoding]::UTF8.GetString($legacyBytes) | ConvertFrom-Json).profile.id
    Invoke-Control 'OpenSetup'
    $null = Wait-Setup '*Checkpoint: Choice*'
    Invoke-Control 'SetupSaveExit'
    $null = Wait-Status '*Profile: Fixture;*Setup checkpoint: Choice*'
    $setupSnapshots = @(Get-ChildItem -LiteralPath $data -Filter 'settings.v1.*.bak' -File | Select-Object -ExpandProperty FullName)
    if ($setupSnapshots.Count -ne 1 -or
        [Convert]::ToHexString([IO.File]::ReadAllBytes($setupSnapshots[0])) -cne [Convert]::ToHexString($legacyBytes)) {
        throw 'Explicit setup migration did not preserve the original version 1 bytes.'
    }
    Invoke-Control 'OpenSetup'
    $null = Wait-Setup '*Checkpoint: Choice*'
    (Find-Control 'SetupApi').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Control 'SetupNext'
    $null = Wait-Setup '*Checkpoint: Destinations*'
    (Find-Control 'SetupModel').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('gpt-4o-mini-transcribe')
    (Find-Control 'SetupConsent').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Invoke-Control 'SetupApplyRoute'
    $null = Wait-Setup '*Stt: route selected; destination choice recorded*credential not configured*'
    Invoke-Control 'SetupNext'
    $null = Wait-Setup '*Checkpoint: Credentials*'
    $key = Find-Control 'SetupKey'
    if ($null -eq $key -or -not $key.Current.IsPassword) { throw 'API key entry must be masked.' }
    Invoke-Control 'SetupNext'
    $null = Wait-Setup '*Checkpoint: Review*'
    Invoke-Control 'SetupBack'
    $null = Wait-Setup '*Checkpoint: Credentials*'
    Invoke-Control 'SetupSaveExit'
    $null = Wait-Status '*Profile: Api;*Setup checkpoint: Credentials*key not configured*'
    Close-Desktop
    Start-Desktop
    $null = Wait-Status '*Setup checkpoint: Credentials*'
    Invoke-Control 'OpenSetup'
    $null = Wait-Setup '*Checkpoint: Credentials*'
    Invoke-Control 'SetupBack'
    $null = Wait-Setup '*Checkpoint: Destinations*'
    (Find-Control 'SetupModel').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('whisper-1')
    Invoke-Control 'SetupApplyRoute'
    $null = Wait-Setup '*Stt: route selected; consent missing or invalidated*'
    Invoke-Control 'SetupSaveExit'
    $null = Wait-Status '*Setup checkpoint: Destinations*consent missing or invalidated*'
    Close-Desktop
    $configured = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    if ($configured.schema_version -ne 2 -or $configured.profile.id -ne $legacyId -or
        $configured.setup.routes[0].model_id -cne 'whisper-1' -or $null -ne $configured.setup.routes[0].consent -or
        $null -ne $configured.setup.routes[0].credential_id -or $configured.setup.pending_removals.Count -ne 0) {
        throw 'Actual no-key setup did not preserve identity, model edit and consent invalidation.'
    }

    foreach ($case in @(
        @{ Content = '{PRIVATE-CANARY'; Code = 'settings.malformed' },
        @{ Content = '{"schema_version":99}'; Code = 'settings.version' }
    )) {
        [byte[]]$bytes = [System.Text.Encoding]::UTF8.GetBytes($case.Content)
        [System.IO.File]::WriteAllBytes($settings, $bytes)
        Start-Desktop
        $value = Wait-Status "*$($case.Code)*"
        if ($value -notlike '*exit 3*' -or $value -notlike '*settings.restore*' -or (Find-Control 'CreateProfile').Current.IsEnabled) {
            throw 'Invalid settings did not show an actionable, non-destructive failure.'
        }
        Invoke-Control 'StartFixture'
        $null = Wait-Fixture '*fixture.completed*'
        Close-Desktop
        if ([Convert]::ToHexString([System.IO.File]::ReadAllBytes($settings)) -cne [Convert]::ToHexString($bytes)) {
            throw 'Desktop changed invalid settings bytes.'
        }
    }
    [System.IO.File]::Delete($settings)
    [System.IO.Directory]::CreateDirectory($settings) | Out-Null
    Start-Desktop
    $value = Wait-Status '*settings.inaccessible*'
    if ($value -notlike '*settings.check_access*' -or (Find-Control 'CreateProfile').Current.IsEnabled) {
        throw 'Inaccessible settings must remain a guided error, not first run.'
    }
    Close-Desktop
    [System.IO.Directory]::Delete($settings)
    Start-Desktop
    $null = Wait-Status '*First run:*'
    Select-Fixture 'slow'
    Invoke-Control 'StartFixture'
    $null = Wait-Fixture '*Scenario: slow*Stage: Script*'
    Close-Desktop
    Start-Desktop 'relative'
    $value = Wait-Status '*Cannot open the data directory*'
    if ((Find-Control 'CreateProfile').Current.IsEnabled -or (Find-Control 'RefreshDiagnostics').Current.IsEnabled) {
        throw 'Invalid launch arguments must leave a read-only, actionable startup error.'
    }
    Close-Desktop
    Write-Output 'PASS: actual offline fixtures/refusal/Stop, audio OFF; accessible no-key setup/migration/save/resume/Back/model consent invalidation; profile/remedies/preservation; bounded close. No OS credential actions or network.'
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
    if ([System.IO.File]::Exists($settings)) { [System.IO.File]::Delete($settings) }
    if ([System.IO.Directory]::Exists($settings)) { [System.IO.Directory]::Delete($settings) }
    $lockFile = Join-Path $data 'settings.json.lock'
    if ([System.IO.File]::Exists($lockFile)) { [System.IO.File]::Delete($lockFile) }
    foreach ($snapshot in $setupSnapshots) { [IO.File]::Delete($snapshot) }
    if ([System.IO.Directory]::Exists($data)) { [System.IO.Directory]::Delete($data) }
}
