#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PackagingPins {
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw | ConvertFrom-Json
}

function Assert-PackagingHost {
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
        throw 'Packaging requires Windows x64 with PowerShell 7; ARM64/emulated builds are not qualified.'
    }
}

function Get-RequiredFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file missing: $Path. Rebuild the complete payload; do not copy selected binaries."
    }
    Get-Item -LiteralPath $Path
}

function Assert-Sha256([string]$Path, [string]$Expected) {
    $null = Get-RequiredFile $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) {
        throw "SHA-256 integrity failure: $Path. Discard this artifact and rebuild or reacquire the pinned tool."
    }
}

function New-OutputDirectory([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw 'Output paths must be absolute.'
    }
    if (Test-Path -LiteralPath $Path) {
        throw "Output already exists: $Path. Choose a new directory; existing work is never deleted or reused."
    }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
}

function Assert-MSBuildPath([string]$Path) {
    if ($Path -match '[,;%=]') {
        throw 'Unsupported MSBuild path character: comma, semicolon, percent and equals are not supported. Choose a repository/output path without these characters; no output was created.'
    }
}

function Assert-X64Pe([string]$Path) {
    $null = Get-RequiredFile $Path
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a Windows PE file: $Path" }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt ($stream.Length - 6)) { throw "Invalid PE header: $Path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "Wrong binary architecture: $Path. Expected native win-x64, not x86 or ARM64."
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-PayloadFiles([string]$Root) {
    $directory = Get-Item -LiteralPath $Root
    if (-not $directory.PSIsContainer) { throw "Payload is not a directory: $Root" }
    $items = @($directory) + @(Get-ChildItem -LiteralPath $Root -Recurse -Force)
    foreach ($item in $items) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse points are not allowed in a distributable payload: $($item.FullName)"
        }
    }
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($file in $items | Where-Object { -not $_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($directory.FullName, $file.FullName)
        if ($relative -in @('manifest.json', 'SHA256SUMS.txt')) { continue }
        if ($relative -notmatch '^(Desktop|Doctor|help|notices)\\' -or
            $relative -match '(^|\\)(settings[^\\]*\.json|[^\\]*\.lock|data|logs|models|recordings|profiles)(\\|$)') {
            throw "Unowned or mutable data cannot be packaged: $relative"
        }
        $paths.Add($relative)
    }
    $paths.Sort([StringComparer]::Ordinal)
    foreach ($relative in $paths) {
        $file = Get-Item -LiteralPath (Join-Path $Root $relative)
        [ordered]@{
            path = $relative
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

function Get-PublishProjectNames([ValidateSet('Desktop', 'Doctor')][string]$Entry) {
    @("Martlet.$Entry", 'Martlet.Core', 'Martlet.Diagnostics', 'Martlet.Fixtures', 'Martlet.Sessions', 'Martlet.Audio')
    if ($Entry -eq 'Desktop') {
        @('Martlet.Credentials.Windows', 'Martlet.Conversation', 'Martlet.Providers', 'Martlet.Participation')
    }
}

function Assert-PublishLayout([string]$Root) {
    $pins = Get-PackagingPins
    $versions = @()
    foreach ($entry in @('Desktop', 'Doctor')) {
        $directory = Join-Path $Root $entry
        $name = "Martlet.$entry"
        $projects = @(Get-PublishProjectNames $entry)
        foreach ($project in $projects) {
            $null = Get-RequiredFile (Join-Path $directory "$project.dll")
        }
        $publishedProjects = @(Get-ChildItem -LiteralPath $directory -File -Filter 'Martlet.*.dll' |
            Select-Object -ExpandProperty BaseName)
        if (@(Compare-Object $projects $publishedProjects -CaseSensitive).Count -ne 0) {
            throw "Unexpected Martlet assembly in $entry. Package only its reviewed project graph."
        }
        foreach ($file in @("$name.exe", "$name.deps.json", "$name.runtimeconfig.json",
                'coreclr.dll', 'hostfxr.dll',
                'hostpolicy.dll', 'System.Private.CoreLib.dll', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt')) {
            $null = Get-RequiredFile (Join-Path $directory $file)
        }
        foreach ($file in @("$name.exe", 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
            Assert-X64Pe (Join-Path $directory $file)
        }
        $config = Get-Content -LiteralPath (Join-Path $directory "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
        $options = $config.runtimeOptions
        if ($options.PSObject.Properties.Name -contains 'framework' -or
            $options.PSObject.Properties.Name -contains 'frameworks' -or
            $options.PSObject.Properties.Name -notcontains 'includedFrameworks') {
            throw "$entry is not self-contained. Publish with the WindowsInternal profile."
        }
        $expectedFrameworks = @('Microsoft.NETCore.App')
        if ($entry -eq 'Desktop') { $expectedFrameworks += 'Microsoft.WindowsDesktop.App' }
        if (@(Compare-Object $expectedFrameworks @($options.includedFrameworks.name)).Count -ne 0) {
            throw "Unexpected included runtime frameworks for $entry."
        }
        foreach ($framework in $options.includedFrameworks) {
            if ($framework.version -cne $pins.runtimeVersion) { throw "Wrong runtime version in $entry." }
        }
        $deps = Get-Content -LiteralPath (Join-Path $directory "$name.deps.json") -Raw | ConvertFrom-Json
        if (-not $deps.runtimeTarget.name.EndsWith("/$($pins.rid)", [StringComparison]::Ordinal)) {
            throw "Wrong RID in $entry dependency manifest; expected $($pins.rid)."
        }
        $target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
        $projectLibraries = @($deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' })
        $dependencyProjects = @($projectLibraries | ForEach-Object { ($_.Name -split '/')[0] })
        if (@(Compare-Object $projects $dependencyProjects -CaseSensitive).Count -ne 0) {
            throw "Missing or unexpected project dependency in $entry. Package only its reviewed project graph."
        }
        foreach ($library in $projectLibraries) {
            $project = ($library.Name -split '/')[0]
            $projectTarget = $target.PSObject.Properties[$library.Name]
            if ($null -eq $projectTarget -or
                $projectTarget.Value.PSObject.Properties.Name -notcontains 'runtime' -or
                $null -eq $projectTarget.Value.runtime.PSObject.Properties["$project.dll"]) {
                throw "Missing project runtime asset $project.dll in $entry dependency manifest."
            }
        }
        foreach ($package in $pins.managedPackages) {
            if ($null -eq $target.PSObject.Properties["$($package.id)/$($package.version)"]) {
                throw "Missing or incorrect managed package $($package.id) $($package.version) in $entry."
            }
            $null = Get-RequiredFile (Join-Path $directory ([IO.Path]::GetFileName($package.runtimeAsset)))
        }
        foreach ($library in $target.PSObject.Properties) {
            foreach ($kind in @('runtime', 'native', 'resources')) {
                if ($library.Value.PSObject.Properties.Name -notcontains $kind) { continue }
                foreach ($asset in $library.Value.$kind.PSObject.Properties) {
                    $assetName = [IO.Path]::GetFileName($asset.Name)
                    if ($assetName -eq '_._') { continue }
                    if ($kind -eq 'resources') {
                        $assetName = Join-Path $asset.Value.locale $assetName
                    }
                    $null = Get-RequiredFile (Join-Path $directory $assetName)
                    if ($kind -eq 'native' -and [IO.Path]::GetExtension($assetName) -in @('.dll', '.exe')) {
                        Assert-X64Pe (Join-Path $directory $assetName)
                    }
                }
            }
        }
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $directory "$name.exe"))
        $versions += $version.FileVersion
        if ($version.FileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Missing application version in $entry." }
        $runtime = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $directory 'coreclr.dll'))
        $nativeVersion = "$($runtime.FileMajorPart).$($runtime.FileMinorPart).$($runtime.FileBuildPart).$($runtime.FilePrivatePart)"
        if ($nativeVersion -cne $pins.nativeRuntimeFileVersion) {
            throw "Native runtime version does not match $($pins.runtimeVersion) in $entry."
        }
    }
    if ($versions[0] -cne $versions[1]) { throw 'Desktop and Doctor versions do not agree.' }
    foreach ($file in @('PresentationFramework.dll', 'PresentationCore.dll', 'wpfgfx_cor3.dll', 'D3DCompiler_47_cor3.dll')) {
        $null = Get-RequiredFile (Join-Path $Root "Desktop\$file")
    }
    foreach ($file in @('help\INTERNAL.txt', 'notices\DEPENDENCIES.txt',
            'notices\NAudio-THIRD-PARTY-NOTICES.txt',
            'notices\Microsoft.WindowsDesktop.App\LICENSE.txt', 'notices\WPF-THIRD-PARTY-NOTICES.txt',
            'notices\WinForms-THIRD-PARTY-NOTICES.txt', 'notices\Inno-Setup-LICENSE.txt')) {
        $null = Get-RequiredFile (Join-Path $Root $file)
    }
    foreach ($notice in $pins.notices) {
        $null = Get-RequiredFile (Join-Path $Root "notices\$($notice.file)")
    }
    foreach ($package in $pins.managedPackages) {
        foreach ($notice in $package.notices) {
            $null = Get-RequiredFile (Join-Path $Root "notices\$($notice.file)")
        }
    }
    return $versions[0]
}

function Get-ChecksumText([object[]]$Files, [string]$ManifestHash) {
    $lines = @($Files | ForEach-Object { "$($_.sha256)  $($_.path)" })
    $lines += "$($ManifestHash.ToLowerInvariant())  manifest.json"
    return ($lines -join "`n") + "`n"
}

function Write-PayloadManifest([string]$Root, [string]$SourceCommit, [bool]$SourceDirty) {
    $pins = Get-PackagingPins
    $version = Assert-PublishLayout $Root
    $files = @(Get-PayloadFiles $Root)
    $manifest = [ordered]@{
        schemaVersion = 1
        channel = 'INTERNAL DEVELOPMENT ONLY - UNSIGNED'
        applicationVersion = $version
        rid = $pins.rid
        sdkVersion = $pins.sdkVersion
        runtimeVersion = $pins.runtimeVersion
        sourceCommit = $SourceCommit
        sourceDirty = $SourceDirty
        files = $files
    }
    $path = Join-Path $Root 'manifest.json'
    [IO.File]::WriteAllText($path, ($manifest | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
    $text = Get-ChecksumText $files (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $Root 'SHA256SUMS.txt'), $text, [Text.UTF8Encoding]::new($false))
}

function Test-PayloadManifest([string]$Root) {
    $pins = Get-PackagingPins
    $version = Assert-PublishLayout $Root
    $manifestPath = (Get-RequiredFile (Join-Path $Root 'manifest.json')).FullName
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.channel -cne 'INTERNAL DEVELOPMENT ONLY - UNSIGNED' -or
        $manifest.rid -cne $pins.rid -or $manifest.sdkVersion -cne $pins.sdkVersion -or
        $manifest.runtimeVersion -cne $pins.runtimeVersion -or $manifest.applicationVersion -cne $version -or
        $manifest.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $manifest.sourceDirty -isnot [bool]) {
        throw 'Payload manifest metadata is invalid or differs from the pinned build.'
    }
    $actual = @(Get-PayloadFiles $Root)
    if ($manifest.files.Count -ne $actual.Count) { throw 'Payload file count differs from manifest; rebuild the complete payload.' }
    for ($index = 0; $index -lt $actual.Count; $index++) {
        $expected = $manifest.files[$index]
        if ($expected.path -cne $actual[$index].path -or $expected.bytes -ne $actual[$index].bytes -or
            $expected.sha256 -cne $actual[$index].sha256) {
            throw "Payload integrity mismatch: $($actual[$index].path). Rebuild; do not regenerate a manifest over damaged files."
        }
    }
    $sumsPath = (Get-RequiredFile (Join-Path $Root 'SHA256SUMS.txt')).FullName
    $expectedText = Get-ChecksumText $actual (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if ([IO.File]::ReadAllText($sumsPath) -cne $expectedText) { throw 'SHA256SUMS.txt does not match the payload and manifest.' }
    return $manifest
}

function Invoke-BoundedProcess(
    [string]$Executable,
    [string[]]$Arguments,
    [int]$TimeoutSeconds = 30,
    [string]$WorkingDirectory = (Get-Location).Path
) {
    $location = Resolve-Path -LiteralPath $WorkingDirectory -ErrorAction Stop
    if ($location.Provider.Name -ne 'FileSystem' -or -not (Get-Item -LiteralPath $location.Path).PSIsContainer) {
        throw 'Native processes require a filesystem working directory. Use Set-Location to a directory or supply -WorkingDirectory with an existing filesystem directory.'
    }
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $location.ProviderPath
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not launch $Executable." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Process exceeded $TimeoutSeconds seconds: $Executable"
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout.GetAwaiter().GetResult(); Stderr = $stderr.GetAwaiter().GetResult() }
    }
    finally {
        $process.Dispose()
    }
}

function Initialize-PackagingSdk([string]$DotnetPath, [string]$CliHome, [string]$WorkingDirectory = (Get-Location).Path) {
    Assert-PackagingHost
    $sdk = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
    $env:DOTNET_ROOT = Split-Path $sdk
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    $env:DOTNET_CLI_HOME = $CliHome
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_NOLOGO = '1'
    $result = Invoke-BoundedProcess $sdk @('--version') -WorkingDirectory $WorkingDirectory
    if ($result.ExitCode -ne 0 -or $result.Stdout.Trim() -cne (Get-PackagingPins).sdkVersion) {
        throw "Wrong SDK. Use the exact SDK $((Get-PackagingPins).sdkVersion) from global.json; detected '$($result.Stdout.Trim())'."
    }
    return $sdk
}

function Invoke-Dotnet([string]$Sdk, [string[]]$Arguments, [string]$WorkingDirectory = (Get-Location).Path) {
    # Keep a verified expected failure from leaking into GitHub's pwsh LASTEXITCODE wrapper.
    $result = Invoke-BoundedProcess $Sdk $Arguments 600 -WorkingDirectory $WorkingDirectory
    if ($result.Stdout) { $result.Stdout.TrimEnd() | Out-Host }
    if ($result.Stderr) { $result.Stderr.TrimEnd() | Out-Host }
    if ($result.ExitCode -ne 0) {
        $failure = [InvalidOperationException]::new("dotnet $($Arguments[0]) failed (exit $($result.ExitCode)). No completed payload was produced; inspect the build/restore errors above.")
        $failure.Data['NativeOutput'] = $result.Stdout + $result.Stderr
        throw $failure
    }
}

function Get-PackagingProperties {
    @(
        "-p:CustomBeforeMicrosoftCommonTargets=$(Join-Path $PSScriptRoot 'Packaging.targets')"
        "-p:CustomBeforeMicrosoftCommonCrossTargetingTargets=$(Join-Path $PSScriptRoot 'Packaging.targets')"
        '-p:PublishProfile=WindowsInternal'
        '-p:DebugType=None'
        '-p:DebugSymbols=false'
        '-p:ContinuousIntegrationBuild=true'
    )
}

function Assert-RestoreGraphLocks([string]$GraphPath, [string]$LocksDirectory = "$PSScriptRoot\locks") {
    $graph = Get-Content -LiteralPath $GraphPath -Raw | ConvertFrom-Json
    foreach ($project in $graph.projects.PSObject.Properties.Value) {
        $expected = Join-Path $LocksDirectory "$($project.restore.projectName).packages.lock.json"
        if ($project.restore.restoreLockProperties.PSObject.Properties.Name -notcontains 'nuGetLockFilePath' -or
            $project.restore.restoreLockProperties.nuGetLockFilePath -ine $expected) {
            throw "Packaging lock import was not applied to $($project.restore.projectName)."
        }
        if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) {
            throw "Missing committed RID lock: $expected. Run Update-PublishLocks.ps1 deliberately and review its diff."
        }
    }
}

function Get-VerifiedPackageArchive($Assets, [string]$Id, [string]$Version, [string]$Sha512) {
    $lowerId = $Id.ToLowerInvariant()
    foreach ($base in $Assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $base "$lowerId\$Version\$lowerId.$Version.nupkg"
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA512).Hash -ine $Sha512) {
            throw "Package integrity failure: $Id. Do not package this cache."
        }
        return $candidate
    }
    throw "Required package archive missing: $Id. Restore with the pinned SDK."
}

function Copy-RuntimeNotices([string]$Root, [string]$AssetsPath) {
    $pins = Get-PackagingPins
    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    foreach ($package in $pins.runtimePackages) {
        $archivePath = Get-VerifiedPackageArchive $assets $package.id $pins.runtimeVersion $package.sha512
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $destinations = if ($package.id -like 'microsoft.netcore.*') { @('Desktop', 'Doctor') }
                else { @('notices\Microsoft.WindowsDesktop.App') }
            foreach ($destination in $destinations) {
                $directory = Join-Path $Root $destination
                [IO.Directory]::CreateDirectory($directory) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($package.license), (Join-Path $directory 'LICENSE.txt'))
                if ($package.id -like 'microsoft.netcore.*') {
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('THIRD-PARTY-NOTICES.TXT'), (Join-Path $directory 'THIRD-PARTY-NOTICES.txt'))
                }
            }
            $packAssets = @{}
            foreach ($application in @('Desktop', 'Doctor')) {
                $deps = Get-Content -LiteralPath (Join-Path $Root "$application\Martlet.$application.deps.json") -Raw | ConvertFrom-Json
                $target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
                $owner = $target.PSObject.Properties["runtimepack.$($package.id)/$($pins.runtimeVersion)"]
                $packAssets[$application] = if ($null -eq $owner) { @() }
                    else { @($owner.Value.PSObject.Properties | ForEach-Object { $_.Value.PSObject.Properties.Name }) }
            }
            foreach ($entry in $archive.Entries) {
                if ($entry.FullName -notmatch '^runtimes/win-x64/(native|lib/net10\.0)/(.+)$') { continue }
                $relative = $Matches[2].Replace('/', '\')
                foreach ($application in @('Desktop', 'Doctor')) {
                    $published = Join-Path $Root "$application\$relative"
                    if (-not (Test-Path -LiteralPath $published -PathType Leaf)) { continue }
                    # WindowsDesktop overrides some Core runtime facade assemblies, e.g. WindowsBase.
                    if ($relative.Replace('\', '/') -notin $packAssets[$application]) { continue }
                    $stream = $entry.Open()
                    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
                    finally { $stream.Dispose() }
                    Assert-Sha256 $published $hash
                }
            }
        }
        finally { $archive.Dispose() }
    }
    foreach ($package in $pins.managedPackages) {
        $archivePath = Get-VerifiedPackageArchive $assets $package.id $package.version $package.sha512
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $entry = $archive.GetEntry($package.runtimeAsset)
            if ($null -eq $entry) { throw "Pinned managed asset missing: $($package.runtimeAsset)." }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            foreach ($application in @('Desktop', 'Doctor')) {
                Assert-Sha256 (Join-Path $Root "$application\$([IO.Path]::GetFileName($package.runtimeAsset))") $hash
            }
            foreach ($notice in $package.notices) {
                $entry = $archive.GetEntry($notice.source)
                if ($null -eq $entry) { throw "Pinned package notice missing: $($package.id) $($notice.source)." }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $Root "notices\$($notice.file)"))
            }
        }
        finally { $archive.Dispose() }
    }
    foreach ($notice in $pins.notices) {
        $path = Join-Path $Root "notices\$($notice.file)"
        Invoke-WebRequest -Uri $notice.url -OutFile $path
        Assert-Sha256 $path $notice.sha256
    }
}
