[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The desktop smoke requires Windows and an interactive desktop session.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$assembly = Join-Path $root "src\Martlet.Desktop\bin\$Configuration\net10.0-windows\Martlet.Desktop.dll"
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build Martlet.slnx first.' }
$data = Join-Path ([System.IO.Path]::GetTempPath()) ("Martlet.Desktop.Smoke." + [guid]::NewGuid().ToString('N'))
$settings = Join-Path $data 'settings.json'
$process = $null
$window = $null

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
function Start-Desktop {
    $script:process = Start-Process (Get-Command dotnet).Source -ArgumentList "`"$assembly`" --data-directory `"$data`"" -PassThru
}
function Close-Desktop {
    if (-not $script:process.CloseMainWindow() -or -not $script:process.WaitForExit(10000)) {
        throw 'Closing the diagnostic window did not exit Martlet within 10 seconds.'
    }
    if ($script:process.ExitCode -ne 0) { throw 'Desktop exited with an error.' }
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
    $stop = Find-Control 'StopDiagnostics'
    if ($null -eq $stop -or $stop.Current.IsEnabled -or $stop.Current.Name -ne 'Stop diagnostics') {
        throw 'Stop must be exposed and disabled while idle.'
    }
    Invoke-Control 'RefreshDiagnostics'
    $refreshed = Wait-Status '*First run:*' $first
    if ($refreshed -notlike '*settings.first_run*') { throw 'Refresh did not use the shared diagnostic catalog.' }
    Invoke-Control 'CreateProfile'
    $saved = Wait-Status '*Profile: NotConfigured;*'
    if ($saved -notlike '*settings.valid*' -or -not [System.IO.File]::Exists($settings)) { throw 'Explicit profile creation failed.' }
    if ((Find-Control 'CreateProfile').Current.IsEnabled) { throw 'Create must be disabled for an existing profile.' }
    Close-Desktop

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
    Write-Output 'PASS: accessible pipeline/keyboard focus; refresh/create; corrupt/newer/inaccessible remedies; bounded close; no startup writes.'
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
    if ([System.IO.File]::Exists($settings)) { [System.IO.File]::Delete($settings) }
    if ([System.IO.Directory]::Exists($settings)) { [System.IO.Directory]::Delete($settings) }
    [System.IO.File]::Delete((Join-Path $data 'settings.json.lock'))
    if ([System.IO.Directory]::Exists($data)) { [System.IO.Directory]::Delete($data) }
}
