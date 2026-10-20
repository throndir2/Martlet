# Repository Layout

This page tells you where things are in the repository. Before you change a rule, find the file that owns it in the [decision map](https://github.com/throndir2/Martlet/blob/main/docs/DECISION_MAP.md). Use the names in the [glossary](https://github.com/throndir2/Martlet/blob/main/docs/GLOSSARY.md).

## Root

| Path | Purpose |
| --- | --- |
| `README.md` | The overview for users. |
| `CHANGELOG.md` | What changed in each version. Each release takes its notes from it. |
| `CONTRIBUTING.md` | The developer quick start and the local-only validation policy. |
| `AGENTS.md` | The rules for coding agents. |
| `LICENSE` | The license terms. |
| `Martlet.slnx` | The main solution for the IDE. |
| `Directory.Build.props` | Shared build settings and the Martlet `<Version>`. |
| `Directory.Build.targets`, `Directory.Packages.props`, `NuGet.config` | Shared build steps, central package versions and package sources. |
| `global.json` | The pinned .NET SDK version. |
| `.github\workflows\windows-release.yml` | The one workflow: the release build that you start by hand. |

## Apps in `src\`

| Project | Purpose |
| --- | --- |
| `Martlet.Desktop` | The Windows app (WPF). |
| `Martlet.Companion` | The early Linux and macOS desktop companion (Avalonia). |
| `Martlet.Companion.Platform`, `Martlet.Platform.Linux`, `Martlet.Platform.MacOS` | The companion's platform service contracts, and their Linux and macOS versions (hotkey, overlay, keys, screen capture, tray, start at login). |
| `Martlet.Doctor` | The Doctor command-line tool. |
| `Martlet.Mcp`, `Martlet.Mcp.Protocol` | Martlet's own MCP server and its tools. |
| `Martlet.Avatar.RendererHost` | The private character renderer process on Windows (WebView2). |
| `Martlet.Gateway.Host.Linux` | The host gateway on Linux hosts and on the Mac host. |

## Libraries in `src\`

| Project | Purpose |
| --- | --- |
| `Martlet.Core` | Settings, sync, accounts, the platform catalog and other shared rules. |
| `Martlet.Conversation` | The conversation, background jobs, the Thinking pool and web research. |
| `Martlet.Providers` | Cloud and local model providers. |
| `Martlet.Participation` | When Martlet takes part in a conversation. |
| `Martlet.Audio`, `Martlet.EchoCancellation` | Audio, echo reduction and voice activity detection. |
| `Martlet.Sherpa` | Speech recognition on this PC with sherpa-onnx (Parakeet, speaker recognition, sound tagging, Smart Turn). |
| `Martlet.Memory` | Remembered facts and retrieval. |
| `Martlet.Perception` | The contracts for the reading (OCR) and seeing (VLM) host workers. |
| `Martlet.Discord`, `Martlet.Messaging`, `Martlet.Home` | Discord, Telegram and WhatsApp, and Home Assistant. |
| `Martlet.Avatars`, `Martlet.Avatar.Hosting`, `Martlet.Avatar.Audio2Face` | Character contracts, hosting and Audio2Face lip-sync. |
| `Martlet.HostClient` | The client for a Martlet host's API (pairing, sign-in and accounts, chat, memories, media, cluster, settings) and the network sync and household sign-in. |
| `Martlet.Avatar.Vrm`, `Martlet.Avatar.Live2D` | The VRM and Live2D web packages (npm). |
| `Martlet.Gateway`, `Martlet.Gateway.Persistence` | The host gateway and its saved pairings and state. |
| `Martlet.Gateway.*` (Ollama, Stt, F5, Xtts, GptSovits, Dia, Singing, Pictures, Ocr, Motion, VoiceRecognition, Audio2Face) | The relays from the gateway to each host role. |
| `Martlet.F5` | The F5 voice worker client and voice presets. |
| `Martlet.Credentials.Windows` | API keys in Windows Credential Manager. |
| `Martlet.Diagnostics`, `Martlet.Support` | Diagnostic probes and the local support bundle. |
| `Martlet.Mcp.Client` | The MCP client for the tools Martlet uses while you talk. |
| `Martlet.NodeLinkCheck` | A test tool that MCP self-tests run between real gateways. |

## Other folders

| Path | Purpose |
| --- | --- |
| `tests\` | The xUnit test projects (`<Project>.Tests`) and `known-failures.txt`. |
| `workers\` | Python workers for the host roles: audio2face, chatterbox, dia, f5, gpt-sovits, motion, ocr, parakeet, perception, pictures, singing, voice-recognition and xtts. Each has its tests in `workers\<name>\tests`. |
| `deploy\host` | The `martlet-host` engine, its `Dockerfile` and the host roles in `deploy\host\roles`. |
| `deploy\ubuntu` | Ubuntu Compose files, setup and artifacts. |
| `packaging\windows` | The Windows publish, installer and release scripts. |
| `packaging\linux`, `packaging\macos` | The Linux (AppImage and .deb) and macOS (.dmg) package scripts. |
| `contracts\avatars`, `contracts\golden` | Contract schemas and test vectors. |
| `scripts\` | Developer scripts: `Test-Martlet.ps1`, `Invoke-MartletMcp.ps1`, `Register-MartletMcp.ps1`, `Clean-Martlet.ps1`, `Smoke-Doctor.ps1`, `Get-ReleaseNotes.ps1`, `Publish-Wiki.ps1`, `Update-ModelCatalog.ps1`, `Measure-Footprint.ps1` and others. |
| `docs\` | Developer and design docs. `docs\DECISION_MAP.md` names the owner of each decision. `docs\GLOSSARY.md` gives one name for each concept. |
| `docs\wiki` | The source of this wiki. `scripts\Publish-Wiki.ps1` checks the links and publishes it. |
| `docs\images` | The screenshots for the README and the wiki. |

More detail: [Architecture](Architecture), [Contributing](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md), [Decision map](https://github.com/throndir2/Martlet/blob/main/docs/DECISION_MAP.md), [Glossary](https://github.com/throndir2/Martlet/blob/main/docs/GLOSSARY.md), [Host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md), [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md).
