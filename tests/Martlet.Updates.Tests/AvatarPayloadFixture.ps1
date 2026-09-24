function New-InertAvatarPayloads([string]$OutputDirectory, $Pins, $Encoding, [string]$Mutation = 'None') {
    $package = $Pins.managedPackages[0]
    $runtime = $Pins.runtimePackages[0]
    $runtimeId = 'Microsoft.NETCore.App.Runtime.win-x64'
    $runtimeKey = "runtimepack.$runtimeId/$($Pins.runtimeVersion)"
    $packageKey = "$($package.id)/$($package.version)"
    $webKey = 'Microsoft.Web.WebView2/1.0.4191.47'
    $audioKey = 'Martlet.Avatar.Audio2Face/0.1.0'
    $memoryKey = 'Martlet.Memory/0.1.0'
    $contentHash = [Convert]::ToBase64String([byte[]]::new(64))
    $sourceFiles = [Collections.Generic.List[object]]::new()
    function Source([string]$Path, [string]$Text) {
        $bytes = $Encoding.GetBytes($Text)
        $record = [ordered]@{ path = $Path; bytes = $bytes.Length; sha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes)) }
        $sourceFiles.Add($record)
        return $record
    }
    $projects = @('Martlet.Desktop', 'Martlet.Doctor', 'Martlet.Avatar.RendererHost', 'Martlet.Avatar.Audio2Face', 'Martlet.Memory')
    foreach ($project in $projects) { $null = Source "packaging\windows\locks\$project.packages.lock.json" "inert $project lock" }
    $webSource = 'src\Martlet.Avatar.RendererHost\web'
    $browserInputs = [Collections.Generic.List[object]]::new()
    function Authored([string]$Path, [string]$Text, [string]$Role) {
        $record = Source $Path $Text
        $browserInputs.Add([ordered]@{ path = $record.path; bytes = $record.bytes; sha256 = $record.sha256; package = $null; entry = $null; roles = @($Role) })
        return $record
    }
    $null = Authored "$webSource\app.js" 'INERT browser source, never execute' 'bundle-source'
    $null = Authored "$webSource\build.mjs" 'INERT build script, never execute' 'build-script'
    $static = Authored "$webSource\index.html" 'INERT static HTML, never render' 'static'
    $locks = @(
        foreach ($module in @('Live2D', 'Vrm')) {
            $null = Authored "src\Martlet.Avatar.$module\package.json" "INERT $module package" 'package-metadata'
            Authored "src\Martlet.Avatar.$module\package-lock.json" "INERT $module lock" 'lock'
        }
    )
    $npmGraph = @{
        '@esbuild/win32-x64' = @()
        '@pixiv/three-vrm' = @('@pixiv/three-vrm-core', '@pixiv/three-vrm-materials-hdr-emissive-multiplier', '@pixiv/three-vrm-materials-mtoon', '@pixiv/three-vrm-materials-v0compat', '@pixiv/three-vrm-node-constraint', '@pixiv/three-vrm-springbone', 'three')
        '@pixiv/three-vrm-core' = @('@pixiv/types-vrm-0.0', '@pixiv/types-vrmc-vrm-1.0', 'three')
        '@pixiv/three-vrm-materials-hdr-emissive-multiplier' = @('@pixiv/types-vrmc-materials-hdr-emissive-multiplier-1.0', 'three')
        '@pixiv/three-vrm-materials-mtoon' = @('@pixiv/types-vrm-0.0', '@pixiv/types-vrmc-materials-mtoon-1.0', 'three')
        '@pixiv/three-vrm-materials-v0compat' = @('@pixiv/types-vrm-0.0', '@pixiv/types-vrmc-materials-mtoon-1.0', 'three')
        '@pixiv/three-vrm-node-constraint' = @('@pixiv/types-vrmc-node-constraint-1.0', 'three')
        '@pixiv/three-vrm-springbone' = @('@pixiv/types-vrm-0.0', '@pixiv/types-vrmc-springbone-1.0', '@pixiv/types-vrmc-springbone-extended-collider-1.0', 'three')
        '@pixiv/types-vrm-0.0' = @()
        '@pixiv/types-vrmc-materials-hdr-emissive-multiplier-1.0' = @()
        '@pixiv/types-vrmc-materials-mtoon-1.0' = @()
        '@pixiv/types-vrmc-node-constraint-1.0' = @()
        '@pixiv/types-vrmc-springbone-1.0' = @()
        '@pixiv/types-vrmc-springbone-extended-collider-1.0' = @()
        '@pixiv/types-vrmc-vrm-1.0' = @()
        'esbuild' = @('@esbuild/win32-x64')
        'three' = @()
    }
    $npmPackages = @(
        foreach ($name in Get-EvidenceOrdinalStrings @($npmGraph.Keys)) {
            $buildTool = $name -cin @('esbuild', '@esbuild/win32-x64')
            $description = @{
                name = $name; version = $(if ($buildTool) { '0.25.12' } elseif ($name -ceq 'three') { '0.180.0' } else { '3.5.5' })
                scope = $(if ($buildTool) { 'build' } else { 'runtime' })
                dependencies = @($npmGraph[$name] | ForEach-Object { "node_modules/$_" })
            }
            $key = "node_modules/$($description.name)"
            $notices = @()
            $entries = @('package.json')
            if ($name -ceq '@esbuild/win32-x64') { $entries += 'esbuild.exe' }
            if ($name -ceq 'esbuild') { $entries += 'lib/main.js' }
            if ($description.scope -ceq 'runtime') {
                $entries += 'LICENSE'
                if ($name -cnotlike '@pixiv/types-*') { $entries += 'dist/index.js' }
            }
            foreach ($entry in $entries) {
                $text = "INERT $key $entry material"
                $bytes = $Encoding.GetBytes($text)
                $path = 'src\Martlet.Avatar.Vrm\' + $key.Replace('/', '\') + '\' + $entry.Replace('/', '\')
                $role = if ($entry -ceq 'package.json') { 'package-metadata' } elseif ($buildTool) { 'build-script' }
                    elseif ($entry -ceq 'LICENSE') { 'notice' } else { 'bundle-source' }
                if ($role -ceq 'notice') { $notices += $path }
                $browserInputs.Add([ordered]@{
                    path = $path; bytes = $bytes.Length; sha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
                    package = $key; entry = "package/$entry"; roles = @($role)
                })
            }
            [ordered]@{
                key = $key; name = $description.name; version = $description.version; scope = $description.scope
                integrity = "sha512-$contentHash"; archiveSha512 = '0' * 128
                dependencies = $description.dependencies; notices = $notices
            }
        }
    )
    $inputMap = @{}
    foreach ($input in $browserInputs) { $inputMap[$input.path] = $input }
    $orderedInputs = @(foreach ($path in Get-EvidenceOrdinalStrings @($inputMap.Keys)) { $inputMap[$path] })
    $esbuildInput = @($orderedInputs | Where-Object { $_.package -ceq 'node_modules/@esbuild/win32-x64' -and $_.entry -ceq 'package/esbuild.exe' })[0]
    $sourceMap = @{}
    foreach ($record in $sourceFiles) { $sourceMap[$record.path] = $record }
    $sourceList = @(foreach ($path in Get-EvidenceOrdinalStrings @($sourceMap.Keys)) { $sourceMap[$path] })
    $source = [ordered]@{ commit = 'a' * 40; tree = 'b' * 40; dirty = $true; files = $sourceList; sha256 = (Get-EvidenceSha256 $sourceList) }
    $sdk = [ordered]@{
        version = $Pins.sdkVersion; observation = 'Local tool-file fingerprints; not an SDK distribution attestation.'
        files = @(foreach ($path in Get-EvidenceSdkPaths $Pins.sdkVersion) { [ordered]@{ path = $path; bytes = 1; sha256 = 'c' * 64 } })
    }
    foreach ($version in @('0.2.0.0', '0.3.0.0')) {
        $root = Join-Path $OutputDirectory $version
        New-OutputDirectory $root
        function Leaf([string]$Path, [string]$Text) {
            $destination = Join-Path $root $Path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.File]::WriteAllText($destination, $Text, $Encoding)
            return [ordered]@{ path = $Path; bytes = (Get-Item -LiteralPath $destination).Length; sha256 = (Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() }
        }
        function Library([string]$Key, [string]$Type, [object[]]$Dependencies, [object[]]$Assets) {
            [ordered]@{
                key = $Key; type = $Type; contentHash = $(if ($Type -ceq 'package') { $contentHash } else { $null })
                dependencies = @(Get-EvidenceOrdinalStrings $Dependencies); assets = $Assets
            }
        }
        function Restored($Library) {
            [ordered]@{ key = $Library.key; type = $Library.type; contentHash = $Library.contentHash; dependencies = @($Library.dependencies | Where-Object { $_ -cnotlike 'runtimepack.*' -and $_ -cnotlike 'Microsoft.Web.WebView2.*/*' }) }
        }
        $applications = @()
        $restores = @()
        $packageOrigins = @()
        $runtimeOrigins = @()
        $webOrigins = @()
        $sdkOrigins = @()
        foreach ($context in Get-PublishContexts) {
            $directory = $context.directory
            $project = $context.project
            foreach ($extension in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
                $null = Leaf "$directory\$project.$extension" "INERT SYNTHETIC $project $extension; NEVER EXECUTE"
            }
            $rootDependencies = @($runtimeKey)
            $libraries = @()
            if ($context.name -ceq 'AvatarRenderer') {
                if ($Mutation -ceq 'WindowsSdk') {
                    $sdkKey = "runtimepack.$($Pins.windowsSdkPackage.id)/$($Pins.windowsSdkPackage.version)"
                    $rootDependencies += $sdkKey
                    $sdkAssets = @(
                        foreach ($asset in Get-WindowsSdkArchiveAssets) {
                            $file = Leaf $asset.path 'INERT Windows SDK projection; NEVER EXECUTE'
                            $sdkOrigins += [ordered]@{ path = $file.path; component = $asset.component; entry = $asset.entry; sha256 = $file.sha256 }
                            [ordered]@{ path = $file.path; kind = 'runtime'; source = [IO.Path]::GetFileName($file.path) }
                        }
                    )
                    $libraries += Library $sdkKey 'runtimepack' @() $sdkAssets
                    $null = Leaf 'notices\Microsoft.Windows.SDK.NET.Ref-LICENSE.rtf' 'INERT synthetic license marker, not actual licensing evidence'
                }
                $rootDependencies += $webKey
                $loader = Leaf "$directory\WebView2Loader.dll" 'INERT identical loader copies'
                $null = Leaf "$directory\runtimes\win-x64\native\WebView2Loader.dll" 'INERT identical loader copies'
                $libraries += Library $webKey 'package' @() @([ordered]@{ path = $loader.path; kind = 'native'; source = 'runtimes/win-x64/native/WebView2Loader.dll' })
                foreach ($name in @('Core', 'WinForms', 'Wpf')) {
                    $key = "Microsoft.Web.WebView2.$name/1.0.4191.47"
                    $rootDependencies += $key
                    $dll = Leaf "$directory\Microsoft.Web.WebView2.$name.dll" "INERT WebView $name dll"
                    $null = Leaf "$directory\Microsoft.Web.WebView2.$name.xml" "INERT WebView $name xml"
                    $libraries += Library $key 'reference' @() @([ordered]@{ path = $dll.path; kind = 'runtime'; source = "Microsoft.Web.WebView2.$name.dll" })
                }
                foreach ($asset in Get-WebViewArchiveAssets) {
                    $webOrigins += [ordered]@{
                        path = $asset.path; component = $asset.component; entry = $asset.entry
                        sha256 = (Get-FileHash -LiteralPath (Join-Path $root $asset.path)).Hash.ToLowerInvariant()
                    }
                }
            } else {
                $rootDependencies += $packageKey
                $upstream = Leaf "$directory\upstream.dll" "INERT $directory upstream"
                $libraries += Library $packageKey 'package' @() @([ordered]@{ path = $upstream.path; kind = 'runtime'; source = $package.runtimeAsset })
                $packageOrigins += [ordered]@{ path = $upstream.path; component = "$($context.name)|$packageKey"; entry = $package.runtimeAsset; sha256 = $upstream.sha256 }
                if ($context.name -ceq 'Desktop') {
                    $rootDependencies += $audioKey
                    $audio = Leaf "$directory\Martlet.Avatar.Audio2Face.dll" 'INERT generated-project output'
                    $libraries += Library $audioKey 'project' @() @([ordered]@{ path = $audio.path; kind = 'runtime'; source = 'Martlet.Avatar.Audio2Face.dll' })
                    $rootDependencies += $memoryKey
                    $memoryFile = Leaf "$directory\Martlet.Memory.dll" 'INERT memory-project output'
                    $libraries += Library $memoryKey 'project' @() @([ordered]@{ path = $memoryFile.path; kind = 'runtime'; source = 'Martlet.Memory.dll' })
                }
            }
            $runtimeFile = Leaf "$directory\coreclr.dll" "INERT $directory runtime"
            $libraries += Library $runtimeKey 'runtimepack' @() @([ordered]@{ path = $runtimeFile.path; kind = 'native'; source = 'coreclr.dll' })
            $runtimeOrigins += [ordered]@{ path = $runtimeFile.path; component = "$($context.name)|$runtimeKey"; entry = 'runtimes/win-x64/native/coreclr.dll'; sha256 = $runtimeFile.sha256 }
            $rootLibrary = Library "$project/0.1.0" 'project' $rootDependencies @([ordered]@{ path = "$directory\$project.dll"; kind = 'runtime'; source = "$project.dll" })
            $libraries += $rootLibrary
            $libraryMap = @{}
            foreach ($library in $libraries) { $libraryMap[$library.key] = $library }
            $libraries = @(foreach ($key in Get-EvidenceOrdinalStrings @($libraryMap.Keys)) { $libraryMap[$key] })
            $restoreLibraries = @($libraries | Where-Object { $_.key -cne $rootLibrary.key -and $_.type -cin @('project', 'package') } | ForEach-Object { Restored $_ })
            $restoreRoots = @($rootLibrary.dependencies | Where-Object { $_ -cnotlike 'runtimepack.*' -and $_ -cnotlike 'Microsoft.Web.WebView2.*/*' })
            $applications += [ordered]@{
                name = $context.name; project = $project; directory = $directory; parent = $context.parent
                target = '.NETCoreApp,Version=v10.0/win-x64'; buildOnlyLibraries = @(); libraries = $libraries
            }
            $lockPath = "packaging\windows\locks\$project.packages.lock.json"
            $restores += [ordered]@{
                project = $project; path = "src\$project\$project.csproj"; version = '0.1.0'
                lockPath = $lockPath; lockSha256 = $sourceMap[$lockPath].sha256; sourceSetSha256 = 'e' * 64
                targets = @([ordered]@{
                    name = 'net10.0-windows/win-x64'; framework = 'net10.0-windows7.0'
                    rootDependencies = $restoreRoots; libraries = @($restoreLibraries | Sort-Object key -CaseSensitive)
                    frameworkDownloads = @([ordered]@{ id = $runtimeId; requested = "[$($Pins.runtimeVersion), $($Pins.runtimeVersion)]" })
                })
            }
            if ($context.name -ceq 'AvatarRenderer' -and $Mutation -ceq 'WindowsSdk') {
                $target = $restores[-1].targets[0]
                $target.name = 'net10.0-windows10.0.19041.0/win-x64'
                $target.framework = 'net10.0-windows10.0.19041'
                $target.frameworkDownloads += [ordered]@{
                    id = $Pins.windowsSdkPackage.id
                    requested = "[$($Pins.windowsSdkPackage.version), $($Pins.windowsSdkPackage.version)]"
                }
            }
        }
        $toolLock = 'packaging\windows\locks\Martlet.Avatar.Audio2Face.packages.lock.json'
        $restores += [ordered]@{
            project = 'Martlet.Avatar.Audio2Face'; path = 'src\Martlet.Avatar.Audio2Face\Martlet.Avatar.Audio2Face.csproj'; version = '0.1.0'
            lockPath = $toolLock; lockSha256 = $sourceMap[$toolLock].sha256; sourceSetSha256 = 'e' * 64
            targets = @([ordered]@{
                name = 'net10.0/win-x64'; framework = 'net10.0'; rootDependencies = @('Grpc.Tools/2.84.0')
                libraries = @([ordered]@{ key = 'Grpc.Tools/2.84.0'; type = 'package'; contentHash = $contentHash; dependencies = @() })
                frameworkDownloads = @()
            })
        }
        $memoryLock = 'packaging\windows\locks\Martlet.Memory.packages.lock.json'
        $restores += [ordered]@{
            project = 'Martlet.Memory'; path = 'src\Martlet.Memory\Martlet.Memory.csproj'; version = '0.1.0'
            lockPath = $memoryLock; lockSha256 = $sourceMap[$memoryLock].sha256; sourceSetSha256 = 'e' * 64
            targets = @([ordered]@{
                name = 'net10.0/win-x64'; framework = 'net10.0'; rootDependencies = @()
                libraries = @(); frameworkDownloads = @()
            })
        }
        $archives = @(
            [ordered]@{ id = $package.id; version = $package.version; archiveSha512 = $package.sha512; nuspecSha256 = 'f' * 64; licenseExpression = 'MIT'; licenseFile = $null; repositoryUrl = 'https://example.invalid/synthetic'; repositoryCommit = $null; origins = $packageOrigins }
            [ordered]@{ id = $runtime.id; version = $Pins.runtimeVersion; archiveSha512 = $runtime.sha512; nuspecSha256 = 'f' * 64; licenseExpression = $null; licenseFile = 'LICENSE.txt'; repositoryUrl = $null; repositoryCommit = $null; origins = $runtimeOrigins }
            [ordered]@{ id = 'Microsoft.Web.WebView2'; version = '1.0.4191.47'; archiveSha512 = '1' * 128; nuspecSha256 = 'f' * 64; licenseExpression = $null; licenseFile = 'LICENSE.txt'; repositoryUrl = $null; repositoryCommit = $null; origins = $webOrigins }
        )
        if ($Mutation -ceq 'WindowsSdk') {
            $archives += [ordered]@{
                id = $Pins.windowsSdkPackage.id; version = $Pins.windowsSdkPackage.version
                archiveSha512 = $Pins.windowsSdkPackage.sha512; nuspecSha256 = 'f' * 64
                licenseExpression = $null; licenseFile = $null; repositoryUrl = $null; repositoryCommit = $null
                origins = $sdkOrigins
            }
        }
        $buildArchives = @([ordered]@{
            id = 'Grpc.Tools'; version = '2.84.0'; archiveSha512 = '2' * 128; nuspecSha256 = 'f' * 64
            licenseExpression = 'Apache-2.0'; licenseFile = $null; repositoryUrl = $null; repositoryCommit = $null
            uses = @([ordered]@{ project = 'Martlet.Avatar.Audio2Face'; target = 'net10.0/win-x64'; key = 'Grpc.Tools/2.84.0'; contentHash = $contentHash })
        })
        $bundleInputs = @($orderedInputs | Where-Object { $_.roles -ccontains 'bundle-source' } | ForEach-Object path)
        $noticeInputs = @($orderedInputs | Where-Object { $_.roles -ccontains 'notice' } | ForEach-Object path)
        $outputs = @(
            foreach ($description in @(
                @{ file = 'THIRD-PARTY-NOTICES.txt'; kind = 'notices'; inputs = $noticeInputs; text = 'INERT complete synthetic notices' },
                @{ file = 'app.js'; kind = 'bundle'; inputs = $bundleInputs; text = 'INERT generated bundle; never execute' },
                @{ file = 'app.js.LEGAL.txt'; kind = 'legal'; inputs = $bundleInputs; text = 'INERT generated legal comments' },
                @{ file = 'index.html'; kind = 'static'; inputs = @($static.path); text = 'INERT static HTML, never render' }
            )) {
                $file = Leaf "Desktop\AvatarRenderer\web\$($description.file)" $description.text
                [ordered]@{ path = $file.path; bytes = $file.bytes; sha256 = $file.sha256; kind = $description.kind; inputs = $description.inputs }
            }
        )
        $browser = [ordered]@{
            schemaVersion = 1; context = 'AvatarRenderer'
            recipe = [ordered]@{ script = "$webSource\build.mjs"; entryPoints = @("$webSource\app.js"); metafileSha256 = 'a' * 64; receiptSha256 = 'b' * 64 }
            lockFiles = $locks
            tools = @(
                foreach ($tool in @(@('node', '20.11.1', 'node.exe'), @('npm', '10.2.4', 'bin\npm-cli.js'), @('esbuild', '0.25.12', 'esbuild.exe'))) {
                    $file = if ($tool[0] -ceq 'esbuild') { [ordered]@{ path = $tool[2]; bytes = $esbuildInput.bytes; sha256 = $esbuildInput.sha256 } }
                        else { [ordered]@{ path = $tool[2]; bytes = 1; sha256 = 'c' * 64 } }
                    [ordered]@{ name = $tool[0]; version = $tool[1]; files = @($file) }
                }
            )
            packages = $npmPackages; inputs = $orderedInputs; outputs = $outputs
        }
        $canary = (Join-Path $OutputDirectory 'PAYLOAD-EXECUTED').Replace("'", "''")
        $null = Leaf 'help\do-not-run.ps1' "[IO.File]::WriteAllText('$canary','BAD')"
        $null = Leaf 'notices\INTERNAL.txt' 'Inert synthetic graph; no publisher or native qualification.'
        if ($Mutation.StartsWith('Project', [StringComparison]::Ordinal)) {
            $path = switch ($Mutation) {
                'ProjectLoader' { 'Desktop\AvatarRenderer\runtimes\win-arm64\native\WebView2Loader.dll' }
                'ProjectWebViewAlias' { 'Desktop\AvatarRenderer\Microsoft.Web.WebView2.Unknown.dll' }
                'ProjectWebViewXml' { 'Desktop\AvatarRenderer\Microsoft.Web.WebView2.Unknown.xml' }
                'ProjectRuntimeInstaller' { 'Desktop\AvatarRenderer\MicrosoftEdgeWebView2Setup.exe' }
                'ProjectSiblingLoader' { 'Desktop\AvatarRendererX\WebView2Loader.dll' }
                default { throw 'Unknown explicit project mutation.' }
            }
            $extra = Leaf $path 'INERT unsupported project claim'
            $context = if ($Mutation -ceq 'ProjectSiblingLoader') { $applications[0] } else { $applications[2] }
            $projectRoot = @($context.libraries | Where-Object key -CEQ "$($context.project)/0.1.0")[0]
            $projectRoot.assets += [ordered]@{ path = $extra.path; kind = 'native'; source = $path.Replace('\', '/') }
        }
        if ($Mutation -ceq 'OmittedRuntimeEdge') {
            $applications[0].buildOnlyLibraries = @($webKey)
            $desktopRestore = @($restores | Where-Object project -CEQ 'Martlet.Desktop')[0].targets[0]
            $desktopRestore.libraries += [ordered]@{ key = $webKey; type = 'package'; contentHash = $contentHash; dependencies = @() }
            $audio = @($desktopRestore.libraries | Where-Object key -CEQ $audioKey)[0]
            $audio.dependencies = @($webKey)
        }
        $provenance = New-PackageProvenanceDocument $source $sdk $applications $restores $archives $Pins.rid -SchemaVersion 2 -Browser $browser -BuildArchives $buildArchives
        $sbom = New-PackageSbomDocument @(Get-PayloadFiles $root) $version $provenance
        [IO.File]::WriteAllText((Join-Path $root 'sbom.cdx.json'), (ConvertTo-EvidenceJson $sbom), $Encoding)
        $files = @(Get-PayloadFiles $root)
        $manifest = New-PayloadManifestDocument $version $source.commit $source.dirty $provenance $files $Pins -FormatVersion 3
        [IO.File]::WriteAllText((Join-Path $root 'manifest.json'), (ConvertTo-EvidenceJson $manifest), $Encoding)
        [IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'), (Get-ChecksumText $files (Get-FileHash -LiteralPath (Join-Path $root 'manifest.json')).Hash), $Encoding)
    }
    $vectors = @(
        [ordered]@{ z = @(); single = @(1); nullValue = $null; bool = $true; nested = @([ordered]@{ value = "$([char]0x00e9)$([char]0x6f22)<>&'`"\`n`r`t"; count = [long]9007199254740993 }) }
        [ordered]@{ empty = @{}; text = "$([char]0x85)$([char]0x2028)$([char]0x2029)"; path = 'src\name space\file.txt' }
    )
    for ($index = 0; $index -lt $vectors.Count; $index++) {
        [IO.File]::WriteAllText((Join-Path $OutputDirectory "canonical-$index.json"), (ConvertTo-EvidenceJson $vectors[$index]), $Encoding)
    }
}
