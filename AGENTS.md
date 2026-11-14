# Repository instructions for coding agents

Read and follow the [full engineering rules](docs/kb/engineering-rules.md).
They retain the repository's authorization, ownership, validation, release,
and cleanup rules. This file is the short bootstrap, not a second policy.

## Knowledge Base First (Required)

Always use KB search when you must find, search, or navigate the repository.
Call `kb_search` on the local `repo-kb` MCP server. Open cited sections with
`kb_read_section`. Follow their `**Code:**` lines, then read only the source
that the task needs.

Do not start with broad grep, glob, directory listings, or file reads.
Use those tools only for an exact name that the KB gave you.
If the KB has no answer or a wrong answer, find the evidence in the code.
Fix the page in the same pull request.

## Keep the KB true (Required)

A pull request that adds, renames, moves, or deletes code, configuration,
scripts, commands, or behavior updates the KB in the same pull request.
Update the page, its `**Code:**` and `**Last verified:**` lines, and the
folder index. A new feature gets a page or section. Remove a deleted feature's
page or mark it Superseded.

If no documentation needs a change, write `KB: no change needed` in the
pull request body and give the reason.
Run `python scripts\kb\check_kb.py --diff origin/main` before merge.
Fix every error and review every coverage warning.

## Short map

- [Knowledge map](docs/README.md): Find the main systems and documentation folders.
- [Page contract](docs/kb/knowledge-base.md): Write current, verifiable KB pages.
- [Decision map](docs/DECISION_MAP.md): Change a rule at its owner.
- [Glossary](docs/GLOSSARY.md): Use one name for each concept.
- [Full rules](docs/kb/engineering-rules.md): Follow all existing repository policies.

## Default: deliver through merge

Work autonomously on the assigned task branch. Implement, validate locally,
commit, publish a focused pull request, and merge when eligible.
Explicit holds and required permissions still apply.
Do not change workflows or trigger hosted CI for validation.

## Validate before merge

Run the KB check, the required targeted tests, and the applicable Martlet MCP
verification. Documentation-only changes need source and link checks, not
product tests. Report unrun checks honestly.
Follow the [complete validation rules](docs/kb/engineering-rules.md#validate-before-merge).

## Existing policy links

These anchors retain links from earlier instructions and documentation.

- <a id="highest-priority-autonomous-autopilot-style-work"></a>[Autonomous work](docs/kb/engineering-rules.md#highest-priority-autonomous-autopilot-style-work)
- <a id="decisive-autonomous-loop-engineering"></a>[Engineering loop](docs/kb/engineering-rules.md#decisive-autonomous-loop-engineering)
- <a id="file-issues-for-problems-you-find"></a>[File issues](docs/kb/engineering-rules.md#file-issues-for-problems-you-find)
- <a id="write-in-simplified-technical-english"></a>[Technical English](docs/kb/engineering-rules.md#write-in-simplified-technical-english)
- <a id="never-add-conversation-latency"></a>[Conversation latency](docs/kb/engineering-rules.md#never-add-conversation-latency)
- <a id="martlet-is-one-app-configuration-is-shared"></a>[Shared configuration](docs/kb/engineering-rules.md#martlet-is-one-app-configuration-is-shared)
- <a id="branch-and-worktree-ownership"></a>[Worktree ownership](docs/kb/engineering-rules.md#branch-and-worktree-ownership)
- <a id="use-martlet-through-its-mcp-server"></a>[Use Martlet MCP](docs/kb/engineering-rules.md#use-martlet-through-its-mcp-server)
- <a id="publication-and-merge"></a>[Publication and merge](docs/kb/engineering-rules.md#publication-and-merge)
- <a id="clean-up-before-you-finish"></a>[Cleanup](docs/kb/engineering-rules.md#clean-up-before-you-finish)
- <a id="authorization-boundaries"></a>[Authorization](docs/kb/engineering-rules.md#authorization-boundaries)
- <a id="no-copilot-cloud-or-github-actions-minutes"></a>[No cloud or CI](docs/kb/engineering-rules.md#no-copilot-cloud-or-github-actions-minutes)
- <a id="changelog-and-release-notes"></a>[Changelog and releases](docs/kb/engineering-rules.md#changelog-and-release-notes)
