# Repository knowledge base

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against fc325668d
**Code:** `.github/mcp.json`

The knowledge base (KB) is the Markdown documentation in `docs/`.
It describes the current code. The [top-level map](../README.md) gives the
main areas. The [decision map](../DECISION_MAP.md) names the rule owners.
The [glossary](../GLOSSARY.md) gives the names to use.

## Search before reading source

Use the local `repo-kb` MCP server to find a topic. Call `kb_search`,
then open its cited sections with `kb_read_section`. Read the page's
`**Code:**` line to find the source. Read only the source that the task needs.
Use an exact-name search when the page names a symbol but not its file.

If a page is missing or wrong, inspect the relevant code and correct the
page in the same pull request. Search results are a route to evidence,
not proof that a claim is true.

`.github/mcp.json` starts the existing tool with `npx`. It needs Node.js 20
or later and Git. It does not need a repository package install.
The first run downloads the tool and the local embedding model.
The index stays outside the checkout. The tool selects the index from
the repository's Git remote. Searches combine keywords with local embeddings
when the embeddings are ready. All indexing and search run on this computer.

The default file selection includes `docs/**/*.md`, the root agent and
contributor guides, and the checked-in assistant instructions. Martlet uses
these paths, so it does not need a `.repo-kb.json` override.
`scripts/MartletDev.ps1` supplies developer helper functions; it is not an
installer and has no install option.

## Page contract

Each authored KB page starts with an H1 title and these fields:

```markdown
# Page title

**Status:** Implemented
**Last Updated:** YYYY-MM-DD
**Audience:** Developers and coding agents
**Last verified:** YYYY-MM-DD against <short sha>
**Code:** `src/Area/Owner.cs`, `scripts/Some-Command.ps1`
```

Use repository-relative paths in `Code`. Name the main files or source
folders that implement the page. A folder covers its descendants.
Do not use wildcards, absolute paths, symbols, or missing paths.
Use forward slashes in Markdown paths so links work on all platforms.
Use Windows paths in PowerShell commands.

| Status | Meaning |
| --- | --- |
| Implemented | The described behavior exists in the named code. |
| Partial | Some described behavior exists. Put unbuilt work in a Planned section. |
| Planned | The page describes unbuilt work, not current behavior. |
| Superseded | Another linked page replaces this page. |

Put intended behavior only under a heading that contains `Planned`.
Put past evidence and provenance under clearly named history sections.
Do not describe a past test or a fixture as current product qualification.

`Last Updated` is the date of a content change. `Last verified` records the
code revision inspected for the page's claims. Do not update verification
fields without checking the code. A source-map check alone does not verify
behavior. Documentation-only indexes name the revision used to check their links.
Pages that describe built code must have a `Code` line.

## Folder indexes and generated pages

Each documentation folder has a `README.md` index. List each immediate page
and each immediate subfolder. Give one line that states the question each
page answers. Keep the root map's code areas and folder links current.
Do not require a page to list itself.

Scripts regenerate generated pages. Do not edit their output by hand.
Each generated page names its generator. Generated image folders and wiki
navigation fragments are not authored KB pages.
The current wiki's Markdown source lives in `docs/wiki/`.
`scripts/Publish-Wiki.ps1` publishes that source; the browser copy is not
another source of truth.

## Pull request completion

A change to code, configuration, scripts, commands, or behavior also updates
the matching page, its `Code` and verification fields, and its folder index.
A new feature gets a page or a section. Remove a deleted feature's page
or mark it Superseded and link its replacement.

If no page needs a change, write `KB: no change needed` in the pull request
body and give the reason.

## Planned local checks and evaluation

The local check script will check source paths, folder indexes, and changed
source coverage. It will not prove that every behavioral claim is correct.
The evaluation file will contain real repository questions and expected pages.
Record measured retrieval results here after running the evaluation.
