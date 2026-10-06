# Messaging apps

Martlet can be reached from a messaging app on your phone: Telegram and
WhatsApp. The design (`src\Martlet.Messaging`) keeps each app behind one
transport and shares pairing, the allow-list, typing, splitting and retries
(`MessagingBridge`), so others can follow.

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
- Each answered message is in Companion › Memory › Open conversation history
  with its Telegram message IDs (yours and Martlet's reply pieces). Deleting a
  message there deletes it in Telegram too (within Telegram's 48 hours), and
  editing Martlet's reply edits it there; your own messages can't be edited by a
  bot. See [Conversation history](MEMORY.md#conversation-history). WhatsApp's
  Cloud API can't delete or edit sent messages, so WhatsApp messages are
  deleted and edited on this PC only.

**Disconnect** stops the bot, deletes the token and forgets the paired chats.
**Remove** unpairs one chat.

## WhatsApp

What it does: the same as Telegram, from WhatsApp. You write to a WhatsApp
business number you own (Meta's free test number is enough), and Martlet on
your companion PC answers in the same conversation as the talk window.

Martlet uses Meta's official **WhatsApp Cloud API**, so nothing unofficial runs
against your personal WhatsApp account. The Cloud API delivers messages to a
public web address (a webhook); Martlet sets that up itself.

Set it up in **Companion › Messaging › WhatsApp** (the card has buttons for
each link):

1. [Create a Meta app](https://developers.facebook.com/apps/creation/) with
   the use case *Connect with customers through WhatsApp* and a business
   portfolio (Meta offers to make one).
2. In the app, open **WhatsApp › API Setup**. Meta gives you a free test
   number. Under *To*, add your own phone number and confirm it with the code
   WhatsApp sends you (the test number can only talk to up to five confirmed
   numbers).
3. Make a permanent access token: in
   [System users](https://business.facebook.com/latest/settings/system_users)
   add a system user (Admin), assign it your app and your WhatsApp account with
   full control, then **Generate token** with `whatsapp_business_messaging` and
   `whatsapp_business_management`. API Setup's temporary token also works, for
   24 hours.
4. In the app, open **App settings › Basic** and copy the **App secret**.
5. Paste both in Martlet and press **Connect**. Leave the phone number ID and
   WhatsApp Business account ID empty and Martlet finds them from the token
   (`debug_token`, then the account's first number); type them to pick
   another. You don't configure Webhooks in Meta.
6. Press **Pair a chat** and send the six-digit code to the number (or open
   **Open in WhatsApp**, a `wa.me` link with the code filled in).

What Martlet does on Connect and at every start:

- Checks the token with Meta and the app secret against the token's app, then
  keeps both in Windows Credential Manager
  (`Martlet/v3/messaging/whatsapp/<id>`). They go only to `graph.facebook.com`.
- Listens on `http://localhost:<port>` (no administrator rights, no open
  firewall port) at a random secret path, accepting only deliveries signed with
  the app secret (`X-Hub-Signature-256`) for its phone number.
- Opens a public address: by default a free
  [Cloudflare quick tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/)
  (`https://….trycloudflare.com`; no account, port or router change). It needs
  Cloudflare's `cloudflared`: Martlet uses one already installed (PATH,
  Program Files, winget) or, after you agree, downloads Cloudflare's official
  build from GitHub into `<data directory>\tools`. The address changes at each
  start, so Martlet waits until it resolves and points the webhook at it again.
- Sets the app's webhook (`POST /<app>/subscriptions`, field `messages`, with a
  fresh verify token, using the app access token) and subscribes the app to the
  WhatsApp Business account (`POST /<waba>/subscribed_apps`). Meta checks the
  webhook right away.
- Marks each message read with "typing" while Martlet answers, and replies as
  text (split at 4096 characters).

Already have a public HTTPS address (a named Cloudflare tunnel, a reverse
proxy)? Put it in **Public address**; it must forward to the
`http://localhost:<port>` Martlet shows, with `Host: localhost` (or
`127.0.0.1`). The app's webhook belongs to Martlet while it runs, so use a Meta
app for Martlet only.

Everything else matches Telegram: only paired one-to-one chats are answered,
strangers are told once how to pair, text only, answered while Windows is
locked, optional **Also say replies aloud on this PC**, **Disconnect** deletes
the secrets and forgets the chats, **Remove** unpairs one chat. Business
numbers may reply freely within 24 hours of your last message, which every
answer is.

## Files and diagnostics

- `messaging.json` (data directory): `Telegram.Enabled`, `BotName`,
  `BotUsername`, `CredentialId` (which Credential Manager entry), `Chats` (`Id`,
  `Name`) and `SpeakReplies`; `WhatsApp` with `Enabled`, `Name`, `Number`,
  `AppId`, `BusinessAccountId`, `PhoneNumberId`, `Port`, `PublicAddress`,
  `CredentialId`, `Chats` and `SpeakReplies`. Never a token or app secret.
- The desktop log records when Telegram or WhatsApp connects, a chat pairs and a paired
  chat's message is answered (never message text).
- MCP: `messaging_status` reads `messaging.json` (counts chats, never their
  names or IDs); Companion › Messaging's status lines are readable through
  `ui_*` ([MCP](MCP.md)). `MARTLET_TELEGRAM_API` and `MARTLET_WHATSAPP_API` (a
  loopback `http://127.0.0.1:<port>/` address only) point the desktop at a local
  fake Bot API or Graph API for MCP verification; with the latter, a loopback
  `http://` public address is accepted too.

## Other apps

A new app implements `IMessagingTransport` (connect, receive, send, typing,
longest message) and reuses `MessagingBridge` for pairing, the allow-list,
typing, splitting and retries. An app whose bot may find, delete and edit its
messages also implements `IMessagingMessageControl` (send returning the
message's ID, delete, edit), so the record of conversations keeps those IDs and
deletes or edits there through `MessagingPlatform`. Discord has its own, richer foundation (servers,
channels, people and chat modes) in [Discord](DISCORD.md). Other candidates:
Matrix (`/sync` long polling, no public address needed) and Signal through
`signal-cli`. A webhook-based app can reuse WhatsApp's pieces: a local
listener and `IPublicAddress` (Cloudflare quick tunnel or an own address).
