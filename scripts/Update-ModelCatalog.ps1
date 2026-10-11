<#
.SYNOPSIS
Refreshes the model catalog's snapshot that ships with Martlet (src\Martlet.Core\Planning\Catalog\model-catalog.json).

.DESCRIPTION
Builds Martlet's MCP server from this checkout and runs model_catalog_refresh with live=true and snapshotPath: the production
refresh (ModelCatalogRefresh) reads the public sources (OpenRouter, models.dev, NVIDIA Build, vLLM, LMArena and Hugging Face;
no key) and writes the snapshot only when every source was read. It takes a few minutes (NVIDIA Build's model pages are slow).
Run it before a release and commit the changed snapshot with the release PR. See docs/MODEL_CATALOG.md.

.EXAMPLE
.\scripts\Update-ModelCatalog.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$snapshot = Join-Path $root 'src\Martlet.Core\Planning\Catalog\model-catalog.json'
$calls = ConvertTo-Json -Compress -Depth 5 -InputObject @(@{ name = 'model_catalog_refresh'; arguments = @{ live = $true; snapshotPath = $snapshot } })
$output = & (Join-Path $PSScriptRoot 'Invoke-MartletMcp.ps1') -Build -Configuration $Configuration -Calls $calls -TimeoutSeconds $TimeoutSeconds
$output
$result = ($output | Out-String | ConvertFrom-Json)[0].result
if ($result -is [string]) { $result = $result | ConvertFrom-Json }
if (-not $result.snapshotWritten) { throw "The snapshot was not written: $($result.why)" }
Write-Host "Wrote $snapshot ($([math]::Round($result.bytes / 1MB, 1)) MB). Rebuild Martlet.Core to ship it."
