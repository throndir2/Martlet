# GitHub Wiki mirror

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against 5900f68ee
**Code:** `scripts/kb/publish_wiki.py`, `scripts/kb/test_publish_wiki.py`

## Source and output

The files under `docs/` are the source for this mirror. Do not edit mirrored
pages in the wiki browser.

The script fetches the current default branch from `origin`. It reads source
files from a new detached worktree of that branch.

The script flattens Markdown page names. It keeps current `docs/wiki/` page
names when possible. It gives a root documentation page a `docs-` prefix when
its name conflicts with a wiki page. It stops if other names still conflict.

The script rewrites relative links to flattened wiki pages. It keeps heading
anchors. It links repository files outside `docs/` to GitHub source pages. It
copies non-Markdown files under `docs/` to the wiki's `assets/` folder.

The script checks every absolute wiki page and heading link in the repository
root `README.md`. It stops if the mirror would remove one of those targets.

The script builds `Home.md` and `_Sidebar.md`. It uses
`docs/wiki/Home.md` as the home page when that file exists. It adds the
documentation map and a list of mirrored pages. Otherwise, it uses
`docs/README.md` as the home page.

The sync check ignores line-ending differences in Markdown pages.

Each publish removes root Markdown pages that are no longer in `docs/`. It
also removes old mirrored files from the wiki's `assets/` folder.

The older `scripts/Publish-Wiki.ps1` remains available for compatibility. It
publishes only `docs/wiki/` from the current checkout and can replace pages
from this mirror. Use one publisher at a time.

## Run

Run a dry run first:

```powershell
python .\scripts\kb\publish_wiki.py --dry-run
```

The dry run fetches the source branch and reads the wiki. It does not commit or
push changes.

Publish the mirror:

```powershell
python .\scripts\kb\publish_wiki.py
```

The publish command commits and pushes changes to the GitHub Wiki repository.

Run the focused tests:

```powershell
python .\scripts\kb\test_publish_wiki.py
```
