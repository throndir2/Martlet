# Martlet

**Status: internal offline fixture product, not a working AI companion.**
Martlet has an accessible Windows desktop demo and Doctor self-test, using the
production text validator, bounded session state, diagnostics and optional PCM
sink. All demo text is authored synthetic content, **FIXTURE - NOT AI**.
No microphone, provider, network, credentials or GPU is used. Audio is OFF by
default; the separate confirmed 200 ms tone is not speech or proof of audibility.
Real conversations, capture, provider setup, signed installation and supported
end-user deployment are not available.

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

The broader plan documents remain future specifications except for the current
implementation/acceptance ledger in [DELIVERY](docs/DELIVERY.md) and the
[fixture experience](docs/DIAGNOSTICS.md#offline-fixture-experience-f03c).
The repository
does not have a project license; selecting one is an owner decision before
distributing software. No code license grant is implied.

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
the existing fixture, audio and integration suites.

These SDK commands are **not the intended end-user installation experience**.
F02 will provide an internal self-contained per-user installer path; a supported
release must ultimately need no Git, developer SDK or separately installed .NET.
Clean Windows, real audio/provider, Ubuntu/GPU and signing gates remain unpassed.
