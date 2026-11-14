# Linux and Mac

**Status:** Partial
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Companion/MainWindow.axaml`, `src/Martlet.Companion/CompanionConversation.cs`, `src/Martlet.Platform.Linux/Martlet.Platform.Linux.csproj`, `src/Martlet.Platform.MacOS/Martlet.Platform.MacOS.csproj`, `packaging/linux/build-linux.sh`, `packaging/macos/build-macos.sh`

Martlet has a separate, smaller desktop companion app for Linux and macOS. It does not have all features of the Windows app.

## Downloads

The release build creates Linux `.deb` and AppImage packages, and macOS `.dmg` packages. It builds Linux x64 and ARM64 packages, and macOS x64 and ARM64 packages. Download the package for your computer from [the latest release](https://github.com/throndir2/Martlet/releases/latest).

## What the companion app does

The app has **Talk**, **Settings**, and **This computer** tabs. In Talk, you can type a message or hold **Hold to talk** while you speak. You can stop speech, show the character, or start a new conversation.

Settings lets you choose Thinking, listening and speaking options. Thinking uses OpenAI or an OpenAI-compatible server. The app has presets for Ollama, LM Studio and Docker Model Runner. **Find model apps** searches for local model servers.

Cloud services need the required API key and consent. The app stores keys in the operating system's credential store when one is available.

The app can pair with a host. It can share supported settings with that host. The push-to-talk key, character file and cloud consent stay with this computer.

## Limits

The companion app does not provide the full Windows app's pages and workflows. It uses push-to-talk, not hands-free listening. The available features depend on the operating system and its desktop services.

For platform details, see [Linux and macOS desktop companion](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_LINUX_MACOS.md), [macOS](https://github.com/throndir2/Martlet/blob/main/docs/MACOS.md), [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md), and [Host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md).
