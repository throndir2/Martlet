# Linux and macOS desktop companion: research, decision and plan

**Plan, 2026-10-06. Built slices report their status below; every
Linux-desktop and Mac result is NOT RUN until a slice reports it.** The owner asked for:

1. A **Mac as the companion PC** (talk to it, character on its screen), and
   whether a Mac can be a **host**.
2. A **Linux desktop** (for example Ubuntu) as the companion PC, with the
   character shown on the Linux screen. Linux already works as a host.
3. **Guardrails:** when Martlet knows it runs on a Mac (or Linux), options the
   machine cannot run are not offered.
4. **Installers** for Linux and macOS, built by the same release build as the
   Windows installer.

## Decision: one .NET companion for Linux and macOS

The Windows desktop is WPF (Windows-only), but almost everything under it is
already portable `net10.0`: `Martlet.Core` (including the
[platform catalog](../src/Martlet.Core/Platforms/PlatformCatalog.cs)),
`Conversation`, `Providers`, `Participation`, `Memory`, `Avatars`, `Perception`,
`VoiceActivity`, `LocalStt`, `Sherpa`, `Messaging`, `Updates`, the gateway
client and `Audio` (which also targets plain `net10.0`). So:

- **New app `Martlet.Companion`** on **Avalonia 12** (MIT), `net10.0`, for
  `linux-x64`, `linux-arm64`, `osx-arm64` and `osx-x64`. It reuses the C#
  libraries instead of re-implementing them. The character uses Avalonia's
  now open-source `NativeWebView` (WKWebView on macOS, WebKitGTK on Linux) with
  the existing VRM and Live2D web bundles.
- This **replaces the Swift companion plan** in [macOS](MACOS.md) (MA04-MA08):
  a Swift app would re-implement conversation, providers, memory and Voice ID
  and still need a separate Linux app. Swift stays the plan for iPhone/iPad
  ([iOS](IOS.md)); Apple-only engines (Apple Intelligence, Apple speech,
  Personal Voice, MLX F5) can later be reached from the .NET app through a small
  Swift helper process, not a second UI.
- **Platform services** behind interfaces in `Martlet.Companion` (owned by the
  app slice), implemented per OS in `Martlet.Platform.Linux` and
  `Martlet.Platform.MacOS`: global push-to-talk hotkey (down/up), character
  overlay window behaviors, screen capture to one JPEG per look, credential
  store, autostart, tray/menu bar, audio devices.

## Compatibility research (accessed 2026-10-06)

### Mac as a host: is Docker supported?

Docker Desktop for Mac runs, but **containers get no GPU**: they run in a Linux
VM and Apple's GPU is not passed through (Docker GPU support is NVIDIA on
Windows/WSL 2 only; Podman/libkrun has no Metal passthrough either). So the
existing `martlet-host` Docker method works on a Mac **CPU-only** (small
whisper and 1-4B Ollama models), and needs the arm64 images (PL04/MA10) on
Apple silicon. Audio2Face and the PyTorch F5 worker are NVIDIA-only and are
impossible on any Mac.

**The GPU path on a Mac is native, not Docker.** Ollama, LM Studio (MLX engine)
and Docker Model Runner (llama.cpp or vLLM-metal running as a native host
process, OpenAI-compatible API) all use Metal on Apple silicon. whisper.cpp
also runs on Metal. So:

- **Mac companion, thinking on the same Mac:** point Chat Completions at
  loopback Ollama / LM Studio / Docker Model Runner. GPU-accelerated on Apple
  silicon, CPU on Intel. Works with today's provider code.
- **Mac host (lending a Mac to the Windows PC):** run the existing .NET gateway
  natively on macOS (option (a) in [macOS](MACOS.md#host)) relaying to native
  Ollama (Metal) and whisper.cpp (Metal), installed as a launchd agent. Needs a
  macOS state-custody backend for `Martlet.Gateway.Persistence` (today
  Linux-only). This replaces the Swift host for Macs.

### What each component can use

| Component | Mac (Apple silicon) | Mac (Intel) | Linux desktop |
| --- | --- | --- | --- |
| Thinking: cloud (OpenAI, OpenRouter, NVIDIA Build, any Chat Completions) | Yes | Yes | Yes |
| Thinking: on this computer | Yes, GPU: Ollama / LM Studio / Docker Model Runner on Metal | CPU only, 1-4B models | Ollama/llama.cpp on NVIDIA (CUDA), AMD (ROCm/Vulkan) or CPU |
| Thinking: Apple Intelligence, MLX | Later, through a Swift helper (Swift-only APIs) | No: Apple silicon only | No |
| Listening: OpenAI | Yes | Yes | Yes |
| Listening: whisper.cpp / Parakeet on this computer | Yes (Metal / CPU) | CPU, `base`/`small` | CPU, CUDA or Vulkan |
| Listening: Windows speech | **No** | **No** | **No** |
| Speaking: OpenAI | Yes | Yes | Yes |
| Speaking: Windows voices | **No** | **No** | **No** |
| Speaking: F5 voice cloning | Via a paired NVIDIA host (F5-MLX later) | Via a paired NVIDIA host | Via a paired host, or this PC's own Docker host with an NVIDIA GPU (6 GB+) |
| Lip-sync: loudness | Yes | Yes | Yes |
| Lip-sync: Audio2Face | Only via a paired NVIDIA host | Only via a paired NVIDIA host | Paired host, or this PC with NVIDIA 4 GB+ |
| Echo cancellation (hands-free) | Not in the first release: push-to-talk default, headphones advised | Same | Same (PipeWire's echo-cancel module can be enabled by the user) |
| Character over games | Floating non-activating window on all Spaces and beside full-screen apps | Same (WebGL heavier) | X11 and XWayland: always-on-top transparent click-through window. Native Wayland: layer-shell on KDE/wlroots; GNOME Wayland only through XWayland |
| Push-to-talk hotkey | Carbon global hot key, no permission | Same | X11 `XGrabKey`; on Wayland the `GlobalShortcuts` portal (KDE, GNOME 48+) |
| Watch my screen | ScreenCaptureKit (Screen Recording permission) | Same | X11 capture; Wayland `ScreenCast` portal + PipeWire (consent dialog) |
| Keys and secrets | Keychain | Keychain | Secret Service (libsecret) |

### Guardrails

The [platform catalog](../src/Martlet.Core/Platforms/PlatformCatalog.cs) is the
single source of truth. The companion detects its own platform
(`OperatingSystem.IsMacOS()/IsLinux()`, architecture, Apple silicon vs Intel,
NVIDIA presence) and offers only engines the catalog marks as working there:
Windows speech/voices are never shown on Linux or macOS; local Audio2Face and
local F5 are never shown on a Mac; MLX and Apple Intelligence never on Intel
Macs or Linux; local GPU models on an Intel Mac carry the CPU-only warning.
Choices made on another device that this one cannot run are refused with the
catalog's reason, not silently changed.

## Installers (part of the release build)

`windows-release.yml` (manual dispatch, the only allowed workflow) gains Linux
and macOS jobs that attach to the same `v<version>` release, with no tests:

- **Linux:** `Martlet-<version>-linux-x64.AppImage`,
  `martlet_<version>_amd64.deb`, plus arm64 equivalents. Self-contained .NET;
  the `.deb` depends on `libwebkit2gtk-4.1-0` and `libsecret-1-0`.
- **macOS:** `Martlet-<version>-macos-arm64.dmg` and
  `Martlet-<version>-macos-x64.dmg`, `.app` bundle ad-hoc signed (not notarized;
  first launch needs **Open Anyway** once, see
  [Unsigned builds](MACOS.md#unsigned-builds-what-the-user-sees)). The Mac host
  ships inside the app bundle.
- Packaging scripts live in `packaging/linux` and `packaging/macos` and run
  locally too (Linux packaging on a Linux validation host or WSL).

## Delivery slices

| ID | Deliverable | Owner area |
| --- | --- | --- |
| DX01 | `Martlet.Companion` Avalonia app: settings, typed and push-to-talk conversation with cloud and loopback Chat Completions, OpenAI listening/speaking, cross-platform audio, character window with the web bundles, catalog guardrails, platform-service interfaces, MCP reachability | `src/Martlet.Companion`, catalog companion rows |
| DX02 | Linux integration: overlay (X11/XWayland, layer-shell), hotkey (X11 + portal), Secret Service, tray, autostart, screen capture (X11 + portal) | `src/Martlet.Platform.Linux` |
| DX03 | macOS integration: floating panel behaviors, Carbon hotkey, Keychain, menu bar, ScreenCaptureKit, Apple silicon/Intel detection, loopback Ollama/LM Studio detection | `src/Martlet.Platform.MacOS` |
| DX04 | Mac host: .NET gateway on macOS (launchd, macOS custody backend, native Ollama/whisper Metal relays, machine report), plus arm64 host images so Docker on a Mac works CPU-only | gateway, persistence, host setup, `deploy/` |
| DX05 | Linux and macOS installers in the release build | `packaging/linux`, `packaging/macos`, `windows-release.yml` |

DX01 lands its interfaces first; DX02, DX03 and DX05 build on them. DX04 is
independent. Merges are serialized.

## DX03 status: macOS integration (2026-10-06)

Built in `src/Martlet.Platform.MacOS` (plain `net10.0`, Objective-C runtime and
C exports, no net-macos workload) and returned by `MacPlatform.Create()`.
Pure logic is unit tested in `tests/Martlet.Platform.MacOS.Tests`, and the
companion publishes for `osx-arm64` and `osx-x64`. **No Mac was available:
every native behavior below is NOT RUN** until someone runs it on a real Mac.

| Service | How | NOT RUN on a Mac: what to check |
| --- | --- | --- |
| Platform probe (`MacPlatformProbe`) | sysctl `hw.optional.arm64` (Apple silicon, also under Rosetta), `sysctl.proc_translated` (Rosetta), `hw.memsize` (unified memory), `kern.osproductversion`, CPU brand and model. Intel Macs report no usable GPU (local models CPU-only, no MLX or Apple Intelligence); `Warnings()` says so, flags the Intel build under Rosetta and macOS below 14, and notes Audio2Face/F5 need a paired NVIDIA host | Values on an M-series Mac, an Intel Mac and the x64 build under Rosetta |
| Loopback model servers (`LocalModelServers`) | Asks `127.0.0.1` only: Ollama `:11434/api/tags`, LM Studio `:1234/v1/models`, Docker Model Runner `:12434/engines/v1/models`; returns each running one's Chat Completions base URL and models (Metal on Apple silicon, CPU on Intel) | Detection with each server running (portable code, unit tested with a fake server) |
| Push-to-talk (`MacPushToTalkHotkey`) | Carbon `RegisterEventHotKey` key down/up, no Accessibility or Input Monitoring permission; requires Command or Control (bare F-keys allowed, with fn on Mac keyboards); suggests Control+Option+T | Hold to talk inside a full-screen game; a combination another app owns reports "already used" |
| Character overlay (`MacCharacterOverlay`) | From the NSWindow handle: floating level, collectionBehavior canJoinAllSpaces, stationary, ignoresCycle and fullScreenAuxiliary, transparent and shadowless, a runtime subclass that can never become key or main (non-activating without replacing Avalonia's window), click-through toggled 20 times a second by hit-testing the character regions (kept during a drag) | The character over a full-screen game on another Space for 10 minutes, never taking focus; clicks pass through except on the character |
| Watch my screen (`MacScreenCapture`) | One look = macOS's own `screencapture -x -t jpg -D 1` resized by `sips`; `CGPreflightScreenCaptureAccess` gates every look and `CGRequestScreenCaptureAccess` runs only from the user's Start watching. The permission text explains System Settings, the restart and macOS 15's periodic re-confirmation. A ScreenCaptureKit helper (window choice, exclusions) is a later step | Prompt, restart, a look while a game is in front |
| Keys (`KeychainCredentialStore`) | Login keychain generic passwords, service "Martlet", account `companion/<name>` | Save, read, delete; the access prompt after an update of an ad-hoc signed build |
| Start at login (`MacAutostart`) | Inside `Martlet.app` on macOS 13+: `SMAppService.mainAppService` (Login Items, with the "allow in Settings" state); outside a bundle: `~/Library/LaunchAgents/io.github.throndir2.martlet.companion.plist` | Register, approval state, start after logging in |
| Menu bar (`MacTrayStatus`) | NSStatusItem whose title shows the state ("Martlet ● Listening") with Show, Show or Hide Character and Quit | Item appears after the first `Show`, menu commands arrive |
| Listening keeps the Mac awake (`MacListeningActivity`) | `NSProcessInfo beginActivity` (no App Nap, no idle sleep) plus an IOKit `PreventUserIdleSystemSleep` assertion while listening; outside the shared contracts, the app holds one while it listens | Replies stay prompt behind a full-screen game with the Mac otherwise idle |
