# Local MCP control (Windows)

`Martlet.Mcp` is a local stdio Model Context Protocol server. It does not listen
on a network port, start the desktop, or activate a provider, microphone or
speaker on launch. Configure an MCP client to start the Windows executable
built from `src\Martlet.Mcp` (for development:
`src\Martlet.Mcp\bin\Release\net10.0-windows\Martlet.Mcp.exe`). The server
speaks newline-delimited JSON-RPC 2.0 on standard input/output; stderr is for
diagnostics. This developer companion is not included in the existing internal
installer payload. Build locally with:

```powershell
dotnet build src\Martlet.Mcp\Martlet.Mcp.csproj -c Release
```

For example, a client configuration with an absolute executable path:

```json
{
  "mcpServers": {
    "martlet": {
      "command": "C:\\path\\to\\Martlet.Mcp.exe",
      "args": []
    }
  }
}
```

The headless tools `doctor_status`, `doctor_list`, `doctor_run` and `fixture`
call the production Doctor implementation in-process. Status and selected
probes are local read-only checks; `fixture` runs scripted, offline, audio-OFF
scenarios only (**not AI**). They return Doctor's structured JSON report and
exit code; nonzero exit codes describe incomplete, failed or invalid results,
not a passed check. An optional absolute `dataDirectory` argument isolates
settings reads; without it, Doctor uses the current user's Martlet directory.
No headless MCP tool creates a profile, opens a device, plays a tone, sends a
request or handles credentials.

To drive the visible desktop, start `Martlet.Desktop.exe` yourself in the **same
interactive Windows session** (ideally with a disposable `--data-directory`).
Call `ui_connect` with that process ID. `ui_snapshot` returns window accessible names,
automation IDs, enabled states, checkbox states, and selected read-only status
fields; it does not dump arbitrary editable fields or credentials. `ui_click`
invokes a control by automation ID and `ui_select` selects a named combo-box
option. By default only passive navigation and
audio-OFF fixture controls can be clicked; only `FixtureScenario` may be
selected. The main window is split into pages, and a page's controls are only
visible after you open it: click `NavHome`, `NavDevices`, `NavCompanion` or
`NavSettings` first (for example `NavSettings` before `StartFixture`, or
`NavCompanion` before `OpenSetup`). On Settings, click `DiagnosticsSection` to
expand the pipeline and status fields. On a fresh data directory, `TourSkip`
dismisses the welcome tour. Use `ui_snapshot` again to observe asynchronous effects. Modal
actions may return `completed: false` while their dialog remains open; this
means the invoke is still pending, not that the action finished.

Window discovery uses visible top-level native handles filtered to the attached
process, then verifies ownership around each UI Automation handle lookup.
This avoids transient omissions from UI Automation's desktop-root enumeration
when unrelated WPF windows close. The Martlet main-window automation ID is
still required on every operation; hidden, closed or changed-owner windows
fail rather than falling back to another process. Same-process dialogs remain
available, and duplicate control IDs still fail as ambiguous.

For broader **explicitly authorized** live UI testing, start the MCP server
with `--allow-ui-effects`. This unlocks arbitrary ID-based `ui_click` and
`ui_select`, plus `ui_set_text` and `ui_toggle`. It does **not** waive the
desktop's own per-action confirmations, spending/data disclosures, or Stop
controls. This opt-in can allow the LLM to approve chargeable provider calls,
audio capture/playback, credential actions and file operations by manipulating
the UI; only enable it for an attended, isolated test with the intended
permissions and budget. Do not use a real profile or provide this flag to an
untrusted MCP client. UI Automation needs an unlocked interactive desktop and
can fail in an unattended/headless session; use Doctor's headless tools there.
Neither a fixture pass nor an automation click is proof of actual microphone,
speaker or provider readiness.
