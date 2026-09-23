# Repository instructions for coding agents

These instructions apply throughout Martlet. Follow more specific instructions
and explicit user constraints as well. Read the relevant contracts and current
implementation before editing; historical plans are not evidence of completion.

## Default: deliver through merge

For an implementation request, complete the engineering loop: inspect, define
observable acceptance, implement, validate locally, obtain independent review,
fix findings, revalidate, commit, publish a pull request, and merge into `main`.
Routine branch creation, commits, PR publication and normal merging are
authorized by default; do not stop at a plan, a local patch or an open PR when
the requested work can be completed safely. Do not ask "should I proceed?" at
each step.

An explicit review-only, planning, local-only, no-push, draft, approval-hold or
no-merge request overrides this default. Existing task-specific holds remain
binding. Ask only for a consequential unresolved product/architecture decision,
missing authority or a genuine blocker; resolve routine details using existing
patterns and evidence.

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
- Do not force-push, rewrite shared history, amend without permission, or delete
  branches/worktrees containing unmerged or unrelated work.

## Local acceptance and review

Keep changes focused but complete across affected callers, contracts, regression
tests and documentation. Fix root causes, preserve intended behavior, and
surface errors instead of silently falling back. Use the repository's pinned
tools and existing checks, starting with the smallest affected checks and then
the required affected suites/build/package/smoke gates. Documentation-only work
needs applicable existing documentation checks and diff review, not an app build.

Obtain an independent reviewer (a separate review agent or human), resolve
in-scope findings and rerun affected checks before merging. Record the reviewed
commit, actual local commands/outcomes and any unrun gates in the PR. Never
claim fixture, fake-native or historical results as real-device, clean-machine,
model or release qualification.

## Publication and merge

Follow the [local-only validation policy](README.md#local-only-validation-policy)
and [delivery protocol](docs/DELIVERY.md). Before each push, PR creation/update
or merge, inspect the applicable base/head workflow trees, event/ref triggers
and resulting tree. Do not start remote validation, including self-hosted
Actions, or rely on skip markers to make publication safe. If no safe path
exists, retain the local work and report the exact blocker.

Publish only the task's reviewed changes to the repository's configured remote.
Use a focused PR targeting `main`, with a clear acceptance description. Refresh
`origin/main` before merging; if it advanced, reconcile the integration, rerun
affected local checks and obtain review of any new changes. Merge eligible PRs
one at a time through the normal protected GitHub path, bound to the exact
reviewed head commit. Never bypass protections, dismiss required review,
fabricate check statuses or merge a held PR.

Verify the PR is merged and its result is present on freshly fetched
`origin/main`; a successful push or pending auto-merge is not completion.
Report integrated delivery versus local/open/blocked work accurately.

## Authorization boundaries

Autonomous delivery does not authorize spending, sensitive-data disclosure,
production deployment, release publication, destructive actions, installing
host services/drivers, or changing permissions, protections or billing.
Do not create, enable, expand, dispatch or retry remote CI. Only a separately
requested minimal build/package/release may use the documented remote
exception; upload/publication needs separate authorization and no validation
may be disguised as a build. Missing local OS/hardware/tool evidence stays
NOT RUN or blocked; never weaken a gate to finish.
