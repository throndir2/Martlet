#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PackagingPins {
    Read-PackagingJson (Join-Path $PSScriptRoot 'toolchain.json')
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

function Get-PayloadFiles([string]$Root, [switch]$ExcludeSbom) {
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
        Assert-EvidenceRelativePath $relative
        if ($relative -ceq 'sbom.cdx.json') {
            if ($ExcludeSbom) { continue }
        }
        elseif ($relative -notmatch '^(Desktop|Doctor|help|notices)\\' -or
            $relative -match '(^|\\)(settings[^\\]*\.json|[^\\]*\.lock|data|logs|models|recordings|profiles)(\\|$)') {
            throw "Unowned or mutable data cannot be packaged: $relative"
        }
        $paths.Add($relative)
        if ($paths.Count -gt 8192) { throw 'Payload file inventory exceeds the 8192-file evidence bound.' }
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

function Get-PublishDependencyManifest([string]$Root, [string]$Entry) {
    $deps = Read-PackagingJson (Join-Path $Root "$Entry\Martlet.$Entry.deps.json")
    if (-not $deps.runtimeTarget.name.EndsWith("/$((Get-PackagingPins).rid)", [StringComparison]::Ordinal) -or
        $null -eq $deps.targets.PSObject.Properties[$deps.runtimeTarget.name]) {
        throw "Wrong RID or missing target in $Entry dependency manifest."
    }
    return $deps
}

function Get-PublishedAssetPath([string]$Entry, [string]$Kind, [string]$Name, $Metadata) {
    Assert-EvidenceRelativePath $Name.Replace('/', '\')
    $relative = [IO.Path]::GetFileName($Name)
    if ($Kind -ceq 'resources') {
        Assert-EvidenceRelativePath $Metadata.locale
        $relative = Join-Path $Metadata.locale $relative
    }
    return "$Entry\$relative"
}

function Get-PublishProjectNames([ValidateSet('Desktop', 'Doctor')][string]$Entry) {
    @("Martlet.$Entry", 'Martlet.Core', 'Martlet.Diagnostics', 'Martlet.Fixtures', 'Martlet.Sessions', 'Martlet.Audio')
    if ($Entry -eq 'Desktop') {
        @('Martlet.Credentials.Windows', 'Martlet.Conversation', 'Martlet.Providers', 'Martlet.Participation', 'Martlet.Support')
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
        $config = Read-PackagingJson (Join-Path $directory "$name.runtimeconfig.json")
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
        $deps = Get-PublishDependencyManifest $Root $entry
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
                    if ([IO.Path]::GetFileName($asset.Name) -eq '_._') { continue }
                    $assetPath = Join-Path $Root (Get-PublishedAssetPath $entry $kind $asset.Name $asset.Value)
                    $null = Get-RequiredFile $assetPath
                    if ($kind -eq 'native' -and [IO.Path]::GetExtension($assetPath) -in @('.dll', '.exe')) {
                        Assert-X64Pe $assetPath
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
    foreach ($file in @('help\INTERNAL.txt', 'help\TROUBLESHOOTING.md', 'notices\DEPENDENCIES.txt',
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

function Write-PayloadManifest([string]$Root, [string]$SourceCommit, [bool]$SourceDirty, $Provenance) {
    $pins = Get-PackagingPins
    $version = Assert-PublishLayout $Root
    $files = @(Get-PayloadFiles $Root)
    Test-PackageProvenance $Root $Provenance
    if ($Provenance.source.commit -cne $SourceCommit -or $Provenance.source.dirty -ne $SourceDirty) {
        throw 'Payload source metadata differs from provenance.'
    }
    Test-PackageSbom $Root $Provenance
    $manifest = New-PayloadManifestDocument $version $SourceCommit $SourceDirty $Provenance $files $pins
    $path = Join-Path $Root 'manifest.json'
    $text = ConvertTo-EvidenceJson $manifest
    if ([Text.Encoding]::UTF8.GetByteCount($text) -gt 16MB) { throw 'Manifest exceeds the 16 MiB evidence bound.' }
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    $text = Get-ChecksumText $files (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $Root 'SHA256SUMS.txt'), $text, [Text.UTF8Encoding]::new($false))
}

function New-PayloadManifestDocument([string]$ApplicationVersion, [string]$SourceCommit,
    [bool]$SourceDirty, $Provenance, [object[]]$Files, $Pins) {
    return [ordered]@{
        schemaVersion = 2
        channel = 'INTERNAL DEVELOPMENT ONLY - UNSIGNED'
        applicationVersion = $ApplicationVersion
        rid = $Pins.rid
        sdkVersion = $Pins.sdkVersion
        runtimeVersion = $Pins.runtimeVersion
        sourceCommit = $SourceCommit
        sourceDirty = $SourceDirty
        provenance = $Provenance
        files = $Files
    }
}

function Test-PayloadManifest([string]$Root, [switch]$RequireCurrentSource) {
    $pins = Get-PackagingPins
    $version = Assert-PublishLayout $Root
    $manifestPath = (Get-RequiredFile (Join-Path $Root 'manifest.json')).FullName
    $manifest = Read-PackagingJson $manifestPath -AsHashtable
    if ($manifest.schemaVersion -ne 2) { throw 'Payload evidence requires manifest schema v2. Rebuild the complete payload.' }
    Assert-EvidenceKeys $manifest @('schemaVersion', 'channel', 'applicationVersion', 'rid', 'sdkVersion',
        'runtimeVersion', 'sourceCommit', 'sourceDirty', 'provenance', 'files')
    if ($manifest.schemaVersion -isnot [long] -or $manifest.channel -cne 'INTERNAL DEVELOPMENT ONLY - UNSIGNED' -or
        $manifest.rid -cne $pins.rid -or $manifest.sdkVersion -cne $pins.sdkVersion -or
        $manifest.runtimeVersion -cne $pins.runtimeVersion -or $manifest.applicationVersion -cne $version -or
        $manifest.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $manifest.sourceDirty -isnot [bool]) {
        throw 'Payload manifest metadata is invalid or differs from the pinned build.'
    }
    $null = Get-RequiredFile (Join-Path $Root 'sbom.cdx.json')
    $actual = @(Get-PayloadFiles $Root)
    if ($manifest.files.Count -ne $actual.Count) { throw 'Payload file count differs from manifest; rebuild the complete payload.' }
    for ($index = 0; $index -lt $actual.Count; $index++) {
        $expected = $manifest.files[$index]
        Assert-EvidenceFileRecord $expected
        if ($expected.path -cne $actual[$index].path -or $expected.bytes -ne $actual[$index].bytes -or
            $expected.sha256 -cne $actual[$index].sha256) {
            throw "Payload integrity mismatch: $($actual[$index].path). Rebuild; do not regenerate a manifest over damaged files."
        }
    }
    $sumsPath = (Get-RequiredFile (Join-Path $Root 'SHA256SUMS.txt')).FullName
    if ((Get-Item -LiteralPath $sumsPath).Length -gt 16MB) { throw 'Checksums exceed the 16 MiB evidence bound.' }
    $expectedText = Get-ChecksumText $actual (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if ([IO.File]::ReadAllText($sumsPath) -cne $expectedText) { throw 'SHA256SUMS.txt does not match the payload and manifest.' }
    Test-PackageProvenance $Root $manifest.provenance
    if ($manifest.sourceCommit -cne $manifest.provenance.source.commit -or
        $manifest.sourceDirty -ne $manifest.provenance.source.dirty) {
        throw 'Payload source metadata differs from provenance.'
    }
    Test-PackageSbom $Root $manifest.provenance
    if ($RequireCurrentSource) { Assert-PackagingSourceReceipt $manifest.provenance.source }
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
    $graph = Read-PackagingJson $GraphPath
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
    $assets = Read-PackagingJson $AssetsPath
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
                $deps = Get-PublishDependencyManifest $Root $application
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

function Get-PackageApplications([string]$Root) {
    foreach ($application in @('Desktop', 'Doctor')) {
        $deps = Get-PublishDependencyManifest $Root $application
        $target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
        $keys = @(Get-EvidenceOrdinalStrings @($deps.libraries.PSObject.Properties.Name))
        if ($keys.Count -gt 2048 -or
            (Get-EvidenceSha256 $keys) -cne (Get-EvidenceSha256 @(Get-EvidenceOrdinalStrings @($target.PSObject.Properties.Name)))) {
            throw "Dependency library/target inventory differs or exceeds its bound in $application."
        }
        $owners = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $libraries = @(
            foreach ($key in $keys) {
                if ($key.Length -gt 256 -or $key -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.+-]+$') {
                    throw "Unsupported dependency identity: $key"
                }
                $metadata = $deps.libraries.PSObject.Properties[$key].Value
                $node = $target.PSObject.Properties[$key].Value
                if ($metadata.type -cnotin @('package', 'project', 'runtimepack') -or
                    @($node.PSObject.Properties.Name | Where-Object { $_ -cnotin @('dependencies', 'runtime', 'native', 'resources') }).Count) {
                    throw "Unsupported dependency type or asset category: $key"
                }
                $dependencies = @(
                    if ($node.PSObject.Properties.Name -ccontains 'dependencies') {
                        foreach ($id in Get-EvidenceOrdinalStrings @($node.dependencies.PSObject.Properties.Name)) {
                            $resolved = "$id/$($node.dependencies.PSObject.Properties[$id].Value)"
                            if ($keys -cnotcontains $resolved) { throw "Unresolved dependency edge: $key -> $resolved" }
                            $resolved
                        }
                    }
                )
                $assets = @(
                    foreach ($kind in @('native', 'resources', 'runtime')) {
                        if ($node.PSObject.Properties.Name -cnotcontains $kind) { continue }
                        foreach ($name in Get-EvidenceOrdinalStrings @($node.$kind.PSObject.Properties.Name)) {
                            if ([IO.Path]::GetFileName($name) -ceq '_._') { continue }
                            $path = Get-PublishedAssetPath $application $kind $name $node.$kind.PSObject.Properties[$name].Value
                            if (-not $owners.Add($path)) { throw "Ambiguous dependency asset ownership: $path" }
                            $null = Get-RequiredFile (Join-Path $Root $path)
                            [ordered]@{ path = $path; kind = $kind; source = $name }
                        }
                    }
                )
                $contentHash = if ($metadata.type -ceq 'package') {
                    if ($metadata.sha512 -cnotmatch '^sha512-([A-Za-z0-9+/]{86}==)$') {
                        throw "Invalid NuGet content hash in published dependency: $key"
                    }
                    $Matches[1]
                } else { $null }
                [ordered]@{ key = $key; type = $metadata.type; contentHash = $contentHash; dependencies = $dependencies; assets = $assets }
            }
        )
        [ordered]@{ name = $application; target = $deps.runtimeTarget.name; libraries = $libraries }
    }
}

function Get-ResolvedEvidenceLibraries($Target, $Libraries) {
    $byId = @{}
    foreach ($key in $Target.Keys) {
        $id = $key.Split('/')[0]
        if ($byId.ContainsKey($id)) { throw "Duplicate resolved dependency identity: $id" }
        $byId[$id] = $key
    }
    foreach ($key in Get-EvidenceOrdinalStrings @($Target.Keys)) {
        $node = $Target[$key]
        if ($node.type -cnotin @('package', 'project')) { throw "Unsupported restore library: $key" }
        $dependencies = @(
            if ($node.Contains('dependencies')) {
                foreach ($id in Get-EvidenceOrdinalStrings @($node.dependencies.Keys)) {
                    if (-not $byId.ContainsKey($id)) { throw "Unresolved restore dependency: $key -> $id" }
                    $byId[$id]
                }
            }
        )
        $hash = if ($node.type -ceq 'package') { $Libraries[$key].sha512 } else { $null }
        [ordered]@{ key = $key; type = $node.type; contentHash = $hash; dependencies = $dependencies }
    }
}

function Get-FrameworkDownloadEvidence($Framework) {
    if (-not $Framework.Contains('downloadDependencies')) { return }
    $items = $Framework.downloadDependencies
    if ($items -isnot [array] -or $items.Count -gt 16) { throw 'Invalid framework download declaration bound or type.' }
    $downloads = @{}
    foreach ($item in $items) {
        Assert-EvidenceKeys $item @('name', 'version')
        if ($item.name -isnot [string] -or $item.name -cnotmatch '^Microsoft\.(NETCore|WindowsDesktop|AspNetCore)\.App\.Runtime\.win-x64$' -or
            $item.version -isnot [string] -or $item.version -cne "[$((Get-PackagingPins).runtimeVersion), $((Get-PackagingPins).runtimeVersion)]" -or
            $downloads.ContainsKey($item.name)) {
            throw 'Unpinned or duplicate framework download declaration.'
        }
        $downloads[$item.name] = $item.version
    }
    foreach ($id in Get-EvidenceOrdinalStrings @($downloads.Keys)) {
        [ordered]@{ id = $id; requested = $downloads[$id] }
    }
}

function Get-PackageRestoreEvidence([string]$PublishDirectory, $Source) {
    $root = Split-Path (Split-Path $PSScriptRoot)
    $projects = @{}
    foreach ($application in @('Desktop', 'Doctor')) {
        $path = Join-Path $PublishDirectory "$application.restore-graph.json"
        Assert-RestoreGraphLocks $path
        $graph = Read-PackagingJson $path -AsHashtable
        foreach ($project in $graph.projects.Values) {
            $name = $project.restore.projectName
            if ($projects.ContainsKey($name)) {
                if ($projects[$name].restore.projectPath -cne $project.restore.projectPath) { throw "Ambiguous restore project: $name" }
                foreach ($alias in $projects[$name].frameworks.Keys) {
                    if (-not $project.frameworks.Contains($alias) -or
                        (Get-EvidenceSha256 @(Get-FrameworkDownloadEvidence $projects[$name].frameworks[$alias])) -cne
                            (Get-EvidenceSha256 @(Get-FrameworkDownloadEvidence $project.frameworks[$alias]))) {
                        throw "Framework downloads differ between generated restore graphs: $name $alias"
                    }
                }
            }
            $projects[$name] = $project
        }
    }
    $expectedProjects = @(@(Get-PublishProjectNames Desktop) + @(Get-PublishProjectNames Doctor) | Select-Object -Unique)
    if ($projects.Count -gt 128 -or @(Compare-Object $expectedProjects @($projects.Keys) -CaseSensitive).Count) {
        throw 'Restore project closure differs from the reviewed publish graph.'
    }
    foreach ($name in Get-EvidenceOrdinalStrings @($projects.Keys)) {
        $project = $projects[$name]
        $projectPath = [IO.Path]::GetRelativePath($root, $project.restore.projectPath)
        Assert-EvidenceRelativePath $projectPath
        if ($projectPath -cne "src\$name\$name.csproj") { throw "Unsupported external restore project: $name" }
        $assetsPath = Join-Path $PublishDirectory "build\obj\$name\project.assets.json"
        $assets = Read-PackagingJson $assetsPath -AsHashtable
        $specPath = Join-Path $PublishDirectory "build\obj\$name\$name.csproj.nuget.dgspec.json"
        Assert-RestoreGraphLocks $specPath
        $spec = Read-PackagingJson $specPath -AsHashtable
        $restoredProject = $spec.projects[$project.restore.projectPath]
        if ($null -eq $restoredProject -or $restoredProject.restore.projectName -cne $name -or
            $restoredProject.version -cne $project.version) {
            throw "Actual restore specification differs from the preflight project: $name"
        }
        if ($assets.project.restore.projectPath -ine $project.restore.projectPath -or
            $assets.project.restore.restoreLockProperties.restoreLockedMode -ne $true -or
            $assets.project.restore.restoreLockProperties.nuGetLockFilePath -ine $project.restore.restoreLockProperties.nuGetLockFilePath) {
            throw "Stale or unlocked restore assets: $name"
        }
        $sourceSetHash = Get-EvidenceSha256 @(Get-EvidenceOrdinalStrings @($assets.project.restore.sources.Keys))
        if ($sourceSetHash -cne (Get-EvidenceSha256 @(Get-EvidenceOrdinalStrings @($project.restore.sources.Keys)))) {
            throw "Effective restore sources differ from the actual restore graph: $name"
        }
        if ($assets.project.version -cne $project.version) {
            throw "Project version differs from the actual restore graph: $name"
        }
        $aliases = @(Get-EvidenceOrdinalStrings @($project.frameworks.Keys))
        $expectedTargets = @(Get-EvidenceOrdinalStrings @($aliases | ForEach-Object { $_; "$_/$((Get-PackagingPins).rid)" }))
        if ((Get-EvidenceSha256 $aliases) -cne (Get-EvidenceSha256 @(Get-EvidenceOrdinalStrings @($assets.project.frameworks.Keys))) -or
            (Get-EvidenceSha256 $aliases) -cne (Get-EvidenceSha256 @(Get-EvidenceOrdinalStrings @($assets.project.restore.frameworks.Keys))) -or
            (Get-EvidenceSha256 $aliases) -cne (Get-EvidenceSha256 @(Get-EvidenceOrdinalStrings @($assets.project.restore.originalTargetFrameworks))) -or
            (Get-EvidenceSha256 $aliases) -cne (Get-EvidenceSha256 @(Get-EvidenceOrdinalStrings @($restoredProject.frameworks.Keys))) -or
            (Get-EvidenceSha256 $expectedTargets) -cne (Get-EvidenceSha256 @(Get-EvidenceOrdinalStrings @($assets.targets.Keys)))) {
            throw "Complete framework/target set differs from the actual restore graph: $name"
        }
        $frameworkDownloads = @{}
        foreach ($alias in $aliases) {
            $assetDeclarations = if ($assets.project.frameworks[$alias].Contains('dependencies')) { $assets.project.frameworks[$alias].dependencies } else { $null }
            $graphDeclarations = if ($project.frameworks[$alias].Contains('dependencies')) { $project.frameworks[$alias].dependencies } else { $null }
            $restoreDeclarations = if ($restoredProject.frameworks[$alias].Contains('dependencies')) { $restoredProject.frameworks[$alias].dependencies } else { $null }
            if ($assets.project.frameworks[$alias].framework -cne $project.frameworks[$alias].framework -or
                $restoredProject.frameworks[$alias].framework -cne $project.frameworks[$alias].framework -or
                (Get-EvidenceSha256 $assetDeclarations) -cne (Get-EvidenceSha256 $graphDeclarations) -or
                (Get-EvidenceSha256 $restoreDeclarations) -cne (Get-EvidenceSha256 $graphDeclarations) -or
                (Get-EvidenceSha256 $assets.project.restore.frameworks[$alias].projectReferences) -cne
                    (Get-EvidenceSha256 $project.restore.frameworks[$alias].projectReferences) -or
                (Get-EvidenceSha256 $restoredProject.restore.frameworks[$alias].projectReferences) -cne
                    (Get-EvidenceSha256 $project.restore.frameworks[$alias].projectReferences)) {
                throw "Root dependency declarations differ from the actual restore graph: $name $alias"
            }
            $downloads = @(Get-FrameworkDownloadEvidence $assets.project.frameworks[$alias])
            # NuGet's restore specification includes SDK-injected downloads for referenced projects.
            $graphDownloads = @(Get-FrameworkDownloadEvidence $restoredProject.frameworks[$alias])
            if ((Get-EvidenceSha256 $downloads) -cne (Get-EvidenceSha256 $graphDownloads) -or
                ($project.frameworks[$alias].Contains('downloadDependencies') -and
                    (Get-EvidenceSha256 $graphDownloads) -cne (Get-EvidenceSha256 @(Get-FrameworkDownloadEvidence $project.frameworks[$alias])))) {
                throw "Framework downloads differ from the actual restore graph: $name $alias"
            }
            $frameworkDownloads[$alias] = $downloads
        }
        $lockPath = "packaging\windows\locks\$name.packages.lock.json"
        $lock = Read-PackagingJson (Join-Path $root $lockPath) -AsHashtable
        $inputFile = @($Source.files | Where-Object path -CEQ $lockPath)
        if ($inputFile.Count -ne 1) { throw "Source receipt does not identify RID lock: $name" }
        Assert-Sha256 (Join-Path $root $lockPath) $inputFile[0].sha256
        $targets = @(
            foreach ($alias in $aliases) {
                $targetName = "$alias/$((Get-PackagingPins).rid)"
                if (-not $assets.targets.Contains($targetName)) { throw "Missing RID assets target: $name $targetName" }
                $framework = $assets.project.frameworks[$alias].framework
                $locked = @{}
                foreach ($lockTarget in @($framework, "$framework/$((Get-PackagingPins).rid)")) {
                    if (-not $lock.dependencies.Contains($lockTarget)) { throw "Missing RID lock target: $name $lockTarget" }
                    foreach ($id in $lock.dependencies[$lockTarget].Keys) { $locked[$id] = $lock.dependencies[$lockTarget][$id] }
                }
                $libraries = @(Get-ResolvedEvidenceLibraries $assets.targets[$targetName] $assets.libraries)
                if ($libraries.Count -ne $locked.Count) { throw "Resolved graph differs from committed RID lock: $name $targetName" }
                foreach ($library in $libraries) {
                    $id, $version = $library.key.Split('/')
                    if (-not $locked.ContainsKey($id)) { throw "Dependency absent from committed RID lock: $id" }
                    $entry = $locked[$id]
                    if ($library.type -ceq 'package') {
                        if ($entry.type -ceq 'Project' -or $entry.resolved -cne $version -or $entry.contentHash -cne $library.contentHash) {
                            throw "Resolved package version/content hash differs from RID lock: $id"
                        }
                    } elseif ($entry.type -cne 'Project') { throw "Resolved project differs from RID lock: $id" }
                    $lockEdges = if ($entry.Contains('dependencies')) { @($entry.dependencies.Keys) } else { @() }
                    $assetEdges = @($library.dependencies | ForEach-Object { $_.Split('/')[0] })
                    if (@(Compare-Object @($lockEdges | ForEach-Object { $_.ToLowerInvariant() }) @($assetEdges | ForEach-Object { $_.ToLowerInvariant() })).Count) {
                        throw "Resolved dependency edges differ from RID lock: $id"
                    }
                }
                $references = $assets.project.restore.frameworks[$alias].projectReferences
                $rootDependencies = @(
                    foreach ($reference in $references.Values) {
                        $referenceName = [IO.Path]::GetFileNameWithoutExtension($reference.projectPath)
                        $match = @($libraries | Where-Object { $_.key.Split('/')[0] -ceq $referenceName })
                        if ($match.Count -ne 1) { throw "Missing restored project reference: $referenceName" }
                        $match[0].key
                    }
                    if ($assets.project.frameworks[$alias].Contains('dependencies')) {
                        foreach ($id in $assets.project.frameworks[$alias].dependencies.Keys) {
                            $match = @($libraries | Where-Object { $_.key.Split('/')[0] -ieq $id })
                            if ($match.Count -ne 1) { throw "Missing restored direct dependency: $id" }
                            $match[0].key
                        }
                    }
                )
                [ordered]@{
                    name = $targetName; framework = $framework
                    rootDependencies = @(Get-EvidenceOrdinalStrings $rootDependencies)
                    libraries = $libraries; frameworkDownloads = $frameworkDownloads[$alias]
                }
            }
        )
        [ordered]@{ project = $name; path = $projectPath; version = $assets.project.version; lockPath = $lockPath; lockSha256 = $inputFile[0].sha256; sourceSetSha256 = $sourceSetHash; targets = $targets }
    }
}

function Get-PackageArchiveEvidence([string]$Root, [string]$AssetsPath, $Applications) {
    $pins = Get-PackagingPins
    $assets = Read-PackagingJson $AssetsPath
    $packages = @($pins.managedPackages | ForEach-Object {
        [ordered]@{ id = $_.id; version = $_.version; sha512 = $_.sha512 }
    }) + @($pins.runtimePackages | ForEach-Object {
        [ordered]@{ id = $_.id; version = $pins.runtimeVersion; sha512 = $_.sha512 }
    })
    foreach ($id in Get-EvidenceOrdinalStrings @($packages.id)) {
        $package = @($packages | Where-Object id -CEQ $id)[0]
        $archivePath = Get-VerifiedPackageArchive $assets $id $package.version $package.sha512
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $archive.Entries) {
                if (-not $names.Add($entry.FullName)) { throw "Duplicate archive entry: $id $($entry.FullName)" }
            }
            $nuspec = @($archive.Entries | Where-Object { $_.FullName -cmatch '^[^/]+\.nuspec$' })
            if ($nuspec.Count -ne 1 -or $nuspec[0].Length -gt 1MB) { throw "Missing, ambiguous or oversized package declaration: $id" }
            $stream = $nuspec[0].Open()
            try { $nuspecHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $settings.MaxCharactersInDocument = 1MB
            $stream = $nuspec[0].Open()
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $xml = [Xml.XmlDocument]::new()
                $xml.XmlResolver = $null
                $xml.Load($reader)
            } finally { $reader.Dispose(); $stream.Dispose() }
            $metadata = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
            $declaredId = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
            $declaredVersion = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
            if ($declaredId -ine $id -or $declaredVersion -cne $package.version) { throw "Package declaration identity differs: $id" }
            $licenseNodes = $metadata.SelectNodes("*[local-name()='license']")
            $expression = $null
            $licenseFile = $null
            if ($licenseNodes.Count -eq 1) {
                if ($licenseNodes[0].GetAttribute('type') -ceq 'expression') { $expression = $licenseNodes[0].InnerText }
                elseif ($licenseNodes[0].GetAttribute('type') -ceq 'file') { $licenseFile = $licenseNodes[0].InnerText }
            }
            $repository = $metadata.SelectNodes("*[local-name()='repository']")
            $repositoryUrl = $null
            $repositoryCommit = $null
            if ($repository.Count -eq 1 -and $repository[0].GetAttribute('type') -ceq 'git') {
                $uri = [uri]$repository[0].GetAttribute('url')
                if ($uri.IsAbsoluteUri -and $uri.Scheme -ceq 'https' -and -not $uri.UserInfo -and -not $uri.Query) {
                    $repositoryUrl = $uri.AbsoluteUri
                    $repositoryCommit = $repository[0].GetAttribute('commit')
                }
            }
            $origins = @(
                foreach ($application in $Applications) {
                    $owners = @($application.libraries | Where-Object {
                        $_.key -ieq "$id/$($package.version)" -or $_.key -ieq "runtimepack.$id/$($package.version)"
                    })
                    if (-not $owners.Count) { continue }
                    if ($owners.Count -ne 1) { throw "Ambiguous package owner: $id" }
                    $owner = $owners[0]
                    $entryPaths = @{}
                    foreach ($asset in $owner.assets) {
                        $archiveName = $asset.source
                        if ($owner.type -ceq 'runtimepack') {
                            $prefix = if ($asset.kind -ceq 'native') { 'native' } else { 'lib/net10.0' }
                            $archiveName = "runtimes/$($pins.rid)/$prefix/$($asset.source)"
                        }
                        $entryPaths[$asset.path] = $archiveName
                    }
                    if ($owner.type -ceq 'runtimepack') {
                        foreach ($entry in $archive.Entries) {
                            if ($entry.FullName -cmatch '^runtimes/win-x64/lib/net10\.0/([^/]+/[^/]+\.resources\.dll)$') {
                                $relative = "$($application.name)\$($Matches[1].Replace('/', '\'))"
                                if (Test-Path -LiteralPath (Join-Path $Root $relative) -PathType Leaf) { $entryPaths[$relative] = $entry.FullName }
                            }
                        }
                    }
                    foreach ($path in Get-EvidenceOrdinalStrings @($entryPaths.Keys)) {
                        $entry = $archive.GetEntry($entryPaths[$path])
                        if ($null -eq $entry) { throw "Owned package asset absent from archive: $path" }
                        $stream = $entry.Open()
                        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
                        finally { $stream.Dispose() }
                        Assert-Sha256 (Join-Path $Root $path) $hash
                        [ordered]@{ path = $path; component = "$($application.name)|$($owner.key)"; entry = $entry.FullName; sha256 = $hash }
                    }
                }
            )
            [ordered]@{
                id = $id; version = $package.version; archiveSha512 = $package.sha512
                nuspecSha256 = $nuspecHash; licenseExpression = $expression; licenseFile = $licenseFile
                repositoryUrl = $repositoryUrl; repositoryCommit = $repositoryCommit; origins = $origins
            }
        } finally { $archive.Dispose() }
    }
}

function Get-PackageProvenance([string]$Root, [string]$PublishDirectory, $Source, $SdkReceipt) {
    Test-PackagingSourceReceipt $Source
    Test-PackagingSdkReceipt $SdkReceipt
    $applications = @(Get-PackageApplications $Root)
    $restores = @(Get-PackageRestoreEvidence $PublishDirectory $Source)
    $archives = @(Get-PackageArchiveEvidence $Root (Join-Path $PublishDirectory 'build\obj\Martlet.Desktop\project.assets.json') $applications)
    $result = New-PackageProvenanceDocument $Source $SdkReceipt $applications $restores $archives (Get-PackagingPins).rid
    Test-PackageProvenance $Root $result
    return $result
}

function New-PackageProvenanceDocument($Source, $SdkReceipt, $Applications, $Restores, $Archives, [string]$RuntimeIdentifier) {
    return [ordered]@{
        schemaVersion = 1
        assurance = 'UNSIGNED INTERNAL OBSERVATION - NOT PUBLISHER ATTESTATION'
        source = $Source; sdk = $SdkReceipt
        publish = [ordered]@{ configuration = 'Release'; framework = 'net10.0-windows'; rid = $RuntimeIdentifier; selfContained = $true; trimmed = $false; singleFile = $false; readyToRun = $false }
        applications = $Applications; restores = $Restores; archives = $Archives
    }
}

function Test-PackageProvenance([string]$Root, $Provenance) {
    if ($null -eq $Provenance) { throw 'Required provenance missing. Rebuild the complete payload.' }
    Assert-EvidenceKeys $Provenance @('schemaVersion', 'assurance', 'source', 'sdk', 'publish', 'applications', 'restores', 'archives')
    if (($Provenance.schemaVersion -isnot [int] -and $Provenance.schemaVersion -isnot [long]) -or
        $Provenance.schemaVersion -ne 1 -or $Provenance.assurance -cne 'UNSIGNED INTERNAL OBSERVATION - NOT PUBLISHER ATTESTATION') {
        throw 'Unsupported unsigned provenance metadata.'
    }
    Test-PackagingSourceReceipt $Provenance.source
    Test-PackagingSdkReceipt $Provenance.sdk
    $pins = Get-PackagingPins
    $publish = [ordered]@{ configuration = 'Release'; framework = 'net10.0-windows'; rid = $pins.rid; selfContained = $true; trimmed = $false; singleFile = $false; readyToRun = $false }
    if ((ConvertTo-EvidenceJson $Provenance.publish) -cne (ConvertTo-EvidenceJson $publish)) { throw 'Unsupported provenance publish settings.' }
    $applications = @(Get-PackageApplications $Root)
    if ((ConvertTo-EvidenceJson $Provenance.applications) -cne (ConvertTo-EvidenceJson $applications)) {
        throw 'Provenance dependency graph differs from actual published dependencies.'
    }
    $projects = @{}
    if ($Provenance.restores -isnot [array] -or $Provenance.restores.Count -gt 128) { throw 'Invalid restore evidence bound.' }
    foreach ($restore in $Provenance.restores) {
        Assert-EvidenceKeys $restore @('project', 'path', 'version', 'lockPath', 'lockSha256', 'sourceSetSha256', 'targets')
        if ($restore.project -cnotmatch '^Martlet\.[A-Za-z.]+$' -or $projects.ContainsKey($restore.project) -or
            $restore.path -cne "src\$($restore.project)\$($restore.project).csproj" -or
            $restore.lockPath -cne "packaging\windows\locks\$($restore.project).packages.lock.json" -or
            $restore.sourceSetSha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Duplicate or invalid restore project evidence.'
        }
        $projects[$restore.project] = $restore
        $sourceLock = @($Provenance.source.files | Where-Object path -CEQ $restore.lockPath)
        if ($sourceLock.Count -ne 1 -or $restore.lockSha256 -cne $sourceLock[0].sha256) { throw 'Restore lock differs from source input receipt.' }
        if ($restore.targets -isnot [array] -or $restore.targets.Count -lt 1 -or $restore.targets.Count -gt 8) { throw 'Invalid restore target count.' }
        $targetNames = @{}
        foreach ($target in $restore.targets) {
            Assert-EvidenceKeys $target @('name', 'framework', 'rootDependencies', 'libraries', 'frameworkDownloads')
            if ($target.name -cnotin @('net10.0/win-x64', 'net10.0-windows/win-x64') -or $targetNames.ContainsKey($target.name) -or
                $target.framework -cnotin @('net10.0', 'net10.0-windows7.0') -or
                $target.libraries -isnot [array] -or $target.libraries.Count -gt 2048) {
                throw 'Invalid or duplicate restore target evidence.'
            }
            $targetNames[$target.name] = $true
            $libraries = @{}
            foreach ($library in $target.libraries) {
                Assert-EvidenceKeys $library @('key', 'type', 'contentHash', 'dependencies')
                if ($library.key -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.+-]+$' -or
                    $library.key.Length -gt 256 -or $libraries.ContainsKey($library.key) -or
                    $library.type -cnotin @('package', 'project')) { throw 'Invalid or duplicate restored library evidence.' }
                $libraries[$library.key] = $library
                if ($library.type -ceq 'package') {
                    $id, $version = $library.key.Split('/')
                    if (@($pins.managedPackages | Where-Object { $_.id -ceq $id -and $_.version -ceq $version }).Count -ne 1 -or
                        $library.contentHash -cnotmatch '^[A-Za-z0-9+/]{86}==$') { throw 'Unpinned restored package or invalid content hash.' }
                } elseif ($null -ne $library.contentHash) { throw 'Project cannot claim a NuGet content hash.' }
            }
            foreach ($edges in @(,$target.rootDependencies) + @($target.libraries | ForEach-Object { ,$_.dependencies })) {
                # The root and each library are separate adjacency lists, including known leaves.
                $edgeList = @($edges)
                if ($edgeList.Count -gt 2048) { throw 'Dependency edges exceed their bound.' }
                $seen = @{}
                foreach ($edge in $edgeList) {
                    if ($edge -isnot [string] -or -not $libraries.ContainsKey($edge) -or $seen.ContainsKey($edge)) {
                        throw 'Dangling or duplicate restored dependency edge.'
                    }
                    $seen[$edge] = $true
                }
            }
            if ($target.frameworkDownloads -isnot [array] -or $target.frameworkDownloads.Count -gt 16) { throw 'Invalid framework download evidence.' }
            $downloads = @{}
            foreach ($download in $target.frameworkDownloads) {
                Assert-EvidenceKeys $download @('id', 'requested')
                if ($download.id -cnotmatch '^Microsoft\.(NETCore|WindowsDesktop|AspNetCore)\.App\.Runtime\.win-x64$' -or
                    $download.requested -cne "[$($pins.runtimeVersion), $($pins.runtimeVersion)]" -or $downloads.ContainsKey($download.id)) {
                    throw 'Unpinned or duplicate framework download evidence.'
                }
                $downloads[$download.id] = $true
            }
        }
    }
    $expectedProjects = @(@(Get-PublishProjectNames Desktop) + @(Get-PublishProjectNames Doctor) | Select-Object -Unique)
    if (@(Compare-Object $expectedProjects @($projects.Keys) -CaseSensitive).Count) { throw 'Provenance restore project closure differs.' }
    foreach ($restore in $Provenance.restores) {
        foreach ($target in $restore.targets) {
            foreach ($library in $target.libraries) {
                if ($library.type -cne 'project') { continue }
                $id, $version = $library.key.Split('/')
                if (-not $projects.ContainsKey($id) -or $projects[$id].version -cne $version) {
                    throw "Supporting project version contradicts the resolved graph: $id"
                }
            }
        }
    }
    foreach ($application in $applications) {
        $restore = $projects["Martlet.$($application.name)"]
        $target = @($restore.targets | Where-Object name -CEQ "net10.0-windows/$($pins.rid)")
        if ($target.Count -ne 1) { throw 'Missing entry-point restore target evidence.' }
        $rootKey = "$($restore.project)/$($restore.version)"
        foreach ($runtime in @($application.libraries | Where-Object type -CEQ 'runtimepack')) {
            $id, $version = $runtime.key.Substring('runtimepack.'.Length).Split('/')
            $downloads = @($target[0].frameworkDownloads | Where-Object {
                $_.id -ceq $id -and $_.requested -ceq "[$version, $version]"
            })
            if ($downloads.Count -ne 1) { throw "Published runtime pack is missing matching framework download evidence: $($runtime.key)" }
        }
        $published = @($application.libraries | Where-Object { $_.type -cne 'runtimepack' })
        if ($published.Count -ne $target[0].libraries.Count + 1) { throw 'Published/restored dependency counts differ.' }
        foreach ($library in $published) {
            if ($library.key -ceq $rootKey) {
                $expectedEdges = $target[0].rootDependencies
                $actualEdges = @($library.dependencies | Where-Object { -not $_.StartsWith('runtimepack.', [StringComparison]::Ordinal) })
                if ((Get-EvidenceSha256 $expectedEdges) -cne (Get-EvidenceSha256 $actualEdges)) { throw 'Published root dependencies differ from restore evidence.' }
            } else {
                $match = @($target[0].libraries | Where-Object key -CEQ $library.key)
                if ($match.Count -ne 1 -or $match[0].type -cne $library.type -or $match[0].contentHash -cne $library.contentHash -or
                    (Get-EvidenceSha256 $match[0].dependencies) -cne (Get-EvidenceSha256 $library.dependencies)) {
                    throw "Published dependency differs from resolved evidence: $($library.key)"
                }
            }
        }
    }
    $fileMap = @{}
    foreach ($file in Get-PayloadFiles $Root -ExcludeSbom) { $fileMap[$file.path] = $file }
    $origins = @{}
    $archiveIds = @{}
    if ($Provenance.archives -isnot [array] -or $Provenance.archives.Count -ne $pins.managedPackages.Count + $pins.runtimePackages.Count) {
        throw 'Missing or additional pinned package archive evidence.'
    }
    foreach ($archive in $Provenance.archives) {
        Assert-EvidenceKeys $archive @('id', 'version', 'archiveSha512', 'nuspecSha256', 'licenseExpression', 'licenseFile', 'repositoryUrl', 'repositoryCommit', 'origins')
        $pin = @($pins.managedPackages | Where-Object id -IEQ $archive.id)
        $runtime = $false
        if (-not $pin.Count) { $pin = @($pins.runtimePackages | Where-Object id -IEQ $archive.id); $runtime = $true }
        $version = if ($runtime) { $pins.runtimeVersion } elseif ($pin.Count -eq 1) { $pin[0].version } else { '' }
        if ($pin.Count -ne 1 -or $archiveIds.ContainsKey($archive.id) -or $archive.version -cne $version -or
            $archive.archiveSha512 -cne $pin[0].sha512 -or $archive.nuspecSha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Archive evidence differs from pinned identity/digest.'
        }
        $archiveIds[$archive.id] = $true
        foreach ($field in @('licenseExpression', 'licenseFile', 'repositoryUrl', 'repositoryCommit')) {
            $value = $archive.$field
            if ($null -ne $value -and ($value -isnot [string] -or $value.Length -gt 1024 -or $value -match '[\x00-\x1f]')) {
                throw "Invalid bounded package declaration: $field"
            }
        }
        if ($archive.origins -isnot [array] -or $archive.origins.Count -gt 8192) { throw 'Invalid archive asset count.' }
        foreach ($origin in $archive.origins) {
            Assert-EvidenceKeys $origin @('path', 'component', 'entry', 'sha256')
            Assert-EvidenceRelativePath $origin.path
            Assert-EvidenceRelativePath $origin.entry.Replace('/', '\')
            if (-not $fileMap.ContainsKey($origin.path) -or $origins.ContainsKey($origin.path) -or $origin.sha256 -cne $fileMap[$origin.path].sha256) {
                throw "Archive asset integrity/ownership mismatch: $($origin.path)"
            }
            $parts = $origin.component.Split('|')
            $application = @($applications | Where-Object name -CEQ $parts[0])
            $key = if ($runtime) { "runtimepack.$($archive.id)/$version" } else { "$($archive.id)/$version" }
            $owner = @($application.libraries | Where-Object key -IEQ $key)
            if ($parts.Count -ne 2 -or $application.Count -ne 1 -or $owner.Count -ne 1 -or $parts[1] -cne $owner[0].key) {
                throw 'Archive asset refers to an incorrect component.'
            }
            $asset = @($owner[0].assets | Where-Object path -CEQ $origin.path)
            if ($asset.Count -eq 1) {
                $expected = $asset[0].source
                if ($runtime) {
                    $kind = if ($asset[0].kind -ceq 'native') { 'native' } else { 'lib/net10.0' }
                    $expected = "runtimes/$($pins.rid)/$kind/$expected"
                }
            } elseif ($runtime -and $origin.path -cmatch '^(Desktop|Doctor)\\[^\\]+\\[^\\]+\.resources\.dll$') {
                $expected = "runtimes/$($pins.rid)/lib/net10.0/" + $origin.path.Substring($parts[0].Length + 1).Replace('\', '/')
            } else { throw 'Unowned archive asset is not an evidenced runtime satellite.' }
            if ($origin.entry -cne $expected) { throw 'Archive asset entry differs from actual dependency ownership.' }
            $origins[$origin.path] = $origin.component
        }
    }
    $projectAssets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($application in $applications) {
        $null = $projectAssets.Add("$($application.name)\Martlet.$($application.name).exe")
        foreach ($library in $application.libraries) {
            foreach ($asset in $library.assets) {
                if ($library.type -ceq 'project') { $null = $projectAssets.Add($asset.path); continue }
                if (-not $origins.ContainsKey($asset.path) -or $origins[$asset.path] -cne "$($application.name)|$($library.key)") {
                    throw "Missing archive asset provenance: $($asset.path)"
                }
            }
        }
    }
    foreach ($file in $fileMap.Values) {
        if ($file.path -imatch '\.(dll|exe)$' -and -not $origins.ContainsKey($file.path) -and -not $projectAssets.Contains($file.path)) {
            throw "Unowned shipped binary: $($file.path)"
        }
    }
}

function Get-PackageSbom([string]$Root, $Provenance) {
    $files = @(Get-PayloadFiles $Root -ExcludeSbom)
    return New-PackageSbomDocument $files (Assert-PublishLayout $Root) $Provenance
}

function New-PackageSbomDocument([object[]]$Files, [string]$ApplicationVersion, $Provenance) {
    $fileComponents = @{}
    $components = [Collections.Generic.List[object]]::new()
    $dependencies = [Collections.Generic.List[object]]::new()
    $rootRefs = @()
    foreach ($file in $Files) {
        $path = $file.path.Replace('\', '/')
        $fileComponents[$file.path] = [ordered]@{
            type = 'file'; 'bom-ref' = "file:$path"; name = $path
            hashes = @([ordered]@{ alg = 'SHA-256'; content = $file.sha256 })
            properties = @(
                [ordered]@{ name = 'martlet:payload:path'; value = $file.path }
                [ordered]@{ name = 'martlet:payload:bytes'; value = [string]$file.bytes }
                [ordered]@{ name = 'martlet:license:status'; value = 'NOT ASSESSED - no file-level license conclusion' }
                [ordered]@{ name = 'martlet:origin:classification'; value = 'Document; source or upstream ownership not inferred' }
            )
        }
    }
    foreach ($application in $Provenance.applications) {
        foreach ($library in $application.libraries) {
            $id, $version = $library.key.Split('/')
            $reference = "$($application.name)|$($library.key)"
            $isRoot = $id -ceq "Martlet.$($application.name)"
            if ($isRoot) { $rootRefs += $reference }
            $type = if ($isRoot) { 'application' } elseif ($library.type -ceq 'runtimepack') { 'framework' } else { 'library' }
            $component = [ordered]@{
                type = $type; 'bom-ref' = $reference; name = $id; version = $version
                properties = @([ordered]@{ name = 'martlet:publish:context'; value = $application.name })
            }
            $ownedPaths = @($library.assets.path)
            if ($library.type -cne 'project') {
                $packageId = $id -creplace '^runtimepack\.', ''
                $archive = @($Provenance.archives | Where-Object id -IEQ $packageId)[0]
                $component.purl = "pkg:nuget/$($packageId.ToLowerInvariant())@$version"
                $component.properties += @(
                    [ordered]@{ name = 'martlet:nuget:archive-sha512'; value = $archive.archiveSha512 }
                    [ordered]@{ name = 'martlet:nuget:nuspec-sha256'; value = $archive.nuspecSha256 }
                    [ordered]@{ name = 'martlet:license:status'; value = 'UPSTREAM DECLARATION ONLY - rights not assessed' }
                )
                if ($library.contentHash) { $component.properties += [ordered]@{ name = 'martlet:nuget:lock-content-hash'; value = $library.contentHash } }
                if ($archive.licenseExpression) { $component.licenses = @([ordered]@{ expression = $archive.licenseExpression; acknowledgement = 'declared' }) }
                if ($archive.licenseFile) { $component.properties += [ordered]@{ name = 'martlet:nuget:license-file'; value = $archive.licenseFile } }
                if ($archive.repositoryUrl) {
                    $component.externalReferences = @([ordered]@{ type = 'vcs'; url = $archive.repositoryUrl })
                    if ($archive.repositoryCommit) { $component.properties += [ordered]@{ name = 'martlet:nuget:declared-repository-commit'; value = $archive.repositoryCommit } }
                }
                $origins = @($archive.origins | Where-Object component -CEQ $reference)
                $ownedPaths = @($origins.path)
                foreach ($origin in $origins) {
                    $fileComponents[$origin.path].properties[3].value = 'Verified upstream archive entry'
                    $fileComponents[$origin.path].properties += [ordered]@{ name = 'martlet:nuget:archive-entry'; value = $origin.entry }
                }
            } else {
                $component.properties += [ordered]@{ name = 'martlet:license:status'; value = 'UNKNOWN - no project license granted' }
                foreach ($path in $ownedPaths) { $fileComponents[$path].properties[3].value = 'Project build output; not a copy of source bytes' }
                if ($isRoot) {
                    $generated = @("$($application.name)\Martlet.$($application.name).exe", "$($application.name)\Martlet.$($application.name).deps.json", "$($application.name)\Martlet.$($application.name).runtimeconfig.json")
                    foreach ($path in $generated) { $fileComponents[$path].properties[3].value = 'SDK-generated publish output; not an unchanged archive asset' }
                    $ownedPaths += $generated
                }
            }
            $component.components = @(
                foreach ($path in Get-EvidenceOrdinalStrings $ownedPaths) {
                    if (-not $fileComponents.ContainsKey($path)) { throw "Duplicate or missing SBOM file ownership: $path" }
                    $fileComponents[$path]
                    $fileComponents.Remove($path)
                }
            )
            $components.Add($component)
            $dependencies.Add([ordered]@{ ref = $reference; dependsOn = @($library.dependencies | ForEach-Object { "$($application.name)|$_" }) })
        }
    }
    foreach ($path in Get-EvidenceOrdinalStrings @($fileComponents.Keys)) { $components.Add($fileComponents[$path]) }
    $dependencies.Insert(0, [ordered]@{ ref = 'martlet-internal-payload'; dependsOn = $rootRefs })
    return [ordered]@{
        bomFormat = 'CycloneDX'; specVersion = '1.6'; version = 1
        metadata = [ordered]@{
            tools = [ordered]@{ components = @(
                [ordered]@{ type = 'application'; name = 'Martlet Windows packaging'; version = $Provenance.source.commit }
                [ordered]@{ type = 'application'; name = '.NET SDK'; version = $Provenance.sdk.version; properties = @(
                    [ordered]@{ name = 'martlet:tool:observation'; value = $Provenance.sdk.observation }
                    foreach ($file in $Provenance.sdk.files) { [ordered]@{ name = "martlet:tool:sha256:$($file.path)"; value = $file.sha256 } }
                ) }
            ) }
            component = [ordered]@{ type = 'application'; 'bom-ref' = 'martlet-internal-payload'; name = 'Martlet INTERNAL UNSIGNED payload'; version = $ApplicationVersion }
            properties = @(
                [ordered]@{ name = 'martlet:assurance'; value = $Provenance.assurance }
                [ordered]@{ name = 'martlet:source:commit'; value = $Provenance.source.commit }
                [ordered]@{ name = 'martlet:source:tree'; value = $Provenance.source.tree }
                [ordered]@{ name = 'martlet:source:dirty'; value = $Provenance.source.dirty.ToString().ToLowerInvariant() }
                [ordered]@{ name = 'martlet:source:inputs-sha256'; value = $Provenance.source.sha256 }
                [ordered]@{ name = 'martlet:resolved-evidence:sha256'; value = (Get-EvidenceSha256 $Provenance.restores) }
                [ordered]@{ name = 'martlet:scope'; value = 'Actual packaged files and resolved .NET publish dependencies; upstream vendored/native internals and OS dependencies are not fully decomposed.' }
                [ordered]@{ name = 'martlet:excluded-metadata'; value = 'sbom.cdx.json, manifest.json, SHA256SUMS.txt (avoids circular hashes)' }
                [ordered]@{ name = 'martlet:reproducibility'; value = 'Canonical metadata only; no hermetic-build or reproducible-binary claim.' }
            )
        }
        components = @($components.ToArray()); dependencies = @($dependencies.ToArray())
        compositions = @([ordered]@{ aggregate = 'incomplete'; assemblies = @('martlet-internal-payload'); dependencies = @('martlet-internal-payload') })
    }
}

function Write-PackageSbom([string]$Root, $Provenance) {
    foreach ($name in @('sbom.cdx.json', 'manifest.json', 'SHA256SUMS.txt')) {
        if (Test-Path -LiteralPath (Join-Path $Root $name)) { throw "Generated evidence collision: $name. Use fresh staging." }
    }
    Test-PackageProvenance $Root $Provenance
    $text = ConvertTo-EvidenceJson (Get-PackageSbom $Root $Provenance)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    if ($bytes.Length -gt 16MB) { throw 'SBOM exceeds the 16 MiB evidence bound.' }
    $stream = [IO.File]::Open((Join-Path $Root 'sbom.cdx.json'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes) } finally { $stream.Dispose() }
}

function Test-PackageSbom([string]$Root, $Provenance) {
    $path = Join-Path $Root 'sbom.cdx.json'
    $null = Read-PackagingJson $path
    $expected = Get-EvidenceSha256 (Get-PackageSbom $Root $Provenance)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) {
        throw 'SBOM differs from actual payload and resolved provenance. Rebuild the complete payload.'
    }
}

. "$PSScriptRoot\Provenance.Common.ps1"
