# Martlet Copilot instructions

Read and follow [the repository agent instructions](../AGENTS.md) for every
task. They are the canonical autonomous engineering, validation (tests and MCP),
parallel ownership and serial merge policy.

**Highest priority: work autonomously, autopilot style.** Make the decisions,
do the work and finish it end to end without routine confirmation, plan
approval or choice questions. Stop only for a genuine blocker or an action that
needs explicit authorization.

**Validate every change before merge** ([docs/VALIDATION.md](../docs/VALIDATION.md)):
run `.\scripts\Test-Martlet.ps1` (it selects the tests the change affects and runs
them in parallel here and on the developer's validation hosts from
`~\.martlet-dev\validation.json`) and make it pass with no new failures. Verify
every feature or behavior change through Martlet's own MCP server on this dev
machine whenever possible: build it and drive the actual feature with
`scripts\Invoke-MartletMcp.ps1` (Doctor and status tools headless, `-Desktop` for
UI) on a disposable data directory. **Always extend the MCP server in the same
change** so new controls, status and capabilities are reachable and observable
(automation IDs, `SafeClicks`/`SafeValues`, new tools or Doctor probes,
`docs/MCP.md`). Run what tests do not cover the way it is used. Report anything
this environment cannot exercise as NOT RUN with the reason, and put the
runner's `summary.md` and the MCP results in the PR. Never claim an unrun check
passed, and add to `tests\known-failures.txt` only tests shown failing on
`origin/main` without your change.

For implementation work, reuse the assigned worktree branch, commit, publish a
PR and merge it into `main` yourself once validation passes, without routine
approval prompts. Explicit user holds, required protections and authorization
boundaries still apply. Never trigger
remote validation; check workflow triggers before publication and merge, and
confirm the merged result on `origin/main`. Releases are pre-authorized:
agents may build and publish them through a minimal build/release workflow
without asking. Deliver actual production paths
early, replace obsolete designs when warranted and minimize narration.
Parallelize independent work with isolated ownership. Safety, consent and
honest reporting remain mandatory.

Never use Copilot Cloud (cloud/coding agent, cloud sessions, Copilot code
review or any GitHub-hosted agent) and never consume GitHub Actions minutes,
apart from dispatching `windows-release.yml` to build an actual release. All
agent work, builds, tests and checks run locally or on the developer's own
validation hosts.

Never create GitHub CI pipelines: Actions are reserved for minimal
build/package/release workflows (manual dispatch or tag/release triggers only;
no PR, push or schedule triggers, hosted tests or disguised validation).
Unsigned installers are actual, normal releases (not prototype prereleases);
code signing is not required. Never misrepresent a GitHub digest as a
publisher signature, and preserve user consent for downloads and installation.
