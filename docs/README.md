# Martlet knowledge map

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against 5f40af2d9 (page map and local Markdown links checked; page bodies are verified separately)
**Code:** Indexed areas include `src/Martlet.Core/`, `src/Martlet.Conversation/`, and `src/Martlet.Desktop/`.

Search this knowledge base with `kb_search`, then read cited sections with
`kb_read_section`. Follow their `Code` lines to the source.
Use [the page contract](kb/knowledge-base.md) when you update a page.

## Knowledge folders

- [KB maintenance](kb/README.md): How do agents search and keep these pages true?
- [MCP guides](mcp/): Which tools reach and show each Martlet feature?
- [Wiki source](wiki/README.md): Which guides help people use and develop Martlet?
- [Images](images/): Where do the guides keep screenshots?

## Main code areas

| Code folder | Start page | Question |
| --- | --- | --- |
| `src/Martlet.Core` | [Settings and cluster](CLUSTER.md) | Which choices travel between computers? |
| `src/Martlet.Conversation` | [Conversation](CONVERSATION.md) | How does Martlet prepare replies and background work? |
| `src/Martlet.Desktop` | [Desktop](DESKTOP_APP.md) | How does the Windows app connect its features? |
| `src/Martlet.Companion` | [Companion](DESKTOP_LINUX_MACOS.md) | What runs on Linux and macOS? |
| `src/Martlet.Mcp`, `src/Martlet.Mcp.Protocol` | [MCP](MCP.md) | How can an assistant control and inspect Martlet? |
| `src/Martlet.Providers` | [Endpoints](OPENAI_ENDPOINT.md) | How does Martlet call model providers? |
| `src/Martlet.Memory` | [Memory](MEMORY.md) | How does Martlet keep and retrieve memories? |
| `src/Martlet.Audio`, `src/Martlet.EchoCancellation` | [Echo cancellation](ECHO_CANCELLATION.md) | How does Martlet remove speaker sound from microphone audio? |
| `src/Martlet.Avatar*`, `src/Martlet.Rendering*` | [Avatars](AVATARS.md) | How does the character render and move? |
| `workers`, `deploy/host` | [Cluster](CLUSTER.md) | How do worker roles run on a host? |
| `scripts`, `tests` | [Validation](VALIDATION.md) | How do developers check a change locally? |
| `packaging` | [Installation](INSTALLATION_SUPPORT.md) | How do packages deliver Martlet? |

The code-area names above are navigation hints, not `Code` coverage.
Use the exact paths on each area page. The [decision map](DECISION_MAP.md)
identifies the owner of a rule. The [glossary](GLOSSARY.md) defines its terms.

## Pages

- [Accounts](ACCOUNTS.md): How do accounts and household choices work?
- [Android](ANDROID.md): What is implemented or planned for Android?
- [API](API.md): Which API contracts does Martlet expose?
- [App guides](APP_GUIDES.md): How does Martlet guide work in another app?
- [Architecture](ARCHITECTURE.md): How do the main systems fit together?
- [Audio](AUDIO.md): How do audio capture and playback work?
- [Audio notices](AUDIO_NOTICES.md): Which rights apply to audio dependencies?
- [Avatars](AVATARS.md): How does the character render?
- [Capture](CAPTURE.md): How does capture stay bounded and consented?
- [Chatterbox voice](CHATTERBOX_VOICE.md): How does the Chatterbox worker speak?
- [Cleanup](CLEANUP.md): How do developers remove their build output?
- [Cluster](CLUSTER.md): How do computers share roles and settings?
- [Companion requirements](COMPANION_REQUIREMENTS.md): What must a companion provide?
- [Components](COMPONENTS.md): Which component design note was replaced?
- [Conversation](CONVERSATION.md): How do replies and background jobs run?
- [Creations](CREATIONS.md): How does Martlet create and track output?
- [Decision map](DECISION_MAP.md): Which source owns each important decision?
- [Deep research](DEEP_RESEARCH.md): How does research work run?
- [Echo cancellation](ECHO_CANCELLATION.md): How does Martlet remove speaker sound from microphone audio?
- [Desktop app](DESKTOP_APP.md): What does the Windows desktop implement?
- [Linux and macOS desktop](DESKTOP_LINUX_MACOS.md): What works on the cross-platform desktop?
- [Diagnostics](DIAGNOSTICS.md): How does Doctor diagnose setup and local component problems?
- [Dia voice](DIA_VOICE.md): How does the Dia worker speak?
- [Discord](DISCORD.md): How does Martlet connect to Discord?
- [ElevenLabs voice](ELEVENLABS_VOICE.md): How does the ElevenLabs voice adapter work?
- [F5 voice](F5_VOICE.md): How does the F5 worker speak?
- [Game state plan](GAME_STATE_PLAN.md): Which completed plan was replaced by current game-state guides?
- [Glossary](GLOSSARY.md): Which names describe Martlet concepts?
- [GPT-SoVITS voice](GPT_SOVITS_VOICE.md): How does the GPT-SoVITS worker speak?
- [Hosted Thinking](HOSTED_THINKING.md): How does Thinking use a hosted model?
- [Installation and support](INSTALLATION_SUPPORT.md): How does setup deliver and recover components?
- [iOS](IOS.md): What is implemented or planned for iOS?
- [Local model apps](LOCAL_MODEL_APPS.md): How does Martlet find local model servers?
- [Lorebooks](LOREBOOKS.md): How does conversation use lorebook entries?
- [macOS](MACOS.md): Which macOS constraints and plans apply?
- [MCP](MCP.md): Which MCP guide covers a feature?
- [Memory](MEMORY.md): How are memories stored and retrieved?
- [Messaging](MESSAGING.md): How does messaging connect to conversation?
- [Model catalog](MODEL_CATALOG.md): How is model metadata refreshed and used?
- [Motion host](MOTION_HOST.md): How does the motion worker run?
- [Network](NETWORK.md): How do paired computers communicate?
- [OCR host](OCR_HOST.md): How does the OCR role read screen images on a host?
- [OpenAI endpoint](OPENAI_ENDPOINT.md): How do compatible endpoints stream replies?
- [Perception](PERCEPTION.md): How does perception interpret input?
- [Pictures](PICTURES.md): How does Martlet work with pictures?
- [Pictures host](PICTURES_HOST.md): How does a picture worker run?
- [Platforms](PLATFORMS.md): Which platform supports each capability?
- [Prerequisites](PREREQUISITES.md): What is bundled, installed, or supplied by the user?
- [Reading](READING.md): How does OCR read the screen and which computers can run it?
- [Recommendation design](RECOMMENDATION_DESIGN.md): Which recommendation design was replaced?
- [Recommended setups](RECOMMENDED_SETUPS.md): How does Martlet choose a setup?
- [Research ledger](RESEARCH.md): Which historical sources informed earlier design work?
- [Resource footprints](RESOURCE_FOOTPRINTS.md): How are resource measurements recorded?
- [Screen commentary](SCREEN_COMMENTARY.md): How does Martlet comment on a screen?
- [Search](SEARCH.md): How does search retrieve information?
- [Sense models](SENSE_MODELS.md): Which model takes a picture or recording?
- [Setup](SETUP.md): How do setup operations and credentials work?
- [Singing](SINGING.md): How does Martlet sing?
- [Smart home](SMART_HOME.md): How does Martlet use Home Assistant?
- [Troubleshooting](TROUBLESHOOTING.md): How do users inspect and recover problems?
- [UI design](UI_DESIGN.md): Which interface rules and current patterns apply?
- [User stories](USER_STORIES.md): Which user flows does Martlet target?
- [VAD](VAD.md): How does voice activity detection find speech?
- [Validation](VALIDATION.md): How do developers run targeted local checks?
- [Voices](VOICES.md): How do users select and test a voice route?
- [Voice latency](VOICE_LATENCY.md): What did the 2026-10-03 latency run measure?
- [Voice recognition host](VOICE_RECOGNITION_HOST.md): How does a host identify a voice?
- [Voice studio](VOICE_STUDIO.md): Which voice setup design did the current voice controls replace?
- [Welcome wizard](WELCOME_WIZARD.md): How does first-run setup work?
- [XTTS voice](XTTS_VOICE.md): How does the XTTS worker speak?
