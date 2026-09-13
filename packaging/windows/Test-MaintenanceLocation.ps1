#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$DotnetPath,
    [Parameter(Mandatory)][string]$CliHome,
    [switch]$ExerciseImplicitSource
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$osLocation = [Environment]::CurrentDirectory
Set-Location -LiteralPath $SourceDirectory
if ($osLocation -ieq (Get-Location).ProviderPath) { throw 'Regression requires different OS and PowerShell working directories.' }
. "$SourceDirectory\packaging\windows\Packaging.Common.ps1"
if ($ExerciseImplicitSource) {
    $env:_WorkloadLibraryPacksFolder = "$WorkDirectory-library-packs"
    New-OutputDirectory $env:_WorkloadLibraryPacksFolder
}

# Delete only copied locks so the real maintenance command must regenerate both graphs.
foreach ($project in @('Core', 'Diagnostics', 'Desktop', 'Doctor')) {
    [IO.File]::Delete((Join-Path $SourceDirectory "packaging\windows\locks\Martlet.$project.packages.lock.json"))
}
& "$SourceDirectory\packaging\windows\Update-PublishLocks.ps1" -DotnetPath $DotnetPath -CliHome $CliHome -WorkDirectory $WorkDirectory
$sdk = Initialize-PackagingSdk $DotnetPath $CliHome
$info = Invoke-BoundedProcess $sdk @('--info')
if ($info.ExitCode -ne 0 -or -not $info.Stdout.Contains((Join-Path $SourceDirectory 'global.json'))) {
    throw 'SDK resolution did not use the copied repository global.json from the PowerShell location.'
}
[xml]$config = Get-Content -LiteralPath (Join-Path $SourceDirectory 'NuGet.config') -Raw
foreach ($application in @('Desktop', 'Doctor')) {
    $assets = Get-Content -LiteralPath (Join-Path $WorkDirectory "obj\Martlet.$application\project.assets.json") -Raw | ConvertFrom-Json
    $expectedProject = Join-Path $SourceDirectory "src\Martlet.$application\Martlet.$application.csproj"
    if ($assets.project.restore.projectPath -ine $expectedProject) {
        throw "Maintenance project mismatch for ${application}: expected '$expectedProject', got '$($assets.project.restore.projectPath)'."
    }
    if ($assets.project.restore.configFilePaths -inotcontains (Join-Path $SourceDirectory 'NuGet.config')) {
        throw "Maintenance config mismatch for ${application}: copied NuGet.config missing from $($assets.project.restore.configFilePaths.Count) resolved configuration files."
    }
    $evaluation = Invoke-BoundedProcess $sdk (@('msbuild', $expectedProject,
        '-getProperty:NETCoreSdkVersion,RestoreAdditionalProjectSources', '-p:RuntimeIdentifier=win-x64') + @(Get-PackagingProperties))
    if ($evaluation.ExitCode -ne 0) { throw "Cannot evaluate effective restore sources: $($evaluation.Stderr)" }
    $properties = ($evaluation.Stdout | ConvertFrom-Json).Properties
    if ($properties.NETCoreSdkVersion -cne (Get-PackagingPins).sdkVersion) { throw 'Maintenance evaluated the wrong SDK.' }
    # SDKs with installed workloads can add library-packs independently of NuGet.config.
    $expectedSources = @($config.configuration.packageSources.add.value) +
        @($properties.RestoreAdditionalProjectSources.Split(';', [StringSplitOptions]::RemoveEmptyEntries))
    $expectedSources = @($expectedSources | Sort-Object -Unique)
    $actualSources = @($assets.project.restore.sources.PSObject.Properties.Name)
    if (@(Compare-Object $expectedSources $actualSources).Count -ne 0) {
        $hosts = @($actualSources | ForEach-Object { ([uri]$_).Host })
        throw "Maintenance sources mismatch for ${application}: expected $($expectedSources.Count), got $($actualSources.Count); hosts: $($hosts -join ', ')."
    }
}
if ([Environment]::CurrentDirectory -cne $osLocation -or (Get-Location).ProviderPath -ine $SourceDirectory) {
    throw 'Maintenance mutated global OS working directory or did not restore the PowerShell location.'
}
Write-Output "PASS: real maintenance regenerates both lock graphs with distinct OS/PowerShell locations, pinned SDK and effective repository NuGet sources; isolated SDK source=$ExerciseImplicitSource; process cwd unchanged."
if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }
