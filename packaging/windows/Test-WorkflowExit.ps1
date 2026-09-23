#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$ComparePayloadRoot,
    [Parameter(Mandatory)][string]$BuilderDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$CliHome,
    [string]$DotnetPath = 'dotnet',
    [string]$ProtectedDataDirectory,
    [switch]$InteractiveDesktop,
    [switch]$RunSteps
)
. "$PSScriptRoot\Packaging.Common.ps1"
. "$PSScriptRoot\ProtectedData.Common.ps1"
Assert-MSBuildPath $WorkDirectory
if ($PSBoundParameters.ContainsKey('ProtectedDataDirectory')) {
    $ProtectedDataDirectory = Resolve-ProtectedDataDirectory $ProtectedDataDirectory @(
        $PayloadRoot, $ComparePayloadRoot, $WorkDirectory, $CliHome)
}

if ($RunSteps) {
    # Mirror a GitHub Actions pwsh step, including its implicit native-exit wrapper.
    $ErrorActionPreference = 'Stop'
    & "$PSScriptRoot\Test-Packaging.ps1" -PayloadRoot $PayloadRoot -ComparePayloadRoot $ComparePayloadRoot -BuilderDirectory $BuilderDirectory -WorkDirectory "$WorkDirectory\assertions" -DotnetPath $DotnetPath -CliHome $CliHome
    $smokeOptions = @{}
    if ($PSBoundParameters.ContainsKey('ProtectedDataDirectory')) { $smokeOptions.ProtectedDataDirectory = $ProtectedDataDirectory }
    if ($InteractiveDesktop) { $smokeOptions.InteractiveDesktop = $true }
    & "$PSScriptRoot\Smoke-Package.ps1" -PayloadRoot $PayloadRoot @smokeOptions
    if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }
    return
}

New-OutputDirectory $WorkDirectory
$shell = (Get-Command pwsh -CommandType Application).Source
$common = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-RunSteps',
    '-ComparePayloadRoot', $ComparePayloadRoot, '-BuilderDirectory', $BuilderDirectory,
    '-DotnetPath', $DotnetPath, '-CliHome', $CliHome)
if ($PSBoundParameters.ContainsKey('ProtectedDataDirectory')) { $common += @('-ProtectedDataDirectory', $ProtectedDataDirectory) }
if ($InteractiveDesktop) { $common += '-InteractiveDesktop' }
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

$scenarioScript = Join-Path $WorkDirectory 'controlled-smoke.ps1'
[IO.File]::WriteAllText($scenarioScript, @'
param([string]$ExecutablePath, [switch]$CompanionOnly)
$case = Get-Content -LiteralPath $ExecutablePath -Raw | ConvertFrom-Json
$scenario = if ($CompanionOnly) { 'Companion' } else { 'General' }
if ($case.scenario -eq $scenario) {
    if ($case.outcome -eq 'Timeout') { Start-Sleep -Seconds 300 }
    exit 7
}
'@)
foreach ($scenario in @('General', 'Companion')) {
    foreach ($outcome in @('Failure', 'Timeout')) {
        $casePath = Join-Path $WorkDirectory "$scenario-$outcome.json"
        [IO.File]::WriteAllText($casePath, (@{ scenario = $scenario; outcome = $outcome } | ConvertTo-Json))
        $rejected = $false
        try {
            Invoke-ExecutableSmoke $scenarioScript $casePath
            Invoke-ExecutableSmoke $scenarioScript $casePath -CompanionOnly
        }
        catch {
            $expected = if ($outcome -eq 'Timeout') { 'Process exceeded 180 seconds:*' } else { 'Packaged executable regression smoke failed:*' }
            if ($_.Exception.Message -notlike $expected) { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "$scenario $outcome did not fail the complete native smoke gate." }
    }
}
Write-Output 'PASS: actual GitHub pwsh wrapper reaches smoke/compiler and fails for broken assertions; either General or Companion failure/180-second timeout fails the complete native gate.'
