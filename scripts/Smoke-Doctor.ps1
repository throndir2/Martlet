[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assembly = Join-Path $root "src\Martlet.Doctor\bin\$Configuration\net10.0\Martlet.Doctor.dll"
$data = Join-Path ([System.IO.Path]::GetTempPath()) ("Martlet.Doctor.Smoke." + [guid]::NewGuid().ToString('N'))
$output = & dotnet $assembly status --json --data-directory $data
if ($LASTEXITCODE -ne 2) {
    throw "Expected incomplete exit 2, got $LASTEXITCODE."
}
$report = ($output -join "`n") | ConvertFrom-Json
if ($report.version.major -ne 1 -or $report.exit_code -ne 2 -or $report.ready -ne $false -or
    $report.settings_state -ne 'first_run' -or $report.probes.Count -ne 3) {
    throw 'Unexpected foundation JSON report shape or readiness.'
}
if (Test-Path -LiteralPath $data) {
    throw 'Read-only doctor unexpectedly created a data directory.'
}
[System.IO.Directory]::CreateDirectory($data) | Out-Null
$settingsFile = Join-Path $data 'settings.json'
try {
    foreach ($case in @('surrogate', 'utf8')) {
        [byte[]]$bytes = if ($case -eq 'surrogate') {
            [System.Text.Encoding]::UTF8.GetBytes('{"\uD800":0}')
        }
        else {
            0x7B, 0x22, 0xED, 0xA0, 0x80, 0x22, 0x3A, 0x30, 0x7D
        }
        [System.IO.File]::WriteAllBytes($settingsFile, $bytes)
        $output = & dotnet $assembly status --json --data-directory $data
        if ($LASTEXITCODE -ne 3) {
            throw "Malformed $case settings must produce exit 3."
        }
        $invalidSettings = ($output -join "`n") | ConvertFrom-Json
        if ($invalidSettings.settings_state -ne 'invalid' -or $invalidSettings.exit_code -ne 3 -or
            $invalidSettings.probes[0].error.code -ne 'settings_malformed') {
            throw "Malformed $case settings must produce the sanitized JSON error envelope."
        }
        if ([Convert]::ToHexString([System.IO.File]::ReadAllBytes($settingsFile)) -cne [Convert]::ToHexString($bytes)) {
            throw "Doctor changed the malformed $case settings file."
        }
    }
}
finally {
    [System.IO.File]::Delete($settingsFile)
    [System.IO.Directory]::Delete($data)
}
$output = & dotnet $assembly --json self-test
if ($LASTEXITCODE -ne 3) {
    throw 'An unimplemented command must return invalid invocation, not success.'
}
$invalid = ($output -join "`n") | ConvertFrom-Json
if ($invalid.exit_code -ne 3 -or $null -eq $invalid.invocation_error) {
    throw 'Invalid invocation must use the versioned error envelope.'
}
Write-Output 'PASS: actual doctor JSON and exit codes; malformed encodings sanitized; input bytes preserved.'
exit 0
