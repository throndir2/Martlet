# ElevenLabs voice: your cloned voice with tones

Martlet can speak replies with **ElevenLabs** (elevenlabs.io). ElevenLabs
copies one of your saved voices and speaks with it in real time. It also does
tones of voice, such as `[whispers]`, `[happy]` or `[sad]`, and sounds such as
`[laughs]`. Martlet's self-hosted Chatterbox Turbo clones voices too, but it
does not perform tones (see [Chatterbox voice](CHATTERBOX_VOICE.md#tags)).
ElevenLabs is the only cloud service we checked whose own documentation covers
a cloned voice and tone control in real time.

> **Status.** Martlet's ElevenLabs support follows ElevenLabs' documentation
> (read 2026-10-07). It was **never run against the live service**, because
> there is no ElevenLabs account to test with. Every check ran against a local
> fixture that follows the documented protocol (see
> [How it was checked](#how-it-was-checked)).

## What you need

- An ElevenLabs account with a plan that allows Instant Voice Cloning and the
  API. ElevenLabs charges per character ([pricing](https://elevenlabs.io/pricing/api)).
- An ElevenLabs API key (elevenlabs.io › Developers › API keys). The key must
  allow Text to Speech and Voices.
- One of your saved voices (Companion › Voice › This PC › Voices). The
  recording must be your own voice, a voice whose speaker gave you permission,
  or a published sample that anyone may use.

## Set it up

1. Open Companion › Voice.
2. Under **Where it runs**, choose **A cloud provider**.
3. In the **ElevenLabs: your cloned voice with tones** card, choose the model.
   Keep **Eleven v4 Turbo** unless ElevenLabs refuses it.
4. Choose the voice to clone.
5. Paste your ElevenLabs API key. Martlet keeps it in Windows Credential
   Manager.
6. Read the box and tick it. It allows Martlet to upload the recording once and
   to send reply text to ElevenLabs.
7. Press **Clone and use ElevenLabs**.

Martlet uploads the recording, gets the new voice's ID and saves the Voice
route. The cloned voice stays in your ElevenLabs account (My Voices) until you
delete it there. If ElevenLabs asks you to verify the voice, the card says so.
Verify it on elevenlabs.io before it speaks.

Press the button again with the same voice and the saved key: Martlet uses the
voice it cloned before and uploads nothing. A new key, or another voice, makes
a new clone. When Voice moves to another provider, the ElevenLabs key goes to
**Keys from before**. When Voice comes back to ElevenLabs, Martlet uses that
key again.

## What is sent, and when

| When | What goes to ElevenLabs |
| --- | --- |
| You press **Clone and use ElevenLabs** (box ticked) | The chosen recording (WAV), its name `Martlet - <voice name>` and your key |
| Martlet speaks a reply | Each spoken piece of the reply text, with its tags, the model, the cloned voice's ID and your key |

Saving settings, opening the page or starting Martlet sends nothing. Quick
sounds (Companion › Voice) use ElevenLabs only when you press **Make quick
sounds now**, because each clip costs money. Discord voice calls do not use
ElevenLabs; they speak only with a voice engine on a host.

## Models

| Model | ID | Notes from ElevenLabs' documentation |
| --- | --- | --- |
| Eleven v4 Turbo (default) | `eleven_v4_turbo` | Real time, median model latency about 100 ms without the network. Audio tags. One voice per connection. Offered through the Text to Dialogue WebSocket only. |
| Eleven v3 Conversational | `eleven_v3_conversational` | Real time, about 280 ms. Audio tags. One voice per connection. The WebSocket's API reference names it as the default model. |

ElevenLabs' documentation does not agree with itself here. The Text to
Dialogue guide and the models page say Eleven v4 Turbo works on the WebSocket.
The WebSocket's API reference says the model ID must start with `eleven_v3`.
If ElevenLabs refuses the model, the voice stops for that reply, the text still
arrives, and the desktop log says: *ElevenLabs refused eleven_v4_turbo here;
choose Eleven v3 Conversational in Companion › Voice.* Martlet never changes the
model by itself.

## Tags

When ElevenLabs speaks, the Thinking model gets ElevenLabs' own tags in
Companion › Prompts › *Voice sounds and tones* (see
[Voice tags](CONVERSATION.md#voice-tags)). The tags are the ones ElevenLabs
documents for Eleven v3 and v4, limited to those that suit a conversation:

- **Sounds:** `[laughs]`, `[chuckles]`, `[sighs]`, `[clears throat]`,
  `[inhales deeply]`, `[exhales]`.
- **Tones:** `[whispers]`, `[shouts]`, `[happy]`, `[excited]`, `[sad]`,
  `[crying]`, `[angry]`, `[sarcastic]`, `[annoyed]`, `[surprised]`,
  `[curious]`.

Sound effects (`[gunshot]`, `[applause]`), accents and `[sings]` are left out.
Each tag reaches ElevenLabs as it is written. Other spellings count as the tag:
`[whisper]` or `*whispers*` is sent as `[whispers]`, and Chatterbox's `[laugh]`
as `[laughs]`. The chat, the saved conversation and the captions never show a
tag.

Each tag uses a cue that the desktop character already has, so the character
acts with the voice: `[whispers]` is the whispering cue, `[shouts]` dramatic,
`[excited]` happy, `[sad]` sigh, `[annoyed]` sarcastic and `[curious]`
surprised. Tags without a fitting cue (`[snorts]`, `[thoughtful]`,
`[mischievously]`) are left out.

ElevenLabs documents that the tags change the delivery. Martlet has not
measured how well they work with a cloned voice. ElevenLabs says a tag works
best when the recording already has that delivery.

## How Martlet talks to ElevenLabs

**Cloning.** `POST https://api.elevenlabs.io/v1/voices/add`, a multipart form
with `name`, `files` (one WAV file), `remove_background_noise=false` and
`description`, and the key in the `xi-api-key` header. The answer gives
`voice_id` and `requires_verification`.

**Speaking.** Each spoken piece of a reply uses one connection to
`wss://api.elevenlabs.io/v1/text-to-dialogue/stream-input?model_id=<model>&output_format=pcm_24000`:

1. Martlet connects with the key in the `xi-api-key` header (never in a
   message).
2. The first message registers the one cloned voice: `{"voices":["<id>"]}`.
3. The next message sends the piece: `{"inputs":[{"text":"...","voice_id":"<id>"}]}`.
4. The last message is `{"close_socket":true}`. ElevenLabs then says the
   buffered text at once, so a short piece does not wait for the server's
   40-character buffer.
5. ElevenLabs sends base64 `audio` messages and then `is_final`. The audio is
   raw 24 kHz mono 16-bit PCM, which Martlet plays as it arrives, with nothing
   to decode.

An error message (`message`, `error`, `code`, `param`) or an early close stops
that piece's voice. Martlet writes ElevenLabs' reason to the desktop log, never
the key or the text. A key that is wrong, out of credits or rate limited, and a
refused model or voice, each get their own failure code.

One connection per piece keeps each piece under its own one-use permission and
key, as Martlet's other voices do. The cost is a new connection (TCP, TLS and
the WebSocket upgrade) per piece. Pieces after the first are made while the
one before plays, so mostly the first piece's connection adds to the time to
the first audible word.

## Latency

ElevenLabs' latency was **not measured**: there is no account. When you use
ElevenLabs, the desktop log's *Reply latency* line shows *voice synthesis*
(from the voice request to its first audio) for each reply, as it does for
every voice. `ElevenLabsDialogueClient.LastSegment` keeps the last piece's
connect time, time to first audio and total time.

Choosing ElevenLabs does not change the latency of any other voice: the other
routes run exactly as before.

## How it was checked

All checks ran on this PC against `ElevenLabsFixture`. It is a local stand-in
on 127.0.0.1 that follows ElevenLabs' documented protocol. It is a FIXTURE,
NOT ElevenLabs and NOT AI: its "voice" is a quiet tone as long as the words,
cut into base64 chunks at odd byte boundaries.

- Unit tests (`ElevenLabsTests` in `Martlet.Providers.Tests`,
  `ElevenLabsSettingsTests` in `Martlet.Core.Tests`, `ElevenLabsTagTests` in
  `Martlet.Conversation.Tests`).
- `elevenlabs_check` in [Martlet MCP](MCP.md#elevenlabs-voice): cloning, one
  piece, and a whole spoken reply through the production conversation runtime,
  plus a refused model and a wrong key.
- Companion › Voice through the desktop's MCP automation (`ElevenLabsStatus`,
  `ElevenLabsModel`, `ElevenLabsKeyStatus`).

**NOT RUN:** any request to the live ElevenLabs service (cloning, speaking,
tags, latency, voice verification, billing). The reason: there is no
ElevenLabs account or key.

## Limits

- Only `api.elevenlabs.io` is used. ElevenLabs' regional servers (US, EU,
  India, Singapore) are not offered.
- Martlet does not delete cloned voices from your ElevenLabs account. Delete
  them on elevenlabs.io.
- The voice's stability and other voice settings stay at ElevenLabs' defaults.
- The ElevenLabs choice stays on the computer where you made it. It is not
  shared with your other Martlet computers.
