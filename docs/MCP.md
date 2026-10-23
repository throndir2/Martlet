# MCP in Martlet

Martlet speaks the Model Context Protocol in both directions:

- **As a client** ([Tools while you talk](mcp/apps.md#tools-while-you-talk-mcp-client)): MCP
  servers on your PC give Martlet tools it can use while you talk.
- **As a server** ([Use Martlet from an AI assistant](mcp/start-here.md#use-martlet-from-an-ai-assistant)
  and [Local MCP control](mcp/start-here.md#local-mcp-control-windows)): `Martlet.Mcp` lets an AI
  assistant (GitHub Copilot, Claude, VS Code or any MCP client) make and change
  characters, change settings, drive the desktop window and run Martlet's
  diagnostics.

## Pages

`thinking_pool_check` checks exponential retry waits and the final failed
request. `memory_status.upkeep` shows when failed memory tidying has stopped
and gives its error. See [Thinking and models](mcp/thinking.md) and
[Apps, home and memory](mcp/apps.md).

One page for each area of `martlet_guide` (the server's own guide, which lists the tools by these areas):

| Page | What it covers |
| --- | --- |
| [Start here](mcp/start-here.md) | Connecting an assistant, the server, its flags, verifying changes and extending the server. |
| [Characters](mcp/characters.md) | Making, changing and checking characters: personalities, looks, voices, motions and touch. |
| [Settings](mcp/settings.md) | Settings, prompts, shared settings and the recommended setup. |
| [The app's window](mcp/app-window.md) | Driving the desktop window: `ui_*` tools, the character window and the notification area. |
| [Diagnostics and logs](mcp/diagnostics.md) | Doctor, logs, latency and the Linux and macOS companion app. |
| [Voice and listening](mcp/voice.md) | Voices, listening, voice recognition, singing, echo and the talk window. |
| [Thinking and models](mcp/thinking.md) | Thinking, the Thinking pool, models, vision and context. |
| [Computers, network and accounts](mcp/network.md) | Your computers, hosts, the network, accounts, sign-in and sharing. |
| [Apps, home and memory](mcp/apps.md) | MCP servers Martlet uses, the terminal, Discord, messaging, smart home, memory, reminders, check-ins and app guides. |
| [Other](mcp/other.md) | Situations, greetings, search and other checks. |

## Old links

Before, all of this was on this one page. A link to one of its old sections lands here; the section is now on the page in the list.

- <a name="use-martlet-from-an-ai-assistant"></a>[`#use-martlet-from-an-ai-assistant`](mcp/start-here.md#use-martlet-from-an-ai-assistant): [Start here](mcp/start-here.md)
- <a name="connect-an-assistant"></a>[`#connect-an-assistant`](mcp/start-here.md#connect-an-assistant): [Start here](mcp/start-here.md)
- <a name="flags"></a>[`#flags`](mcp/start-here.md#flags): [Start here](mcp/start-here.md)
- <a name="what-an-assistant-sees"></a>[`#what-an-assistant-sees`](mcp/start-here.md#what-an-assistant-sees): [Start here](mcp/start-here.md)
- <a name="characters"></a>[`#characters`](mcp/start-here.md#characters): [Start here](mcp/start-here.md)
- <a name="settings"></a>[`#settings`](mcp/start-here.md#settings): [Start here](mcp/start-here.md)
- <a name="a-running-martlet-follows-the-changes"></a>[`#a-running-martlet-follows-the-changes`](mcp/start-here.md#a-running-martlet-follows-the-changes): [Start here](mcp/start-here.md)
- <a name="limits"></a>[`#limits`](mcp/start-here.md#limits): [Start here](mcp/start-here.md)
- <a name="tools-while-you-talk-mcp-client"></a>[`#tools-while-you-talk-mcp-client`](mcp/apps.md#tools-while-you-talk-mcp-client): [Apps, home and memory](mcp/apps.md)
- <a name="terminal"></a>[`#terminal`](mcp/apps.md#terminal): [Apps, home and memory](mcp/apps.md)
- <a name="thinking-longer"></a>[`#thinking-longer`](mcp/thinking.md#thinking-longer): [Thinking and models](mcp/thinking.md)
- <a name="thinking-pool"></a>[`#thinking-pool`](mcp/thinking.md#thinking-pool): [Thinking and models](mcp/thinking.md)
- <a name="thinking-requests"></a>[`#thinking-requests`](mcp/thinking.md#thinking-requests): [Thinking and models](mcp/thinking.md)
- <a name="what-was-sent-a-messages-context"></a>[`#what-was-sent-a-messages-context`](mcp/thinking.md#what-was-sent-a-messages-context): [Thinking and models](mcp/thinking.md)
- <a name="image-and-audio-models"></a>[`#image-and-audio-models`](mcp/thinking.md#image-and-audio-models): [Thinking and models](mcp/thinking.md)
- <a name="live-floor-the-live-turn-first"></a>[`#live-floor-the-live-turn-first`](mcp/thinking.md#live-floor-the-live-turn-first): [Thinking and models](mcp/thinking.md)
- <a name="searching-past-conversations"></a>[`#searching-past-conversations`](mcp/apps.md#searching-past-conversations): [Apps, home and memory](mcp/apps.md)
- <a name="creations"></a>[`#creations`](mcp/apps.md#creations): [Apps, home and memory](mcp/apps.md)
- <a name="reading"></a>[`#reading`](mcp/thinking.md#reading): [Thinking and models](mcp/thinking.md)
- <a name="picture-making"></a>[`#picture-making`](mcp/thinking.md#picture-making): [Thinking and models](mcp/thinking.md)
- <a name="motion"></a>[`#motion`](mcp/characters.md#motion): [Characters](mcp/characters.md)
- <a name="body-motions"></a>[`#body-motions`](mcp/characters.md#body-motions): [Characters](mcp/characters.md)
- <a name="reminders"></a>[`#reminders`](mcp/apps.md#reminders): [Apps, home and memory](mcp/apps.md)
- <a name="check-ins"></a>[`#check-ins`](mcp/apps.md#check-ins): [Apps, home and memory](mcp/apps.md)
- <a name="local-mcp-control-windows"></a>[`#local-mcp-control-windows`](mcp/start-here.md#local-mcp-control-windows): [Start here](mcp/start-here.md)
- <a name="elevenlabs-voice"></a>[`#elevenlabs-voice`](mcp/voice.md#elevenlabs-voice): [Voice and listening](mcp/voice.md)
- <a name="latency"></a>[`#latency`](mcp/diagnostics.md#latency): [Diagnostics and logs](mcp/diagnostics.md)
- <a name="martlet-for-linux-and-macos"></a>[`#martlet-for-linux-and-macos`](mcp/diagnostics.md#martlet-for-linux-and-macos): [Diagnostics and logs](mcp/diagnostics.md)
- <a name="verifying-changes-with-martlet-mcp"></a>[`#verifying-changes-with-martlet-mcp`](mcp/start-here.md#verifying-changes-with-martlet-mcp): [Start here](mcp/start-here.md)
- <a name="extending-the-server"></a>[`#extending-the-server`](mcp/start-here.md#extending-the-server): [Start here](mcp/start-here.md)
