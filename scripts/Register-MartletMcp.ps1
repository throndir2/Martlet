<#
.SYNOPSIS
Makes Martlet's MCP server known to the AI assistants on this PC.

.DESCRIPTION
Builds src\Martlet.Mcp from this checkout, copies it to its own folder under
%LOCALAPPDATA%\Martlet.Mcp (so a running server never locks this checkout's
build) and adds a "martlet" server to the MCP configuration of each assistant:

- copilot: GitHub Copilot CLI and the GitHub Copilot app (%USERPROFILE%\.copilot\mcp-config.json)
- claude:  Claude Desktop (%APPDATA%\Claude\claude_desktop_config.json)
- vscode:  VS Code (%APPDATA%\Code\User\mcp.json)

Without -Client it registers with every assistant whose settings folder exists
(Copilot when none does). The server may make and change characters and
settings (--allow-changes) unless -ReadOnly is given; -AllowUiEffects also lets
its ui_* tools press buttons that change things. Other servers in each file stay
as they are. Each run first copies the file to a new backup next to it
(<file>.martlet-backup-<date>-<time>; the first one and the four newest are
kept) and prints its path. A file with comments (VS Code's mcp.json may have
them) is not changed unless -Force is given, because the rewrite removes the
comments; the backup keeps them. Restart the assistant afterwards.
-Unregister removes the "martlet" entry again.

Every tool works from the copy except the developer self-tests that start
Martlet.NodeLinkCheck or Martlet.Companion from a checkout; run those with
scripts\Invoke-MartletMcp.ps1.

.EXAMPLE
.\scripts\Register-MartletMcp.ps1

.EXAMPLE
.\scripts\Register-MartletMcp.ps1 -Client copilot, claude -ReadOnly

.EXAMPLE
.\scripts\Register-MartletMcp.ps1 -Unregister
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('copilot', 'claude', 'vscode')][string[]]$Client,
    [switch]$ReadOnly,
    [switch]$AllowUiEffects,
    [switch]$Unregister,
    [switch]$NoBuild,
    # Rewrites a configuration file that has comments (they are lost; the backup keeps them).
    [switch]$Force,
    [string]$Configuration = 'Release',
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Martlet.Mcp'),
    # Writes this configuration file instead of the assistant's own (one -Client only); for checking the script.
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw "Martlet's MCP server runs on Windows." }
$root = Split-Path $PSScriptRoot -Parent

$clients = [ordered]@{
    copilot = @{ Name = 'GitHub Copilot CLI and app'; Path = Join-Path $env:USERPROFILE '.copilot\mcp-config.json'; Key = 'mcpServers' }
    claude  = @{ Name = 'Claude Desktop'; Path = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'; Key = 'mcpServers' }
    vscode  = @{ Name = 'VS Code'; Path = Join-Path $env:APPDATA 'Code\User\mcp.json'; Key = 'servers' }
}
if (-not $Client) {
    $Client = @($clients.Keys | Where-Object { Test-Path -LiteralPath (Split-Path $clients[$_].Path -Parent) -PathType Container })
    if ($Client.Count -eq 0) { $Client = @('copilot') }
}
if ($ConfigPath -and $Client.Count -ne 1) { throw '-ConfigPath needs exactly one -Client.' }

$server = $null
if (-not $Unregister) {
    $bin = Join-Path $root "src\Martlet.Mcp\bin\$Configuration\net10.0-windows"
    if (-not $NoBuild) {
        . (Join-Path $PSScriptRoot 'MartletDev.ps1')
        $dotnet = Find-MartletDotnet $root (Get-MartletDevProfile)
        $arguments = @('build', (Join-Path $root 'src\Martlet.Mcp\Martlet.Mcp.csproj'), '-c', $Configuration, '--nologo', '-v', 'q')
        $node = (Get-Command node -ErrorAction SilentlyContinue).Source
        if ($node) { $arguments += "-p:NodeExecutable=$node" }
        & $dotnet @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Building Martlet.Mcp failed.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $bin 'Martlet.Mcp.exe') -PathType Leaf)) { throw "Martlet.Mcp is not built at $bin." }

    # Each copy gets its own folder, so a server an assistant still runs keeps working and is never overwritten.
    $copy = Join-Path $InstallDirectory ('server-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    if ($PSCmdlet.ShouldProcess($copy, 'Copy Martlet.Mcp')) {
        $null = New-Item -ItemType Directory -Force -Path $copy
        Copy-Item -Path (Join-Path $bin '*') -Destination $copy -Recurse -Force
        # Older copies go when no server runs from them any more.
        $running = @(Get-Process -Name 'Martlet.Mcp' -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Where-Object { $_ })
        Get-ChildItem -LiteralPath $InstallDirectory -Directory -Filter 'server-*' | Where-Object FullName -ne $copy | ForEach-Object {
            $old = $_.FullName
            if (-not ($running | Where-Object { $_.StartsWith($old + '\', [StringComparison]::OrdinalIgnoreCase) })) {
                Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    $server = Join-Path $copy 'Martlet.Mcp.exe'
}

# Whether JSON text has // or /* */ comments outside its strings (JSONC, which VS Code allows and ConvertTo-Json drops).
function Test-JsonComments([string]$Text) {
    $inString = $false
    for ($i = 0; $i -lt $Text.Length - 1; $i++) {
        $c = $Text[$i]
        if ($inString) {
            if ($c -eq '\') { $i++ } elseif ($c -eq '"') { $inString = $false }
        }
        elseif ($c -eq '"') { $inString = $true }
        elseif ($c -eq '/' -and $Text[$i + 1] -in '/', '*') { return $true }
    }
    return $false
}

# Copies the file to a new dated backup next to it and keeps the first backup (the file before Martlet) and the four newest.
function Backup-Config([string]$Path) {
    $backup = "$Path.martlet-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')"
    Copy-Item -LiteralPath $Path -Destination $backup
    $backups = @(Get-ChildItem -LiteralPath (Split-Path $Path -Parent) -File -Filter "$(Split-Path $Path -Leaf).martlet-backup-*" | Sort-Object Name)
    if ($backups.Count -gt 5) {
        $backups[1..($backups.Count - 5)] | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }
    return $backup
}

$serverArguments = @()
if (-not $ReadOnly) { $serverArguments += '--allow-changes' }
if ($AllowUiEffects) { $serverArguments += '--allow-ui-effects' }

foreach ($name in $Client) {
    $target = $clients[$name]
    $path = if ($ConfigPath) { $ConfigPath } else { $target.Path }
    $config = [ordered]@{}
    $comments = $false
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $text = Get-Content -LiteralPath $path -Raw
        if ($text -and (Test-JsonComments $text)) {
            if (-not $Force) {
                Write-Warning "$($target.Name): $path has comments, and rewriting it would remove them, so it was not changed. Add the martlet server by hand, or run again with -Force (a backup keeps the comments)."
                continue
            }
            $comments = $true
        }
        if ($text -and $text.Trim()) {
            try { $config = ConvertFrom-Json -InputObject $text -AsHashtable -Depth 64 }
            catch { throw "$path isn't JSON that this script can read, so it was not changed: $($_.Exception.Message)" }
        }
    }
    if (-not $config.Contains($target.Key) -or $null -eq $config[$target.Key]) { $config[$target.Key] = [ordered]@{} }
    $servers = $config[$target.Key]
    if ($Unregister) {
        if (-not $servers.Contains('martlet')) { Write-Host "$($target.Name): no martlet server to remove ($path)."; continue }
        $servers.Remove('martlet')
    }
    else {
        $entry = [ordered]@{}
        switch ($name) {
            'copilot' { $entry.type = 'local'; $entry.command = $server; $entry.args = $serverArguments; $entry.tools = @('*') }
            'vscode' { $entry.type = 'stdio'; $entry.command = $server; $entry.args = $serverArguments }
            default { $entry.command = $server; $entry.args = $serverArguments }
        }
        $servers['martlet'] = $entry
    }
    if (-not $PSCmdlet.ShouldProcess($path, "$(if ($Unregister) { 'Remove' } else { 'Add' }) the martlet MCP server")) { continue }
    $folder = Split-Path $path -Parent
    if ($folder) { $null = New-Item -ItemType Directory -Force -Path $folder }
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $backup = Backup-Config $path
        Write-Host "$($target.Name): backed up $path to $backup."
        if ($comments) { Write-Warning "$($target.Name): the comments in $path are removed; $backup keeps them." }
    }
    Set-Content -LiteralPath $path -Value (ConvertTo-Json -InputObject $config -Depth 64) -Encoding utf8NoBOM
    Write-Host "$($target.Name): $(if ($Unregister) { 'removed the martlet server from' } else { 'added the martlet server to' }) $path."
}
if (-not $Unregister) {
    Write-Host "Martlet's MCP server: $server $($serverArguments -join ' ')"
    Write-Host 'Restart the assistant, then ask it to call martlet_guide.'
}
