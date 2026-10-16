# Developer Guide

This page tells you how to build Martlet, change it and validate the change. The full rules are in [CONTRIBUTING.md](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md) and [AGENTS.md](https://github.com/throndir2/Martlet/blob/main/AGENTS.md). AGENTS.md is for coding agents, but its engineering practice also applies to people.

## Prerequisites

- Windows 11 with the .NET SDK **10.0.401**. `global.json` pins this version.
- PowerShell 7 and Node.js with npm. The avatar renderer is a bundled web app.
- Python 3.12 (`py -3.12`) for the worker tests.
- Optional: Docker Desktop in Linux container mode. It runs the Linux-only tests on your PC.

The first NuGet and npm restores need the internet. You do not need provider keys, a microphone, a GPU or administrator rights to build.

## Build

```powershell
npm ci --prefix src\Martlet.Avatar.Vrm --no-audit --no-fund
dotnet restore Martlet.slnx --locked-mode
dotnet build Martlet.slnx --no-restore -c Release "-p:NodeExecutable=$((Get-Command node).Source)"
```

`Martlet.slnx` lists the main projects for the IDE. The test runner finds every test project under `tests\` by itself.

## Create your developer profile

Do this one time:

```powershell
.\scripts\Test-Martlet.ps1 -InitProfile
```

The profile is in `~\.martlet-dev\` (`validation.json` and `NOTES.md`). It is outside the repository and all your checkouts use it. It lists the machines that you use for validation, for example Docker on your PC or a Linux computer that you reach with key-based SSH. Never commit it.

## Run Martlet with disposable data

Always use a disposable data folder while you develop. Never use your real profile.

```powershell
$data = Join-Path $env:TEMP ('Martlet.Dev.' + [guid]::NewGuid().ToString('N'))
dotnet run --project src\Martlet.Desktop --no-build -c Release -- --data-directory $data
dotnet run --project src\Martlet.Doctor -f net10.0-windows --no-build -c Release -- status --json --data-directory $data
```

Without `--data-directory`, the desktop and Doctor use `%LocalAppData%\Martlet`. Starting the app never starts capture or networking.

The Linux and macOS companion is `src\Martlet.Companion`. Publish it with `dotnet publish src\Martlet.Companion -c Release -r <rid> --self-contained` (`linux-x64`, `linux-arm64`, `osx-arm64` or `osx-x64`). `Martlet.Companion --status` prints what it detected and which engines it offers.

## Make a change

1. Fetch `origin/main` and make a branch from it. Keep one focused change on each branch.
2. Find the file that owns the rule in the [decision map](https://github.com/throndir2/Martlet/blob/main/docs/DECISION_MAP.md). Change that owner, not the pages and tools that only show its result.
3. Use the names in the [glossary](https://github.com/throndir2/Martlet/blob/main/docs/GLOSSARY.md) in code, UI text and docs.
4. Share each new setting on all the owner's computers. Keep a setting on one computer only when it describes that computer. See [One Martlet on every computer](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md#one-martlet-on-every-computer).
5. Add or update tests in the matching `tests\` project.
6. If users can notice the change, add one plain line with the pull request link under `## [Unreleased]` in `CHANGELOG.md`. See [Releasing](Releasing).
7. Validate the change locally. See [Validation and Testing](Validation-and-Testing).
8. Open a pull request to `main`. Put `artifacts\validation\summary.md`, the MCP calls and their results, and each **NOT RUN** item with its reason in the description.
9. Merge when the validation passes.
10. Clean up your build output (below).

Do not make the conversation slower. The time from the end of your speech to Martlet's first audible word must never grow. See [Never add conversation latency](https://github.com/throndir2/Martlet/blob/main/AGENTS.md#never-add-conversation-latency).

## Validate

```powershell
.\scripts\Test-Martlet.ps1 -List
.\scripts\Test-Martlet.ps1 -Project Martlet.Core.Tests -Filter 'FullyQualifiedName~Settings'
.\scripts\Smoke-Doctor.ps1
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"doctor_status"}]'
.\scripts\Invoke-MartletMcp.ps1 -Build -Desktop -Calls '[{"name":"ui_click","arguments":{"id":"TourSkip"}},{"name":"ui_click","arguments":{"id":"NavSettings"}},{"name":"ui_snapshot","until":"AutomaticUpdateCheck"}]'
```

Run only your targeted tests. Show each feature or behavior change through [Martlet's MCP server](Martlet-MCP-Server), and extend the server in the same change. `-AllowUiEffects` is only for disposable data with no real credentials. It never permits spending, real provider requests, credential handling, audio capture or playback, or data disclosure.

All validation is local. Never add, enable or dispatch GitHub Actions or other hosted validation. The only workflow is the release build that you start by hand. See the [local-only validation policy](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md#local-only-validation-policy).

## Doctor exit codes

| Exit | Meaning |
| --- | --- |
| 0 | The selected checks passed, or help or version succeeded. |
| 1 | A reported failure or a callback fault. |
| 2 | Incomplete: warning, unknown, skipped, not configured, timed out, catalog-only or no required checks. A first run gives 2. |
| 3 | Invalid invocation, or the selected settings are invalid, newer or not accessible. |

`status` only reads. It does not open devices or provider connections. `--help` lists the commands.

## Clean up

```powershell
.\scripts\Clean-Martlet.ps1 -WhatIf   # show what it removes, with sizes
.\scripts\Clean-Martlet.ps1           # remove this checkout's build output and old disposable %TEMP% folders
```

A checkout can grow to several GB of `bin\`, `obj\` and `node_modules\`. The script removes only ignored, generated folders. It never removes tracked files or uncommitted work. Stop the processes you started (Desktop, MCP server, worker hosts) before you run it. `-Git` and `-AllWorktrees` are for the developer only.

More detail: [Contributing](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md), [Validating changes](https://github.com/throndir2/Martlet/blob/main/docs/VALIDATION.md), [Decision map](https://github.com/throndir2/Martlet/blob/main/docs/DECISION_MAP.md), [Glossary](https://github.com/throndir2/Martlet/blob/main/docs/GLOSSARY.md), [Cleaning up disk space](https://github.com/throndir2/Martlet/blob/main/docs/CLEANUP.md), [Diagnostics](https://github.com/throndir2/Martlet/blob/main/docs/DIAGNOSTICS.md), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md).
