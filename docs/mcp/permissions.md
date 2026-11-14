# MCP: Tool permissions

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de (permission values, `--allow-changes` list and `--allow-ui-effects` messages read in the source; the `Writes` list was extracted by script)
**Code:** `src/Martlet.Mcp.Protocol/Tools/McpTool.cs`, `src/Martlet.Mcp.Protocol/McpServer.Discovery.cs`, `src/Martlet.Mcp.Protocol/AutomationPolicy.Rules.cs`, `src/Martlet.Mcp.Protocol/DesktopAutomation.cs`, `src/Martlet.Mcp/Program.cs`

Part of [MCP in Martlet](../MCP.md). This page answers one question: which
tools need `--allow-changes` or `--allow-ui-effects`, and what each flag
never allows. The [start page](start-here.md) tells how to set the flags.

## The two flags

Each tool has a `ToolPermission` when it is registered with `McpServer.Tool(...)`.

| Value | Meaning |
|---|---|
| `None` | The tool only reads. It needs no flag. |
| `Changes` | The tool always saves something. It needs `--allow-changes`. |
| `ChangesUnlessPreview` | The tool checks the flag itself. A preview is free. A real change needs `--allow-changes`. |
| `UiEffects` | The tool acts on the desktop window. `DesktopAutomation` checks `--allow-ui-effects`. |

Some tools have a `Writes` rule: a test of the arguments of one call. When
the test is true, the call needs `--allow-changes`. The server refuses the
call and says to add the flag or run `scripts\Register-MartletMcp.ps1`.

## Tools that always need `--allow-changes`

`character_create`, `character_update`, `character_use`, `character_delete`,
`settings_set`, `skill_directory_add`, `preferences_add`,
`preferences_update`, `preferences_remove`, `game_state_correct`,
`game_state_delete`, `logs_export`.

## Tools that need it only for some calls

| Tool | Needs `--allow-changes` when |
|---|---|
| `memory_forget`, `memory_where` | The call is not a preview. |
| `character_eyes` | `save` or `forget` is given. |
| `character_picture` | `outputPath` is given. |
| `character_reaction_changes` | `save` is given. |
| `character_touch_zones` | `save` is given. |
| `discord_companion_check` | `requestFrom` is given. |
| `kept_motions_check` | `dataDirectory` is given. |
| `thinking_fallback_timing` | `seed` is given. |
| `thinking_speed_check` | `seedSlow` is given. |

## Tools that need `--allow-ui-effects`

`character_animation`, `character_mouth`, `character_stroke`,
`character_touch`, `ui_click`, `ui_move`, `ui_select`, `ui_set_range`,
`ui_set_text`, `ui_toggle`, `ui_tray` and `ui_type`.

Without the flag, `ui_click` presses only the controls in `AutomationPolicy`
`SafeClicks` and `SafeClickPrefixes`. `ui_toggle` changes only
`MemoryShowHistory` and `SettingsShowAdvanced`. `ui_snapshot` returns the
text only of `SafeValues` controls. It also replaces an e-mail address with
`[email]` and a file path with `[path]`. `ui_scroll` and the read-only
character tools (look, zones, breath, face) need no flag.

## Output files and data folders

- An output file must not be on a network path (UNC or network drive).
  It must not replace a file that exists (`RequireLocalNewFile`).
- A tool that saves a fixture refuses Martlet's own data folder,
  `%LOCALAPPDATA%\Martlet`. Only an absolute `dataDirectory` that differs
  from it counts as disposable (`DisposableDataDirectory`).
- `dataDirectory` defaults to `%LOCALAPPDATA%\Martlet` and must be absolute.

## What the flags never allow

`--allow-ui-effects` is for verification on a disposable data directory with
no real credentials. It never authorizes spending, real provider requests,
credential handling, audio capture or playback, or disclosure of data. Stop
at those steps. The rule comes from the repository agent instructions in
`AGENTS.md`.

## Planned

None.
