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
  speech output back to 48 kHz Opus (see [Voice](#voice)).
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
- **History:** each answered text message (and `/martlet`) is recorded in the record of conversations while it is kept,
  in one conversation per channel or DM, with the server, channel and the Discord IDs of the person's message and Martlet's
  reply pieces (`DiscordService.History.cs`). Deleting or editing it in Companion › Memory › Open conversation history
  deletes or edits it in Discord while the bot is online: Martlet's own messages always, the person's only in a server (with
  Manage Messages), never in a DM, through a queue at one change a second that waits out 429s
  ([Conversation history](MEMORY.md#conversation-history)). `/martlet` answers are interaction follow-ups and stay in
  Discord. The talk window never recalls Discord exchanges.
- Status: Companion › Discord's `DiscordTextStatus` line (counts of seen, considered, answered, passed, dropped and failed
  messages, the last reply's place kind and last problem) and *Discord text: ...* lines in the desktop log.

## Reply engine

`DiscordService.Replies` is a `DiscordReplyEngine` (`src\Martlet.Desktop`), wired at start (`DiscordService.UseReplies`).
Callers pass each `DiscordTurn` with its place's chat `Mode` (`DiscordPreferences.TextMode` or `VoiceChat`); `ReplyAsync`
returns the reply, or null to stay quiet.

- **Discord side** (`DiscordReplier`, `src\Martlet.Discord\DiscordReplies.cs`): history per place (`DiscordPlace.Key`, the
  last 40 lines of up to 1,500 characters, 200 places, in memory only and merged with the caller's `Recent` lines); one
  request per place at a time and two at once; the turn shaped for several people (`DiscordPrompts`: each line "Name: text",
  everything said since Martlet's last reply in one message, Martlet's own replies as its messages, and instructions naming
  the DM, server channel or voice call and the owner); the answer cleaned (`DiscordReplyText`: a copied "Martlet:" removed,
  at most 2,000 characters for text, plain sentences of at most 600 characters without markdown, links or emoji for voice).
- **Speaking up** (`DiscordAmbientGate`): an unaddressed turn in Sometimes mode goes to the model only past a cooldown since
  Martlet's last message there (90 s text, 30 s voice), at most 6 unprompted replies an hour per place, never two unprompted
  replies without 3 lines from others in between, and then always when it names Martlet, otherwise 30% of the time. The
  model may still answer `[pass]` (said in that turn's note, so the place's instructions never change). An ambient turn
  never waits behind another in the same place.
- **Thinking side**: each turn reads the saved settings and builds its request with the live conversation's own
  `LiveConversationConfiguration.Request`: the persona and its style, lorebooks, the notes prompt and the reply length prompt,
  with the Discord framing after the persona. Remembered facts go only to the owner's own DMs (others would read them). No
  pictures, recordings, tools, Home Assistant or past conversations. Requests run on the engine's own two text-only runtimes,
  never the local conversation's, so its requests, history and prompt cache stay as they were.
- **Sharing a model with the local conversation**: when Thinking runs on this PC or the home network (Ollama, a paired host,
  a LAN server), Discord requests go one at a time and only after the local conversation has been quiet for 20 s; an ambient
  turn is skipped rather than waiting, an addressed one waits up to 90 s. If the local conversation starts a reply while a
  Discord request runs, the Discord request is stopped at once (an addressed turn asks again once it is quiet). A server
  that keeps one prompt cache may still have to read the local conversation again after a Discord reply; set
  `OLLAMA_NUM_PARALLEL=2` (or more) so Ollama keeps both. A cloud route is used as it is.
- **Observability**: `discord-replies.json` in the data directory (counts, times, the last skip and error codes, latency and
  prompt-cache use; never what was said) and one `Discord reply (...)` desktop log line per reply. MCP's
  `discord_reply_status` reads them and `discord_reply_check` rehearses the Discord side against a loopback fixture (see
  [MCP](MCP.md)).

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
  they aren't in the server, and joins the channel through Discord voice (`DiscordService.JoinVoiceAsync`; when it can't, such
  as without libdave, the owner is told and the friend still has the link). When everyone else leaves, Martlet leaves too
  (`VoiceEmptied`) and the channel is removed as soon as Martlet has left it (`VoiceLeft`); otherwise it is removed 2 minutes
  after everyone leaves, or after 10 minutes
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
(in the taskbar, not on top) on a solid green, blue, magenta or black background, or on a picture; the window's size is fixed
and its place is never saved, and closing it puts the overlay back where it was, zoomed as it was. For a picture, *Choose a picture file...* takes a
PNG, JPEG or WebP (24 MB at most), *A picture from Creations...* uses one Martlet drew before, and, while Companion › Pictures
has a place, *Draw it* draws a 16:9 picture from your instruction there (a cloud provider asks first, as it may cost money)
and keeps it in Creations too. The picture is copied to `discord-camera-background` in Martlet's data folder, fills the window
and becomes the *Picture* background choice; if that file is gone the camera goes back to green. In OBS (installed by you):
add a Window Capture of *Martlet camera*, add a Chroma Key filter for a color background (a picture needs none), **Start
Virtual Camera**, then pick **OBS Virtual Camera** as your camera in Discord. Martlet ships no virtual camera driver.

**Framing the character.** Frame the character in the camera window before OBS captures it: drag it anywhere in the window,
use the mouse wheel to zoom it in (up to 16×) or out (down to a quarter of its fitted size) around the cursor, nudge it with
the arrow keys (Shift for 1 pixel), and press Home or 0 to reset it; Shift+drag moves the window. The Discord card's
*Bigger*, *Smaller*, *Left*, *Right*, *Up*, *Down* and *Reset framing* buttons do the same. The framing is saved in
`discord-calls.json`, the camera view opens framed that way next time, and a change of background keeps it.

**Martlet changes its own background.** While the mode is on, every reply on a route that does function calling also gets
`set_camera_background`: when someone in the call (or you) asks for a different background, or a new one fits, Martlet
switches to a plain color (`color`), one of its pictures (`picture`, a creation id from `list_creations`), or draws a new
16:9 picture (`draw`, while Companion › Pictures has a place) as an ordinary `picture-N` background job that is kept in
Creations and becomes the background when it's ready. A picture goes through the same `discord-camera-background` file
as the card's choices, so it stays the background until changed, and shows at once while the camera view is open.

**Settings and status.** `discord-calls.json` (`DiscordCallPreferences`): `On`, `Capture`, `SeeSpeakers`, `OwnerName`,
`OutputId`/`OutputName`, `AlsoSpeakers`, `BargeIn`, `CameraBackground`, `CameraPicture` (`File`, `Creation` or `Drawn`),
`CameraZoom` (1 fits the view's height), `CameraX`/`CameraY` (the character's middle from the view's center, as fractions of its
width and height, +x right, +y up). The card's status lines, *Check this PC* and MCP's
`discord_call_check` (a doctor check plus a simulated call utterance) are described in [MCP](MCP.md).

## Voice

`DiscordService.Voice.cs` connects NetCord voice to `DiscordVoiceConversation` (`src\Martlet.Discord`), which holds the audio
path and decisions so tests and MCP (`discord_voice_check`) run them with a fake transport. Martlet never uses this PC's
microphone or speakers for Discord.

- **Joining:** `/join` (servers only) joins the voice channel the person using it is in; `/leave` leaves. Martlet also joins a
  channel when someone moves it there (Move Members) and, with `VoiceFollowOwner` in `discord.json`, follows the owner into
  any voice channel of a server it is in. It leaves after `VoiceLeaveAloneMinutes` (default 2) alone in a channel, when the
  bot stops, or when Discord closes the connection. One channel per server (Discord's rule). The companion feature calls
  `DiscordService.JoinVoiceAsync(guildId, channelId, token)` and `LeaveVoiceAsync(guildId)`, and can watch `VoiceLeft` and
  `VoiceEmptied` (guild, channel). `/join` and `/leave` go through the shared `Bot.Commands` registry (guild install,
  servers only). Voice chat Off refuses `/join`.
- **Listening:** received Opus packets are copied into a bounded queue (Discord's receive loop never waits). Per speaker (SSRC,
  mapped to a user from the connection's Speaking events): packets are put back in order (late ones dropped, a frame still
  missing after two later ones is concealed), decoded at 48 kHz stereo, mixed to mono and low-passed down to 16 kHz, and an
  utterance ends after 0.7 s of silence or of no packets (Discord clients stop sending in silence). Under 0.25 s of voice is
  dropped; utterances are capped at 25 s.
- **Speech-to-text never uses the cloud** for other people's voices: Parakeet on this PC (Listening's model, or a downloaded
  Parakeet) or the paired computer Listening uses, chosen like Add a voice does (`RecordingTranscriber`). Without either,
  the status says to download Parakeet. While the local conversation is replying, Discord's transcription waits for it (up
  to 15 s), so Discord never holds the shared model when the owner talks to Martlet here; Discord utterances are transcribed
  one at a time.
- **Addressed** means one of Martlet's names said as a word (Martlet, the bot's names and nickname, the personas'), or the
  speaker being the only person in the call with Martlet (one-to-one, such as the owner alone with it). The voice chat mode
  (`VoiceChat`, default Sometimes) and `DiscordChatRules` decide whether the turn goes to the reply engine; in Sometimes the
  engine may stay quiet on unaddressed turns, so a group call gets occasional comments. Each call keeps its last 12 lines.
- **Speaking:** the reply is cut at sentence ends into segments of up to 400 characters, spoken by the voice engine of Its voice on this PC's host
  service or a paired host (Chatterbox Nano runs on a processor too; with a cloud voice or no voice, Discord voice says so
  instead of speaking), upsampled
  to 48 kHz stereo, encoded to 20 ms Opus frames (Concentus, 64 kb/s) and written to NetCord's voice stream, which paces them
  at real time, with the Speaking flag on and five silence frames after.
- **Barge-in:** when someone else talks over Martlet with at least 0.4 s of voice, it stops speaking at once; what they said
  is then heard as their turn.
- **Natives:** Discord requires DAVE end-to-end encryption on every call since March 2026. NetCord 1.0.0-beta.28 implements
  it by calling **libdave**; the build downloads Discord's official unmodified libdave v1.2.1 Windows x64 release
  (`libdave-Windows-X64-boringssl.zip`, SHA-256 `eb4a7bbc…ae8ea2f`, pinned in `Martlet.Discord.csproj` and
  `packaging\windows\toolchain.json`) and ships `libdave.dll` beside `Martlet.Desktop.exe` (it statically links mlspp,
  nlohmann/json and BoringSSL; only `KERNEL32.dll` is imported). Notices: `src\Martlet.Discord\DISCORD-VOICE-NOTICES.txt`,
  shipped as `notices\Discord-Voice-NOTICES.txt`. Opus is managed (Concentus), so no `opus.dll`; transport encryption is
  `aead_aes256_gcm_rtpsize` with .NET's AES-GCM, so no `libsodium` (NetCord only needs it for the XChaCha20 fallback on
  processors without AES support). DSharpPlus voice was not needed.
- Status: Companion › Discord's `DiscordVoiceStatus` line (`DiscordService.VoiceSummary`: channel, counts of speakers heard,
  utterances transcribed and replies spoken, DAVE, libdave and the last problem) and *Discord voice: ...* lines in the
  desktop log.

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
