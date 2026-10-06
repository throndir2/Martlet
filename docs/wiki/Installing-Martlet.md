# Installing Martlet

Martlet is a per-user Windows app. You do not need administrator rights for the normal install.

## Requirements

| Requirement | Notes |
| --- | --- |
| Windows | Windows 10 version 2004 or later, or Windows 11. x64 only. |
| Installer | `Martlet-<version>-win-x64.exe` from the latest GitHub release. |
| Signature | The installer is **unsigned**. Windows may warn that the publisher is unknown. |
| .NET runtime | Bundled. You do not install .NET separately. |

## Download

1. Open [the latest release](https://github.com/throndir2/Martlet/releases/latest).
2. Download `Martlet-<version>-win-x64.exe`.
3. Run it.
4. Read the installer terms.
5. Leave **Start Martlet and finish setting up** checked if you want to continue.

Unsigned does not mean prototype. It means the release is not code-signed by a publisher certificate; do not claim it is signed.

## First launch

The installer asks no setup questions and installs no optional prerequisites. The welcome wizard asks:

- Whether to **start a new Martlet network** or **join yours** (it finds your other computers on this network), or make this a host PC.
- Whether to **keep everything on your computers** or use **free online services** too.

Then it shows what fits this PC, with each part's share of the graphics card, memory and processor, and **Use these suggestions** sets it up after one confirmation. **Ask me three questions instead** and **I'll choose myself** are there too.

![Welcome tour](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/welcome.png)

## Optional prerequisites

Martlet bundles the app runtime, many audio libraries, voice recognition runtime and default character assets. Optional features can need WebView2, Windows speech, Ollama, Docker Desktop + WSL 2, an NVIDIA driver or provider API keys.

Use **Settings › Tools › Prerequisites (check / install)** or Start menu **Martlet prerequisites**. Nothing changes until you choose an item.

## Updates

Martlet checks GitHub Releases by default. See [App Settings and Updates](App-Settings-and-Updates) to change that.

More detail: [README getting started](https://github.com/throndir2/Martlet/blob/main/README.md#-up-and-running-in-minutes), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md).
