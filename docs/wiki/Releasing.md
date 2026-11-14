# Releasing

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `.github/workflows/windows-release.yml`, `scripts/Get-ReleaseNotes.ps1`, `scripts/Update-ModelCatalog.ps1`, `Directory.Build.props`

Martlet publishes a normal GitHub release with an unsigned Windows installer and Linux and macOS companion packages. The [release instructions](https://github.com/throndir2/Martlet/blob/main/AGENTS.md#changelog-and-release-notes) are the source for release preparation.

## Prepare a release

Add user-visible changes to the Unreleased section of [CHANGELOG.md](https://github.com/throndir2/Martlet/blob/main/CHANGELOG.md). In the release pull request, update the version, date the changelog section, refresh the README highlights and refresh the model catalog. Check the notes with `scripts/Get-ReleaseNotes.ps1`. The [release instructions](https://github.com/throndir2/Martlet/blob/main/AGENTS.md#changelog-and-release-notes) give the full order and version rules.

Validate locally before merging. See [Validation and Testing](Validation-and-Testing). Do not use hosted workflows for validation or create a release to obtain test evidence.

## Release workflow

`windows-release.yml` has a manual dispatch trigger. The requested version must match `Directory.Build.props`, and the version tag must not exist. The workflow checks the changelog section and the current main commit.

The Windows job creates a draft release. Linux and macOS jobs build the companion packages. The attach job adds those packages, checks asset digests, and publishes the release as Latest. Verify the release and its notes after the run. See [Windows packaging release](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md#public-release-build-manual-dispatch) for the command and failure guidance.

The Windows installer is unsigned. The Mac app is not notarized. Asset digests check integrity, not publisher identity. See [Packaging](Packaging).
