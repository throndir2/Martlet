# Messaging apps

Martlet can be reached from a messaging app on your phone. Telegram is the
first; the design (`src\Martlet.Messaging`) keeps each app behind one transport
so others can follow.

## Telegram

What it does: you write to your own Telegram bot, and Martlet on your companion
PC answers in the **same conversation** as the talk window: the same Thinking
model, personality, memory, lorebooks and conversation history. Replies come
back as text.

Set it up in **Companion › Messaging**:

1. In Telegram, open [BotFather](https://t.me/BotFather), send `/newbot` and
   pick a name. BotFather gives you a token like `123456789:AAH...`.
2. Paste the token and press **Connect**. Martlet checks it with Telegram
   (`getMe`), keeps it in Windows Credential Manager on this PC
   (`Martlet/v3/messaging/telegram/<id>`, a new entry per connection) and turns on **Answer Telegram messages on
   this PC**.
3. Press **Pair a chat**. Martlet shows a six-digit code for 10 minutes; send it
   to the bot (or open the **Open in Telegram** link, which sends `/start
   <code>`). That chat is now paired.

How it works:

- Martlet uses the Bot API's long polling (`getUpdates`), so it needs no public
  address, open port or webhook. Messages travel through Telegram's servers.
  The token goes only to `api.telegram.org`.
- Only paired one-to-one chats are answered. Group chats are ignored; an
  unpaired chat is told once (per run) how to pair and never reaches the
  Thinking model. Five wrong codes end the pairing code.
- Each text message becomes a typed message in the conversation (shown in the
  talk window as "*Name* (Telegram)"); the conversation starts hidden when none
  is running. While Martlet answers, the chat shows "typing". Replies longer
  than 4096 characters are split at paragraph, line or word breaks. A message
  waits at most 3 minutes for its reply.
- Messages are answered **while Windows is locked** too, since that is when you
  are away: text in, text out, never the microphone, the screen or Martlet's
  voice. Locking still starts a fresh conversation, as it does for the talk
  window.
- **Also say replies aloud on this PC** (off by default) speaks them with the
  chosen voice, never while Windows is locked.
- Only one program can read a bot's messages, so the bot runs on this PC only;
  messaging.json is not synced. A host PC never answers.
- Martlet reads text only for now (photos, voice notes and stickers get a short
  note). A tool call or smart-home action that asks first waits for you at the
  PC and otherwise times out.

**Disconnect** stops the bot, deletes the token and forgets the paired chats.
**Remove** unpairs one chat.

## Files and diagnostics

- `messaging.json` (data directory): `Telegram.Enabled`, `BotName`,
  `BotUsername`, `CredentialId` (which Credential Manager entry), `Chats` (`Id`,
  `Name`) and `SpeakReplies`. Never the token.
- The desktop log records when Telegram connects, a chat pairs and a paired
  chat's message is answered (never message text).
- MCP: `messaging_status` reads `messaging.json` (counts chats, never their
  names or IDs); Companion › Messaging's status lines are readable through
  `ui_*` ([MCP](MCP.md)). `MARTLET_TELEGRAM_API` (a loopback `http://127.0.0.1:<port>/`
  address only) points the desktop at a local fake Bot API for MCP verification.

## Other apps

A new app implements `IMessagingTransport` (connect, receive, send, typing,
longest message) and reuses `MessagingBridge` for pairing, the allow-list,
typing, splitting and retries. Candidates: Discord (a bot over its Gateway
WebSocket, DMs only), Matrix (`/sync` long polling, no public address needed)
and Signal through `signal-cli`. WhatsApp's Business API needs a public webhook
and a business account, so it is not a good fit for a personal PC.
