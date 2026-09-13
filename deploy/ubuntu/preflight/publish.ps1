[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsLinux) { throw 'Native AOT publishing requires the hosted Ubuntu build lane. Windows tests are not Linux execution evidence.' }
if ($env:CI -ne 'true') { throw 'Run with CI=true for the same locked build policy as CI.' }
if ((& dotnet --version) -ne '10.0.401') { throw 'Use the repository-pinned .NET SDK 10.0.401.' }
foreach ($path in @($ArtifactsPath, $Destination)) {
    if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'Build artifact and destination paths must be absolute.' }
}
if (Test-Path -LiteralPath $Destination) { throw 'Destination must be a new directory; existing files are never overwritten.' }
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$project = Join-Path $root 'src/Martlet.Host.Doctor/Martlet.Host.Doctor.csproj'
$native = Join-Path $ArtifactsPath 'native'
$staging = Join-Path $ArtifactsPath 'publish'

& dotnet restore $project -r linux-x64 -p:HostNativeAot=true --locked-mode --artifacts-path $native
if ($LASTEXITCODE -ne 0) { throw 'Locked native restore failed.' }
& dotnet publish $project --no-restore -c Release -r linux-x64 --self-contained true -p:HostNativeAot=true --artifacts-path $native -o $staging
if ($LASTEXITCODE -ne 0) { throw 'Native publish failed. No package is accepted.' }
$binary = Join-Path $staging 'martlet-host'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'The native publisher did not produce martlet-host.' }
$header = [IO.File]::ReadAllBytes($binary)[0..3]
if (($header -join ',') -ne '127,69,76,70') { throw 'Publisher output is not an ELF binary.' }

New-Item -ItemType Directory -Path $Destination | Out-Null
Copy-Item -LiteralPath $binary -Destination (Join-Path $Destination 'martlet-host')
& chmod 755 (Join-Path $Destination 'martlet-host')
if ($LASTEXITCODE -ne 0) { throw 'Cannot set executable mode on the new package artifact.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'candidate-requirements.json') -Destination $Destination
$epoch = & git -C $root show -s --format=%ct HEAD
if ($LASTEXITCODE -ne 0 -or $epoch -notmatch '^\d+$') { throw 'Cannot record source timestamp.' }
$commit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record source commit.' }
$compiler = (& clang --version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) { throw 'The preinstalled native compiler is unavailable.' }
$manifest = [ordered]@{
    schemaVersion = 1
    status = 'InternalUnsignedNotARelease'
    target = 'ubuntu-24.04-linux-x64'
    sdk = '10.0.401'
    sourceCommit = $commit
    sourceDateEpoch = $epoch
    nativeCompiler = $compiler
    nativeAot = $true
    eventPipe = $false
    application = 'martlet-host'
    hostWrites = 'stdout/stderr only; explicit caller redirection is caller-owned'
    requiredClientTools = @()
    deploymentQualified = $false
}
[IO.File]::WriteAllText((Join-Path $Destination 'BUILD-MANIFEST.json'), ($manifest | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
$lines = foreach ($file in (Get-ChildItem -LiteralPath $Destination -File | Sort-Object Name)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.Name
}
[IO.File]::WriteAllText((Join-Path $Destination 'SHA256SUMS'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

$archive = Join-Path $ArtifactsPath 'martlet-host-0.1.0-linux-x64.tar'
& tar --sort=name "--mtime=@$epoch" --owner=0 --group=0 --numeric-owner -cf $archive -C $Destination .
if ($LASTEXITCODE -ne 0) { throw 'Internal archive creation failed.' }
# gzip -n suppresses source filename/time, making packaging repeatable for the same binary/toolchain.
& gzip -n -f $archive
if ($LASTEXITCODE -ne 0) { throw 'Internal archive compression failed.' }
$archive += '.gz'
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$archiveHash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Built internal Linux ELF package. SHA256 $archiveHash. No signing, publication, installation, or deployment qualification."
