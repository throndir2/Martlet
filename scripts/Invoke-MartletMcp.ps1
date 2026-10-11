<#
.SYNOPSIS
Runs a sequence of Martlet MCP tool calls against this checkout's build.

.DESCRIPTION
Starts src\Martlet.Mcp from this checkout, sends initialize and each call in
order, and prints one JSON array of results. Doctor, voices_status, voices_naming_check, f5_voices, cluster_status, network_status, accounts_status, account_security_status, nearby_status, logs_tail,
logs_timeline, logs_export, virtualization_status, mcp_servers_status, api_keys_status, smart_home_status, messaging_status, discord_status, discord_check, terminal_status, terminal_check, prompts_status, settings_sync_status, memory_sync_status, memory_status, character_status, hearing_check, model_ability_check, local_model_facts,
echo_check, pc_audio_check, discord_call_check, context_check, context_board, thinking_steps_check, think_longer_status, thinking_pool_status, thinking_requests, sense_models_status, audio_model_check, image_model_check, live_floor_status, helper_jobs_status, reminders_status, check_ins_status, setup_run_status, discord_reply_status, discord_reply_check, conversation_history_status, app_guides_status, creations_status, chattiness_status, discord_text_check, discord_companion_check, vision_history_check, active_app_check, screen_digest_check, songs_status, pictures_status, latency_report, thinking_trace, character_models, character_profiles, household_sharing,
character_actions, character_gaze, character_touch_zones, character_reaction_changes, character_eyes, character_physical_check, character_theme, singing_status, sound_digest_check, utterance_filter_check, barge_in_check, parakeet_check, gpu_priority_status, quick_sounds_status, voice_sounds_status, node_presence_status, situation_status, recommended_setup_status, lip_sync_pool_status, pc_scope, characters_list, character_create, character_update, character_use, character_delete, settings_get, settings_set and model_catalog_* calls without an explicit dataDirectory get a disposable one
(-AllowChanges lets the character and settings tools save there; martlet_call gives it to the tool it runs);
voices_status, voices_engine_check, utterance_filter_check, parakeet_check, sound_digest_check, straight_voice_check, discord_voice_check and turn_judge_check also use this checkout's Desktop
build (martletDirectory) when it is built. -Desktop launches Martlet.Desktop with the
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
    # Lets the character and settings tools save (--allow-changes); they change the disposable data directory unless one is given.
    [switch]$AllowChanges,
    [switch]$KeepDesktop,
    # Keep pairing secrets in a lab-credentials folder inside the data directory instead of Windows Credential Manager, for the
    # desktop and MCP processes this starts (MARTLET_LAB_CREDENTIALS); needed by signin_lab and role_lab. It also gives both a
    # lab-browser folder (MARTLET_LAB_BROWSER) where the desktop hands a browser sign-in page to signin_lab's simulated browser.
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
        if ($labFolder) { $start.Environment['MARTLET_LAB_CREDENTIALS'] = $labFolder; $start.Environment['MARTLET_LAB_BROWSER'] = $labBrowser }
        $desktopProcess = [System.Diagnostics.Process]::Start($start)
        $DesktopProcessId = $desktopProcess.Id
    }

    $start = [System.Diagnostics.ProcessStartInfo]::new($server)
    if ($AllowUiEffects) { $start.ArgumentList.Add('--allow-ui-effects') }
    if ($AllowChanges) { $start.ArgumentList.Add('--allow-changes') }
    $start.UseShellExecute = $false
    if ($labFolder) { $start.Environment['MARTLET_LAB_CREDENTIALS'] = $labFolder; $start.Environment['MARTLET_LAB_BROWSER'] = $labBrowser }
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
        if ($toolName -like 'doctor_*' -or $toolName -like 'voices_*' -or $toolName -like 'logs_*' -or $toolName -like 'f5_*' -or
            $toolName -like 'cluster_*' -or $toolName -like 'nearby_*' -or $toolName -like 'virtualization_*' -or $toolName -eq 'mcp_servers_status' -or
            $toolName -eq 'network_status' -or $toolName -eq 'accounts_status' -or $toolName -eq 'account_security_status' -or $toolName -eq 'signin_lab' -or $toolName -eq 'role_lab' -or $toolName -eq 'api_keys_status' -or $toolName -eq 'smart_home_status' -or $toolName -eq 'messaging_status' -or $toolName -eq 'discord_status' -or $toolName -eq 'discord_check' -or $toolName -eq 'prompts_status' -or
            $toolName -eq 'settings_sync_status' -or $toolName -eq 'memory_sync_status' -or $toolName -eq 'memory_status' -or $toolName -eq 'hearing_check' -or $toolName -eq 'model_ability_check' -or $toolName -eq 'local_model_facts' -or $toolName -eq 'echo_check' -or $toolName -eq 'pc_audio_check' -or $toolName -eq 'discord_call_check' -or $toolName -eq 'character_status' -or $toolName -eq 'character_models' -or $toolName -eq 'character_profiles' -or $toolName -eq 'household_sharing' -or
            $toolName -eq 'character_actions' -or $toolName -eq 'character_gaze' -or $toolName -eq 'character_touch_zones' -or $toolName -eq 'character_reaction_changes' -or $toolName -eq 'character_eyes' -or $toolName -eq 'character_physical_check' -or $toolName -eq 'character_theme' -or $toolName -eq 'context_check' -or $toolName -eq 'context_board' -or $toolName -eq 'thinking_steps_check' -or
            $toolName -eq 'latency_report' -or $toolName -eq 'thinking_trace' -or $toolName -like 'terminal_*' -or $toolName -eq 'utterance_filter_check' -or $toolName -eq 'barge_in_check' -or $toolName -eq 'parakeet_check' -or
            $toolName -eq 'think_longer_status' -or $toolName -eq 'thinking_pool_status' -or $toolName -eq 'thinking_requests' -or $toolName -eq 'sense_models_status' -or $toolName -eq 'audio_model_check' -or $toolName -eq 'image_model_check' -or $toolName -eq 'live_floor_status' -or $toolName -eq 'helper_jobs_status' -or $toolName -eq 'work_sharing_status' -or $toolName -eq 'reminders_status' -or $toolName -eq 'check_ins_status' -or $toolName -eq 'setup_run_status' -or $toolName -eq 'discord_reply_status' -or $toolName -eq 'discord_reply_check' -or $toolName -eq 'conversation_history_status' -or $toolName -eq 'app_guides_status' -or $toolName -eq 'creations_status' -or $toolName -eq 'chattiness_status' -or $toolName -eq 'discord_text_check' -or $toolName -eq 'discord_companion_check' -or $toolName -eq 'vision_history_check' -or $toolName -eq 'active_app_check' -or $toolName -eq 'screen_digest_check' -or $toolName -eq 'songs_status' -or $toolName -eq 'pictures_status' -or
            $toolName -eq 'singing_status' -or $toolName -eq 'sound_digest_check' -or $toolName -eq 'gpu_priority_status' -or $toolName -eq 'quick_sounds_status' -or $toolName -eq 'voice_sounds_status' -or $toolName -eq 'node_presence_status' -or $toolName -eq 'situation_status' -or $toolName -eq 'recommended_setup_status' -or
            $toolName -eq 'lip_sync_pool_status' -or $toolName -eq 'pc_scope' -or $toolName -like 'model_catalog_*' -or
            $toolName -in 'characters_list', 'character_create', 'character_update', 'character_use', 'character_delete', 'settings_get', 'settings_set') {
            if ($null -eq $target.PSObject.Properties['dataDirectory']) {
                $target | Add-Member -NotePropertyName dataDirectory -NotePropertyValue $data
            }
            # voices_status, voices_engine_check, utterance_filter_check and parakeet_check use this checkout's Desktop build (its bundled
            # sherpa-onnx runtime), when built.
            $desktopBuild = Join-Path $root "src\Martlet.Desktop\$bin"
            if (($toolName -like 'voices_*' -or $toolName -eq 'utterance_filter_check' -or $toolName -eq 'parakeet_check' -or $toolName -eq 'sound_digest_check') -and $null -eq $target.PSObject.Properties['martletDirectory'] -and
                (Test-Path -LiteralPath (Join-Path $desktopBuild 'Martlet.Desktop.exe') -PathType Leaf)) {
                $target | Add-Member -NotePropertyName martletDirectory -NotePropertyValue $desktopBuild
            }
        }
        # straight_voice_check, discord_voice_check and turn_judge_check take no data directory; they use this checkout's Desktop
        # build (its sherpa-onnx runtime, its libdave.dll, its Smart Turn model) too.
        if ($call.name -eq 'straight_voice_check' -or $call.name -eq 'discord_voice_check' -or $call.name -eq 'turn_judge_check') {
            if ($null -eq $arguments) { $arguments = [pscustomobject]@{} }
            $desktopBuild = Join-Path $root "src\Martlet.Desktop\$bin"
            if ($null -eq $arguments.PSObject.Properties['martletDirectory'] -and
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
