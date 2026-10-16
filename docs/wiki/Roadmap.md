# Roadmap

This page tells what ships in each release now and what is only planned. **NOT RUN** means that the part is built but nobody has run it on that real hardware yet.

## What ships now

### The Windows app

The Windows app is the full Martlet. It runs on Windows 10 (version 2004 or later) and Windows 11, x64. Windows 11 on Arm PCs run the x64 app through Windows' emulation (NOT RUN on a real Arm PC). The app has:

- Typed, push-to-talk and hands-free conversation, Voice ID and many voices, with cloned voices and singing.
- The Live2D or VRM character on your desktop, with lip-sync, emotes, eyes and touch.
- Vision, app guides, memory, personalities, lorebooks and prompts.
- The Thinking pool and check-ins for background work.
- Picture making, songs, web research and Creations.
- Tools from MCP servers, the Terminal, reminders and Home Assistant.
- Discord, Telegram and WhatsApp.
- One Martlet network for all your computers, with sync, failover and the recommended setup.
- Household accounts, and the Share page for friends and your language models.

### Hosts

- Linux with Docker or natively with systemd, on x86_64 or ARM64 (for example a Raspberry Pi 5).
- Windows with Docker Desktop, on this PC or on another PC that runs Martlet.
- A Mac: Martlet's Mac host lends the Mac's own Ollama and whisper.cpp (NOT RUN on a Mac).
- ARM64 hosts run the processor roles: Ollama, whisper and Parakeet. The NVIDIA roles (voice cloning, singing, picture making, Audio2Face) need an x86_64 computer with an NVIDIA graphics card.

### The early Linux and macOS companion

Each release has AppImage and `.deb` files for Linux (x64 and arm64) and `.dmg` files for macOS 14 or later (Apple silicon and Intel). See [Linux and Mac](Linux-and-Mac). The app can:

- Take typed or push-to-talk conversation.
- Think with OpenAI, any Chat Completions server, or Ollama, LM Studio or Docker Model Runner on the same computer.
- Listen and speak with OpenAI.
- Show the VRM or Live2D character on top of your windows, with lip-sync from the voice's loudness.
- Keep keys in the desktop keyring or the Mac keychain, watch your screen after you start it, start at login and show a tray or menu bar item.
- Offer only the engines that the computer can run.

The Linux app was checked in an Ubuntu container. A real Linux desktop session (for example GNOME or KDE on Wayland) is NOT RUN. Everything on a Mac is NOT RUN.

### Web research

Martlet answers what it knows, makes a quick look-up for a fact it is not sure of, and does full or in-depth research when you ask. Research tells you as soon as it finds the answer, keeps a report in Creations and then checks the report's sources. The work runs on the Thinking pool. Search uses a built-in web search that needs no key.

## Planned

Planned items are not built. The app cannot offer them, and the platform catalog shows them with their plan ID.

### Linux and macOS companion

- Pairing with your hosts, who does each job, and failover.
- Listening with whisper.cpp on the same computer, and hands-free listening.
- Memory and Voice ID.
- Apple engines through a small Swift helper: Apple voices and Personal Voice, Apple speech, Apple Intelligence and F5 voice cloning on MLX (Apple silicon only).
- The Mac as a microphone and speaker for another computer.

### Windows

- A native ARM64 build and its own installer (PL06).
- Windows speech and whisper.cpp in conversations (PL02). Today they can be saved, but conversations do not use them.
- Setup suggestions for Macs, phones, tablets and old PCs (PL03).
- The conversation window shows what does not work before a turn (PL05).

### iPhone and iPad

A native Swift app, with no code yet. It is a companion while you game on the device, and a host while the app is open. It plans to use Apple Intelligence and Apple speech, watch a screen broadcast that you start, and show the character with Picture-in-Picture as an experiment. It installs with a free Apple ID, and the app must be signed again every 7 days.

### Android

A native Kotlin app (Jetpack Compose), with no code yet. It is a companion with the character over your game, and a host in the background with a notification. It plans to use Android speech and voices, and small LiteRT or llama.cpp models (Gemini Nano only while Martlet is in front). An old phone can be a room microphone and speaker or a camera. It installs as an APK from outside Google Play.

### Research

- More search services: SearXNG on your own host (a search host role), Ollama web search, Tavily, Brave and Exa.
- Several sub-researchers at the same time.
- Paid cloud research (OpenAI Deep Research), only when you choose it.
- Research that you start without a conversation, for example from Creations.

## How plans become features

- The plans use no paid programs, such as the Apple Developer Program or Google Play, and only hardware that the owner already has.
- Results on hardware that nobody has stay NOT RUN. They are never reported as passed.
- All validation is local. GitHub Actions run only the release build.
- Work never moves to a cloud provider silently.

More detail: [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md), [Linux and macOS companion](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_LINUX_MACOS.md), [macOS](https://github.com/throndir2/Martlet/blob/main/docs/MACOS.md), [iOS](https://github.com/throndir2/Martlet/blob/main/docs/IOS.md), [Android](https://github.com/throndir2/Martlet/blob/main/docs/ANDROID.md), [Deep research](https://github.com/throndir2/Martlet/blob/main/docs/DEEP_RESEARCH.md), [Changelog](https://github.com/throndir2/Martlet/blob/main/CHANGELOG.md).
