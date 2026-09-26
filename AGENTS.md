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

## Prototype speed: no local gates

Martlet is a prototype. Speed matters more than gates. Local test suites,
build/package/smoke gates and independent review agents are **not required**
and should not be run by default. Run at most a quick, targeted build or test
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
production deployment, release publication, destructive actions, installing
host services/drivers, or changing permissions, protections or billing.
Never create, enable, expand, dispatch or retry GitHub CI pipelines (or other
remote validation), including PR, push, scheduled and self-hosted jobs. Free
GitHub Actions minutes are reserved exclusively for an explicitly requested,
minimal **manual release build/package** on GitHub: only necessary restores,
compilation and packaging. No tests, lint, scans, smoke checks, qualification
or disguised validation in that workflow. Run the release workflow, upload
artifacts to a public Release or publish a release only when the owner
explicitly asks for a release; never add automatic release triggers.

The owner has chosen an **unsigned, clearly disclosed hobby release**; do not
make code signing a prerequisite for the official Windows installer or add a
signing-cost dependency. Never mislabel an unsigned build as signed or suppress
Windows security warnings. An unsigned GitHub asset digest checks integrity
against the GitHub metadata, not independent publisher identity. Retain explicit
user consent for download and installation, narrow official-binary use rights
and third-party notices. Keep the private V07 signed-candidate library's trust
contract separate from this GitHub installer delivery route.
