# FAQ

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Desktop/MainWindow.xaml`, `src/Martlet.Companion/MainWindow.axaml`, `packaging/windows/Martlet.iss`, `packaging/linux/build-linux.sh`, `packaging/macos/build-macos.sh`

## What is Martlet?

Martlet is an AI companion app. The Windows app has setup, companion and device features. A separate, smaller app is available for Linux and macOS. See [Home](Home) and [Linux and Mac](Linux-and-Mac).

## Which computers can run Martlet?

The Windows installer supports 64-bit x64 Windows 10 version 2004 or later, and Windows 11. Linux packages are built for x64 and ARM64. macOS packages are built for Intel and Apple silicon. See [Installing Martlet](Installing-Martlet) and [Linux and Mac](Linux-and-Mac).

## Do I need a graphics card?

No. Some local models can run on the processor. A graphics card can run supported models faster. The available choices depend on the computer and model.

## Does Martlet listen or watch when it starts?

The Windows app does not start listening by default. You can enable a startup option to start listening, and watching when vision is on. The Linux and macOS companion app uses **Hold to talk** for voice input. See [Talking to Martlet](Talking-to-Martlet) and [Linux and Mac](Linux-and-Mac).

## Does Martlet send my messages to an online service?

A local model app may keep requests on this PC, but the app controls how it handles them. A cloud service receives the requests sent to it. Check the service and its terms before you use it.

## Is the Windows installer signed?

No. The Windows installer is not code-signed. Download it from [Martlet releases](https://github.com/throndir2/Martlet/releases/latest). A SHA-256 checksum can detect a damaged or changed download, but it does not prove who published it.

## Where can I find more answers?

See [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md), and [Troubleshooting](Troubleshooting).
