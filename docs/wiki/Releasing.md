# Releasing

Martlet releases are normal GitHub releases with unsigned Windows installers.

## Policy

Remote Actions are used only for the minimal build/package/release workflow. It runs no tests, lint, scans or validation. Validate locally before the version PR merges.

## Version bump

Bump `<Version>` in `Directory.Build.props` through a normal PR and merge it to `main`.

## Dispatch

```powershell
gh workflow run windows-release.yml --ref main -f version=<v>
```

The input must match `<Version>` on `main` and the tag `v<version>` must not already exist.

## Workflow summary

The workflow checks version and tag absence, restores/compiles/packages once, builds `Martlet-<version>-win-x64.exe`, checks receipt/SHA-256, creates `v<version>`, uploads to a draft release, verifies uploaded asset digest and publishes as latest normal release.

If it fails after tag creation, inspect and delete the tag/draft before dispatching again. Never move a release tag.

## Wording

The installer is unsigned. Windows may warn about unknown publisher. A GitHub digest checks integrity, not publisher identity. Never call it signed or tell users to disable Windows security.

More detail: [Windows packaging release](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md#public-release-build-manual-dispatch), [Delivery](https://github.com/throndir2/Martlet/blob/main/docs/DELIVERY.md), [Contributing policy](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md#local-only-validation-policy).
