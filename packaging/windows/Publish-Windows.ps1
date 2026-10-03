#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotnetPath = 'dotnet',
    [string]$NodePath = 'node',
    [Parameter(Mandatory)][string]$CliHome,
    [ValidateSet('win-x64')][string]$RuntimeIdentifier = 'win-x64',
    [switch]$PublicRelease
)
. "$PSScriptRoot\Packaging.Common.ps1"
$root = Split-Path (Split-Path $PSScriptRoot)
$channel = if ($PublicRelease) { 'PublicUnsigned' } else { 'Internal' }
if ($PublicRelease) { Assert-PublicReleaseRights $root }
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
        # Desktop is single-targeted; a global TFM would override its versioned renderer child.
        $framework = if ($application -ceq 'Doctor') { @('--framework', 'net10.0-windows') } else { @() }
        Invoke-Dotnet $sdk (@('publish', $project, '--no-restore', '-c', 'Release', '-r', $RuntimeIdentifier,
            '--artifacts-path', $build, '-o', (Join-Path $staging $application), '--verbosity', 'minimal') + $framework + $properties) -WorkingDirectory $root
    }
    [IO.Directory]::CreateDirectory((Join-Path $staging 'help')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $staging 'notices')) | Out-Null
    $help = if ($PublicRelease) { 'RELEASE.txt' } else { 'INTERNAL.txt' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $help) -Destination (Join-Path $staging "help\$help")
    if ($PublicRelease) { Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $staging 'help\LICENSE.txt') }
    Copy-Item -LiteralPath (Join-Path $root 'docs\TROUBLESHOOTING.md') -Destination (Join-Path $staging 'help\TROUBLESHOOTING.md')
    [IO.Directory]::CreateDirectory((Join-Path $staging 'prerequisites')) | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot\Install-Prerequisites.ps1" -Destination (Join-Path $staging 'prerequisites\Install-Prerequisites.ps1')
    Copy-Item -LiteralPath "$PSScriptRoot\DEPENDENCIES.txt" -Destination (Join-Path $staging 'notices\DEPENDENCIES.txt')
    Copy-Item -LiteralPath "$PSScriptRoot\NAudio-THIRD-PARTY-NOTICES.txt" -Destination (Join-Path $staging 'notices\NAudio-THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath "$PSScriptRoot\WebRTC-APM-NOTICES.txt" -Destination (Join-Path $staging 'notices\WebRTC-APM-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $root 'src\Martlet.Avatar.Audio2Face\THIRD-PARTY-NOTICES.md') -Destination (Join-Path $staging 'notices\Audio2Face-THIRD-PARTY-NOTICES.md')
    Copy-Item -LiteralPath (Join-Path $root 'src\Martlet.Avatar.Audio2Face\Protos\LICENSE-2.0.txt') -Destination (Join-Path $staging 'notices\Audio2Face-Protos-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $root 'src\Martlet.F5\BundledVoices\NOTICES.txt') -Destination (Join-Path $staging 'notices\F5-Voices-NOTICES.txt')
    Copy-RuntimeNotices $staging (Join-Path $build 'obj\Martlet.Desktop\project.assets.json')
    $provenance = Get-PackageProvenance $staging $OutputDirectory $source $sdkReceipt -NodePath $node -Channel $channel
    Assert-PackagingSourceReceipt $source $root
    Assert-PackagingSdkReceipt $sdkReceipt $sdk
    Write-PackageSbom $staging $provenance -Channel $channel
    Write-PayloadManifest $staging $source.commit $source.dirty $provenance -Channel $channel
    $manifest = Test-PayloadManifest $staging
    Assert-PackagingSourceReceipt $source $root
    [IO.Directory]::Move($staging, (Join-Path $OutputDirectory 'payload'))
    Write-Output "$($manifest.channel): complete $($manifest.rid) payload at $OutputDirectory\payload ($($manifest.files.Count) files)."
}
finally { Pop-Location }
