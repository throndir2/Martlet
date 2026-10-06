# Discord

Martlet can live on Discord: it hosts its own Discord bot inside the Martlet process, chats in DMs and the server
channels you allow, joins voice channels to talk and listen, and calls the people it knows. This page records what
Discord allows, how Martlet is built around it and the work in progress.

## What Discord allows (researched October 2026)

| Wish | Possible? | How Martlet does it |
|---|---|---|
| Martlet has its own Discord account | **Bot account only.** Logging in a normal user account from code (a "self-bot") breaks Discord's Terms of Service and gets the account banned, so Martlet never does it. | Martlet's own Discord application and bot user, with its name and the character's picture. |
| Friends list, friend requests | **No.** Bots cannot have friends or accept friend requests (the relationships API rejects bots). | **People** list in Martlet (the owner plus the people Martlet knows). They DM the bot after sharing a server with it, or install Martlet's app on their own account. |
| Martlet starts a private call with someone | **Not a DM call.** Bots cannot start or join DM or group-DM calls. | Martlet **calls** someone by opening a private voice channel in its **home server** (a small server the owner makes for Martlet), DMing them a link and joining it. In **your own** DM and group-DM calls Martlet takes part through your PC instead ([below](#martlet-in-your-own-calls)). |
| Invited to a group chat (group DM) | **Commands only.** Bots cannot be members of group DMs; a user-installed app's slash commands work there. | Martlet's app installs on a person's account (user install), so `/martlet` works in any DM or group DM. It cannot read the group's messages on its own. |
| Join server voice calls, speak and listen | **Yes.** Voice is end-to-end encrypted (DAVE) for every call since March 2026; the bot library must support it. | NetCord voice (send and receive, DAVE) with `/join`, `/leave`, being dragged into a channel and following the owner. |
| Show the character as its webcam | **No.** The bot API has no camera or Go Live video; streaming video means self-botting. | The bot's avatar and banner follow the character; posted snapshots of the model. A Discord Activity (Embedded App) showing the live character in a call is a possible later step; it needs a public HTTPS address. In your own calls the character can be your webcam through OBS ([below](#martlet-in-your-own-calls)). |
| Answer in server chats, configurable | **Yes.** Needs the Message Content privileged intent, which a bot under 10,000 users turns on itself in the Developer Portal. | Per server and per channel: Off, Mentions, Sometimes (Martlet decides) or Always. |
| Create the Discord app for the user | **No API for it.** Applications are created in the Developer Portal by hand. | A guided setup with links to the portal and the exact switches, then the token goes in Windows Credential Manager and Martlet builds the invite links. |

## Setting up (owner)

Everything happens on **Companion › Discord**, which walks through these steps (its first line always says what to do next):

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) (**Open the Developer Portal**) and choose
   **New Application**. Name it after your character.
2. On **Bot**: **Reset Token**, copy it and paste it into **Bot token**, then **Save and connect**. Martlet checks that it looks
   like a bot token, keeps it in Windows Credential Manager (never in `discord.json`, logs or MCP) and connects. **Forget the
   bot** disconnects and removes it.
3. Still on **Bot**, under **Privileged Gateway Intents**, turn on **Message Content Intent**. Without it Discord refuses the
   connection (close code 4014) and the page says so, with a button to the bot's page; **Reconnect** afterwards.
4. On **Installation**, allow both **Guild Install** and **User Install**.
5. In Martlet, use **Add to a server** for each server and **Add to home server** for your own small server (it may also make
   private call channels), and **Add to my account** to use `/martlet` in DMs and group DMs. Each link has a Copy button.
6. Under **People on Discord**, set your own account: DM the bot and choose **That's me**, or paste your user ID (Discord's
   Settings › Advanced › Developer Mode, then right-click your name › Copy User ID). Pick the home server there too.

**Connection** turns the bot on or off (it then connects whenever Martlet runs) and shows its state, name, server count and any
problem. **Where Martlet chats** sets the server, DM and voice chat modes (Off, Only when mentioned, Sometimes, Always), whether
anyone may DM it, and per-channel rules picked from the connected bot's servers. MCP reads all of it (`discord_status`,
`discord_check` and the page's `Discord*` values; see [MCP](MCP.md)).

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

## Martlet in your own calls

The bot above is Martlet's own Discord account. **Martlet in your own calls** is the other way in: you are in a DM call, a
group DM call or a server call on **your own** account, and Martlet takes part through your PC (Companion › Discord ›
**Martlet in your Discord calls**, off by default; it works while always listening runs). It is the only way Martlet can be in
a DM or group-DM call or show up as a webcam, because bots can't do either.

| | Martlet's bot | Martlet in your own calls |
|---|---|---|
| Whose account | Martlet's bot user | Yours, used by you as usual; Martlet never touches it |
| Calls | Server voice channels (and its home server's private call channels) | Any call you are in: DMs, group DMs, servers |
| Hears | Each speaker's own stream, by name | The Discord app's sound on this PC, named from the Discord window |
| Speaks | As the bot | Through your microphone input (a virtual cable you install) |
| Camera | None (bots can't send video) | The character through OBS Virtual Camera (you install OBS) |

**Never automating Discord.** Driving the Discord client or web page (clicks, typing, UI automation, Playwright), using your
token or controlling your account is a self-bot and gets accounts banned. Martlet does none of it: it only hears sound this PC
already plays, looks at pixels of the Discord window on this PC and plays its voice into an audio device you chose.

**Hearing.** With *The Discord app only* (the default) the call's listener hears Discord and the processes it started through a
Windows process loopback (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` with *include target process tree*, Windows 10 2004
or later; `WasapiPcAudioSourceFactory.OpenApp`), so nothing else on the PC and never Martlet's own voice. Where that isn't
possible, or with *Everything this PC plays except Martlet*, it hears what Hear what this PC plays hears. When Discord starts
or quits, the listener opens again within a few seconds. Your own voice still comes from the microphone listener. Each line
from the call goes to Thinking marked `[PC audio]` as *Alice in the call: ...* (never as you, never into memory, Voice ID or
voice recognition, no tools or Home Assistant from it alone), with the *In your Discord call* prompt (Companion › Prompts) in
place of *What this PC plays*: it is a group conversation, so Martlet answers whenever someone says its name or talks to it
(that line goes to Thinking as soon as the call pauses), and otherwise only now and then by the chattiness pace, passing
(`[pass]`) when it has nothing to add.

**Who is speaking.** With *See who is talking in the Discord window* on, while the call's listener hears someone Martlet takes
a picture of the Discord window (PrintWindow's full-content rendering; never input) every 450 ms, at most 6 per utterance.
`DiscordSpeakingDetector` (`src\Martlet.Discord\Calls`) finds Discord's speaking green (#23A55A, older #3BA55D/#43B581) in
hollow shapes: an avatar ring (the voice channel's member list, a DM call's avatars) or a tile border (the call grid); filled
status dots and green buttons don't count. The name beside a ring (to its right, else below) or at a tile's bottom-left is read
with Windows' own OCR (`WindowsCallTextReader`, Windows.Media.Ocr through the Windows Runtime ABI, on this PC), and the name lit
in most pictures names the utterance. Your own Discord name (set on the card) is left out, since your tile lights up when you
or Martlet through your microphone talk. Pictures stay in memory only for that reading, are cleared afterwards and never go to
Thinking, cloud vision or any provider. Nobody lit, Discord minimized or no OCR language: the line is *Someone in the call*.
Call audio is deliberately never used for voice recognition, so there is no voice-based fallback.

**Speaking into the call.** Install a virtual audio cable yourself (for example VB-Audio Virtual Cable; Martlet never installs
drivers), pick its **CABLE Input** as *Martlet's voice in the call* and choose **CABLE Output** as your Input Device in Discord ›
User Settings › Voice & Video. With *Also play Martlet's voice on my usual output* (on by default) you hear it too: the call
device sets the pace and your usual output takes what fits, never holding the call back. Your own voice must reach the cable
as well (Voicemeeter, or Windows' *Listen to this device* on your microphone with the cable as playback). A chosen output that
isn't connected leaves Martlet's voice on its usual output. With *Stop talking when someone in the call talks over Martlet* (on
by default) Martlet stops a reply once someone in the call has talked over it for 700 ms (the desktop log says *Barge-in:
someone in the Discord call...*).

**Webcam: the character.** *Open camera view* moves the character into its own ordinary 16:9 window titled *Martlet camera*
(in the taskbar, not on top) on a solid green, blue, magenta or black background; its size and place are fixed and never
saved, and closing it puts the overlay back where it was. In OBS (installed by you): add a Window Capture of *Martlet camera*,
add a Chroma Key filter for the background, **Start Virtual Camera**, then pick **OBS Virtual Camera** as your camera in
Discord. Martlet ships no virtual camera driver.

**Settings and status.** `discord-calls.json` (`DiscordCallPreferences`): `On`, `Capture`, `SeeSpeakers`, `OwnerName`,
`OutputId`/`OutputName`, `AlsoSpeakers`, `BargeIn`, `CameraBackground`. The card's status lines, *Check this PC* and MCP's
`discord_call_check` (a doctor check plus a simulated call utterance) are described in [MCP](MCP.md).

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
7. **Martlet in your own calls**: companion mode on the owner's own account (above): hearing the Discord app, naming speakers
   from the Discord window, speaking through a virtual cable and the camera view for OBS.
