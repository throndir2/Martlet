<#
.SYNOPSIS
Runs a sequence of Martlet MCP tool calls against this checkout's build.

.DESCRIPTION
Starts src\Martlet.Mcp from this checkout, sends initialize and each call in
order, and prints one JSON array of results. Every call whose tool takes a
dataDirectory (its input schema in tools/list has that property) and gives none
gets a disposable one, so no tool reads or writes the real profile
(%LOCALAPPDATA%\Martlet) by default; martlet_call gives it to the tool it runs.
-AllowChanges lets the tools save or write there (character and settings
tools, memory_forget, and the save, forget and output files of the diagnostic
tools). Every call whose tool takes a martletDirectory (the voice and listening
checks) also uses this checkout's Desktop build when it is built. -Desktop
launches Martlet.Desktop with the same disposable data directory (plus any
-DesktopArguments, such as --tray) and connects ui_* tools to it first.

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
    # Lets the tools save or write (--allow-changes); they change the disposable data directory unless one is given.
    [switch]$AllowChanges,
    [switch]$KeepDesktop,
    # Keep pairing secrets in a lab-credentials folder inside the data directory instead of Windows Credential Manager, for the
    # desktop and MCP processes this starts (MARTLET_LAB_CREDENTIALS); needed by signin_lab and role_lab. It also gives both a
    # lab-browser folder (MARTLET_LAB_BROWSER) where the desktop hands a browser sign-in page to signin_lab's simulated browser,
    # and keeps their finds of hosts on the home network on loopback (MARTLET_HOST_FIND_LOOPBACK), so a lab never reaches real hosts.
    [switch]$LabCredentials,
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
    . (Join-Path $PSScriptRoot 'MartletDev.ps1')
    $dotnet = Find-MartletDotnet $root (Get-MartletDevProfile)
    $projects = @('src\Martlet.Mcp\Martlet.Mcp.csproj')
    if ($Desktop) { $projects += 'src\Martlet.Desktop\Martlet.Desktop.csproj' }
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    foreach ($project in $projects) {
        $arguments = @('build', (Join-Path $root $project), '-c', $Configuration, '--nologo', '-v', 'q')
        if ($node) { $arguments += "-p:NodeExecutable=$node" }
        & $dotnet @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}

$server = Join-Path $root "src\Martlet.Mcp\$bin\Martlet.Mcp.exe"
if (-not (Test-Path -LiteralPath $server -PathType Leaf)) { throw "Martlet.Mcp is not built at $server; rerun with -Build." }

$ownsData = -not $DataDirectory
$data = if ($DataDirectory) { $DataDirectory } else {
    Join-Path ([System.IO.Path]::GetTempPath()) ('Martlet.Mcp.Verify.' + [guid]::NewGuid().ToString('N'))
}
$labFolder = $null
$labBrowser = $null
if ($LabCredentials) {
    $labFolder = Join-Path $data 'lab-credentials'
    $null = New-Item -ItemType Directory -Force -Path $labFolder
    $labBrowser = Join-Path $data 'lab-browser'
    $null = New-Item -ItemType Directory -Force -Path $labBrowser
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
        if ($labFolder) { $start.Environment['MARTLET_LAB_CREDENTIALS'] = $labFolder; $start.Environment['MARTLET_LAB_BROWSER'] = $labBrowser; $start.Environment['MARTLET_HOST_FIND_LOOPBACK'] = '1' }
        $desktopProcess = [System.Diagnostics.Process]::Start($start)
        $DesktopProcessId = $desktopProcess.Id
    }

    $start = [System.Diagnostics.ProcessStartInfo]::new($server)
    # --all-tools: tools/list gives every tool's input schema, which says which tools take a dataDirectory or martletDirectory.
    $start.ArgumentList.Add('--all-tools')
    if ($AllowUiEffects) { $start.ArgumentList.Add('--allow-ui-effects') }
    if ($AllowChanges) { $start.ArgumentList.Add('--allow-changes') }
    $start.UseShellExecute = $false
    if ($labFolder) { $start.Environment['MARTLET_LAB_CREDENTIALS'] = $labFolder; $start.Environment['MARTLET_LAB_BROWSER'] = $labBrowser; $start.Environment['MARTLET_HOST_FIND_LOOPBACK'] = '1' }
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

    # Each tool's input properties, from the server itself, so a new tool with a dataDirectory is covered without a list here.
    $properties = @{}
    foreach ($tool in (Send-Request 'tools/list' $null).tools) {
        $properties[[string]$tool.name] = @($tool.inputSchema.properties.PSObject.Properties.Name)
    }
    $desktopBuild = Join-Path $root "src\Martlet.Desktop\$bin"
    $desktopBuilt = Test-Path -LiteralPath (Join-Path $desktopBuild 'Martlet.Desktop.exe') -PathType Leaf

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
        if ($null -eq $arguments) { $arguments = [pscustomobject]@{} }
        # martlet_call runs another tool: that tool gets the disposable data directory in its own arguments.
        $toolName = $call.name
        $target = $arguments
        if ($call.name -eq 'martlet_call' -and $arguments.tool) {
            $toolName = [string]$arguments.tool
            if ($null -eq $arguments.PSObject.Properties['arguments'] -or $null -eq $arguments.arguments) {
                $arguments | Add-Member -Force -NotePropertyName arguments -NotePropertyValue ([pscustomobject]@{})
            }
            $target = $arguments.arguments
        }
        $takes = if ($properties.ContainsKey($toolName)) { $properties[$toolName] } else { @() }
        if ($null -ne $target -and $takes -contains 'dataDirectory' -and $null -eq $target.PSObject.Properties['dataDirectory']) {
            $target | Add-Member -NotePropertyName dataDirectory -NotePropertyValue $data
        }
        # The voice and listening checks use this checkout's Desktop build (its sherpa-onnx runtime, libdave.dll and Smart Turn
        # model) when it is built.
        if ($null -ne $target -and $desktopBuilt -and $takes -contains 'martletDirectory' -and $null -eq $target.PSObject.Properties['martletDirectory']) {
            $target | Add-Member -NotePropertyName martletDirectory -NotePropertyValue $desktopBuild
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
            elseif ($call.name -in 'ui_click', 'ui_select', 'ui_set_text', 'ui_toggle', 'ui_move', 'ui_scroll') { $WaitMs } else { 0 }
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
