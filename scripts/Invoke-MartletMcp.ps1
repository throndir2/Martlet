<#
.SYNOPSIS
Runs a sequence of Martlet MCP tool calls against this checkout's build.

.DESCRIPTION
Starts src\Martlet.Mcp from this checkout, sends initialize and each call in
order, and prints one JSON array of results. Doctor, voices_status, f5_voices, cluster_status, network_status, nearby_status, logs_tail,
logs_timeline, virtualization_status, mcp_servers_status, api_keys_status, smart_home_status, prompts_status, settings_sync_status, character_status, hearing_check,
echo_check, pc_audio_check, context_check, thinking_steps_check, latency_report, character_models and character_actions calls
without an explicit dataDirectory get a disposable one; voices_status and voices_engine_check also use this checkout's
Desktop build (martletDirectory) when it is built. -Desktop launches Martlet.Desktop with the
same disposable data directory (plus any -DesktopArguments, such as --tray) and connects ui_* tools to it first.

Each call is {"name": "<tool>", "arguments": {...}} with optional "waitMs"
(pause after the call) and "until" (repeat the call for up to 20 seconds until
its result text contains this string, e.g. polling ui_snapshot).

.EXAMPLE
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"doctor_status"}]'

.EXAMPLE
.\scripts\Invoke-MartletMcp.ps1 -Desktop -Calls '[{"name":"ui_click","arguments":{"id":"TourSkip"}},{"name":"ui_click","arguments":{"id":"NavSettings"}},{"name":"ui_snapshot","until":"AutomaticUpdateCheck"}]'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Calls,
    [switch]$Desktop,
    [int]$DesktopProcessId,
    [string]$DataDirectory,
    [string[]]$DesktopArguments = @(),
    [switch]$AllowUiEffects,
    [switch]$KeepDesktop,
    [switch]$Build,
    [string]$Configuration = 'Release',
    [int]$WaitMs = 300,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Martlet MCP verification requires Windows.' }
if ($Desktop -and $DesktopProcessId) { throw 'Use either -Desktop or -DesktopProcessId, not both.' }
$root = Split-Path $PSScriptRoot -Parent
$bin = "bin\$Configuration\net10.0-windows"

$callText = if (Test-Path -LiteralPath $Calls -PathType Leaf) { Get-Content -LiteralPath $Calls -Raw } else { $Calls }
$requested = @(ConvertFrom-Json -InputObject $callText -NoEnumerate) | ForEach-Object { $_ }
foreach ($call in $requested) {
    if (-not $call.name) { throw 'Every call needs a "name".' }
}

if ($Build) {
    $projects = @('src\Martlet.Mcp\Martlet.Mcp.csproj')
    if ($Desktop) { $projects += 'src\Martlet.Desktop\Martlet.Desktop.csproj' }
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    foreach ($project in $projects) {
        $arguments = @('build', (Join-Path $root $project), '-c', $Configuration, '--nologo', '-v', 'q')
        if ($node) { $arguments += "-p:NodeExecutable=$node" }
        & dotnet @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}

$server = Join-Path $root "src\Martlet.Mcp\$bin\Martlet.Mcp.exe"
if (-not (Test-Path -LiteralPath $server -PathType Leaf)) { throw "Martlet.Mcp is not built at $server; rerun with -Build." }

$ownsData = -not $DataDirectory
$data = if ($DataDirectory) { $DataDirectory } else {
    Join-Path ([System.IO.Path]::GetTempPath()) ('Martlet.Mcp.Verify.' + [guid]::NewGuid().ToString('N'))
}
$desktopProcess = $null
$mcp = $null
$nextId = 0
$results = [System.Collections.Generic.List[object]]::new()
$failed = $false

function Send-Request([string]$Method, $Params) {
    $script:nextId++
    $request = [ordered]@{ jsonrpc = '2.0'; id = $script:nextId; method = $Method }
    if ($null -ne $Params) { $request.params = $Params }
    $script:mcp.StandardInput.WriteLine((ConvertTo-Json $request -Compress -Depth 30))
    $script:mcp.StandardInput.Flush()
    $read = $script:mcp.StandardOutput.ReadLineAsync()
    if (-not $read.Wait($TimeoutSeconds * 1000)) { throw "No MCP response to $Method within $TimeoutSeconds seconds." }
    if ($null -eq $read.Result) { throw "Martlet.Mcp exited during $Method." }
    $response = ConvertFrom-Json $read.Result
    if ($response.error) { throw "MCP $Method failed: $($response.error.message)" }
    return $response.result
}

function Invoke-Tool([string]$Name, $Arguments) {
    $params = [ordered]@{ name = $Name }
    if ($null -ne $Arguments) { $params.arguments = $Arguments }
    $result = Send-Request 'tools/call' $params
    $text = [string]$result.content[0].text
    $value = try { ConvertFrom-Json $text -Depth 64 } catch { $text }
    return [pscustomobject]@{ name = $Name; arguments = $Arguments; isError = [bool]$result.isError; text = $text; result = $value }
}

try {
    if ($Desktop) {
        $executable = Join-Path $root "src\Martlet.Desktop\$bin\Martlet.Desktop.exe"
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Martlet.Desktop is not built at $executable; rerun with -Build." }
        $start = [System.Diagnostics.ProcessStartInfo]::new($executable)
        $start.ArgumentList.Add('--data-directory')
        $start.ArgumentList.Add($data)
        foreach ($argument in $DesktopArguments) { $start.ArgumentList.Add($argument) }
        $start.UseShellExecute = $false
        $desktopProcess = [System.Diagnostics.Process]::Start($start)
        $DesktopProcessId = $desktopProcess.Id
    }

    $start = [System.Diagnostics.ProcessStartInfo]::new($server)
    if ($AllowUiEffects) { $start.ArgumentList.Add('--allow-ui-effects') }
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $mcp = [System.Diagnostics.Process]::Start($start)
    $mcp.StandardInput.AutoFlush = $true
    $diagnostics = $mcp.StandardError.ReadToEndAsync()

    $null = Send-Request 'initialize' ([ordered]@{
        protocolVersion = '2025-06-18'; capabilities = @{}
        clientInfo = [ordered]@{ name = 'Invoke-MartletMcp'; version = '1.0' }
    })
    $mcp.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')

    if ($DesktopProcessId) {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $connection = Invoke-Tool 'ui_connect' ([ordered]@{ pid = $DesktopProcessId })
            if (-not $connection.isError) { break }
            if ($desktopProcess -and $desktopProcess.HasExited) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        $results.Add($connection)
        if ($connection.isError) { throw "ui_connect failed: $($connection.text)" }
    }

    foreach ($call in $requested) {
        $arguments = $call.arguments
        if ($call.name -like 'doctor_*' -or $call.name -like 'voices_*' -or $call.name -like 'logs_*' -or $call.name -like 'f5_*' -or
            $call.name -like 'cluster_*' -or $call.name -like 'nearby_*' -or $call.name -like 'virtualization_*' -or $call.name -eq 'mcp_servers_status' -or
            $call.name -eq 'network_status' -or $call.name -eq 'api_keys_status' -or $call.name -eq 'smart_home_status' -or $call.name -eq 'prompts_status' -or
            $call.name -eq 'settings_sync_status' -or $call.name -eq 'hearing_check' -or $call.name -eq 'echo_check' -or $call.name -eq 'pc_audio_check' -or $call.name -eq 'character_status' -or $call.name -eq 'character_models' -or
            $call.name -eq 'character_actions' -or $call.name -eq 'context_check' -or $call.name -eq 'thinking_steps_check' -or
            $call.name -eq 'latency_report') {
            if ($null -eq $arguments) { $arguments = [pscustomobject]@{} }
            if ($null -eq $arguments.PSObject.Properties['dataDirectory']) {
                $arguments | Add-Member -NotePropertyName dataDirectory -NotePropertyValue $data
            }
            # voices_status and voices_engine_check use this checkout's Desktop build (its bundled voice recognition), when built.
            $desktopBuild = Join-Path $root "src\Martlet.Desktop\$bin"
            if ($call.name -like 'voices_*' -and $null -eq $arguments.PSObject.Properties['martletDirectory'] -and
                (Test-Path -LiteralPath (Join-Path $desktopBuild 'Martlet.Desktop.exe') -PathType Leaf)) {
                $arguments | Add-Member -NotePropertyName martletDirectory -NotePropertyValue $desktopBuild
            }
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $outcome = Invoke-Tool $call.name $arguments
            $met = -not $call.until -or $outcome.text.Contains([string]$call.until, [StringComparison]::Ordinal)
            if ($met -or $outcome.isError) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($call.until) { $outcome | Add-Member -NotePropertyName untilMet -NotePropertyValue $met }
        $results.Add($outcome)
        if ($outcome.isError -or -not $met) { $failed = $true }
        $pause = if ($null -ne $call.waitMs) { [int]$call.waitMs }
            elseif ($call.name -in 'ui_click', 'ui_select', 'ui_set_text', 'ui_toggle', 'ui_move') { $WaitMs } else { 0 }
        if ($pause -gt 0) { Start-Sleep -Milliseconds $pause }
    }
}
catch {
    $failed = $true
    $results.Add([pscustomobject]@{ name = 'script'; isError = $true; result = $_.Exception.Message })
}
finally {
    if ($mcp) {
        $mcp.StandardInput.Close()
        if (-not $mcp.WaitForExit(5000)) { $mcp.Kill() }
        $stderr = $diagnostics.Result
        if ($stderr) { Write-Warning "Martlet.Mcp stderr:`n$stderr" }
        $mcp.Dispose()
    }
    if ($desktopProcess) {
        if ($KeepDesktop -and -not $desktopProcess.HasExited) {
            Write-Host "Desktop kept running: -DesktopProcessId $($desktopProcess.Id) -DataDirectory '$data'"
        }
        else {
            if (-not $desktopProcess.HasExited) { $desktopProcess.Kill() }
            $null = $desktopProcess.WaitForExit(10000)
        }
        $desktopProcess.Dispose()
    }
    if ($ownsData -and -not ($KeepDesktop -and $desktopProcess) -and (Test-Path -LiteralPath $data)) {
        Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$results | Select-Object name, arguments, isError, untilMet, result |
    ConvertTo-Json -Depth 64 -AsArray
if ($failed) { exit 1 }
