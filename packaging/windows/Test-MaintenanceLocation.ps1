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

$normalLocks = @{}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $SourceDirectory 'src') -Recurse -Filter packages.lock.json) {
    $normalLocks[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
# Delete only copied RID locks so maintenance must regenerate every transitive project, including outer builds.
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $SourceDirectory 'packaging\windows\locks') -File) {
    [IO.File]::Delete($file.FullName)
}
& "$SourceDirectory\packaging\windows\Update-PublishLocks.ps1" -DotnetPath $DotnetPath -CliHome $CliHome -WorkDirectory $WorkDirectory
foreach ($path in $normalLocks.Keys) {
    Assert-Sha256 $path $normalLocks[$path]
}
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
        '-getProperty:NETCoreSdkVersion,RestoreAdditionalProjectSources,TargetFrameworks', '-p:RuntimeIdentifier=win-x64') + @(Get-PackagingProperties))
    if ($evaluation.ExitCode -ne 0) { throw "Cannot evaluate effective restore sources: $($evaluation.Stderr)" }
    $properties = ($evaluation.Stdout | ConvertFrom-Json).Properties
    $evaluations = @($properties)
    foreach ($framework in $properties.TargetFrameworks.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
        $inner = Invoke-BoundedProcess $sdk (@('msbuild', $expectedProject, "-p:TargetFramework=$framework",
            '-getProperty:NETCoreSdkVersion,RestoreAdditionalProjectSources', '-p:RuntimeIdentifier=win-x64') + @(Get-PackagingProperties))
        if ($inner.ExitCode -ne 0) { throw "Cannot evaluate inner-build restore sources: $($inner.Stderr)" }
        $evaluations += ($inner.Stdout | ConvertFrom-Json).Properties
    }
    foreach ($evaluated in $evaluations) {
        if ($evaluated.NETCoreSdkVersion -cne (Get-PackagingPins).sdkVersion) { throw 'Maintenance evaluated the wrong SDK.' }
    }
    # SDKs with installed workloads can add library-packs independently of NuGet.config.
    # Multi-target restore includes inner-build sources that the outer build does not evaluate.
    $expectedSources = @($config.configuration.packageSources.add.value) +
        @($evaluations | ForEach-Object { $_.RestoreAdditionalProjectSources.Split(';', [StringSplitOptions]::RemoveEmptyEntries) })
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
