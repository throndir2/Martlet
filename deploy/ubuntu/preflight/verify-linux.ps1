[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$ArtifactsPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsLinux) { throw 'This is actual Linux execution validation, not a cross-publish simulation.' }
if (-not [IO.Path]::IsPathFullyQualified($Package) -or -not [IO.Path]::IsPathFullyQualified($ArtifactsPath)) {
    throw 'Use absolute package and verification artifact paths.'
}
$binary = Join-Path $Package 'martlet-host'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'Missing actual publisher-produced binary.' }
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
    & strace -f -qq -yy -s 2048 -e 'trace=%file,%network,process,write,writev' -o $trace -- $binary @Arguments > $report 2> $errorFile
    $code = $LASTEXITCODE
    if ($code -notin $ExitCodes) { throw "$Name unexpected exit $code; inspect the private runner temp artifacts." }
    $execCount = 0
    foreach ($line in [IO.File]::ReadLines($trace)) {
        if ($line -match '\b(?:socket|socketpair|connect|bind|listen|accept|accept4|sendto|sendmsg|sendmmsg|recvfrom|recvmsg|recvmmsg)\(') {
            throw "$Name attempted a network/socket syscall. No network readiness is accepted."
        }
        if ($line -match '\b(?:creat|mkdir|mkdirat|unlink|unlinkat|rename|renameat|renameat2|chmod|fchmod|fchmodat|chown|fchown|lchown|truncate|ftruncate|symlink|symlinkat|link|linkat|mknod|mknodat|mount|umount2)\(') {
            throw "$Name attempted a host mutation."
        }
        if ($line -match '\bopen(?:at|at2)?\(' -and $line -match 'O_WRONLY|O_RDWR|O_CREAT|O_TRUNC|O_APPEND') {
            throw "$Name attempted writable file access."
        }
        if ($line -match '\b(?:write|writev)\((.+?),') {
            $descriptor = $Matches[1]
            if ($descriptor -notmatch '^[12](?:<|$)' -and $descriptor -notmatch '^\d+<(?:pipe:|anon_inode:)') {
                throw "$Name attempted a write outside stdout/stderr or private process-coordination pipes."
            }
        }
        if ($line -match '\bexecve\("([^"]+)"') {
            $exe = $Matches[1]
            $execCount++
            if ($exe -ne $binary -and (-not $AllowPackages -or $exe -ne '/usr/bin/dpkg-query')) {
                throw "$Name attempted an executable outside its approved scope."
            }
            if ($exe -eq '/usr/bin/dpkg-query' -and
                ($line -notmatch '--admindir=/var/lib/dpkg' -or $line -notmatch '--showformat=' -or $line -notmatch 'nvidia-container-toolkit-base')) {
                throw 'Unexpected package query argument form.'
            }
        }
        if ($line -match '\bexecveat\(') { throw 'Unexpected alternate execution path.' }
    }
    if ($execCount -ne $(if ($AllowPackages) { 2 } else { 1 })) { throw "$Name unexpected process execution count $execCount." }
    if ((Get-Item -LiteralPath $report).Length -gt 131072) { throw 'Report output exceeds the 128 KiB contract budget.' }
    if ((Get-Item -LiteralPath $errorFile).Length -ne 0) { throw "$Name emitted stderr; no successful gate is accepted." }
    Write-Host "$Name : exit $code, bounded output, no file mutations or network/socket syscalls, exec count $execCount."
    return $report
}

$help = Invoke-ReadOnlyCase 'help' @('--help') @(0) $false
$fixture = Invoke-ReadOnlyCase 'fixture-inventory' @('doctor', '--fixture', 'inventory', '--scope', 'inventory', '--json') @(0) $false
$missing = Invoke-ReadOnlyCase 'fixture-missing' @('doctor', '--fixture', 'missing-tools', '--json') @(1) $false
$local = Invoke-ReadOnlyCase 'hosted-ubuntu-local' @('doctor', '--json', '--no-gpu-query') @(1, 2) $true
$fixtureDocument = Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json
if ($fixtureDocument.provenance -ne 'AuthoredFixture' -or $fixtureDocument.deploymentQualified) { throw 'Fixture provenance contract failed.' }
$document = Get-Content -LiteralPath $local -Raw | ConvertFrom-Json
if ($document.schemaVersion -ne 1 -or $document.provenance -ne 'LiveLocal' -or $document.deploymentQualified) {
    throw 'Live report contract failed.'
}
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
Get-Content -LiteralPath $local -Raw
# Doctor exits 1/2 are expected above; do not leak that last native exit into the successful harness.
exit 0
