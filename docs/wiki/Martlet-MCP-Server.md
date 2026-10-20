# Martlet MCP Server

`src\Martlet.Mcp` is Martlet's own MCP server (Model Context Protocol). AI assistants such as GitHub Copilot, Claude, VS Code or any MCP client use it to make and change Martlet's characters, change its settings, drive the desktop window and run diagnostics. Developers use it to verify changes. It is a different thing from the MCP client that gives Martlet tools while you talk (see [Tools and MCP](Tools-and-MCP)).

The server runs only on Windows, for the current Windows user. It speaks JSON-RPC over standard input and output. It never listens on a network port. It does not start the desktop, use providers, open audio devices or handle credentials when it starts. The installer does not include it: build it from a checkout.

## Connect an assistant

1. Run this from a checkout:

   ```powershell
   .\scripts\Register-MartletMcp.ps1
   ```

   It builds the server, copies it to `%LOCALAPPDATA%\Martlet.Mcp` and adds a `martlet` server to GitHub Copilot (CLI and app), Claude Desktop and VS Code, for each one that is installed. It backs up each configuration file first.
2. Restart the assistant.
3. Ask, for example: "Make a Martlet character called Ava, a cheerful astronomer." The assistant starts with `martlet_guide`.

Script options: `-Client copilot, claude, vscode` chooses the assistants, `-ReadOnly` leaves changes off, `-AllowUiEffects` lets the `ui_*` tools press buttons that change things, `-Unregister` removes the entry, and `-NoBuild` uses the existing build.

## Flags

| Flag | What it does |
| --- | --- |
| `--allow-changes` | Lets `character_create`, `character_update`, `character_use`, `character_delete` and `settings_set` save. Without it, they refuse. |
| `--allow-ui-effects` | Lets the `ui_*` tools press buttons, type and toggle. Without it, they only navigate and read. |
| `--all-tools` | Lists every tool in `tools/list`. Every tool always runs by name. |

A running Martlet follows saved changes by itself, without a restart.

## The tools

By default, the server lists the 24 tools that an assistant needs most:

- Start here: `martlet_guide` (the common tasks step by step, every tool by area, and a search) and `martlet_call` (runs any tool by name).
- Characters: `characters_list`, `character_find_looks`, `character_find_on_web`, `character_create` (from a description or a SillyTavern or Chub card), `character_update`, `character_use`, `character_delete`.
- Settings: `settings_get`, `settings_schema`, `settings_set`.
- Diagnostics: `doctor_status`, `doctor_list`, `doctor_run`, `logs_tail`.
- The desktop window: `ui_connect`, `ui_snapshot`, `ui_click`, `ui_select`, `ui_set_text`, `ui_toggle`, `ui_set_range`, `ui_scroll`.

More than 200 other tools run through `martlet_call` or with `--all-tools`. They include status tools and checks for voices, listening, the Thinking pool, memory, the network, accounts, sharing, Discord, smart home, pictures, singing, latency and more, the self-tests, and the other window tools (`ui_type`, `ui_move`, `ui_tray`).

The server never changes where Martlet thinks, listens and speaks, or its API keys. `settings_set` refuses `setup`, `thinking_fallback`, `audio` and `profile`, because they need the owner's consent in the app.

## Verify a change

`scripts\Invoke-MartletMcp.ps1` runs this checkout's server, sends a list of tool calls in order and prints one JSON array of results. It uses a disposable data directory and deletes it at the end.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"doctor_status"}]'
.\scripts\Invoke-MartletMcp.ps1 -Build -Desktop -Calls '[{"name":"ui_click","arguments":{"id":"TourSkip"}},{"name":"ui_click","arguments":{"id":"NavSettings"}},{"name":"ui_snapshot","until":"AutomaticUpdateCheck"}]'
```

- Each call is `{"name":"<tool>","arguments":{}}`, with an optional `waitMs` (a pause after the call) and `until` (repeat the call for up to 20 seconds until the result contains this text). `-Calls` also takes the path of a JSON file.
- `-Build` builds the server, and the desktop with `-Desktop`.
- `-Desktop` starts the desktop on the same disposable data directory. It needs an unlocked, interactive desktop. `-DesktopArguments` adds start flags, for example `--tray`.
- `-KeepDesktop` leaves the desktop running. `-DesktopProcessId` and `-DataDirectory` use it again in a later run.
- `-AllowChanges` passes `--allow-changes`. The tools save to the disposable data directory.
- `-AllowUiEffects` passes `--allow-ui-effects`. Use it only with disposable data and no real credentials. It never permits spending, real provider requests, credential handling, audio capture or playback, or data disclosure.

The script exits 1 when a call fails or an `until` text does not show.

`companion_status` checks the Linux and macOS companion from Windows. It runs `Martlet.Companion --status` and can simulate another platform (`platform`: `linux-x64`, `linux-nvidia`, `linux-arm64`, `macos-arm64` or `macos-x64`).

## Extend the server with each feature

A feature that MCP cannot reach or observe is not finished. In the same change:

1. Give each new control and status field a stable, unique `AutomationProperties.AutomationId`.
2. Add passive navigation to `SafeClicks`, and non-secret status fields to `SafeValues`, in `src\Martlet.Mcp.Protocol\AutomationPolicy.Clicks.cs` and `AutomationPolicy.Values.cs`. Never expose credentials, personal data, editable fields or file paths.
3. Add a headless tool as a `Tool(...)` registration in `src\Martlet.Mcp.Protocol\Tools\<Area>Tools.cs`, or add a Doctor probe.
4. Add a tool that assistants need to `Listed` in `src\Martlet.Mcp.Protocol\McpServer.Discovery.cs`. Keep the short list small.
5. Name each new tool in `docs\MCP.md`. `McpDocsAgreementTests` fails when a tool and the page do not agree.

More detail: [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md), [Use Martlet from an AI assistant](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md#use-martlet-from-an-ai-assistant), [Extending the server](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md#extending-the-server), [Validating changes](https://github.com/throndir2/Martlet/blob/main/docs/VALIDATION.md#mcp-verification).
