#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$ComparePayloadRoot,
    [string]$PublishDirectory,
    [string]$BuilderDirectory,
    [string]$DotnetPath = 'dotnet',
    [string]$NodePath = 'node',
    [Parameter(Mandatory)][string]$CliHome
)
. "$PSScriptRoot\Installer.Common.ps1"
. "$PSScriptRoot\ProtectedData.Common.ps1"
Assert-PackagingHost
Assert-MSBuildPath (Split-Path (Split-Path $PSScriptRoot))
Assert-MSBuildPath $WorkDirectory
New-OutputDirectory $WorkDirectory
$sdk = Initialize-PackagingSdk $DotnetPath $CliHome
$node = (Get-Command $NodePath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
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

& {
    $calls = @{ browser = 0; build = 0 }
    $browserValidator = ${function:Test-AvatarBrowserEvidence}
    $buildValidator = ${function:Test-BuildArchiveEvidence}
    function Test-AvatarBrowserEvidence($Root, $Browser, $Source) {
        $calls.browser++
        & $browserValidator $Root $Browser $Source
    }
    function Test-BuildArchiveEvidence($Provenance) {
        $calls.build++
        & $buildValidator $Provenance
    }
    if ($manifest.files.Count -le 1) { throw 'Whole-inventory regression requires a real multi-file payload.' }
    Test-PackageProvenance $PayloadRoot $manifest.provenance
    if ($calls.browser -ne 1 -or $calls.build -ne 1) {
        throw 'Whole-inventory browser/build validation must run exactly once per provenance validation.'
    }
}
$script:cases++

$copy = Join-Path $WorkDirectory ("payload with spaces $([char]0x00E9)")
Copy-Item -LiteralPath $PayloadRoot -Destination $copy -Recurse
$copied = Test-PayloadManifest $copy
$manifestPath = Join-Path $copy 'manifest.json'
$originalManifest = [IO.File]::ReadAllBytes($manifestPath)
Write-PayloadManifest $copy $copied.sourceCommit $copied.sourceDirty $copied.provenance
if ([Convert]::ToHexString([IO.File]::ReadAllBytes($manifestPath)) -cne [Convert]::ToHexString($originalManifest)) {
    throw 'Manifest is not reproducible after copying to a different Unicode/spaced path.'
}
$script:cases++

function Write-TestEnvelope($Value, [switch]$KeepInventory) {
    if (-not $KeepInventory) { $Value.files = @(Get-PayloadFiles $copy) }
    [IO.File]::WriteAllText($manifestPath, (ConvertTo-EvidenceJson $Value), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $copy 'SHA256SUMS.txt'),
        (Get-ChecksumText $Value.files (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash))
}

function ConvertTo-UnorderedTestObject($Value) {
    if ($Value -is [Collections.IDictionary]) {
        $result = @{}
        foreach ($key in $Value.psbase.Keys) { $result[$key] = ConvertTo-UnorderedTestObject $Value[$key] }
        return $result
    }
    if ($Value -is [array]) { return ,@($Value | ForEach-Object { ConvertTo-UnorderedTestObject $_ }) }
    return $Value
}

$orderedFixture = Join-Path $WorkDirectory 'ordered-evidence.json'
$firstObject = [ordered]@{ z = [ordered]@{ beta = 2; alpha = 1 }; a = @([ordered]@{ y = 2; x = 1 }); empty = @(); value = $null; object = [ordered]@{} }
$secondObject = [ordered]@{ object = [ordered]@{}; value = $null; empty = @(); a = @([ordered]@{ x = 1; y = 2 }); z = [ordered]@{ alpha = 1; beta = 2 } }
$canonical = ConvertTo-EvidenceJson $firstObject
if ($canonical -cne (ConvertTo-EvidenceJson $secondObject) -or
    $canonical -cne (ConvertTo-EvidenceJson (ConvertTo-UnorderedTestObject $firstObject))) {
    throw 'Canonical evidence depends on dictionary insertion/enumeration order.'
}
$script:cases++
foreach ($inputObject in @($firstObject, $secondObject)) {
    [IO.File]::WriteAllText($orderedFixture, (ConvertTo-Json -InputObject $inputObject -Depth 64))
    $parsed = Read-PackagingJson $orderedFixture -AsHashtable
    if ($parsed -isnot [Collections.Specialized.OrderedDictionary] -or
        $parsed.z -isnot [Collections.Specialized.OrderedDictionary] -or
        $parsed.a[0] -isnot [Collections.Specialized.OrderedDictionary] -or
        $parsed.object -isnot [Collections.Specialized.OrderedDictionary] -or $parsed.object.Count -ne 0 -or
        $parsed.empty -isnot [array] -or $parsed.empty.Count -ne 0 -or $parsed.a.Count -ne 1 -or
        (ConvertTo-EvidenceJson $parsed) -cne $canonical -or
        (ConvertTo-EvidenceJson (Read-PackagingJson $orderedFixture)) -cne $canonical) {
        throw 'Bounded JSON reader did not preserve canonical nested objects, nulls and arrays.'
    }
    $script:cases++
}
$unorderedProvenance = ConvertTo-UnorderedTestObject $copied.provenance
Test-PackagingSourceReceipt $unorderedProvenance.source
Test-PackageProvenance $copy $unorderedProvenance
if ((ConvertTo-EvidenceJson $unorderedProvenance.publish) -cne (ConvertTo-EvidenceJson $copied.provenance.publish)) {
    throw 'Publish settings canonical roundtrip depends on dictionary ordering.'
}
$script:cases++
[IO.File]::WriteAllText($orderedFixture, (ConvertTo-Json -InputObject $unorderedProvenance -Depth 64))
$roundtrip = Read-PackagingJson $orderedFixture -AsHashtable
Test-PackagingSourceReceipt $roundtrip.source
Test-PackageProvenance $copy $roundtrip
$script:cases++

$collisionFixture = Join-Path $WorkDirectory 'json-member-collisions.json'
$collisionObjects = @(
    [ordered]@{ keys = 'kept'; kept = 1; dropped = 2 }
    [ordered]@{
        Keys = 'kept'; Values = @('first', $null, 3); Count = 42; Length = 'length'
        Add = $false; Remove = 'remove'; GetEnumerator = [ordered]@{ nested = $true }
        kept = 1; dropped = 2
    }
)
foreach ($collisionObject in $collisionObjects) {
    foreach ($nested in @($false, $true)) {
        $inputObject = if ($nested) { [ordered]@{ nested = $collisionObject; siblings = @($collisionObject) } } else { $collisionObject }
        $expectedJson = ConvertTo-EvidenceJson $inputObject
        foreach ($asHashtable in @($false, $true)) {
            [IO.File]::WriteAllText($collisionFixture, (ConvertTo-Json -InputObject $inputObject -Depth 64))
            $parsed = Read-PackagingJson $collisionFixture -AsHashtable:$asHashtable
            $subject = if ($nested) { $parsed.nested } else { $parsed }
            Assert-EvidenceKeys $subject @($collisionObject.psbase.Keys)
            if ((ConvertTo-EvidenceJson $parsed) -cne $expectedJson) {
                throw 'JSON member names shadowed collection introspection or changed property values/types.'
            }
            $script:cases++
        }
    }
}
foreach ($reserved in @('psbase', 'PSObject')) {
    $inputObject = [ordered]@{ $reserved = 'kept'; other = 1 }
    [IO.File]::WriteAllText($collisionFixture, (ConvertTo-Json -InputObject $inputObject))
    $parsed = Read-PackagingJson $collisionFixture -AsHashtable
    Assert-EvidenceKeys $parsed @($inputObject.psbase.Keys)
    if ((ConvertTo-EvidenceJson $parsed) -cne (ConvertTo-EvidenceJson $inputObject)) {
        throw 'Dictionary-mode JSON lost a PowerShell-reserved property name.'
    }
    $script:cases++
    Assert-Fails "PSCustomObject mode preserves reserved-name rejection: $reserved" {
        Read-PackagingJson $collisionFixture
    } '*reserved*'
}
$collisionDepsPath = Join-Path $copy 'Desktop\Martlet.Desktop.deps.json'
$collisionDepsBytes = [IO.File]::ReadAllBytes($collisionDepsPath)
try {
    $deps = Read-PackagingJson $collisionDepsPath -AsHashtable
    $rootKey = @($deps.libraries.psbase.Keys | Where-Object { $_ -clike 'Martlet.Desktop/*' })[0]
    $target = $deps.targets[$deps.runtimeTarget.name][$rootKey]
    $target['keys'] = 'runtime'
    $target['unsupportedAssets'] = [ordered]@{ 'unreviewed.dll' = [ordered]@{} }
    [IO.File]::WriteAllText($collisionDepsPath, (ConvertTo-EvidenceJson $deps))
    Assert-Fails 'actual dependency metadata retains and rejects shadowing and unknown asset members' {
        Get-PackageApplications $copy
    } '*Unsupported dependency type or asset category*'
} finally { [IO.File]::WriteAllBytes($collisionDepsPath, $collisionDepsBytes) }

$sbomPath = Join-Path $copy 'sbom.cdx.json'
$originalSbom = [IO.File]::ReadAllBytes($sbomPath)
$evidenceSums = [IO.File]::ReadAllBytes((Join-Path $copy 'SHA256SUMS.txt'))
foreach ($case in @('missing component', 'duplicate component', 'wrong standard', 'circular file', 'wrong file hash')) {
    try {
        $changed = Read-PackagingJson $sbomPath -AsHashtable
        switch ($case) {
            'missing component' { $changed.components = @($changed.components | Select-Object -Skip 1) }
            'duplicate component' { $changed.components += $changed.components[0] }
            'wrong standard' { $changed.specVersion = '1.5' }
            'circular file' { $changed.components += [ordered]@{ type = 'file'; 'bom-ref' = 'file:manifest.json'; name = 'manifest.json' } }
            'wrong file hash' { $changed.components[0].components[0].hashes[0].content = '0' * 64 }
        }
        [IO.File]::WriteAllText($sbomPath, (ConvertTo-EvidenceJson $changed))
        Write-TestEnvelope (Read-PackagingJson $manifestPath -AsHashtable)
        Assert-Fails "rechecksummed SBOM $case" { Test-PayloadManifest $copy } '*SBOM differs*'
        Assert-Fails "installer rejects SBOM $case" { Write-InstallerFileList $copy (Join-Path $WorkDirectory 'invalid-sbom.iss') } '*SBOM differs*'
    } finally {
        [IO.File]::WriteAllBytes($sbomPath, $originalSbom)
        [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
        [IO.File]::WriteAllBytes((Join-Path $copy 'SHA256SUMS.txt'), $evidenceSums)
    }
}
foreach ($case in @('archive digest', 'resolved hash', 'missing origin', 'duplicate library', 'source mismatch',
        'legacy schema', 'missing provenance', 'missing edge', 'dangling edge', 'wrong facade owner', 'supporting version',
        'file byte type', 'provenance schema type', 'missing runtime download')) {
    try {
        $changed = Read-PackagingJson $manifestPath -AsHashtable
        switch ($case) {
            'archive digest' { $changed.provenance.archives[0].archiveSha512 = '0' * 128 }
            'resolved hash' {
                $target = @($changed.provenance.restores | Where-Object project -CEQ 'Martlet.Desktop')[0].targets[0]
                @($target.libraries | Where-Object type -CEQ 'package')[0].contentHash = ('A' * 86) + '=='
            }
            'missing origin' { $changed.provenance.archives[0].origins = @($changed.provenance.archives[0].origins | Select-Object -Skip 1) }
            'duplicate library' { $changed.provenance.applications[0].libraries += $changed.provenance.applications[0].libraries[0] }
            'source mismatch' { $changed.sourceCommit = '0' * 40 }
            'legacy schema' { $changed.schemaVersion = 1 }
            'missing provenance' { $changed.Remove('provenance') }
            'missing edge' { @($changed.provenance.applications[0].libraries | Where-Object { $_.dependencies.Count -gt 0 })[0].dependencies = @() }
            'dangling edge' { $changed.provenance.restores[0].targets[0].rootDependencies += 'missing/1.0.0' }
            'wrong facade owner' {
                $origin = @($changed.provenance.archives.origins | Where-Object path -CEQ 'Desktop\WindowsBase.dll')[0]
                $origin.component = 'Desktop|runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12'
            }
            'supporting version' { @($changed.provenance.restores | Where-Object project -CEQ 'Martlet.Audio')[0].version = '9.9.9' }
            'file byte type' { $changed.files[0].bytes = [string]$changed.files[0].bytes }
            'provenance schema type' { $changed.provenance.schemaVersion = '1' }
            'missing runtime download' {
                @($changed.provenance.restores | Where-Object project -CEQ 'Martlet.Desktop')[0].targets[0].frameworkDownloads = @()
            }
        }
        Write-TestEnvelope $changed -KeepInventory:($case -ceq 'file byte type')
        $pattern = switch ($case) {
            'archive digest' { '*Archive evidence differs*' }
            'resolved hash' { '*Published dependency differs*' }
            'missing origin' { '*Missing archive asset provenance*' }
            'duplicate library' { '*dependency graph differs*' }
            'source mismatch' { '*source metadata differs*' }
            'legacy schema' { '*requires manifest schema v3*' }
            'missing provenance' { '*exactly these properties*' }
            'missing edge' { '*dependency graph differs*' }
            'dangling edge' { '*Dangling or duplicate restored dependency edge*' }
            'wrong facade owner' { '*incorrect component*' }
            'supporting version' { '*Supporting project version contradicts*' }
            'file byte type' { '*bytes must be a nonnegative*' }
            'provenance schema type' { '*Unsupported unsigned provenance*' }
            'missing runtime download' { '*Published runtime pack is missing matching framework download evidence*' }
        }
        Assert-Fails "rechecksummed provenance $case" { Test-PayloadManifest $copy } $pattern
    } finally {
        [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
        [IO.File]::WriteAllBytes((Join-Path $copy 'SHA256SUMS.txt'), $evidenceSums)
    }
}

$browserFixture = Join-Path $WorkDirectory 'browser-evidence.json'
[IO.File]::WriteAllText($browserFixture, (ConvertTo-EvidenceJson $copied.provenance.browser))
Test-AvatarBrowserEvidence $copy $copied.provenance.browser $copied.provenance.source
$script:cases++
foreach ($case in @('missing package', 'extra package', 'scope', 'identity', 'archive hash', 'missing dependency',
        'notice coverage', 'source hash', 'package entry', 'recipe', 'tool version', 'output hash', 'output path',
        'duplicate input', 'input role', 'static mismatch', 'lock hash', 'unknown field',
        'missing build material', 'runtime build material', 'build material role', 'build fingerprint hash', 'build fingerprint bytes')) {
    $browser = Read-PackagingJson $browserFixture -AsHashtable
    switch ($case) {
        'missing package' { $browser.packages = @($browser.packages | Select-Object -Skip 1) }
        'extra package' { $browser.packages += $browser.packages[0] }
        'scope' { $browser.packages[0].scope = 'runtime' }
        'identity' { $browser.packages[0].version = '0.25.13' }
        'archive hash' { $browser.packages[0].archiveSha512 = '0' * 128 }
        'missing dependency' { @($browser.packages | Where-Object { $_.dependencies.Count -gt 0 })[0].dependencies = @() }
        'notice coverage' { $browser.outputs[0].inputs = @($browser.outputs[0].inputs | Select-Object -Skip 1) }
        'source hash' { @($browser.inputs | Where-Object { $null -eq $_.package })[0].sha256 = '0' * 64 }
        'package entry' { @($browser.inputs | Where-Object { $null -ne $_.package })[0].entry = 'package/../outside' }
        'recipe' { $browser.recipe.entryPoints = @('src\unexpected.js') }
        'tool version' { $browser.tools[0].version = '0.0.0' }
        'output hash' { $browser.outputs[1].sha256 = '0' * 64 }
        'output path' { $browser.outputs[1].path = 'Desktop\AvatarRenderer\web\..\outside.js' }
        'duplicate input' { $browser.inputs += $browser.inputs[0] }
        'input role' { $browser.inputs[0].roles = @('unknown') }
        'static mismatch' { $browser.outputs[3].inputs = @($browser.recipe.script) }
        'lock hash' { $browser.lockFiles[0].sha256 = '0' * 64 }
        'unknown field' { $browser.extra = $true }
        'missing build material' { $browser.inputs = @($browser.inputs | Where-Object entry -CNE 'package/lib/main.js') }
        'runtime build material' {
            @($browser.inputs | Where-Object { $_.package -ceq 'node_modules/three' -and $_.roles -ccontains 'bundle-source' })[0].roles = @('build-script')
        }
        'build material role' { @($browser.inputs | Where-Object entry -CEQ 'package/lib/main.js')[0].roles = @('package-metadata') }
        'build fingerprint hash' { $browser.tools[2].files[0].sha256 = '0' * 64 }
        'build fingerprint bytes' { $browser.tools[2].files[0].bytes++ }
    }
    Assert-Fails "browser production validator: $case" {
        Test-AvatarBrowserEvidence $copy $browser $copied.provenance.source
    } '*'
}
foreach ($case in @('nonempty omissions', 'wrong private parent', 'wrong private path', 'missing build tool',
        'wrong build use', 'wrong reference owner')) {
    try {
        $changed = Read-PackagingJson $manifestPath -AsHashtable
        switch ($case) {
            'nonempty omissions' { $changed.provenance.applications[0].buildOnlyLibraries = @('Martlet.Avatar.RendererHost/0.1.0') }
            'wrong private parent' { $changed.provenance.applications[2].parent = 'Doctor' }
            'wrong private path' { $changed.provenance.applications[2].directory = 'Desktop\AvatarRendererX' }
            'missing build tool' { $changed.provenance.buildArchives = @() }
            'wrong build use' { $changed.provenance.buildArchives[0].uses[0].project = 'Martlet.Doctor' }
            'wrong reference owner' {
                $archive = @($changed.provenance.archives | Where-Object id -CEQ 'Microsoft.Web.WebView2')[0]
                @($archive.origins | Where-Object path -CEQ 'Desktop\AvatarRenderer\Microsoft.Web.WebView2.Core.xml')[0].component = 'Doctor|Microsoft.Web.WebView2.Core/1.0.4191.47'
            }
        }
        Write-TestEnvelope $changed
        Assert-Fails "rechecksummed avatar provenance: $case" { Test-PayloadManifest $copy } '*'
    } finally {
        [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
        [IO.File]::WriteAllBytes((Join-Path $copy 'SHA256SUMS.txt'), $evidenceSums)
    }
}
foreach ($name in @('sdk.js', 'model.vrm', 'extra.xml')) {
    $extra = Join-Path $copy "Desktop\AvatarRenderer\web\$name"
    try {
        [IO.File]::WriteAllText($extra, 'Unowned test data.')
        Assert-Fails "unexpected browser asset $name" { Assert-AvatarWebInventory $copy } '*Unexpected browser output*'
    } finally { [IO.File]::Delete($extra) }
}
$rendererDeps = Join-Path $copy 'Desktop\AvatarRenderer\Martlet.Avatar.RendererHost.deps.json'
$rendererDepsBytes = [IO.File]::ReadAllBytes($rendererDeps)
$unreviewedLoader = Join-Path $copy 'Desktop\AvatarRenderer\WebView2Loader-arm64.dll'
try {
    Copy-Item -LiteralPath (Join-Path $copy 'Desktop\AvatarRenderer\WebView2Loader.dll') -Destination $unreviewedLoader
    $deps = Read-PackagingJson $rendererDeps -AsHashtable
    $rootLibrary = @($deps.libraries.Keys | Where-Object { $_.StartsWith('Martlet.Avatar.RendererHost/', [StringComparison]::Ordinal) })[0]
    $deps.targets[$deps.runtimeTarget.name][$rootLibrary].runtime['WebView2Loader-arm64.dll'] = [ordered]@{}
    [IO.File]::WriteAllText($rendererDeps, (ConvertTo-EvidenceJson $deps))
    Assert-Fails 'project cannot launder extra WebView2 runtime assets' { Get-PackageApplications $copy } '*Unreviewed project runtime assets*'
} finally {
    [IO.File]::WriteAllBytes($rendererDeps, $rendererDepsBytes)
    [IO.File]::Delete($unreviewedLoader)
}

$readerFixture = Join-Path $WorkDirectory 'invalid-evidence.json'
foreach ($json in @('{"a":1,"a":2}', '{"a":1,"A":2}', '{"a":{"x":1,"x":2}}')) {
    [IO.File]::WriteAllText($readerFixture, $json)
    Assert-Fails 'duplicate JSON keys' { Read-PackagingJson $readerFixture } '*duplicate*'
}
[IO.File]::WriteAllText($readerFixture, '{"a":"' + ('x' * 16MB) + '"}')
Assert-Fails 'metadata byte bound' { Read-PackagingJson $readerFixture } '*bound*'
[IO.File]::WriteAllText($readerFixture, '{"a":"' + ('x' * (16MB - 8)) + '"}')
$null = Read-PackagingJson $readerFixture
$script:cases++
[IO.File]::WriteAllText($readerFixture, ('{"a":' * 64) + '0' + ('}' * 64))
$null = Read-PackagingJson $readerFixture
$script:cases++
[IO.File]::WriteAllText($readerFixture, ('{"a":' * 65) + '0' + ('}' * 65))
Assert-Fails 'metadata depth bound' { Read-PackagingJson $readerFixture } '*depth*'
Assert-EvidenceRelativePath (('d\' * 400) + ('a' * 224))
$script:cases++
foreach ($path in @('..\outside', 'C:\absolute', '\\server\share', 'Desktop\\double', 'Desktop/forward', ('a' * 1025))) {
    Assert-Fails "invalid evidence path $path" { Assert-EvidenceRelativePath $path } '*path*'
}
Assert-Fails 'production rejects generated-file collision' { Write-PackageSbom $copy $copied.provenance } '*Generated evidence collision*'
$sourceFixture = Read-PackagingJson $manifestPath -AsHashtable
$sourceFixture.provenance.source.files += $sourceFixture.provenance.source.files[0]
Assert-Fails 'duplicate source inputs' { Test-PackagingSourceReceipt $sourceFixture.provenance.source } '*source*'
$sourceFixture.provenance.source.files = @($sourceFixture.provenance.source.files[0]) * 16385
Assert-Fails 'source input count bound' { Test-PackagingSourceReceipt $sourceFixture.provenance.source } '*bound*'
$sourceFixture.provenance.source.files = @(0..16383 | ForEach-Object {
    [ordered]@{ path = ('f{0:D5}' -f $_); bytes = [long]0; sha256 = $null }
})
$sourceFixture.provenance.source.sha256 = Get-EvidenceSha256 $sourceFixture.provenance.source.files
Test-PackagingSourceReceipt $sourceFixture.provenance.source
$script:cases++

$satellite = Join-Path $copy 'Desktop\cs\PresentationCore.resources.dll'
$satelliteBytes = [IO.File]::ReadAllBytes($satellite)
try {
    $damaged = [byte[]]$satelliteBytes.Clone()
    $damaged[-1] = $damaged[-1] -bxor 1
    [IO.File]::WriteAllBytes($satellite, $damaged)
    Write-TestEnvelope (Read-PackagingJson $manifestPath -AsHashtable)
    Assert-Fails 'rechecksummed runtime satellite' { Test-PayloadManifest $copy } '*Archive asset integrity/ownership mismatch*'
} finally {
    [IO.File]::WriteAllBytes($satellite, $satelliteBytes)
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
    [IO.File]::WriteAllBytes((Join-Path $copy 'SHA256SUMS.txt'), $evidenceSums)
}

# Unsigned evidence is not authentication: consistent rewriting is inspectable,
# but the independent current-source installer boundary must still reject it.
try {
    $changed = Read-PackagingJson $manifestPath -AsHashtable
    $changed.provenance.source.tree = '0' * 40
    [IO.File]::WriteAllText($sbomPath, (ConvertTo-EvidenceJson (Get-PackageSbom $copy $changed.provenance)))
    Write-TestEnvelope $changed
    $null = Test-PayloadManifest $copy
    $script:cases++
    Assert-Fails 'current-source handoff rejects stale source' {
        Write-InstallerFileList $copy (Join-Path $WorkDirectory 'stale-source.iss')
    } '*Source input snapshot differs*'
    $rejectedInstaller = Join-Path $WorkDirectory 'rejected-installer'
    Assert-Fails 'actual installer preflight rejects source before compiler use' {
        & "$PSScriptRoot\Build-Installer.ps1" -PayloadRoot $copy -BuilderDirectory (Join-Path $WorkDirectory 'missing-builder') -OutputDirectory $rejectedInstaller
    } '*Source input snapshot differs*'
    if (Test-Path -LiteralPath $rejectedInstaller) { throw 'Stale evidence created installer output.' }
} finally {
    [IO.File]::WriteAllBytes($sbomPath, $originalSbom)
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
    [IO.File]::WriteAllBytes((Join-Path $copy 'SHA256SUMS.txt'), $evidenceSums)
}

if ($PublishDirectory) {
    $restoreEvidence = @(Get-PackageRestoreEvidence $PublishDirectory $copied.provenance.source)
    if ((Get-EvidenceSha256 $restoreEvidence) -cne (Get-EvidenceSha256 $copied.provenance.restores)) { throw 'Supplied publish assets do not match this payload.' }
    $script:cases++
    $restoreCopy = Join-Path $WorkDirectory 'real-restore-inputs'
    [IO.Directory]::CreateDirectory($restoreCopy) | Out-Null
    foreach ($entry in (Get-PublishContexts).name) {
        Copy-Item -LiteralPath (Join-Path $PublishDirectory "$entry.restore-graph.json") -Destination $restoreCopy
    }
    foreach ($project in $copied.provenance.restores.project) {
        $destination = Join-Path $restoreCopy "build\obj\$project"
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PublishDirectory "build\obj\$project\project.assets.json") -Destination $destination
        Copy-Item -LiteralPath (Join-Path $PublishDirectory "build\obj\$project\$project.csproj.nuget.dgspec.json") -Destination $destination
    }
    foreach ($name in @('avatar-bundle-receipt.json', 'avatar-esbuild-metafile.json')) {
        $receipt = @(Get-ChildItem -LiteralPath (Join-Path $PublishDirectory 'build\obj\Martlet.Avatar.RendererHost') -Recurse -File -Filter $name)
        if ($receipt.Count -ne 1) { throw 'Publish directory must retain one private-host browser receipt/metafile.' }
        Copy-Item -LiteralPath $receipt[0].FullName -Destination (Join-Path $restoreCopy 'build\obj\Martlet.Avatar.RendererHost')
    }
    Copy-Item -LiteralPath (Join-Path $PublishDirectory 'npm-archives') -Destination $restoreCopy -Recurse
    Copy-Item -LiteralPath (Join-Path $PublishDirectory 'browser-evidence.json') -Destination $restoreCopy
    $cachedArchive = @(Get-ChildItem -LiteralPath (Join-Path $restoreCopy 'npm-archives') -File | Sort-Object Name)[0]
    $archiveBytes = [IO.File]::ReadAllBytes($cachedArchive.FullName)
    try {
        $changedArchive = [byte[]]$archiveBytes.Clone()
        $changedArchive[0] = $changedArchive[0] -bxor 1
        [IO.File]::WriteAllBytes($cachedArchive.FullName, $changedArchive)
        Assert-Fails 'npm archive bytes must match lock integrity' {
            Get-AvatarBrowserEvidence $copy $restoreCopy $copied.provenance.source -NodePath $node
        } '*Npm archive integrity failure*'
        $savedPath = $env:PATH
        try {
            $env:PATH = [Environment]::SystemDirectory
            Assert-Fails 'explicit Node path reaches archive assertion without PATH discovery' {
                Get-AvatarBrowserEvidence $copy $restoreCopy $copied.provenance.source -NodePath $node
            } '*Npm archive integrity failure*'
        } finally { $env:PATH = $savedPath }
    } finally { [IO.File]::WriteAllBytes($cachedArchive.FullName, $archiveBytes) }
    $assetsPath = Join-Path $restoreCopy 'build\obj\Martlet.Desktop\project.assets.json'
    $assetsBytes = [IO.File]::ReadAllBytes($assetsPath)
    try {
        $assets = Read-PackagingJson $assetsPath -AsHashtable
        $assets.libraries['NAudio.Core/3.1.0'].sha512 = ('A' * 86) + '=='
        [IO.File]::WriteAllText($assetsPath, (ConvertTo-EvidenceJson $assets))
        Assert-Fails 'actual restored content hash must match lock' {
            Get-PackageRestoreEvidence $restoreCopy $copied.provenance.source
        } '*content hash differs from RID lock*'
    } finally { [IO.File]::WriteAllBytes($assetsPath, $assetsBytes) }
    $audioAssetsPath = Join-Path $restoreCopy 'build\obj\Martlet.Audio\project.assets.json'
    $audioAssetsBytes = [IO.File]::ReadAllBytes($audioAssetsPath)
    foreach ($case in @('omitted framework', 'omitted target', 'project version', 'project reference', 'package declaration')) {
        try {
            $assets = Read-PackagingJson $audioAssetsPath -AsHashtable
            switch ($case) {
                'omitted framework' { $assets.project.frameworks.Remove('net10.0-windows') }
                'omitted target' { $assets.targets.Remove('net10.0-windows/win-x64') }
                'project version' { $assets.project.version = '9.9.9' }
                'project reference' { $assets.project.restore.frameworks['net10.0'].projectReferences.Clear() }
                'package declaration' { $assets.project.frameworks['net10.0-windows'].dependencies['NAudio.Wasapi'].version = '[9.9.9, )' }
            }
            [IO.File]::WriteAllText($audioAssetsPath, (ConvertTo-EvidenceJson $assets))
            $pattern = switch ($case) {
                'omitted framework' { '*Complete framework/target set differs*' }
                'omitted target' { '*Complete framework/target set differs*' }
                'project version' { '*Project version differs*' }
                default { '*Root dependency declarations differ*' }
            }
            Assert-Fails "actual supporting-project assets: $case" {
                Get-PackageRestoreEvidence $restoreCopy $copied.provenance.source
            } $pattern
        } finally { [IO.File]::WriteAllBytes($audioAssetsPath, $audioAssetsBytes) }
    }
    foreach ($project in $copied.provenance.restores) {
        $projectAssetsPath = Join-Path $restoreCopy "build\obj\$($project.project)\project.assets.json"
        $specPath = Join-Path $restoreCopy "build\obj\$($project.project)\$($project.project).csproj.nuget.dgspec.json"
        $projectBytes = [IO.File]::ReadAllBytes($projectAssetsPath)
        $specBytes = [IO.File]::ReadAllBytes($specPath)
        foreach ($target in $project.targets) {
            $alias = $target.name.Split('/')[0]
            $downloadCases = @('missing field')
            if ($project.project -cin @('Martlet.Desktop', 'Martlet.Doctor', 'Martlet.Audio')) {
                $downloadCases += @('missing entry', 'identity', 'version', 'extra', 'duplicate', 'null', 'graph omission', 'graph duplicate', 'reverse order')
            }
            foreach ($case in $downloadCases) {
                try {
                    $assets = Read-PackagingJson $projectAssetsPath -AsHashtable
                    $spec = Read-PackagingJson $specPath -AsHashtable
                    $framework = $assets.project.frameworks[$alias]
                    $specFramework = $spec.projects[$assets.project.restore.projectPath].frameworks[$alias]
                    switch ($case) {
                        'missing field' { $framework.Remove('downloadDependencies') }
                        'missing entry' { $framework.downloadDependencies = @($framework.downloadDependencies | Select-Object -Skip 1) }
                        'identity' { $framework.downloadDependencies[0].name = 'Microsoft.NETCore.App.Runtime.win-arm64' }
                        'version' { $framework.downloadDependencies[0].version = '[9.9.9, 9.9.9]' }
                        'extra' { $framework.downloadDependencies += [ordered]@{ name = 'Unreviewed.Framework'; version = '[10.0.12, 10.0.12]' } }
                        'duplicate' { $framework.downloadDependencies += $framework.downloadDependencies[0] }
                        'null' { $framework.downloadDependencies = $null }
                        'graph omission' { $specFramework.Remove('downloadDependencies') }
                        'graph duplicate' { $specFramework.downloadDependencies += $specFramework.downloadDependencies[0] }
                        'reverse order' { [array]::Reverse($framework.downloadDependencies) }
                    }
                    [IO.File]::WriteAllText($projectAssetsPath, (ConvertTo-EvidenceJson $assets))
                    [IO.File]::WriteAllText($specPath, (ConvertTo-EvidenceJson $spec))
                    if ($case -ceq 'reverse order') {
                        $actual = @(Get-PackageRestoreEvidence $restoreCopy $copied.provenance.source)
                        if ((Get-EvidenceSha256 $actual) -cne (Get-EvidenceSha256 $copied.provenance.restores)) {
                            throw 'Framework download set normalization depends on declaration order.'
                        }
                        $script:cases++
                    } else {
                        Assert-Fails "$($project.project) $alias framework downloads: $case" {
                            Get-PackageRestoreEvidence $restoreCopy $copied.provenance.source
                        } '*framework download*'
                    }
                } finally {
                    [IO.File]::WriteAllBytes($projectAssetsPath, $projectBytes)
                    [IO.File]::WriteAllBytes($specPath, $specBytes)
                }
            }
        }
    }
    $coreAssetsPath = Join-Path $restoreCopy 'build\obj\Martlet.Core\project.assets.json'
    $coreSpecPath = Join-Path $restoreCopy 'build\obj\Martlet.Core\Martlet.Core.csproj.nuget.dgspec.json'
    $coreBytes = [IO.File]::ReadAllBytes($coreAssetsPath)
    $coreSpecBytes = [IO.File]::ReadAllBytes($coreSpecPath)
    foreach ($emptyStyle in @('absent', 'empty array')) {
        try {
            $assets = Read-PackagingJson $coreAssetsPath -AsHashtable
            $spec = Read-PackagingJson $coreSpecPath -AsHashtable
            foreach ($framework in @($assets.project.frameworks['net10.0'], $spec.projects[$assets.project.restore.projectPath].frameworks['net10.0'])) {
                if ($emptyStyle -ceq 'absent') { $framework.Remove('downloadDependencies') }
                else { $framework.downloadDependencies = @() }
            }
            [IO.File]::WriteAllText($coreAssetsPath, (ConvertTo-EvidenceJson $assets))
            [IO.File]::WriteAllText($coreSpecPath, (ConvertTo-EvidenceJson $spec))
            $actual = @(Get-PackageRestoreEvidence $restoreCopy $copied.provenance.source)
            $core = @($actual | Where-Object project -CEQ 'Martlet.Core')[0]
            if ($core.targets[0].frameworkDownloads -isnot [array] -or $core.targets[0].frameworkDownloads.Count -ne 0) {
                throw 'Matching empty authoritative framework-download sets were not preserved.'
            }
            $script:cases++
        } finally {
            [IO.File]::WriteAllBytes($coreAssetsPath, $coreBytes)
            [IO.File]::WriteAllBytes($coreSpecPath, $coreSpecBytes)
        }
    }

    foreach ($directory in @('Desktop', 'Doctor', 'help')) {
        foreach ($extension in @('dll', 'DLL', 'dLl', 'exe', 'EXE', 'eXe')) {
            $plugin = Join-Path $copy "$directory\VendorPlugin.$extension"
            Copy-Item -LiteralPath (Join-Path $copy 'Doctor\Martlet.Core.dll') -Destination $plugin
            try {
                Assert-Fails "production generator rejects $directory unowned .$extension" {
                    Get-PackageProvenance $copy $restoreCopy $copied.provenance.source $copied.provenance.sdk -NodePath $node
                } '*Unowned shipped binary*'
            } finally { [IO.File]::Delete($plugin) }
        }
    }
    foreach ($entry in @('Desktop', 'Doctor')) {
        $mixedCase = if ($entry -ceq 'Desktop') { 'dEsKtOp' } else { 'dOcToR' }
        [IO.Directory]::Move((Join-Path $copy $entry), (Join-Path $copy 'renaming'))
        [IO.Directory]::Move((Join-Path $copy 'renaming'), (Join-Path $copy $mixedCase))
        try {
            foreach ($extension in @('dll', 'DLL', 'exe', 'EXE')) {
                $plugin = Join-Path $copy "$mixedCase\VendorPlugin.$extension"
                Copy-Item -LiteralPath (Join-Path $copy "$mixedCase\Martlet.Core.dll") -Destination $plugin
                try {
                    Assert-Fails "production generator rejects $mixedCase unowned .$extension" {
                        Get-PackageProvenance $copy $restoreCopy $copied.provenance.source $copied.provenance.sdk -NodePath $node
                    } '*Unowned shipped binary*'
                } finally { [IO.File]::Delete($plugin) }
            }
            $null = Get-PackageProvenance $copy $restoreCopy $copied.provenance.source $copied.provenance.sdk -NodePath $node
            $script:cases++
        } finally {
            [IO.Directory]::Move((Join-Path $copy $mixedCase), (Join-Path $copy 'renaming'))
            [IO.Directory]::Move((Join-Path $copy 'renaming'), (Join-Path $copy $entry))
        }
    }
} else {
    Write-Output 'NOT RUN: retained restore-input mutation cases (supply -PublishDirectory for acceptance).'
}

$help = [IO.File]::ReadAllText((Join-Path $copy 'help\INTERNAL.txt'))
foreach ($required in @('FIXTURE - NOT AI', 'self-test --scenario streaming --json',
        'Stop fixture', 'NOT spoken AI', 'Permission is not saved.',
        'V04b', 'Send typed text', 'Stop / revoke this action', 'LOCAL microphone capture',
        'separately permit uploading', 'NOT RUN', 'V06b', 'Record troubleshooting metadata',
        'OFF at every launch', 'support-v1', 'Preview every frozen file',
        'No support contact or upload channel is configured', 'CLI export is not implemented',
        'V07a', 'Configuration snapshots are NOT support bundles', 'same-profile v1/v2',
        'Read exact restore preview', 'settings.recovery.', 'Capture/logging stay OFF.')) {
    if (-not $help.Contains($required)) { throw "Installed help is missing: $required" }
}
$script:cases++

$troubleshootingSource = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'docs\TROUBLESHOOTING.md'
Assert-Sha256 (Join-Path $copy 'help\TROUBLESHOOTING.md') (Get-FileHash -LiteralPath $troubleshootingSource -Algorithm SHA256).Hash
$script:cases++

$sentinel = Join-Path $WorkDirectory 'protected-sentinel'
[IO.Directory]::CreateDirectory($sentinel) | Out-Null
[IO.File]::WriteAllText((Join-Path $sentinel 'settings.json'), '{"authored":"sentinel"}')
[IO.File]::WriteAllText((Join-Path $sentinel 'unrelated.keep'), 'unrelated protected bytes')
$resolvedSentinel = Resolve-ProtectedDataDirectory $sentinel @($PayloadRoot, $copy)
$protectedBefore = Get-ProtectedDataSnapshot $resolvedSentinel
Assert-ProtectedDataUnchanged $resolvedSentinel $protectedBefore
$script:cases++
Assert-Fails 'relative protected scope' { Resolve-ProtectedDataDirectory 'relative' @($copy) } '*explicit absolute local*'
Assert-Fails 'protected file instead of directory' {
    Resolve-ProtectedDataDirectory (Join-Path $sentinel 'settings.json') @($copy)
} '*existing directories*'
Assert-Fails 'overlapping protected scope' { Resolve-ProtectedDataDirectory $copy @($copy) } '*overlaps*'
[IO.File]::AppendAllText((Join-Path $sentinel 'unrelated.keep'), ' deliberate change')
Assert-Fails 'protected scope modification' {
    Assert-ProtectedDataUnchanged $resolvedSentinel $protectedBefore
} '*selected protected scope changed*'

$requiredFiles = @('sbom.cdx.json', 'Doctor\Martlet.Doctor.exe', 'Desktop\coreclr.dll', 'Desktop\PresentationFramework.dll',
        'Doctor\System.Text.Json.dll', 'notices\WPF-THIRD-PARTY-NOTICES.txt',
        'Desktop\Martlet.Support.dll', 'help\INTERNAL.txt', 'help\TROUBLESHOOTING.md', 'notices\DEPENDENCIES.txt',
        'Desktop\NAudio.Wasapi.dll', 'Doctor\NAudio.Core.dll', 'Desktop\System.Numerics.Tensors.dll',
        'notices\NAudio-LICENSE.txt', 'notices\NAudio-THIRD-PARTY-NOTICES.txt',
        'notices\System.Numerics.Tensors-LICENSE.txt', 'notices\System.Numerics.Tensors-THIRD-PARTY-NOTICES.txt')
foreach ($context in Get-PublishContexts) {
    $requiredFiles += @(Get-PublishProjectNames $context.name | ForEach-Object { "$($context.directory)\$_.dll" })
}
$requiredFiles += @((Get-WebViewArchiveAssets).path)
$requiredFiles += @('Desktop\AvatarRenderer\Martlet.Avatar.RendererHost.exe',
    'Desktop\AvatarRenderer\web\app.js', 'Desktop\AvatarRenderer\web\app.js.LEGAL.txt',
    'Desktop\AvatarRenderer\web\index.html', 'Desktop\AvatarRenderer\web\THIRD-PARTY-NOTICES.txt',
    'notices\Audio2Face-Protos-LICENSE.txt', 'notices\Audio2Face-THIRD-PARTY-NOTICES.md')
$requiredFiles += @((Get-PackagingPins).notices | ForEach-Object { "notices\$($_.file)" })
$requiredFiles += @((Get-PackagingPins).managedPackages.notices | ForEach-Object { "notices\$($_.file)" })
foreach ($relative in $requiredFiles | Select-Object -Unique) {
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

$desktopDepsPath = Join-Path $copy 'Desktop\Martlet.Desktop.deps.json'
$desktopDeps = [IO.File]::ReadAllBytes($desktopDepsPath)
foreach ($project in @('Martlet.Conversation', 'Martlet.Memory', 'Martlet.Providers', 'Martlet.Participation', 'Martlet.Support')) {
    try {
        $changedDeps = [Text.Encoding]::UTF8.GetString($desktopDeps) | ConvertFrom-Json
        $library = @($changedDeps.libraries.PSObject.Properties.Name | Where-Object { $_ -clike "$project/*" })
        if ($library.Count -ne 1) { throw "Expected one $project dependency in Desktop." }
        $target = $changedDeps.targets.PSObject.Properties[$changedDeps.runtimeTarget.name].Value
        $changedDeps.libraries.PSObject.Properties.Remove($library[0])
        $target.PSObject.Properties.Remove($library[0])
        [IO.File]::WriteAllText($desktopDepsPath, ($changedDeps | ConvertTo-Json -Depth 100))
        Assert-Fails "omitted $project dependency metadata" { Test-PayloadManifest $copy } '*Missing or unexpected project dependency*'
        Assert-Fails "installer rejects omitted $project dependency metadata" {
            Write-InstallerFileList $copy (Join-Path $WorkDirectory 'omitted-dependency.iss')
        } '*Missing or unexpected project dependency*'

        $changedDeps = [Text.Encoding]::UTF8.GetString($desktopDeps) | ConvertFrom-Json
        $target = $changedDeps.targets.PSObject.Properties[$changedDeps.runtimeTarget.name].Value
        $target.PSObject.Properties[$library[0]].Value.runtime.PSObject.Properties.Remove("$project.dll")
        [IO.File]::WriteAllText($desktopDepsPath, ($changedDeps | ConvertTo-Json -Depth 100))
        Assert-Fails "omitted $project runtime metadata" { Test-PayloadManifest $copy } '*Missing project runtime asset*'
    }
    finally { [IO.File]::WriteAllBytes($desktopDepsPath, $desktopDeps) }
    $unexpected = Join-Path $copy "Doctor\$project.dll"
    try {
        Copy-Item -LiteralPath (Join-Path $copy "Desktop\$project.dll") -Destination $unexpected
        Assert-Fails "Doctor excludes $project" { Test-PayloadManifest $copy } '*Unexpected Martlet assembly in Doctor*'
        Assert-Fails "installer excludes $project from Doctor" {
            Write-InstallerFileList $copy (Join-Path $WorkDirectory 'unexpected-project.iss')
        } '*Unexpected Martlet assembly in Doctor*'
    }
    finally { [IO.File]::Delete($unexpected) }
}

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
    foreach ($file in @('Publish-Windows.ps1', 'Packaging.Common.ps1', 'Provenance.Common.ps1', 'AvatarEvidence.Common.ps1')) {
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
        Where-Object { -not (Test-EvidenceOutputPath ([IO.Path]::GetRelativePath($repo, $_.FullName))) }) {
    $destination = Join-Path $source ([IO.Path]::GetRelativePath($repo, $file.FullName))
    [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$packaging = Join-Path $source 'packaging\windows'
[IO.Directory]::CreateDirectory($packaging) | Out-Null
foreach ($file in @('Packaging.targets', 'Packaging.Common.ps1', 'Provenance.Common.ps1', 'AvatarEvidence.Common.ps1', 'BrowserEvidence.mjs', 'Update-PublishLocks.ps1', 'toolchain.json')) {
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
foreach ($projectName in @('Martlet.Conversation', 'Martlet.Memory', 'Martlet.Providers', 'Martlet.Participation', 'Martlet.Support',
        'Martlet.Avatars', 'Martlet.Avatar.Hosting', 'Martlet.Avatar.Audio2Face', 'Martlet.Avatar.RendererHost')) {
    $projectLock = Join-Path $packaging "locks\$projectName.packages.lock.json"
    $projectLockBytes = [IO.File]::ReadAllBytes($projectLock)
    try {
        [IO.File]::Delete($projectLock)
        Assert-Fails "missing $projectName RID lock preflight" {
            Assert-RestoreGraphLocks $graph (Join-Path $packaging 'locks')
        } "*Missing committed RID lock: *$projectName.packages.lock.json*"
    }
    finally { [IO.File]::WriteAllBytes($projectLock, $projectLockBytes) }
}
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
