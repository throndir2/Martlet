#Requires -Version 7.2
<#
.SYNOPSIS
Runs the tests affected by this checkout's changes, in parallel, on this PC and the developer's validation hosts.

.DESCRIPTION
Compares the working tree (commits, staged, unstaged and untracked files) with its merge-base on -Base
(default origin/main) and maps every changed file to what it can affect: the owning .NET project, projects
that link or read the file, and everything that references those projects, transitively. Then, at once:

- builds every affected .NET project and runs the affected xUnit test projects on this PC in one
  `dotnet test` (test assemblies run side by side; xUnit runs test classes in parallel inside each);
- runs test projects marked <MartletTestHosts>linux</MartletTestHosts> on a Linux validation host: an SSH
  host from the developer profile, or Docker on this PC (disposable ext4 volume, non-root user);
- runs the affected Python worker suites (workers/<name>/tests), npm package tests and node tests.

Build settings shared by every project (Directory.Build.props, Directory.Packages.props, global.json,
NuGet.config, .editorconfig) select every .NET project; Markdown-only changes select nothing. Changed files
no suite covers are listed so they can be validated by running them. The summary is written to
artifacts/validation/summary.md (for the pull request description), with full logs and TRX files next to it.

A new failure is retried up to -Retries times once the parallel load is gone; a test that then passes is
reported as Flaky. Tests listed in tests/known-failures.txt (already failing on main) are reported as Known
failures. Neither blocks; any other failure does.

The developer profile (~/.martlet-dev/validation.json, or under $env:MARTLET_DEV_HOME) belongs to one
developer and is shared by all of their checkouts and worktrees: their .NET SDK and Python locations and
the validation hosts they have set up. -InitProfile creates it. See docs/VALIDATION.md.

Exits 1 when anything failed. What this environment cannot run is reported as NOT RUN with the reason;
-RequireAll makes NOT RUN fail the run too.

.EXAMPLE
.\scripts\Test-Martlet.ps1 -Project Martlet.Core.Tests -Filter 'FullyQualifiedName~Settings'
Runs only the named test project, filtered: the targeted tests routine validation uses (docs/VALIDATION.md).

.EXAMPLE
.\scripts\Test-Martlet.ps1 -List
Shows what this branch's changes select, and why, without building anything.

.EXAMPLE
.\scripts\Test-Martlet.ps1
Runs everything this branch's changes affect, including every dependent suite (broad; not for routine validation).

.EXAMPLE
.\scripts\Test-Martlet.ps1 -Project Martlet.Core -Filter 'FullyQualifiedName~Settings'
Runs the tests covering Martlet.Core and everything that depends on it, filtered.

.EXAMPLE
.\scripts\Test-Martlet.ps1 -All
Runs every suite.
#>
[CmdletBinding()]
param(
    [string]$Base = 'origin/main',
    [switch]$All,
    [string[]]$Project = @(),
    [string]$Filter,
    [switch]$List,
    [switch]$NoHosts,
    [switch]$RequireAll,
    [switch]$InitProfile,
    [string]$Configuration = 'Release',
    [int]$MaxParallel = 0,
    [int]$Retries = 2,
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'MartletDev.ps1')
$sdkVersion = (Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json).sdk.version
$devHome = Get-MartletDevHome
$profilePath = Join-Path $devHome 'validation.json'
$notesPath = Join-Path $devHome 'NOTES.md'
$here = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { 'macos' }
$pathComparison = if ($IsLinux) { [StringComparison]::Ordinal } else { [StringComparison]::OrdinalIgnoreCase }

if ($InitProfile) {
    New-Item -ItemType Directory -Force -Path $devHome | Out-Null
    if (Test-Path -LiteralPath $profilePath) { Write-Host "Kept the existing $profilePath" }
    else {
        [ordered]@{
            schemaVersion = 1
            dotnet = $null
            python = $null
            hosts = @(
                [ordered]@{
                    name = 'docker'; kind = 'docker'; enabled = $true; image = $null; allowPull = $false
                    notes = 'Docker on this PC in Linux container mode. Runs Linux-only tests in the .NET SDK image pinned by global.json, as a non-root user on a disposable ext4 volume.'
                },
                [ordered]@{
                    name = 'example-linux'; kind = 'ssh'; enabled = $false; target = 'me@linux-box.lan'; port = 22
                    workDirectory = '~/.cache/martlet-validation'; dotnet = '~/.dotnet/dotnet'
                    linuxTestParent = '/home/me/martlet-validation-ext4'; sshOptions = @(); capabilities = @('linux-x64', 'ext4')
                    notes = 'Key-based SSH only (BatchMode). Needs the .NET SDK pinned in global.json. linuxTestParent: an existing private (0700) ext4 directory owned by the SSH user.'
                }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $profilePath -Encoding utf8NoBOM
        Write-Host "Created $profilePath"
    }
    if (-not (Test-Path -LiteralPath $notesPath)) {
        @(
            '# Martlet developer notes (this PC)'
            ''
            'Local to this developer and shared by every Martlet checkout and worktree on this PC. Never commit'
            'this directory and keep credentials out of it: hosts are reached with SSH keys, never passwords.'
            ''
            'Record facts that help the next validation run: which host has a GPU or Docker, where an SDK is'
            'installed, why a host is disabled, what a host can and cannot exercise.'
            ''
        ) | Set-Content -LiteralPath $notesPath -Encoding utf8NoBOM
        Write-Host "Created $notesPath"
    }
    return
}

function Get-Setting($Object, [string]$Name) {
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name }
}

$devProfile = Get-MartletDevProfile

function Invoke-Git {
    $output = & git -C $root -c core.quotePath=false @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed." }
    $output
}

function Get-RepoPath([string]$FullPath) {
    [IO.Path]::GetRelativePath($root, $FullPath).Replace('\', '/').TrimEnd('/')
}

function Test-Under([string]$Path, [string]$Directory) {
    $Path.Equals($Directory, $pathComparison) -or $Path.StartsWith($Directory + '/', $pathComparison)
}

function Get-Property([xml]$Xml, [string]$Name) {
    $node = $Xml.SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($node) { $node.InnerText.Trim() }
}

# --- Project graph ---------------------------------------------------------------------------------------
$defaultTfm = Get-Property ([xml](Get-Content -Raw -LiteralPath (Join-Path $root 'Directory.Build.props'))) 'TargetFramework'
$projects = [ordered]@{}
$projectFiles = foreach ($top in 'src', 'tests') {
    Get-ChildItem -LiteralPath (Join-Path $root $top) -Recurse -File -Filter *.csproj |
        Where-Object { (Get-RepoPath $_.FullName) -notmatch '/(bin|obj|node_modules)/' }
}
foreach ($file in $projectFiles) {
    $text = Get-Content -Raw -LiteralPath $file.FullName
    $xml = [xml]$text
    $directory = Get-RepoPath $file.DirectoryName
    $refs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $inputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    # Every relative path leaving the project directory: project references, linked/embedded files and
    # build inputs (MartletValidationInputs declares ones MSBuild cannot see, such as npm packages).
    foreach ($match in [regex]::Matches($text, '\.\.[\\/][^"''<>;*?\s]*')) {
        $full = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName ($match.Value -replace '[\\/]', [IO.Path]::DirectorySeparatorChar)))
        $path = Get-RepoPath $full
        if ($path -eq '.' -or $path.StartsWith('..') -or (Test-Under $directory $path)) { continue }
        if ($path.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) { [void]$refs.Add($path) } else { [void]$inputs.Add($path) }
    }
    $tfmText = @((Get-Property $xml 'TargetFrameworks'), (Get-Property $xml 'TargetFramework'), $defaultTfm) | Where-Object { $_ } | Select-Object -First 1
    $tfms = @($tfmText -split ';' | Where-Object { $_ })
    $hosts = Get-Property $xml 'MartletTestHosts'
    $os = if ($hosts) { @($hosts -split '[;,\s]+' | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() }) }
        elseif ($tfms -match '-windows') { @('windows') } else { @($here) }
    $rel = Get-RepoPath $file.FullName
    $projects[$rel] = [pscustomobject]@{
        Rel = $rel; Name = $file.BaseName; Dir = $directory
        Assembly = (Get-Property $xml 'AssemblyName') ?? $file.BaseName
        IsTest = (Get-Property $xml 'IsTestProject') -eq 'true'
        Tfms = $tfms; Os = $os; WindowsTfm = [bool]($tfms -match '-windows')
        Refs = @($refs); Inputs = @($inputs); Dependents = [Collections.Generic.List[string]]::new()
    }
}
$byPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($p in $projects.Values) { $byPath[$p.Rel] = $p }
foreach ($p in $projects.Values) {
    foreach ($r in $p.Refs) { if ($byPath.ContainsKey($r)) { $byPath[$r].Dependents.Add($p.Rel) } }
}

# The projects plus everything they reference: a generated solution lists them all so MSBuild builds each
# once, in the requested configuration (references outside a solution would build in Debug).
function Get-WithReferences([object[]]$Start) {
    $set = [ordered]@{}
    $stack = [Collections.Generic.Stack[object]]::new()
    foreach ($p in $Start) { $stack.Push($p) }
    while ($stack.Count) {
        $p = $stack.Pop()
        if ($set.Contains($p.Rel)) { continue }
        $set[$p.Rel] = $p
        foreach ($r in $p.Refs) { if ($byPath.ContainsKey($r)) { $stack.Push($byPath[$r]) } }
    }
    @($set.Values)
}

# --- Non-.NET suites -------------------------------------------------------------------------------------
$suites = [ordered]@{}
foreach ($worker in Get-ChildItem -LiteralPath (Join-Path $root 'workers') -Directory -ErrorAction SilentlyContinue) {
    if (Test-Path -LiteralPath (Join-Path $worker.FullName 'tests') -PathType Container) {
        $suites["python:$($worker.Name)"] = [pscustomobject]@{ Name = "python:$($worker.Name)"; Kind = 'python'; Dir = Get-RepoPath $worker.FullName; Files = @() }
    }
}
foreach ($package in Get-ChildItem -LiteralPath (Join-Path $root 'src') -Directory) {
    $manifest = Join-Path $package.FullName 'package.json'
    if (-not (Test-Path -LiteralPath $manifest)) { continue }
    $scripts = Get-Setting (Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json) 'scripts'
    if (Get-Setting $scripts 'test') {
        $suites["npm:$($package.Name)"] = [pscustomobject]@{ Name = "npm:$($package.Name)"; Kind = 'npm'; Dir = Get-RepoPath $package.FullName; Files = @() }
    }
}

function Get-NodeSuite($P) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $root $P.Dir) -Recurse -File -Filter *.test.mjs |
        Where-Object { (Get-RepoPath $_.FullName) -notmatch '/(bin|obj|node_modules)/' } | ForEach-Object { Get-RepoPath $_.FullName })
    if ($files) { [pscustomobject]@{ Name = "node:$($P.Name)"; Kind = 'node'; Dir = '.'; Files = $files } }
}

# --- Selection -------------------------------------------------------------------------------------------
$globalFiles = 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config', '.editorconfig'
$reasons = [ordered]@{}            # project rel or suite name -> why it was selected
$selectedSuites = [ordered]@{}
$uncovered = [Collections.Generic.List[string]]::new()
$docs = [Collections.Generic.List[string]]::new()
$changed = @()
$scope = ''

function Add-Reason([string]$Key, [string]$Why) { if (-not $reasons.Contains($Key)) { $reasons[$Key] = $Why } }

if ($All) {
    $scope = 'all suites (-All)'
    foreach ($p in $projects.Values) { Add-Reason $p.Rel 'requested (-All)' }
    foreach ($s in $suites.Values) { $selectedSuites[$s.Name] = $s; Add-Reason $s.Name 'requested (-All)' }
}
elseif ($Project) {
    $scope = "requested: $($Project -join ', ')"
    foreach ($name in $Project) {
        $p = $projects.Values | Where-Object { $_.Name -eq $name -or $_.Name -eq "$name.Tests" } | Select-Object -First 1
        $s = $suites.Values | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if ($p) { Add-Reason $p.Rel 'requested (-Project)' }
        elseif ($s) { $selectedSuites[$s.Name] = $s; Add-Reason $s.Name 'requested (-Project)' }
        else { throw "Unknown project or suite '$name'. Projects: Martlet.* names under src/ and tests/; suites: $($suites.Keys -join ', ')." }
    }
}
else {
    $mergeBase = & git -C $root merge-base HEAD $Base 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $mergeBase) { throw "No merge-base between HEAD and '$Base'. Fetch it first (git fetch origin) or pass -Base." }
    $changed = @(@(Invoke-Git diff --name-only --no-renames $mergeBase) + @(Invoke-Git ls-files --others --exclude-standard) |
        Where-Object { $_ } | Sort-Object -Unique)
    $scope = "changes since $Base (merge-base $($mergeBase.Substring(0, 9))): $($changed.Count) file(s)"
    foreach ($file in $changed) {
        $leaf = Split-Path $file -Leaf
        if ($file -notmatch '/' -and $globalFiles -contains $leaf) {
            foreach ($p in $projects.Values) { Add-Reason $p.Rel "build setting $file changed" }
            continue
        }
        if ($file -like '*.md' -or $file -eq 'tests/known-failures.txt') { $docs.Add($file); continue }
        $hit = $false
        foreach ($s in $suites.Values) {
            if (Test-Under $file $s.Dir) { $selectedSuites[$s.Name] = $s; Add-Reason $s.Name "changed: $file"; $hit = $true }
        }
        $owner = $projects.Values | Where-Object { Test-Under $file $_.Dir } | Sort-Object { $_.Dir.Length } -Descending | Select-Object -First 1
        if ($owner) { Add-Reason $owner.Rel "changed: $file"; $hit = $true }
        foreach ($p in $projects.Values) {
            foreach ($i in $p.Inputs) { if (Test-Under $file $i) { Add-Reason $p.Rel "uses changed $file"; $hit = $true } }
        }
        if (-not $hit) { $uncovered.Add($file) }
    }
}

$changedProjects = @($reasons.Keys | Where-Object { $projects.Contains($_) })
$queue = [Collections.Generic.Queue[string]]::new([string[]]$changedProjects)
while ($queue.Count) {
    $current = $projects[$queue.Dequeue()]
    foreach ($dependent in $current.Dependents) {
        if (-not $reasons.Contains($dependent)) { $reasons[$dependent] = "depends on $($current.Name)"; $queue.Enqueue($dependent) }
    }
}
$affected = @($reasons.Keys | Where-Object { $projects.Contains($_) } | ForEach-Object { $projects[$_] })
foreach ($p in $affected) {
    $node = Get-NodeSuite $p
    if ($node -and -not $selectedSuites.Contains($node.Name)) { $selectedSuites[$node.Name] = $node; Add-Reason $node.Name "covers $($p.Name)" }
}

$tests = @($affected | Where-Object IsTest)
$localTests = @($tests | Where-Object { $_.Os -contains $here })
$linuxTests = @($tests | Where-Object { $here -ne 'linux' -and $_.Os -contains 'linux' })
$otherTests = @($tests | Where-Object { $_.Os -notcontains $here -and $_.Os -notcontains 'linux' })
$buildOnly = @($affected | Where-Object { -not $_.IsTest -and ($IsWindows -or -not $_.WindowsTfm) })
$productChanged = if ($All -or $Project) { @() } else { @($changedProjects | Where-Object { $_ -like 'src/*' } | ForEach-Object { $projects[$_].Name }) }

Write-Host "Martlet validation: $scope"
if ($List -or $VerbosePreference -ne 'SilentlyContinue') {
    foreach ($p in $localTests) { Write-Host ("  test   here    {0,-48} {1}" -f $p.Name, $reasons[$p.Rel]) }
    foreach ($p in $linuxTests) { Write-Host ("  test   linux   {0,-48} {1}" -f $p.Name, $reasons[$p.Rel]) }
    foreach ($p in $otherTests) { Write-Host ("  test   {0,-7} {1,-48} {2}" -f ($p.Os -join ','), $p.Name, $reasons[$p.Rel]) }
    foreach ($s in $selectedSuites.Values) { Write-Host ("  suite  here    {0,-48} {1}" -f $s.Name, $reasons[$s.Name]) }
    foreach ($p in $buildOnly) { Write-Host ("  build  here    {0,-48} {1}" -f $p.Name, $reasons[$p.Rel]) }
    foreach ($f in $uncovered) { Write-Host "  not covered by automated tests: $f" }
    if ($docs.Count) { Write-Host "  documentation only (no tests): $($docs.Count) file(s)" }
}
if ($List) { return }
if (-not $tests -and -not $selectedSuites.Count -and -not $buildOnly) {
    Write-Host 'Nothing to build or test for these changes.'
    if ($uncovered.Count) { Write-Host "Validate these by running them: $($uncovered -join ', ')" }
    return
}

# --- Tools -----------------------------------------------------------------------------------------------
function Find-Dotnet { Find-MartletDotnet $root $devProfile }

function Find-Python([string[]]$Modules = @()) {
    $configured = Get-Setting $devProfile 'python'
    $options = [Collections.Generic.List[object]]::new()
    if ($configured) { $options.Add(@($configured)) }
    if ($IsWindows) { $options.Add(@('py', '-3.12')) }
    foreach ($name in 'python3.12', 'python3', 'python') { $options.Add(@($name)) }
    $check = if ($Modules) { "import $($Modules -join ', ')" } else { 'pass' }
    foreach ($option in $options) {
        $command = Get-Command $option[0] -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        $prefix = @($option | Select-Object -Skip 1)
        & $command.Source @prefix -c $check *> $null
        if ($LASTEXITCODE -ne 0) { continue }
        $version = & $command.Source @prefix --version 2>&1
        return [pscustomobject]@{ Exe = $command.Source; Prefix = $prefix; Version = "$version".Trim() }
    }
}

function Quote-Bash([string]$Value) { "'" + $Value.Replace("'", "'\''") + "'" }

# --- Linux validation host -------------------------------------------------------------------------------
function Select-LinuxHost {
    $skipped = [Collections.Generic.List[string]]::new()
    $configured = @(Get-Setting $devProfile 'hosts' | Where-Object { $_ })
    $candidates = @($configured | Where-Object { (Get-Setting $_ 'enabled') -ne $false })
    if (-not ($configured | Where-Object { (Get-Setting $_ 'kind') -eq 'docker' })) {
        $candidates += [pscustomobject]@{ name = 'docker'; kind = 'docker' }
    }
    foreach ($h in $candidates) {
        $name = Get-Setting $h 'name'
        switch (Get-Setting $h 'kind') {
            'docker' {
                if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) { $skipped.Add("${name}: Docker is not installed"); continue }
                $os = & docker version --format '{{.Server.Os}}' 2>$null
                if ($LASTEXITCODE -ne 0) { $skipped.Add("${name}: the Docker engine is not running"); continue }
                if ($os -ne 'linux') { $skipped.Add("${name}: Docker is not in Linux container mode"); continue }
                $image = (Get-Setting $h 'image') ?? "mcr.microsoft.com/dotnet/sdk:$sdkVersion"
                & docker image inspect $image *> $null
                $present = $LASTEXITCODE -eq 0
                if (-not $present -and -not (Get-Setting $h 'allowPull')) {
                    $skipped.Add("${name}: image $image is not on this PC (docker pull $image, or set allowPull in the profile)"); continue
                }
                return [pscustomobject]@{ Name = $name; Kind = 'docker'; Image = $image; Pull = $(if ($present) { 'never' } else { 'missing' }); Skipped = $skipped }
            }
            'ssh' {
                $target = Get-Setting $h 'target'
                $parent = Get-Setting $h 'linuxTestParent'
                $workDirectory = (Get-Setting $h 'workDirectory') ?? '~/.cache/martlet-validation'
                $remoteDotnet = (Get-Setting $h 'dotnet') ?? 'dotnet'
                if ((Get-Setting $h 'os') -and (Get-Setting $h 'os') -ne 'linux') { $skipped.Add("${name}: only Linux SSH hosts run Linux tests"); continue }
                if ($target -notmatch '^[A-Za-z0-9_.@-]+$') { $skipped.Add("${name}: missing or unsupported target"); continue }
                if (-not $parent) { $skipped.Add("${name}: no linuxTestParent (private ext4 directory) in the profile"); continue }
                if (@($parent, $workDirectory, $remoteDotnet) -notmatch '^[A-Za-z0-9_./~-]+$' ) { $skipped.Add("${name}: linuxTestParent, workDirectory and dotnet must be plain paths"); continue }
                $ssh = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10')
                $scp = @('-q', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10')
                if (Get-Setting $h 'port') { $ssh += @('-p', "$(Get-Setting $h 'port')"); $scp += @('-P', "$(Get-Setting $h 'port')") }
                # Extra options for both ssh and scp, such as -i <key> or -o UserKnownHostsFile=<file>.
                foreach ($option in @(Get-Setting $h 'sshOptions' | Where-Object { $_ })) { $ssh += "$option"; $scp += "$option" }
                $probe = & ssh @ssh $target ('d=$(command -v ' + $remoteDotnet + ') && readlink -f "$d" && "$d" --list-sdks && echo "HOME=$HOME"') 2>$null
                if ($LASTEXITCODE -ne 0) { $skipped.Add("${name}: unreachable over key-based SSH, or no dotnet at $remoteDotnet"); continue }
                if (-not ($probe -match "^$([regex]::Escape($sdkVersion)) ")) { $skipped.Add("${name}: no .NET SDK $sdkVersion"); continue }
                $remoteHome = ($probe | Where-Object { $_ -like 'HOME=*' } | Select-Object -Last 1).Substring(5)
                $resolve = { param($path) if ($path -like '~*') { $remoteHome + $path.Substring(1) } else { $path } }
                return [pscustomobject]@{
                    Name = $name; Kind = 'ssh'; Target = $target; Ssh = $ssh; Scp = $scp; Dotnet = $probe[0]
                    Parent = & $resolve $parent; WorkDirectory = & $resolve $workDirectory; Skipped = $skipped
                }
            }
            default { $skipped.Add("${name}: unknown kind '$(Get-Setting $h 'kind')'") }
        }
    }
    [pscustomobject]@{ Name = $null; Skipped = $skipped }
}

# --- Run -------------------------------------------------------------------------------------------------
$out = Join-Path $root 'artifacts' 'validation'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

if ($IsWindows -and -not ('MartletProcessTree' -as [type])) {
    # A Toolhelp snapshot lists every process with its parent in milliseconds (WMI takes most of a second).
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class MartletProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, ProcessId; public IntPtr HeapId; public uint ModuleId, Threads, ParentProcessId;
        public int PriorityBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static Dictionary<int, int> Parents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return parents;
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        return parents;
    }
}
'@
}

# Runs one job's steps in a thread job. Output goes straight to files, so a process a test leaves running
# cannot hold the run open; on Windows such leftovers are tracked and stopped (build servers are kept).
$runSteps = {
    param([object[]]$Steps, [string]$WorkingDirectory, [string]$Log, [int]$TimeoutMinutes)
    function ConvertTo-Argument([string]$Value) {
        if ($Value -and $Value -notmatch '[\s"]') { return $Value }
        '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
    }
    function Get-StartTime([int]$Id) {
        try { [Diagnostics.Process]::GetProcessById($Id).StartTime } catch { $null }
    }
    function Update-Tracked([int]$RootId, [datetime]$Since, [hashtable]$Tracked) {
        $parents = [MartletProcessTree]::Parents()
        do {
            $grew = $false
            foreach ($id in @($parents.Keys)) {
                if ($Tracked.ContainsKey($id) -or $id -eq $RootId) { continue }
                $parent = $parents[$id]
                if ($parent -ne $RootId -and -not $Tracked.ContainsKey($parent)) { continue }
                $start = Get-StartTime $id
                if ($start -and $start -ge $Since) { $Tracked[$id] = $start; $grew = $true }
            }
        } while ($grew)
    }
    function Add-Output([string]$Path) {
        if (-not (Test-Path -LiteralPath $Path)) { return }
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { $text = [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
        if ($text) { [IO.File]::AppendAllText($Log, $text) }
        Remove-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $code = 0
    $notes = [Collections.Generic.List[string]]::new()
    foreach ($step in $Steps) {
        if ($code -ne 0 -and -not $step.Always) { continue }
        $arguments = @(@($step.Args) | ForEach-Object { ConvertTo-Argument ([string]$_) })
        Add-Content -LiteralPath $Log -Value "> $($step.Exe) $($arguments -join ' ')"
        $started = [DateTime]::Now.AddSeconds(-1)
        $stepCode = 0
        try {
            $process = Start-Process -FilePath $step.Exe -ArgumentList $arguments -WorkingDirectory $WorkingDirectory -NoNewWindow -PassThru `
                -RedirectStandardOutput "$Log.out" -RedirectStandardError "$Log.err"
            $null = $process.Handle
            $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
            $tracked = @{}
            $timedOut = $false
            while (-not $process.WaitForExit(3000)) {
                if ($IsWindows) { Update-Tracked $process.Id $started $tracked }
                if ([DateTime]::UtcNow -gt $deadline) { $process.Kill($true); $timedOut = $true; break }
            }
            $process.WaitForExit()
            $stepCode = if ($timedOut) { -2 } else { $process.ExitCode }
            if ($timedOut) { $notes.Add("$(Split-Path $step.Exe -Leaf) stopped after $TimeoutMinutes min (-TimeoutMinutes)") }
            if ($IsWindows) {
                Update-Tracked $process.Id $started $tracked
                foreach ($id in @($tracked.Keys)) {
                    if ((Get-StartTime $id) -ne $tracked[$id]) { continue }
                    $p = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -Property Name, CommandLine -ErrorAction SilentlyContinue
                    # Build servers are shared with other builds on this PC and are meant to outlive this one.
                    if (-not $p -or "$($p.Name) $($p.CommandLine)" -match 'VBCSCompiler|MSBuild\.dll|/nodemode|rzc\.dll|conhost') { continue }
                    try {
                        $leftover = [Diagnostics.Process]::GetProcessById($id)
                        $leftover.Kill(); $null = $leftover.WaitForExit(5000)
                        $notes.Add("stopped $($p.Name) ($id), left running by $(Split-Path $step.Exe -Leaf)")
                    }
                    catch { }
                }
            }
        }
        catch { Add-Content -LiteralPath $Log -Value "$_"; $stepCode = -1 }
        Add-Output "$Log.out"
        Add-Output "$Log.err"
        if ($code -eq 0) { $code = $stepCode }
    }
    foreach ($note in $notes) { Add-Content -LiteralPath $Log -Value "Test-Martlet: $note" }
    [pscustomobject]@{ ExitCode = $code; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1); Notes = @($notes) }
}

$runs = [Collections.Generic.List[object]]::new()
$notRun = [Collections.Generic.List[string]]::new()
$savedEnvironment = @{}
function Set-ProcessEnvironment([string]$Name, [string]$Value) {
    if (-not $savedEnvironment.ContainsKey($Name)) { $savedEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name) }
    [Environment]::SetEnvironmentVariable($Name, $Value)
}
function New-Step([string]$Exe, [string[]]$Arguments, [switch]$Always) { [pscustomobject]@{ Exe = $Exe; Args = $Arguments; Always = [bool]$Always } }
function Start-Run([string]$Name, [string]$Kind, [string]$Where, [object[]]$Steps, [string]$WorkingDirectory, [object[]]$Items = @(), [string]$Detail = '', [string]$Results = '') {
    $log = Join-Path $out ("$Name.log" -replace '[^A-Za-z0-9_.-]', '-')
    $job = Start-ThreadJob -ScriptBlock $runSteps -ArgumentList $Steps, $WorkingDirectory, $log, $TimeoutMinutes -ThrottleLimit ([Environment]::ProcessorCount)
    $run = [pscustomobject]@{
        Name = $Name; Kind = $Kind; Where = $Where; Log = $log; Job = $job; Items = $Items; Detail = $Detail; Results = $Results
        Steps = $Steps; WorkingDirectory = $WorkingDirectory; ExitCode = $null; Seconds = 0; Notes = @()
    }
    $runs.Add($run)
    $run
}
function Wait-Runs([object[]]$Batch) {
    $pending = [Collections.Generic.List[object]]::new()
    foreach ($run in $Batch) { $pending.Add($run) }
    while ($pending.Count) {
        $null = Wait-Job -Job @($pending | ForEach-Object Job) -Any
        foreach ($run in @($pending | Where-Object { $_.Job.State -in 'Completed', 'Failed', 'Stopped' })) {
            try { $result = Receive-Job -Job $run.Job -Wait -AutoRemoveJob; $run.ExitCode = $result.ExitCode; $run.Seconds = $result.Seconds; $run.Notes = @($result.Notes) }
            catch { $run.ExitCode = -1; Add-Content -LiteralPath $run.Log -Value "$_" }
            $mark = if ($run.ExitCode -eq 0) { 'done  ' } else { 'failed' }
            Write-Host ("  {0} {1} [{2}] {3:n0} s" -f $mark, $run.Name, $run.Where, $run.Seconds)
            [void]$pending.Remove($run)
        }
    }
}

$watch = [Diagnostics.Stopwatch]::StartNew()
$headCommit = (Invoke-Git rev-parse --short=9 HEAD)
$dotnet = $null
$pythonByRequirements = @{}
$filterArguments = if ($Filter) { @('--filter', $Filter) } else { @() }

# Linux-only tests: on a Linux host, started first because it is usually the longest run.
if ($linuxTests) {
    $linuxHost = if ($NoHosts) { [pscustomobject]@{ Name = $null; Skipped = @('validation hosts disabled (-NoHosts)') } } else { Select-LinuxHost }
    if (-not $linuxHost.Name) {
        $why = if ($linuxHost.Skipped.Count) { $linuxHost.Skipped -join '; ' } else { 'no Linux validation host' }
        foreach ($p in $linuxTests) { $notRun.Add("$($p.Name) (Linux): $why") }
    }
    else {
        $stage = Join-Path $out 'linux'
        $results = Join-Path $out 'trx' 'linux'
        New-Item -ItemType Directory -Force -Path $stage, $results | Out-Null
        $files = @(Invoke-Git ls-files -co --exclude-standard | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
        [IO.File]::WriteAllLines((Join-Path $stage 'files.txt'), [string[]]$files)
        & tar -cf (Join-Path $stage 'source.tar') -C $root -T (Join-Path $stage 'files.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Could not package the source tree for the Linux host.' }
        $projectLines = Get-WithReferences $linuxTests | ForEach-Object { "  <Project Path=`"src/$($_.Rel)`" />" }
        [IO.File]::WriteAllText((Join-Path $stage 'Linux.slnx'), "<Solution>`n$($projectLines -join "`n")`n</Solution>`n")
        $test = "test Linux.slnx -c $Configuration --nologo -nodeReuse:false --logger 'trx;LogFilePrefix=r'" + $(if ($Filter) { ' --filter ' + (Quote-Bash $Filter) } else { '' })
        if ($linuxHost.Kind -eq 'docker') {
            $script = @(
                'set -euo pipefail'
                'mkdir -p /work/src /tmp/home /ext4/parent'
                'tar -xf /in/source.tar -C /work/src'
                'cp /in/Linux.slnx /work/Linux.slnx'
                'chown -R app:app /work /tmp/home'
                'chown app:app /nuget /ext4/parent && chmod 700 /ext4/parent'
                'cd /work'
                'export DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"'
                'set +e'
                "runuser -u app -- env HOME=/tmp/home NUGET_PACKAGES=/nuget DOTNET_ROOT=`"`$DOTNET_ROOT`" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 MARTLET_LINUX_TEST_PARENT=/ext4/parent dotnet $test --results-directory /tmp/results"
                'code=$?'
                'cp /tmp/results/*.trx /out/ 2>/dev/null'
                'exit $code'
            )
            [IO.File]::WriteAllText((Join-Path $stage 'run.sh'), ($script -join "`n") + "`n")
            $container = 'martlet-validation-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
            $steps = @(New-Step 'docker' @('run', '--rm', '--name', $container, '--pull', $linuxHost.Pull,
                '--mount', "type=bind,source=$stage,target=/in,readonly", '--mount', "type=bind,source=$results,target=/out",
                '--mount', 'type=volume,source=martlet-validation-nuget,target=/nuget',
                '--mount', 'type=volume,target=/ext4', $linuxHost.Image, 'bash', '/in/run.sh'))
            $where = "$($linuxHost.Name) (Docker)"
        }
        else {
            $remote = "$($linuxHost.WorkDirectory)/run-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
            $dotnetDirectory = $linuxHost.Dotnet.Substring(0, $linuxHost.Dotnet.LastIndexOf('/'))
            $script = @(
                'set -u'
                "cd $(Quote-Bash $remote) || exit 1"
                'mkdir -p src results && tar -xf source.tar -C src || exit 1'
                "export DOTNET_ROOT=$(Quote-Bash $dotnetDirectory) DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 MARTLET_LINUX_TEST_PARENT=$(Quote-Bash $linuxHost.Parent)"
                "$(Quote-Bash $linuxHost.Dotnet) $test --results-directory results"
            )
            [IO.File]::WriteAllText((Join-Path $stage 'run.sh'), ($script -join "`n") + "`n")
            $steps = @(
                New-Step 'ssh' (@($linuxHost.Ssh) + @($linuxHost.Target, "mkdir -p $(Quote-Bash $remote)"))
                New-Step 'scp' (@($linuxHost.Scp) + @((Join-Path $stage 'source.tar'), (Join-Path $stage 'Linux.slnx'), (Join-Path $stage 'run.sh'), "$($linuxHost.Target):$remote/"))
                New-Step 'ssh' (@($linuxHost.Ssh) + @($linuxHost.Target, "bash $(Quote-Bash "$remote/run.sh")"))
                New-Step 'scp' (@($linuxHost.Scp) + @("$($linuxHost.Target):$remote/results/*.trx", $results)) -Always
                New-Step 'ssh' (@($linuxHost.Ssh) + @($linuxHost.Target, "rm -rf $(Quote-Bash $remote)")) -Always
            )
            $where = "$($linuxHost.Name) (SSH)"
        }
        $null = Start-Run 'linux-dotnet' 'dotnet' $where $steps $root $linuxTests -Detail $linuxHost.Name -Results $results
    }
}
foreach ($p in $otherTests) { $notRun.Add("$($p.Name): needs a $($p.Os -join '/') PC; this is $here") }

# Python worker, npm package and node suites on this PC.
foreach ($s in $selectedSuites.Values) {
    switch ($s.Kind) {
        'python' {
            # A worker's tests/requirements.txt names the modules its tests import; the first Python that has them runs it.
            $requirements = Join-Path $root $s.Dir 'tests' 'requirements.txt'
            $modules = @(if (Test-Path -LiteralPath $requirements) {
                Get-Content -LiteralPath $requirements | ForEach-Object { ($_ -replace '#.*$', '' -split '[<>=!~\[;\s]')[0].Trim().Replace('-', '_') } | Where-Object { $_ }
            })
            $key = $modules -join ','
            if (-not $pythonByRequirements.ContainsKey($key)) { $pythonByRequirements[$key] = Find-Python $modules }
            $python = $pythonByRequirements[$key]
            if (-not $python) {
                $need = if ($modules) { " with $($modules -join ', ') ($($s.Dir)/tests/requirements.txt)" } else { '' }
                $notRun.Add("$($s.Name): no Python$need; install it or set `"python`" in the developer profile"); continue
            }
            $null = Start-Run $s.Name 'python' 'here' @(New-Step $python.Exe (@($python.Prefix) + @('-m', 'unittest', 'discover', '-s', 'tests'))) (Join-Path $root $s.Dir) -Detail $python.Version
        }
        'npm' {
            $npm = Get-Command npm -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $npm) { $notRun.Add("$($s.Name): npm is not installed"); continue }
            $steps = @()
            if (-not (Test-Path -LiteralPath (Join-Path $root $s.Dir 'node_modules'))) { $steps += New-Step $npm.Source @('ci', '--no-audit', '--no-fund') }
            $steps += New-Step $npm.Source @('test')
            $null = Start-Run $s.Name 'node' 'here' $steps (Join-Path $root $s.Dir)
        }
        'node' {
            $node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $node) { $notRun.Add("$($s.Name): Node.js is not installed"); continue }
            $null = Start-Run $s.Name 'node' 'here' @(New-Step $node.Source (@('--experimental-vm-modules', '--test') + $s.Files)) $root
        }
    }
}

# .NET on this PC: one generated solution, so MSBuild builds each project once and runs the test assemblies
# side by side.
$local = @($localTests) + @($buildOnly)
if ($local) {
    $dotnet = Find-Dotnet
    $dotnetDirectory = Split-Path $dotnet -Parent
    Set-ProcessEnvironment 'DOTNET_ROOT' $dotnetDirectory
    Set-ProcessEnvironment 'PATH' ($dotnetDirectory + [IO.Path]::PathSeparator + $env:PATH)
    Set-ProcessEnvironment 'DOTNET_MULTILEVEL_LOOKUP' '0'
    Set-ProcessEnvironment 'DOTNET_NOLOGO' '1'
    Set-ProcessEnvironment 'DOTNET_CLI_TELEMETRY_OPTOUT' '1'
    $solution = Join-Path $out 'Local.slnx'
    $projectLines = Get-WithReferences $local | ForEach-Object { "  <Project Path=`"../../$($_.Rel)`" />" }
    [IO.File]::WriteAllText($solution, "<Solution>`n$($projectLines -join "`n")`n</Solution>`n")
    $results = Join-Path $out 'trx' 'local'
    $arguments = @('test', $solution, '-c', $Configuration, '--nologo', '-nodeReuse:false', '--logger', 'trx;LogFilePrefix=r', '--results-directory', $results) + $filterArguments
    if ($MaxParallel -gt 0) { $arguments += "-m:$MaxParallel" }
    $node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($node) { $arguments += "-p:NodeExecutable=$($node.Source)" }
    $null = Start-Run 'local-dotnet' 'dotnet' 'here' @(New-Step $dotnet $arguments) $root $localTests -Detail "builds $($local.Count) project(s)" -Results $results
}

Write-Host ("Running {0} job(s): {1}" -f $runs.Count, (($runs | ForEach-Object { "$($_.Name) [$($_.Where)]" }) -join ', '))
try { Wait-Runs @($runs) }
catch { foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }; throw }

# --- Results ---------------------------------------------------------------------------------------------
# tests/known-failures.txt lists tests that already fail on main: they are reported but do not block. Any
# other failure blocks the change. Known failures that pass again are reported so their lines get deleted;
# entries marked "env:" depend on the machine and are never reported that way.
$knownPath = Join-Path $root 'tests' 'known-failures.txt'
$known = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::Ordinal)
if (Test-Path -LiteralPath $knownPath) {
    foreach ($line in Get-Content -LiteralPath $knownPath) {
        $id = ($line -replace '#.*$', '').Trim()
        if ($id) { $known[$id] = $line -match '#.*\benv:' }
    }
}
$trxNamespace = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$pythonFailure = '^(?:FAIL|ERROR): (?<name>\S+) \((?<scope>[^)\s]+)\)'
$nodeFailure = '^\s*not ok \d+ - (?<name>.+?)\s*$'

function Get-TestId([string]$Name) { ($Name -replace '\(.*$', '').Trim() }

# One record per .NET test result: Group (assembly), Detail (target framework), Id, Outcome, Message.
function Read-TrxResults([string]$Directory) {
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Filter *.trx -File -ErrorAction SilentlyContinue)) {
        $xml = [xml](Get-Content -Raw -LiteralPath $file.FullName)
        $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable)
        $ns.AddNamespace('t', $trxNamespace)
        $codeBase = @{}
        foreach ($test in $xml.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) { $codeBase[$test.id] = $test.SelectSingleNode('t:TestMethod', $ns).codeBase }
        $times = $xml.SelectSingleNode('/t:TestRun/t:Times', $ns)
        $duration = if ($times -and $times.start -and $times.finish) { [math]::Round(([datetimeoffset]$times.finish - [datetimeoffset]$times.start).TotalSeconds, 1) } else { 0 }
        foreach ($result in $xml.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
            $path = "$($codeBase[$result.testId])" -replace '\\', '/'
            $message = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
            [pscustomobject]@{
                Group = [IO.Path]::GetFileNameWithoutExtension($path); Detail = Split-Path (Split-Path $path -Parent) -Leaf
                Id = Get-TestId $result.testName; Name = $result.testName; Outcome = $result.outcome
                Message = $(if ($message) { $message.InnerText.Trim() } else { '' }); Seconds = $duration
            }
        }
    }
}

function Read-SuiteResults($Run) {
    $lines = if (Test-Path -LiteralPath $Run.Log) { @(Get-Content -LiteralPath $Run.Log) } else { @() }
    $failures = [Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $id = $null
        if ($Run.Kind -eq 'python') {
            $m = [regex]::Match($lines[$i], $pythonFailure)
            if ($m.Success) {
                $scope = $m.Groups['scope'].Value; $name = $m.Groups['name'].Value
                $id = "$($Run.Name -replace '^retry\d*-')/" + $(if ($scope.EndsWith(".$name")) { $scope } else { "$scope.$name" })
            }
        }
        else {
            $m = [regex]::Match($lines[$i], $nodeFailure)
            if ($m.Success) { $id = "$($Run.Name -replace '^retry\d*-')/$($m.Groups['name'].Value)" }
        }
        if (-not $id) { continue }
        $block = [Collections.Generic.List[string]]::new()
        for ($j = $i + 1; $j -lt [math]::Min($i + 80, $lines.Count); $j++) {
            if ($lines[$j] -match '^(FAIL|ERROR):|^Ran \d+ tests?|^={20,}$') { break }
            if ($lines[$j].Trim() -and $lines[$j] -notmatch '^-{20,}$') { $block.Add($lines[$j].Trim()) }
        }
        $failures.Add([pscustomobject]@{ Id = $id; Message = (($block | Select-Object -Last 3) -join "`n") })
    }
    $text = $lines -join "`n"
    $passed = 0; $skipped = 0
    if ($Run.Kind -eq 'python') {
        $total = [regex]::Match($text, 'Ran (\d+) tests?')
        $skips = [regex]::Match($text, 'skipped=(\d+)')
        $skipped = if ($skips.Success) { [int]$skips.Groups[1].Value } else { 0 }
        $passed = if ($total.Success) { [math]::Max(0, [int]$total.Groups[1].Value - $failures.Count - $skipped) } else { 0 }
    }
    else {
        foreach ($m in [regex]::Matches($text, '(?m)^\S*\s*(pass|skipped)\s+(\d+)\s*$')) {
            if ($m.Groups[1].Value -eq 'pass') { $passed += [int]$m.Groups[2].Value } else { $skipped += [int]$m.Groups[2].Value }
        }
    }
    [pscustomobject]@{ Failures = $failures; Passed = $passed; Skipped = $skipped; Lines = $lines }
}

$dotnetResults = [Collections.Generic.List[object]]::new()
$suiteResults = @{}
foreach ($run in @($runs)) {
    if ($run.Kind -eq 'dotnet') {
        foreach ($r in Read-TrxResults $run.Results) { $r | Add-Member Where $run.Where; $r | Add-Member Run $run.Name; $dotnetResults.Add($r) }
    }
    else { $suiteResults[$run.Name] = Read-SuiteResults $run }
}

# Retry new failures (up to -Retries times), apart from the full parallel load: a test that then passes is
# reported as flaky instead of blocking. Failures on a Linux host are not retried.
$flaky = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$firstRuns = @($runs)
$pendingTests = @{}
foreach ($group in $dotnetResults | Where-Object { $_.Outcome -eq 'Failed' -and $_.Run -eq 'local-dotnet' -and -not $known.ContainsKey($_.Id) } | Group-Object Group) {
    $pendingTests[$group.Name] = @($group.Group.Id | Sort-Object -Unique)
}
$pendingSuites = @($firstRuns | Where-Object { $_.Kind -ne 'dotnet' -and ($suiteResults[$_.Name].Failures | Where-Object { -not $known.ContainsKey($_.Id) }) })
for ($attempt = 1; $attempt -le $Retries -and ($pendingTests.Count -or $pendingSuites.Count); $attempt++) {
    $retryRuns = [Collections.Generic.List[object]]::new()
    foreach ($assembly in @($pendingTests.Keys)) {
        $p = $localTests | Where-Object Assembly -eq $assembly | Select-Object -First 1
        if (-not $p) { $pendingTests.Remove($assembly); continue }
        $retryFilter = ($pendingTests[$assembly] | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
        $results = Join-Path $out 'trx' "retry$attempt-$($p.Name)"
        $arguments = @('test', (Join-Path $root $p.Rel), '-c', $Configuration, '--no-build', '--nologo', '--logger', 'trx;LogFilePrefix=r', '--results-directory', $results, '--filter', $retryFilter)
        $run = Start-Run "retry$attempt-$($p.Name)" 'dotnet' 'here' @(New-Step $dotnet $arguments) $root @($p) -Results $results
        $run | Add-Member Retries $assembly
        $retryRuns.Add($run)
    }
    foreach ($suite in $pendingSuites) {
        $run = Start-Run "retry$attempt-$($suite.Name)" $suite.Kind 'here' $suite.Steps $suite.WorkingDirectory -Detail $suite.Detail
        $run | Add-Member Retries $suite.Name
        $retryRuns.Add($run)
    }
    Write-Host "Retrying new failures (attempt $attempt of $Retries): $(($retryRuns | ForEach-Object Name) -join ', ')"
    Wait-Runs @($retryRuns)
    $nextSuites = [Collections.Generic.List[object]]::new()
    foreach ($run in $retryRuns) {
        if ($run.Kind -eq 'dotnet') {
            $again = @(Read-TrxResults $run.Results)
            $still = @($pendingTests[$run.Retries] | Where-Object {
                $id = $_
                $mine = @($again | Where-Object Id -eq $id)
                if ($mine -and -not ($mine | Where-Object Outcome -ne 'Passed')) { [void]$flaky.Add($id); $false } else { $true }
            })
            if ($still) { $pendingTests[$run.Retries] = $still } else { $pendingTests.Remove($run.Retries) }
        }
        else {
            $original = $suiteResults[$run.Retries]
            $again = Read-SuiteResults $run
            if ($run.ExitCode -in 0, 1 -and $again.Passed -gt 0) {
                foreach ($f in $original.Failures) { if (-not ($again.Failures | Where-Object Id -eq $f.Id)) { [void]$flaky.Add($f.Id) } }
            }
            if ($original.Failures | Where-Object { -not $known.ContainsKey($_.Id) -and -not $flaky.Contains($_.Id) }) {
                $nextSuites.Add(($firstRuns | Where-Object Name -eq $run.Retries))
            }
        }
    }
    $pendingSuites = @($nextSuites)
}
foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }

function Get-Verdict([string[]]$Failed) {
    if ($Failed | Where-Object { -not $known.ContainsKey($_) -and -not $flaky.Contains($_) }) { 'Failed' }
    elseif ($Failed | Where-Object { $flaky.Contains($_) }) { 'Flaky' }
    elseif ($Failed) { 'Known failures' }
    else { 'Passed' }
}

$rows = [Collections.Generic.List[object]]::new()
$details = [Collections.Generic.List[string]]::new()
$failedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$ranGroups = [Collections.Generic.List[string]]::new()
$runNotes = [Collections.Generic.List[string]]::new()
foreach ($run in $runs) { foreach ($note in $run.Notes) { $runNotes.Add("$($run.Name): $note") } }
function Add-Row($Suite, $Where, $Result, $Passed, $Failed, $Skipped, $Time) {
    $rows.Add([pscustomobject]@{ Suite = $Suite; Where = $Where; Result = $Result; Passed = $Passed; Failed = $Failed; Skipped = $Skipped; Time = $Time })
}
function Add-LogTail($Run, [string[]]$Lines) {
    $details.Add("[$($Run.Where)] $($Run.Name) exited with $($Run.ExitCode); last lines of $($Run.Log):")
    $Lines | Select-Object -Last 10 | ForEach-Object { $details.Add("    $_") }
}

foreach ($run in $firstRuns) {
    $lines = if (Test-Path -LiteralPath $run.Log) { @(Get-Content -LiteralPath $run.Log) } else { @() }
    if ($run.Kind -eq 'dotnet') {
        $mine = @($dotnetResults | Where-Object Run -eq $run.Name)
        foreach ($set in $mine | Group-Object Group, Detail) {
            $failed = @($set.Group | Where-Object Outcome -eq 'Failed')
            foreach ($f in $failed) {
                [void]$failedIds.Add($f.Id)
                if (-not $known.ContainsKey($f.Id) -and -not $flaky.Contains($f.Id)) {
                    $details.Add("[$($run.Where)] $($f.Name)")
                    foreach ($l in @($f.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 4)) { $details.Add("    $($l.Trim())") }
                }
            }
            $first = $set.Group[0]
            Add-Row "$($first.Group) ($($first.Detail))" $run.Where (Get-Verdict @($failed.Id)) @($set.Group | Where-Object Outcome -eq 'Passed').Count `
                $failed.Count @($set.Group | Where-Object { $_.Outcome -notin 'Passed', 'Failed' }).Count "$($first.Seconds) s"
            if (-not $Filter) { $ranGroups.Add($first.Group) }
        }
        $missing = @($run.Items | Where-Object { -not ($mine | Where-Object Group -eq $_.Assembly) })
        foreach ($p in $missing) { Add-Row $p.Name $run.Where 'Failed' 0 0 0 'no test result: see log' }
        foreach ($e in @($lines | Where-Object { $_ -match ': error [A-Z]+\d*:' } | ForEach-Object { $_.Trim() } | Select-Object -Unique -First 15)) { $details.Add("[$($run.Where)] $e") }
        if ($run.ExitCode -ne 0 -and -not ($mine | Where-Object Outcome -eq 'Failed')) {
            if (-not $missing) { Add-Row $run.Name $run.Where 'Failed' 0 0 0 "exit $($run.ExitCode): see log" }
            Add-LogTail $run $lines
        }
    }
    else {
        $suite = $suiteResults[$run.Name]
        $ids = @($suite.Failures | ForEach-Object Id)
        foreach ($id in $ids) { [void]$failedIds.Add($id) }
        $result = Get-Verdict $ids
        if ($run.ExitCode -notin 0, 1 -or ($run.ExitCode -ne 0 -and -not $ids.Count)) { $result = 'Failed' }
        $newOnes = @($suite.Failures | Where-Object { -not $known.ContainsKey($_.Id) -and -not $flaky.Contains($_.Id) })
        foreach ($f in $newOnes) {
            $details.Add("[here] $($f.Id)")
            foreach ($l in @($f.Message -split "`n")) { if ($l.Trim()) { $details.Add("    $($l.Trim())") } }
        }
        if ($result -eq 'Failed' -and -not $newOnes) { Add-LogTail $run $suite.Lines }
        $suiteName = if ($run.Detail) { "$($run.Name) ($($run.Detail))" } else { $run.Name }
        Add-Row $suiteName $run.Where $result $suite.Passed $ids.Count $suite.Skipped "$($run.Seconds) s"
        if (-not $Filter -and $run.ExitCode -in 0, 1) { $ranGroups.Add($run.Name) }
    }
}

$fixed = @($known.Keys | Where-Object {
    $id = $_
    -not $known[$id] -and -not $failedIds.Contains($id) -and
        ($ranGroups | Where-Object { $id.StartsWith("$_.", [StringComparison]::Ordinal) -or $id.StartsWith("$_/", [StringComparison]::Ordinal) })
} | Sort-Object)
$knownHit = @($failedIds | Where-Object { $known.ContainsKey($_) })
$flakyHit = @($failedIds | Where-Object { $flaky.Contains($_) } | Sort-Object)
$elapsed = [math]::Round($watch.Elapsed.TotalSeconds)
$blocking = [bool]($rows | Where-Object Result -eq 'Failed')
$order = @{ 'Failed' = 0; 'Flaky' = 1; 'Known failures' = 2; 'Passed' = 3 }
$sorted = @($rows | Sort-Object { $order[$_.Result] }, Suite)
$sorted | Format-Table Suite, Where, Result, Passed, Failed, Skipped, Time -AutoSize | Out-String -Width 220 | Write-Host
if ($details.Count) { Write-Host 'New failures (these block the change):'; $details | Select-Object -First 120 | ForEach-Object { Write-Host "  $_" } }
foreach ($id in $flakyHit) { Write-Host "Flaky (failed, then passed on retry; fix it): $id" }
if ($knownHit.Count) { Write-Host "Known failures (tests/known-failures.txt, not blocking): $($knownHit.Count)" }
foreach ($id in $fixed) { Write-Host "Known failure now passes; delete its line from tests/known-failures.txt: $id" }
foreach ($n in $runNotes) { Write-Host "Note: $n" }
foreach ($n in $notRun) { Write-Host "NOT RUN: $n" }
foreach ($f in $uncovered) { Write-Host "Not covered by automated tests (validate by running it): $f" }
if ($productChanged) {
    $names = if ($productChanged.Count -gt 6) { "$(($productChanged | Select-Object -First 6) -join ', ') and $($productChanged.Count - 6) more" } else { $productChanged -join ', ' }
    Write-Host "Product code changed ($names): verify the behavior through Martlet MCP too (scripts/Invoke-MartletMcp.ps1, docs/VALIDATION.md)."
}

$md = [Collections.Generic.List[string]]::new()
$md.Add('### Tests')
$md.Add('')
$md.Add("``scripts/Test-Martlet.ps1`` at $headCommit on $here ($scope$(if ($Filter) { ", filter $Filter" })), $elapsed s.")
$md.Add('')
$md.Add('| Suite | Where | Result | Passed | Failed | Skipped | Time |')
$md.Add('| --- | --- | --- | ---: | ---: | ---: | --- |')
foreach ($r in $sorted) { $md.Add("| $($r.Suite) | $($r.Where) | $($r.Result) | $($r.Passed) | $($r.Failed) | $($r.Skipped) | $($r.Time) |") }
if ($flakyHit.Count) { $md.Add(''); $md.Add('**Flaky** (failed, then passed on retry):'); $md.Add(''); foreach ($id in $flakyHit) { $md.Add("- ``$id``") } }
if ($knownHit.Count) { $md.Add(''); $md.Add("Known failures from ``tests/known-failures.txt`` (already failing on main): $($knownHit.Count).") }
if ($fixed.Count) { $md.Add(''); $md.Add('**Known failures that now pass** (delete their lines):'); $md.Add(''); foreach ($id in $fixed) { $md.Add("- ``$id``") } }
if ($notRun.Count) { $md.Add(''); $md.Add('**NOT RUN**'); $md.Add(''); foreach ($n in $notRun) { $md.Add("- $n") } }
if ($runNotes.Count) { $md.Add(''); foreach ($n in $runNotes) { $md.Add("- Note: $n") } }
if ($uncovered.Count) { $md.Add(''); $md.Add('**Not covered by automated tests** (validated by running them):'); $md.Add(''); foreach ($f in $uncovered) { $md.Add("- ``$f``") } }
[IO.File]::WriteAllLines((Join-Path $out 'summary.md'), $md)

$passedCount = ($rows | Measure-Object Passed -Sum).Sum
$newCount = @($failedIds | Where-Object { -not $known.ContainsKey($_) -and -not $flaky.Contains($_) }).Count
$verdict = if ($blocking) { 'FAILED' } elseif ($notRun.Count) { 'PASSED with NOT RUN items' } else { 'PASSED' }
Write-Host ("{0}: {1} passed, {2} new failing test(s), {3} flaky, {4} known, in {5} s. Summary: {6}" -f $verdict, $passedCount, $newCount, $flakyHit.Count, $knownHit.Count, $elapsed, (Join-Path $out 'summary.md'))
if ($blocking -or ($RequireAll -and $notRun.Count)) { exit 1 }
