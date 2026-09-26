#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotnetPath = 'dotnet',
    [string]$NodePath = 'node'
)
. "$PSScriptRoot\Packaging.Common.ps1"
$root = Split-Path (Split-Path $PSScriptRoot)
Assert-PackagingHost
Assert-PublicReleaseRights $root
Assert-MSBuildPath $root
Assert-MSBuildPath $OutputDirectory
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory) -or (Test-Path -LiteralPath $OutputDirectory)) {
    throw 'Use a new absolute output directory for the public build.'
}
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if (([version]$properties.Project.PropertyGroup.Version).ToString(3) -cne $Version) {
    throw 'Requested public version differs from Directory.Build.props.'
}
Push-Location $root
try {
    & npm ci --prefix src\Martlet.Avatar.Vrm --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "Locked browser dependency restore failed (exit $LASTEXITCODE)." }
    New-OutputDirectory $OutputDirectory
    $publish = Join-Path $OutputDirectory 'publish'
    $builder = Join-Path $OutputDirectory 'inno'
    $package = Join-Path $OutputDirectory 'package'
    & "$PSScriptRoot\Publish-Windows.ps1" -PublicRelease -DotnetPath $DotnetPath -NodePath $NodePath `
        -CliHome (Join-Path $OutputDirectory 'cli-home') -OutputDirectory $publish
    $payload = Join-Path $publish 'payload'
    $manifest = Test-PayloadManifest $payload -RequireCurrentSource
    if ($manifest.channel -cne (Get-PackagingChannel PublicUnsigned).name -or
        $manifest.applicationVersion -cne "$Version.0") { throw 'Public payload version/channel differs from the requested release.' }
    & "$PSScriptRoot\Get-InnoSetup.ps1" -Destination $builder
    & "$PSScriptRoot\Build-Installer.ps1" -PublicRelease -PayloadRoot $payload -BuilderDirectory $builder -OutputDirectory $package
    Write-Output "Release installer: $(Join-Path $package "installer\Martlet-$Version-win-x64.exe")."
}
finally { Pop-Location }
