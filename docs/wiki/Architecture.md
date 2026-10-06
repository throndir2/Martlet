# Architecture

Martlet is one app across many computers. The Windows desktop owns consent, capture, playback, UI and orchestration. Optional hosts lend heavy work through paired gateways.

## Components

| Component | Responsibility |
| --- | --- |
| Desktop shell | WPF UI, setup, status, tray, hotkeys and consent. |
| Audio engine | WASAPI/NAudio, conversion, meters and playback. |
| Session core | Turn state, policy, routing, cancellation and settings. |
| Provider adapters | Vendor schemas, auth, limits and error translation. |
| Doctor | Shared bounded diagnostic probes. |
| Host gateway | Paired TLS/auth, capability/readiness and job transport. |
| Workers | Isolated engine runtimes. |
| Memory | Local fact store, retrieval, export and sync. |
| Avatar renderer | Live2D/VRM rendering and animation protocol. |

## Boundaries

Capture is stopped until the user starts it. Credentials are in OS storage. Screens are disabled until Vision starts. Tool output is untrusted data. Jobs do not silently move to cloud providers.

## Pairing

Hosts have stable identity and TLS keys. Pairing pins the host and exchanges a scoped credential. Pairing never grants shell or Docker access; host lifecycle operations are closed commands.

## Turn execution

Requests keep stable instructions first for prompt caching, then add persona/style, tools, memory, lore, recent conversation and the current message within budgets. Stop/cancel prevents stale text or audio from continuing.

## Degradation

Voice failure leaves text. Lip-sync can fall back to loudness. Failover moves only between your hosts running the same engine. Text-only use remains supported.

More detail: [Architecture](https://github.com/throndir2/Martlet/blob/main/docs/ARCHITECTURE.md), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md), [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md).
