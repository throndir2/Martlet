#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PublishContexts {
    @(
        [ordered]@{ name = 'Desktop'; project = 'Martlet.Desktop'; directory = 'Desktop'; parent = $null }
        [ordered]@{ name = 'Doctor'; project = 'Martlet.Doctor'; directory = 'Doctor'; parent = $null }
        [ordered]@{ name = 'AvatarRenderer'; project = 'Martlet.Avatar.RendererHost'; directory = 'Desktop\AvatarRenderer'; parent = 'Desktop' }
    )
}

function Get-PublishContext([string]$Name) {
    $matches = @(Get-PublishContexts | Where-Object name -CEQ $Name)
    if ($matches.Count -ne 1) { throw "Unreviewed publish context: $Name." }
    return $matches[0]
}

function Get-PublishRestoreTarget([string]$Name) {
    $null = Get-PublishContext $Name
    if ($Name -ceq 'AvatarRenderer') { return 'net10.0-windows10.0.19041.0/win-x64' }
    return 'net10.0-windows/win-x64'
}

function Get-WindowsSdkArchiveAssets {
    $pin = (Get-PackagingPins).windowsSdkPackage
    foreach ($name in @('Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll')) {
        [ordered]@{
            path = "Desktop\AvatarRenderer\$name"
            component = "AvatarRenderer|runtimepack.$($pin.id)/$($pin.version)"
            entry = "lib/net8.0/$name"
        }
    }
}

function Test-PinnedFrameworkDownload([string]$Id, [string]$Version, [bool]$Renderer = $false) {
    $pins = Get-PackagingPins
    if ($Renderer -and $Id -ceq $pins.windowsSdkPackage.id) {
        return $Version -ceq "[$($pins.windowsSdkPackage.version), $($pins.windowsSdkPackage.version)]"
    }
    return $Id -cmatch '^Microsoft\.(NETCore|WindowsDesktop|AspNetCore)\.App\.Runtime\.win-x64$' -and
        $Version -ceq "[$($pins.runtimeVersion), $($pins.runtimeVersion)]"
}

function Get-WebViewArchiveAssets {
    foreach ($name in @('Core', 'WinForms', 'Wpf')) {
        $prefix = if ($name -ceq 'Wpf') { 'lib_manual/net5.0-windows10.0.17763.0' } else { 'lib_manual/netcoreapp3.0' }
        foreach ($extension in @('dll', 'xml')) {
            [ordered]@{
                path = "Desktop\AvatarRenderer\Microsoft.Web.WebView2.$name.$extension"
                component = "AvatarRenderer|Microsoft.Web.WebView2.$name/1.0.4191.47"
                entry = "$prefix/Microsoft.Web.WebView2.$name.$extension"
            }
        }
    }
    foreach ($path in @('WebView2Loader.dll', 'runtimes\win-x64\native\WebView2Loader.dll')) {
        [ordered]@{
            path = "Desktop\AvatarRenderer\$path"
            component = 'AvatarRenderer|Microsoft.Web.WebView2/1.0.4191.47'
            entry = 'runtimes/win-x64/native/WebView2Loader.dll'
        }
    }
}

function Test-WebViewReference([string]$Context, [string]$Key) {
    return $Context -ceq 'AvatarRenderer' -and $Key -cin @(
        'Microsoft.Web.WebView2.Core/1.0.4191.47',
        'Microsoft.Web.WebView2.WinForms/1.0.4191.47',
        'Microsoft.Web.WebView2.Wpf/1.0.4191.47')
}

function Get-PackageOwnerAssets([string]$DesktopAssetsPath, [string]$Id) {
    $pins = Get-PackagingPins
    $projects = @(Get-PublishContexts | Where-Object { $pins.applicationGraphs.($_.name).packages -ccontains $Id } |
        ForEach-Object { $_.project })
    if ($Id -ceq 'Grpc.Tools') { $projects = @('Martlet.Avatar.Audio2Face') }
    if ($Id -ceq $pins.windowsSdkPackage.id) { $projects = @('Martlet.Avatar.RendererHost') }
    if ($projects.Count -eq 0) { throw "Package has no reviewed restore owner: $Id" }
    $directory = Split-Path (Split-Path $DesktopAssetsPath)
    return Read-PackagingJson (Join-Path $directory "$($projects[0])\project.assets.json")
}

function Set-PublishBuildOnlyLibraries($Applications, $Restores) {
    $renderer = @($Applications | Where-Object name -CEQ 'AvatarRenderer')
    if ($renderer.Count -ne 1) { throw 'Missing private renderer application evidence.' }
    foreach ($application in $Applications) {
        $restore = @($Restores | Where-Object project -CEQ $application.project)
        if ($restore.Count -ne 1) { throw "Missing or duplicate entry-point restore: $($application.project)" }
        $target = @($restore[0].targets | Where-Object name -CEQ (Get-PublishRestoreTarget $application.name))
        if ($target.Count -ne 1) { throw 'Missing entry-point RID target.' }
        $keys = @($application.libraries.key)
        $omitted = @($target[0].libraries.key | Where-Object { $keys -cnotcontains $_ })
        if ($omitted.Count) {
            throw 'Unreviewed restore-only library in published application.'
        }
        $application.buildOnlyLibraries = @()
    }
}

function Assert-AvatarWebInventory([string]$Root) {
    $expected = @('THIRD-PARTY-NOTICES.txt', 'app.js', 'app.js.LEGAL.txt', 'index.html')
    $directory = Join-Path $Root 'Desktop\AvatarRenderer\web'
    foreach ($name in $expected) { $null = Get-RequiredFile (Join-Path $directory $name) }
    $items = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($items.Count -ne $expected.Count -or @($items | Where-Object {
        $_ -isnot [IO.FileInfo] -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $expected -cnotcontains $_.Name
    }).Count) { throw 'Unexpected browser output; package only the four reviewed offline files.' }
}

function Test-BuildArchiveEvidence($Provenance) {
    $archives = $Provenance.buildArchives
    $pins = @(Get-PackagingPins).buildPackages
    if ($archives -isnot [array] -or $archives.Count -ne $pins.Count) { throw 'Build archive inventory differs.' }
    $seen = @{}
    foreach ($archive in $archives) {
        Assert-EvidenceKeys $archive @('id', 'version', 'archiveSha512', 'nuspecSha256', 'licenseExpression', 'licenseFile', 'repositoryUrl', 'repositoryCommit', 'uses')
        $pin = @($pins | Where-Object id -CEQ $archive.id)
        if ($pin.Count -ne 1 -or $seen.ContainsKey($archive.id) -or $archive.version -cne $pin[0].version -or
            $archive.archiveSha512 -cne $pin[0].sha512 -or $archive.nuspecSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Unpinned build archive.' }
        $seen[$archive.id] = $true
        foreach ($name in @('licenseExpression', 'licenseFile', 'repositoryUrl', 'repositoryCommit')) {
            $value = $archive.$name
            if ($null -ne $value -and ($value -isnot [string] -or $value.Length -gt 1024 -or $value -match '[\x00-\x1f]')) {
                throw 'Invalid build archive declaration.'
            }
        }
        $expected = @(
            foreach ($restore in $Provenance.restores) {
                foreach ($target in $restore.targets) {
                    foreach ($library in $target.libraries | Where-Object key -CEQ "$($archive.id)/$($archive.version)") {
                        if ($restore.project -cne 'Martlet.Avatar.Audio2Face' -or $library.type -cne 'package') { throw 'Build tool leaked into another project graph.' }
                        [ordered]@{ project = $restore.project; target = $target.name; key = $library.key; contentHash = $library.contentHash }
                    }
                }
            }
        )
        if ($archive.uses -isnot [array] -or $archive.uses.Count -lt 1 -or $archive.uses.Count -gt 1024 -or
            (Get-EvidenceSha256 $expected) -cne (Get-EvidenceSha256 $archive.uses)) { throw 'Build archive restore uses differ.' }
    }
}

function Get-ReviewedBrowserPackages {
    foreach ($name in @('three-vrm', 'three-vrm-core', 'three-vrm-materials-hdr-emissive-multiplier',
            'three-vrm-materials-mtoon', 'three-vrm-materials-v0compat', 'three-vrm-node-constraint', 'three-vrm-springbone',
            'types-vrm-0.0', 'types-vrmc-materials-hdr-emissive-multiplier-1.0', 'types-vrmc-materials-mtoon-1.0',
            'types-vrmc-node-constraint-1.0', 'types-vrmc-springbone-1.0', 'types-vrmc-springbone-extended-collider-1.0', 'types-vrmc-vrm-1.0')) {
        [ordered]@{ key = "node_modules/@pixiv/$name"; name = "@pixiv/$name"; version = '3.5.5'; scope = 'runtime' }
    }
    [ordered]@{ key = 'node_modules/three'; name = 'three'; version = '0.180.0'; scope = 'runtime' }
    foreach ($name in @('esbuild', '@esbuild/win32-x64')) {
        [ordered]@{ key = "node_modules/$name"; name = $name; version = '0.25.12'; scope = 'build' }
    }
}

function Assert-BrowserReferences($References, [int]$Maximum, $Known, [switch]$Nonempty) {
    if ($References -isnot [array] -or $References.Count -gt $Maximum -or ($Nonempty -and $References.Count -eq 0)) {
        throw 'Browser reference inventory exceeds its bound or has the wrong type.'
    }
    $previous = $null
    foreach ($reference in $References) {
        if ($reference -isnot [string] -or $reference.Length -gt 1024 -or -not $Known.ContainsKey($reference) -or
            ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $reference) -ge 0)) {
            throw 'Dangling, duplicate, unordered or invalid browser reference.'
        }
        $previous = $reference
    }
}

function Test-AvatarBrowserEvidence([string]$Root, $Browser, $Source) {
    Assert-EvidenceKeys $Browser @('schemaVersion', 'context', 'recipe', 'lockFiles', 'tools', 'packages', 'inputs', 'outputs')
    if (($Browser.schemaVersion -isnot [int] -and $Browser.schemaVersion -isnot [long]) -or
        $Browser.schemaVersion -ne 1 -or $Browser.context -cne 'AvatarRenderer') { throw 'Unsupported browser evidence.' }
    Assert-AvatarWebInventory $Root
    $sourceFiles = @{}
    foreach ($file in $Source.files) { $sourceFiles[$file.path] = $file }
    $recipe = $Browser.recipe
    Assert-EvidenceKeys $recipe @('script', 'entryPoints', 'metafileSha256', 'receiptSha256')
    if ($recipe.script -cne 'src\Martlet.Avatar.RendererHost\web\build.mjs' -or
        $recipe.entryPoints -isnot [array] -or $recipe.entryPoints.Count -ne 1 -or
        $recipe.entryPoints[0] -cne 'src\Martlet.Avatar.RendererHost\web\app.js') { throw 'Unreviewed browser build recipe.' }
    foreach ($name in @('metafileSha256', 'receiptSha256')) {
        if ($recipe.$name -isnot [string] -or $recipe.$name -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid browser recipe hash.' }
    }
    $locks = @('src\Martlet.Avatar.Live2D\package-lock.json', 'src\Martlet.Avatar.Vrm\package-lock.json')
    if ($Browser.lockFiles -isnot [array] -or $Browser.lockFiles.Count -ne 2) { throw 'Browser lock inventory differs.' }
    for ($i = 0; $i -lt 2; $i++) {
        $file = $Browser.lockFiles[$i]
        Assert-EvidenceFileRecord $file
        if ($file.path -cne $locks[$i] -or -not $sourceFiles.ContainsKey($file.path) -or
            (Get-EvidenceSha256 $file) -cne (Get-EvidenceSha256 $sourceFiles[$file.path])) { throw 'Browser lock differs from source receipt.' }
    }
    if ($Browser.tools -isnot [array] -or $Browser.tools.Count -ne 3) { throw 'Browser tool inventory differs.' }
    $toolNames = @('node', 'npm', 'esbuild')
    $pins = Get-PackagingPins
    for ($i = 0; $i -lt 3; $i++) {
        $tool = $Browser.tools[$i]
        Assert-EvidenceKeys $tool @('name', 'version', 'files')
        if ($tool.name -cne $toolNames[$i] -or $tool.version -cne $pins.browserTools.($tool.name) -or
            $tool.files -isnot [array] -or $tool.files.Count -lt 1 -or $tool.files.Count -gt 32) { throw 'Unreviewed browser tool.' }
        $previous = $null
        foreach ($file in $tool.files) {
            Assert-EvidenceFileRecord $file -RequireNonempty
            if ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $file.path) -ge 0) { throw 'Browser tool files must be ordinal-sorted and unique.' }
            $previous = $file.path
        }
    }
    $reviewed = @{}
    foreach ($package in Get-ReviewedBrowserPackages) { $reviewed[$package.key] = $package }
    if ($Browser.packages -isnot [array] -or $Browser.packages.Count -ne $reviewed.Count) { throw 'Browser package closure differs.' }
    $packages = @{}
    $previous = $null
    foreach ($package in $Browser.packages) {
        Assert-EvidenceKeys $package @('key', 'name', 'version', 'scope', 'integrity', 'archiveSha512', 'dependencies', 'notices')
        if ($package.key -isnot [string] -or -not $reviewed.ContainsKey($package.key) -or $packages.ContainsKey($package.key) -or
            ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $package.key) -ge 0)) { throw 'Unreviewed or unordered browser package.' }
        $pin = $reviewed[$package.key]
        if ($package.name -cne $pin.name -or $package.version -cne $pin.version -or $package.scope -cne $pin.scope -or
            $package.integrity -isnot [string] -or $package.integrity -cnotmatch '^sha512-([A-Za-z0-9+/]{86}==)$' -or
            $package.archiveSha512 -isnot [string] -or $package.archiveSha512 -cnotmatch '^[0-9a-f]{128}$') { throw 'Browser package identity/integrity differs.' }
        $hash = [Convert]::FromBase64String($package.integrity.Substring(7))
        if ([Convert]::ToBase64String($hash) -cne $package.integrity.Substring(7) -or
            [BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant() -cne $package.archiveSha512) { throw 'Browser archive digest differs from npm integrity.' }
        $packages[$package.key] = $package
        $previous = $package.key
    }
    foreach ($package in $Browser.packages) {
        Assert-BrowserReferences $package.dependencies 256 $packages
        $expectedEdges = $pins.browserDependencies.PSObject.Properties[$package.key]
        if ($null -eq $expectedEdges -or (Get-EvidenceSha256 $package.dependencies) -cne (Get-EvidenceSha256 $expectedEdges.Value)) {
            throw 'Browser dependency edges differ from the reviewed lock resolution.'
        }
    }
    if ($Browser.inputs -isnot [array] -or $Browser.inputs.Count -eq 0 -or $Browser.inputs.Count -gt 8192) { throw 'Invalid browser input bound.' }
    $inputs = @{}
    $roles = @{}
    foreach ($role in @('build-script', 'bundle-source', 'lock', 'notice', 'package-metadata', 'static')) { $roles[$role] = $true }
    $buildMaterials = @{
        'node_modules/esbuild' = 'package/lib/main.js'
        'node_modules/@esbuild/win32-x64' = 'package/esbuild.exe'
    }
    $previous = $null
    foreach ($input in $Browser.inputs) {
        Assert-EvidenceKeys $input @('path', 'bytes', 'sha256', 'package', 'entry', 'roles')
        $file = [ordered]@{ path = $input.path; bytes = $input.bytes; sha256 = $input.sha256 }
        Assert-EvidenceFileRecord $file
        if ($inputs.ContainsKey($input.path) -or ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $input.path) -ge 0)) {
            throw 'Duplicate or unordered browser input.'
        }
        Assert-BrowserReferences $input.roles 6 $roles -Nonempty
        if ($null -eq $input.package) {
            if ($null -ne $input.entry -or -not $sourceFiles.ContainsKey($input.path) -or
                (Get-EvidenceSha256 $file) -cne (Get-EvidenceSha256 $sourceFiles[$input.path])) { throw 'Authored browser input differs from source receipt.' }
        } else {
            if ($input.package -isnot [string] -or -not $packages.ContainsKey($input.package) -or
                $input.entry -isnot [string] -or -not $input.entry.StartsWith('package/', [StringComparison]::Ordinal)) { throw 'Invalid npm input owner/entry.' }
            Assert-EvidenceRelativePath $input.entry.Replace('/', '\')
            $expected = "src\Martlet.Avatar.Vrm\$($input.package.Replace('/', '\'))\$($input.entry.Substring(8).Replace('/', '\'))"
            if ($input.path -cne $expected) { throw 'Npm source input path differs from archive owner.' }
            if ($packages[$input.package].scope -ceq 'build' -and $input.roles -ccontains 'bundle-source') { throw 'Build tool falsely contributes runtime bundle bytes.' }
            if ($input.roles -ccontains 'build-script' -and
                ($packages[$input.package].scope -cne 'build' -or -not $buildMaterials.ContainsKey($input.package) -or
                 $input.entry -cne $buildMaterials[$input.package] -or $input.roles.Count -ne 1)) {
                throw 'Unreviewed npm build material.'
            }
        }
        $inputs[$input.path] = $input
        $previous = $input.path
    }
    foreach ($key in $buildMaterials.Keys) {
        $material = @($Browser.inputs | Where-Object {
            $_.package -ceq $key -and $_.entry -ceq $buildMaterials[$key] -and
            $_.roles.Count -eq 1 -and $_.roles[0] -ceq 'build-script'
        })
        if ($material.Count -ne 1) { throw 'Required npm build material missing.' }
        if ($key -ceq 'node_modules/@esbuild/win32-x64') {
            $toolFile = @($Browser.tools[2].files | Where-Object path -CEQ 'esbuild.exe')
            if ($toolFile.Count -ne 1 -or $toolFile[0].sha256 -cne $material[0].sha256 -or $toolFile[0].bytes -ne $material[0].bytes) {
                throw 'Esbuild tool fingerprint differs from verified npm build material.'
            }
        }
    }
    foreach ($package in $Browser.packages) {
        Assert-BrowserReferences $package.notices 8192 $inputs -Nonempty:($package.scope -ceq 'runtime')
        foreach ($notice in $package.notices) {
            if ($inputs[$notice].package -cne $package.key -or $inputs[$notice].roles -cnotcontains 'notice') { throw 'Npm notice ownership differs.' }
        }
        $metadata = @($Browser.inputs | Where-Object { $_.package -ceq $package.key -and $_.entry -ceq 'package/package.json' -and $_.roles -ccontains 'package-metadata' })
        if ($metadata.Count -ne 1) { throw 'Npm package metadata input missing.' }
    }
    foreach ($name in @($recipe.script) + @($recipe.entryPoints) + $locks +
        @('src\Martlet.Avatar.Live2D\package.json', 'src\Martlet.Avatar.Vrm\package.json', 'src\Martlet.Avatar.Live2D\NOTICES.md')) {
        if (-not $inputs.ContainsKey($name) -or $null -ne $inputs[$name].package) { throw 'Required authored browser source missing.' }
    }
    $expectedOutputs = [ordered]@{
        'Desktop\AvatarRenderer\web\THIRD-PARTY-NOTICES.txt' = 'notices'
        'Desktop\AvatarRenderer\web\app.js' = 'bundle'
        'Desktop\AvatarRenderer\web\app.js.LEGAL.txt' = 'legal'
        'Desktop\AvatarRenderer\web\index.html' = 'static'
    }
    if ($Browser.outputs -isnot [array] -or $Browser.outputs.Count -ne 4) { throw 'Browser output inventory differs.' }
    $outputNames = @($expectedOutputs.Keys)
    $noticeInputs = @()
    $bundledInputs = @()
    for ($i = 0; $i -lt 4; $i++) {
        $output = $Browser.outputs[$i]
        Assert-EvidenceKeys $output @('path', 'bytes', 'sha256', 'kind', 'inputs')
        $file = [ordered]@{ path = $output.path; bytes = $output.bytes; sha256 = $output.sha256 }
        Assert-EvidenceFileRecord $file
        if ($output.path -cne $outputNames[$i] -or $output.kind -cne $expectedOutputs[$output.path]) { throw 'Unreviewed browser output.' }
        Assert-BrowserReferences $output.inputs 8192 $inputs -Nonempty
        $actual = Get-EvidenceFileFingerprint (Get-RequiredFile (Join-Path $Root $output.path)) $output.path
        if ((Get-EvidenceSha256 $file) -cne (Get-EvidenceSha256 $actual)) { throw 'Browser output integrity differs.' }
        if ($output.kind -ceq 'bundle') {
            $bundledInputs = $output.inputs
            foreach ($name in $output.inputs) {
                if ($inputs[$name].roles -cnotcontains 'bundle-source') { throw 'Bundle input is not a source contributor.' }
            }
        }
        if ($output.kind -ceq 'notices') {
            $noticeInputs = $output.inputs
            foreach ($name in $output.inputs) {
                if ($inputs[$name].roles -cnotcontains 'notice') { throw 'Combined notices reference a non-notice input.' }
            }
        }
        if ($output.kind -ceq 'static' -and ($output.inputs.Count -ne 1 -or
            $output.inputs[0] -cne 'src\Martlet.Avatar.RendererHost\web\index.html' -or
            $inputs[$output.inputs[0]].sha256 -cne $output.sha256 -or $inputs[$output.inputs[0]].bytes -ne $output.bytes)) { throw 'Static browser output differs from source.' }
    }
    foreach ($input in $Browser.inputs) {
        if ($input.roles -ccontains 'bundle-source' -and $bundledInputs -cnotcontains $input.path) { throw 'Unbound browser bundle source.' }
        if ($input.roles -ccontains 'notice' -and $noticeInputs -cnotcontains $input.path) { throw 'Required browser notice missing from distributed notices.' }
    }
}

function Get-AvatarBrowserEvidence([string]$Root, [string]$PublishDirectory, $Source, [string]$NodePath = 'node') {
    $repository = Split-Path (Split-Path $PSScriptRoot)
    $receipts = @(Get-ChildItem -LiteralPath (Join-Path $PublishDirectory 'build\obj\Martlet.Avatar.RendererHost') -Recurse -File -Filter 'avatar-bundle-receipt.json')
    if ($receipts.Count -ne 1) { throw 'Exactly one actual private-host browser receipt is required.' }
    foreach ($name in @('avatar-bundle-receipt.json', 'avatar-esbuild-metafile.json')) {
        $null = Read-PackagingJson (Join-Path $receipts[0].DirectoryName $name) -AsHashtable
    }
    foreach ($name in @('Live2D', 'Vrm')) {
        $null = Read-PackagingJson (Join-Path $repository "src\Martlet.Avatar.$name\package-lock.json") -AsHashtable
    }
    $node = (Get-Command $NodePath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $result = Invoke-BoundedProcess $node @("$PSScriptRoot\BrowserEvidence.mjs", $repository, $Root, $PublishDirectory, $receipts[0].DirectoryName) 600 -WorkingDirectory $repository
    if ($result.ExitCode -ne 0) { throw "Browser archive/input evidence failed (exit $($result.ExitCode)): $($result.Stderr)" }
    $browser = Read-PackagingJson (Join-Path $PublishDirectory 'browser-evidence.json') -AsHashtable
    Test-AvatarBrowserEvidence $Root $browser $Source
    return $browser
}

function Add-AvatarSbomComponents($Provenance, [string]$Version, $Files, $Components, $Dependencies) {
    $browser = $Provenance.browser
    $script = @($browser.inputs | Where-Object path -CEQ $browser.recipe.script)
    if ($script.Count -ne 1) { throw 'Browser recipe must have exactly one observed input.' }
    $component = [ordered]@{
        type = 'application'; 'bom-ref' = 'AvatarRenderer|browser'; name = 'Martlet offline avatar browser'; version = $Version
        properties = @(
            [ordered]@{ name = 'martlet:publish:context'; value = 'AvatarRenderer' }
            [ordered]@{ name = 'martlet:license:status'; value = 'UNKNOWN - no project license granted' }
            [ordered]@{ name = 'martlet:browser:recipe-sha256'; value = $script[0].sha256 }
            [ordered]@{ name = 'martlet:browser:metafile-sha256'; value = $browser.recipe.metafileSha256 }
            [ordered]@{ name = 'martlet:browser:receipt-sha256'; value = $browser.recipe.receiptSha256 }
        )
        components = @(
            foreach ($output in $browser.outputs) {
                if (-not $Files.ContainsKey($output.path)) { throw "Duplicate or missing browser file ownership: $($output.path)" }
                $file = $Files[$output.path]
                $file.properties[3].value = if ($output.kind -ceq 'static') { 'Verified authored static input' }
                    else { 'Generated browser output; verified input graph, not unchanged archive bytes' }
                $file.properties += [ordered]@{ name = 'martlet:browser:inputs-sha256'; value = (Get-EvidenceSha256 $output.inputs) }
                $file
                $Files.Remove($output.path)
            }
        )
    }
    $Components.Add($component)
    $bundled = @(
        foreach ($package in $browser.packages) {
            if ($package.scope -ceq 'runtime' -and @($browser.inputs | Where-Object {
                    $_.package -ceq $package.key -and $_.roles -ccontains 'bundle-source'
                }).Count) { "AvatarRenderer|npm:$($package.key)" }
        }
    )
    $Dependencies.Add([ordered]@{ ref = 'AvatarRenderer|browser'; dependsOn = @(Get-EvidenceOrdinalStrings $bundled) })
    foreach ($package in $browser.packages) {
        $reference = "AvatarRenderer|npm:$($package.key)"
        $purlName = $package.name.Replace('@', '%40')
        $Components.Add([ordered]@{
            type = 'library'; 'bom-ref' = $reference; name = $package.name; version = $package.version
            purl = "pkg:npm/$purlName@$($package.version)"
            properties = @(
                [ordered]@{ name = 'martlet:publish:context'; value = 'AvatarRenderer' }
                [ordered]@{ name = 'martlet:npm:lock-key'; value = $package.key }
                [ordered]@{ name = 'martlet:npm:integrity'; value = $package.integrity }
                [ordered]@{ name = 'martlet:npm:archive-sha512'; value = $package.archiveSha512 }
                [ordered]@{ name = 'martlet:npm:scope'; value = $package.scope }
                [ordered]@{ name = 'martlet:npm:inputs-sha256'; value = (Get-EvidenceSha256 @($browser.inputs | Where-Object package -CEQ $package.key)) }
                [ordered]@{ name = 'martlet:npm:notices-sha256'; value = (Get-EvidenceSha256 $package.notices) }
                [ordered]@{ name = 'martlet:license:status'; value = 'UPSTREAM DECLARATION ONLY - rights not assessed' }
            )
        })
        $Dependencies.Add([ordered]@{ ref = $reference; dependsOn = @(
            Get-EvidenceOrdinalStrings @($package.dependencies | ForEach-Object { "AvatarRenderer|npm:$_" })
        ) })
    }
}

function Add-AvatarSbomMetadata($Document, $Provenance) {
    $Document.metadata.properties[6].value = 'Actual packaged files, resolved .NET publish dependencies and observed offline JavaScript bundle inputs; upstream vendored/native internals and OS dependencies are not fully decomposed.'
    $Document.metadata.properties += @(
        [ordered]@{ name = 'martlet:browser-evidence:sha256'; value = (Get-EvidenceSha256 $Provenance.browser) }
        [ordered]@{ name = 'martlet:build-archives:sha256'; value = (Get-EvidenceSha256 $Provenance.buildArchives) }
        [ordered]@{ name = 'martlet:payload-format'; value = '3' }
    )
    foreach ($tool in $Provenance.browser.tools) {
        $Document.metadata.tools.components += [ordered]@{
            type = 'application'; name = $tool.name; version = $tool.version
            properties = @(
                [ordered]@{ name = 'martlet:tool:observation'; value = $Provenance.sdk.observation }
                foreach ($file in $tool.files) { [ordered]@{ name = "martlet:tool:sha256:$($file.path)"; value = $file.sha256 } }
            )
        }
    }
    foreach ($archive in $Provenance.buildArchives) {
        $Document.metadata.tools.components += [ordered]@{
            type = 'application'; name = $archive.id; version = $archive.version
            purl = "pkg:nuget/$($archive.id.ToLowerInvariant())@$($archive.version)"
            properties = @(
                [ordered]@{ name = 'martlet:nuget:archive-sha512'; value = $archive.archiveSha512 }
                [ordered]@{ name = 'martlet:nuget:nuspec-sha256'; value = $archive.nuspecSha256 }
                [ordered]@{ name = 'martlet:build:uses-sha256'; value = (Get-EvidenceSha256 $archive.uses) }
                [ordered]@{ name = 'martlet:tool:observation'; value = 'Verified restore-only package archive; not a shipped runtime component.' }
                [ordered]@{ name = 'martlet:license:status'; value = 'UPSTREAM DECLARATION ONLY - rights not assessed' }
            )
        }
    }
}
