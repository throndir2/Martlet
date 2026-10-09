#Requires -Version 7.2
<#
.SYNOPSIS
Frees the disk space that Martlet builds, tests and agent sessions leave behind. Never deletes tracked files or
uncommitted work.

.DESCRIPTION
By default, cleans this checkout only:

- build and test output that Git ignores: every bin\, obj\, node_modules\, dist\, dev-dist\, live2d-dist\,
  TestResults\ and __pycache__\ folder, and artifacts\ (validation logs and summary.md);
- disposable Martlet folders in %TEMP% older than -TempAgeHours (named Martlet.<name>.<guid> or
  martlet-<name>-<guid>; MCP verification, Doctor smoke runs and tests make them).

A folder is removed only when Git lists it as ignored and untracked (git ls-files --others --ignored
--directory) and no file in it is tracked. Tracked files, untracked source files and the Live2D SDK files you
put in local-sdk\ or vendor\ stay. The next build makes the output again (npm ci restores node_modules).

-Git also does housekeeping on the shared Git folder that all worktrees of this repository use:

- git worktree prune: removes registrations whose worktree folder is gone;
- git fetch origin --prune: removes remote-tracking branches deleted on GitHub;
- deletes local branches that are fully merged into origin/main and not checked out in a worktree;
- removes [branch] sections in the Git config for branches that do not exist;
- deletes .lock files older than -LockAgeMinutes (a stopped Git process left them);
- git gc: packs loose objects. It keeps unreachable objects for the default two weeks, so it is safe while
  other worktrees use the repository.

-AllWorktrees cleans the build output of every worktree of this repository, not only this checkout. It is for
the developer; agents use it only when the developer asks.

-WhatIf shows what the script would remove, with sizes, and changes nothing. See docs/CLEANUP.md.

.EXAMPLE
.\scripts\Clean-Martlet.ps1 -WhatIf
Shows the build output and temporary folders this checkout would free, and changes nothing.

.EXAMPLE
.\scripts\Clean-Martlet.ps1
Removes this checkout's build output and old disposable temporary folders. Agents run this before they finish.

.EXAMPLE
.\scripts\Clean-Martlet.ps1 -Git -WhatIf
Also shows the Git housekeeping it would do: stale worktrees, merged branches, config sections, lock files.

.EXAMPLE
.\scripts\Clean-Martlet.ps1 -AllWorktrees -Git
Developer only: cleans the build output of every worktree, then does the Git housekeeping.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Git,
    [switch]$AllWorktrees,
    [switch]$KeepNodeModules,
    [switch]$SkipTemp,
    [int]$TempAgeHours = 24,
    [int]$LockAgeMinutes = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$generated = @('bin', 'obj', 'node_modules', 'dist', 'dev-dist', 'live2d-dist', 'TestResults', '__pycache__')
if ($KeepNodeModules) { $generated = $generated | Where-Object { $_ -ne 'node_modules' } }
$enumeration = [IO.EnumerationOptions]@{ RecurseSubdirectories = $true; IgnoreInaccessible = $true; AttributesToSkip = 0 }
$report = [Collections.Generic.List[object]]::new()

function Invoke-Git([string]$Directory, [string[]]$Arguments) {
    $output = & git -C $Directory @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed in $Directory (exit code $LASTEXITCODE)." }
    $output
}

function Measure-Folder([string]$Path) {
    $bytes = 0L; $files = 0
    foreach ($file in [IO.DirectoryInfo]::new($Path).EnumerateFiles('*', $enumeration)) { $bytes += $file.Length; $files++ }
    [pscustomobject]@{ Bytes = $bytes; Files = $files }
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { '{0:N2} GB' -f ($Bytes / 1GB) } elseif ($Bytes -ge 1MB) { '{0:N1} MB' -f ($Bytes / 1MB) } else { '{0:N0} KB' -f ($Bytes / 1KB) }
}

function Remove-Folder([string]$Path, [string]$Kind) {
    $size = Measure-Folder $Path
    $result = 'would remove'
    if ($PSCmdlet.ShouldProcess($Path, "Remove $Kind ($(Format-Size $size.Bytes), $($size.Files) files)")) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable failures
        $result = if (-not (Test-Path -LiteralPath $Path)) { 'removed' } else { "partly removed: $($failures.Count) items in use" }
    }
    $report.Add([pscustomobject]@{ Kind = $Kind; Path = $Path; Size = $size.Bytes; Files = $size.Files; Result = $result })
}

# Ignored, untracked folders with a generated name. --directory shows a folder only when nothing in it is tracked.
function Get-BuildOutput([string]$Checkout) {
    $listing = (Invoke-Git $Checkout @('ls-files', '--others', '--ignored', '--exclude-standard', '--directory', '-z')) -join ''
    $folders = @($listing -split "`0" | Where-Object {
            $_.EndsWith('/') -and ($_ -eq 'artifacts/' -or (Split-Path $_.TrimEnd('/') -Leaf) -in $generated)
        })
    if (-not $folders.Count) { return @() }
    $tracked = @(((Invoke-Git $Checkout (@('ls-files', '-z', '--') + $folders)) -join '') -split "`0" | Where-Object { $_ })
    foreach ($folder in $folders) {
        if ($tracked | Where-Object { $_.StartsWith($folder) } | Select-Object -First 1) {
            Write-Warning "Kept ${folder} in ${Checkout}: it holds tracked files."
            continue
        }
        Join-Path $Checkout $folder.TrimEnd('/')
    }
}

$checkouts = @($root)
if ($AllWorktrees) {
    $checkouts = @(Invoke-Git $root @('worktree', 'list', '--porcelain') | Where-Object { $_ -like 'worktree *' } |
            ForEach-Object { [IO.Path]::GetFullPath($_.Substring(9)) } | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
}
foreach ($checkout in $checkouts) {
    Write-Host "Build output in $checkout"
    try { $folders = @(Get-BuildOutput $checkout) }
    catch { Write-Warning "Skipped ${checkout}: $($_.Exception.Message)"; continue }
    foreach ($folder in $folders) {
        try { Remove-Folder $folder 'build output' }
        catch { Write-Warning "Skipped ${folder}: $($_.Exception.Message)" }
    }
}

if (-not $SkipTemp) {
    $before = (Get-Date).AddHours(-$TempAgeHours)
    $temp = [IO.Path]::GetTempPath()
    Write-Host "Disposable Martlet folders in $temp older than $TempAgeHours hours"
    Get-ChildItem -LiteralPath $temp -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(?i)martlet[.-].*[.-][0-9a-f]{32}$' -and $_.LastWriteTime -lt $before } |
        ForEach-Object { Remove-Folder $_.FullName 'temporary folder' }
}

if ($Git) {
    $common = (Invoke-Git $root @('rev-parse', '--path-format=absolute', '--git-common-dir') | Select-Object -First 1).Trim()
    $common = [IO.Path]::GetFullPath($common)
    Write-Host "Git housekeeping in $common"

    $prune = if ($PSCmdlet.ShouldProcess($common, 'git worktree prune')) { @('worktree', 'prune', '--verbose') } else { @('worktree', 'prune', '--dry-run', '--verbose') }
    $pruned = @(Invoke-Git $root $prune 2>&1 | ForEach-Object { "$_" })
    $pruned | Write-Host
    Write-Host "Worktree registrations whose folder is gone: $($pruned.Count)."

    if ($PSCmdlet.ShouldProcess('origin', 'git fetch --prune')) { & git -C $root fetch origin --prune --quiet }
    else { & git -C $root remote prune origin --dry-run }
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not reach origin; merged branches are judged by the last fetched origin/main.' }

    $checkedOut = @(Invoke-Git $root @('worktree', 'list', '--porcelain') | Where-Object { $_ -like 'branch refs/heads/*' } |
            ForEach-Object { $_.Substring('branch refs/heads/'.Length) })
    $merged = @(Invoke-Git $root @('for-each-ref', '--merged', 'origin/main', '--format=%(refname:short)', 'refs/heads'))
    $deleted = 0
    foreach ($branch in $merged) {
        if ($branch -eq 'main' -or $branch -in $checkedOut) { continue }
        if ($PSCmdlet.ShouldProcess($branch, 'Delete local branch merged into origin/main')) {
            Invoke-Git $root @('branch', '-D', $branch) | Write-Host
        }
        $deleted++
    }
    $kept = @(Invoke-Git $root @('for-each-ref', '--no-merged', 'origin/main', '--format=%(refname:short)', 'refs/heads')).Count
    Write-Host "Merged local branches $(if ($WhatIfPreference) { 'to delete' } else { 'deleted' }): $deleted. Kept $kept branches not merged into origin/main (they can hold work)."

    $branches = [Collections.Generic.HashSet[string]]::new([string[]]@(Invoke-Git $root @('for-each-ref', '--format=%(refname:short)', 'refs/heads')))
    $sections = @(& git -C $root config --name-only --get-regexp '^branch\.' | ForEach-Object {
            if ($_ -match '^branch\.(.+)\.[^.]+$') { $Matches[1] } } | Select-Object -Unique)
    $orphans = @($sections | Where-Object { -not $branches.Contains($_) })
    foreach ($name in $orphans) {
        if ($PSCmdlet.ShouldProcess("branch.$name", 'Remove Git config section of a missing branch')) {
            & git -C $root config --remove-section "branch.$name" 2>$null
        }
    }
    Write-Host "[branch] config sections of missing branches: $($orphans.Count) (of $($sections.Count))."

    $staleBefore = (Get-Date).AddMinutes(-$LockAgeMinutes)
    foreach ($lock in [IO.DirectoryInfo]::new($common).EnumerateFiles('*.lock', $enumeration)) {
        if ($lock.LastWriteTime -ge $staleBefore) { continue }
        if ($PSCmdlet.ShouldProcess($lock.FullName, "Delete stale Git lock file from $($lock.LastWriteTime)")) {
            Remove-Item -LiteralPath $lock.FullName -Force -ErrorAction SilentlyContinue
        }
        Write-Host "Stale lock: $($lock.FullName)"
    }

    $objects = Invoke-Git $root @('count-objects', '-v')
    Write-Host "Before gc: $(($objects | Where-Object { $_ -match '^(count|size|size-pack):' }) -join ', ')"
    if ($PSCmdlet.ShouldProcess($common, 'git gc')) {
        & git -C $root gc --quiet
        if ($LASTEXITCODE -ne 0) { Write-Warning 'git gc failed; another Git process can hold the repository. Run it again later.' }
        $objects = Invoke-Git $root @('count-objects', '-v')
        Write-Host "After gc: $(($objects | Where-Object { $_ -match '^(count|size|size-pack):' }) -join ', ')"
    }
}

if ($report.Count) {
    $report | Sort-Object Size -Descending |
        Format-Table Kind, @{ n = 'Size'; e = { Format-Size $_.Size }; a = 'right' }, Files, Result, Path -AutoSize -Wrap | Out-Host
}
$total = [long]($report | Measure-Object Size -Sum).Sum
$files = [long]($report | Measure-Object Files -Sum).Sum
$verb = if ($WhatIfPreference) { 'Would free' } else { 'Freed up to' }
Write-Host "$verb $(Format-Size $total) in $files files ($($report.Count) folders)."
if ($report | Where-Object Result -like 'partly*') {
    Write-Warning 'Some files are in use. Stop the Martlet processes started from these folders, then run the script again.'
}
