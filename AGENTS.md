# Repository instructions for coding agents

These instructions apply throughout Martlet. Follow more specific instructions
and explicit user constraints as well. Read the relevant contracts and current
implementation before editing; historical plans are not evidence of completion.

## Highest priority: autonomous, autopilot-style work

Work autonomously end to end as if in autopilot mode. Make the decisions, do
the work and finish it without waiting on the owner. Do not stop for routine
confirmation, plan approval or choice questionnaires; state material
assumptions briefly and continue. Stop only for a genuine blocker or an action
outside the authorization boundaries below.

## Default: deliver through merge

For an implementation request: inspect, implement, commit, publish a pull
request and merge into `main`. Routine branch creation, commits, PR publication
and normal merging are authorized by default; do not stop at a plan, a local
patch or an open PR when the requested work can be completed safely. Do not
ask "should I proceed?" at each step.

An explicit review-only, planning, local-only, no-push, draft, approval-hold or
no-merge request overrides this default. Existing task-specific holds remain
binding. Make routine decisions directly using best practices and repository
evidence. Ask only when missing authority or a consequential unresolved decision
cannot responsibly be resolved from the user's goal and available evidence.

## Decisive autonomous loop engineering

- Be aggressive about completing the requested outcome, not defensive about
  preserving obsolete designs. Replace them when warranted, updating affected
  callers, contracts, migration paths and documentation together. Keep scope
  focused; decisiveness is not permission for unrelated rewrites or unsafe work.
- Deliver working production paths early, not endless disabled foundations,
  stubs or configuration-only milestones. Wire the actual supported route
  through its callers. Preserve required consent, opt-in defaults and
  qualification gates; report remaining prerequisites honestly rather than
  enabling an unqualified feature.
- Investigate uncertainty in current code, documentation and local evidence.
  Choose the simplest complete solution and act without routine
  questionnaires, speculative scaffolding or repeated plans.
- Fix root causes across affected callers and documentation; do not silently
  reduce scope to finish.
- Continue through each next authorized step without routine approval prompts.
  If blocked, try reasonable alternatives within scope and authorization, then
  preserve the work and report the exact blocker and smallest human action
  needed. Do not repeat unsuccessful attempts without new evidence.
- Minimize narration and token use: batch relevant reads, avoid duplicate
  investigation and report only meaningful decisions, blockers and outcomes.
  Distinguish local work, open review and verified integration; completion
  requires a working persistent result, not a proposal or queued merge.

## Branch and worktree ownership

- Use one focused task branch per bounded change, based on freshly fetched
  `origin/main`. Never implement directly on `main`.
- Reuse an assigned task branch/worktree; do not create another branch just
  because a session starts. Inspect its status and ancestry first. If it needs
  current `main`, reconcile without discarding existing work.
- Give new branches short, descriptive kebab-case names. In app-managed
  sessions, use the app's branch/session tools and preserve its configured
  prefix; otherwise use normal non-interactive Git branch creation.
- Work only in the assigned worktree. Never switch, edit or clean the main
  checkout or another task's worktree. Preserve unrelated and uncommitted work.
- Start independent tasks from current `main`. Stack on an unmerged task branch
  only when explicitly requested or when the work genuinely depends on it;
  merge prerequisites first and target the final integration to `main`.
- Run independent reads, checks and bounded workstreams in parallel when safe.
  Use isolated sessions/worktrees for substantial independent implementation;
  do small tasks directly rather than multiplying agents or coordination.
- Agree shared-file and contract ownership before parallel edits; assign one
  integration owner for overlaps. Exchange exact commits, not competing
  snapshots. Serialize merges and refresh against each preceding merge;
  parallel implementation never permits concurrent integration.
- Do not force-push, rewrite shared history, amend without permission, or delete
  branches/worktrees containing unmerged or unrelated work.

## Verify changes through Martlet MCP

Every new feature or behavior change must be shown working on the current dev
machine through Martlet's own MCP server (`src\Martlet.Mcp`) before merge,
whenever this machine can exercise it. This is the one required local check.
See [Verifying changes with Martlet MCP](docs/MCP.md#verifying-changes-with-martlet-mcp).

- Build what the change needs, then drive the actual feature with
  `scripts\Invoke-MartletMcp.ps1`: Doctor tools for headless behavior and
  `-Desktop` for UI behavior, always on a disposable data directory, never the
  real profile. Check the expected outcome (status values, control state,
  Doctor report), not merely that a call returned.
- **Always extend the MCP server with the feature.** In the same change, make
  everything new reachable and observable through MCP: stable automation IDs
  on new controls, passive navigation in `SafeClicks`, non-secret status
  fields in `SafeValues`, and new or extended tools (or Doctor probes) for new
  headless capabilities, with [MCP](docs/MCP.md) updated to match. A feature
  MCP cannot reach or observe is not finished.
- `--allow-ui-effects` is allowed for this verification only with a disposable
  data directory and no real credentials. It never authorizes spending, real
  provider requests, credential handling, audio capture/playback or data
  disclosure; stop at those steps.
- When full verification is impossible here (locked or headless desktop,
  missing hardware, credentials or paid services, another OS), verify
  everything reachable up to that boundary and report the rest as **NOT RUN**
  with the exact reason. Never claim unrun or partial verification passed.
  Documentation-only changes need no verification.
- State in the PR description what was verified through MCP and what was not.

## Prototype speed: no other local gates

Martlet is developed at prototype speed (a development pace, not a release
label). Speed matters more than gates. Apart from MCP verification above,
local test suites, build/package/smoke gates and independent review agents
are **not required** and should not be run by default. Build what MCP
verification needs; otherwise run at most a quick, targeted build or test
when it directly helps you finish or debug the change. Do not add
test-coverage, review or evidence-recording steps to satisfy process.

Keep changes focused and surface errors instead of silently falling back.
Never claim a check passed that you did not run, and never present fixture,
fake-native or historical results as real-device, model or release
qualification.

## Publication and merge

Follow the [validation policy](README.md#local-only-validation-policy)
and [delivery protocol](docs/DELIVERY.md). Before each push, PR creation/update
or merge, check the applicable workflow triggers so publishing starts no remote
validation, including self-hosted Actions. If no safe path exists, retain the
local work and report the exact blocker.

Publish only the task's changes to the repository's configured remote. Use a
focused PR targeting `main` with a short description. Refresh `origin/main`
before merging and reconcile if it advanced. Merge eligible PRs one at a time
through the normal GitHub path. Never bypass protections, dismiss required
review, fabricate check statuses or merge a held PR.

Automatic completion means the agent performs the normal eligible PR merge;
it does not mean enabling repository-wide auto-merge, changing repository
settings or adding background/remote automation. If GitHub queues a merge,
the task remains pending until the merged result is verified.

Verify the PR is merged and its result is present on freshly fetched
`origin/main`; a successful push or pending auto-merge is not completion.
Report integrated delivery versus local/open/blocked work accurately.

## Authorization boundaries

Autonomous delivery does not authorize spending, sensitive-data disclosure,
production deployment, destructive actions, installing host services/drivers,
or changing permissions, protections or billing.
Do not create, enable, expand, dispatch or retry remote validation CI.

## No Copilot Cloud or GitHub Actions minutes

- Never use Copilot Cloud: do not start, delegate to, assign issues to or
  request work from the Copilot cloud/coding agent, cloud sessions, Copilot
  code review or any other GitHub-hosted agent. All agent work runs locally.
- Never consume GitHub Actions minutes, apart from building a release with
  `windows-release.yml`. Do not add, enable, dispatch or rerun any other
  workflow, add `copilot-setup-steps.yml`, or use hosted or self-hosted
  runners for builds, tests, validation, agents or automation.
- Releasing is the only exception: dispatch `windows-release.yml` only to
  publish an actual release, at most once per version unless a build failure
  has been fixed.

**Releases are pre-authorized.** Agents may create and maintain a minimal
build/package/release workflow (manual dispatch or tag/release trigger only),
dispatch it and publish GitHub releases without asking. It contains no tests or
validation, and releases are never cut merely to obtain validation evidence.

To release: bump `<Version>` in `Directory.Build.props` through a normal merged
PR, then run
`gh workflow run windows-release.yml --ref main -f version=<that version>`
and confirm the release is published as Latest on GitHub. If the build fails,
fix the cause before dispatching again.

Code signing is not required. Martlet is a personal project and publishes
unsigned installers as actual, normal releases; do not label them prototype
builds or prereleases, and do not buy or require a signing certificate. Never
claim an unsigned build is signed or suppress Windows security warnings; a
GitHub asset digest checks integrity, not publisher identity. Retain explicit
user consent for download and installation, the narrow official-binary use
rights and third-party notices. Keep the private V07 signed-candidate library's
trust contract separate from this GitHub installer delivery route.
