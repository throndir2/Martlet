# Martlet Copilot instructions

Read and follow [the repository agent instructions](../AGENTS.md) for every
task. They are the canonical autonomous engineering, parallel ownership,
prototype-speed and serial merge policy.

**Highest priority: work autonomously, autopilot style.** Make the decisions,
do the work and finish it end to end without routine confirmation, plan
approval or choice questions. Stop only for a genuine blocker or an action that
needs explicit authorization.

Martlet is a prototype: local test suites, build/package/smoke gates and
independent review agents are not required and should not be run by default.
Use at most a quick targeted build or test when it directly helps finish the
change, and never claim an unrun check passed.

For implementation work, reuse the assigned worktree branch, commit, publish a
PR and merge into `main` without routine approval prompts. Explicit user holds,
required protections and authorization boundaries still apply. Never trigger
remote validation; check workflow triggers before publication and merge, and
confirm the merged result on `origin/main`. Deliver actual production paths
early, replace obsolete designs when warranted and minimize narration.
Parallelize independent work with isolated ownership. Safety, consent and
honest reporting remain mandatory.

Never create GitHub CI pipelines: free Actions minutes are reserved solely
for separately requested minimal manual release builds. No PR, push, schedule
or automatic release triggers, hosted tests or disguised validation. Release
uploads/publication need explicit owner authorization.
The owner explicitly permits a clearly labeled unsigned hobby installer;
do not require purchased code signing or misrepresent a GitHub digest as a
publisher signature. Preserve user consent for downloads and installation.
