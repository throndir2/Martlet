# Martlet

**Status: internal explicit API conversation integration; not a qualified release.**
Desktop now has a separate [real API conversation](docs/CONVERSATION.md) surface:
explicit typed input, bounded push-to-talk or hands-free voice activity through
configured OpenAI STT, participation policy, streaming LLM and optional generated
voice/playback. Optional local **Voice ID** recognizes your enrolled voice and
ignores other people before anything is uploaded.
Each new action requires a bounded data/cost/output authorization; no credentials or audio are accessed on launch; network remains idle unless
opt-in update checks, host updates or [who-does-what sync](docs/CLUSTER.md) are enabled. Text-only never requests TTS or opens output.
Actual account/device/first-conversation qualification remains **NOT RUN**.

The desktop starts with a short welcome tour, then a Home page with one next
step at a time (setup checklist, then **Start talking**), a **Devices** map of
every computer and cloud service with its hardware and roles, where **Who does
what** hands jobs such as Audio2Face lip-sync to any paired host on the spot and
installs or removes host roles remotely, a host dashboard
for PCs that lend their GPU, and Companion and Settings pages for everything
else. Opt-in [shared who does what](docs/CLUSTER.md) keeps those assignments in
sync on every host and every one of your computers, and moves a job to another
host that runs the same engine when its host stops answering. See the [desktop UI design](docs/UI_DESIGN.md).

Martlet also retains an accessible Windows desktop demo and Doctor self-test, using the
production text validator, bounded session state, diagnostics and optional PCM
sink. All demo text is authored synthetic content, **FIXTURE - NOT AI**.
The fixture uses no microphone, provider, network, credentials or GPU. Audio is OFF by
default; the separate confirmed 200 ms tone is not speech or proof of audibility.
Resumable configuration and explicit Windows credential actions are available
through **Setup / resume**; see [SETUP](docs/SETUP.md). Saved API routes are not
verified connections or spending permission. **Audio setup (local only)** offers explicit microphone/
output selection and separately confirmed bounded local capture/tone tests.
Opening it does not enumerate or open devices. Historical local checkpoints
are not device readiness or permission to listen later. Hands-free voice
activity listens only while you keep **Start listening** on; acoustic wake words,
automatic name/group listening and supported end-user deployment
are not available. A PC microphone does not automatically
capture remote participants.

The intended first experience is a Windows installer, microphone and speaker
setup, an explicitly selected AI provider, and a working voice conversation
with built-in troubleshooting. Dedicated AI hardware is not required for the
planned API-backed route. API use may cost money and sends the selected data
to the selected provider; a separate fixture demo will not perform AI inference.

Later milestones cover Ubuntu self-hosting, two-host GPU deployments, opt-in
screen understanding and memory. [Live2D and VRM avatar development](docs/AVATARS.md)
is authorized in parallel now, with Audio2Face first/preferred and avatars OFF
by default. The internal Desktop route renders either model in a transparent,
always-on-top character overlay: drag the character or its **Move character**
handle to reposition it while she talks. Explicit model inspection and
generated-speech activation still require the local prerequisites in the
[Desktop avatar guide](src/Martlet.Avatar.Hosting/README.md). This is not a
qualified end-user avatar release; voice reliability and model/device
qualification remain separate requirements.

**Watch my screen or a camera** lets Martlet comment now and then on your
screen, a webcam or capture card, a phone camera (phone-as-webcam apps or an
IP camera address) or other video sources. See
[screen and camera commentary](docs/SCREEN_COMMENTARY.md).

**Voice Library (local preparation)** now offers all five self-hosted research
targets (F5-TTS, Qwen3-TTS, Chatterbox, GPT-SoVITS, XTTS-v2), explicit private
WAV/transcript import for reference or training material, persistent
inspection/removal and engine-specific guidance. It does not install models,
upload audio, train, synthesize, preview speech or change the conversation
voice. Those stages follow the [Voice Studio plan](docs/VOICE_STUDIO.md).

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
| [Coding-agent instructions](AGENTS.md) | Autonomous autopilot-style work, task branches, prototype-speed policy (no required local gates), and normal merge into `main`; explicit holds and safety boundaries remain binding |
| [Development plan](DEVELOPMENT_PLAN.md) | Scope, proposed decisions, priorities, risks, and reading order |
| [Desktop UI design](docs/UI_DESIGN.md) | Per-stage design: welcome tour, companion home and setup checklist, host dashboard, Devices hardware map, add-a-computer wizard, chat-first conversation and motion system |
| [Companion requirements](docs/COMPANION_REQUIREMENTS.md) | Planned persona editing, F5 reference voices, LLM/VLM selection, listen-first behavior, speech interruption and response-style controls; not current features |
| [Avatar guide and compatibility](docs/AVATARS.md) | Accepted Live2D/VRM direction, Audio2Face-first analysis, per-model mappings, working/degraded/blocked permutations, parallel plan and remaining qualification |
| [Architecture and provider contracts](docs/ARCHITECTURE.md) | Components, trust boundaries, conversation policy, streaming, and failure behavior |
| [Installation and support design](docs/INSTALLATION_SUPPORT.md) | First run, host setup, lifecycle, doctor, and troubleshooting matrix |
| [Recommended setups](docs/RECOMMENDED_SETUPS.md) | What must run on your PC versus an API or host, offloading the LLM to OpenRouter/NVIDIA Build, fastest-response layouts, GPU/VRAM priorities, and layouts from one machine to an unlimited-budget setup |
| [iOS and iPadOS plan](docs/IOS.md) | What it takes to run Martlet on iPhone/iPad as a companion while you game there and as a host lending on-device speech, voices and Apple Intelligence to the desktop: feature matrix, platform limits, gateway subset and slices IO01-IO11 (plan only) |
| [Shared who does what and failover](docs/CLUSTER.md) | The cluster plan every host and desktop keeps, how copies merge, the 15-second sync, per-job failover between hosts and its edge cases |
| [Prerequisites](docs/PREREQUISITES.md) | Every runtime prerequisite by feature and machine: what is bundled, what the installer and **Martlet prerequisites** tool install on request (WebView2, microphone access, Windows speech, Ollama, WSL 2 + Docker Desktop), what hosts install, and what you supply |
| [Delivery and release plan](docs/DELIVERY.md) | PR-sized backlog, dependencies, acceptance criteria, release gates, and traceability |
| [Research and provenance](docs/RESEARCH.md) | Dated primary sources, verified constraints, and unresolved integration questions |
| [Local MCP control](docs/MCP.md) | Stdio tools for headless diagnostics/fixtures and interactive desktop UI Automation |
| [Implemented foundation and decisions](docs/FOUNDATION.md) | Accepted/deferred decisions, exact APIs/bounds, current behavior and next ownership |
| [Resumable setup and local audio](docs/SETUP.md) | V02a configuration/vault actions and V02b explicit local device tests, historical checkpoints, strict migration and remaining live gates |
| [Explicit API conversation](docs/CONVERSATION.md) | V04b typed/PTT path, hands-free voice activity, local Voice ID, exact supported models and bounds, fresh authorization, Stop/cleanup, troubleshooting and separately authorized live-trial checklist |
| [Voice Studio research and setup](docs/VOICE_STUDIO.md) | Five-engine implementation research, guided setup, audio imports, A/B previews, training and staged acceptance |
| [Consented local memory](docs/MEMORY.md) | OFF-by-default P03a/P03b/P03c Desktop fact management, per-turn retrieval, privacy/deletion/export and remaining qualification gates |

The broader plan documents remain future specifications except for the current
implementation/acceptance ledger in [DELIVERY](docs/DELIVERY.md) and the
[fixture experience](docs/DIAGNOSTICS.md#offline-fixture-experience-f03c).
Martlet's original source, artwork, and documentation are
[all rights reserved](LICENSE), even if the repository becomes public. Only
unmodified official Release binaries receive a narrow personal,
noncommercial download/install/use grant; no redistribution or source reuse is
allowed. Third-party components retain their own terms and must be cleared
for distribution separately.

**App updates** come from Martlet's public GitHub Releases (Settings > App
updates). Checks are **OFF by default**. Turning on *Check GitHub for new
versions automatically* checks right away and then every 15 minutes to 24
hours (default: every hour) while Martlet runs; **Check for updates now**
makes an explicit request instead. Only normal (non-draft, non-prerelease)
releases with the exact `Martlet-<version>-win-x64.exe` asset are offered.
**Install** downloads it (at most 512 MiB) into `updates\` in the local data
directory, verifies the exact bytes against GitHub's SHA-256 asset digest,
closes Martlet, runs the installer with its progress window (`/SILENT`, no
optional prerequisite tasks) and starts Martlet again; the next launch reports
the result. With *Download and install updates automatically* this happens by
itself, but only while the character is hidden, no conversation, demo or
Martlet window is open and Martlet is not the active window (it restarts
minimized); otherwise the downloaded update installs when you exit Martlet.
Choices live in `update-checks.txt` and `updates.json`, separately from profile
settings and configuration backup; unreadable preferences stay OFF with a
visible error. Releases are normal GitHub releases; code signing is not a
requirement for this personal project, so the installer is unsigned. The
digest detects a damaged download; it does not prove who published it. Do not
run an internal build as an update.

Paired **Martlet hosts** follow the desktop's version: the gateway reports its
release, the Devices map shows *Update available* for older hosts, and **Update
host** rebuilds that host's gateway from this version in a console
(`martlet-host update`; identity, pairings and roles stay). *Keep my Martlet
hosts on this PC's version* does the same in the background every interval
for hosts Martlet reaches over an SSH key or this PC's Docker Desktop; a host
that needs a password, sudo or an approval keeps the console route. The
[Windows packaging guide](packaging/windows/README.md) distinguishes local
internal builds from the manually dispatched release workflow, which has no
push, PR, tag or scheduled trigger.

## Local-only validation policy

**Current owner policy, 2026-09-26: prototype speed, never remote validation.**
This supersedes earlier plans for hosted CI and the 2026-09-13 local-gate
requirement. Do not create, enable, dispatch or retry remote test/validation
pipelines, including GitHub Actions with self-hosted runners. Do not restore
Actions billing, increase spending limits or use another hosted service to
obtain validation evidence.

Martlet is a prototype. Local test suites, build/package/smoke gates and
independent review are **not required** before commit, PR or merge and should
not be run by default. Run at most a quick targeted build or test when it
directly helps finish or debug a change. Existing tests and scripts remain
available for manual use. `CI=true` remains a local MSBuild setting for locked
restore and deterministic build metadata; it does not require a remote runner.

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
still require actual execution before being claimed. Unrun qualification is
reported as **NOT RUN**, never as passed; it does not block prototype work.

## Developer quick start

For **development**, use Windows with .NET SDK **10.0.401** (the exact .NET 10
LTS SDK in `global.json`). PowerShell 7 is needed only for the smoke scripts.
Desktop/solution builds also require Node.js and npm to build the real bundled
avatar shell (locally exercised with Node **20.11.1**, npm **10.2.4** and locked
esbuild **0.25.12**). Initial NuGet/npm restores need internet; no provider keys,
microphone, GPU, Docker, Python, administrator rights or model downloads are
needed for building. End users need no Node/npm/dev server: the shell is bundled.
Actual avatar activation has separate user-owned runtime/model prerequisites;
see the [Desktop avatar guide](src/Martlet.Avatar.Hosting/README.md).

```powershell
npm ci --prefix src\Martlet.Avatar.Vrm --no-audit --no-fund
dotnet restore Martlet.slnx --locked-mode
dotnet build Martlet.slnx --no-restore -c Release "-p:NodeExecutable=$((Get-Command node).Source)"
dotnet test Martlet.slnx --no-build -c Release
.\scripts\Smoke-Doctor.ps1
.\scripts\Smoke-Desktop.ps1
```

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
dotnet run --project src\Martlet.Doctor -f net10.0-windows --no-build -c Release -- self-test --scenario streaming --json --data-directory $data
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

Doctor `status` currently exits **2 (incomplete)** on first run or a valid
unconfigured profile, not success. Exit 3 means invalid invocation/settings;
exit 1 is reserved for reported probe failures; exit 0 means requested required
checks passed or help/version completed. `--help` lists implemented commands.
`self-test` reports only its explicit fixture: 0 completed, 1 scripted/device
failure, 2 refused/silent/canceled/incomplete, 3 invalid invocation. A fixture
pass does not change real-mode readiness. `--profile` and live probes are unavailable.

In the desktop choose a scenario and **Try fixture (audio OFF)**. No profile
is needed, and malformed settings are left intact. Stop clears pending text and
PCM; a new action uses fresh turn/request IDs and epochs. `refused`,
`refused-after-partial`, `no-speech`, `not-addressed`, `canceled`, `truncated`,
`slow`, and `failed` explain common outcomes without contacting a provider.
Script time is synthetic; `slow` is an accelerated deadline demonstration.
Only the separate confirmation button (or Windows Doctor's explicit
`self-test --play-tone`) permits the fixed-at-start default output for that
one action. Check the output, volume and audience before permitting a tone.

Without `--data-directory`, both programs read
`%LocalAppData%\Martlet\settings.json`. Launch never creates a profile or starts
capture/networking. The desktop's explicit **Create unconfigured local profile**
action saves settings only. Malformed, newer or inaccessible files are reported
with a remedy and are not reset to defaults or overwritten.

Core/Fixtures/Sessions/Diagnostics and test projects target `net10.0` without WPF.
Doctor has portable `net10.0` (text only) and `net10.0-windows` targets; Audio
has a portable sink and a Windows WASAPI adapter. Both Doctor targets support
the same offline text command. No device is needed for text or diagnostics.
For a UI-independent test build, run
`dotnet test tests\Martlet.Core.Tests -c Release` or
`dotnet test tests\Martlet.Doctor.Tests -c Release`.
The full solution additionally builds the Windows-only WPF project and runs
the existing fixture, audio and integration suites, including Providers,
Conversation, Participation and Desktop's production-path in-process HTTP tests.
Dedicated direct-project provider/runtime/policy commands remain available for
targeted local validation; no hosted validation workflows are retained here.

These SDK commands are **not the intended end-user installation experience**.
The [internal packaging scripts](packaging/windows/README.md) now build complete
self-contained Desktop/Doctor payloads and an unsigned per-user Inno installer.
They include the fixture experience, actual runtime/audio dependencies and
notices, but do not install anything as part of validation. Internal artifacts
are not a supported download or an instruction to bypass Windows protection.
The V07c packaging foundation adds required offline unsigned provenance and a
CycloneDX 1.6 SBOM tied to the actual payload and resolved dependency graph;
these checksums and observations do not establish publisher trust or license
clearance.
A supported release must ultimately need no Git, SDK or separately installed .NET.
Clean Windows, real audio/provider, Ubuntu/GPU and signing gates remain unpassed.
