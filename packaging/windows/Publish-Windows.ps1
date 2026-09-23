#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotnetPath = 'dotnet',
    [string]$NodePath = 'node',
    [Parameter(Mandatory)][string]$CliHome,
    [ValidateSet('win-x64')][string]$RuntimeIdentifier = 'win-x64'
)
. "$PSScriptRoot\Packaging.Common.ps1"
$root = Split-Path (Split-Path $PSScriptRoot)
Assert-MSBuildPath $root
Assert-MSBuildPath $OutputDirectory
Push-Location $root
try {
    $sdk = Initialize-PackagingSdk $DotnetPath $CliHome -WorkingDirectory $root
    New-OutputDirectory $OutputDirectory
    $source = Get-PackagingSourceReceipt $root
    $sdkReceipt = Get-PackagingSdkReceipt $sdk
    $staging = Join-Path $OutputDirectory 'staging'
    $build = Join-Path $OutputDirectory 'build'
    $properties = @(Get-PackagingProperties)
    $node = (Get-Command $NodePath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    Assert-MSBuildPath $node
    $properties += "-p:NodeExecutable=$node"
    # File-local generated type names hash source paths even when PDBs are disabled.
    $properties += "-p:PathMap=$build=C:\_\artifacts%2C$root=C:\_\src"
    foreach ($context in Get-PublishContexts) {
        $application = $context.name
        $project = Join-Path $root "src\$($context.project)\$($context.project).csproj"
        $graph = Join-Path $OutputDirectory "$application.restore-graph.json"
        Invoke-Dotnet $sdk (@('msbuild', $project, '-t:GenerateRestoreGraphFile',
            "-p:RestoreGraphOutputPath=$graph", "-p:RuntimeIdentifier=$RuntimeIdentifier",
            '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$build", '-verbosity:quiet') + $properties) -WorkingDirectory $root
        Assert-RestoreGraphLocks $graph
        Invoke-Dotnet $sdk (@('restore', $project, '--locked-mode', '-r', $RuntimeIdentifier,
            '--artifacts-path', $build, '--verbosity', 'minimal') + $properties) -WorkingDirectory $root
    }
    foreach ($application in @('Desktop', 'Doctor')) {
        $project = Join-Path $root "src\Martlet.$application\Martlet.$application.csproj"
        Invoke-Dotnet $sdk (@('publish', $project, '--no-restore', '-c', 'Release', '-r', $RuntimeIdentifier,
            '--framework', 'net10.0-windows',
            '--artifacts-path', $build, '-o', (Join-Path $staging $application), '--verbosity', 'minimal') + $properties) -WorkingDirectory $root
    }
    [IO.Directory]::CreateDirectory((Join-Path $staging 'help')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $staging 'notices')) | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot\INTERNAL.txt" -Destination (Join-Path $staging 'help\INTERNAL.txt')
    Copy-Item -LiteralPath (Join-Path $root 'docs\TROUBLESHOOTING.md') -Destination (Join-Path $staging 'help\TROUBLESHOOTING.md')
    Copy-Item -LiteralPath "$PSScriptRoot\DEPENDENCIES.txt" -Destination (Join-Path $staging 'notices\DEPENDENCIES.txt')
    Copy-Item -LiteralPath "$PSScriptRoot\NAudio-THIRD-PARTY-NOTICES.txt" -Destination (Join-Path $staging 'notices\NAudio-THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $root 'src\Martlet.Avatar.Audio2Face\THIRD-PARTY-NOTICES.md') -Destination (Join-Path $staging 'notices\Audio2Face-THIRD-PARTY-NOTICES.md')
    Copy-Item -LiteralPath (Join-Path $root 'src\Martlet.Avatar.Audio2Face\Protos\LICENSE-2.0.txt') -Destination (Join-Path $staging 'notices\Audio2Face-Protos-LICENSE.txt')
    Copy-RuntimeNotices $staging (Join-Path $build 'obj\Martlet.Desktop\project.assets.json')
    $provenance = Get-PackageProvenance $staging $OutputDirectory $source $sdkReceipt -NodePath $node
    Assert-PackagingSourceReceipt $source $root
    Assert-PackagingSdkReceipt $sdkReceipt $sdk
    Write-PackageSbom $staging $provenance
    Write-PayloadManifest $staging $source.commit $source.dirty $provenance
    $manifest = Test-PayloadManifest $staging
    Assert-PackagingSourceReceipt $source $root
    [IO.Directory]::Move($staging, (Join-Path $OutputDirectory 'payload'))
    Write-Output "INTERNAL ONLY: complete $($manifest.rid) payload at $OutputDirectory\payload ($($manifest.files.Count) files)."
}
finally { Pop-Location }
