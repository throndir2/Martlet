# Martlet

**Status: internal explicit API conversation integration; not a qualified release.**
Desktop now has a [talk window](docs/CONVERSATION.md) that is just the
conversation: a chat history (what you typed or said and Martlet's replies) and
a message box. It sits beside the rest of Martlet instead of blocking it. By
default Martlet listens from when you press **Start listening** until **Stop
listening** (once a microphone is tested), or push-to-talk; it streams the reply and speaks it with the chosen
voice. How it listens, speaks and sees is chosen in Companion. Optional local
**Voice ID** recognizes your enrolled voice and ignores other people before
anything is uploaded. **People** recognition (part of Martlet, on by default) tells everyone at the
microphone apart, learns the names they go by and keeps that list the same on
all your computers; **Parakeet** listens on this PC without Docker (see
[VOICES](docs/VOICES.md)). Each message or utterance is its own bounded action; no
credentials or audio are accessed on launch; network remains idle except for
update checks (on by default; can be turned off), host updates, keeping Martlet the same on all your computers ([CLUSTER](docs/CLUSTER.md), through paired hosts only), or [Martlet network](docs/NETWORK.md) sync with paired hosts. Text-only never requests TTS or opens output.
Actual account/device/first-conversation qualification remains **NOT RUN**.

The desktop starts with a short welcome tour, then a Home page that lists what
needs attention (what isn't set up, what stopped working and how to fix each)
with a health tile per part and **Start talking**, a **Devices** map of
every computer and cloud service with its hardware and roles, where **Who does
what** hands jobs such as Audio2Face lip-sync to any paired host on the spot and
installs or removes host roles remotely, a host dashboard
for PCs that lend their GPU, and Companion and Settings pages for everything
else. See the [desktop UI design](docs/UI_DESIGN.md).

**One Martlet on all your computers.** Martlet is one app that lives on every
computer you install it on: the same companion with the same settings, API
keys, memories, people, voices, characters and Home Assistant everywhere, kept
in sync through your paired hosts ([CLUSTER](docs/CLUSTER.md), on by default).
Each computer is a companion PC (where you talk) or a host PC (lending its
GPU); adding a host to your [Martlet network](docs/NETWORK.md) (pair it once,
for example a Linux PC set up over SSH, and every PC pairs with it by itself)
adds what it can do to the whole app: jobs move between hosts on the spot and
fail over to another host that runs the same engine. Only what belongs to a
computer itself stays with it: its devices, screens, role, startup choices and
installed engines.

Resumable configuration and explicit Windows credential actions are available
through **Setup / resume**; see [SETUP](docs/SETUP.md). Saved API routes are not
verified connections or spending permission. **Audio setup (local only)** offers explicit microphone/
output selection and separately confirmed bounded local capture/tone tests.
Opening it does not enumerate or open devices. Historical local checkpoints
are not device readiness. Always listening runs from **Start listening** on Home (or the notification-area
menu) until **Stop listening**, and vision (when Companion turns it on) runs from
**Start watching** until **Stop watching**; each has its own button on Home, in the
notification-area menu and in the talk window, works without the other and runs
with or without the talk window open. Settings ›
*Startup and closing* can show the character and start listening (and watching) as Martlet
starts, including with Windows. Acoustic wake words,
automatic name/group listening and supported end-user deployment
are not available. A PC microphone does not automatically
capture remote participants.

The intended first experience is a Windows installer, microphone and speaker
setup, an explicitly selected AI provider, and a working voice conversation
with built-in troubleshooting. Dedicated AI hardware is not required for the
planned API-backed route. API use may cost money and sends the selected data
to the selected provider.

Later milestones cover Ubuntu self-hosting, two-host GPU deployments, opt-in
screen understanding and memory. [Live2D and VRM avatar development](docs/AVATARS.md)
is authorized in parallel now, with Audio2Face first/preferred and avatars OFF
by default. The internal Desktop route renders either model in a transparent,
always-on-top character overlay with no buttons of its own: drag the character
to reposition it while she talks, and use **Hide character** or **Reset
character position** in the main window. **Speech bubbles** beside the
character (on by default whenever the character is showing) and optional
**subtitles** at the bottom of the active screen (over full-screen games, the
same way the character is) show each sentence as she says it; change either in
Companion › Character. Explicit model inspection and
generated-speech activation still require the local prerequisites in the
[Desktop avatar guide](src/Martlet.Avatar.Hosting/README.md). This is not a
qualified end-user avatar release; voice reliability and model/device
qualification remain separate requirements.

**Watch my screen or a camera** lets Martlet comment now and then on your
screen, a webcam or capture card, a phone camera (phone-as-webcam apps or an
IP camera address) or other video sources. See
[screen and camera commentary](docs/SCREEN_COMMENTARY.md).

**Tools (MCP)** let Martlet use MCP servers on this PC while you talk: your files, a
browser, a calendar and anything else with an MCP server. Add servers on
**Companion > Tools**: browse and search the GitHub or official MCP Registry and
install one with a click, or edit mcp.json (the standard `mcpServers` format); the
talk window asks before each tool call unless you always allow it. The same page
has Martlet's own **Terminal** (off by default): turn it on and Martlet can run
PowerShell or Command Prompt commands when you ask, hidden and never as
administrator, asking before each one unless you change that. See [MCP](docs/MCP.md).

**Thinking longer** (Companion > Deep thinking, on by default): replies answer right
away, and when a task really needs thought (song lyrics, a story, a plan, tricky
math or code) Martlet says it'll think it over, works it out in the background
with Thinking steps on while you keep talking, then brings it up when it's done.
**Deep thinking** always does that thinking in parallel, so it needs a model of
its own: another of your computers, a cloud provider, a second model in Ollama on
this PC (when both fit on the graphics card), or Thinking's own model when its
provider answers several requests at once. Without one (say, a single PC whose
Thinking model is local) Martlet doesn't offer to think things over, and *Where
it thinks* › *Off* turns it off.
See [Thinking longer](docs/CONVERSATION.md#thinking-longer-and-background-work).

**Voices (F5)**: add your own voice recordings on **Companion > Voice >
Voices** and switch between them in one click. F5 copies a voice from a short
recording with its transcript; nothing is trained. Martlet keeps its own copy of
each recording on this PC and sends it with each reply only to the computer that
speaks. The same voices work with [Chatterbox Turbo](docs/CHATTERBOX_VOICE.md), the
default engine, which can also laugh, sigh and change tone, with
[XTTS-v2](docs/XTTS_VOICE.md), which starts speaking before a sentence is
finished, and with [GPT-SoVITS](docs/GPT_SOVITS_VOICE.md), good for anime-style
voices from 3-10 second recordings, and with [Dia](docs/DIA_VOICE.md), which can
laugh, sigh, cough and gasp (English only; Companion > Voice > Voice engine). See
[Voices](docs/SETUP.md#voices-f5) and the [Voice Studio plan](docs/VOICE_STUDIO.md)
for other engines. With the [Singing](docs/SINGING.md) role, Martlet also writes
songs and sings them in a voice from the same list (Companion > Voice > Singing).

The [planned installation flow](docs/INSTALLATION_SUPPORT.md#feature-first-multi-machine-setup)
coordinates optional features and mixed API/self-hosted roles across machines.
It targets guided Ubuntu Desktop/Server hosting with Docker Engine/Compose,
advanced user-managed containers, and separately qualified native Windows
Ollama / Windows Docker-WSL hosting lanes. Multiple roles may share a machine;
Docker is not required for the Windows client. These remain design targets,
not available host installers or enabled application routes.

[P03a/P03b/P03c consented local memory](docs/MEMORY.md) provides OFF-by-default
Desktop settings and explicit save/inspect/edit/delete/export over one local
fact store. Optional next-turn retrieval sends only bounded matching facts
with provenance to the already authorized LLM; it never ingests transcripts.
There is no remote memory, embedding, vector database or automatic backup.

| Document | Purpose |
| --- | --- |
| [Coding-agent instructions](AGENTS.md) | Autonomous autopilot-style work, task branches, validation before merge (targeted tests and MCP verification), and normal merge into `main`; explicit holds and safety boundaries remain binding |
| [Validating changes](docs/VALIDATION.md) | The validation flow for every change: the change-aware parallel test runner, validation hosts and the developer profile, known failures, MCP verification and writing tests |
| [Contributing](CONTRIBUTING.md) | Setup, branches, validation and pull requests for developers joining the project |
| [Development plan](DEVELOPMENT_PLAN.md) | Scope, proposed decisions, priorities, risks, and reading order |
| [Desktop UI design](docs/UI_DESIGN.md) | Per-stage design: welcome tour, companion home and setup checklist, host dashboard, Devices hardware map, add-a-computer wizard, chat-first conversation and motion system |
| [User stories and target flows](docs/USER_STORIES.md) | UX specification: audit of today's paths, flow rules, target navigation, interaction patterns, and every user story with all entry points, screens, click counts, edge cases and acceptance criteria (first run, joining a network, Thinking, Voice, Listening, Character, permissions, talking, devices, maintenance) |
| [Companion requirements](docs/COMPANION_REQUIREMENTS.md) | Planned persona editing, F5 reference voices, LLM/VLM selection, listen-first behavior, speech interruption and response-style controls; not current features |
| [Avatar guide and compatibility](docs/AVATARS.md) | Accepted Live2D/VRM direction, Audio2Face-first analysis, per-model mappings, working/degraded/blocked permutations, parallel plan and remaining qualification |
| [Architecture and provider contracts](docs/ARCHITECTURE.md) | Components, trust boundaries, conversation policy, streaming, and failure behavior |
| [Installation and support design](docs/INSTALLATION_SUPPORT.md) | First run, host setup, lifecycle, doctor, and troubleshooting matrix |
| [Recommended setups](docs/RECOMMENDED_SETUPS.md) | What must run on your PC versus an API or host, offloading the LLM to OpenRouter/NVIDIA Build, fastest-response layouts, GPU/VRAM priorities, and layouts from one machine to an unlimited-budget setup |
| [Voice latency](docs/VOICE_LATENCY.md) | How long from when you stop talking to Martlet's voice, step by step (the desktop log's reply latency line and MCP `latency_report`), where the time goes today, the Chatterbox speed-ups, how other voice products get fast, a budget under 800 ms and the next steps, keeping voice cloning and sighs |
| [Platforms](docs/PLATFORMS.md) | What Windows, macOS, iPhone/iPad, Android and Linux can each do as the device you talk to and as a host, what old hardware is good for, the guarantees that keep impossible choices off the menus and show when a job stops working, and the edge cases |
| [iOS and iPadOS plan](docs/IOS.md) | What it takes to run Martlet on iPhone/iPad as a companion while you game there and as a host lending on-device speech, voices and Apple Intelligence to the desktop: feature matrix, platform limits, gateway subset and slices IO01-IO11 (plan only) |
| [macOS plan](docs/MACOS.md) | Martlet on Apple-silicon and Intel Macs as a companion with a floating character and as a host (Ollama/whisper.cpp on the GPU, F5 on MLX, Apple speech, voices and Apple Intelligence); unsigned builds and slices MA01-MA10 (plan only) |
| [Android plan](docs/ANDROID.md) | Martlet on Android phones and tablets as a companion while gaming, as a host that keeps serving with the screen off, and old phones as satellite microphones; signed-APK sideloading and slices AN01-AN11 (plan only) |
| [Smart home and cameras](docs/SMART_HOME.md) | Home Assistant connection and control on your own turns (built-in Assist with every Thinking model, plus Home Assistant's MCP tools for free-form requests on tool-capable models; locks/doors/garages/alarms blocked or click-confirmed), HA camera snapshots in Watch, plus the Matter/Thread/Zigbee/Z-Wave/camera landscape, other integrations worth knowing and the remaining slices |
| [Deep research plan](docs/DEEP_RESEARCH.md) | Web search and long background research jobs on Deep thinking: the plan, search, read, note and reflect loop, search backends (self-hosted SearXNG by default; Ollama, Tavily, Brave and Exa keys; paid provider-native research), safe page fetching, privacy, budgets and slices R0-R5 |
| [Shared who does what and failover](docs/CLUSTER.md) | The cluster plan every host and desktop keeps, how copies merge, the 15-second sync, per-job failover between hosts and its edge cases |
| [Your Martlet network](docs/NETWORK.md) | Pair a host once for all your computers: the signed roster of your desktops and hosts, joining with a check number, pairing by itself, removing a computer, the trust model and its limits |
| [API for apps and scripts](docs/API.md) | API keys for software outside your network (Home Assistant, scripts, future integrations): making and revoking keys, scopes (read, voice, perception, manage), every endpoint a key can call, pinning the host key, how keys reach every host, and why API keys |
| [Prerequisites](docs/PREREQUISITES.md) | Every runtime prerequisite by feature and machine: what is bundled, what the installer and **Martlet prerequisites** tool install on request (WebView2, microphone access, Windows speech, Ollama, WSL 2 + Docker Desktop), what hosts install, and what you supply |
| [Delivery and release plan](docs/DELIVERY.md) | PR-sized backlog, dependencies, acceptance criteria, release gates, and traceability |
| [Research and provenance](docs/RESEARCH.md) | Dated primary sources, verified constraints, and unresolved integration questions |
| [MCP: tools while you talk, and local MCP control](docs/MCP.md) | Martlet as an MCP client: MCP servers on this PC (stdio or streamable HTTP, standard `mcpServers` mcp.json) give replies tools, with per-call confirmations in the talk window and a tool log on Companion > Tools; the built-in Terminal (off by default; PowerShell or Command Prompt, asks before each command); plus Martlet's own stdio MCP server for headless diagnostics and desktop UI Automation |
| [Implemented foundation and decisions](docs/FOUNDATION.md) | Accepted/deferred decisions, exact APIs/bounds, current behavior and next ownership |
| [Resumable setup and local audio](docs/SETUP.md) | V02a configuration/vault actions and V02b explicit local device tests, historical checkpoints, strict migration and remaining live gates |
| [Explicit API conversation](docs/CONVERSATION.md) | V04b typed/PTT path, hands-free voice activity, local Voice ID, exact supported models and bounds, fresh authorization, Stop/cleanup, troubleshooting and separately authorized live-trial checklist |
| [Recognizing people by voice, and Parakeet](docs/VOICES.md) | Companion › People: AudioTranscriber's sherpa-onnx speaker recognition, names learned from conversation, editing/merging/forgetting voices, sharing the list through paired hosts, and Parakeet speech-to-text on this PC |
| [Voice Studio research and setup](docs/VOICE_STUDIO.md) | Five-engine implementation research, guided setup, audio imports, A/B previews, training and staged acceptance |
| [Memory](docs/MEMORY.md) | ON-by-default local memory: automatic recall each turn, remembering lasting facts from conversations, Desktop fact management, privacy/deletion/export and remaining qualification gates |
| [Lorebooks](docs/LOREBOOKS.md) | SillyTavern-style World Info: keyword-triggered lore added to replies, persona scope, budget and recursion, the editor and its local test, SillyTavern/character card import and export |
| [Creations](docs/CREATIONS.md) | Everything Martlet makes (songs, later more): kinds and their handlers, FLAC assets stored by SHA-256, limits and cleanup, sharing with every computer through the hosts, `list_creations`/`perform_creation`, and the Creations page (no Play button: Martlet performs them itself) |

The broader plan documents remain future specifications except for the current
implementation/acceptance ledger in [DELIVERY](docs/DELIVERY.md) and the
[diagnostics contract](docs/DIAGNOSTICS.md).
Martlet's original source, artwork, and documentation are
[all rights reserved](LICENSE), even if the repository becomes public. Only
unmodified official Release binaries receive a narrow personal,
noncommercial download/install/use grant; no redistribution or source reuse is
allowed. Third-party components retain their own terms and must be cleared
for distribution separately.

**App updates** come from Martlet's public GitHub Releases (Settings > App
updates). Checks are **ON by default**: Martlet checks at launch and then every
15 minutes to 24 hours (default: every hour) while it runs; turning off
*Check GitHub for new versions automatically* stops all automatic requests,
and **Check for updates now** makes an explicit request instead. When a check
finds a new version, Martlet asks once per version whether to update now.
Only normal (non-draft, non-prerelease)
releases with the exact `Martlet-<version>-win-x64.exe` asset are offered.
**Update now** (or **Install**) downloads it (at most 512 MiB) into `updates\` in the local data
directory, verifies the exact bytes against GitHub's SHA-256 asset digest,
closes Martlet, runs the installer with its progress window (`/SILENT`, no
optional prerequisite tasks) and starts Martlet again; the next launch reports
the result. With *Download and install updates automatically* this happens by
itself as soon as the update is downloaded, whether or not you are at the PC,
Martlet's window is in front or the character is showing. It waits only for a
reply or something you are saying, a question waiting for your answer, or work
that exiting would cut short (a setup task, a host update, a command from
another computer, backup and restore, a troubleshooting report, a download),
and Status in **Settings › App updates** says which. Martlet restarts minimized
(or in the notification area when it was there) and shows the character and
listens again when they were on as it closed. An automatic install, and one
another of your computers asks for (`martlet.update`, see
[CLUSTER](docs/CLUSTER.md#commands-between-your-computers)), shows no installer
window at all (`/VERYSILENT`): Martlet downloads, closes, installs and restarts
by itself. Every step still goes to the log: the helper's steps
(`updates\update.log`) and, after a failed install, the end of the installer's
own `updates\install.log` are copied into Martlet's log (the **Diagnostics**
page) when it starts again.
Choices live in `update-checks.txt` and `updates.json`, separately from profile
settings and configuration backup; a choice saved while checks were opt-in
resets to on, and unreadable preferences fall back to checks ON (installs and
host updates OFF) with a visible error. Releases are normal GitHub releases; code signing is not a
requirement for this personal project, so the installer is unsigned. The
digest detects a damaged download; it does not prove who published it. Do not
run an internal build as an update.

**Closing to the notification area.** Closing Martlet's window keeps it
running in the notification area by the clock (on by default), so the
character, sync, updates and commands from your other computers carry on.
Click the icon to open Martlet; right-click it to talk to Martlet (or show the
talk window), pause or resume Martlet (a reply, listening and vision stop until
you resume), end the conversation, show or hide the character, change *Keep
running when closed* and *Start with Windows*, or **Exit Martlet**, which closes
it completely (as does Exit Martlet in Settings). Settings > *Startup and
closing* holds the same choices plus *Start in the notification area* for a
start at sign-in (the per-user Run entry `Martlet`, off by default; Windows'
own Startup apps switch is respected, and the uninstaller removes the entry).
Starting Martlet again while it runs shows the running window instead of a
second copy (per data folder). Choices live in `background.json`.

**Exiting.** If an exit would cut work short (an update download, an install
in a run window, backup and restore, a troubleshooting report, a download or a
command from another computer), Martlet lists what it is still doing and asks:
*Exit anyway* interrupts it, *Keep Martlet open* doesn't. While it closes, its
window says what it is finishing (the notification-area tooltip too); if that
takes more than a few seconds the window shows with **Exit now**, which says
what exiting without waiting interrupts and asks once more. A part that fails
to close is logged and skipped, so Martlet never stays stuck closing. An
unattended update's exit (automatic, or asked for by another computer) never
asks or shows the window: it starts only when nothing would be cut short (and
otherwise waits for it), and a slow close stays out of sight.

Paired **Martlet hosts** follow the desktop's version: the gateway reports its
release, the Devices map shows *Update available* for older hosts, and clicking
it (or **Update host**) rebuilds that host's gateway from this version in a
Martlet run window (`martlet-host update`; identity, pairings and roles stay).
*Keep my Martlet hosts on this PC's version* does the same in the background
every interval for hosts Martlet reaches over an SSH key or this PC's Docker
Desktop; a host that needs a password, sudo or an approval keeps the Update
host route. This PC's **own host service** follows its version whatever that
setting says: Martlet installs its own update and is back moments later (the
update helper starts the installer within about a second of Martlet's exit),
then updates its host service in the background while you use it, and checks
again every minute until it runs the same version. The
[Windows packaging guide](packaging/windows/README.md) distinguishes local
internal builds from the manually dispatched release workflow, which has no
push, PR, tag or scheduled trigger.

## Local-only validation policy

**Current owner policy, 2026-10-03: every change is validated locally before it
merges; never remote validation.** This supersedes the 2026-09-26
prototype-speed policy, which made tests optional, and earlier plans for hosted
CI. Do not create, enable, dispatch or retry remote test/validation pipelines,
including GitHub Actions with self-hosted runners. Do not restore Actions
billing, increase spending limits or use another hosted service to obtain
validation evidence.

Before merge, every change except documentation passes its targeted tests (the
tests it adds or changes and the suite directly covering the changed code, never
every affected suite), run by `scripts\Test-Martlet.ps1 -Project` on the
developer's PC and on the validation hosts they have set up (their local
developer profile, `~\.martlet-dev\validation.json`). Every feature or behavior
change is also verified working through Martlet's own MCP server, which is
extended in the same change so it can reach and observe the feature. A targeted
test the change breaks blocks the merge; tests that already fail on `main` are
listed in `tests\known-failures.txt` until they are fixed. The flow, the runner and the
hosts are described in [Validating changes](docs/VALIDATION.md). `CI=true`
remains a local MSBuild setting for locked restore and deterministic build
metadata; it does not require a remote runner.

The only permitted remote workflow is a **minimal build/package/release**:
restore necessary build dependencies, compile/package the deliverable and
publish it as a GitHub release. Agents may create, dispatch and publish these
releases automatically without owner approval. No tests, lint, smoke,
qualification, reproducibility checks, matrices or disguised validation belong
in that workflow. Use only manual dispatch or tag/release triggers; never add
PR/push/scheduled automation. Releases are normal GitHub releases; the
unsigned installer never suppresses Windows protection warnings.

Before any push or PR creation/update, inspect the applicable workflow events,
refs and resulting tree, including older branches that could restore deleted
workflows. Establish a publication path that starts no remote validation.
Report impossible required checks instead of bypassing/changing protections,
inventing successful statuses or relying on skip markers.

Historical failed or blocked hosted results remain historical, not local passes.
Real Windows/Linux, native-device, model/GPU and clean-machine qualifications
still require actual execution before being claimed. What an environment cannot
run is reported as **NOT RUN**, never as passed.
## Developer quick start

For **development**, use Windows with .NET SDK **10.0.401** (the exact .NET 10
LTS SDK in `global.json`) and PowerShell 7 (for the test runner and smoke
scripts). Desktop/solution builds also require Node.js and npm to build the real bundled
avatar shell (locally exercised with Node **20.11.1**, npm **10.2.4** and locked
esbuild **0.25.12**). Worker tests use Python 3.12 (`py -3.12`), and Docker
Desktop in Linux container mode runs the Linux-only tests on a Windows PC.
Initial NuGet/npm restores need internet; no provider keys, microphone, GPU,
Docker, Python, administrator rights or model downloads are needed for building.
End users need no Node/npm/dev server: the shell is bundled.
Actual avatar activation has separate user-owned runtime/model prerequisites;
see the [Desktop avatar guide](src/Martlet.Avatar.Hosting/README.md).

```powershell
npm ci --prefix src\Martlet.Avatar.Vrm --no-audit --no-fund
dotnet restore Martlet.slnx --locked-mode
dotnet build Martlet.slnx --no-restore -c Release "-p:NodeExecutable=$((Get-Command node).Source)"
.\scripts\Test-Martlet.ps1 -InitProfile   # once: your local developer profile and validation hosts
.\scripts\Test-Martlet.ps1 -List          # what your change touches, without running anything
.\scripts\Test-Martlet.ps1 -Project Martlet.Core.Tests -Filter 'FullyQualifiedName~Settings'   # targeted tests
.\scripts\Test-Martlet.ps1 -All           # every suite, only when explicitly wanted
.\scripts\Smoke-Doctor.ps1
.\scripts\Smoke-Desktop.ps1
```

`Martlet.slnx` lists the main projects for the IDE; `Test-Martlet.ps1` finds every
test project under `tests\` itself (see [Validating changes](docs/VALIDATION.md)).
The desktop smoke needs an interactive Windows desktop and exits the app after
reading its accessible status. Both smoke scripts use unique temporary data
paths, never the real user profile.

`NodeExecutable` defaults to `node`; the explicit property also works when a
long inherited Windows PATH is truncated by nested command execution. It does
not modify machine settings or skip the browser build. Explicit RID restores
use project-local generated `obj\runtime-locks` documents, not ordinary source
locks. Those development locks are not release reproducibility evidence;
reviewed packaging supplies separate authoritative committed lock paths.

Launch with an explicit disposable data location while developing:

```powershell
$data = Join-Path $env:TEMP ("Martlet.Dev." + [guid]::NewGuid().ToString('N'))
dotnet run --project src\Martlet.Desktop --no-build -c Release -- --data-directory $data
dotnet run --project src\Martlet.Doctor -f net10.0-windows --no-build -c Release -- status --json --data-directory $data
```

### Appearance

The desktop's **Your palette** selector switches between **Pink light** (blush,
cream and berry) and **Rose dark** (deep plum and soft rose). Rounded controls,
matching form fields, confirmation prompts and the companion home screen share the palette across
conversation, setup, companion, voice library, avatar, memory, audio, troubleshooting and recovery windows.
The transparent avatar overlay keeps its canvas clear while its move/close controls
follow the current palette and Windows high contrast.
The same styles appear when a window is opened independently by local UI tests.
Windows high contrast overrides the decorative colors; Windows file and folder
pickers retain their system appearance.

Pink light is the first-launch default. Changing the selector saves only
`appearance.txt` in the selected data directory, separately from profile settings,
credentials and consent. It is not part of configuration backup/restore. Launch
reads this preference without creating files; inaccessible or malformed preferences
are reported on the home screen. An unsavable choice still applies for the current
session. Appearance changes never start a conversation, network or audio action.

The original **Martlet bird** icon matches both palettes: a cream bird with rosy
cheeks and a berry heart on a pink badge. It is embedded in the desktop executable,
window/taskbar icons and internal installer; the Start menu shortcut uses the
desktop executable's icon. `src\Martlet.Desktop\Assets\Martlet.svg` is the editable,
favicon-ready vector source, alongside a transparent 256 px PNG and a multi-size
ICO (16, 20, 24, 32, 40, 48, 64, 128 and 256 px). There is no web frontend to wire
a browser favicon into. Regenerate the PNG/ICO locally after artwork changes with
`.\scripts\Generate-AppIcon.ps1` on Windows with PowerShell 7; no downloads or
third-party image tooling are needed.

The same bird is the in-app **Martlet mascot**: the navigation rail logo, the Home
hero, the welcome tour, and the Talk header and reply avatar draw it as a round
badge from the `MascotImage` vector in `src\Martlet.Desktop\Themes\Controls.xaml`.
Copy bird artwork changes from the SVG into that resource too.

Doctor `status` currently exits **2 (incomplete)** on first run or a valid
unconfigured profile, not success. Exit 3 means invalid invocation/settings;
exit 1 is reserved for reported probe failures; exit 0 means requested required
checks passed or help/version completed. `--help` lists implemented commands.
`self-test` is not implemented and exits 3 as an invalid invocation.

Without `--data-directory`, both programs read
`%LocalAppData%\Martlet\settings.json`. Launch never creates a profile or starts
capture/networking. The desktop's explicit **Create unconfigured local profile**
action saves settings only. Malformed, newer or inaccessible files are reported
with a remedy and are not reset to defaults or overwritten.

Core/Diagnostics and test projects target `net10.0` without WPF.
Doctor has portable `net10.0` (text only) and `net10.0-windows` targets; Audio
has a portable sink and a Windows WASAPI adapter. No device is needed for text
or diagnostics.
For a UI-independent test build, run
`dotnet test tests\Martlet.Core.Tests -c Release` or
`dotnet test tests\Martlet.Doctor.Tests -c Release`.
The full solution additionally builds the Windows-only WPF project and runs
the existing audio and integration suites, including Providers,
Conversation, Participation and Desktop's production-path in-process HTTP tests.
Dedicated direct-project provider/runtime/policy commands remain available for
targeted local validation; no hosted validation workflows are retained here.

These SDK commands are **not the intended end-user installation experience**.
The [internal packaging scripts](packaging/windows/README.md) now build complete
self-contained Desktop/Doctor payloads and an unsigned per-user Inno installer.
They include actual runtime/audio dependencies and notices, but do not install
anything as part of validation. Internal artifacts
are not a supported download or an instruction to bypass Windows protection.
The V07c packaging foundation adds required offline unsigned provenance and a
CycloneDX 1.6 SBOM tied to the actual payload and resolved dependency graph;
these checksums and observations do not establish publisher trust or license
clearance.
A supported release must ultimately need no Git, SDK or separately installed .NET.
Clean Windows, real audio/provider, Ubuntu/GPU and signing gates remain unpassed.
