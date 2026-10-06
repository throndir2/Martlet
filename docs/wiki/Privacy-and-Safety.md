# Privacy and Safety

Martlet is local-first and asks before actions that capture, disclose, spend or change things.

## Launch behavior

Launching Martlet does not start microphone, camera, provider requests, model downloads, tool calls or speaker playback. Update checks are on by default and can be turned off. Sync happens only with paired computers.

## Data destinations

- Microphone audio goes only to the selected speech-to-text route.
- Text/transcripts go to the selected Thinking route and optional fallback.
- Reply text goes to the selected voice route when spoken.
- Screenshots go to the selected Thinking model only while Vision is active or attached to your message.
- Smart-home commands go to the Home Assistant you connected.
- Tool arguments/results go to the tool and Thinking model for that reply.

Cloud routes may cost money and have provider retention terms.

## Storage

Settings contain key references, not API keys. Keys and tokens live in Windows Credential Manager. Memories, voices, characters and creations stay on your computers and paired hosts. Logs omit keys/conversation content but can contain local paths, host names and provider error text.

## Safety controls

Listening starts with **Start listening** or push-to-talk. Vision starts with **Start watching**. Tool calls ask unless auto-approved. Terminal is off by default and asks before every command. Sensitive smart-home actions are blocked or require **Yes, send it**.

## Identity and media

Voice recognition is not authentication. Do not clone voices, make songs or generate images of people without permission. Say AI-generated images/songs are AI-generated when sharing.

More detail: [README privacy and safety](https://github.com/throndir2/Martlet/blob/main/README.md#-private-by-design), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md), [Smart home](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md), [Troubleshooting](https://github.com/throndir2/Martlet/blob/main/docs/TROUBLESHOOTING.md).
