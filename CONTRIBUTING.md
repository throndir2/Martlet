# Contributing to Martlet

Welcome. This is the short version; the details live in the linked documents.

## Set up

Windows 11 with the .NET SDK pinned in `global.json`, PowerShell 7, Node.js/npm,
Python 3.12 (`py -3.12`) for worker tests, and Docker Desktop in Linux container
mode if you want the Linux-only tests to run on your PC. The
[developer quick start](#developer-quick-start) has the build commands.
Then create your local developer profile once:

```powershell
.\scripts\Test-Martlet.ps1 -InitProfile
```

It lives in `~\.martlet-dev\` (outside the repository, shared by all your
checkouts and worktrees) and lists the machines you use for validation, such as
Docker on your PC or a Linux box over key-based SSH. See
[Validation hosts](docs/VALIDATION.md#validation-hosts-and-the-developer-profile).

## Make a change

1. Branch from freshly fetched `origin/main`; keep one focused change per branch.
2. Write or update tests in the matching `tests\` project. If users will notice
   the change, add a line under `## [Unreleased]` in `CHANGELOG.md`
   ([how](AGENTS.md#changelog-and-release-notes)).
3. Validate ([Validating changes](docs/VALIDATION.md)):
   - `.\scripts\Test-Martlet.ps1 -Project <Project>.Tests -Filter '<your tests>'`
     runs only your targeted tests: the ones you added or changed and the suite
     directly covering your change, here or on your validation hosts. Don't run
     everything; many unrelated suites fail or are flaky under load. Your
     targeted tests must pass.
   - For a feature or behavior change, drive it through Martlet's MCP server
     with `.\scripts\Invoke-MartletMcp.ps1` on a disposable data directory, and
     extend the MCP server so the new feature can be reached and observed.
   - Run anything tests do not cover (scripts, packaging) the way it is used.
4. Open a pull request to `main` with a short description, the runner's
   `artifacts\validation\summary.md`, what you verified through MCP and anything
   you could not run (**NOT RUN** with the reason).
5. Merge once validation is green. Coding agents merge their own validated pull
   requests.

## Ground rules

- **No remote CI.** Never add, enable or dispatch GitHub Actions or hosted
  validation; all builds and tests run on developers' own machines. The only
  workflow is the manually dispatched release build. See the
  [validation policy](#local-only-validation-policy).
- **Keep `main` green.** A targeted test your change breaks blocks a merge. Tests that already
  fail on `main` are listed in `tests\known-failures.txt`; fix them and delete
  their lines. Add a line only for a test shown failing on `origin/main` without
  your change, never for one your change breaks.
- **Never use a real profile, real credentials or paid services in tests or
  verification**, and never claim a check passed that you did not run.
- **Conversation latency must never grow** (time from the end of speech to the
  first audible word). See [AGENTS.md](AGENTS.md#never-add-conversation-latency).
- Coding agents follow [AGENTS.md](AGENTS.md), which also applies to people where
  it describes engineering practice.

## Developer quick start

Windows with the .NET SDK **10.0.401** (`global.json`), PowerShell 7 and
Node.js/npm. Worker tests use Python 3.12; Docker Desktop in Linux container
mode runs the Linux-only tests. See [Build notes](#build-notes)
for details.

```powershell
npm ci --prefix src\Martlet.Avatar.Vrm --no-audit --no-fund
dotnet restore Martlet.slnx --locked-mode
dotnet build Martlet.slnx --no-restore -c Release "-p:NodeExecutable=$((Get-Command node).Source)"
.\scripts\Test-Martlet.ps1 -InitProfile   # once: your local developer profile and validation hosts
.\scripts\Test-Martlet.ps1 -List          # what your change touches, without running anything
.\scripts\Test-Martlet.ps1 -Project Martlet.Core.Tests -Filter 'FullyQualifiedName~Settings'   # targeted tests
.\scripts\Smoke-Doctor.ps1
.\scripts\Smoke-Desktop.ps1
```

Run with a disposable data folder while developing, never your real profile:

```powershell
$data = Join-Path $env:TEMP ("Martlet.Dev." + [guid]::NewGuid().ToString('N'))
dotnet run --project src\Martlet.Desktop --no-build -c Release -- --data-directory $data
dotnet run --project src\Martlet.Doctor -f net10.0-windows --no-build -c Release -- status --json --data-directory $data
```

## Local-only validation policy

**Every change is validated locally before it merges; never with remote
validation** (owner policy, 2026-10-03).

- Every change except documentation passes its **targeted tests** (the tests it
  adds or changes and the suite directly covering the changed code, never every
  affected suite), run by `scripts\Test-Martlet.ps1 -Project` on the developer's
  PC and the validation hosts in their local profile
  (`~\.martlet-dev\validation.json`). A targeted test the change breaks blocks
  the merge; tests already failing on `main` are listed in
  `tests\known-failures.txt` until fixed.
- Every feature or behavior change is also verified through **Martlet's own MCP
  server**, extended in the same change so it can reach and observe the feature.
  See [Validating changes](docs/VALIDATION.md).
- Do not create, enable, dispatch or retry remote test/validation pipelines,
  including GitHub Actions with self-hosted runners, and do not restore Actions
  billing, raise spending limits or use another hosted service for validation
  evidence. `CI=true` is only a local MSBuild setting for locked restore and
  deterministic build metadata.
- The only remote workflow is a **minimal build/package/release** (restore,
  compile, package, publish a GitHub release) with manual dispatch or
  tag/release triggers only: no tests, lint, smoke, qualification, matrices or
  disguised validation, and no PR/push/scheduled automation. Agents may dispatch
  and publish releases without approval. Releases are normal GitHub releases;
  the unsigned installer never suppresses Windows protection warnings.
- Before any push or PR creation/update, inspect the workflow events, refs and
  resulting tree (including older branches that could restore deleted
  workflows) so publishing starts no remote validation. Report impossible
  required checks instead of bypassing protections or inventing statuses.
- Historical hosted results stay historical, not local passes. Real
  Windows/Linux, device, model/GPU and clean-machine qualification needs actual
  execution before being claimed; what an environment cannot run is reported as
  **NOT RUN**, never as passed.

## Build notes

- Building needs the .NET SDK **10.0.401** from `global.json`, PowerShell 7 and
  Node.js/npm for the bundled avatar shell (locally exercised with Node
  **20.11.1**, npm **10.2.4** and locked esbuild **0.25.12**). Initial NuGet/npm
  restores need internet; no provider keys, microphone, GPU, Docker, Python,
  administrator rights or model downloads are needed to build. End users need
  no Node/npm/dev server: the shell is bundled.
- `NodeExecutable` defaults to `node`; passing it explicitly also works when a
  long inherited Windows PATH is truncated by nested command execution. Explicit
  RID restores use project-local generated `obj\runtime-locks` documents, not
  ordinary source locks, and are not release reproducibility evidence.
- `Martlet.slnx` lists the main projects for the IDE; `Test-Martlet.ps1` finds
  every test project under `tests\` itself.
- `Smoke-Desktop.ps1` needs an interactive Windows desktop and exits the app
  after reading its accessible status. Both smoke scripts use unique temporary
  data paths, never the real user profile.
- Without `--data-directory`, the desktop and Doctor read
  `%LocalAppData%\Martlet\settings.json`. Launch never creates a profile or
  starts capture/networking. Malformed, newer or inaccessible files are reported
  with a remedy and are not reset or overwritten.
- Doctor `status` exits **2 (incomplete)** on first run or a valid unconfigured
  profile. Exit 3 means invalid invocation/settings, exit 1 reported probe
  failures and exit 0 that the requested checks passed. `--help` lists the
  implemented commands.
- Core/Diagnostics and their tests target `net10.0` without WPF. Doctor has
  portable `net10.0` (text only) and `net10.0-windows` targets; Audio has a
  portable sink and a Windows WASAPI adapter.
- The [packaging scripts](packaging/windows/README.md) build self-contained
  Desktop/Doctor payloads, an unsigned per-user Inno installer, offline
  provenance and a CycloneDX 1.6 SBOM. Avatar activation has its own runtime and
  model prerequisites; see the [Desktop avatar guide](src/Martlet.Avatar.Hosting/README.md).
