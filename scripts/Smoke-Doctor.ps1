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
$output = & dotnet $assembly --json self-test
if ($LASTEXITCODE -ne 3) {
    throw 'An unimplemented command must return invalid invocation, not success.'
}
$invalid = ($output -join "`n") | ConvertFrom-Json
if ($invalid.exit_code -ne 3 -or $null -eq $invalid.invocation_error) {
    throw 'Invalid invocation must use the versioned error envelope.'
}
Write-Output 'PASS: actual doctor JSON and incomplete/invalid exit codes; no data writes.'
exit 0
