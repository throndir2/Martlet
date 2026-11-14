# Source Projects

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Audio`, `src/Martlet.Gateway`, `src/Martlet.Gateway.Persistence`, `src/Martlet.Home`, `src/Martlet.Memory`, `src/Martlet.Messaging`, `src/Martlet.Discord`, `src/Martlet.Participation`, `src/Martlet.Perception`, `src/Martlet.Sherpa`, `src/Martlet.Support`, `src/Martlet.Avatars`, `src/Martlet.Avatar.Hosting`, `src/Martlet.Avatar.Audio2Face`, `src/Martlet.Credentials.Windows`, `src/Martlet.EchoCancellation`, `src/Martlet.F5`, `src/Martlet.NodeLinkCheck`

This page lists the source projects that [Repository Layout](Repository-Layout) does not name. Find a project here, then open its folder under `src`. The purpose of each project comes from its main files. Read the code before you rely on the details.

## Audio and speech

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Audio` | `CaptureNormalizer`, `EchoReduction`, `EnergyVoiceActivityDetector` | Microphone capture handling, echo reduction and voice activity detection. |
| `src/Martlet.EchoCancellation` | `WebRtcEchoCanceller` | The echo canceller that uses WebRTC. |
| `src/Martlet.Sherpa` | `ParakeetEngine`, `SmartTurnEngine`, `SherpaNative` | Native speech recognition and turn detection engines. |
| `src/Martlet.F5` | `F5GatewayClientAdapter`, `F5BundledVoices` | The F5 voice client and its bundled voices. |

## Character

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Avatars` | `AvatarComposer`, `AvatarChannels` | Shared avatar frames and how the channels combine. |
| `src/Martlet.Avatar.Hosting` | `AvatarProfile`, `CharacterActionSettings`, `CharacterCombos` | Character profile, actions, animations and combos. |
| `src/Martlet.Avatar.Audio2Face` | `Audio2FaceAdapter`, `AnimationDecoder` | The Audio2Face face-animation adapter. |

## Memory, people and messages

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Memory` | `LexicalIndex`, `MemoryExport` | Memory search, activity and export. |
| `src/Martlet.Participation` | `ParticipationPolicy` | Rules for when Martlet takes part in a conversation. |
| `src/Martlet.Perception` | `PerceptionFrames`, `PerceptionWorkerContracts` | Contracts for the perception workers. |
| `src/Martlet.Messaging` | `MessagingBridge`, `MessagingSharing`, `PublicAddress` | Text messaging bridge, sharing and the public address. |
| `src/Martlet.Discord` | `DiscordBot`, `DiscordCompanion` | The Discord bot. See [Discord](Discord). |
| `src/Martlet.Home` | `HomeAssistantClient`, `HomeAssistantDiscovery` | The Home Assistant client. See [Smart Home](Smart-Home). |

## Gateway and workers

The gateway serves accounts, API keys and shared state to many computers. See [Multiple Computers](Multiple-Computers).

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Gateway` | `GatewayAccounts`, `GatewayApiKeys`, `GatewayAuthentication` | Gateway accounts, keys and authentication. |
| `src/Martlet.Gateway.Persistence` | `AuthorityStore`, `DurableGatewayHost`, `HostCertificate` | Durable gateway state and the host certificate. |
| `src/Martlet.Gateway.Ollama` | `OllamaRelayWorker` | Relay worker for Ollama models. |
| `src/Martlet.Gateway.Stt`, `src/Martlet.Gateway.VoiceRecognition` | `SttRelayWorker`, `VoiceRecognitionRelayWorker` | Relay workers for speech recognition and voice recognition. |
| `src/Martlet.Gateway.F5`, `src/Martlet.Gateway.Xtts`, `src/Martlet.Gateway.Dia`, `src/Martlet.Gateway.GptSovits` | `F5RelayWorker`, `XttsRelay`, `DiaRelay`, `GptSovitsRelay` | Relay workers for the speech engines. |
| `src/Martlet.Gateway.Singing`, `src/Martlet.Gateway.Pictures`, `src/Martlet.Gateway.Ocr`, `src/Martlet.Gateway.Motion`, `src/Martlet.Gateway.Audio2Face` | `SongRelayWorker`, `PictureRelayWorker`, `OcrRelayWorker`, `MotionRelayWorker`, `Audio2FaceRelayWorker` | Relay workers for songs, pictures, screen reading, motion and face animation. |

## Support

| Project | Main files | What it covers |
| --- | --- | --- |
| `src/Martlet.Support` | `DiagnosticJournal`, `BuildMetadata` | Diagnostic journal and build information. |
| `src/Martlet.Credentials.Windows` | `WindowsCredentialStore` | Secret storage in Windows Credential Manager. |
| `src/Martlet.NodeLinkCheck` | `AccountRehearsal`, `ApiRehearsal` | Checks of the link between computers. |
