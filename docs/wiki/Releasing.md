# Releasing

Martlet releases are normal GitHub releases. Each release has the unsigned Windows installer and the Linux and macOS packages of the early desktop companion. All files go on the same `v<version>` release.

## Policy

- GitHub Actions run only the minimal build, package and release workflow, `windows-release.yml`. It runs no tests, lint, scans or other validation.
- Validate the changes locally before the release pull request merges. See [Validation and Testing](Validation-and-Testing).
- Dispatch the workflow at most one time for each version, unless you fixed the cause of a failed build.
- Do not cut a release only to get validation evidence.

## Changelog during development

Each pull request with a change that users can notice adds one line under `## [Unreleased]` in [`CHANGELOG.md`](https://github.com/throndir2/Martlet/blob/main/CHANGELOG.md). Put it in `### Added`, `### Changed`, `### Fixed` or `### Removed`, in that order. Write one plain sentence about what users notice, and end it with the pull request link. Internal work (tests, refactors, developer docs) needs no line.

## The release pull request

Make one normal pull request with the title `Release <version>`:

1. Pick the version. Use a minor version (for example `0.49.0`) when `Unreleased` has an entry under Added or Removed. Otherwise, use a patch version (for example `0.48.1`).
2. Set `<Version>` in `Directory.Build.props`.
3. In `CHANGELOG.md`, rename `## [Unreleased]` to `## [<version>] - <YYYY-MM-DD>` with today's date. Add a new, empty `## [Unreleased]` above it. Tidy the entries. The section must have at least one `- ` line.
4. In `README.md`, update the *What's new in <version>* heading and its three to five highlight lines, with no pull request links. The download and version badges update themselves.
5. Check the notes: `.\scripts\Get-ReleaseNotes.ps1 -Version <version>` must print the section.
6. Merge the pull request.

## Dispatch the release

```powershell
gh workflow run windows-release.yml --ref main -f version=<version>
```

The input must be equal to `<Version>` in `Directory.Build.props` on `main`. The tag `v<version>` must not exist yet, and `CHANGELOG.md` must have a section for that version.

## What the workflow does

The workflow runs only from `main` of the public repository. It has four jobs:

1. **release** (Windows): checks the version, the release notes, the dispatched `main` commit and that the tag is absent. Then it restores, compiles and packages one time, checks the installer against its receipt and SHA-256, and creates the tag `v<version>` at the built commit. It uploads the installer, `installer-manifest.json` and `SHA256SUMS.txt` to a draft release, with the changelog section as *What's new*. It publishes the release as Latest only when GitHub reports the expected SHA-256.
2. **linux**: builds the x64 and arm64 AppImage and `.deb` files.
3. **macos**: builds the Apple silicon and Intel `.dmg` files, with the Mac host inside the app.
4. **attach**: waits for the other jobs, uploads the six Linux and macOS files to the release, checks their SHA-256 digests on GitHub and adds the Linux and macOS install text to the notes.

After the workflow finishes, confirm on GitHub that `v<version>` is Latest and that its notes show the changelog entries.

## When something goes wrong

- **Wrong notes:** fix `CHANGELOG.md` in a normal pull request. Then run `gh release edit v<version> --notes-file <file>`. Never dispatch the workflow again only for the notes.
- **Failed build:** fix the cause first. If it failed after the tag was created, inspect and delete the tag and the draft release before you dispatch again. Never move a release tag.

## Wording

The Windows installer is not code-signed, and Windows may warn about an unknown publisher. The Mac app is not notarized, so users choose **Open Anyway** one time. A GitHub asset digest checks that the download is complete. It does not prove who published it. Never call a package signed, and never tell users to turn off Windows or macOS security.

More detail: [Changelog and release notes](https://github.com/throndir2/Martlet/blob/main/AGENTS.md#changelog-and-release-notes), [Windows packaging release](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md#public-release-build-manual-dispatch), [Linux and macOS installers](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_LINUX_MACOS.md#installers-part-of-the-release-build), [Packaging](Packaging), [Local-only validation policy](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md#local-only-validation-policy).
