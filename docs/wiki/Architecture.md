# Architecture

Martlet is one app that runs on each of the owner's computers. A **companion PC** is a computer where you talk to Martlet. A **host PC** lends its graphics card and processor to the app. One computer can be both. Adding a host adds capabilities to the whole app.

## The apps

| App | Where it runs | What it does |
| --- | --- | --- |
| Windows desktop (`Martlet.Desktop`, WPF) | Windows 10 and 11, x64, and Windows 11 on Arm through emulation | The full companion: UI, consent, capture, playback, the character, the conversation and the setup of hosts. |
| Linux and macOS companion (`Martlet.Companion`, Avalonia 12) | Linux x64 and arm64, macOS 14 or later | An early companion: typed and push-to-talk conversation, OpenAI listening and speaking, the character. `Martlet.Platform.Linux` and `Martlet.Platform.MacOS` supply the hotkey, overlay, keys, screen capture, tray and start at login. |
| Host gateway (`martlet-host`, `Martlet.Gateway.Host.Linux`) | Linux (Docker or native with systemd), Windows with Docker Desktop, macOS (a launchd agent) | Paired TLS, capabilities, readiness and job transport to the host roles. |
| Workers (`workers\`, `deploy\host\roles`) | Hosts | One isolated runtime for each engine: Ollama, whisper, Parakeet, voices, Audio2Face, singing, picture making and more. |
| Character renderer | Each companion | VRM and Live2D web bundles: a private WebView2 process on Windows, WebKitGTK or WKWebView on Linux and macOS. |
| Doctor and the MCP server | Windows (Doctor also has a portable, text-only build) | Read-only diagnostics, and [Martlet's MCP server](Martlet-MCP-Server) for AI assistants and validation. |

Most libraries under the apps are portable `net10.0`: Core, Conversation, Providers, Memory, Audio and others. The two companions share them.

## Platform guardrails

`PlatformCatalog` (`src\Martlet.Core\Platforms`) decides what each platform and device can run. Menus show an engine that a computer cannot run as disabled, with the reason. A choice from another computer that this computer cannot run is refused with the reason. It is never silently changed.

## Trust and privacy boundaries

- The microphone, camera and screen stay off until the user starts them.
- API keys stay in the operating system's store: Windows Credential Manager, the macOS Keychain or the Linux Secret Service.
- Screenshots stay in memory only.
- Tool output is untrusted data. Tools ask before they run.
- Work never moves to a cloud provider silently.

## Pairing and the Martlet network

Each host has a stable identity and TLS key. Pairing pins the host's key and gives the desktop a scoped credential. Pair a host once, and every member desktop of your Martlet network pairs with it. Pairing never gives shell or Docker access. Changes on a host are a closed set of commands that the host's own Martlet runs.

## One app: shared settings

Settings, API keys, people, voices, characters, memories and creations travel as last-writer-wins documents through the paired hosts. Each host keeps a private copy. Hosts do not sync with each other: the desktops carry changes between them. Only facts about one computer stay on it: its devices, installed engines, pairings, screens and startup.

- `SettingsFiles` (`src\Martlet.Core\Settings`) lists each settings file, its scope (shared, account or local) and its store.
- `AppSettingsSections` and the desktop's `DesktopSections()` carry the shared sections.
- `SettingScopes` decides if a shared setting belongs to the household or to each person.

## Accounts and the household

Each person in the home has an **account**. Windows signs the person in by default. A person can add a password, an authenticator app and other sign-ins. The household shares the computers, hosts, routes, paid API keys, Home Assistant and the voices Martlet knows. Each person keeps their own characters, prompts, theme, reminders, creations and conversations. Memories are in **memory spaces**: the household's, each person's and each shared character's. See [Accounts and Household](Accounts-and-Household).

## The Thinking pool

The live conversation uses its own Thinking route and prompt cache. Background work goes to the **Thinking pool**: thinking longer, web research, screen and sound summaries, remembering, naming, check-ins and the judges. `ThinkingJobBoard` (`src\Martlet.Conversation\ThinkingPool.cs`) chooses a free member for each job by its kind, priority and the quick or long jobs that the member takes. The conversation always comes first: background work on the computer that the conversation uses waits or pauses while you talk. Paired hosts with a Thinking pool role join by themselves. See [Thinking pool](Thinking#thinking-pool).

## Sharing outside the network

- **Friends.** A host can let a friend's Martlet use its engines: thinking, listening, speaking, lip-sync and reading. A friend's computer never joins your network and never sees your settings, memories, voices or keys.
- **OpenAI-compatible endpoint.** Each host serves `/v1` to people who have a key. A **front door** host runs a request on its own model, or sends it on over pinned TLS to a host of your network that has the model. These requests rank below the owner's work. Your cloud models and keys are never shared.

See [Sharing with Friends](Sharing-with-Friends).

## Turn execution

The start of each Thinking request stays stable, so prompt caches are used again. The request then adds the persona and style, tools, memory, lorebooks, recent conversation and the current message, within budgets. A change must never make the time to Martlet's first audible word longer. Stop and cancel prevent old text or audio from continuing.

## Degradation

- When speaking fails, Martlet replies as text.
- Lip-sync falls back to the voice's loudness.
- Failover moves a job only between your own hosts that run the same engine.
- When the Listening route fails, a downloaded Parakeet model on this PC can hear you. Nothing is sent anywhere.
- Text-only use always works.

More detail: [Architecture](https://github.com/throndir2/Martlet/blob/main/docs/ARCHITECTURE.md), [Decision map](https://github.com/throndir2/Martlet/blob/main/docs/DECISION_MAP.md), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md), [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md), [Accounts](https://github.com/throndir2/Martlet/blob/main/docs/ACCOUNTS.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [OpenAI-compatible endpoint](https://github.com/throndir2/Martlet/blob/main/docs/OPENAI_ENDPOINT.md), [Linux and macOS companion](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_LINUX_MACOS.md), [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md).
