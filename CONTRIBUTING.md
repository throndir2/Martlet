# Contributing to Martlet

Welcome. This is the short version; the details live in the linked documents.

## Set up

Windows 11 with the .NET SDK pinned in `global.json`, PowerShell 7, Node.js/npm,
Python 3.12 (`py -3.12`) for worker tests, and Docker Desktop in Linux container
mode if you want the Linux-only tests to run on your PC. The
[developer quick start](README.md#developer-quick-start) has the build commands.
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
2. Write or update tests in the matching `tests\` project.
3. Validate ([Validating changes](docs/VALIDATION.md)):
   - `.\scripts\Test-Martlet.ps1` runs the tests your change affects, in
     parallel, here and on your validation hosts. It must pass with no new
     failures.
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
  [validation policy](README.md#local-only-validation-policy).
- **Keep `main` green.** A new failing test blocks a merge. Tests that already
  fail on `main` are listed in `tests\known-failures.txt`; fix them and delete
  their lines. Add a line only for a test shown failing on `origin/main` without
  your change, never for one your change breaks.
- **Never use a real profile, real credentials or paid services in tests or
  verification**, and never claim a check passed that you did not run.
- **Conversation latency must never grow** (time from the end of speech to the
  first audible word). See [AGENTS.md](AGENTS.md#never-add-conversation-latency).
- Coding agents follow [AGENTS.md](AGENTS.md), which also applies to people where
  it describes engineering practice.
