# Linux and Mac

The full Martlet app is for Windows. Each release also has an **early desktop companion** for Linux and macOS. It is a smaller, separate app: you type or hold a key to talk, hear the replies and see your character on top of your windows.

Linux computers and Macs can also be **hosts** that lend their power to your other computers.

## What the early app can do

- **Talk.** Type a message, or hold the **Hold to talk** button or your push-to-talk key while you speak. The default key is F8 on Linux and Control+Option+T on a Mac. Change it in **Settings**.
- **Think.** Use OpenAI, or any OpenAI-compatible server. One click fills in **Ollama on this computer**, **LM Studio** or **Docker Model Runner**, and **Find model apps** looks for the model apps that run on this computer.
- **Listen and speak** with OpenAI, after you add an OpenAI key and allow **Send what I type and say to the cloud provider I chose**. Without an OpenAI key, use a model app on this computer for Thinking, type your messages and read the replies.
- **Show your character.** **Show character** puts your VRM or Live2D character on top of your windows. Its mouth moves with the voice's loudness. Without a model of your own, it shows the built-in Hiyori.
- **Keep your keys safe** in your desktop's keyring (GNOME Keyring or KWallet) on Linux, or in the Keychain on a Mac.
- **Stay in the tray** on Linux, or in the menu bar on a Mac. From there you can show Martlet, show or hide the character, or quit.

Martlet offers only what this computer can run. When you import settings from another computer (**Import settings from another device…**), Martlet keeps what works here and says why it refused the rest. The **This computer** tab shows what Martlet found on this computer.

## Downloads

Each [release](https://github.com/throndir2/Martlet/releases/latest) has these files:

| Computer | File |
| --- | --- |
| Ubuntu or Debian, 64-bit Intel or AMD | `martlet_<version>_amd64.deb` |
| Ubuntu or Debian, ARM | `martlet_<version>_arm64.deb` |
| Any Linux, 64-bit Intel or AMD | `Martlet-<version>-linux-x64.AppImage` |
| Any Linux, ARM | `Martlet-<version>-linux-arm64.AppImage` |
| Mac with Apple silicon (About This Mac shows "Chip") | `Martlet-<version>-macos-arm64.dmg` |
| Mac with an Intel processor ("Processor") | `Martlet-<version>-macos-x64.dmg` |

The Mac app needs macOS 14 or later. The release notes list the SHA-256 checksum of each file.

## Install on Linux

**Ubuntu or Debian:**

1. Download the `.deb` file for your computer.
2. In a terminal, in the download folder, run `sudo apt install ./martlet_<version>_amd64.deb` (or the `arm64` file).
3. Start **Martlet** from your app menu, or run `martlet`.

**Any other distribution:**

1. Install WebKitGTK 4.1 (`libwebkit2gtk-4.1-0` on Debian-based systems) and libsecret from your distribution.
2. Download the AppImage for your computer.
3. Make it executable with `chmod +x Martlet-<version>-linux-x64.AppImage`.
4. Run it.

## Install on a Mac

1. Download the `.dmg` file for your Mac and open it.
2. Drag **Martlet** to **Applications**.
3. Open Martlet. macOS says that it can't check Martlet for malicious software. Choose **Done**.
4. Open **System Settings > Privacy & Security**. Scroll down and choose **Open Anyway**, then confirm.

The Mac app isn't notarized by Apple, so you do this once for each new version. macOS can also ask again for the microphone after an update. Never turn off Gatekeeper to run Martlet.

## Linux and Macs as hosts

A host lends its processor and graphics card to your other computers, for example for Thinking and Listening.

- **Linux:** add the computer from Martlet on your Windows PC with **Devices › Add a computer › A Linux computer over SSH**, or set it up on the Linux computer itself. ARM64 computers such as a Raspberry Pi 5 can be hosts for Thinking (Ollama) and Listening. See [Linux over SSH](Setting-Up-a-Host#linux-over-ssh).
- **Mac:** the Mac app has the Mac host inside. It lends the Mac's own Ollama and whisper.cpp, on the graphics chip on Apple silicon, while you are logged in. You set it up with two commands in Terminal. See [Macs](Setting-Up-a-Host#macs).

Jobs that need an NVIDIA graphics card (voice cloning, singing, Picture making and Audio2Face) need an x86_64 computer with an NVIDIA card. Martlet never offers them on a Mac or an ARM computer. See [Setting Up a Host](Setting-Up-a-Host).

## Limits compared with Windows

- **No hands-free listening yet.** Use push-to-talk. Headphones help.
- **Not in your Martlet network yet.** The early app can't pair with your hosts, so it doesn't use your other computers.
- **No memory, Voice ID or watching your screen yet.** Most other features are on Windows only for now, for example App guides, tools, smart home, Discord, singing and Picture making.
- **Listening and speaking need OpenAI.** Windows speech and Windows voices aren't available. Audio2Face and local voice cloning never run on a Mac.
- **On an Intel Mac,** a model on this computer runs on the processor only. On Linux without an NVIDIA graphics card it may too (AMD cards can work through Ollama). Use a small model, or a cloud provider.
- **Linux desktops differ.** On Wayland, the character runs through XWayland, and a full-screen Wayland game can cover it. On Wayland, the push-to-talk key works in every app only on desktops with global shortcuts (KDE Plasma, GNOME 48 or later). Without a compositor, the character's background shows black. On GNOME, the tray icon needs the AppIndicator extension. Without a keyring, keys are kept only until Martlet quits.
- **No in-app updates.** To update, install the newer download.
- **Not much tested yet.** The Mac app and the Mac host were not yet tried on a real Mac, and the Linux app was not yet tried on a real GNOME or KDE desktop. Please [report problems](https://github.com/throndir2/Martlet/issues).

More detail: [Linux and macOS desktop companion](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_LINUX_MACOS.md#installers-part-of-the-release-build), [macOS](https://github.com/throndir2/Martlet/blob/main/docs/MACOS.md), [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md), [Host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md).
