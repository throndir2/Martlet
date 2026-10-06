# Discord

Martlet can run a Discord bot from **Companion › Discord**.

## Limits

Martlet uses a bot account, never a self-bot user account. Bots can chat in DMs and allowed server channels and join server voice. Bots cannot have friends, join DM/group-DM calls, or send live camera video.

## Setup

1. Open the Discord Developer Portal from Martlet.
2. Create an application and bot.
3. Paste the bot token into **Bot token** and **Save and connect**.
4. Enable **Message Content Intent**.
5. Allow Guild Install and User Install.
6. Use **Add to a server**, **Add to home server** and **Add to my account**.
7. Identify your own account under **People on Discord**.

The bot token is in Windows Credential Manager, not `discord.json` or logs.

## Chat and calls

**Where Martlet chats** controls DMs, server defaults and channel rules: Off, Only when mentioned, Sometimes or Always. `/martlet message:<text>` asks Martlet. `/chatmode` changes channel mode where permitted.

For private calls, Martlet creates a private voice channel in its home server and DMs a link. **Martlet in your own Discord calls** is separate: it hears Discord app audio and can speak through a device you choose, without controlling your account.

More detail: [Discord](https://github.com/throndir2/Martlet/blob/main/docs/DISCORD.md), [Messaging](https://github.com/throndir2/Martlet/blob/main/docs/MESSAGING.md), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md).
