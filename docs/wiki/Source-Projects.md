# Source Projects

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Audio`, `src/Martlet.Gateway`, `src/Martlet.Gateway.Persistence`, `src/Martlet.Home`, `src/Martlet.Memory`, `src/Martlet.Messaging`, `src/Martlet.Discord`, `src/Martlet.Participation`, `src/Martlet.Perception`, `src/Martlet.Sherpa`, `src/Martlet.Support`, `src/Martlet.Avatars`, `src/Martlet.Avatar.Hosting`, `src/Martlet.Avatar.Audio2Face`, `src/Martlet.Credentials.Windows`, `src/Martlet.EchoCancellation`, `src/Martlet.F5`, `src/Martlet.NodeLinkCheck`

This page lists the source projects that [Repository Layout](Repository-Layout) does not name. Find a project here, then open its folder under `src`. Each row comes from the project file references, the users of the project and the code comments. Read the code before you rely on the details.

## Audio and speech

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Audio` | `CaptureNormalizer`, `EchoReduction`, `EnergyVoiceActivityDetector` | Capture normalising (a resampler), echo reduction and an energy-based voice activity detector. Used by the desktop, conversation and Doctor projects. |
| `src/Martlet.EchoCancellation` | `WebRtcEchoCanceller` | `WebRtcEchoCanceller`. It builds on `src/Martlet.Audio`. |
| `src/Martlet.Sherpa` | `ParakeetEngine`, `SmartTurnEngine`, `SherpaNative` | Installs speech components at pinned sizes and SHA-256 checks, and runs the Parakeet speech engine and the smart turn engine through native sherpa-onnx code. |
| `src/Martlet.F5` | `F5GatewayClientAdapter`, `F5BundledVoices` | The client adapter for the F5 gateway contracts and the bundled F5 voices. `src/Martlet.Gateway` uses it. |

## Character

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Avatars` | `AvatarComposer`, `AvatarChannels` | Shared avatar frame, channel and composition types. The other avatar projects build on it. |
| `src/Martlet.Avatar.Hosting` | `AvatarProfile`, `CharacterActionSettings`, `CharacterCombos` | The character profile (including the lip-sync choice Auto, Loudness or Audio2Face), actions, animations and combos. |
| `src/Martlet.Avatar.Audio2Face` | `Audio2FaceAdapter`, `AnimationDecoder` | The adapter and decoder for Audio2Face face animation. `src/Martlet.Gateway.Audio2Face` relays it. |

## Memory, people and messages

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Memory` | `LexicalIndex`, `MemoryExport` | Memory facts: a lexical search index, activity records and export. |
| `src/Martlet.Participation` | `ParticipationPolicy` | The policy that decides to allow, wait or suppress a reply from the participation mode, input source and response activity. It runs no voice detection; the caller gives it the events. |
| `src/Martlet.Perception` | `PerceptionFrames`, `PerceptionWorkerContracts` | Protocol and frame types for selected-window capture. `src/Martlet.Gateway` uses them. |
| `src/Martlet.Messaging` | `MessagingBridge`, `MessagingSharing`, `PublicAddress` | The Telegram and WhatsApp bridge: its state, bot identity, pairing code, sharing and public address. |
| `src/Martlet.Discord` | `DiscordBot`, `DiscordCompanion` | The Discord bot. See [Discord](Discord). |
| `src/Martlet.Home` | `HomeAssistantClient`, `HomeAssistantDiscovery` | The Home Assistant client. See [Smart Home](Smart-Home). |

## Gateway and workers

The gateway project holds accounts, API keys and authentication. Each relay worker project below references `src/Martlet.Gateway`, and `src/Martlet.Gateway.Host.Linux` and `src/Martlet.NodeLinkCheck` reference them. See [Multiple Computers](Multiple-Computers).

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Gateway` | `GatewayAccounts`, `GatewayApiKeys`, `GatewayAuthentication` | Gateway accounts, keys and authentication. |
| `src/Martlet.Gateway.Persistence` | `AuthorityStore`, `DurableGatewayHost`, `HostCertificate` | Durable gateway storage, the host certificate and the host control that opens the state. |
| `src/Martlet.Gateway.Ollama` | `OllamaRelayWorker` | Relay worker for Ollama models. |
| `src/Martlet.Gateway.Stt`, `src/Martlet.Gateway.VoiceRecognition` | `SttRelayWorker`, `VoiceRecognitionRelayWorker` | Relay workers for speech recognition and voice recognition. |
| `src/Martlet.Gateway.F5`, `src/Martlet.Gateway.Xtts`, `src/Martlet.Gateway.Dia`, `src/Martlet.Gateway.GptSovits` | `F5RelayWorker`, `XttsRelay`, `DiaRelay`, `GptSovitsRelay` | Relay workers for the speech engines. |
| `src/Martlet.Gateway.Singing`, `src/Martlet.Gateway.Pictures`, `src/Martlet.Gateway.Ocr`, `src/Martlet.Gateway.Motion`, `src/Martlet.Gateway.Audio2Face` | `SongRelayWorker`, `PictureRelayWorker`, `OcrRelayWorker`, `MotionRelayWorker`, `Audio2FaceRelayWorker` | Relay workers for songs, pictures, screen reading, motion and face animation. |

## Support

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Support` | `DiagnosticJournal`, `BuildMetadata` | A bounded diagnostic journal, build metadata and report projection. |
| `src/Martlet.Credentials.Windows` | `WindowsCredentialStore` | Windows Credential Manager storage for secrets. |
| `src/Martlet.NodeLinkCheck` | `AccountRehearsal`, `ApiRehearsal` | A command-line check program. It runs commands between computers on this PC's loopback with fixture devices, or checks a disposable Linux gateway container. `src/Martlet.Mcp` uses it. |
