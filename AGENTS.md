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

For an implementation request: inspect, implement, validate, commit, publish a
pull request and merge into `main`. Routine branch creation, commits, PR
publication and normal merging are authorized by default once the change is
validated; do not stop at a plan, a local patch or an open PR when the
requested work can be completed safely. Do not ask "should I proceed?" at each
step.

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
- Batch relevant reads and avoid duplicate investigation. When work changes
  code or files, distinguish local work, open review and verified
  integration; completion requires a working persistent result, not a
  proposal or queued merge.

## Write in Simplified Technical English

Write responses, progress updates, PR descriptions and developer
documentation in ASD-STE100 Simplified Technical English (STE), so that
technical text is easy to understand. Give as much detail as the reader needs;
do not cut useful information only to be short.

- Use simple, common words, each with only one meaning. Use the same word for
  the same thing every time.
- Keep sentences short: no more than 20 words in an instruction and no more
  than 25 words in a description. Put one instruction in each sentence.
- Use the active voice. Write instructions in the imperative and write
  procedures as numbered steps.
- Keep each paragraph to one topic and no more than six sentences.
- Use a technical term only when it is necessary, and explain it in simple
  words the first time.
- Do not use slang, idioms or unexplained abbreviations.

User-facing text (`CHANGELOG.md`, the README and the app) keeps its own plain,
friendly voice; apply the STE rules where they do not conflict with it.

## Never add conversation latency

The time from the end of the user's speech (or Send) to Martlet's first
audible word must never grow. Treat it as a hard product goal: any change on
that path (prompt assembly, provider requests, background work after a reply,
speech and playback) must keep it the same or make it shorter, for local and
cloud models alike. Keep the start of every Thinking request stable so prompt
caches (OpenAI, OpenRouter, Ollama) are reused, and never let background work
evict the conversation from a local model's cache. When verifying such a
change, compare the desktop log's `Reply latency` and `Thinking input` lines
before and after, and report the numbers.

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

## Validate before merge

Every change except documentation is validated locally before it merges, by the
flow in [Validating changes](docs/VALIDATION.md). Both parts are required:

- **Targeted tests only:** run just the tests you added or changed and the test
  project or suite that directly covers the changed code:
  `.\scripts\Test-Martlet.ps1 -Project <Project>.Tests -Filter '<your tests>'`
  (`-Project python:<worker>` for a worker suite; `-List` shows which projects
  the change touches directly). Never run the runner's default diff-wide
  selection, `-All` or suites that merely depend on the changed project unless
  the owner asks: many suites fail for unrelated reasons or under parallel
  load, and that noise is not evidence about the change. Name test projects
  exactly; `-Project <Project>` without `.Tests` pulls in every dependent
  suite. When a change alters an API other projects use, build them instead of
  testing them. The targeted tests must pass: fix every failure your change
  causes. A failure it cannot cause (also failing on `origin/main`, or only
  under load and passing alone) does not block; narrow the filter, note it in
  the PR and do not chase it in this change. Add to `tests\known-failures.txt`
  only a test shown failing on `origin/main` without your change (noted in the
  PR and [#332](https://github.com/throndir2/Martlet/issues/332)), never one
  your change breaks, and delete a line when its test passes again. Add or
  update tests for new behavior in the matching `tests\` project. Details:
  [Targeted tests only](docs/VALIDATION.md#targeted-tests-only).
- **Behavior through Martlet MCP:** every feature or behavior change is shown
  working through Martlet's own MCP server (`src\Martlet.Mcp`) on this machine
  whenever it can exercise it. Build what the change needs, then drive the
  actual feature with `scripts\Invoke-MartletMcp.ps1`: Doctor and status tools
  for headless behavior and `-Desktop` for UI behavior, always on a disposable
  data directory, never the real profile. Check the expected outcome (status
  values, control state, Doctor report), not merely that a call returned.
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
- **Run what the tests do not cover** (scripts, packaging, worker hosts,
  workflows) the way it is used; `-List` names such files.
- **Use the developer's validation hosts.** The developer profile
  (`~\.martlet-dev\validation.json` and `NOTES.md`, shared by all of that
  developer's checkouts and worktrees, never committed) lists the machines the
  developer has set up: Docker on this PC, Linux boxes over key-based SSH, GPU
  hosts. Read it before validating and run what needs those machines there; the
  runner already sends targeted Linux-only suites to them. Record useful facts about
  listed hosts in `NOTES.md`. Adding a host, installing anything on one or using
  a password is the developer's decision, not an agent's.
- When full validation is impossible here (locked or headless desktop, missing
  hardware, credentials or paid services, another OS, no suitable host), validate
  everything reachable up to that boundary and report the rest as **NOT RUN**
  with the exact reason. Never claim unrun or partial validation passed.
- Put the runner's `artifacts\validation\summary.md`, the MCP calls and what they
  showed, and every NOT RUN item in the PR description.

Keep changes focused and surface errors instead of silently falling back.
Never present fixture, fake-native or historical results as real-device, model
or release qualification. Independent review agents and package/smoke gates are
optional; use them when the change warrants it.
## Publication and merge

Follow the [validation policy](CONTRIBUTING.md#local-only-validation-policy)
and [delivery protocol](docs/DELIVERY.md). Before each push, PR creation/update
or merge, check the applicable workflow triggers so publishing starts no remote
validation, including self-hosted Actions. If no safe path exists, retain the
local work and report the exact blocker.

Publish only the task's changes to the repository's configured remote. Use a
focused PR targeting `main` with a short description and the validation report.
Every PR with a change users can notice also adds a line under
`## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) (see
[Changelog and release notes](#changelog-and-release-notes)).
Refresh `origin/main` before merging and reconcile if it advanced (rerun the
targeted tests when the reconcile touched the code they cover). Merge eligible PRs
one at a time through the normal GitHub path. Never bypass protections, dismiss
required review, fabricate check statuses or merge a held PR.

Auto-merge is encouraged for autonomous work, **after validation**: once the
targeted tests pass, MCP verification is done and NOT RUN items are reported,
the agent merges its own PR without waiting for approval. A PR whose validation
failed or was skipped is not eligible. Automatic completion means the agent
performs the normal eligible PR merge; it does not mean changing repository
settings or adding background/remote automation. If GitHub queues a merge, the
task remains pending until the merged result is verified.

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

To release, follow [Changelog and release notes](#changelog-and-release-notes):
one normal merged release PR bumps `<Version>` in `Directory.Build.props`,
dates the changelog section and refreshes the README's *What's new*; then run
`gh workflow run windows-release.yml --ref main -f version=<that version>`
and confirm the release is published as Latest on GitHub with its notes. If the
build fails, fix the cause before dispatching again.

Code signing is not required. Martlet is a personal project and publishes
unsigned installers as actual, normal releases; do not label them prototype
builds or prereleases, and do not buy or require a signing certificate. Never
claim an unsigned build is signed or suppress Windows security warnings; a
GitHub asset digest checks integrity, not publisher identity. Retain explicit
user consent for download and installation, the narrow official-binary use
rights and third-party notices. Keep the private V07 signed-candidate library's
trust contract separate from this GitHub installer delivery route.

## Changelog and release notes

[CHANGELOG.md](CHANGELOG.md) is the single source of every release's notes, and
the README's *What's new* is its newest highlights. Both are written for people
who use Martlet, not developers.

**In every PR** with a change users can notice (a feature, a behavior change, a
fix, a removal), add one line under `## [Unreleased]` in the matching
`### Added`, `### Changed`, `### Fixed` or `### Removed` subsection (create it
if missing, in that order). Write one plain sentence about what users notice,
not how it was built, and end it with the PR link, for example
`- The talk window keeps your place when a reply arrives. ([#480](https://github.com/throndir2/Martlet/pull/480))`.
Add the link after opening the PR (or use the PR number GitHub will assign).
Internal-only work (tests, refactors, CI policy, agent instructions,
developer docs) needs no line, or one shared "Behind-the-scenes improvements"
line under `### Changed` when a release would otherwise look empty. Never
rewrite the sections of versions that were already released, except to fix
a wrong statement.

**In the release PR** (title `Release <version>`), in this order:

1. Pick the version: minor (`0.49.0`) when `Unreleased` has anything under
   Added or Removed, otherwise patch (`0.48.1`).
2. Set `<Version>` in `Directory.Build.props`.
3. In `CHANGELOG.md`, rename `## [Unreleased]` to
   `## [<version>] - <YYYY-MM-DD>` (today's date) and add a new empty
   `## [Unreleased]` above it. Tidy the entries: merge duplicates, fix wording,
   keep the newest first. The section must contain at least one `- ` bullet.
4. In `README.md`, replace the *What's new in <version>* section's heading
   version and its three to five highlight bullets with the most notable entries
   of the new section (plain words, no PR links), keeping the links to the
   changelog and releases. The download and version badges update themselves;
   don't hardcode the version anywhere else in the README.
5. Check the notes locally: `.\scripts\Get-ReleaseNotes.ps1 -Version <version>`
   must print the section.
6. Merge the PR, then dispatch `windows-release.yml` with that version. The
   workflow runs the same script before restoring anything, stops if the
   section is missing, and publishes it as the release's *What's new* followed
   by the install, license and SHA-256 text.
7. Confirm on GitHub that `v<version>` is Latest and its notes show the
   changelog entries.

If a published release's notes need correcting, fix `CHANGELOG.md` in a normal
PR and update the release body with `gh release edit v<version> --notes-file`;
never re-dispatch the workflow for notes.
