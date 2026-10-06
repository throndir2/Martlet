# Telegram

Martlet can answer Telegram, and the same Messaging design covers WhatsApp.

## Telegram

Open **Companion › Messaging**:

1. In Telegram, use BotFather `/newbot`.
2. Paste the token in Martlet and press **Connect**.
3. Press **Pair a chat**.
4. Send the six-digit code to the bot or use **Open in Telegram**.

Martlet uses long polling, so no public address or webhook is needed. Only paired one-to-one chats are answered; group chats are ignored. Messages become typed conversation messages and replies return as text. It can answer while Windows is locked, but never uses microphone, screen or voice then.

## WhatsApp

WhatsApp uses Meta's official Cloud API, not your personal account. Setup asks for a Meta app, access token, app secret and pairing. Martlet opens a webhook using a Cloudflare quick tunnel by default or your own public HTTPS address.

## History

Answered messages are recorded in conversation history. Telegram supports delete/edit mirroring where allowed; WhatsApp changes remain local because Cloud API cannot delete or edit sent messages.

More detail: [Messaging](https://github.com/throndir2/Martlet/blob/main/docs/MESSAGING.md), [Memory](https://github.com/throndir2/Martlet/blob/main/docs/MEMORY.md), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md).
