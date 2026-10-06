# Discord

Martlet can live on Discord: it hosts its own Discord bot inside the Martlet process, chats in DMs and the server
channels you allow, joins voice channels to talk and listen, and calls the people it knows. This page records what
Discord allows, how Martlet is built around it and the work in progress.

## What Discord allows (researched October 2026)

| Wish | Possible? | How Martlet does it |
|---|---|---|
| Martlet has its own Discord account | **Bot account only.** Logging in a normal user account from code (a "self-bot") breaks Discord's Terms of Service and gets the account banned, so Martlet never does it. | Martlet's own Discord application and bot user, with its name and the character's picture. |
| Friends list, friend requests | **No.** Bots cannot have friends or accept friend requests (the relationships API rejects bots). | **People** list in Martlet (the owner plus the people Martlet knows). They DM the bot after sharing a server with it, or install Martlet's app on their own account. |
| Martlet starts a private call with someone | **Not a DM call.** Bots cannot start or join DM or group-DM calls. | Martlet **calls** someone by opening a private voice channel in its **home server** (a small server the owner makes for Martlet), DMing them a link and joining it. |
| Invited to a group chat (group DM) | **Commands only.** Bots cannot be members of group DMs; a user-installed app's slash commands work there. | Martlet's app installs on a person's account (user install), so `/martlet` works in any DM or group DM. It cannot read the group's messages on its own. |
| Join server voice calls, speak and listen | **Yes.** Voice is end-to-end encrypted (DAVE) for every call since March 2026; the bot library must support it. | NetCord voice (send and receive, DAVE) with `/join`, `/leave`, being dragged into a channel and following the owner. |
| Show the character as its webcam | **No.** The bot API has no camera or Go Live video; streaming video means self-botting. | The bot's avatar and banner follow the character; posted snapshots of the model. A Discord Activity (Embedded App) showing the live character in a call is a possible later step; it needs a public HTTPS address. |
| Answer in server chats, configurable | **Yes.** Needs the Message Content privileged intent, which a bot under 10,000 users turns on itself in the Developer Portal. | Per server and per channel: Off, Mentions, Sometimes (Martlet decides) or Always. |
| Create the Discord app for the user | **No API for it.** Applications are created in the Developer Portal by hand. | A guided setup with links to the portal and the exact switches, then the token goes in Windows Credential Manager and Martlet builds the invite links. |

## Setting up (owner)

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and choose **New Application**. Name it after
   your character.
2. On **Bot**: **Reset Token**, copy it and paste it into Martlet (Companion › Discord). Martlet keeps it in Windows Credential
   Manager.
3. Still on **Bot**, under **Privileged Gateway Intents**, turn on **Message Content Intent**.
4. On **Installation**, allow both **Guild Install** and **User Install**.
5. In Martlet, use **Add to a server** for each server (and your home server), and **Add to my account** to use `/martlet` in DMs
   and group DMs.

## Architecture

- `src\Martlet.Discord` (`net10.0`, NetCord): `DiscordBot` (the gateway connection, its status and events),
  `DiscordPreferences` (`discord.json` in the data directory: application ID, credential reference, people, chat modes, channel
  rules, home server), `DiscordInvite` (portal and invite links, permission bits) and the reply contract
  (`IDiscordReplyEngine`, `DiscordTurn`, `DiscordChatRules`).
- `src\Martlet.Desktop\DiscordService*.cs`: the desktop host. `DiscordService.cs` owns the preferences, the token in
  `WindowsCredentialStore` (`Martlet/v3/discord-bot/<id>`) and the bot's lifetime (connects at start when enabled). Text chat,
  voice and companion features are partial files that attach to `DiscordService.Bot`.
- Replies go through `IDiscordReplyEngine`, separate from the local `LiveConversationController`, with its own history per
  Discord place. It reuses the persona, Thinking route and memory, and must never add latency to, block or evict the local
  conversation (see AGENTS.md).
- Voice converts Discord's 48 kHz stereo Opus to Martlet's canonical 16 kHz mono PCM per speaker for speech-to-text, and
  speech output back to 48 kHz Opus.
- **Slash commands** share one registry: each feature calls `Bot.Commands.Add(command, handler)` (build with
  `DiscordCommands.Slash`, which allows guild and user installs in servers, the bot's DMs and other DMs and group DMs). After
  every Ready the bot registers the whole list with **one** `BulkOverwriteGlobalApplicationCommandsAsync`, so no feature's
  registration removes another's, and dispatches each interaction by command name. Never register commands yourself.

## Text chat

`DiscordService.Text.cs` adapts NetCord messages and interactions to `DiscordTextChat` (`src\Martlet.Discord`), which holds
the decisions so tests and MCP (`discord_text_check`) run them with a fake transport:

- **Place and speaker:** a DM, or a server channel or thread (named `#channel in Server`); the speaker's server nickname,
  global name or username, and whether it is the owner (`OwnerUserId`).
- **Addressed** means a DM, an @mention of the bot or its own managed role, a reply to one of Martlet's messages, or one of
  its names said as a word (the bot's username, global name and server nickname, and the personas' names).
- **Chat mode** comes from `DiscordPreferences.TextMode` (DMs: the owner, the People list or anyone when allowed; servers:
  channel rule, else the server default). `DiscordChatRules.Considers` decides whether the turn goes to the reply engine; in
  Sometimes the engine may stay quiet on unaddressed turns.
- Other bots, webhooks, system messages and Martlet itself are never answered (their lines still count as context).
- Each place keeps its last 20 lines (Martlet's replies included, up to 200 places) for `DiscordTurn.Recent`, and turns run
  one at a time per place. Martlet shows **typing** while it thinks (up to 2 minutes).
- **Stale turns:** when a newer message in the same place arrives before a reply is sent, the older turn is dropped and the
  newer one answers (taking over "addressed" and the message to reply to); after two drops in a row the next reply is sent
  anyway. `DiscordTextOptions.DropStaleTurns` turns this off.
- **Sending:** in channels an addressed turn is answered as a Discord reply; DMs and ambient answers are plain messages.
  Replies are split under 2,000 characters (at paragraph, line, sentence or word breaks), `@everyone`, `@here` and role
  mentions are neutralized, and messages go with no mentions allowed except the replied-to person. At most one message per
  1.5 s per channel; NetCord waits out Discord's 429s.
- **`/martlet message:<text>`** works in servers, Martlet's DMs and (with the app on the person's account) any DM or group
  DM. In a server where Martlet's bot is installed that channel's mode applies; anywhere else the DM allowance does. It
  defers ("Martlet is thinking...") and follows up with the reply, quoting the words asked. In a group DM Martlet sees only the
  command text, never the group's messages.
- **`/chatmode mode:<Off|Mentions|Sometimes|Always|Server default>`** (servers only; the owner or someone with Manage
  Channels) saves the current channel's rule in `discord.json`.
- Status: Companion › Discord's `DiscordTextStatus` line (counts of seen, considered, answered, passed, dropped and failed
  messages, the last reply's place kind and last problem) and *Discord text: ...* lines in the desktop log.

## Companion presence

`DiscordCompanion` (`src\Martlet.Discord`, rules in `DiscordCompanion.cs`) holds the decisions; `DiscordService.Companion.cs`
wires it to the bot (`NetCordCompanionTransport`) and `MainWindow.Discord.Companion.cs` to the character, the conversation and
Companion › Discord's **Friends and calls** card. Its state is `discord-companion.json` beside `discord.json` (waiting requests,
recent declines, call channels it made, when the picture last changed; no secrets). MCP's `discord_companion_check` rehearses
it all against an in-memory Discord.

- **Friends:** bots can't have friends, so the People list is Martlet's friends list. Anyone can **`/friend ask`**; the request
  waits (at most 50) on the card until the owner approves (they get a welcome DM and join People, allowed to call) or declines
  (they can ask again after 7 days). The owner can also add someone by user ID. **`/friend remove`** takes the person off the
  list and drops any waiting request, so Martlet neither DMs nor calls them; the owner's Remove does the same. A friend whose
  name is exactly one named voice in Companion › People shows "Martlet also knows them by voice" (a display link only).
- **Calling:** from the card's Call button, the owner's **`/call person:<name>`** or by asking Martlet in the local
  conversation ("call Ana": the `call_on_discord` tool, offered while Discord is set up with a home server and someone who
  takes calls, so the tool list doesn't change while the bot reconnects). Martlet makes (or reuses) the voice channel
  "*Character* & *Name*" in the **home server** with permission overwrites: @everyone denied View Channel and Connect; the
  friend and the owner allowed View Channel, Connect, Speak and Voice Activity; the bot also Manage Channels, Move Members and
  Create Invite. It DMs the friend a jump link (*Hiyori is calling you — join here: ...*), with a one-use, one-day invite when
  they aren't in the server, and joins the channel through Discord voice (`DiscordService.JoinCall`; until voice is available
  the DM says Martlet will join as soon as it can). The channel is removed 2 minutes after everyone leaves, or after 10 minutes
  when nobody came (checked every minute and at each connection). A friend with "Martlet may call them" off is never called.
- **Presence:** the bot's status follows Martlet every 15 s, sent only when it changes and at most every 20 s: on a call
  (Online, *On a call with Ana*), paused (Idle, *Taking a break*), talking (Do Not Disturb, *Talking with you*), the PC idle for
  10 minutes (Idle, *Away for a bit*), watching (*Watching along*), listening (*Listening*), else *Hanging out*.
- **Picture instead of a webcam:** bots can't send camera video or Go Live, so the bot's **avatar** follows the character: a
  head-and-shoulders snapshot from the character's renderer (the renderer protocol's `snapshot` command: WebView2's capture,
  cropped to the character's opaque pixels, at most 512 px) or, while it is hidden, a VRM model's own thumbnail. It changes only
  when the character changed, at most every 30 minutes (Update now: 10 minutes), because Discord allows few avatar changes.
  Banners: the same change sets the bot's profile banner to the whole character when it is on screen (Discord crops it). **`/selfie`** posts a picture of the whole character
  as it looks now (anyone in a server; friends in DMs and group DMs).
- **Live camera video is impossible for a bot.** A Discord **Activity** (an Embedded App iframe in a call showing the live
  character) is the possible later step; it needs a public HTTPS address serving the renderer.

## Workstreams

1. **Foundation** (merged first): the project, NetCord, preferences, bot host, token vault, reply contract and this page.
2. **Setup and status**: Companion › Discord page (guided setup, token, invite links, on/off, status, chat modes, people),
   MCP `SafeValues`/`SafeClicks`, a Doctor probe and `docs\MCP.md`.
3. **Reply engine**: `IDiscordReplyEngine` over Martlet's persona, Thinking route and memory, per-place history and the
   "should I say something" decision for ambient turns.
4. **Text chat**: server channels, DMs and threads with chat modes, mentions and replies, typing, rate limits and
   `/martlet` for the user-installed app.
5. **Voice**: `/join`, `/leave`, follow the owner and being moved in, per-speaker listening and speech-to-text, speaking
   replies, Sometimes mode in group calls, DAVE native libraries.
6. **Companion presence**: the People list as Martlet's friends, calling someone through its home server, presence status
   and the bot's avatar following the character.
