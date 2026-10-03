# Prerequisites: what Martlet bundles, installs for you, or needs from you

**Inventory date 2026-09-29.** Covers every feature on `main`, including the
OpenRouter / NVIDIA Build / OpenAI-compatible LLM routes and the setup advisor,
plus features still in progress (section 3). Every prerequisite is delivered one
of four ways:

| Delivery | Meaning |
| --- | --- |
| **Bundled** | Shipped inside the Martlet installer. Nothing to do. |
| **First-run option** | An **Install on this PC** button in the setup advisor's plan, and an item in **Prerequisites** (Settings › Tools) and the **Martlet prerequisites** tool. Downloaded from its publisher only when you choose it. |
| **Host tool** | Installed on a GPU host by `martlet-host setup` / `add <role>` after you type `yes` ([Martlet host](../deploy/host/README.md)). |
| **You supply** | Accounts, keys, your own models and GPU drivers. Martlet cannot redistribute or create them. |

## How users install and configure prerequisites

1. **The first time Martlet starts.** The installer asks nothing and installs no
   prerequisites; its Finished page offers to start Martlet. The welcome tour
   asks what this PC is for (the PC you talk on, or a host that lends its GPU)
   and installs nothing. Next, **Recommend a setup for me** asks your goal,
   features and computers; when its plan runs Ollama, Windows speech or Docker
   Desktop on this PC and one is missing, **Install on this PC** installs just
   those (Thinking's *This PC* also installs Ollama with its model). Chosen items
   run `prerequisites\Install-Prerequisites.ps1 -NoPrompt -Install ...` hidden,
   with its output in a Martlet run window. Nothing chosen here is final: see
   step 2, and change where each role runs later in Martlet (Companion, Devices).
   A one-click quick start that reads this PC's hardware and installs the whole
   plan is planned ([A1](USER_STORIES.md#a1-quick-start-on-a-first-pc)).
2. **Any time later.** Start menu > **Martlet prerequisites**, or
   **Prerequisites (check / install)** in Martlet's Settings › Tools, opens the same
   tool as an interactive checklist. It shows the status of every item and changes
   nothing until you pick one.
3. **From a command line** (Windows PowerShell 5.1 or PowerShell 7):

   ```powershell
   & "$env:LOCALAPPDATA\Programs\Martlet\prerequisites\Install-Prerequisites.ps1" -Check     # status; exit 2 if WebView2/microphone need action
   & "$env:LOCALAPPDATA\Programs\Martlet\prerequisites\Install-Prerequisites.ps1" -Install WebView2,Ollama -OllamaModel gemma4:e4b
   ```

   IDs: `WebView2`, `Microphone`, `WindowsSpeech`, `Ollama`, `NvidiaDriver`,
   `DockerDesktop`. `-Culture de-DE` selects another speech language (default:
   the Windows display language).

Rules the tool follows: publishers' installers only (Microsoft's signed WebView2
bootstrapper, checked with Authenticode before it runs, or `winget` with the
package's own license terms); administrator approval only for Windows features
(speech capabilities, WSL), each in its own UAC prompt; Martlet itself stays a
per-user app; no large model download without typing `y` or a model tag.

## 1. The Windows PC you talk to

| Prerequisite | Needed for | Delivery | Details |
| --- | --- | --- | --- |
| Windows 10 2004+ or Windows 11, x64 (build 19041+) | Everything | Checked by the installer | `MinVersion=10.0.19041`, x64 only. Windows 11 25H2 is the main test target. |
| .NET 10.0.12 + Windows Desktop runtime | Everything | **Bundled** | Self-contained Desktop, Doctor and avatar renderer; no separate .NET install. |
| NAudio 3.1.0 (WASAPI), System.Speech 10.0.12, System.Numerics.Tensors | Audio capture/playback, Windows speech adapters | **Bundled** | [DEPENDENCIES](../packaging/windows/DEPENDENCIES.txt). |
| gRPC 2.84.0 / Protobuf 3.36.2 | Audio2Face client | **Bundled** | |
| WebView2 SDK 1.0.4191.47 loader, Windows SDK projection | Avatar renderer process | **Bundled** | The SDK, not the runtime. |
| Live2D Cubism Core 05.01.0000 + Framework 5-r.4 + Hiyori sample | Default desktop character | **Bundled** | Redistributed under Live2D's terms; see RELEASE.txt. |
| three 0.180.0 + @pixiv/three-vrm 3.5.5 | VRM characters | **Bundled** | Offline browser bundle; no Node/npm for users. |
| **Microsoft Edge WebView2 Evergreen Runtime** | Showing or inspecting any character | **First-run option** (`WebView2`, ticked when missing) | Preinstalled on Windows 11, so usually already present. Not bundled: Microsoft services it evergreen and the offline installer is large (well over 100 MB). The tool runs Microsoft's bootstrapper (`go.microsoft.com/fwlink/p/?LinkId=2124703`, `/silent /install`), falling back to `winget Microsoft.EdgeWebView2Runtime`. |
| Microphone + Windows privacy access for desktop apps | Push-to-talk, audio tests | **First-run option** (`Microphone`, shown only when Windows blocks it) | Configuration only: opens `ms-settings:privacy-microphone`. Martlet still asks before every capture. |
| Speakers or headphones | Generated voice, tone test | You supply | |
| Windows speech recognizer + SAPI voices for your language | Windows offline speech-to-text and Windows-voices TTS (CPU, free, offline) | **First-run option** (`WindowsSpeech`, unticked; the private plan offers it) | Adds `Language.Speech~~~<culture>~0.0.1.0` and `Language.TextToSpeech~~~<culture>~0.0.1.0` from Windows Update (one UAC prompt). en-US already includes a recognizer and two voices. Martlet uses SAPI (System.Speech); OneCore "natural" voices are not visible to it. |
| OpenAI API key | OpenAI STT, TTS and LLM routes | You supply | Setup / resume > Credentials stores it in Windows Credential Manager. [Keys](https://platform.openai.com/api-keys). |
| OpenRouter or NVIDIA Build API key | Hosted LLM through the Chat Completions route | You supply | Setup / resume > Destinations > LLM provider. Base URLs `https://openrouter.ai/api/v1`, `https://integrate.api.nvidia.com/v1`. [OpenRouter keys](https://openrouter.ai/settings/keys), [NVIDIA Build](https://build.nvidia.com/). |
| **Ollama** (or LM Studio / llama.cpp) | Local LLM on this PC over loopback | **First-run option** (`Ollama`, unticked; plans with a model on this PC offer it) | `winget Ollama.Ollama` (MIT). The tool then offers one model sized to your GPU (`gemma4:e2b`, `gemma4:e4b`, `gemma4:12b`, `gemma4:26b`; each also sees your screen and calls tools; you can type any tag) and prints the Martlet setting: Setup / resume > Destinations > LLM provider *Custom OpenAI-compatible endpoint*, base URL `http://127.0.0.1:11434/v1`, no key. LM Studio (`winget ElementLabs.LMStudio`) works the same way but is not automated. |
| Your own Live2D (`.model3.json`) or VRM1 (`.vrm`) models | Custom characters | You supply | Character settings > Browse model. Rights stay with you. |
| NVIDIA GPU driver | Any local GPU role (LLM, Audio2Face, GPU voice) | You supply; the tool helps (`NvidiaDriver`) | NVIDIA drivers cannot be redistributed. The tool detects an NVIDIA GPU, checks `nvidia-smi` and opens NVIDIA's driver page. Not needed for API routes or loudness lip-sync. |
| **WSL 2 + Docker Desktop** | Martlet hosts > *This PC* (Audio2Face and future GPU roles on this PC) | **First-run option** (`DockerDesktop`, ticked for an NVIDIA host PC); also *Install Docker Desktop* in Martlet hosts | Martlet installs `winget Docker.DockerDesktop` (Docker Subscription Service Agreement; free for personal use), then turns on Virtual Machine Platform and Windows Subsystem for Linux, the Windows hypervisor and WSL 2.1.5+ (`wsl --install --no-distribution` or `wsl --update`) in one UAC step. When Windows must restart it asks, restarts and continues the setup after sign-in; firmware virtualization (VT-x/SVM) you turn on yourself, after an offered restart into the firmware settings ([Troubleshooting](TROUBLESHOOTING.md#docker-desktop-is-unable-to-start-virtualization-wsl-2)). |
| Windows Firewall rule `Martlet-Host-Gateway` (TCP 9443, private/domain, local subnet) | Other PCs reaching a host on this PC | Automatic in Martlet hosts > *This PC* (one UAC prompt, only when needed) | Not a first-run option: it is only needed once this PC becomes a host. |
| NVIDIA NGC API key | Only the Audio2Face role's optional `nim` engine (NVIDIA's NIM image); the default `local` engine needs no NVIDIA account or key | You supply; `martlet-host add audio2face` asks once when you choose `nim` | [NGC API key](https://org.ngc.nvidia.com/setup/api-key); stored on the host only. |

**Not needed on the client:** Git, Python, Node/npm, a .NET SDK, Docker (unless
this PC hosts GPU roles), administrator rights (except the optional Windows
feature steps), or a GPU.

## 2. A GPU host (another PC or server)

| Prerequisite | Needed for | Delivery |
| --- | --- | --- |
| Ubuntu 24.04 x86_64 with SSH, **or** another Windows PC with Martlet + Docker Desktop | Running roles | You supply the machine and OS; a Windows host uses the `DockerDesktop` first-run option |
| Docker Engine + Compose v2 | All container roles | **Host tool** (`martlet-host` installs after `yes`) |
| NVIDIA driver | GPU roles | **Host tool** on Ubuntu (after `yes`); on Windows, the `NvidiaDriver` item |
| NVIDIA Container Toolkit | GPU containers on Ubuntu | **Host tool** (after `yes`); Docker Desktop uses WSL 2 GPU support instead |
| .NET SDK for the native Linux gateway | Native Ubuntu method | **Host tool** (`setup` installs it into `~/.dotnet`, no sudo); the Docker method needs nothing extra |
| Gateway TLS identity, pairing code | Pinned TLS between client and host | **Host tool** (`setup`, `pair`) |
| Audio2Face-3D, 4 GB+ VRAM, models `claire`/`mark`/`james`: the default `local` engine is NVIDIA's open-source Audio2Face-3D SDK built on the host ([`workers/audio2face`](../workers/audio2face/README.md); RTX 20 series or newer, driver 570+, models downloaded from Hugging Face on first start); the `nim` engine is NIM `nvcr.io/nim/nvidia/audio2face-3d:1.3` + pinned model configs | Rich lip-sync | **Host tool** (`add audio2face`); `nim` also needs your NGC key |

## 3. Coming with features in progress

These are not yet wired into the Desktop app; the delivery shown is the plan so
each lands with an install path instead of a manual step.

| Feature | Prerequisites | Planned delivery |
| --- | --- | --- |
| Windows offline STT / Windows voices TTS dispatch | Windows speech capability for your language | `WindowsSpeech` first-run option (above). |
| whisper.cpp local STT | whisper.cpp v1.9.2 CPU binaries (MIT) + `ggml-base.en.bin` (148 MB, MIT) | Bundle the small binaries; download the model on request with its pinned SHA-256. |
| Learned VAD / barge-in | Silero VAD v6.2.1 ONNX (1.3 MB, MIT) + ONNX Runtime 1.30.0 (MIT) | **Bundle** both when the feature ships. |
| F5-TTS (`f5`, 6 GB+), XTTS-v2 (`xtts`, 4 GB+; coqui-tts 0.27.5, CPML non-commercial weights; see [XTTS-v2](XTTS_VOICE.md)) and other Voice Studio engines (Qwen3-TTS, Chatterbox, GPT-SoVITS) | Python 3.12, PyTorch + CUDA, model weights, NVIDIA GPU | Host roles (`martlet-host add <role>`, containers built on the host from hash-locked requirements; weights downloaded with pinned SHA-256 after the role's licence terms). No Python on the Windows client. |
| Screen understanding | Python 3.12 + OCR/VLM model on GPU, or hosted vision model | Host role or API key; a CPU OCR option would use `winget UB-Mannheim.TesseractOCR`. |
| Local MCP server (`Martlet.Mcp`) | None beyond a build | Developer tool, not part of the installer. |

See [Recommended setups](RECOMMENDED_SETUPS.md) for which roles belong on which
machine, and the [Martlet host guide](../deploy/host/README.md) for host commands.
