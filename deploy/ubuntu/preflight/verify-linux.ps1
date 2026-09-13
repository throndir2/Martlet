[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][string]$TraceVerifier
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsLinux) { throw 'This is actual Linux execution validation, not a cross-publish simulation.' }
if (-not [IO.Path]::IsPathFullyQualified($Package) -or -not [IO.Path]::IsPathFullyQualified($ArtifactsPath)) {
    throw 'Use absolute package and verification artifact paths.'
}
$binary = Join-Path $Package 'martlet-host'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'Missing actual publisher-produced binary.' }
if (-not [IO.Path]::IsPathFullyQualified($TraceVerifier) -or -not (Test-Path -LiteralPath $TraceVerifier -PathType Leaf)) { throw 'Missing built trace-policy verifier.' }
if (-not (Get-Command strace -ErrorAction SilentlyContinue)) { throw 'Hosted syscall gate needs preinstalled strace; no system package installation is authorized.' }
if (Test-Path -LiteralPath $ArtifactsPath) { throw 'Use a new verification artifact directory.' }
New-Item -ItemType Directory -Path $ArtifactsPath | Out-Null
Push-Location $Package
try {
    & sha256sum --check --quiet SHA256SUMS
    if ($LASTEXITCODE -ne 0) { throw 'Package checksum mismatch.' }
}
finally { Pop-Location }
$before = (Get-ChildItem -LiteralPath $Package -File | Sort-Object Name | ForEach-Object {
    '{0} {1}' -f $_.Name, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}) -join "`n"

function Invoke-ReadOnlyCase([string]$Name, [string[]]$Arguments, [int[]]$ExitCodes, [bool]$AllowPackages) {
    $trace = Join-Path $ArtifactsPath "$Name.syscalls"
    $report = Join-Path $ArtifactsPath "$Name.stdout"
    $errorFile = Join-Path $ArtifactsPath "$Name.stderr"
    # Do not set DOTNET_EnableDiagnostics=0 here: the packaged artifact itself must be non-writing.
    & strace -f -q -yy -s 2048 -e 'trace=all' -e 'raw=read,pread64,readv,preadv,preadv2,getdents,getdents64,getrandom,uname,readlink,readlinkat,getcwd' -o $trace -- $binary @Arguments > $report 2> $errorFile
    $code = $LASTEXITCODE
    if ($code -notin $ExitCodes) { throw "$Name unexpected exit $code; inspect the private runner temp artifacts." }
    $policyScope = if ($AllowPackages) { 'packages' } else { 'no-packages' }
    $policyOutput = & dotnet $TraceVerifier --verify-trace $trace $binary $policyScope $code @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name failed the fail-closed trace operation policy." }
    if ((Get-Item -LiteralPath $report).Length -gt 131072) { throw 'Report output exceeds the 128 KiB contract budget.' }
    if ((Get-Item -LiteralPath $errorFile).Length -ne 0) { throw "$Name emitted stderr; no successful gate is accepted." }
    Write-Host "$Name : doctor exit $code; bounded output; $policyOutput"
    return [pscustomobject]@{ ReportPath = $report; ExitCode = $code }
}

$help = Invoke-ReadOnlyCase 'help' @('--help') @(0) $false
$fixture = Invoke-ReadOnlyCase 'fixture-inventory' @('doctor', '--fixture', 'inventory', '--scope', 'inventory', '--json') @(0) $false
$missing = Invoke-ReadOnlyCase 'fixture-missing' @('doctor', '--fixture', 'missing-tools', '--json') @(1) $false
$incomplete = Invoke-ReadOnlyCase 'fixture-incomplete' @('doctor', '--fixture', 'prerequisites', '--json') @(2) $false
$unsupported = Invoke-ReadOnlyCase 'fixture-unsupported' @('doctor', '--fixture', 'windows', '--json') @(3) $false
$local = Invoke-ReadOnlyCase 'hosted-ubuntu-local' @('doctor', '--json', '--no-gpu-query') @(1, 2) $true
if (-not ([IO.File]::ReadAllText($help.ReportPath).Contains('INTERNAL read-only inventory'))) { throw 'Native help output failed.' }
function Read-ValidatedReport($Case, [string]$Provenance, [string]$Scope) {
    $document = Get-Content -LiteralPath $Case.ReportPath -Raw | ConvertFrom-Json
    if ($document.schemaVersion -ne 1 -or $document.provenance -ne $Provenance -or
        $document.scope -ne $Scope -or $document.deploymentQualified -ne $false -or
        $document.exitCode -ne $Case.ExitCode -or $document.probes.Count -ne 19) {
        throw 'Native JSON schema/provenance/scope/measured-exit contract failed.'
    }
    return $document
}
$fixtureDocument = Read-ValidatedReport $fixture 'AuthoredFixture' 'Inventory'
$missingDocument = Read-ValidatedReport $missing 'AuthoredFixture' 'Prerequisites'
$incompleteDocument = Read-ValidatedReport $incomplete 'AuthoredFixture' 'Prerequisites'
$unsupportedDocument = Read-ValidatedReport $unsupported 'AuthoredFixture' 'Prerequisites'
foreach ($id in @('Nvidia', 'DockerEngine', 'Compose', 'ContainerToolkit')) {
    if (($missingDocument.probes | Where-Object id -eq $id).code -ne 'HOST_MISSING') { throw 'Missing-tool fixture did not establish its expected finding.' }
}
if (($incompleteDocument.probes | Where-Object id -eq 'QualifiedTuple').code -ne 'HOST_UNQUALIFIED_VERSIONS' -or
    ($unsupportedDocument.probes | Where-Object id -eq 'Platform').code -ne 'HOST_UNSUPPORTED_EXECUTION') {
    throw 'Incomplete/unsupported fixture semantics failed.'
}
$document = Read-ValidatedReport $local 'LiveLocal' 'Prerequisites'
$platform = $document.probes | Where-Object id -eq 'Platform'
if ($platform.code -ne 'HOST_OBSERVED' -or $platform.evidence.platform.distribution -ne 'Ubuntu' -or
    $platform.evidence.platform.version -ne '24.04' -or $platform.evidence.platform.osArchitecture -ne 'X64') {
    throw 'The actual runner did not establish the required Ubuntu 24.04 x64 package execution lane.'
}
foreach ($id in @('Cpu', 'Memory', 'Disk', 'Network', 'GatewayPort', 'LocalClock')) {
    $probe = $document.probes | Where-Object id -eq $id
    if ($probe.code -ne 'HOST_OBSERVED' -and -not ($id -eq 'GatewayPort' -and $probe.code -eq 'HOST_PORT_IN_USE')) {
        throw "Production $id local inventory did not return a valid observation on the hosted CPU runner."
    }
}
foreach ($id in @('Nvidia', 'ContainerGpu', 'ModelInference', 'ClockAccuracy', 'PairingFirewall')) {
    if (($document.probes | Where-Object id -eq $id).code -ne 'HOST_NOT_RUN') { throw "Unexpected $id probe execution." }
}
if (($document.probes | Where-Object id -eq 'DockerAccess').evidence.dockerAccess.daemonContacted) { throw 'Unexpected daemon contact claim.' }
$after = (Get-ChildItem -LiteralPath $Package -File | Sort-Object Name | ForEach-Object {
    '{0} {1}' -f $_.Name, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}) -join "`n"
if ($before -ne $after) { throw 'Package files changed during read-only execution.' }
Write-Output 'ACTUAL EPHEMERAL GITHUB UBUNTU 24.04 CPU RUNNER - NOT OWNER HARDWARE, NOT GPU/DAEMON/MODEL/FIREWALL/SETUP QUALIFICATION.'
# Only the already-sanitized product report goes to the build log. Raw syscall traces remain in runner temp and are not uploaded.
Get-Content -LiteralPath $local.ReportPath -Raw
# Doctor exits 1/2 are expected above; do not leak that last native exit into the successful harness.
exit 0
