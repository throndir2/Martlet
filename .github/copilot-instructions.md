# Martlet Copilot instructions

Read and follow [the repository agent instructions](../AGENTS.md) for every
task. They are the canonical loop engineering, branch, worktree, local
acceptance, independent review and merge policy.

For implementation work, use a focused task branch (reuse the assigned
worktree branch), complete the local implement/review/fix loop, and normally
commit, publish a PR and merge into `main` without routine approval prompts.
Explicit user holds, required reviews/protections and authorization boundaries
still apply. Never trigger remote validation; inspect workflow triggers before
publication and merge, and verify the merged result on `origin/main`.
Continue through the next authorized step until the requested outcome is
verified and persistent or a genuine blocker is reported; a plan, local patch
or queued merge alone is not integrated delivery.
