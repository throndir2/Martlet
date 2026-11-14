# MCP: Server and protocol internals

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de (flags, error codes, limits and the tool registry; the list of check classes is by file name only)
**Code:** `src/Martlet.Mcp/Program.cs`, `src/Martlet.Mcp.Protocol/McpServer.cs`, `src/Martlet.Mcp.Protocol/McpServer.Discovery.cs`, `src/Martlet.Mcp.Protocol/Tools/McpTool.cs`, `src/Martlet.Mcp.Protocol/Tools/StartTools.cs`, `src/Martlet.Mcp.Protocol/Fixtures`, `tests/Martlet.Desktop.Tests/McpDocsAgreementTests.cs`

Part of [MCP in Martlet](../MCP.md). This page answers: how does the server
read requests, how are tools registered, and where does a new tool go?

## Process and flags

`src/Martlet.Mcp/Program.cs` starts the server on standard input and
output. It accepts `--allow-ui-effects`, `--allow-changes` and
`--all-tools`. Any other argument prints a usage line and exits with code 2.
[Tool permissions](permissions.md) tells what the first two flags allow.

## Protocol

- The server reads one JSON-RPC message for each line. A line over 1 MiB is
  refused.
- It answers `initialize` with protocol version `2025-06-18` and
  `serverInfo` `{name: "martlet", title: "Martlet", version}`.
- Error codes: `-32700` bad JSON, `-32600` bad request, `-32601` unknown
  method, `-32603` internal error. An error text is cut to 2000 characters.
- Tool calls run in the order they came. The server keeps reading while one
  runs: `ping` and the other methods answer at once. `notifications/cancelled`
  stops a running or waiting call, and a cancelled call gets no response.

## Tool registry

1. Each area has a file `Tools/<Area>Tools[.Feature].cs` in
   `src/Martlet.Mcp.Protocol`. It registers tools with
   `McpServer.Tool(name, description, props, [required], handler, permission)`.
2. `McpServer.ToolGroups()` sets the order of the areas, and so the order of
   the ten area pages in this folder.
3. `tools/list` returns only the 24 tools in `Listed` (with titles and
   hints) unless the server runs with `--all-tools`. Every other tool runs
   through `martlet_call`, which refuses to call itself.
4. `martlet_guide` explains each area (see `Tools/StartTools.cs`).

## Checks and fixtures

A check class (for example `CheckInsCheck.cs` or `HelperJobsCheck.cs`) runs a
Martlet feature headless on a disposable data directory and returns each
step with `passed`. `Fixtures` holds `FixtureChat` and `LoopbackHttpServer`. They stand in
for a model and for a web server on this computer, so a check does not need
real credentials.

## Keeping docs and code the same

`McpDocsAgreementTests` reads `docs/MCP.md` and every page in this folder. It
fails when a registered tool is not named in them, or when they name a tool
the server does not have. Add a tool and its page entry in one change.

## Planned

None.
