#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ReferenceMetadataRoot,
    [ValidateSet('Payload', 'Failure', 'OutputOverflow', 'Wait')][string]$Mode = 'Payload'
)
. "$PSScriptRoot\..\..\packaging\windows\Packaging.Common.ps1"
New-OutputDirectory $OutputDirectory
$encoding = [Text.UTF8Encoding]::new($false, $true)
$pins = Get-PackagingPins
if ($Mode -ne 'Payload') {
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'KEEP.fixture'), 'Retain failed child output until its owner retires.', $encoding)
    switch ($Mode) {
        'Failure' { throw 'Controlled producer failure.' }
        'OutputOverflow' { [Console]::Out.Write('x' * 65536); [Console]::Error.Write('y' * 65536); return }
        'Wait' { [Console]::Out.WriteLine('READY'); [Threading.Thread]::Sleep(60000); return }
    }
}

if ($ReferenceMetadataRoot) {
    $hashes = @{
        'manifest.json' = '4c3718a6337893e4effe65a88cbc7e5901ed3cb58798f9013872cd39455a959a'
        'sbom.cdx.json' = '6318f869f498c8983bc11cecaf03e582b16189e2fbbeb5d39df1e1c2ab9ebdc4'
        'SHA256SUMS.txt' = 'c461a62e24b1c5f016135d33e09abc43db63503029541f01e4bfff2ba5bf3239'
    }
    foreach ($name in $hashes.Keys) { Assert-Sha256 (Join-Path $ReferenceMetadataRoot $name) $hashes[$name] }
    $m = Read-PackagingJson (Join-Path $ReferenceMetadataRoot 'manifest.json') -AsHashtable
    $p = $m.provenance
    $p = New-PackageProvenanceDocument $p.source $p.sdk $p.applications $p.restores $p.archives $pins.rid
    $manifest = New-PayloadManifestDocument $m.applicationVersion $m.sourceCommit $m.sourceDirty $p $m.files $pins
    $sbom = New-PackageSbomDocument @($m.files | Where-Object path -CNE 'sbom.cdx.json') $m.applicationVersion $p
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'), (ConvertTo-EvidenceJson $manifest), $encoding)
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'sbom.cdx.json'), (ConvertTo-EvidenceJson $sbom), $encoding)
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'),
        (Get-ChecksumText $m.files $hashes['manifest.json']), $encoding)
    foreach ($name in $hashes.Keys) { Assert-Sha256 (Join-Path $OutputDirectory $name) $hashes[$name] }
    Write-Output 'PASS: exact historical three-document reconstruction; metadata only, no binary inspection.'
    return
}

# Synthetic leaf facts exercise the actual production document constructors, not build attestation.
$package = $pins.managedPackages[0]
$runtime = $pins.runtimePackages[0]
$packageKey = "$($package.id)/$($package.version)"
$runtimeId = 'Microsoft.NETCore.App.Runtime.win-x64'
$runtimeKey = "runtimepack.$runtimeId/$($pins.runtimeVersion)"
$contentHash = [Convert]::ToBase64String([byte[]]::new(64))
$sourceFiles = @(
    [ordered]@{ path = '.gitignore'; bytes = 1; sha256 = 'a' * 64 }
    [ordered]@{ path = 'packaging\windows\locks\Martlet.Desktop.packages.lock.json'; bytes = 1; sha256 = 'd' * 64 }
    [ordered]@{ path = 'packaging\windows\locks\Martlet.Doctor.packages.lock.json'; bytes = 1; sha256 = 'd' * 64 }
    [ordered]@{ path = "source\missing $([char]0x00e9).txt"; bytes = 0; sha256 = $null }
)
$source = [ordered]@{
    commit = 'a' * 40; tree = 'b' * 40; dirty = $true
    files = $sourceFiles; sha256 = Get-EvidenceSha256 $sourceFiles
}
$sdk = [ordered]@{
    version = $pins.sdkVersion
    observation = 'Local tool-file fingerprints; not an SDK distribution attestation.'
    files = @(foreach ($path in Get-EvidenceSdkPaths $pins.sdkVersion) {
        [ordered]@{ path = $path; bytes = 1; sha256 = 'c' * 64 }
    })
}
foreach ($version in @('0.2.0.0', '0.3.0.0')) {
    $root = Join-Path $OutputDirectory $version
    New-OutputDirectory $root
    $applications = @()
    $restores = @()
    $packageOrigins = @()
    $runtimeOrigins = @()
    foreach ($app in @('Desktop', 'Doctor')) {
        [IO.Directory]::CreateDirectory((Join-Path $root $app)) | Out-Null
        foreach ($file in @("Martlet.$app.exe", "Martlet.$app.dll", "Martlet.$app.deps.json",
                "Martlet.$app.runtimeconfig.json", 'upstream.dll', 'coreclr.dll')) {
            [IO.File]::WriteAllText((Join-Path $root "$app\$file"), "INERT SYNTHETIC $app $file; NEVER EXECUTE", $encoding)
        }
        $libraries = @(
            [ordered]@{
                key = "Martlet.$app/0.1.0"; type = 'project'; contentHash = $null
                dependencies = @($packageKey, $runtimeKey)
                assets = @([ordered]@{ path = "$app\Martlet.$app.dll"; kind = 'runtime'; source = "Martlet.$app.dll" })
            }
            [ordered]@{
                key = $packageKey; type = 'package'; contentHash = $contentHash; dependencies = @()
                assets = @([ordered]@{ path = "$app\upstream.dll"; kind = 'runtime'; source = $package.runtimeAsset })
            }
            [ordered]@{
                key = $runtimeKey; type = 'runtimepack'; contentHash = $null; dependencies = @()
                assets = @([ordered]@{ path = "$app\coreclr.dll"; kind = 'native'; source = 'coreclr.dll' })
            }
        )
        $applications += [ordered]@{ name = $app; target = '.NETCoreApp,Version=v10.0/win-x64'; libraries = $libraries }
        $restores += [ordered]@{
            project = "Martlet.$app"; path = "src\Martlet.$app\Martlet.$app.csproj"; version = '0.1.0'
            lockPath = "packaging\windows\locks\Martlet.$app.packages.lock.json"
            lockSha256 = 'd' * 64; sourceSetSha256 = 'e' * 64
            targets = @([ordered]@{
                name = 'net10.0-windows/win-x64'; framework = 'net10.0-windows7.0'
                rootDependencies = @($packageKey)
                libraries = @([ordered]@{ key = $packageKey; type = 'package'; contentHash = $contentHash; dependencies = @() })
                frameworkDownloads = @([ordered]@{ id = $runtimeId; requested = "[$($pins.runtimeVersion), $($pins.runtimeVersion)]" })
            })
        }
        $packageOrigins += [ordered]@{
            path = "$app\upstream.dll"; component = "$app|$packageKey"; entry = $package.runtimeAsset
            sha256 = (Get-FileHash -LiteralPath (Join-Path $root "$app\upstream.dll")).Hash.ToLowerInvariant()
        }
        $runtimeOrigins += [ordered]@{
            path = "$app\coreclr.dll"; component = "$app|$runtimeKey"; entry = 'runtimes/win-x64/native/coreclr.dll'
            sha256 = (Get-FileHash -LiteralPath (Join-Path $root "$app\coreclr.dll")).Hash.ToLowerInvariant()
        }
    }
    $archives = @(
        [ordered]@{
            id = $package.id; version = $package.version; archiveSha512 = $package.sha512; nuspecSha256 = 'f' * 64
            licenseExpression = 'MIT'; licenseFile = $null; repositoryUrl = 'https://example.invalid/synthetic'
            repositoryCommit = $null; origins = $packageOrigins
        }
        [ordered]@{
            id = $runtime.id; version = $pins.runtimeVersion; archiveSha512 = $runtime.sha512; nuspecSha256 = 'f' * 64
            licenseExpression = $null; licenseFile = 'LICENSE.txt'; repositoryUrl = $null
            repositoryCommit = $null; origins = $runtimeOrigins
        }
    )
    foreach ($dir in @('help', 'notices')) { [IO.Directory]::CreateDirectory((Join-Path $root $dir)) | Out-Null }
    $canary = (Join-Path $OutputDirectory 'PAYLOAD-EXECUTED').Replace("'", "''")
    [IO.File]::WriteAllText((Join-Path $root 'help\do-not-run.ps1'), "[IO.File]::WriteAllText('$canary', 'BAD')", $encoding)
    [IO.File]::WriteAllText((Join-Path $root 'notices\INTERNAL.txt'), 'Synthetic declarations, inert files, no publisher authorization.', $encoding)
    $provenance = New-PackageProvenanceDocument $source $sdk $applications $restores $archives $pins.rid
    $sbom = New-PackageSbomDocument @(Get-PayloadFiles $root) $version $provenance
    [IO.File]::WriteAllText((Join-Path $root 'sbom.cdx.json'), (ConvertTo-EvidenceJson $sbom), $encoding)
    $files = @(Get-PayloadFiles $root)
    $manifest = New-PayloadManifestDocument $version $source.commit $source.dirty $provenance $files $pins
    [IO.File]::WriteAllText((Join-Path $root 'manifest.json'), (ConvertTo-EvidenceJson $manifest), $encoding)
    [IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'),
        (Get-ChecksumText $files (Get-FileHash -LiteralPath (Join-Path $root 'manifest.json')).Hash), $encoding)
}
Write-Output 'PASS: inert fixtures from actual production constructors/serializer; not qualified payloads.'
