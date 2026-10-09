# Cleaning up disk space

Martlet builds, tests and agent sessions make a large quantity of files that Git
ignores. Each Copilot worktree (a separate checkout of a task branch) gets its
own copy. This page tells you what Martlet makes, where, how large it gets, and
how to remove it safely.

Measured on 2026-10-09 on the developer PC with 159 worktrees:

- Each worktree grew to 2.7-4.5 GB and 9,000-11,700 files.
- Across all worktrees: `bin\` 145 GB, `obj\` 12.5 GB and `node_modules\`
  9.8 GB (about 297,000 files, most in `src\Martlet.Avatar.Vrm\node_modules\`).
- The shared Git folder had about 26,000 loose objects, more than 300
  `[branch]` sections in its config, about 111 local-only branches, 147
  registered worktrees (many for archived sessions) and stale `.lock` files.
- Many small files made new worktree checkouts very slow, and archiving
  sessions timed out.

## Safety rules

- Never delete tracked files, uncommitted changes or commits that are not on
  `origin/main` or another remote branch.
- Delete only output that a build, `npm ci` or a test run makes again.
- Agents clean only their own checkout. Never clean another session's worktree
  or the main checkout unless the developer asks.
- Do not delete `src\Martlet.Avatar.Live2D\local-sdk\` or `vendor\`. Git ignores
  them, but they hold Live2D SDK files that you put there. A build cannot make
  them again.
- Do not delete `.vs\`, `*.user`, `~\.martlet-dev\` (the developer profile),
  `~\.nuget\packages\` (the shared NuGet cache) or Martlet user data.

## What Martlet makes, and where

| What | Where | Typical size | Made by | Safe to delete |
| --- | --- | --- | --- | --- |
| .NET build output | `bin\` in every project under `src\` and `tests\` | 145 GB across 159 worktrees (average 0.9 GB each) | `dotnet build`, `Test-Martlet.ps1`, `Invoke-MartletMcp.ps1 -Build` | Yes. The next build makes it again. |
| .NET intermediate output | `obj\` in every project | 12.5 GB across 159 worktrees (average 80 MB each) | The same builds and restores | Yes. The next restore and build make it again. |
| npm packages | `src\Martlet.Avatar.Vrm\node_modules\`, and `src\Martlet.Avatar.Live2D\node_modules\` when you installed it | 9.8 GB and about 297,000 files across 159 worktrees | `npm ci`, run by the Companion and Desktop builds and by `Test-Martlet.ps1` | Yes. `npm ci` restores it from `package-lock.json` (needs the network). |
| Web bundles | `dist\` and `dev-dist\` in the avatar packages, `src\Martlet.Avatar.RendererHost\web\dist\` and `web\live2d-dist\` | Small | The avatar builds | Yes. |
| Validation output | `artifacts\validation\` (`summary.md`, logs, TRX files, Linux test staging) | Small to some MB | `Test-Martlet.ps1` | Yes, after you copy `summary.md` into the pull request. |
| Test results and Python caches | `TestResults\`, `__pycache__\` | Small | `dotnet test`, worker tests | Yes. |
| Disposable data folders | `%TEMP%\Martlet.<name>.<guid>` and `%TEMP%\martlet-<name>-<guid>` (for example `Martlet.Mcp.Verify.<guid>`) | Small each, but hundreds collect | `Invoke-MartletMcp.ps1` (kept by `-KeepDesktop` or a stopped run), `Smoke-Doctor.ps1`, tests, `Martlet.Dev.<guid>` from the quick start | Yes, when they are older than one day. |
| Git data | The shared Git folder of the repository (`git rev-parse --git-common-dir` shows it). All worktrees use it. | Loose objects, branches, config sections, worktree registrations, `.lock` files | Every session, fetch, commit and worktree | See [Git housekeeping](#git-housekeeping). |

The Docker volume `martlet-validation-nuget` is a NuGet cache that the Linux
tests use again on each run. Keep it. Linux test containers use `--rm`, so
Docker removes their other volumes.

## When to clean

- **Agents:** before you finish a session, after the pull request is merged or
  after you copied `artifacts\validation\summary.md` into it.
- **Developers:** when free disk space gets low, or when new worktree checkouts
  or session archiving become slow.
- **Git housekeeping:** when `git count-objects -v` shows more than a few
  thousand loose objects (`count`), when `git worktree list` shows many
  `prunable` entries, when the Git config has hundreds of `[branch]` sections,
  or when Git stops because a `.lock` file exists and no Git process runs.

## Clean with the script

`scripts\Clean-Martlet.ps1` removes only folders that Git lists as ignored and
untracked and that have a generated name. It never deletes tracked files,
untracked source files or `local-sdk\` and `vendor\`.

1. Show what the script would remove, with sizes. This changes nothing:

   ```powershell
   .\scripts\Clean-Martlet.ps1 -WhatIf
   ```

2. Remove the build output of this checkout and old disposable `%TEMP%`
   folders:

   ```powershell
   .\scripts\Clean-Martlet.ps1
   ```

3. Developer only: show, then do, the Git housekeeping on the shared Git
   folder:

   ```powershell
   .\scripts\Clean-Martlet.ps1 -Git -WhatIf
   .\scripts\Clean-Martlet.ps1 -Git
   ```

4. Developer only: clean the build output of every worktree of the repository:

   ```powershell
   .\scripts\Clean-Martlet.ps1 -AllWorktrees -WhatIf
   .\scripts\Clean-Martlet.ps1 -AllWorktrees
   ```

| Parameter | What it does |
| --- | --- |
| `-WhatIf` | Shows what the script would remove and changes nothing. |
| `-Git` | Does the [Git housekeeping](#git-housekeeping). |
| `-AllWorktrees` | Cleans every worktree of the repository, including the main checkout, not only this checkout. |
| `-KeepNodeModules` | Keeps `node_modules\`, so the next build does not run `npm ci` again. |
| `-SkipTemp` | Does not touch `%TEMP%`. |
| `-TempAgeHours` | Minimum age of a `%TEMP%` folder before the script removes it. The default is 24. |
| `-LockAgeMinutes` | Minimum age of a Git `.lock` file before the script deletes it. The default is 60. |

If a file is in use, the script keeps it and says "partly removed". Stop the
Martlet processes that run from that folder (Desktop, MCP server, worker hosts),
then run the script again. The script does not stop shared build servers,
because other worktrees can use them.

## Git housekeeping

`-Git` does these steps on the shared Git folder:

1. `git worktree prune` removes the registrations of worktrees whose folder is
   gone.
2. `git fetch origin --prune` removes remote-tracking branches that were
   deleted on GitHub.
3. It deletes local branches that are fully merged into `origin/main` and that
   no worktree has checked out. It prints each deleted branch with its commit.
   It keeps every branch that is not merged.
4. It removes `[branch]` config sections for branches that do not exist.
5. It deletes `.lock` files older than one hour. A Git process that stopped
   left them.
6. `git gc` packs loose objects. It keeps unreachable objects for the default
   two weeks, so it is safe while other sessions use the repository.

To restore a deleted branch, use the commit that the script printed:
`git branch <name> <commit>`.

## Clean by hand

Use these commands when you cannot use the script. Run them in the checkout
that you clean.

```powershell
# List the ignored folders in this checkout. Delete only generated ones.
git ls-files --others --ignored --exclude-standard --directory
Remove-Item -Recurse -Force src\Martlet.Avatar.Vrm\node_modules

# Git housekeeping
git worktree prune --verbose
git fetch origin --prune
git branch --merged origin/main   # "+" or "*" marks a branch checked out in a worktree: keep it and main
git branch -D <branch>            # only for a branch that the line above lists
git count-objects -vH
git gc
```

To remove the worktree of a finished session, archive the session in the
Copilot app. The app removes the worktree. To remove a worktree by hand, first
make sure that `git -C <path> status --short` shows nothing and that its
commits are merged or pushed. Then run `git worktree remove <path>`.

Do not use these commands:

- `git clean -x` or `git clean -X` without a review. They also delete
  `local-sdk\`, `vendor\`, `.vs\` and `*.user`.
- `git gc --prune=now` while other sessions work. It can delete objects that a
  running Git command just wrote.
- `git branch -D` on a branch that is not merged. It can lose work.
- `git worktree remove --force` on a worktree with changes.
- Deleting a `.lock` file while a Git process uses it.
