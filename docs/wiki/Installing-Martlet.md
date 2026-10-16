# Installing Martlet

Martlet installs for your own Windows user. You don't need administrator rights, and the installer asks no setup questions.

## What you need

| You need | |
| --- | --- |
| **Windows** | Windows 10 (version 2004 or later) or Windows 11, 64-bit (x64). |
| **Windows on Arm** | Windows 11 on Arm PCs, such as Snapdragon X laptops, work through Windows' x64 emulation. Jobs that need an NVIDIA graphics card aren't available there. Windows 10 on Arm can't run Martlet. |
| **Graphics card** | Optional. Any PC works with a cloud provider. A graphics card runs AI models on your own PC. Picture making, singing, Audio2Face and most cloned voices need an NVIDIA card. |
| **Internet** | To download models and to use cloud services. |
| **Cost** | Martlet is free. Cloud providers can charge for use. Martlet walks you through free keys for NVIDIA Build and Google Gemini. |

The app runtime (.NET), its audio and speech-recognition parts and the Hiyori sample character are in the installer. You don't install them yourself.

Using Linux or a Mac? See [Linux and Mac](Linux-and-Mac) for the early desktop app.

## Download and install

1. Open [the latest release](https://github.com/throndir2/Martlet/releases/latest).
2. Download `Martlet-<version>-win-x64.exe`.
3. Run it. If Windows says that it protected your PC, choose **More info**, then **Run anyway**.
4. Read the license and the release notice, then install.
5. On the last page, keep **Start Martlet and finish setting up** selected and choose **Finish**.

Martlet installs to `%LOCALAPPDATA%\Programs\Martlet`. The Start menu gets **Martlet**, **Martlet prerequisites**, **Martlet Doctor** (a status check in a console window), **Read me** and **Uninstall Martlet**.

## About the Windows warning

The installer isn't code-signed, so Windows can warn that the publisher is unknown. This is normal for Martlet: unsigned releases are its actual, normal releases.

- The release page lists the SHA-256 checksum of each download. A checksum shows that the download is complete and not damaged. It doesn't prove who published it.
- Download Martlet only from its GitHub releases page.
- Never turn off Windows security features to install Martlet.

## First start

Martlet opens the welcome tour. It helps you start a new Martlet network or join yours, asks three quick questions and suggests a setup that fits this PC. Continue with [First Steps](First-Steps).

![Welcome tour](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/welcome.png)

## Optional prerequisites

Some features need programs that Martlet doesn't put in its installer:

- The **WebView2 runtime** shows the character. Windows 11 already has it.
- **Ollama** runs a Thinking model on this PC.
- **Windows speech** for your language. The **Test hearing** check speaks its test word with it.
- An **NVIDIA driver** for jobs on an NVIDIA graphics card.
- **Docker Desktop with WSL 2** runs Martlet's host service on this PC (voice engines, Whisper, Audio2Face and more).

Martlet downloads each one from its publisher, and only after you choose it. Windows features ask for administrator approval separately. To check them any time, open **Settings › Tools › Prerequisites**, or **Martlet prerequisites** in the Start menu. Nothing changes until you choose an item.

## Updates

Martlet checks its GitHub releases for updates and installs them in the background when you agree. See [App Settings and Updates](App-Settings-and-Updates).

To update by hand, exit Martlet first (**Exit Martlet** in Settings or in the notification-area menu). Then run the newer installer.

## Uninstall

1. Exit Martlet.
2. Run **Uninstall Martlet** from the Start menu, or remove Martlet in Windows **Settings › Apps**.

Your settings, memories and other data stay in `%LOCALAPPDATA%\Martlet`. Delete that folder too if you want to remove them.

More detail: [README getting started](https://github.com/throndir2/Martlet/blob/main/README.md#-up-and-running-in-minutes), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md).
