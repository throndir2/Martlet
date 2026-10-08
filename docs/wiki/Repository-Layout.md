# Repository Layout

## Root

| Path | Purpose |
| --- | --- |
| `README.md` | User-facing overview and docs index. |
| `CONTRIBUTING.md` | Developer quick start and validation policy. |
| `AGENTS.md` | Coding-agent rules. |
| `DEVELOPMENT_PLAN.md` | Product scope, decisions and risks. |
| `Martlet.slnx` | Main solution. |
| `Directory.Build.props` | Shared build settings and version. |

## Main `src\` areas

| Area | Purpose |
| --- | --- |
| `Martlet.Desktop` | WPF desktop app. |
| `Martlet.Core` | Shared settings/domain logic. |
| `Martlet.Conversation` | Conversation runtime and background jobs. |
| `Martlet.Providers` | Provider adapters. |
| `Martlet.Audio`, `Stt.Windows`, `Sherpa`, `LocalStt`, `VoiceActivity` | Audio, speech and listening foundations. |
| `Martlet.Memory` | Local fact store and retrieval. |
| `Martlet.Discord`, `Martlet.Messaging`, `Martlet.Home` | Discord, Telegram/WhatsApp and Home Assistant. |
| `Martlet.Avatars`, `Martlet.Avatar.*` | Avatar contracts, Live2D/VRM renderer and Audio2Face. |
| `Martlet.Gateway*` | Host gateway, trust, persistence and role relays. |
| `Martlet.Host.*`, `Martlet.HostArtifacts` | Host inventory/setup/artifact inspection. |
| `Martlet.Diagnostics`, `Martlet.Doctor`, `Martlet.Support` | Diagnostics, Doctor CLI and support bundle path. |
| `Martlet.Updates`, `Martlet.Launcher`, `Martlet.Readiness` | Updates, launcher and readiness protocols. |
| `Martlet.Mcp`, `Martlet.Mcp.Client`, `Martlet.Mcp.Protocol` | Local MCP server, MCP client and protocol code. |

## Other directories

| Path | Purpose |
| --- | --- |
| `tests\` | xUnit test projects and fixtures. |
| `workers\` | Audio2Face, Chatterbox, Dia, F5, GPT-SoVITS, XTTS, Parakeet, perception, pictures and singing workers. |
| `docs\` | User/developer/design docs. |
| `deploy\host` | `martlet-host` engine, role flow and host roles. |
| `packaging\windows` | Publish, installer and release scripts. |
| `contracts\avatars`, `contracts\golden` | Contract schemas/vectors. |
| `scripts\` | Test, smoke, MCP and utility scripts. |

More detail: [Architecture](https://github.com/throndir2/Martlet/blob/main/docs/ARCHITECTURE.md), [Contributing](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md), [Host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md), [Packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md).
