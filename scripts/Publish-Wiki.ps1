<#
.SYNOPSIS
Publishes docs\wiki\*.md to the GitHub Wiki (https://github.com/throndir2/Martlet/wiki).

.DESCRIPTION
First checks the links: every link between wiki pages, and every wiki link in README.md, must point to a page
in docs\wiki and to a heading on that page. Relative links to repository files (such as ../ACCOUNTS.md) do not
work on the wiki and also fail the check. Then clones the wiki repository, replaces its pages with docs\wiki,
commits and pushes. The wiki is generated: edits made in the browser are replaced at the next publish.

.PARAMETER CheckOnly
Check the links and stop. Nothing is published.
#>
[CmdletBinding()]
param(
    [string]$Message = 'Update wiki from docs/wiki',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$wiki = Join-Path $root 'docs\wiki'
$wikiUrl = 'https://github.com/throndir2/Martlet/wiki'

function Get-Anchors([string]$text) {
    $anchors = [Collections.Generic.HashSet[string]]::new()
    $inFence = $false
    foreach ($line in $text -split "`r?`n") {
        if ($line -match '^\s*```') { $inFence = -not $inFence; continue }
        if ($inFence -or $line -notmatch '^#{1,6}\s+(.+?)\s*#*\s*$') { continue }
        $slug = ($Matches[1].ToLowerInvariant() -replace '[^\p{L}\p{Nd}\s_-]', '') -replace ' ', '-'
        [void]$anchors.Add($slug)
    }
    $anchors
}

$pages = @{}
Get-ChildItem $wiki -Filter *.md | Where-Object Name -ne 'README.md' | ForEach-Object {
    $pages[$_.BaseName] = Get-Content $_.FullName -Raw
}
$anchors = @{}
foreach ($name in $pages.Keys) { $anchors[$name] = Get-Anchors $pages[$name] }

$problems = [Collections.Generic.List[string]]::new()
function Test-Target([string]$from, [string]$page, [string]$anchor) {
    if (-not $page) { $page = 'Home' }
    if (-not $pages.ContainsKey($page)) { $problems.Add("$from -> $page (no such wiki page)"); return }
    if ($anchor -and -not $anchors[$page].Contains($anchor)) { $problems.Add("$from -> $page#$anchor (no such heading)") }
}

foreach ($name in $pages.Keys) {
    $text = [regex]::Replace($pages[$name], '(?ms)^\s*```.*?^\s*```', '')
    foreach ($m in [regex]::Matches($text, '\]\(([^)\s]+)\)')) {
        $target = $m.Groups[1].Value
        if ($target.StartsWith($wikiUrl)) { $target = $target.Substring($wikiUrl.Length).TrimStart('/') }
        elseif ($target -match '^(https?:|mailto:)') { continue }
        if ($target.StartsWith('#')) { Test-Target $name $name $target.Substring(1); continue }
        if ($target -match '[./\\]') { $problems.Add("$name -> $target (use a wiki page name or an absolute https:// link)"); continue }
        $page, $anchor = $target -split '#', 2
        Test-Target $name $page $anchor
    }
}
$readme = Get-Content (Join-Path $root 'README.md') -Raw
foreach ($m in [regex]::Matches($readme, [regex]::Escape($wikiUrl) + '(?:/([^)\s#"]+))?(?:#([^)\s"]+))?')) {
    Test-Target 'README.md' $m.Groups[1].Value $m.Groups[2].Value
}

if ($problems.Count) {
    $problems | Sort-Object -Unique | ForEach-Object { Write-Warning $_ }
    throw "$($problems.Count) broken wiki link(s). Fix them in docs\wiki or README.md."
}
"Links OK: $($pages.Count) wiki pages."
if ($CheckOnly) { return }

$work = Join-Path ([IO.Path]::GetTempPath()) ('Martlet.Wiki.' + [guid]::NewGuid().ToString('N'))
git clone -q "$wikiUrl.git" $work
if ($LASTEXITCODE -ne 0) { throw "Could not clone the wiki. Create its first page at $wikiUrl, then rerun." }
try {
    Get-ChildItem $work -Filter *.md | Remove-Item
    Copy-Item (Join-Path $wiki '*.md') $work -Exclude README.md
    git -C $work add -A
    if (git -C $work status --porcelain) {
        git -C $work commit -q -m $Message
        git -C $work push -q
        if ($LASTEXITCODE -ne 0) { throw 'Push to the wiki failed.' }
        'Wiki published.'
    } else { 'Wiki already up to date.' }
} finally { Remove-Item -Recurse -Force $work }
