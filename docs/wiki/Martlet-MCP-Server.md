# Martlet MCP Server

`src\Martlet.Mcp` is a local stdio MCP server. AI assistants (GitHub Copilot, Claude, VS Code or any MCP client) use it to make and change Martlet's characters, change its settings, drive the desktop window and run diagnostics. It is separate from Martlet's MCP client tools.

## Connect an assistant

```powershell
.\scripts\Register-MartletMcp.ps1
```

It builds the server, copies it to `%LOCALAPPDATA%\Martlet.Mcp` and adds a `martlet` server to GitHub Copilot (CLI and app), Claude Desktop and VS Code, for each one installed. Restart the assistant and ask it to call `martlet_guide`, or just ask: "Make a Martlet character called Ava, a cheerful astronomer." `-ReadOnly` leaves changes off; `-Unregister` removes it.

The server lists the 22 tools an assistant needs most (`martlet_guide`, `martlet_call`, `characters_list`, `character_create`, `character_update`, `character_use`, `character_delete`, `settings_get`, `settings_schema`, `settings_set`, Doctor, `logs_tail` and the `ui_*` tools); `martlet_call` runs the other tools by name. Flags: `--allow-changes` (the character and settings tools may save), `--allow-ui-effects` (the `ui_*` tools may press buttons that change things) and `--all-tools` (list every tool). A running Martlet follows saved changes by itself.

## Build

```powershell
dotnet build src\Martlet.Mcp\Martlet.Mcp.csproj -c Release
```

The server does not listen on a network port, start the desktop, activate providers, open audio devices or handle credentials on launch.

## Invoke script

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"doctor_status"}]'
.\scripts\Invoke-MartletMcp.ps1 -Build -Desktop -Calls '[{"name":"ui_click","arguments":{"id":"TourSkip"}},{"name":"ui_click","arguments":{"id":"NavSettings"}},{"name":"ui_snapshot","until":"AutomaticUpdateCheck"}]'
```

Each call is `{"name":"tool","arguments":{}}` with optional `waitMs` and `until`. `-Desktop` launches the desktop on the same disposable data directory. `-KeepDesktop` leaves it running. `-AllowUiEffects` is restricted to disposable data and no real credentials. `-AllowChanges` lets the character and settings tools save to the disposable data directory.

## Tool families

- Start here: `martlet_guide` (tasks step by step, every tool, search) and `martlet_call` (any tool by name).
- Characters and settings: `characters_list`, `character_create` (also from a SillyTavern/Chub card), `character_update`, `character_use`, `character_delete`, `settings_get`, `settings_schema`, `settings_set`.
- Doctor: `doctor_status`, `doctor_list`, `doctor_run`.
- Status/checks: voices, cluster, network, logs, API keys, smart home, messaging, Discord, terminal, prompts, memory, character, latency, creations, pictures, singing and more.
- UI: `ui_connect`, `ui_snapshot`, `ui_click`, `ui_select`, `ui_set_text`, `ui_toggle`, `ui_set_range`, `ui_move`, `ui_scroll`, `ui_tray`.

## Extending

Add stable automation IDs, passive navigation in `SafeClicks`, non-secret status in `SafeValues`, a headless tool or Doctor probe, and docs. Never expose credentials, personal data, editable fields or arbitrary file paths.

More detail: [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md), [Validation MCP verification](https://github.com/throndir2/Martlet/blob/main/docs/VALIDATION.md#mcp-verification), [Invoke script](https://github.com/throndir2/Martlet/blob/main/scripts/Invoke-MartletMcp.ps1).
