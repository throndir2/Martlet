# Validation and Testing

Every change except documentation is validated on the developer's own machines before it merges. Validation has two parts: the targeted tests, and the actual behavior through Martlet's MCP server. No remote CI runs.

## The flow

1. Fetch `origin/main` (`git fetch origin`) and make your branch from it.
2. Find your targeted tests: the tests you added or changed, and the test project or suite that directly covers the changed code. `.\scripts\Test-Martlet.ps1 -List` shows what the change touches without a build. Use the entries with the reason `changed: ...`. Ignore the entries that only depend on them.
3. Run only those tests. They must pass.
4. Show each feature or behavior change through [Martlet's MCP server](Martlet-MCP-Server) on a disposable data directory. Extend the server in the same change.
5. Run what the tests do not cover (scripts, packaging, installers, worker hosts, workflows) the way people use it.
6. In the pull request, put `artifacts\validation\summary.md`, the MCP calls and what they showed, and each **NOT RUN** item with its reason.
7. Merge when steps 3 to 6 are done and pass.

## Targeted tests

```powershell
.\scripts\Test-Martlet.ps1 -List
.\scripts\Test-Martlet.ps1 -Project Martlet.Conversation.Tests -Filter 'FullyQualifiedName~ReplyLatencyTests'
.\scripts\Test-Martlet.ps1 -Project Martlet.Conversation.Tests, python:singing
```

- Name each test project exactly, with `.Tests`. A product project name without `.Tests` also selects every suite that depends on it.
- Use `python:<worker>` for a worker suite in `workers\<worker>\tests`.
- Do not run the runner without `-Project`, and do not use `-All`, unless the owner asks for a broad run. Many suites fail for unrelated reasons or under parallel load, and that noise says nothing about your change.
- When your change alters an API that other projects use, build those projects (`dotnet build <project>.csproj -c Release`). Do not run their tests.
- Fix each failure that your change causes. A failure that your change cannot cause does not block the merge. Narrow the filter, note it in the pull request and do not chase it.

Other options: `-NoHosts` keeps everything on this PC, `-RequireAll` fails the run when something is NOT RUN, `-Retries` (default 2) retries a new failure, `-TimeoutMinutes` (default 30) stops a job that hangs, and `-Base` changes the compared branch. `Get-Help .\scripts\Test-Martlet.ps1 -Full` lists all options.

## What runs

| Suites | Engine |
| --- | --- |
| .NET test projects under `tests\` | xUnit with `dotnet test`. One generated solution builds each project once. |
| Linux-only .NET suites (`<MartletTestHosts>linux</MartletTestHosts>`) | The same, on a Linux validation host. |
| Python worker suites (`workers\<name>\tests`) | `unittest`, one job for each worker. |
| Avatar npm packages (`src\Martlet.Avatar.Vrm`, `src\Martlet.Avatar.Live2D`) | `node --test`. |

The runner writes logs and TRX files to `artifacts\validation\`, with the summary in `summary.md`.

## Results

| Result | Blocks the merge |
| --- | --- |
| Passed | No. |
| Failed | Yes, unless your change cannot cause it. |
| Flaky (failed, then passed on a retry) | No. Fix it only when it is one of your targeted tests. |
| Known failures (only tests from `tests\known-failures.txt` failed) | No. |
| NOT RUN (this environment cannot run it) | Report it. `-RequireAll` makes it fail. |

`tests\known-failures.txt` lists the tests that already fail on `main`. When you fix one, delete its line. Add a line only for a test that you showed failing on `origin/main` without your change. Never add a test that your change breaks.

## Validation hosts

Some suites need another machine, for example Linux with ext4. The runner uses the hosts in your developer profile, `~\.martlet-dev\validation.json`. Create it with `.\scripts\Test-Martlet.ps1 -InitProfile`.

- `docker`: Docker on this PC in Linux container mode.
- `ssh`: a Linux computer that you reach with key-based SSH. Never use a password.
- `~\.martlet-dev\NOTES.md` holds facts about the hosts, for example which host has a GPU.

Adding a host is the developer's decision. Never use shared or production machines. Never store passwords, keys or tokens in the profile.

## MCP verification

Tests prove the parts. The MCP check proves that the feature works in the actual app. See [Martlet MCP Server](Martlet-MCP-Server#verify-a-change). Check the outcome the change must give (status values, control state, the Doctor report), not only that the calls returned. On a locked or headless session, verify the headless part and report the UI part as NOT RUN.

## Write tests

- Put tests in the project's test project under `tests\`. A new library gets `tests\<Project>.Tests` with xUnit.
- Use temporary folders and fakes. Never use real credentials, paid services, the network, microphones, speakers or the real Martlet profile.
- Wait for events with generous limits instead of fixed sleeps. Suites run side by side.
- List a worker suite's extra Python modules in `workers\<name>\tests\requirements.txt`.

## Local-only policy

Do not create, enable, dispatch or retry remote test or validation workflows. This includes GitHub Actions with self-hosted runners. GitHub Actions run only the release build that you start by hand. Never say that a check passed when you did not run it.

More detail: [Validating changes](https://github.com/throndir2/Martlet/blob/main/docs/VALIDATION.md), [Local-only validation policy](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md#local-only-validation-policy), [Validate before merge](https://github.com/throndir2/Martlet/blob/main/AGENTS.md#validate-before-merge), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md#verifying-changes-with-martlet-mcp).
