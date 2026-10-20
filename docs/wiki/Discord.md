# Discord

Martlet can be on Discord in two ways:

- **Martlet's own bot** chats in direct messages, channels and threads, joins
  server voice calls and calls you in private.
- **Martlet in your Discord calls** joins the calls you are already in, through
  your PC's sound. There she can also show up on camera as her character, with
  a background.

Open **Companion › Discord**. Its first line always says what to do next.

## What Discord allows

Martlet uses a bot account. She never logs in as a person, because that breaks
Discord's rules and gets accounts banned. So:

- A bot can't have friends. The **Friends and calls** card is her friends list.
- A bot can't start or join DM calls or group DM calls. She calls you in a
  private voice channel in her home server instead.
- A bot can't send camera video. Her bot's picture follows the character. To
  show her live, use **Martlet in your Discord calls** (below).
- In a group DM, the bot answers only `/martlet`. It can't read the group's
  messages.

## Set up the bot

1. Press **Open the Developer Portal** and choose **New Application**. Name it
   after your character.
2. On **Bot**, press **Reset Token** and copy the token. In Martlet, paste it
   into **Bot token** and press **Save and connect**.
3. On **Bot**, under **Privileged Gateway Intents**, turn on **Message Content
   Intent**. Then press **Reconnect** in Martlet, if it asks.
4. On **Installation**, allow **Guild Install** and **User Install**.
5. In Martlet, use **Add to a server** for each server. Use **Add to home
   server** for a small server of your own, where she makes private call
   channels. Use **Add to my account** to use `/martlet` in any DM or group DM.
6. Under **People on Discord**, DM the bot and choose **That's me**, or paste
   your user ID. Choose the **Home server** there too.

Martlet keeps the bot token in Windows Credential Manager, never in settings
files or logs. **Save and connect** first checks that the text looks like a bot
token. If it doesn't, go to the **Bot** page in the Developer Portal, press
**Reset Token** and copy the whole token. **Forget the bot** disconnects and
removes the token, but keeps your chat modes and people. If the token may have
leaked, reset it in the Developer Portal too.

Turn off **Connect to Discord** to stop Martlet from connecting. Your bot token,
chat modes and people are kept.

### Who you are

Martlet must know which Discord user is you:

1. DM Martlet's bot, or write where the bot can see it.
2. Under **People on Discord**, pick yourself and choose **That's me**.

Or paste your user ID:

1. In Discord, open **Settings › Advanced** and turn on **Developer Mode**.
2. Right-click your name and choose **Copy User ID**.
3. Paste the ID in Martlet. It is a long number.

## How often she joins in

**Where Martlet chats** has a mode for **Server channels**, **Direct messages**
and **Voice channels**:

- **Off**: never.
- **Only when mentioned**: when someone mentions her, replies to her, says her
  name or uses `/martlet`.
- **Sometimes**: she joins in when she thinks she should. She waits a while
  after her last message and speaks up on her own only a few times an hour in
  one place.
- **Always**: she answers everything.

At first, server channels are **Off**, direct messages are **Always** and voice
channels are **Sometimes**. Choose a mode for server channels so she chats there.
Under **Channels with their own mode**, choose a channel and a mode and press
**Add rule**. In a server, `/chatmode` changes the mode of the channel you are
in. Only you or someone with the Manage Channels permission can use it.

Only you and the people Martlet knows may DM her. Turn on **Anyone may DM
Martlet** to change that.

## Commands

| Command | What it does |
| --- | --- |
| `/martlet message:<text>` | Ask Martlet something: in a server, in her DMs, or (with **Add to my account**) in any DM or group DM |
| `/chatmode` | Change this channel's mode (servers only) |
| `/join`, `/leave` | Join or leave your voice channel (servers only) |
| `/call person:<name>` | Call one of her friends (you only) |
| `/friend ask`, `/friend remove` | Ask to be her friend, or leave her friends list |
| `/selfie` | Post a picture of the character as she looks now |

## Voice calls

- `/join` brings her into your voice channel. She also joins when someone drags
  her in. She leaves after 2 minutes alone.
- She hears each person by name. She answers when someone says her name, or
  when you are alone with her. In **Sometimes**, she adds a comment now and then.
- When someone talks over her, she stops.
- She speaks with a voice engine on one of your computers. A cloud voice can't
  speak in Discord calls. See [Voices](Voices).
- Other people's speech becomes text on your own computers (Parakeet), never in
  the cloud.

## Friends and calls

Anyone can ask to be her friend with `/friend ask`. You choose **Approve** or
**Decline** on the **Friends and calls** card, or **Add someone by ID**.

To call a friend, press **Call**, use `/call`, or tell Martlet: "Call Ana." She
makes a private voice channel in her home server, DMs your friend a link and
joins. The channel goes away after the call. Turn off **Martlet may call them**
for someone she should never call.

Her Discord status follows what she does, such as *On a call with Ana* or
*Watching along*.

## Martlet in your Discord calls

This brings Martlet into calls on your own Discord account: DM calls, group
calls and server calls. Martlet never controls Discord: no clicks, no typing
and no use of your account. It works while always listening runs.

1. Turn on **Martlet joins my Discord calls**.
2. Under **Hear**, keep **The Discord app only (recommended)**.
3. To name the speakers, turn on **See who is talking in the Discord window
   (on this PC only)**, and type your Discord display name.
4. To let her speak into the call, install a virtual audio cable yourself, such
   as VB-Audio Virtual Cable. Choose **CABLE Input** for **Martlet's voice in the
   call**. In Discord, choose **CABLE Output** as your input device. Your own
   microphone must reach the cable too, for example through Voicemeeter.
5. Press **Check this PC**.

What she hears from the call goes to Thinking as the call's words. It never
counts as you, and it never goes into memory or voice recognition. She answers
when someone says her name, and otherwise only now and then.

### Her voice in calls

Discord hears only your input device, so Martlet speaks through a virtual audio
cable:

1. Install a virtual audio cable yourself, for example VB-Audio Virtual Cable.
2. In Martlet, choose its **CABLE Input** for **Martlet's voice in the call**.
3. In Discord, open **User Settings › Voice & Video** and choose **CABLE Output**
   as your input device.
4. Make your own voice reach the cable too. Speak through Martlet's microphone
   listening, or mix your microphone into the cable with Voicemeeter or the
   Windows **Listen to this device** setting.

### Her camera

She can be your webcam in the call:

1. Press **Open camera view**. The character opens in its own window,
   *Martlet camera*.
2. Choose a background: **Green**, **Blue**, **Magenta**, **Black** or a picture.
   For a picture, use **Choose a picture file...** or **A picture from
   Creations...**, or **Draw it** to have her draw one ([Picture
   making](Pictures-and-Creations)).
3. Frame her: drag her, zoom with the mouse wheel, or use **Bigger**,
   **Smaller**, **Left**, **Right**, **Up**, **Down** and **Reset framing**.
   In the camera window, the arrow keys nudge her, **Home** resets the
   framing and Shift+drag moves the window. OBS captures her as framed.
4. In OBS (you install it), add a Window Capture of *Martlet camera*. For a
   color background, add a Chroma Key filter.
5. In OBS, press **Start Virtual Camera**. In Discord, choose **OBS Virtual
   Camera** as your camera.

Ask her for a new background during the call, and she changes it herself.
When a paid picture provider draws the background, Martlet asks first, because
it may cost money. Martlet installs no camera driver.

## History and privacy

- Each answered message is kept in **Conversations**, one conversation for each
  channel or DM. Delete or edit it there, and Martlet deletes or edits it in
  Discord too while the bot is online. This works for her own messages, and for
  other people's messages only in a server where she can manage messages.
- Discord replies never use tools, Home Assistant or your earlier
  conversations. Remembered facts go only into your own DMs with her.
- Your conversation on the PC comes first. Discord waits, and never slows your
  replies.
- She never does anything sexual in a Discord call.

More detail: [Discord](https://github.com/throndir2/Martlet/blob/main/docs/DISCORD.md), [Messaging](https://github.com/throndir2/Martlet/blob/main/docs/MESSAGING.md), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md).
