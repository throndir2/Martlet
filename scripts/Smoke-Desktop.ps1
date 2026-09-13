[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    throw 'The desktop smoke requires Windows and an interactive desktop session.'
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$assembly = Join-Path $root "src\Martlet.Desktop\bin\$Configuration\net10.0-windows\Martlet.Desktop.dll"
if (-not (Test-Path -LiteralPath $assembly)) {
    throw 'Build Martlet.slnx first.'
}
$data = Join-Path ([System.IO.Path]::GetTempPath()) ("Martlet.Desktop.Smoke." + [guid]::NewGuid().ToString('N'))
$process = $null
try {
    $process = Start-Process (Get-Command dotnet).Source -ArgumentList "`"$assembly`" --data-directory `"$data`"" -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $status = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "Desktop exited before showing status (exit $($process.ExitCode))."
        }
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'FoundationStatus')
            $status = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $status) {
                $value = $status.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
                if ($value -like '*First run:*' -and $value -like '*not ready*') {
                    break
                }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $status -or $value -notlike '*First run:*' -or $value -notlike '*not ready*') {
        throw 'Accessible first-run status was not available within 20 seconds.'
    }
    if (Test-Path -LiteralPath $data) {
        throw 'Read-only launch unexpectedly created a data directory.'
    }
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)) {
        throw 'Closing the main window did not exit Martlet within 10 seconds.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Desktop exited with code $($process.ExitCode)."
    }
    Write-Output 'PASS: accessible offline first-run status; no data writes; window close exits.'
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id
        }
        $process.Dispose()
    }
}
