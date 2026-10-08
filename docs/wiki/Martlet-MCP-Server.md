# Martlet MCP Server

`src\Martlet.Mcp` is a local stdio MCP server for diagnostics, validation and UI automation. It is separate from Martlet's MCP client tools.

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

Each call is `{"name":"tool","arguments":{}}` with optional `waitMs` and `until`. `-Desktop` launches the desktop on the same disposable data directory. `-KeepDesktop` leaves it running. `-AllowUiEffects` is restricted to disposable data and no real credentials.

## Tool families

- Doctor: `doctor_status`, `doctor_list`, `doctor_run`.
- Status/checks: voices, cluster, network, logs, API keys, smart home, messaging, Discord, terminal, prompts, memory, character, latency, creations, pictures, singing and more.
- UI: `ui_connect`, `ui_snapshot`, `ui_click`, `ui_select`, `ui_set_text`, `ui_toggle`, `ui_set_range`, `ui_move`, `ui_scroll`, `ui_tray`.

## Extending

Add stable automation IDs, passive navigation in `SafeClicks`, non-secret status in `SafeValues`, a headless tool or Doctor probe, and docs. Never expose credentials, personal data, editable fields or arbitrary file paths.

More detail: [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md), [Validation MCP verification](https://github.com/throndir2/Martlet/blob/main/docs/VALIDATION.md#mcp-verification), [Invoke script](https://github.com/throndir2/Martlet/blob/main/scripts/Invoke-MartletMcp.ps1).
