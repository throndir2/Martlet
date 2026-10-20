# MCP: Start here

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Start here* area of `martlet_guide` (connecting an assistant, the server, its flags, verifying changes and extending the server). The [index](../MCP.md#pages) lists the other pages.

## Use Martlet from an AI assistant

`Martlet.Mcp` is a local stdio MCP server (Windows). It runs only when an
assistant starts it, never listens on a network port and works on the Martlet
data folder of the current Windows user (`%LOCALAPPDATA%\Martlet`).

### Connect an assistant

1. Run `.\scripts\Register-MartletMcp.ps1` from a checkout. It builds the
   server, copies it to `%LOCALAPPDATA%\Martlet.Mcp\server-<time>` (so a
   running server never locks the checkout's build) and adds a `martlet` server
   to GitHub Copilot CLI and app (`~\.copilot\mcp-config.json`), Claude Desktop
   (`%APPDATA%\Claude\claude_desktop_config.json`) and VS Code
   (`%APPDATA%\Code\User\mcp.json`), for each one installed. `-Client copilot`
   chooses one, `-ReadOnly` leaves changes off, `-AllowUiEffects` adds
   `--allow-ui-effects`, and `-Unregister` removes the entry. Each run first
   copies the file to a new `<file>.martlet-backup-<date>-<time>` and prints
   its path; the first backup and the four newest are kept. A file with
   comments (VS Code's `mcp.json` may have them) is left as it is unless you
   add `-Force`, because the rewrite removes the comments; the backup keeps
   them.
2. Restart the assistant.
3. Ask it, for example: "Make a Martlet character called Ava, a cheerful
   astronomer who speaks briefly, with the built-in look." The assistant starts
   with `martlet_guide`.

To configure a client by hand, give it the full path of `Martlet.Mcp.exe`:

```json
{
  "mcpServers": {
    "martlet": { "command": "C:\\path\\to\\Martlet.Mcp.exe", "args": ["--allow-changes"] }
  }
}
```

### Flags

| Flag | What it does |
| --- | --- |
| `--allow-changes` | Lets `character_create`, `character_update`, `character_use`, `character_delete` and `settings_set` save, and `memory_forget` delete (its `preview: true` runs without the flag). It also lets the tools that write save or write: `logs_export`, `character_picture` with `outputPath`, `kept_motions_check` with a `dataDirectory`, `character_eyes` with `save` or `forget`, `character_touch_zones` and `character_reaction_changes` with `save`, and `discord_companion_check` with `requestFrom`. Without it they refuse and say how to turn it on. The FIXTURE saves also refuse Martlet's own data folder (`%LOCALAPPDATA%\Martlet`), and an output file is never on a network path or over an existing file. |
| `--allow-ui-effects` | Lets `ui_*` tools press buttons, type and toggle (see [Local MCP control](#local-mcp-control-windows)). Without it they only navigate and read. |
| `--all-tools` | Lists every tool. Without it, `tools/list` has only the short list of the tools an assistant needs most (much smaller, and under the 128-tool limit of some clients); every tool still runs by name. `martlet_guide` says how many tools there are and how many are listed. |

### What an assistant sees

- **Instructions** at `initialize`: what Martlet is, where to start, the tool
  groups and which flags are on. `serverInfo` has the title `Martlet` and the
  Martlet version.
- **The short list**: `martlet_guide`, `martlet_call`, the character and
  settings tools, `doctor_status`, `doctor_list`, `doctor_run`, `logs_tail` and
  the `ui_*` tools that drive the window. Each has a `title` and MCP behavior
  hints (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`).
- **`martlet_guide`** (start here): what Martlet is, the server's state
  (version, flags, the data folder, whether the app runs now), the common tasks
  step by step (`topic`: `overview`, `characters`, `settings`, `app`,
  `diagnose`), every tool by area (`topic: tools`), a search over names and
  descriptions (`search`) and one tool's full description and input schema
  (`tool`).
- **`martlet_call`**: runs any tool by name with its `arguments`, so the
  diagnostic and self-test tools that are not listed stay reachable. It returns
  that tool's result as is.

### Characters

A character is a personality (a persona: its name and the instructions that
say who it is and how it talks) and its character profile (Companion ›
Profiles), which adds a look (the built-in character or one of the shared
character models) and a voice (one of the speaking voices). The tools change
them with the same settings code that Personality and Profiles use. They
return names and, when asked, the personality text, because they edit them.

- `characters_list` (`includeText`): every personality with its profile, which
  one Martlet is now, the looks and voices a profile can use, and the limits
  (1,000 personalities, 1,000 profiles, 100,000 characters each and 1,000,000
  together).
- `character_create`: `name` and `personality`, or `cardPath` (an absolute path
  to a SillyTavern, Chub or other Tavern character card: PNG, JSON or CHARX,
  read like Personality's import; its keyword lorebook entries are kept in a
  lorebook for the new personality). `look` (`builtin` or a look's name or ID)
  and `voice` (a voice's name or ID) go on its profile. `profile: false` makes
  only the personality. Martlet becomes the new character unless `use` is
  false.
- `character_update` (`character`: a name, ID or profile key): `name`,
  `personality`, `look` and `voice` (`keep` keeps whatever Martlet uses now). A
  character without a profile gets one when it gets a look or a voice.
- `character_use`: Martlet talks with that personality from its next reply.
- `character_delete`: removes the personality and its profiles, or only the
  profile with `profileOnly`. Martlet keeps at least one personality.
- `character_find_looks` (`query`, `count`, `sources`: `vroidhub`, `osa`): looks
  (VRM models) online for a character you name, as Find a character finds them
  (see [Find a character online](../AVATARS.md#find-a-character-online)): the
  linked VRoid Hub account's own and hearted models, VRoid Hub models whose
  creators let anyone use them, and Open Source Avatars' free models. Each has its
  creator, source, license, every condition of use, `usable`, a picture and page
  address, and its `look` key. Give that key as `look` to `character_create` or
  `character_update`: `vroidhub:<id>` is checked on VRoid Hub and its credit kept
  for the app (the app shows it from VRoid Hub, never saved); `osa:<project>/<avatar>`
  is downloaded and joins the owner's characters. Only the query goes to VRoid Hub
  and GitHub.
- `character_find_on_web` (`query`, `models`, `voices`): the backup finder. Web
  pages (from DuckDuckGo) that may have a model of the character or recordings of
  its voice, and searches on the sites that have them, with the next steps. Martlet
  downloads nothing from them: the owner does, and adds what they may use in the
  app (Add a character, Add a voice).
- `vroid_hub_status` (with `martlet_call`): whether this build can link VRoid Hub,
  whether an account is linked for the account whose settings the data folder
  holds (its VRoid Hub user name, never a token), the VRoid Hub looks this PC used
  with their conditions of use, and whether the character shown is one.

A profile's look and voice change on this PC when the profile is used in the
app (they need the character window and the voice hosts). So
`character_create`, `character_update` and `character_use` return `followUp`
with the steps: Companion › Profiles › Use, or `ui_connect`, `ui_click`
`NavCompanion`, `CompanionTab-Profiles` and `CharacterProfileUse-<key>` (with
`--allow-ui-effects`). New looks (Live2D or VRM models) and new voices (from a
recording) are added in the app: Companion › Character and Companion › Voice.
Companion › Character › **Find a character...** (`CharacterFinderOpen`, a safe
click) opens Find a character (`CharacterFinderWindow`): its status lines
(`CharacterFinderStatus`, `CharacterFinderVRoidHub`,
`CharacterFinderPersonalityState`, `CharacterFinderResults`,
`CharacterFinderWebModels`, `CharacterFinderWebVoices`) and each look's
`CharacterFinderAbout-<n>`, `CharacterFinderConditions-<n>` and
`CharacterFinderState-<n>` and each web page's `CharacterFinderWebAbout-<model|voice>-<n>`
are safe values (never the typed name, notes or personality). **Close**
(`CharacterFinderClose`) and **Add a voice clip you downloaded...**
(`CharacterFinderAddVoice`, which only opens Companion › Voice) are safe clicks;
Search, Write again, the web searches, Link VRoid Hub, Open, Add a model and Use
need `--allow-ui-effects`. Companion › Character's `VRoidHubState` and the Account
page's `AccountVRoidHubState` say whether VRoid Hub is linked; the Account page's
`AccountVRoidHubLink` and `AccountVRoidHubUnlink` need `--allow-ui-effects`.

### Settings

- `settings_get` (`path`, for example `generation` or
  `companion.personas[0].name`): settings.json or one part of it, its
  `revision`, and without a path the parts with what each holds and whether it
  can change here. Before the first save it shows the defaults (`first-run`).
- `settings_schema` (`path`, `depth`): the JSON schema of that part (property
  names, types and allowed values), generated from Martlet's settings types.
- `settings_set` (`path`, `value`, optional `revision`): changes `companion`,
  `generation` (Companion › Replies), `prompts` (Companion › Prompts; see
  `prompts_status` through `martlet_call` for the prompt IDs) and `memory`.
  `null` removes a value, which puts an optional value back to its default.
  The change is checked by Martlet's own settings contract and saved by its
  settings store (write lock, revision check, atomic write). A changed
  personality or memory choice gets a fresh configuration revision, as the
  pages give it. Serializer errors name the part that doesn't fit.
- `setup` (where Martlet thinks, listens and speaks), `thinking_fallback`,
  `audio`, `profile` and `schema_version` are refused: they hold destinations,
  the owner's consent and credential references, so they change only in the
  app, with the owner.

With accounts, settings.json holds the settings of the person signed in on
this PC, so changes go to that person.

### A running Martlet follows the changes

The desktop watches settings.json. About 0.6 seconds after it changes and Martlet
is not busy with Setup, it reads it again (the desktop log says `settings.json
changed; Martlet read it again.`), so Home, the open Companion page and an open
conversation (before its next reply) use the change without a restart. The
desktop's `SettingsChanges` knows where each version came from (a page's own
save, a setting taken from another computer, an account switch or the file): a
version Martlet already applied is not read again, and the log line is written
only for a change this Martlet didn't make (its MCP server or an edit by hand).

### Limits

- Windows only, for the current Windows user.
- The copy that `Register-MartletMcp.ps1` makes runs every tool except the
  developer self-tests that start `Martlet.NodeLinkCheck` or
  `Martlet.Companion` from a checkout; run those with
  `scripts\Invoke-MartletMcp.ps1`.
- The installer does not include `Martlet.Mcp` yet; build it from a checkout.

## Local MCP control (Windows)


`Martlet.Mcp` is a local stdio Model Context Protocol server. It does not listen
on a network port, start the desktop, or activate a provider, microphone or
speaker on launch. Configure an MCP client to start the Windows executable
built from `src\Martlet.Mcp` (`scripts\Register-MartletMcp.ps1` does it for
assistants, see [Use Martlet from an AI assistant](#use-martlet-from-an-ai-assistant);
for development: `src\Martlet.Mcp\bin\Release\net10.0-windows\Martlet.Mcp.exe`).
The server speaks newline-delimited JSON-RPC 2.0 on standard input/output;
stderr is for diagnostics. Tool calls run one at a time, in the order they
come, but the server keeps reading while one runs: `ping`, `tools/list` and
`initialize` answer at once, and `notifications/cancelled` with a call's
`requestId` stops that call (running or waiting); a cancelled call gets no
response. A tool that fails returns a result with `isError: true` and a short
message, and the server keeps running. Only a request line that is not JSON
gets `-32700` (parse error); an invalid request gets `-32600` and any other
server problem `-32603`. It takes the flags `--allow-changes`,
`--allow-ui-effects` and `--all-tools` in any order. The installer does not
include it yet. Build locally with:

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

## Verifying changes with Martlet MCP

Every new feature or behavior change is verified on the dev machine through
this server before merge, whenever the machine can exercise it, alongside the
targeted tests (the policy is in [AGENTS.md](../../AGENTS.md#validate-before-merge)
and the whole flow in [Validating changes](../VALIDATION.md)).
`scripts\Invoke-MartletMcp.ps1` runs this checkout's `Martlet.Mcp`, sends a list
of tool calls in order and prints one JSON array of results; it exits 1 if any
call fails or an `until` is not met.

```powershell
# Headless: Doctor against a fresh disposable data directory
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"doctor_status"}]'

# UI: launch a disposable desktop, connect, navigate and poll until it shows
.\scripts\Invoke-MartletMcp.ps1 -Build -Desktop -Calls '[
  {"name":"ui_click","arguments":{"id":"TourSkip"}},
  {"name":"ui_click","arguments":{"id":"NavSettings"}},
  {"name":"ui_snapshot","until":"AutomaticUpdateCheck"}]'
```

- Each call is `{"name", "arguments"}` plus optional `waitMs` (pause after it;
  `ui_*` effects default to 300 ms) and `until` (repeat the call for up to 20
  seconds until its result text contains that string). `-Calls` also takes a
  path to a JSON file.
- Calls whose tool takes a `martletDirectory` (the voice and listening checks)
  and give none use this checkout's Desktop build when it is built.
- Calls whose tool takes a `dataDirectory` (its input schema in `tools/list`,
  which the script reads from the server with `--all-tools`) and give none get
  the script's disposable data directory, so no tool reaches the real profile
  by default and a new tool is covered without a list in the script. `-Desktop`
  also uses that directory, so Doctor sees the desktop's settings
  and `logs_tail` its logs. The directory and the desktop are removed at the end.
- `-KeepDesktop` leaves the desktop running and prints its `-DesktopProcessId`
  and `-DataDirectory` for follow-up runs; stop it and delete the directory
  when done. `-DesktopArguments` adds launch flags after the data directory
  (for example `'--tray'` to start in the notification area).
- `-AllowUiEffects` passes `--allow-ui-effects` (disposable data and no real
  credentials only; it never authorizes spending, provider requests, credential
  handling, audio capture/playback or data disclosure).
- `-AllowChanges` passes `--allow-changes`, so `character_*` and `settings_set`
  save, `memory_forget` deletes and the tools that write (a `save`, `forget`,
  `requestFrom`, `outputPath` or `logs_export`; `kept_motions_check` with the
  disposable `dataDirectory`) write; without an explicit `dataDirectory` they
  use the disposable data directory, and `martlet_call` passes it to the tool
  it runs.
- `-Build` builds `Martlet.Mcp` (and `Martlet.Desktop` with `-Desktop`) in
  `-Configuration` (default Release) with the `dotnet` that has the SDK pinned in
  `global.json` (found the same way as `scripts\Test-Martlet.ps1`, including the
  developer profile's `dotnet`).

Check the outcome the change should produce (status values, control states,
Doctor probes), not only that calls succeeded. What the machine cannot exercise
(locked desktop, missing hardware, credentials, paid services, another OS) is
reported as NOT RUN with the reason.

## Extending the server

New features extend the server in the same change, so that MCP can reach and
observe them:

- **Controls:** give every new interactive control and status field a stable,
  unique `AutomationProperties.AutomationId` (`ui_snapshot` lists controls by
  ID; duplicates fail as ambiguous).
- **Passive clicks:** add navigation, open/close, refresh and expand controls
  that start no work to `SafeClicks` in
  `src\Martlet.Mcp.Protocol\AutomationPolicy.cs` (or `SafeClickPrefixes` for a
  family of generated IDs such as `CompanionTab-` and `Node-`). A passive click
  may read this PC, ask the local network a discovery question that carries
  none of your data, and stop what Martlet does at this moment (a reply, a
  song). Anything that sends, records, plays, spends, writes files, handles
  credentials, contacts another computer by its address (a host, a ComfyUI
  server, a cloud provider), cancels a background job or pauses Martlet stays
  behind `--allow-ui-effects`. `AutomationPolicyTests` checks the effect texts
  for these words.
- **Status:** add read-only, non-secret status fields to `SafeValues` (or
  `SafeValuePrefixes`) in the same file so snapshots return their text (a value
  pattern's value, a text block's text, or a status button's accessible name).
  Never expose editable fields, credentials, personal data or file paths.
  `ui_snapshot` also replaces every e-mail address with `[email]` and every
  file path with `[path]` in the text it returns (`AutomationPolicy.Redact`).
- **One line for each ID:** write each entry as
  `new("<ID>", "<page>", "<what it does or shows>")`, for example
  `new("CheckInsOpenPool", "Companion › Check-ins", "Only opens Companion › Thinking pool.")`.
  Say why it is safe ("saves nothing", "never the key"). Name controls that you
  leave out on purpose in a comment line next to it
  (`// Needs --allow-ui-effects: ...` or `// Never returned: ...`).
- **Headless capabilities:** add a `Tool(...)` registration to the file of the
  tool's `martlet_guide` area, `src\Martlet.Mcp.Protocol\Tools\<Area>Tools.cs`
  (for example `VoiceTools.cs` or `ThinkingTools.cs`; `McpServer.ToolGroups`
  lists them). A registration is the tool's name, description, input schema
  (strict, bounded arguments, ID-based results), handler and, when it needs
  one, its `ToolPermission` (`Changes` makes the server refuse it without
  `--allow-changes`). Put code that only that tool uses in the same file. Or
  add a Doctor probe that `doctor_run` reaches.
  Keep tools local, read-only by default and free of network, audio and
  credential effects unless gated like `--allow-ui-effects` (or, for tools that
  save the owner's settings, `--allow-changes`). Begin each description with one
  sentence that says what the tool does: `martlet_guide` shows it as the tool's
  summary and searches the description.
- **Tools for assistants:** a tool that an assistant needs to use or configure
  Martlet also goes in `Listed` in
  `src\Martlet.Mcp.Protocol\McpServer.Discovery.cs` (with a title and behavior
  hints), and in a recipe of `martlet_guide` when it starts a common task. Keep
  the short list small: other tools run through `martlet_call`.
- **Docs:** update the page of the tool's `martlet_guide` area in `docs/mcp/`
  (the [index](../MCP.md#pages) lists them) with the new tools, IDs and status
  fields. Write each tool's name as `name`: `McpDocsAgreementTests`
  (Martlet.Desktop.Tests) fails when a tool is not named on any of these pages,
  or when a page names a tool (`ui_*`, `*_status`, `*_check`, `*_selftest`...)
  that the server does not have.
