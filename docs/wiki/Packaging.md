# Packaging

Martlet ships three kinds of packages:

| System | Package | Built by |
| --- | --- | --- |
| Windows 10 (version 2004 or later) and 11, x64 | `Martlet-<version>-win-x64.exe`, an unsigned per-user Inno Setup installer | `packaging\windows\Build-PublicRelease.ps1` |
| Linux x64 and arm64 | `Martlet-<version>-linux-<arch>.AppImage` and `martlet_<version>_<amd64 or arm64>.deb` | `packaging\linux\build-linux.sh` |
| macOS 14 or later, Apple silicon and Intel | `Martlet-<version>-macos-arm64.dmg` and `Martlet-<version>-macos-x64.dmg` | `packaging\macos\build-macos.sh` |

The installers are not code-signed. The Mac app is ad-hoc signed only, not Developer ID signed and not notarized. These are normal releases. Never call a package signed, and never tell users to turn off Windows or macOS security.

## The release workflow

`.github\workflows\windows-release.yml` (*Release (manual) - Windows, Linux and macOS*) builds all packages for a release. It has only a manual trigger (`workflow_dispatch`). It has no push, pull request, tag or schedule trigger, and it runs no tests, lint, scans or smoke checks. See [Releasing](Releasing).

## Windows

To build the public installer locally without an upload, run this from the repository root on Windows x64 with PowerShell 7:

```powershell
$sdk = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'   # the exact 10.0.401 SDK
$run = Join-Path $PWD ("artifacts\windows-public-" + [guid]::NewGuid().ToString('N'))
.\packaging\windows\Build-PublicRelease.ps1 -Version 0.1.0 -DotnetPath $sdk -OutputDirectory $run
```

It writes `package\installer\Martlet-<version>-win-x64.exe`, `installer-manifest.json` and `SHA256SUMS.txt`. The public installer has its own AppId and installs to `%LocalAppData%\Programs\Martlet` without administrator rights. User data stays in `%LocalAppData%\Martlet`.

- **Payload.** The Desktop (with its private `AvatarRenderer` runtime) and Doctor each have a complete self-contained runtime. The payload also has help, notices, a CycloneDX SBOM (software bill of materials), `manifest.json` and `SHA256SUMS.txt`. It uses no trimming, single-file packing or ReadyToRun. The SDK is not shipped.
- **Prerequisites tool.** Each installer ships `prerequisites\Install-Prerequisites.ps1` and a **Martlet prerequisites** Start menu shortcut. The installer asks no setup questions. The app asks them, and installs optional items only after the user chooses them.
- **Internal lane.** `Publish-Windows.ps1`, `Get-InnoSetup.ps1`, `Build-Installer.ps1`, `Test-Packaging.ps1` and `Smoke-Package.ps1` make and check a separate internal development installer. Never upload an internal build as a release.
- **Evidence.** The manifests, checksums, provenance and SBOM detect accidental changes. They are not publisher signatures or a vulnerability check.

## Linux

```bash
bash packaging/linux/build-linux.sh --version 0.1.0 --arch x64 --arch arm64 --output artifacts/linux-release
```

It needs bash, curl, sha256sum, file and dpkg-deb on x86_64 or aarch64 Linux (a Linux host, WSL or the .NET SDK image in Docker). Both architectures build on either one. The package is a self-contained publish of `src/Martlet.Companion`. The `.deb` installs to `/opt/martlet` with a `martlet` command, a menu entry and an icon. It depends on WebKitGTK 4.1 (`libwebkit2gtk-4.1-0`) and libsecret. The AppImage uses the distribution's WebKitGTK 4.1, libsecret and libicu.

## macOS

```bash
bash packaging/macos/build-macos.sh --version 0.1.0 --arch arm64 --arch x64 --output artifacts/macos-release \
  --host-project src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj
```

`Martlet.app` holds the companion and, with `--host-project`, the Mac host in `Contents/Resources/host`. The script builds and checks the app bundles on any system with python3. It makes the `.dmg` files only on a Mac, because `codesign` and `hdiutil` exist only there. `--no-dmg` stops after the bundles. On first start, users choose **Open Anyway** one time in **System Settings › Privacy & Security**.

More detail: [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md), [Linux and macOS installers](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_LINUX_MACOS.md#installers-part-of-the-release-build), [Unsigned Mac builds](https://github.com/throndir2/Martlet/blob/main/docs/MACOS.md#unsigned-builds-what-the-user-sees), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Releasing](Releasing).
