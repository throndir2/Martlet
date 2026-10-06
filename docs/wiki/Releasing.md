# Releasing

Martlet releases are normal GitHub releases with unsigned Windows installers.

## Policy

Remote Actions are used only for the minimal build/package/release workflow. It runs no tests, lint, scans or validation. Validate locally before the version PR merges.

## Changelog during development

Every PR with a change users can notice adds a plain-language line with its PR link under `## [Unreleased]` in [`CHANGELOG.md`](https://github.com/throndir2/Martlet/blob/main/CHANGELOG.md), in `### Added`, `### Changed`, `### Fixed` or `### Removed`.

## Release PR

One normal PR titled `Release <version>`:

1. Pick the version: minor when `Unreleased` has anything under Added or Removed, otherwise patch.
2. Bump `<Version>` in `Directory.Build.props`.
3. In `CHANGELOG.md`, rename `## [Unreleased]` to `## [<version>] - <YYYY-MM-DD>` and add a new empty `## [Unreleased]` above it.
4. In `README.md`, update the *What's new in <version>* heading and its three to five highlight bullets. The badges update themselves.
5. Check: `.\scripts\Get-ReleaseNotes.ps1 -Version <version>` prints the section.

## Dispatch

```powershell
gh workflow run windows-release.yml --ref main -f version=<v>
```

The input must match `<Version>` on `main`, the tag `v<version>` must not already exist, and `CHANGELOG.md` must have a section for that version.

## Workflow summary

The workflow checks version, release notes and tag absence, restores/compiles/packages once, builds `Martlet-<version>-win-x64.exe`, checks receipt/SHA-256, creates `v<version>`, uploads to a draft release with the changelog section as *What's new*, verifies uploaded asset digest and publishes as latest normal release.

To correct published notes, fix `CHANGELOG.md` in a PR and run `gh release edit v<version> --notes-file <file>`; never re-dispatch for notes.

If it fails after tag creation, inspect and delete the tag/draft before dispatching again. Never move a release tag.

## Wording

The installer is unsigned. Windows may warn about unknown publisher. A GitHub digest checks integrity, not publisher identity. Never call it signed or tell users to disable Windows security.

More detail: [Windows packaging release](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md#public-release-build-manual-dispatch), [Delivery](https://github.com/throndir2/Martlet/blob/main/docs/DELIVERY.md), [Contributing policy](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md#local-only-validation-policy).
