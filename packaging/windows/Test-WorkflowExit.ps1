#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$ComparePayloadRoot,
    [Parameter(Mandatory)][string]$BuilderDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$CliHome,
    [string]$DotnetPath = 'dotnet',
    [switch]$RunSteps
)
. "$PSScriptRoot\Packaging.Common.ps1"
Assert-MSBuildPath $WorkDirectory

if ($RunSteps) {
    # Mirror a GitHub Actions pwsh step, including its implicit native-exit wrapper.
    $ErrorActionPreference = 'Stop'
    & "$PSScriptRoot\Test-Packaging.ps1" -PayloadRoot $PayloadRoot -ComparePayloadRoot $ComparePayloadRoot -BuilderDirectory $BuilderDirectory -WorkDirectory "$WorkDirectory\assertions" -DotnetPath $DotnetPath -CliHome $CliHome
    & "$PSScriptRoot\Smoke-Package.ps1" -PayloadRoot $PayloadRoot
    if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }
    return
}

New-OutputDirectory $WorkDirectory
$shell = (Get-Command pwsh -CommandType Application).Source
$common = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-RunSteps',
    '-ComparePayloadRoot', $ComparePayloadRoot, '-BuilderDirectory', $BuilderDirectory,
    '-DotnetPath', $DotnetPath, '-CliHome', $CliHome)
$success = Invoke-BoundedProcess $shell ($common + @('-PayloadRoot', $PayloadRoot, '-WorkDirectory', "$WorkDirectory\passing")) 600
if ($success.Stdout) { $success.Stdout.TrimEnd() | Out-Host }
if ($success.ExitCode -ne 0) { throw "GitHub pwsh wrapper failed after expected negative cases: $($success.Stderr)" }

# Reaching the actual next step is essential; a PASS line alone is not success.
& "$PSScriptRoot\Build-Installer.ps1" -PayloadRoot $PayloadRoot -BuilderDirectory $BuilderDirectory -OutputDirectory "$WorkDirectory\package"

$broken = Join-Path $WorkDirectory 'broken-payload'
Copy-Item -LiteralPath $PayloadRoot -Destination $broken -Recurse
[IO.File]::Delete((Join-Path $broken 'Doctor\Martlet.Doctor.exe'))
$failure = Invoke-BoundedProcess $shell ($common + @('-PayloadRoot', $broken, '-WorkDirectory', "$WorkDirectory\failing")) 600
if ($failure.ExitCode -eq 0 -or $failure.Stderr -notlike '*Required file missing*') {
    throw "A deliberately broken production assertion did not fail the GitHub pwsh wrapper as expected: $($failure.Stderr)"
}
Write-Output 'PASS: actual GitHub pwsh wrapper exits successfully after verified expected failures, reaches smoke/compiler, and still fails for a broken assertion.'
