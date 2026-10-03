[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$ExecutablePath,
    [switch]$CompanionOnly
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
    Write-Verbose "$([DateTime]::UtcNow.ToString('O')) Invoking $Id"
    $control = Find-Control $Id
    if ($null -eq $control -or -not $control.Current.IsEnabled -or [string]::IsNullOrWhiteSpace($control.Current.Name)) {
        throw 'Required accessible action is unavailable.'
    }
    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Verbose "$([DateTime]::UtcNow.ToString('O')) Invoked $Id"
}

function Select-Theme([string]$Name) {
    $choice = Find-Control 'AppearanceTheme'
    if ($null -eq $choice -or -not $choice.Current.IsKeyboardFocusable) { throw 'Appearance selector is not keyboard accessible.' }
    $choice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty, $Name)
    $item = $choice.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $item) { throw 'Requested theme is unavailable.' }
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $choice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Find-Control 'AppearanceStatus').Current.Name -like "$Name saved locally*") { return }
        Start-Sleep -Milliseconds 50
    }
    throw 'Theme selection did not report successful persistence.'
}

function Wait-Setup([string]$Pattern, [string]$StatusId = 'SetupStatus') {
    Write-Verbose "$([DateTime]::UtcNow.ToString('O')) Waiting for $StatusId"
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
                if (-not $status.Current.HasKeyboardFocus) { $status.SetFocus() }
                if (-not $status.Current.HasKeyboardFocus) { throw 'Setup status did not accept keyboard focus.' }
                Write-Verbose "$([DateTime]::UtcNow.ToString('O')) Ready $StatusId"
                return Read-Value $status
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Accessible setup checkpoint was not available within 20 seconds: $Pattern"
}


try {
    Start-Desktop
    $first = Wait-Status '*First run:*'
    if ($first -notlike '*not ready*') { throw 'First-run diagnostics must remain incomplete.' }
    if (Test-Path -LiteralPath $data) { throw 'Read-only launch unexpectedly created a data directory.' }
    if (-not $CompanionOnly) {
        Invoke-Control 'OpenTroubleshooting'
        $support = Wait-Setup '*Recording: OFF*worker: idle*' 'SupportStatus'
        if ((Find-Control 'SupportExport').Current.IsEnabled -or
            (Find-Control 'SupportIncludeLogs').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
            [Windows.Automation.ToggleState]::Off) { throw 'Support export must be unavailable without preview, and journal selection must default OFF.' }
        $supportHelp = Read-Value (Find-Control 'SupportHelp')
        if ($supportHelp -notlike '*No support contact or upload channel is configured*' -or
            $supportHelp -notlike '*CLI export is not implemented*') { throw 'Support limitations are not visible.' }
        if (Test-Path -LiteralPath $data) { throw 'Passive troubleshooting unexpectedly created app data or started a journal.' }
        Invoke-Control 'SupportClose'
        $null = Wait-Status '*First run:*'
        $pipeline = Find-Control 'PipelineStatus'
        if ($null -eq $pipeline -or -not $pipeline.Current.IsKeyboardFocusable) { throw 'Pipeline must support keyboard and accessibility access.' }
        $pipelineText = Read-Value $pipeline
        foreach ($stage in @('Mic', 'VAD', 'STT', 'Policy', 'LLM', 'TTS', 'Playback')) {
            if ($pipelineText -notlike "*${stage}:*NotRun*") { throw 'A pipeline stage lacks truthful accessible state.' }
        }
        $pipeline.SetFocus()
        if (-not $pipeline.Current.HasKeyboardFocus) { throw 'Pipeline did not accept keyboard focus.' }
        $stop = Find-Control 'StopDiagnostics'
        if ($null -eq $stop -or $stop.Current.IsEnabled -or $stop.Current.Name -ne 'Stop diagnostics') {
            throw 'Stop must be exposed and disabled while idle.'
        }
        Invoke-Control 'RefreshDiagnostics'
        $refreshed = Wait-Status '*First run:*' $first
        if ($refreshed -notlike '*settings.first_run*') { throw 'Refresh did not use the shared diagnostic catalog.' }
        Invoke-Control 'OpenAudioSetup'
        # Exact local evidence sits under the window's Details expander.
        $details = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ($null -eq $details -and [DateTime]::UtcNow -lt $deadline) {
            $script:process.Refresh()
            $script:window = [Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle)
            $details = Find-Control 'AudioDetails'
            if ($null -eq $details) { Start-Sleep -Milliseconds 100 }
        }
        if ($null -eq $details) { throw 'Audio setup details are not accessible.' }
        $details.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $audio = Wait-Setup '*never tested*UNVERIFIED*' 'AudioStatus'
        $heard = Find-Control 'AudioHeard'
        if ($audio -notlike '*No provider*' -or ($null -ne $heard -and $heard.Current.IsEnabled)) {
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
        foreach ($id in @('LiveVoice', 'LiveAcceptAction', 'LiveAcceptCapture', 'LiveAcceptUpload', 'LiveAcceptMemory')) {
            $control = Find-Control $id
            if ($null -eq $control -or $control.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
                [Windows.Automation.ToggleState]::Off) { throw 'Live output and permission must start OFF.' }
        }
        if (Test-Path -LiteralPath $data) { throw 'Opening live conversation unexpectedly wrote settings.' }
        # Do not accept permission or invoke a live Send/PTT. This is a no-key/no-network/no-device native smoke.
        Invoke-Control 'LiveOpenSetup'
        $null = Wait-Setup '*Checkpoint: Choice*'
        Invoke-Control 'SetupRecovery'
        $null = Wait-Setup '*No backup read or written*' 'RecoveryResult'
        if ((Find-Control 'RecoveryRestore').Current.IsEnabled -or (Test-Path -LiteralPath $data)) {
            throw 'Conversation -> Setup -> Recovery must remain passive and require an exact preview.'
        }
        Invoke-Control 'RecoveryClose'
        $null = Wait-Setup '*Recovery closed*Reload*' 'SetupResult'
        Invoke-Control 'SetupClose'
        $null = Wait-Setup '*Choices loaded*' 'LiveStatus'
        Invoke-Control 'CloseLive'
        $null = Wait-Status '*First run:*'
        Invoke-Control 'OpenConfigurationRecovery'
        $null = Wait-Setup '*No backup read or written*' 'RecoveryResult'
        if ((Find-Control 'RecoveryRestore').Current.IsEnabled -or (Test-Path -LiteralPath $data)) {
            throw 'Passive recovery must not read/write snapshots or enable restore without a preview.'
        }
        Invoke-Control 'RecoveryClose'
        $null = Wait-Status '*First run:*'
        Invoke-Control 'OpenSetup'
        $null = Wait-Setup '*Checkpoint: Choice*'
        if (Test-Path -LiteralPath $data) { throw 'Opening setup wrote files before an explicit save.' }
        Invoke-Control 'SetupTroubleshooting'
        $null = Wait-Setup '*Recording: OFF*worker: idle*' 'SupportStatus'
        if (Test-Path -LiteralPath $data) { throw 'Troubleshooting inside first-run setup wrote files.' }
        Invoke-Control 'SupportClose'
        $null = Wait-Setup '*Checkpoint: Choice*'
        Invoke-Control 'SetupRecovery'
        $null = Wait-Setup '*No backup read or written*' 'RecoveryResult'
        if (Test-Path -LiteralPath $data) { throw 'Passive recovery inside setup wrote files.' }
        Invoke-Control 'RecoveryClose'
        $null = Wait-Setup '*Recovery closed*Reload*' 'SetupResult'
        Invoke-Control 'SetupClose'
        $null = Wait-Status '*First run:*'
    }
    Invoke-Control 'CreateProfile'
    $saved = Wait-Status '*Profile: NotConfigured;*'
    if ($saved -notlike '*settings.valid*' -or -not [System.IO.File]::Exists($settings)) { throw 'Explicit profile creation failed.' }
    if ((Find-Control 'CreateProfile').Current.IsEnabled) { throw 'Create must be disabled for an existing profile.' }
    $legacyBytes = [IO.File]::ReadAllBytes($settings)
    $legacyId = ([Text.Encoding]::UTF8.GetString($legacyBytes) | ConvertFrom-Json).profile.id
    Invoke-Control 'OpenSetup'
    $null = Wait-Setup '*Checkpoint: Choice*'
    Invoke-Control 'SetupSaveExit'
    $null = Wait-Status '*Profile: Api;*Setup checkpoint: Choice*'
    $setupSnapshots = @(Get-ChildItem -LiteralPath $data -Filter 'settings.v1.*.bak' -File | Select-Object -ExpandProperty FullName)
    if ($setupSnapshots.Count -ne 1 -or
        [Convert]::ToHexString([IO.File]::ReadAllBytes($setupSnapshots[0])) -cne [Convert]::ToHexString($legacyBytes)) {
        throw 'Explicit setup migration did not preserve the original version 1 bytes.'
    }
    Invoke-Control 'OpenSetup'
    $null = Wait-Setup '*Checkpoint: Choice*'
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
    if ($CompanionOnly) {
        $beforeCompanion = [IO.File]::ReadAllBytes($settings)
        Invoke-Control 'OpenCompanion'
        $null = Wait-Setup '*All changes saved*' 'CompanionSaveState'
        if ([Convert]::ToHexString([IO.File]::ReadAllBytes($settings)) -cne [Convert]::ToHexString($beforeCompanion)) {
            throw 'Opening the companion editor modified saved settings.'
        }
        (Find-Control 'CompanionName').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Smoke persona')
        (Find-Control 'CompanionText').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Offline smoke persona; no provider action.')
        # Personality has no Save button: edits save on their own.
        Start-Sleep -Milliseconds 100
        $null = Wait-Setup '*All changes saved*' 'CompanionSaveState'
        Invoke-Control 'CompanionClose'
        $null = Wait-Status '*Setup checkpoint: Destinations*consent missing or invalidated*'
        $beforeMemory = [IO.File]::ReadAllBytes($settings)
        Invoke-Control 'OpenMemory'
        $null = Wait-Setup '*Memory settings loaded: OFF*fact store was not opened*' 'MemoryStatus'
        foreach ($id in @('MemoryEnable', 'MemoryAcceptEnable', 'MemoryAcceptExport')) {
            if ((Find-Control $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
                [Windows.Automation.ToggleState]::Off) { throw 'Memory enablement and disclosure permissions must default OFF.' }
        }
        foreach ($id in @('MemoryRefreshFacts', 'MemorySaveFact', 'MemoryEditFact', 'MemoryDeleteFact',
            'MemoryPurgeExpired', 'MemoryCreateExportPreview', 'MemoryExport')) {
            if ((Find-Control $id).Current.IsEnabled) { throw 'Disabled memory must not permit fact actions or export.' }
        }
        Invoke-Control 'MemoryClose'
        $null = Wait-Status '*Setup checkpoint: Destinations*consent missing or invalidated*'
        if ((Test-Path -LiteralPath (Join-Path $data 'memory')) -or
            [Convert]::ToHexString([IO.File]::ReadAllBytes($settings)) -cne [Convert]::ToHexString($beforeMemory)) {
            throw 'Passive memory management opened a store or modified settings.'
        }
    }
    Close-Desktop
    $configured = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    if ($configured.schema_version -ne 5 -or $configured.profile.id -ne $legacyId -or
        $configured.setup.schema_version -ne 2 -or
        $configured.setup.routes[0].route_type -cne 'open_ai' -or
        $configured.setup.routes[0].route_schema_version -ne 1 -or
        $configured.setup.routes[0].enabled -ne $true -or
        $configured.companion.personas.Count -ne 1 -or
        $configured.companion.active_persona_id -ne $configured.companion.personas[0].id -or
        $configured.setup.routes[0].model_id -cne 'whisper-1' -or $null -ne $configured.setup.routes[0].consent -or
        $null -ne $configured.setup.routes[0].credential_id -or $configured.setup.pending_removals.Count -ne 0 -or
        $configured.memory.schema_version -ne 1 -or $configured.memory.enabled -ne $false -or
        $configured.memory.storage_policy -cne 'app_local_data' -or $null -ne $configured.memory.custom_directory) {
        throw 'Actual no-key setup/companion save did not preserve identity, persona, model edit, consent invalidation and memory OFF.'
    }
    if ($CompanionOnly) {
        if ($configured.companion.personas[0].name -cne 'Smoke persona' -or
            $configured.companion.personas[0].text -cne 'Offline smoke persona; no provider action.') {
            throw 'Companion editing did not persist the exact selected persona.'
        }
        Start-Desktop
        $null = Wait-Status '*Setup checkpoint: Destinations*consent missing or invalidated*'
        Invoke-Control 'OpenCompanion'
        $null = Wait-Setup '*All changes saved*' 'CompanionSaveState'
        if ((Read-Value (Find-Control 'CompanionName')) -cne 'Smoke persona' -or
            (Read-Value (Find-Control 'CompanionText')) -cne 'Offline smoke persona; no provider action.') {
            throw 'Restart did not load the saved persona through the actual editor.'
        }
        Invoke-Control 'CompanionClose'
        $null = Wait-Status '*Setup checkpoint: Destinations*consent missing or invalidated*'
        Close-Desktop
        Write-Output 'PASS: independent native companion editor/save/restart; passive memory management and all memory permissions OFF; exact persona/profile/route/consent preservation; v1 migration original preserved; no keys/network/audio.'
        return
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
    Select-Theme 'Rose dark'
    if ([IO.File]::ReadAllText((Join-Path $data 'appearance.txt')) -cne 'Dark' -or [IO.File]::Exists($settings)) {
        throw 'Theme selection must persist separately without creating a profile.'
    }
    Close-Desktop
    Start-Desktop
    $null = Wait-Status '*First run:*'
    $selection = (Find-Control 'AppearanceTheme').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection.Count -ne 1 -or $selection[0].Current.Name -ne 'Rose dark') { throw 'Saved dark theme was not restored.' }
    Select-Theme 'Pink light'
    Close-Desktop
    $appearance = Join-Path $data 'appearance.txt'
    [IO.File]::WriteAllText($appearance, 'unrecognized-theme')
    Start-Desktop
    $null = Wait-Status '*First run:*'
    if ((Find-Control 'AppearanceStatus').Current.Name -notlike 'Could not read appearance.txt*' -or
        [IO.File]::ReadAllText($appearance) -cne 'unrecognized-theme') {
        throw 'Malformed appearance must show a recovery notice without rewriting the preference on launch.'
    }
    $selection = (Find-Control 'AppearanceTheme').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection.Count -ne 1 -or $selection[0].Current.Name -ne 'Pink light') { throw 'Malformed appearance must fall back to pink light.' }
    Select-Theme 'Rose dark'
    Close-Desktop
    Start-Desktop 'relative'
    $value = Wait-Status '*Cannot open the data directory*'
    if ((Find-Control 'CreateProfile').Current.IsEnabled -or (Find-Control 'RefreshDiagnostics').Current.IsEnabled) {
        throw 'Invalid launch arguments must leave a read-only, actionable startup error.'
    }
    Close-Desktop
    Write-Output 'PASS: passive Troubleshooting from main/setup, recording and export OFF; accessible no-key setup/migration/save/resume/Back/model consent invalidation; profile/remedies/preservation; pink/dark selection and restart persistence without profile creation; bounded close. No OS credential actions or network.'
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
    $appearance = Join-Path $data 'appearance.txt'
    if ([IO.File]::Exists($appearance)) { [IO.File]::Delete($appearance) }
    if ([System.IO.Directory]::Exists($data)) { [System.IO.Directory]::Delete($data) }
}
