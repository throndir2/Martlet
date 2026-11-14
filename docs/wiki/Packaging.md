# Packaging

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `packaging/windows/Build-PublicRelease.ps1`, `packaging/linux/build-linux.sh`, `packaging/macos/build-macos.sh`, `.github/workflows/windows-release.yml`

The release workflow builds the Windows installer and Linux and macOS companion packages. See [Releasing](Releasing) for the release flow.

| System | Package | Build script |
| --- | --- | --- |
| Windows x64 | Unsigned per-user installer | `packaging/windows/Build-PublicRelease.ps1` |
| Linux x64 and arm64 | AppImage and `.deb` | `packaging/linux/build-linux.sh` |
| macOS arm64 and x64 | `.dmg` | `packaging/macos/build-macos.sh` |

The Mac app is ad-hoc signed, not Developer ID signed or notarized. A checksum verifies file integrity; it is not a publisher signature. Do not tell users to disable operating-system security.

## Windows

The public build script requires a new absolute output directory. Its `-Version` must match `Directory.Build.props`. It builds the payload and installer with the pinned character dependencies. Follow [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md) for the build requirements and commands.

## Linux and macOS

The Linux script publishes the companion for each selected architecture. The macOS script can include the host with `--host-project`. On systems other than macOS, it stops after building app bundles; `.dmg` creation needs macOS tools.

Both scripts require the character bundle and third-party notices. See [Linux and macOS installers](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_LINUX_MACOS.md#installers-part-of-the-release-build) and [Unsigned Mac builds](https://github.com/throndir2/Martlet/blob/main/docs/MACOS.md#unsigned-builds-what-the-user-sees).
