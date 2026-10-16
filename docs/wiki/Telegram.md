# Telegram and WhatsApp

Write to Martlet from your phone. She answers Telegram and WhatsApp messages in
the same conversation as the talk window, with the same Thinking model,
personality and memory. Her replies come back as text.

Open **Companion › Messaging**. Set an app up below, then turn it **On** under
**Answer messages**.

## Telegram

1. In Telegram, open BotFather (**Open BotFather**), send `/newbot` and choose a
   name for your Martlet bot.
2. Paste the token that BotFather gives you into **Bot token**, and press
   **Connect**.
3. Press **Pair a chat**. Martlet shows a six-digit code for 10 minutes.
4. Send the code to your bot, or press **Open in Telegram**.

Martlet asks Telegram for new messages itself, so you need no public address,
open port or webhook. The token stays in Windows Credential Manager on this PC
and goes only to Telegram.

## WhatsApp

Martlet uses Meta's official **WhatsApp Cloud API**. Nothing runs against your
personal WhatsApp account. You write to a WhatsApp business number you own;
Meta's free test number is enough.

1. Press **Create a Meta app**. Choose the use case *Connect with customers
   through WhatsApp*.
2. In the app, open **WhatsApp › API Setup**. Under *To*, add your own phone
   number and confirm it with the code WhatsApp sends. The test number can talk
   to up to five confirmed numbers.
3. Make a permanent access token in **System users**. Add a system user (Admin),
   give it your app and your WhatsApp account, and generate a token with
   `whatsapp_business_messaging` and `whatsapp_business_management`.
4. In the app, open **App settings › Basic** and copy the **App secret**.
5. In Martlet, paste the **Access token** and the **App secret**, and press
   **Connect**. Leave the IDs empty; Martlet finds them.
6. Press **Pair a chat**. Send the code to the number, or press **Open in
   WhatsApp**.

WhatsApp delivers messages to a public web address. Martlet makes one for you
with a free Cloudflare quick tunnel, so you change nothing on your router. It
needs Cloudflare's `cloudflared`: Martlet uses one you have, or **Get
cloudflared** downloads Cloudflare's official copy after you agree. If you have
your own public HTTPS address, put it in **Public address (optional)**.

Martlet accepts only messages signed with your app secret. It keeps the token
and the secret in Windows Credential Manager and sends them only to Meta. Use a
Meta app for Martlet only.

## How it works

- Martlet answers only the chats you paired. She ignores group chats. A stranger
  is told once how to pair, and their message never reaches the Thinking model.
- She answers while Martlet runs on this PC, also while Windows is locked. Then
  she uses text only: never the microphone, the screen or her voice.
- **Also say replies aloud on this PC** speaks her replies too, but never while
  Windows is locked. It is off by default.
- Use each bot or number on one PC. A host PC never answers.
- She reads text only for now. Photos, voice notes and stickers get a short note.
- A tool or smart-home action that asks first waits for you at the PC.
- **Remove** unpairs one chat. **Disconnect** stops the app, deletes its keys
  from this PC and forgets the paired chats.

## History

Your messages show in the talk window as "*Name* (Telegram)" or "*Name*
(WhatsApp)". They are kept in **Conversations** with the rest of your history.
See [Memory](Memory).

- **Telegram:** delete a message on the Conversations page, and Martlet deletes
  it in Telegram too (within Telegram's 48 hours). Edit her reply there, and it
  changes in Telegram. A bot can't edit your own messages.
- **WhatsApp:** the Cloud API can't delete or edit sent messages, so a change
  stays in Martlet only.

More detail: [Messaging](https://github.com/throndir2/Martlet/blob/main/docs/MESSAGING.md), [Conversation history](https://github.com/throndir2/Martlet/blob/main/docs/MEMORY.md#conversation-history), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md).
