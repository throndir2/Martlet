[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][string]$TraceVerifier
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsLinux -or -not [IO.Path]::IsPathFullyQualified($Package) -or -not [IO.Path]::IsPathFullyQualified($ArtifactsPath)) {
    throw 'Wrapper negatives require Linux and absolute owned artifact paths.'
}
if (Test-Path -LiteralPath $ArtifactsPath) { throw 'Use a new wrapper-test artifact directory.' }
New-Item -ItemType Directory -Path $ArtifactsPath | Out-Null
$verifier = Join-Path $PSScriptRoot 'verify-linux.ps1'
function Assert-WrapperFailure([string]$Name, [string]$Payload, [string]$ExpectedError) {
    $log = Join-Path $ArtifactsPath "$Name.log"
    & pwsh -NoLogo -NoProfile -NonInteractive -File $verifier -Package $Payload -ArtifactsPath (Join-Path $ArtifactsPath "$Name-output") -TraceVerifier $TraceVerifier > $log 2>&1
    $code = $LASTEXITCODE
    if ($code -eq 0 -or -not ([IO.File]::ReadAllText($log).Contains($ExpectedError))) {
        throw "Wrapper negative $Name did not fail for the expected assertion."
    }
    Write-Output "${Name}: verifier process exited $code for its expected assertion, not success."
}
Assert-WrapperFailure 'missing-ELF' (Join-Path $ArtifactsPath 'missing-payload') 'Missing actual publisher-produced binary.'
$corrupt = Join-Path $ArtifactsPath 'corrupt-payload'
New-Item -ItemType Directory -Path $corrupt | Out-Null
foreach ($name in @('martlet-host', 'README.md', 'candidate-requirements.json', 'BUILD-MANIFEST.json', 'SHA256SUMS')) {
    Copy-Item -LiteralPath (Join-Path $Package $name) -Destination (Join-Path $corrupt $name)
}
# Mutate only this expressly owned negative fixture, never the accepted package or a host file.
[IO.File]::AppendAllText((Join-Path $corrupt 'README.md'), "`nWRAPPER NEGATIVE FIXTURE`n")
Assert-WrapperFailure 'bad-checksum' $corrupt 'Package checksum mismatch.'
exit 0
