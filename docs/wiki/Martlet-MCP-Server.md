# Martlet MCP Server

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Mcp`, `src/Martlet.Mcp.Protocol`, `scripts/Invoke-MartletMcp.ps1`, `scripts/Register-MartletMcp.ps1`

Martlet's Model Context Protocol (MCP) server lets an assistant manage characters, read settings, use desktop controls and run diagnostics. It is separate from the [MCP client](Tools-and-MCP) that gives Martlet tools during a conversation.

The server is a Windows program. It uses standard input and output for MCP messages. A tool can do more than the server's startup: for example, the character search tools contact websites.

## Connect an assistant

Run `scripts/Register-MartletMcp.ps1` from a checkout. It builds and copies the server, then registers it with assistants whose settings folders exist. If none exist, it selects Copilot. It backs up existing configuration files.

By default, registration enables changes. Use `-ReadOnly` to disable them. Use `-Client` to choose assistants, `-Unregister` to remove the entry, and `-NoBuild` to use an existing build. Restart the assistant after registration. See [Connect an assistant](https://github.com/throndir2/Martlet/blob/main/docs/mcp/start-here.md#connect-an-assistant) for details.

## Flags and tools

- `--allow-changes` enables writes by character, settings and other tools that require it.
- `--allow-ui-effects` allows UI actions that change things. Without it, the UI tools navigate and read.
- `--all-tools` lists all tools. Unlisted tools still work by name or through `martlet_call`.

Start with `martlet_guide`. The default list includes character, settings, Doctor, log and UI tools. The server uses the current user's Martlet data folder unless a tool receives `dataDirectory`. The settings tools do not change provider routes or API keys; those need consent in the app.

## Verify a change

`scripts/Invoke-MartletMcp.ps1` runs tool calls against this checkout. By default it supplies a disposable data directory to tools that accept one. `-Build` builds the server. `-Desktop` also builds and starts the desktop when used with `-Build`. The UI needs an interactive Windows desktop.

Use `-AllowChanges` only for intended writes. Use `-AllowUiEffects` only with disposable data and no real credentials. It does not authorize paid requests, audio capture, playback or data disclosure. Check the returned outcome, not only whether the call succeeded.

The [verification guide](https://github.com/throndir2/Martlet/blob/main/docs/mcp/start-here.md#verifying-changes-with-martlet-mcp) gives call examples and options. The [extension guide](https://github.com/throndir2/Martlet/blob/main/docs/mcp/start-here.md#extending-the-server) explains how to expose new controls and status.
