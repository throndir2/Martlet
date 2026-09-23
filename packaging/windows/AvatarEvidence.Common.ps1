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
