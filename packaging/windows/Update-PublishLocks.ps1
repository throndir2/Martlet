#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [Parameter(Mandatory)][string]$CliHome,
    [Parameter(Mandatory)][string]$WorkDirectory
)
. "$PSScriptRoot\Packaging.Common.ps1"
$root = Split-Path (Split-Path $PSScriptRoot)
Assert-MSBuildPath $root
Assert-MSBuildPath $WorkDirectory
Push-Location $root
try {
    $sdk = Initialize-PackagingSdk $DotnetPath $CliHome
    New-OutputDirectory $WorkDirectory
    foreach ($application in @('Desktop', 'Doctor')) {
        Invoke-Dotnet $sdk (@('restore', "src\Martlet.$application\Martlet.$application.csproj",
            '--force-evaluate', '-p:RestoreLockedMode=false', '-r', (Get-PackagingPins).rid,
            '--artifacts-path', $WorkDirectory, '--verbosity', 'minimal') + @(Get-PackagingProperties))
    }
    Write-Output 'RID locks regenerated intentionally. Review and commit every affected packaging\windows\locks file; publish still enforces locked restore.'
}
finally { Pop-Location }
