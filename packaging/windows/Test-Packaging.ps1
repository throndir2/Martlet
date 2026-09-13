#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$ComparePayloadRoot,
    [string]$BuilderDirectory,
    [string]$DotnetPath = 'dotnet',
    [Parameter(Mandatory)][string]$CliHome
)
. "$PSScriptRoot\Installer.Common.ps1"
Assert-PackagingHost
Assert-MSBuildPath (Split-Path (Split-Path $PSScriptRoot))
Assert-MSBuildPath $WorkDirectory
New-OutputDirectory $WorkDirectory
$sdk = Initialize-PackagingSdk $DotnetPath $CliHome
$manifest = Test-PayloadManifest $PayloadRoot
$script:cases = 0
function Assert-Fails([string]$Name, [scriptblock]$Action, [string]$MessagePattern, [string]$NativeErrorCode) {
    $failed = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike $MessagePattern) { throw "Wrong failure for ${Name}: $($_.Exception.Message)" }
        if ($NativeErrorCode -and [string]$_.Exception.Data['NativeOutput'] -notlike "*error ${NativeErrorCode}:*") {
            throw "Expected native diagnostic $NativeErrorCode was not reported for $Name."
        }
        $failed = $true
    }
    if (-not $failed) { throw "Expected failure did not occur: $Name" }
    $script:cases++
}

$copy = Join-Path $WorkDirectory ("payload with spaces $([char]0x00E9)")
Copy-Item -LiteralPath $PayloadRoot -Destination $copy -Recurse
$copied = Test-PayloadManifest $copy
$manifestPath = Join-Path $copy 'manifest.json'
$originalManifest = [IO.File]::ReadAllBytes($manifestPath)
Write-PayloadManifest $copy $copied.sourceCommit $copied.sourceDirty
if ([Convert]::ToHexString([IO.File]::ReadAllBytes($manifestPath)) -cne [Convert]::ToHexString($originalManifest)) {
    throw 'Manifest is not reproducible after copying to a different Unicode/spaced path.'
}
$script:cases++

$help = [IO.File]::ReadAllText((Join-Path $copy 'help\INTERNAL.txt'))
foreach ($required in @('FIXTURE - NOT AI', 'self-test --scenario streaming --json',
        'Stop fixture', 'NOT spoken AI', 'Permission is not saved.')) {
    if (-not $help.Contains($required)) { throw "Installed fixture help is missing: $required" }
}
$script:cases++

foreach ($relative in @('Doctor\Martlet.Doctor.exe', 'Desktop\coreclr.dll', 'Desktop\PresentationFramework.dll',
        'Doctor\System.Text.Json.dll', 'notices\WPF-THIRD-PARTY-NOTICES.txt',
        'Desktop\Martlet.Sessions.dll', 'Doctor\Martlet.Fixtures.dll', 'Doctor\Martlet.Audio.dll', 'Desktop\Martlet.Credentials.Windows.dll',
        'Desktop\NAudio.Wasapi.dll', 'Doctor\NAudio.Core.dll', 'Desktop\System.Numerics.Tensors.dll',
        'notices\NAudio-LICENSE.txt', 'notices\NAudio-THIRD-PARTY-NOTICES.txt',
        'notices\System.Numerics.Tensors-LICENSE.txt', 'notices\System.Numerics.Tensors-THIRD-PARTY-NOTICES.txt')) {
    $path = Join-Path $copy $relative
    $bytes = [IO.File]::ReadAllBytes($path)
    try {
        [IO.File]::Delete($path)
        Assert-Fails "omitted $relative" { Test-PayloadManifest $copy } '*Required file missing*'
        Assert-Fails "installer rejects omitted $relative" { Write-InstallerFileList $copy (Join-Path $WorkDirectory 'omitted.iss') } '*Required file missing*'
    }
    finally { [IO.File]::WriteAllBytes($path, $bytes) }
}
$corrupt = Join-Path $copy 'Doctor\Martlet.Core.dll'
$original = [IO.File]::ReadAllBytes($corrupt)
try {
    $changed = [byte[]]$original.Clone()
    $changed[-1] = $changed[-1] -bxor 1
    [IO.File]::WriteAllBytes($corrupt, $changed)
    Assert-Fails 'corrupt dependency' { Test-PayloadManifest $copy } '*integrity mismatch*'
}
finally { [IO.File]::WriteAllBytes($corrupt, $original) }

$depsPath = Join-Path $copy 'Doctor\Martlet.Doctor.deps.json'
$originalDeps = [IO.File]::ReadAllBytes($depsPath)
try {
    $changedDeps = [Text.Encoding]::UTF8.GetString($originalDeps).Replace('NAudio.Wasapi/3.1.0', 'NAudio.Wasapi/3.1.1')
    [IO.File]::WriteAllText($depsPath, $changedDeps)
    Assert-Fails 'wrong managed dependency version' { Test-PayloadManifest $copy } '*Missing or incorrect managed package*'
}
finally { [IO.File]::WriteAllBytes($depsPath, $originalDeps) }

$fakeCache = Join-Path $WorkDirectory 'invalid-package-cache'
$packagePath = Join-Path $fakeCache 'naudio.wasapi\3.1.0\naudio.wasapi.3.1.0.nupkg'
[IO.Directory]::CreateDirectory((Split-Path $packagePath)) | Out-Null
[IO.File]::WriteAllText($packagePath, 'Invalid archive fixture; not an actual package.')
$fakeAssets = [pscustomobject]@{ packageFolders = [pscustomobject]@{ $fakeCache = [pscustomobject]@{} } }
$audioPin = (Get-PackagingPins).managedPackages | Where-Object id -eq 'NAudio.Wasapi'
Assert-Fails 'corrupt managed package archive' {
    Get-VerifiedPackageArchive $fakeAssets $audioPin.id $audioPin.version $audioPin.sha512
} '*Package integrity failure*'

foreach ($relative in @('Doctor\settings.json', 'Desktop\data\private.txt', 'unrelated.txt')) {
    $path = Join-Path $copy $relative
    [IO.Directory]::CreateDirectory((Split-Path $path)) | Out-Null
    try {
        [IO.File]::WriteAllText($path, 'do not distribute private data')
        Assert-Fails "exclude $relative" { Write-PayloadManifest $copy $manifest.sourceCommit $manifest.sourceDirty } '*cannot be packaged*'
        Assert-Fails "installer excludes $relative" { Write-InstallerFileList $copy (Join-Path $WorkDirectory 'unsafe.iss') } '*cannot be packaged*'
    }
    finally { [IO.File]::Delete($path) }
}
foreach ($change in @('rid', 'runtimeVersion')) {
    try {
        $tampered = [Text.Encoding]::UTF8.GetString($originalManifest) | ConvertFrom-Json
        $tampered.$change = 'invalid'
        [IO.File]::WriteAllText($manifestPath, ($tampered | ConvertTo-Json -Depth 8))
        Assert-Fails "wrong $change" { Test-PayloadManifest $copy } '*metadata is invalid*'
    }
    finally { [IO.File]::WriteAllBytes($manifestPath, $originalManifest) }
}
$sums = Join-Path $copy 'SHA256SUMS.txt'
$originalSums = [IO.File]::ReadAllBytes($sums)
try {
    [IO.File]::AppendAllText($sums, 'corruption')
    Assert-Fails 'corrupt checksum file' { Test-PayloadManifest $copy } '*SHA256SUMS.txt does not match*'
}
finally { [IO.File]::WriteAllBytes($sums, $originalSums) }
$exe = Join-Path $copy 'Doctor\Martlet.Doctor.exe'
$originalExe = [IO.File]::ReadAllBytes($exe)
try {
    $changed = [byte[]]$originalExe.Clone()
    $pe = [BitConverter]::ToInt32($changed, 0x3C)
    $changed[$pe + 4] = 0x4C
    $changed[$pe + 5] = 0x01
    [IO.File]::WriteAllBytes($exe, $changed)
    Assert-Fails 'wrong PE architecture' { Test-PayloadManifest $copy } '*Wrong binary architecture*'
}
finally { [IO.File]::WriteAllBytes($exe, $originalExe) }

$list = Join-Path $WorkDirectory 'complete-files.iss'
$null = Write-InstallerFileList $copy $list
$lines = @(Get-Content -LiteralPath $list)
if ($lines.Count -ne $manifest.files.Count + 2 -or @($lines | Where-Object { $_ -match '\*|recursesubdirs|skipifsourcedoesntexist|external|uninsneveruninstall' }).Count -gt 0) {
    throw 'Installer must enumerate every checked file explicitly without wildcards, omission or uninstall bypasses.'
}
$script:cases++
$authoring = Get-Content -LiteralPath "$PSScriptRoot\Martlet.iss" -Raw
foreach ($required in @('PrivilegesRequired=lowest', 'UsePreviousAppDir=no', 'ArchitecturesAllowed=x64os',
        'MinVersion=10.0.26200', 'CloseApplications=no', 'RestartApplications=no',
        'DefaultDirName={localappdata}\Programs\Martlet Internal', 'INTERNAL DEVELOPMENT ONLY',
        'AppId={{CDFDFAB4-DAF1-4A6D-8823-A55E0A12CD86}')) {
    if (-not $authoring.Contains($required)) { throw "Installer safety invariant missing: $required" }
}
if ($authoring -match '(?im)^\[(Run|UninstallRun|Registry|InstallDelete|UninstallDelete|Tasks)\]|DelTree|DeleteFile|RegWrite|Exec\(') {
    throw 'Installer contains an unexpected mutation/deletion/autorun surface.'
}
$script:cases++
Assert-Fails 'no output overwrite' { New-OutputDirectory $copy } '*Output already exists*'
Assert-Fails 'wrong requested RID' { & "$PSScriptRoot\Publish-Windows.ps1" -RuntimeIdentifier win-arm64 -OutputDirectory $copy -CliHome $copy } '*does not belong to the set*'
Assert-Fails 'missing SDK' { Initialize-PackagingSdk (Join-Path $WorkDirectory 'missing-dotnet.exe') $WorkDirectory } '*not recognized*'
Assert-Fails 'wrong SDK version' { Initialize-PackagingSdk (Get-Command pwsh).Source $CliHome } '*Wrong SDK*'
$null = Initialize-PackagingSdk $DotnetPath $CliHome
Assert-Fails 'real publish failure propagates' { Invoke-Dotnet $sdk @('publish', (Join-Path $WorkDirectory 'missing.csproj')) } '*failed (exit 1)*' 'MSB1009'
Assert-Fails 'nonfilesystem working directory' {
    Invoke-BoundedProcess $sdk @('--version') -WorkingDirectory 'Env:\'
} '*require a filesystem working directory*'
Assert-Fails 'file instead of working directory' {
    Invoke-BoundedProcess $sdk @('--version') -WorkingDirectory $PSCommandPath
} '*require a filesystem working directory*'
Push-Location 'Env:\'
try {
    Assert-Fails 'nonfilesystem PowerShell location' {
        Invoke-Dotnet $sdk @('--version')
    } '*require a filesystem working directory*'
}
finally { Pop-Location }
foreach ($name in @('comma,name', 'semicolon;name', 'percent%2Cname', 'percent%3Bname', 'percent%25name', 'equal=name', 'mixed,;%2C=name')) {
    $unsupported = Join-Path $WorkDirectory $name
    Assert-Fails "unsupported output path $name" {
        & "$PSScriptRoot\Publish-Windows.ps1" -DotnetPath $sdk -CliHome $CliHome -OutputDirectory $unsupported
    } '*Unsupported MSBuild path character*'
    if (Test-Path -LiteralPath $unsupported) { throw 'Rejected publish path was created before preflight failed.' }
    Assert-Fails "unsupported lock-maintenance path $name" {
        & "$PSScriptRoot\Update-PublishLocks.ps1" -DotnetPath $sdk -CliHome $CliHome -WorkDirectory $unsupported
    } '*Unsupported MSBuild path character*'
    if (Test-Path -LiteralPath $unsupported) { throw 'Rejected maintenance path was created before preflight failed.' }
}
foreach ($name in @('source,comma', 'source;semicolon', 'source%2Cescape', 'source=equals')) {
    $scripts = Join-Path $WorkDirectory "$name\packaging\windows"
    [IO.Directory]::CreateDirectory($scripts) | Out-Null
    foreach ($file in @('Publish-Windows.ps1', 'Packaging.Common.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $scripts
    }
    $output = Join-Path $WorkDirectory 'rejected-source-output'
    Assert-Fails "unsupported repository path $name" {
        & "$scripts\Publish-Windows.ps1" -DotnetPath $sdk -CliHome $CliHome -OutputDirectory $output
    } '*Unsupported MSBuild path character*'
    if (Test-Path -LiteralPath $output) { throw 'Rejected repository path created publish output.' }
}

# Use copied project inputs, never mutate the real repository's lock files for negative tests.
$repo = Split-Path (Split-Path $PSScriptRoot)
$source = Join-Path $WorkDirectory ("source graph $([char]0x00E9)")
[IO.Directory]::CreateDirectory($source) | Out-Null
foreach ($file in @('Directory.Build.props', 'Directory.Packages.props', 'NuGet.config', 'global.json')) {
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $source
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $destination = Join-Path $source ([IO.Path]::GetRelativePath($repo, $file.FullName))
    [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$packaging = Join-Path $source 'packaging\windows'
[IO.Directory]::CreateDirectory($packaging) | Out-Null
foreach ($file in @('Packaging.targets', 'Packaging.Common.ps1', 'Update-PublishLocks.ps1', 'toolchain.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $packaging
}
Copy-Item -LiteralPath "$PSScriptRoot\locks" -Destination $packaging -Recurse
$properties = @("-p:CustomBeforeMicrosoftCommonTargets=$packaging\Packaging.targets",
    "-p:CustomBeforeMicrosoftCommonCrossTargetingTargets=$packaging\Packaging.targets", '-p:PublishProfile=WindowsInternal')
$project = Join-Path $source 'src\Martlet.Desktop\Martlet.Desktop.csproj'
$graph = Join-Path $WorkDirectory 'negative.restore-graph.json'
$build = Join-Path $WorkDirectory 'negative-build'
Invoke-Dotnet $sdk (@('msbuild', $project, '-t:GenerateRestoreGraphFile', "-p:RestoreGraphOutputPath=$graph",
    '-p:RuntimeIdentifier=win-x64', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$build", '-verbosity:quiet') + $properties) -WorkingDirectory $source
$lock = Join-Path $packaging 'locks\Martlet.Core.packages.lock.json'
$lockBytes = [IO.File]::ReadAllBytes($lock)
try {
    [IO.File]::Delete($lock)
    Assert-Fails 'missing RID lock preflight' { Assert-RestoreGraphLocks $graph (Join-Path $packaging 'locks') } '*Missing committed RID lock*'
    [IO.File]::WriteAllText($lock, '{"version":2,"dependencies":{"net10.0":{}}}')
    Assert-Fails 'stale RID lock restore' {
        Invoke-Dotnet $sdk (@('restore', $project, '--locked-mode', '-r', 'win-x64', '--artifacts-path', $build, '--verbosity', 'quiet') + $properties) -WorkingDirectory $source
    } '*failed (exit 1)*' 'NU1004'
}
finally { [IO.File]::WriteAllBytes($lock, $lockBytes) }

$osLocation = [Environment]::CurrentDirectory
$psLocation = (Get-Location).Path
foreach ($implicitSource in @($false, $true)) {
    $maintenance = Join-Path $WorkDirectory "location-maintenance-$implicitSource"
    $arguments = @('-NoProfile', '-NonInteractive', '-File', "$PSScriptRoot\Test-MaintenanceLocation.ps1",
        '-SourceDirectory', $source, '-WorkDirectory', $maintenance, '-DotnetPath', $sdk, '-CliHome', $CliHome)
    if ($implicitSource) { $arguments += '-ExerciseImplicitSource' }
    $result = Invoke-BoundedProcess (Get-Command pwsh).Source $arguments 600 -WorkingDirectory $WorkDirectory
    if ($result.Stdout) { $result.Stdout.TrimEnd() | Out-Host }
    if ($result.ExitCode -ne 0) { throw "Maintenance from a different OS working directory failed: $($result.Stderr)" }
    if ([Environment]::CurrentDirectory -cne $osLocation -or (Get-Location).Path -cne $psLocation) {
        throw 'Bounded child changed the caller working directory.'
    }
    foreach ($file in Get-ChildItem -LiteralPath "$PSScriptRoot\locks" -File) {
        Assert-Sha256 (Join-Path $packaging "locks\$($file.Name)") (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    $script:cases++
}

if ($ComparePayloadRoot) {
    $null = Test-PayloadManifest $ComparePayloadRoot
    Assert-Sha256 (Join-Path $ComparePayloadRoot 'manifest.json') (Get-FileHash -LiteralPath (Join-Path $PayloadRoot 'manifest.json') -Algorithm SHA256).Hash
    $script:cases++
}
if ($BuilderDirectory) {
    $null = Get-VerifiedInnoCompiler $BuilderDirectory
    Assert-Fails 'missing or unverified compiler' { Get-VerifiedInnoCompiler $WorkDirectory } '*Required file missing*'
    $wrongBuilder = Join-Path $WorkDirectory 'wrong-builder'
    [IO.Directory]::CreateDirectory($wrongBuilder) | Out-Null
    [IO.File]::WriteAllText((Join-Path $wrongBuilder 'builder-receipt.json'), '{"version":"9.9.9"}')
    Assert-Fails 'wrong compiler receipt' { Get-VerifiedInnoCompiler $wrongBuilder } '*SHA-256 integrity failure*'
}
$null = Test-PayloadManifest $PayloadRoot
Write-Output "PASS: $script:cases packaging assertions against production publish/manifest/installer functions."
