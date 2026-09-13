#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotnetPath = 'dotnet',
    [Parameter(Mandatory)][string]$CliHome,
    [ValidateSet('win-x64')][string]$RuntimeIdentifier = 'win-x64'
)
. "$PSScriptRoot\Packaging.Common.ps1"
$root = Split-Path (Split-Path $PSScriptRoot)
Push-Location $root
try {
    $sdk = Initialize-PackagingSdk $DotnetPath $CliHome
    New-OutputDirectory $OutputDirectory
    $sourceCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit.' }
    $sourceDirty = -not [string]::IsNullOrWhiteSpace((& git status --porcelain | Out-String))
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source worktree state.' }
    $staging = Join-Path $OutputDirectory 'staging'
    $build = Join-Path $OutputDirectory 'build'
    $properties = @(Get-PackagingProperties)
    # File-local generated type names hash source paths even when PDBs are disabled.
    $properties += "-p:PathMap=$build=C:\_\artifacts%2C$root=C:\_\src"
    foreach ($application in @('Desktop', 'Doctor')) {
        $project = Join-Path $root "src\Martlet.$application\Martlet.$application.csproj"
        $graph = Join-Path $OutputDirectory "$application.restore-graph.json"
        Invoke-Dotnet $sdk (@('msbuild', $project, '-t:GenerateRestoreGraphFile',
            "-p:RestoreGraphOutputPath=$graph", "-p:RuntimeIdentifier=$RuntimeIdentifier",
            '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$build", '-verbosity:quiet') + $properties)
        Assert-RestoreGraphLocks $graph
        Invoke-Dotnet $sdk (@('restore', $project, '--locked-mode', '-r', $RuntimeIdentifier,
            '--artifacts-path', $build, '--verbosity', 'minimal') + $properties)
        Invoke-Dotnet $sdk (@('publish', $project, '--no-restore', '-c', 'Release', '-r', $RuntimeIdentifier,
            '--artifacts-path', $build, '-o', (Join-Path $staging $application), '--verbosity', 'minimal') + $properties)
    }
    [IO.Directory]::CreateDirectory((Join-Path $staging 'help')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $staging 'notices')) | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot\INTERNAL.txt" -Destination (Join-Path $staging 'help\INTERNAL.txt')
    Copy-Item -LiteralPath "$PSScriptRoot\DEPENDENCIES.txt" -Destination (Join-Path $staging 'notices\DEPENDENCIES.txt')
    Copy-RuntimeNotices $staging (Join-Path $build 'obj\Martlet.Desktop\project.assets.json')
    Write-PayloadManifest $staging $sourceCommit $sourceDirty
    $manifest = Test-PayloadManifest $staging
    [IO.Directory]::Move($staging, (Join-Path $OutputDirectory 'payload'))
    Write-Output "INTERNAL ONLY: complete $($manifest.rid) payload at $OutputDirectory\payload ($($manifest.files.Count) files)."
}
finally { Pop-Location }
