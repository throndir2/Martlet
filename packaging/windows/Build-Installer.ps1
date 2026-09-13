#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$BuilderDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
. "$PSScriptRoot\Installer.Common.ps1"
Assert-PackagingHost
$compiler = Get-VerifiedInnoCompiler $BuilderDirectory
$null = Test-PayloadManifest $PayloadRoot
New-OutputDirectory $OutputDirectory
$fileList = Join-Path $OutputDirectory 'payload-files.iss'
$manifest = Write-InstallerFileList $PayloadRoot $fileList
$staging = Join-Path $OutputDirectory 'staging'
[IO.Directory]::CreateDirectory($staging) | Out-Null
$arguments = @('--quiet', '--no-ide-signtools',
    "--define=PayloadRoot=$PayloadRoot", "--define=PayloadFiles=$fileList",
    "--define=AppVersion=$($manifest.applicationVersion)", "--define=BuildOutput=$staging",
    (Join-Path $PSScriptRoot 'Martlet.iss'))
$result = Invoke-BoundedProcess $compiler $arguments 180
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'compiler.log'), $result.Stdout + $result.Stderr)
if ($result.ExitCode -ne 0) {
    throw "Installer compilation failed (exit $($result.ExitCode)): $($result.Stderr). Inspect $OutputDirectory\compiler.log."
}
$name = "Martlet-$($manifest.applicationVersion)-win-x64-INTERNAL-UNSIGNED.exe"
$installer = (Get-RequiredFile (Join-Path $staging $name)).FullName
Assert-X64Pe $installer
if ((Get-Item -LiteralPath $installer).Length -gt 200MB) { throw 'Installer exceeds the 200 MiB planning budget; review before widening it.' }
$null = Test-PayloadManifest $PayloadRoot
$provenance = [ordered]@{
    schemaVersion = 1
    channel = 'INTERNAL DEVELOPMENT ONLY - UNSIGNED'
    installer = $name
    bytes = (Get-Item -LiteralPath $installer).Length
    sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    payloadManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $PayloadRoot 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    toolchain = Get-PackagingPins
    sourceCommit = $manifest.sourceCommit
    sourceDirty = $manifest.sourceDirty
    cleanWindowsLifecycle = 'NOT RUN - requires isolated Windows 11 25H2 x64 VM'
    signing = 'NOT RUN - unsigned internal skeleton, no public distribution authorized'
}
[IO.File]::WriteAllText((Join-Path $staging 'installer-manifest.json'), ($provenance | ConvertTo-Json -Depth 8) + "`n")
$manifestHash = (Get-FileHash -LiteralPath (Join-Path $staging 'installer-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $staging 'SHA256SUMS.txt'), "$($provenance.sha256)  $name`n$manifestHash  installer-manifest.json`n")
[IO.Directory]::Move($staging, (Join-Path $OutputDirectory 'installer'))
Write-Output "INTERNAL ONLY: compiled $name ($($provenance.bytes) bytes). No install/uninstall/signing qualification was performed."
