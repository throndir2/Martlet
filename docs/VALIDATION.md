# Validating changes

Every change is validated on the developer's own machines before it merges: the tests
it can affect, run in parallel, plus the actual behavior through Martlet's MCP server.
No remote CI runs, ever (see the [validation policy](../README.md#local-only-validation-policy)).
This page is the flow for people and coding agents alike; [AGENTS.md](../AGENTS.md)
adds the agent-specific rules.

## The flow

1. **Branch** from freshly fetched `origin/main` (`git fetch origin`).
2. **See what your change touches:** `.\scripts\Test-Martlet.ps1 -List` prints the
   test projects and suites the change selects, and why, without building anything.
3. **Run the affected tests:** `.\scripts\Test-Martlet.ps1`. It must end with
   `PASSED` (no new failing tests). It builds every affected project, runs the
   affected tests here and on your [validation hosts](#validation-hosts-and-the-developer-profile)
   at the same time, and writes `artifacts\validation\summary.md`.
4. **Exercise the behavior through Martlet MCP** for any feature or behavior change
   ([below](#mcp-verification)): drive the actual feature on a disposable data
   directory and check the outcome. Extend the MCP server in the same change so the
   feature can be reached and observed.
5. **Run what the tests do not cover** the way it is used: scripts, packaging,
   installers, worker hosts, release workflow changes. The runner lists changed
   files that no suite covers.
6. **Report it in the pull request:** paste `summary.md`, the MCP calls and what they
   showed, and every **NOT RUN** item with its reason (missing hardware, credentials,
   another OS, a locked desktop). Never present unrun or partial checks as passed.
7. **Merge** once steps 3 to 6 are done and green. Agents merge their own pull
   requests at that point without waiting for approval; see
   [Publication and merge](../AGENTS.md#publication-and-merge).

Documentation-only changes (Markdown) need none of this; the runner selects nothing
for them.

Run `.\scripts\Test-Martlet.ps1 -All` (every suite) before bumping the version for a
release, after changes to shared build settings (the runner does this on its own
when `Directory.Build.props`, `Directory.Packages.props`, `global.json`,
`NuGet.config` or `.editorconfig` change) and after a large refactor.

## What runs, and how fast

| Suites | Engine | Parallelism |
| --- | --- | --- |
| 30 .NET test projects under `tests\` (about 6,200 tests) | xUnit 2.9 on VSTest (`Microsoft.NET.Test.Sdk`), `dotnet test` | One generated solution: MSBuild builds each project once and runs the test assemblies side by side; xUnit runs test classes in parallel inside an assembly unless it opts out (Desktop, Gateway, Gateway.Host, Gateway.Persistence and the Linux suites run serially inside) |
| Linux-only .NET suites (`MartletTestHosts` = `linux`) | the same, on a Linux validation host | Concurrently with everything else |
| Python worker suites (`workers\<name>\tests`) | `unittest` | One job per worker, concurrently |
| Avatar npm packages and node tests (`src\Martlet.Avatar.Vrm`, `src\Martlet.Avatar.Live2D`, `*.test.mjs`) | `node --test` | One job per package, concurrently |

The selection is per project, so a change usually runs a slice. Measured on a
16-core Windows dev PC with a warm build: a change to a leaf library such as
`Martlet.Gateway.Trust` runs in about 15 seconds. `Martlet.Desktop` references
nearly every library, so most library changes also select
`Martlet.Desktop.Tests`, which runs serially and takes 4 to 8 minutes depending
on what else the PC is doing; a change to `Martlet.Core` selects almost
everything. The full run (`-All`) took 12 minutes on a quiet PC and 20 on a busy
one, dominated by `Martlet.Updates.Tests` (6.5 to 9.5 minutes),
`Martlet.Launcher.Tests` (4.5 to 5) and `Martlet.Desktop.Tests`. Making those
three faster is the best way to shorten long runs (tracked in
[#332](https://github.com/throndir2/Martlet/issues/332)).

## How the selection works

The runner compares the working tree (commits, staged, unstaged and untracked files)
with its merge-base on `origin/main` (`-Base` changes that) and maps each changed
file:

- A file inside a project directory selects that project. A file another project
  links or reads (`Compile Include="..\..."`, embedded resources, `MSBuild
  Projects=`) selects that project too. `MartletValidationInputs` in a `.csproj`
  declares inputs MSBuild cannot see, such as the npm packages
  `Martlet.Avatar.RendererHost` bundles.
- Every project that references a selected project, directly or transitively, is
  selected as well. Selected test projects run; the rest are built so a compile
  break anywhere downstream is caught.
- `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
  `global.json`, `NuGet.config` and `.editorconfig` select every .NET project.
- `workers\<name>\...` selects that worker's Python suite; an npm package with a
  `test` script is selected by its own files; `*.test.mjs` files run with their
  project.
- Markdown and `tests\known-failures.txt` select nothing. Anything else nothing
  covers (scripts, packaging, deploy files, workflows) is listed as **not covered
  by automated tests**: validate it by running it.

Options: `-Project <name>` runs named projects or suites (and what depends on them)
instead of the diff; `-Filter` passes a `dotnet test` filter; `-All`; `-NoHosts`
keeps everything on this PC; `-RequireAll` fails the run when anything is NOT RUN;
`-Retries` (default 2) sets how often a new failure is retried; `-TimeoutMinutes`
(default 30) stops a hung job.
`Get-Help .\scripts\Test-Martlet.ps1 -Full` lists them all.

## Results

| Result | Meaning | Blocks the merge |
| --- | --- | --- |
| Passed | Everything selected passed | No |
| Failed | A test failed that is not a known failure, or a project did not build | **Yes** |
| Flaky | A test failed, then passed when retried (up to twice) with the machine quieter | No, but fix it: flaky tests are named in the summary |
| Known failures | Only tests listed in `tests\known-failures.txt` failed | No |
| NOT RUN | This environment cannot run it (no Linux host, missing Python module, another OS) | Report it; `-RequireAll` makes it fail |

`tests\known-failures.txt` lists tests that already fail on `main`. Fixing one means
deleting its line (the runner reports known failures that pass again). A line may be
added only for a test you have shown failing on `origin/main` **without** your change
(run it on a clean export of `origin/main`), with a note in the pull request and in
the burn-down issue ([#332](https://github.com/throndir2/Martlet/issues/332)); never
for a failure your change causes. Entries tagged `env:` fail only on some machines
(for example, symlink tests without Windows Developer Mode) and are not reported as
fixed.

If `main` moves while your pull request is open, merge it and rerun the runner
before merging: someone else's change can break a test your change selects, and that
is how such breaks are caught.

Full logs and TRX files are in `artifacts\validation\`. Processes a suite leaves
running (a worker host a failed test never stopped, for example) are stopped and
noted, so they cannot hold the run open or linger on the machine; build servers
shared with other builds are left alone.

## Validation hosts and the developer profile

Some suites need another machine: the Linux gateway tests need Linux with ext4 and a
non-root user, and features may need a GPU box or a second PC. The runner uses the
hosts you have set up, from a **developer profile that is local to you** and shared
by all your Martlet checkouts and worktrees: `~\.martlet-dev\validation.json`
(`$env:MARTLET_DEV_HOME` moves it). It is never committed. Create it with:

```powershell
.\scripts\Test-Martlet.ps1 -InitProfile
```

```json
{
  "schemaVersion": 1,
  "dotnet": null,
  "python": null,
  "hosts": [
    { "name": "docker", "kind": "docker", "enabled": true, "image": null, "allowPull": false },
    {
      "name": "linux-box", "kind": "ssh", "enabled": true, "target": "me@linux-box.lan", "port": 22,
      "workDirectory": "~/.cache/martlet-validation", "dotnet": "~/.dotnet/dotnet",
      "linuxTestParent": "/home/me/martlet-validation-ext4", "sshOptions": ["-i", "~/.ssh/martlet_validation"],
      "capabilities": ["linux-x64", "ext4", "nvidia-gpu"],
      "notes": "Key-based SSH. SDK 10.0.401 installed by hand."
    }
  ]
}
```

- `dotnet` and `python` point at the .NET SDK pinned in `global.json` and the Python
  for worker tests when they are not found automatically. The runner already looks
  on `PATH`, in `%LOCALAPPDATA%\Microsoft\dotnet`, `~\.dotnet` and `DOTNET_ROOT`,
  and prefers `py -3.12` for workers.
- **docker**: Docker on this PC in Linux container mode. Linux suites run in the
  .NET SDK image pinned by `global.json` (`mcr.microsoft.com/dotnet/sdk:<version>`)
  as the non-root `app` user, on a disposable ext4 volume, with a NuGet cache in the
  `martlet-validation-nuget` volume. It is used without a profile entry when the
  image is already on the PC; it is never pulled unless `allowPull` is set. Add an
  entry with `"enabled": false` to turn it off.
- **ssh**: a Linux machine reached with key-based SSH (`BatchMode`; never a
  password). It needs the pinned SDK at `dotnet` and an existing private (0700) ext4
  directory owned by the SSH user at `linuxTestParent`. `sshOptions` adds options to
  both `ssh` and `scp` (a specific key with `-i`, a known-hosts file with
  `-o UserKnownHostsFile=...`). Each run copies the working tree to a fresh
  directory under `workDirectory`, runs there, brings the results back and deletes
  it.
- Hosts are tried in profile order; the first that is ready runs the Linux suites,
  and the reasons the others were skipped are reported.
- `~\.martlet-dev\NOTES.md` holds free-form facts that help the next validation
  run: which host has a GPU, where an SDK lives, why a host is disabled.

Rules: only hosts listed in the profile are used (plus Docker on this PC). People and
agents may record facts about listed hosts, but adding a host is the developer's
decision, because it runs code on that machine. Never use shared or production
machines, never store passwords, keys or tokens in the profile, and never install
SDKs, services or drivers on a host without its owner's go-ahead.

A test project that must run on Linux sets `<MartletTestHosts>linux</MartletTestHosts>`
(`windows;linux` runs it on both). Without it, projects targeting `-windows`
frameworks run on Windows and the rest run on this PC.

## MCP verification

Tests prove the pieces; the MCP check proves the feature works in the actual app.
`src\Martlet.Mcp` is a local MCP server over stdio, and
`scripts\Invoke-MartletMcp.ps1` drives it from this checkout's build with a list of
tool calls, on a disposable data directory that is deleted afterwards:

```powershell
# Headless: Doctor and status tools
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"doctor_status"}]'

# UI: launch a disposable desktop, connect, click and poll until a control shows
.\scripts\Invoke-MartletMcp.ps1 -Build -Desktop -Calls '[
  {"name":"ui_click","arguments":{"id":"TourSkip"}},
  {"name":"ui_click","arguments":{"id":"NavSettings"}},
  {"name":"ui_snapshot","until":"AutomaticUpdateCheck"}]'
```

The controls it offers:

- **Doctor** (`doctor_status`, `doctor_list`, `doctor_run`): the diagnostic probes
  and their report, headless.
- **Status and checks** for the subsystems: `voices_status`, `cluster_status`,
  `network_status`, `nearby_status`, `logs_tail`, `logs_timeline`,
  `latency_report`, `api_keys_status`, `smart_home_status`, `prompts_status`,
  `character_status`, `hearing_check`, `echo_check`, `pc_audio_check`, `chattiness_status`,
  `context_check`, `thinking_steps_check` and more. They read the same disposable
  data directory the desktop uses.
- **Desktop UI automation** (`ui_connect`, `ui_snapshot`, `ui_click`, `ui_select`,
  `ui_set_text`, `ui_toggle`, `ui_move`, `ui_tray`): find controls by automation ID,
  read status text, and click passive navigation. Anything that sends, records,
  plays, spends, writes files or handles credentials needs `-AllowUiEffects`
  (`--allow-ui-effects`), which is allowed only with a disposable data directory and
  no real credentials, and still never authorizes spending, real provider requests,
  audio capture or playback, or disclosing data.

Check the outcome the change should produce (status values, control state, the
Doctor report), not just that the calls returned. UI automation needs an unlocked
interactive desktop; on a locked or headless session, verify headlessly and report
the UI part as NOT RUN.

**Extend MCP with every feature:** stable `AutomationProperties.AutomationId` on new
controls, passive navigation in `SafeClicks`, non-secret status fields in
`SafeValues`, new tools or Doctor probes for new headless capabilities, and
[MCP](MCP.md) updated to match. A feature MCP cannot reach or observe is not
finished. [MCP](MCP.md#local-mcp-control-windows) documents every tool and
[Extending the server](MCP.md#extending-the-server) the conventions.

## Writing tests

- Put tests in the project's test project under `tests\` (create
  `tests\<Project>.Tests` with xUnit for a new library) so the runner finds them.
- Use temporary directories and fakes; never real credentials, paid services, the
  network, microphones or speakers, or the real Martlet profile.
- Avoid tight timing: suites run side by side and a test that only passes on an idle
  machine will show up as flaky. Wait for events with generous upper bounds rather
  than fixed sleeps.
- A worker suite that imports modules beyond the standard library lists them in
  `workers\<name>\tests\requirements.txt`; the runner picks a Python that has them.
- Mark Linux-only suites with `MartletTestHosts`, and declare build inputs MSBuild
  cannot see with `MartletValidationInputs`.
