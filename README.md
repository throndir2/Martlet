# Martlet

**Status: early application foundation, not a working AI companion.** Martlet
now has an offline Windows WPF status shell, validated per-user settings,
shared bounded contracts and a diagnostic CLI entry point. There is no AI
conversation, microphone capture, playback, fixture demo, provider integration,
inference service, signed installer or supported end-user deployment yet.

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

The broader plan documents remain future specifications except where the
foundation document explicitly identifies implemented subsets. The repository
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
dotnet run --project src\Martlet.Doctor --no-build -c Release -- status --json --data-directory $data
```

Doctor `status` currently exits **2 (incomplete)** on first run or a valid
unconfigured profile, not success. Exit 3 means invalid invocation/settings;
exit 1 is reserved for reported probe failures; exit 0 means requested required
checks passed or help/version completed. `--help` lists implemented commands.
No `self-test`, `--profile` selector or live probes exist yet.

Without `--data-directory`, both programs read
`%LocalAppData%\Martlet\settings.json`. Launch never creates a profile or starts
capture/networking. The desktop's explicit **Create unconfigured local profile**
action saves settings only. Malformed, newer or inaccessible files are reported
with a remedy and are not reset to defaults or overwritten.

Core/Diagnostics/Doctor and both test projects target `net10.0` without WPF.
For a UI-independent test build, run
`dotnet test tests\Martlet.Core.Tests -c Release` or
`dotnet test tests\Martlet.Doctor.Tests -c Release`.
The full solution additionally builds the Windows-only WPF project.

These SDK commands are **not the intended end-user installation experience**.
F02 will provide an internal self-contained per-user installer path; a supported
release must ultimately need no Git, developer SDK or separately installed .NET.
Clean Windows, real audio/provider, Ubuntu/GPU and signing gates remain unpassed.
