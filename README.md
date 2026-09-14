# Martlet

**Status: internal explicit API conversation integration; not a qualified release.**
Desktop now has a separate [real API conversation](docs/CONVERSATION.md) surface:
explicit typed input or bounded push-to-talk through configured OpenAI STT,
participation policy, streaming LLM and optional generated voice/playback.
Each new action requires a bounded data/cost/output authorization; no credentials,
network or audio are accessed on launch. Text-only never requests TTS or opens output.
Actual account/device/first-conversation qualification remains **NOT RUN**.

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
are not device readiness or permission to listen later. Learned VAD, acoustic
wake words, automatic name/group listening, signed installation and supported
end-user deployment are not available. A PC microphone does not automatically
capture remote participants.

The intended first experience is a Windows installer, microphone and speaker
setup, an explicitly selected AI provider, and a working voice conversation
with built-in troubleshooting. Dedicated AI hardware is not required for the
planned API-backed route. API use may cost money and sends the selected data
to the selected provider; a separate fixture demo will not perform AI inference.

Later milestones cover Ubuntu self-hosting, two-host GPU deployments, opt-in
screen understanding and memory, and optional user-supplied avatars. Reliable
voice and installation take priority over avatars.

| Document | Purpose |
| --- | --- |
| [Development plan](DEVELOPMENT_PLAN.md) | Scope, proposed decisions, priorities, risks, and reading order |
| [Architecture and provider contracts](docs/ARCHITECTURE.md) | Components, trust boundaries, conversation policy, streaming, and failure behavior |
| [Installation and support design](docs/INSTALLATION_SUPPORT.md) | First run, host setup, lifecycle, doctor, and troubleshooting matrix |
| [Delivery and release plan](docs/DELIVERY.md) | PR-sized backlog, dependencies, acceptance criteria, release gates, and traceability |
| [Research and provenance](docs/RESEARCH.md) | Dated primary sources, verified constraints, and unresolved integration questions |
| [Implemented foundation and decisions](docs/FOUNDATION.md) | Accepted/deferred decisions, exact APIs/bounds, current behavior and next ownership |
| [Resumable setup and local audio](docs/SETUP.md) | V02a configuration/vault actions and V02b explicit local device tests, historical checkpoints, strict migration and remaining live gates |
| [Explicit API conversation](docs/CONVERSATION.md) | V04b typed/PTT path, exact supported models and bounds, fresh authorization, Stop/cleanup, troubleshooting and separately authorized live-trial checklist |

The broader plan documents remain future specifications except for the current
implementation/acceptance ledger in [DELIVERY](docs/DELIVERY.md) and the
[fixture experience](docs/DIAGNOSTICS.md#offline-fixture-experience-f03c).
The repository
does not have a project license; selecting one is an owner decision before
distributing software. No code license grant is implied.

## Local-only validation policy

**Current owner policy, 2026-09-13: local gates, never remote validation.**
This supersedes earlier plans for hosted CI. Do not create, enable, dispatch or
retry remote test/validation pipelines, including GitHub Actions with
self-hosted runners. Do not restore Actions billing, increase spending limits
or use another hosted service to obtain validation evidence.

Retain pinned dependencies, locked restores, production-backed tests, package
assertions, smoke/trace scripts and independent review. Run targeted local
checks first, then the full affected suites and local build/package/smoke gates
required by the change; fix findings, rerun affected checks and meet normal
repository merge requirements before merging. Documentation-only changes need
only applicable existing local documentation checks and diff review, not an
application rebuild. `CI=true` remains a local MSBuild setting for locked restore
and deterministic build metadata; it does not require a remote runner.

The only permitted remote workflow exception is an **explicitly requested,
minimal build/package/release**: restore necessary build dependencies and
compile/package the requested deliverable. Upload or release publication needs
separate authorization. No tests, lint, smoke, qualification, reproducibility
checks, matrices or disguised validation belong in that workflow.
Do not add ordinary PR/push/scheduled automation by default. This exception is
not a request to create a workflow or publish a release; no replacement
workflow is provided.

Before any push or PR creation/update, inspect the applicable workflow events,
refs and resulting tree, including older branches that could restore deleted
workflows. Establish a publication path that starts no remote validation.
Report impossible required checks instead of bypassing/changing protections,
inventing successful statuses or relying on skip markers. A publication hold
does not prevent independent local implementation and review.

Historical failed or blocked hosted results remain historical, not local passes.
Real Windows/Linux, native-device, model/GPU and clean-machine qualifications
still require actual execution in a suitable, separately authorized local
environment. Missing access remains **NOT RUN / blocked**, not permission to
substitute fixtures or install a VM, WSL, Docker, drivers or services.

## Developer quick start

For **development**, use Windows with .NET SDK **10.0.401** (the exact .NET 10
LTS SDK in `global.json`). PowerShell 7 is needed only for the smoke scripts.
Initial NuGet restore needs internet; no provider keys, microphone, GPU, Docker,
Python, Node, administrator rights or model downloads are needed.

```powershell
dotnet restore Martlet.slnx --locked-mode
dotnet build Martlet.slnx --no-restore -c Release
dotnet test Martlet.slnx --no-build -c Release
.\scripts\Smoke-Doctor.ps1
.\scripts\Smoke-Desktop.ps1
```

The desktop smoke needs an interactive Windows desktop and exits the app after
reading its accessible status. Both smoke scripts use unique temporary data
paths, never the real user profile.

Launch with an explicit disposable data location while developing:

```powershell
$data = Join-Path $env:TEMP ("Martlet.Dev." + [guid]::NewGuid().ToString('N'))
dotnet run --project src\Martlet.Desktop --no-build -c Release -- --data-directory $data
dotnet run --project src\Martlet.Doctor -f net10.0-windows --no-build -c Release -- status --json --data-directory $data
dotnet run --project src\Martlet.Doctor -f net10.0-windows --no-build -c Release -- self-test --scenario streaming --json --data-directory $data
```

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
