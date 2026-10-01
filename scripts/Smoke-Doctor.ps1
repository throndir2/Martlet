[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$candidate = if ($ExecutablePath) { $ExecutablePath } else {
    Join-Path $root "src\Martlet.Doctor\bin\$Configuration\net10.0\Martlet.Doctor.dll"
}
if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'Build Doctor first or provide an existing executable path.' }
$resolved = Resolve-Path -LiteralPath $candidate
if ($resolved.Provider.Name -ne 'FileSystem') { throw 'Executable path must be a filesystem file.' }
$binary = $resolved.ProviderPath
$extension = [System.IO.Path]::GetExtension($binary).ToLowerInvariant()
if ($extension -notin @('.dll', '.exe')) { throw 'Executable path must select a .dll or .exe.' }
$program = if ($extension -eq '.dll') { (Get-Command dotnet).Source } else { $binary }
[string[]]$prefix = if ($extension -eq '.dll') { @($binary) } else { @() }
$data = Join-Path ([System.IO.Path]::GetTempPath()) ("Martlet.Doctor.Smoke." + [guid]::NewGuid().ToString('N'))

function Invoke-Doctor([string[]]$Command, [int]$ExpectedExit) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($program)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $prefix + $Command + @('--json', '--data-directory', $data)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            throw 'Doctor did not exit within its bounded smoke deadline.'
        }
        $text = $stdout.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedExit -or $stderr.GetAwaiter().GetResult().Length -ne 0) {
            throw "Unexpected Doctor exit/output; expected $ExpectedExit, got $($process.ExitCode)."
        }
        if ($text.Contains($data) -or $text.Contains('PRIVATE-CANARY')) {
            throw 'Doctor leaked supplied path or private content.'
        }
        $report = $text | ConvertFrom-Json
        if ($report.exit_code -ne $ExpectedExit -or $report.version.major -ne 1) {
            throw 'Doctor did not emit the single versioned report.'
        }
        return $report
    }
    finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
}

$report = Invoke-Doctor @('status') 2
if ($report.ready -ne $false -or $report.settings_state -ne 'first_run' -or $report.probes.Count -ne 12 -or
    $report.probes[0].diagnostic_code -ne 'settings.first_run' -or $report.probes[0].execution -ne 'completed') {
    throw 'Unexpected production status shape or readiness.'
}
$catalog = Invoke-Doctor @('list') 2
if (@($catalog.probes | Where-Object { $_.provenance -ne 'not_run' -or $_.execution -ne 'not_run' }).Count -ne 0) {
    throw 'Catalog listing invented executed evidence.'
}
$selected = Invoke-Doctor @('run', 'application.version', 'runtime.version') 0
if ($selected.probes.Count -ne 2 -or $selected.ready -ne $true -or $null -ne $selected.settings_state) {
    throw 'Selected local checks implied unrequested settings or pipeline coverage.'
}
$unavailable = Invoke-Doctor @('run', 'audio.input', 'audio.playback', 'host.connection') 2
if (@($unavailable.probes | Where-Object { $_.provenance -ne 'not_run' }).Count -ne 0) {
    throw 'Unavailable checks claimed evidence.'
}
foreach ($command in @(
    @('self-test'), @('run', 'PRIVATE-CANARY'), @('run', 'settings.load', 'settings.load'), @('run'),
    @('status', '--play-tone')
)) {
    $invalid = Invoke-Doctor $command 3
    if ($null -eq $invalid.invocation_error -or $invalid.probes.Count -ne 0) {
        throw 'Invalid invocation did not return its bounded error envelope.'
    }
}
if (Test-Path -LiteralPath $data) {
    throw 'Read-only doctor unexpectedly created a data directory.'
}
[System.IO.Directory]::CreateDirectory($data) | Out-Null
$settingsFile = Join-Path $data 'settings.json'
try {
    foreach ($case in @('surrogate', 'utf8', 'newer')) {
        [byte[]]$bytes = switch ($case) {
            'surrogate' { [System.Text.Encoding]::UTF8.GetBytes('{"PRIVATE-CANARY\uD800":0}') }
            'utf8' { 0x7B, 0x22, 0xED, 0xA0, 0x80, 0x22, 0x3A, 0x30, 0x7D }
            'newer' { [System.Text.Encoding]::UTF8.GetBytes('{"schema_version":99}') }
        }
        [System.IO.File]::WriteAllBytes($settingsFile, $bytes)
        $invalidSettings = Invoke-Doctor @('status') 3
        $expected = if ($case -eq 'newer') { 'unsupported_version' } else { 'settings_malformed' }
        if ($invalidSettings.settings_state -ne 'invalid' -or $invalidSettings.probes[0].error.code -ne $expected -or
            $invalidSettings.probes[0].action_id -ne 'settings.restore') {
            throw 'Invalid settings did not produce the precise code and remedy.'
        }
        if ([Convert]::ToHexString([System.IO.File]::ReadAllBytes($settingsFile)) -cne [Convert]::ToHexString($bytes)) {
            throw 'Doctor changed original settings bytes.'
        }

    }
    [System.IO.File]::Delete($settingsFile)
    [System.IO.Directory]::CreateDirectory($settingsFile) | Out-Null
    try {
        $inaccessible = Invoke-Doctor @('status') 3
        if ($inaccessible.settings_state -ne 'inaccessible' -or $inaccessible.probes[0].action_id -ne 'settings.check_access') {
            throw 'Inaccessible settings must not be treated as first run.'
        }
    }
    finally { [System.IO.Directory]::Delete($settingsFile) }
}
finally {
    if ([System.IO.File]::Exists($settingsFile)) { [System.IO.File]::Delete($settingsFile) }
    [System.IO.Directory]::Delete($data)
}
Write-Output 'PASS: bounded Doctor subprocesses; registry; exact exits/remedies; audio OFF; original settings preserved.'
exit 0
