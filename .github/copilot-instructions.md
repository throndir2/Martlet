# Martlet Copilot instructions

Read and follow [AGENTS.md](../AGENTS.md) and the
[full engineering rules](../docs/kb/engineering-rules.md).
The full rules retain the repository's existing policies. This bootstrap
does not replace authorization boundaries, explicit holds, or merge protections.

## Knowledge Base First (Required)

Always use KB search when you must find, search, or navigate the repository.
Call `kb_search` on the local `repo-kb` MCP server. Open cited sections with
`kb_read_section`. Follow their `**Code:**` lines, then read only the code
that the task needs.

Do not explore with broad grep, glob, directory listings, or file reads.
Use them only for an exact name that the KB gave you.
If the KB has no answer or a wrong answer, inspect the code and fix the
page in the same pull request.

## Keep the KB true (Required)

Changes to code, configuration, scripts, commands, or behavior also update
the matching KB page, its `**Code:**` and `**Last verified:**` lines,
and its folder index. New features get a page or section. Deleted features
lose their page or become Superseded.

If no page needs a change, write `KB: no change needed` in the pull request
body and give the reason.
Run `python scripts\kb\check_kb.py --diff origin/main` before merge.
Fix every error and review coverage warnings.

## Repository rules and map

Work autonomously through an eligible, locally validated merge.
Use the assigned worktree. Preserve unrelated work.
Never use Copilot Cloud or hosted validation. Never bypass consent.
Follow the full rules for targeted tests, Martlet MCP verification, shared
settings, issue filing, changelog entries, releases, and checkout cleanup.

Start at [docs/README.md](../docs/README.md).
Use the [decision map](../docs/DECISION_MAP.md) to find rule owners and the
[page contract](../docs/kb/knowledge-base.md) to maintain current evidence.
