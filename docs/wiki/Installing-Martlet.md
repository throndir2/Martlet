# Installing Martlet

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `packaging/windows/Martlet.iss`, `packaging/windows/README.md`, `src/Martlet.Desktop/MainWindow.Welcome.cs`

The Windows installer installs Martlet for your Windows user. It does not need administrator rights for the app installation.

## What you need

- 64-bit x64 Windows 10 version 2004 or later, or Windows 11.
- An internet connection to download Martlet and use online services.
- A graphics card is optional. Some local models can run on the processor.

Martlet is free to download. Some online services can charge for use.

## Download and install

1. Open [the latest release](https://github.com/throndir2/Martlet/releases/latest).
2. Download `Martlet-<version>-win-x64.exe`.
3. Run the installer and read the license and release notice.
4. Finish the installation. Select **Start Martlet and finish setting up** to open Martlet.

The installer uses `%LOCALAPPDATA%\Programs\Martlet`. It adds **Martlet**, **Martlet prerequisites**, **Martlet Doctor**, and **Uninstall Martlet** to the Start menu.

## About the Windows warning

The installer is not code-signed, so Windows can show an unknown-publisher warning. Download Martlet only from its GitHub releases page. The release includes a SHA-256 checksum. The checksum does not prove who published the file.

## First start

Martlet opens the welcome tour when this Windows user has not selected a role for this computer. The tour helps you set up Martlet. See [First Steps](First-Steps).

## Updates and removal

Martlet checks GitHub releases for updates. It installs an update after you agree, unless you turn on automatic installation. To remove the app, use **Uninstall Martlet** in the Start menu or Windows **Settings › Apps**.

Some features need optional programs or model downloads. Martlet asks before it installs them. See [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md) and [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md).
