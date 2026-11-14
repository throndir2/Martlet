# Architecture

**Status:** Partial
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Desktop`, `src/Martlet.Companion`, `src/Martlet.Core`, `src/Martlet.Gateway.Host.Linux`

Martlet has a Windows desktop app, an early Linux and macOS companion, and a host gateway. A computer can run a companion, a host, or both.

## Components

- `Martlet.Desktop` is the Windows WPF app. It uses shared conversation, provider, audio and memory projects.
- `Martlet.Companion` is the Avalonia app for Linux and macOS. It uses the platform projects for operating-system services.
- `Martlet.Gateway.Host.Linux` hosts the gateway and connects its role projects. `deploy/host` contains the host deployment files.
- `Martlet.HostClient` connects the desktop to hosts. `Martlet.Core` owns shared settings and platform rules.
- `Martlet.Doctor` provides command-line diagnostics. [Martlet MCP Server](Martlet-MCP-Server) lets assistants use diagnostics and desktop controls.
- `Martlet.Mcp.Client` is separate from the server. It connects Martlet to tools from MCP servers.

## Platform decisions

`PlatformCatalog` describes engine support and device requirements. It can return yes, no, unknown, or not yet. Unknown is allowed with a reason; it is not the same as a verified capability.

## Shared settings

`SettingsFiles` records the scope and store of settings files. The shared-settings implementation is in `Martlet.Core`. See [One Martlet on every computer](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md#one-martlet-on-every-computer) for the sync rules and local exceptions.

## Planned and partial behavior

Support differs by platform. The catalog also records planned and impossible combinations. See [Roadmap](Roadmap) and [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md) before assuming a feature is available on a device.

The [architecture document](https://github.com/throndir2/Martlet/blob/main/docs/ARCHITECTURE.md) explains the contracts. The [decision map](https://github.com/throndir2/Martlet/blob/main/docs/DECISION_MAP.md) names their owners.
