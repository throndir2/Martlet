#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ProtectedDataDirectory([string]$Path, [string[]]$ExcludedPaths) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path) -or
        $Path.StartsWith('\\') -or $Path.Substring(2).Contains(':')) {
        throw 'Protected data scope must be an explicit absolute local directory.'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($full)
    if ($full -eq $root.TrimEnd('\') -or [IO.DriveInfo]::new($root).DriveType -ne [IO.DriveType]::Fixed) {
        throw 'Protected data scope must be a non-root local fixed-drive directory.'
    }
    foreach ($excluded in $ExcludedPaths) {
        $other = [IO.Path]::GetFullPath($excluded).TrimEnd('\')
        if ($full.Equals($other, [StringComparison]::OrdinalIgnoreCase) -or
            $full.StartsWith($other + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $other.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Protected data scope overlaps an application data, payload or output scope.'
        }
    }
    $ancestor = $full
    while ($ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Protected data scope and ancestors must be existing directories without reparse points.'
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $full
}

function Get-ProtectedDataSnapshot([string]$Directory) {
    # Deliberately shallow; the explicit sentinel scope contains only test-owned files.
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force | Sort-Object Name)
    if ($entries.Count -gt 32 -or @($entries | Where-Object {
        $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Length -gt 1MB
    }).Count -gt 0) { throw 'Protected test scope must contain at most 32 bounded ordinary files, without directories or links.' }
    return (@($entries | ForEach-Object {
        "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$($_.Attributes)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }) -join "`n")
}

function Assert-ProtectedDataUnchanged([string]$Directory, [string]$Before) {
    if ((Get-ProtectedDataSnapshot $Directory) -cne $Before) {
        throw 'The selected protected scope changed during smoke. Stop and investigate; no automatic restore attempted.'
    }
}
