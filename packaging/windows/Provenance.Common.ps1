#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-EvidenceObject([object]$Value, [int]$Depth = 0) {
    if ($Depth -gt 64) { throw 'Evidence exceeds the 64-level canonicalization depth bound.' }
    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [array]) {
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in $Value) { $items.Add((ConvertTo-EvidenceObject $item ($Depth + 1))) }
        return ,$items.ToArray()
    }
    if ($Value -is [Collections.IDictionary] -or $Value -is [pscustomobject]) {
        $dictionary = $Value -is [Collections.IDictionary]
        if ($dictionary) { $names = @($Value.psbase.Keys) } else { $names = @($Value.PSObject.Properties | ForEach-Object { $_.Name }) }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in $names) {
            if ($name -isnot [string] -or -not $seen.Add($name)) { throw 'Evidence object keys must be unique, unambiguous strings.' }
        }
        $result = [ordered]@{}
        foreach ($name in Get-EvidenceOrdinalStrings $names) {
            if ($dictionary) { $item = $Value[$name] } else { $item = $Value.PSObject.Properties[$name].Value }
            $result.Add($name, (ConvertTo-EvidenceObject $item ($Depth + 1)))
        }
        return $result
    }
    return $Value
}

function ConvertTo-EvidenceJson([object]$Value) {
    $json = ConvertTo-Json -InputObject (ConvertTo-EvidenceObject $Value) -Depth 64 -WarningAction Stop
    return $json.Replace("`r`n", "`n").Replace("`r", "`n") + "`n"
}

function Get-EvidenceSha256([object]$Value) {
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes((ConvertTo-EvidenceJson $Value))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-EvidenceKeys($Object, [string[]]$Names) {
    if ($Object -is [Collections.IDictionary]) {
        $actual = @($Object.psbase.Keys)
    }
    elseif ($Object -is [pscustomobject]) {
        $actual = @($Object.PSObject.Properties | ForEach-Object { $_.Name })
    }
    else {
        throw 'Evidence must be a JSON object, not an array, scalar or null.'
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Names) {
        if (-not $expected.Add($name)) { throw 'Evidence key specification contains duplicate names.' }
    }
    if ($actual.Count -ne $expected.Count) {
        throw "Evidence must contain exactly these properties: $($Names -join ', ')."
    }
    foreach ($name in $actual) {
        if ($name -isnot [string] -or -not $expected.Remove($name)) {
            throw "Unexpected or incorrectly cased evidence property; expected exactly: $($Names -join ', ')."
        }
    }
}

function Assert-EvidenceRelativePath([string]$Path) {
    if ([string]::IsNullOrEmpty($Path) -or $Path.Length -gt 1024 -or
        [IO.Path]::IsPathRooted($Path) -or $Path -match '[<>:"/|?*\x00-\x1f\x7f-\x9f]') {
        throw 'Evidence paths must be canonical Windows relative paths of 1 to 1024 characters, without roots, drives, controls or invalid filename characters.'
    }
    $null = [Text.UTF8Encoding]::new($false, $true).GetByteCount($Path)
    foreach ($segment in $Path.Split('\')) {
        if ($segment.Length -eq 0 -or $segment -in @('.', '..') -or $segment.EndsWith('.') -or
            $segment.EndsWith(' ') -or
            $segment -match '\A(?i:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])(?:\.|\z)') {
            throw 'Evidence paths cannot contain empty or traversal segments, trailing dots/spaces, or reserved Windows device names.'
        }
    }
}

function Get-EvidenceOrdinalStrings([string[]]$Values) {
    if ($null -eq $Values) { return }
    [string[]]$sorted = @($Values)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return $sorted
}

function Assert-EvidenceNoReparseAncestors([IO.FileSystemInfo]$Item) {
    $current = $Item
    while ($null -ne $current) {
        $current.Refresh()
        if (-not $current.Exists) { throw "Evidence input no longer exists: $($current.FullName)." }
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse points are not supported for evidence inputs: $($current.FullName). Use real files and directories."
        }
        if ($current -is [IO.DirectoryInfo]) { $current = $current.Parent }
        else { $current = $current.Directory }
    }
}

function Get-EvidenceDirectoryItem([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item -isnot [IO.DirectoryInfo]) { throw "Evidence root must be a filesystem directory: $Path." }
    Assert-EvidenceNoReparseAncestors $item
    return $item
}

function Get-EvidenceRelativeItem([IO.DirectoryInfo]$Root, [string]$Path, [switch]$AllowMissing) {
    Assert-EvidenceRelativePath $Path
    $current = $Root
    $segments = $Path.Split('\')
    for ($index = 0; $index -lt $segments.Length; $index++) {
        if ($current -isnot [IO.DirectoryInfo]) {
            throw "Source or tool input traverses a non-directory: $Path."
        }
        $current.Refresh()
        if (-not $current.Exists -or ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Evidence input directory disappeared or became a reparse point: $Path."
        }
        try {
            $current = Get-Item -LiteralPath (Join-Path $current.FullName $segments[$index]) -Force -ErrorAction Stop
        }
        catch [Management.Automation.ItemNotFoundException] {
            if ($AllowMissing) { return $null }
            throw "Required evidence input missing: $Path. Rebuild or reacquire the complete pinned input."
        }
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse points are not supported for evidence inputs: $Path."
        }
    }
    return $current
}

function ConvertFrom-EvidenceJsonElement([Text.Json.JsonElement]$Element, [switch]$AsHashtable) {
    switch ($Element.ValueKind) {
        'Object' {
            $properties = @{}
            foreach ($property in $Element.EnumerateObject()) { $properties.Add($property.Name, $property.Value) }
            $result = [ordered]@{}
            foreach ($name in Get-EvidenceOrdinalStrings @($properties.psbase.Keys)) {
                $result.Add($name, (ConvertFrom-EvidenceJsonElement $properties[$name] -AsHashtable:$AsHashtable))
            }
            if ($AsHashtable) { return $result }
            return [pscustomobject]$result
        }
        'Array' {
            $items = [Collections.Generic.List[object]]::new()
            foreach ($item in $Element.EnumerateArray()) { $items.Add((ConvertFrom-EvidenceJsonElement $item -AsHashtable:$AsHashtable)) }
            return ,$items.ToArray()
        }
        'String' { return $Element.GetString() }
        'Number' {
            $integer = [long]0
            if ($Element.TryGetInt64([ref]$integer)) { return $integer }
            $number = [decimal]0
            if ($Element.TryGetDecimal([ref]$number)) { return $number }
            $number = $Element.GetDouble()
            if ([double]::IsInfinity($number) -or [double]::IsNaN($number)) { throw 'Evidence JSON numbers must be finite.' }
            return $number
        }
        'True' { return $true }
        'False' { return $false }
        'Null' { return $null }
        default { throw 'Unsupported evidence JSON value kind.' }
    }
}

function Read-PackagingJson([string]$Path, [switch]$AsHashtable) {
    $file = Get-RequiredFile $Path
    if ($file -isnot [IO.FileInfo]) { throw "JSON input must be a regular filesystem file: $Path." }
    Assert-EvidenceNoReparseAncestors $file
    $stream = $null
    $document = $null
    try {
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -eq 0 -or $stream.Length -gt 16MB) {
            throw 'JSON input violates the nonempty, 16 MiB file-size bound.'
        }
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 64
        $options.AllowTrailingCommas = $false
        $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
        $document = [Text.Json.JsonDocument]::Parse($stream, $options)
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw 'JSON input must have an object root.'
        }
        $pending = [Collections.Generic.Stack[Text.Json.JsonElement]]::new()
        $pending.Push($document.RootElement)
        while ($pending.Count -ne 0) {
            $element = $pending.Pop()
            if ($element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $element.EnumerateObject()) {
                    if (-not $names.Add($property.Name)) {
                        throw 'JSON contains duplicate or case-ambiguous object properties.'
                    }
                    if ($property.Value.ValueKind -in @([Text.Json.JsonValueKind]::Object, [Text.Json.JsonValueKind]::Array)) {
                        $pending.Push($property.Value)
                    }
                }
            }
            else {
                foreach ($child in $element.EnumerateArray()) {
                    if ($child.ValueKind -in @([Text.Json.JsonValueKind]::Object, [Text.Json.JsonValueKind]::Array)) {
                        $pending.Push($child)
                    }
                }
            }
        }
        return ConvertFrom-EvidenceJsonElement $document.RootElement -AsHashtable:$AsHashtable
    }
    catch [Text.Json.JsonException] {
        throw "Invalid packaging JSON '$Path': $($_.Exception.Message) Supply a nonempty object with unique property names, depth at most 64, and no comments or trailing commas."
    }
    finally {
        if ($null -ne $document) { $document.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

function Test-EvidenceOutputPath([string]$Path) {
    foreach ($root in @('src\Martlet.Avatar.Live2D\node_modules', 'src\Martlet.Avatar.Vrm\node_modules',
            'src\Martlet.Avatar.RendererHost\web\dist')) {
        if ([string]::Equals($Path, $root, [StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    foreach ($segment in $Path.Split('\')) {
        foreach ($excluded in @('.git', 'bin', 'obj', 'artifacts', '.vs', 'TestResults')) {
            if ([string]::Equals($segment, $excluded, [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
    }
    return $false
}

function Get-EvidenceFileFingerprint([IO.FileInfo]$File, [string]$Path) {
    Assert-EvidenceRelativePath $Path
    Assert-EvidenceNoReparseAncestors $File
    $stream = [IO.File]::Open($File.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $length = $stream.Length
        $hash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
        if ($stream.Length -ne $length -or $stream.Position -ne $length) {
            throw "Evidence input changed while being read: $Path. Retry with quiescent inputs."
        }
        return [ordered]@{ path = $Path; bytes = $length; sha256 = $hash }
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Add-EvidenceSourcePath([Collections.Generic.Dictionary[string, string]]$Paths, [string]$Path) {
    Assert-EvidenceRelativePath $Path
    if (Test-EvidenceOutputPath $Path) { return }
    if ($Paths.ContainsKey($Path)) {
        if ($Paths[$Path] -cne $Path) {
            throw "Case-ambiguous repository paths cannot be represented safely on Windows: $Path."
        }
        return
    }
    if ($Paths.Count -ge 16384) { throw 'Source input inventory exceeds the 16384-file evidence bound.' }
    $Paths.Add($Path, $Path)
}

function Get-PackagingSourceReceipt([string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot))) {
    $root = Get-EvidenceDirectoryItem $RepositoryRoot
    $git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $observations = @{}
    foreach ($query in @(
            @{ name = 'root'; arguments = @('rev-parse', '--show-toplevel') },
            @{ name = 'commit'; arguments = @('rev-parse', 'HEAD') },
            @{ name = 'tree'; arguments = @('rev-parse', 'HEAD^{tree}') },
            @{ name = 'status'; arguments = @('status', '--porcelain') },
            @{ name = 'paths'; arguments = @('ls-files', '-z', '--cached', '--others', '--exclude-standard') })) {
        $arguments = @('--no-optional-locks', '-C', $root.FullName) + $query.arguments
        $result = Invoke-BoundedProcess $git $arguments -WorkingDirectory $root.FullName
        if ($result.ExitCode -ne 0) {
            throw "Cannot observe repository $($query.name): git exited $($result.ExitCode). Use an accessible repository with an existing commit and index."
        }
        $observations[$query.name] = $result.Stdout
    }
    $gitRoot = [IO.Path]::GetFullPath($observations.root.Trim()).TrimEnd('\')
    if (-not [string]::Equals($gitRoot, $root.FullName.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source evidence requires the repository worktree root, not a subdirectory.'
    }
    $commit = $observations.commit.Trim()
    $tree = $observations.tree.Trim()
    if ($commit -cnotmatch '\A[0-9a-f]{40}\z' -or $tree -cnotmatch '\A[0-9a-f]{40}\z') {
        throw 'Source evidence requires full, lowercase SHA-1 Git commit and tree identifiers.'
    }
    $paths = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $listing = $observations.paths
    if ($listing.Length -gt (16384 * 1025) -or ($listing.Length -gt 0 -and -not $listing.EndsWith("`0"))) {
        throw 'Git source inventory is oversized or is not a complete NUL-delimited listing.'
    }
    if ($listing.Length -gt 0) {
        foreach ($path in $listing.Substring(0, $listing.Length - 1).Split([char]0)) {
            Add-EvidenceSourcePath $paths $path.Replace('/', '\')
        }
    }
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    foreach ($directory in @('src', 'packaging')) {
        $item = Get-EvidenceRelativeItem $root $directory -AllowMissing
        if ($null -eq $item) { continue }
        if ($item -isnot [IO.DirectoryInfo]) { throw "Expected repository input directory: $directory." }
        $pending.Push($item)
    }
    $directoryCount = 0
    while ($pending.Count -ne 0) {
        $directory = $pending.Pop()
        $directoryCount++
        if ($directoryCount -gt 16384) { throw 'Source input traversal exceeds the 16384-directory evidence bound.' }
        Assert-EvidenceNoReparseAncestors $directory
        foreach ($item in $directory.EnumerateFileSystemInfos()) {
            $relative = [IO.Path]::GetRelativePath($root.FullName, $item.FullName)
            Assert-EvidenceRelativePath $relative
            if (Test-EvidenceOutputPath $relative) { continue }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not supported for repository inputs: $relative."
            }
            if ($item -is [IO.DirectoryInfo]) {
                if (($directoryCount + $pending.Count) -ge 16384) {
                    throw 'Source input traversal exceeds the 16384-directory evidence bound.'
                }
                $pending.Push($item)
            }
            elseif ($item -is [IO.FileInfo]) { Add-EvidenceSourcePath $paths $relative }
            else { throw "Unsupported repository input: $relative." }
        }
    }
    $files = [Collections.Generic.List[object]]::new()
    foreach ($path in (Get-EvidenceOrdinalStrings @($paths.Values))) {
        $item = Get-EvidenceRelativeItem $root $path -AllowMissing
        if ($null -eq $item) {
            $files.Add([ordered]@{ path = $path; bytes = [long]0; sha256 = $null })
        }
        elseif ($item -is [IO.FileInfo]) {
            $files.Add((Get-EvidenceFileFingerprint $item $path))
        }
        else {
            throw "Source inventory entry is not a regular file: $path. Submodules and directory replacements are not supported."
        }
    }
    # Observed repository inputs only: this is neither a hermetic compiler-input closure nor a source archive.
    $receipt = [ordered]@{
        commit = $commit
        tree = $tree
        dirty = [bool]($observations.status.Length -ne 0)
        files = $files.ToArray()
        sha256 = Get-EvidenceSha256 $files.ToArray()
    }
    Test-PackagingSourceReceipt $receipt
    return $receipt
}

function Assert-EvidenceFileRecord($File, [switch]$AllowMissing, [switch]$RequireNonempty) {
    Assert-EvidenceKeys $File @('path', 'bytes', 'sha256')
    if ($File.path -isnot [string]) { throw 'Evidence file paths must be strings.' }
    Assert-EvidenceRelativePath $File.path
    $length = $File.bytes
    if (($length -isnot [byte] -and $length -isnot [sbyte] -and $length -isnot [int16] -and
            $length -isnot [uint16] -and $length -isnot [int32] -and $length -isnot [uint32] -and
            $length -isnot [int64] -and $length -isnot [uint64]) -or
        $length -lt 0 -or $length -gt [long]::MaxValue -or ($RequireNonempty -and $length -eq 0)) {
        throw 'Evidence file bytes must be a nonnegative 64-bit integer (strictly positive for SDK files).'
    }
    if ($null -eq $File.sha256) {
        if (-not $AllowMissing -or $length -ne 0) {
            throw 'Only an absent source file may have a null SHA-256, and its byte count must be zero.'
        }
    }
    elseif ($File.sha256 -isnot [string] -or $File.sha256 -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Evidence file SHA-256 must be exactly 64 lowercase hexadecimal characters.'
    }
}

function Test-PackagingSourceReceipt($Receipt) {
    Assert-EvidenceKeys $Receipt @('commit', 'tree', 'dirty', 'files', 'sha256')
    if ($Receipt.commit -isnot [string] -or $Receipt.commit -cnotmatch '\A[0-9a-f]{40}\z' -or
        $Receipt.tree -isnot [string] -or $Receipt.tree -cnotmatch '\A[0-9a-f]{40}\z' -or
        $Receipt.dirty -isnot [bool]) {
        throw 'Invalid source receipt: expected lowercase 40-character Git identifiers and a Boolean dirty flag.'
    }
    if ($Receipt.files -isnot [array] -or $Receipt.files.Count -gt 16384) {
        throw 'Source receipt files must be an array within the 16384-entry evidence bound.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $previous = $null
    foreach ($file in $Receipt.files) {
        Assert-EvidenceFileRecord $file -AllowMissing
        if ((Test-EvidenceOutputPath $file.path) -or -not $seen.Add($file.path) -or
            ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $file.path) -ge 0)) {
            throw 'Source receipt paths must be unique, Ordinal-sorted repository inputs, excluding Git metadata and build output trees.'
        }
        $previous = $file.path
    }
    if ($Receipt.sha256 -isnot [string] -or $Receipt.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
        $Receipt.sha256 -cne (Get-EvidenceSha256 $Receipt.files)) {
        throw 'Source receipt file-inventory SHA-256 is invalid or does not match its canonical files array.'
    }
}

function Assert-PackagingSourceReceipt($Receipt, [string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot))) {
    Test-PackagingSourceReceipt $Receipt
    $current = Get-PackagingSourceReceipt $RepositoryRoot
    if ((ConvertTo-EvidenceJson $Receipt) -cne (ConvertTo-EvidenceJson $current)) {
        throw 'Source input snapshot differs from the supplied receipt. Rebuild from unchanged repository inputs.'
    }
}

function Get-EvidenceSdkPaths([string]$Version) {
    Assert-EvidenceRelativePath "sdk\$Version\MSBuild.dll"
    return Get-EvidenceOrdinalStrings @('dotnet.exe', "sdk\$Version\MSBuild.dll", "sdk\$Version\Roslyn\bincore\csc.dll")
}

function Get-PackagingSdkReceipt([string]$Sdk) {
    $version = (Get-PackagingPins).sdkVersion
    $file = Get-RequiredFile (Get-Command $Sdk -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    if ($file -isnot [IO.FileInfo] -or $file.Name -ine 'dotnet.exe') {
        throw 'SDK evidence requires the selected Windows dotnet.exe host.'
    }
    Assert-EvidenceNoReparseAncestors $file
    $root = Get-EvidenceDirectoryItem $file.DirectoryName
    $result = Invoke-BoundedProcess $file.FullName @('--version') -WorkingDirectory (Split-Path (Split-Path $PSScriptRoot))
    if ($result.ExitCode -ne 0 -or $result.Stdout.Trim() -cne $version) {
        throw "Selected SDK must report exactly pinned version $version; --version exited $($result.ExitCode). Use the complete pinned SDK."
    }
    $files = [Collections.Generic.List[object]]::new()
    foreach ($path in (Get-EvidenceSdkPaths $version)) {
        $item = Get-EvidenceRelativeItem $root $path
        $required = Get-RequiredFile $item.FullName
        if ($required -isnot [IO.FileInfo]) { throw "Required SDK input must be a regular file: $path." }
        $files.Add((Get-EvidenceFileFingerprint $required $path))
    }
    $receipt = [ordered]@{
        version = $version
        observation = 'Local tool-file fingerprints; not an SDK distribution attestation.'
        files = $files.ToArray()
    }
    Test-PackagingSdkReceipt $receipt
    return $receipt
}

function Test-PackagingSdkReceipt($Receipt) {
    Assert-EvidenceKeys $Receipt @('version', 'observation', 'files')
    $version = (Get-PackagingPins).sdkVersion
    if ($Receipt.version -isnot [string] -or $Receipt.version -cne $version -or
        $Receipt.observation -isnot [string] -or
        $Receipt.observation -cne 'Local tool-file fingerprints; not an SDK distribution attestation.') {
        throw 'SDK receipt must identify the pinned version and the exact local tool-file observation disclaimer.'
    }
    $paths = @(Get-EvidenceSdkPaths $version)
    if ($Receipt.files -isnot [array] -or $Receipt.files.Count -ne $paths.Count) {
        throw 'SDK receipt must contain exactly the dotnet host, MSBuild.dll and Roslyn csc.dll fingerprints.'
    }
    for ($index = 0; $index -lt $paths.Count; $index++) {
        Assert-EvidenceFileRecord $Receipt.files[$index] -RequireNonempty
        if ($Receipt.files[$index].path -cne $paths[$index]) {
            throw 'SDK receipt file paths must exactly match the Ordinal-sorted pinned tool paths.'
        }
    }
}

function Assert-PackagingSdkReceipt($Receipt, [string]$Sdk) {
    Test-PackagingSdkReceipt $Receipt
    $current = Get-PackagingSdkReceipt $Sdk
    if ((ConvertTo-EvidenceJson $Receipt) -cne (ConvertTo-EvidenceJson $current)) {
        throw 'SDK tool-file fingerprints differ from the supplied receipt. Use the unchanged pinned SDK.'
    }
}
