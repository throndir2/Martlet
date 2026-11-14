# Repository Layout

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src`, `tests`, `scripts`, `packaging`, `deploy/host`

Use the [decision map](https://github.com/throndir2/Martlet/blob/main/docs/DECISION_MAP.md) to find the owner of a rule. Use the [glossary](https://github.com/throndir2/Martlet/blob/main/docs/GLOSSARY.md) for its names.

## Root

| Path | Purpose |
| --- | --- |
| `README.md`, `CHANGELOG.md` | User overview and release notes. |
| `CONTRIBUTING.md`, `AGENTS.md` | Developer and coding agent instructions. |
| `Martlet.slnx` | Main solution. |
| `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` | Shared build settings and package versions. |
| `global.json` | Pinned .NET SDK. |
| `.github/workflows/windows-release.yml` | Manual release workflow. |

## Source projects

| Path | Purpose |
| --- | --- |
| `src/Martlet.Desktop` | Windows desktop app. |
| `src/Martlet.Companion`, `src/Martlet.Companion.Platform` | Linux and macOS companion and platform contracts. |
| `src/Martlet.Platform.Linux`, `src/Martlet.Platform.MacOS` | Platform services. |
| `src/Martlet.Core`, `src/Martlet.Conversation`, `src/Martlet.Providers` | Shared rules, conversation and model providers. |
| `src/Martlet.Gateway.Host.Linux`, `src/Martlet.HostClient` | Host gateway and its client. |
| `src/Martlet.Doctor`, `src/Martlet.Diagnostics` | Command-line diagnostics and probes. |
| `src/Martlet.Mcp`, `src/Martlet.Mcp.Protocol`, `src/Martlet.Mcp.Client` | Martlet's MCP server, its protocol and tools, and its separate MCP client. |
| `src/Martlet.Avatar.RendererHost`, `src/Martlet.Avatar.Vrm`, `src/Martlet.Avatar.Live2D` | Character renderer and web packages. |

## Other folders

| Path | Purpose |
| --- | --- |
| `tests` | Test projects and known failures. |
| `workers` | Worker implementations and their tests. |
| `deploy/host` | Host deployment files and roles. |
| `packaging` | Platform packaging scripts and notices. |
| `scripts` | Development, validation and release scripts. |
| `docs` | Developer documentation and wiki source. |
| `docs/wiki` | Source of this wiki. `scripts/Publish-Wiki.ps1` checks links and publishes it. |

See [Source Projects](Source-Projects) for the other projects, [Architecture](Architecture), [Developer Guide](Developer-Guide) and the [host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md).
