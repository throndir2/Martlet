<#
.SYNOPSIS
Prints the release notes for one version from CHANGELOG.md.

.DESCRIPTION
Returns the body of the "## [<Version>] - <date>" section of CHANGELOG.md (everything up to the next "## ["
heading). Fails when the section is missing or empty, so a release cannot be published without notes.
windows-release.yml uses it before restoring dependencies; run it locally to preview a release's notes.

.EXAMPLE
.\scripts\Get-ReleaseNotes.ps1 -Version 0.49.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md')
)
$ErrorActionPreference = 'Stop'
$changelog = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).ProviderPath) -replace "`r`n", "`n"
$pattern = '(?ms)^## \[' + [regex]::Escape($Version) + '\] - \d{4}-\d{2}-\d{2}[ \t]*\n(.*?)(?=^## \[|\z)'
$section = [regex]::Match($changelog, $pattern)
$notes = if ($section.Success) { $section.Groups[1].Value.Trim() } else { '' }
if ($notes -notmatch '(?m)^- \S') {
    throw "CHANGELOG.md has no notes for $Version. Move the Unreleased entries under a '## [$Version] - YYYY-MM-DD' heading in the release PR."
}
$notes
