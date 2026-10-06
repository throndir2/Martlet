# Talking to Martlet

**Home › Start talking** opens the talk window: conversation history, what you said and a message box. It stays beside Home, Companion and Settings.

## Ways to talk

| Method | How |
| --- | --- |
| Type | Type and press Enter. Shift+Enter inserts a new line. |
| Push-to-talk | Hold the talk button or Space, speak, release to send. |
| Always listening | Configure Listening, then press **Start listening**. |
| Messaging | Pair Telegram/WhatsApp in **Companion › Messaging**. |
| Discord | Configure **Companion › Discord**. |

## Buttons

- **Start talking** opens the talk window.
- **Show conversation** brings it back.
- **Start listening** / **Stop listening** controls hands-free listening.
- **Start watching** / **Stop watching** controls Vision separately.
- **Stop** or Esc stops a reply, discards a recording and stops watching; it does not turn off always listening.

## Replies

Martlet streams text and speaks sentence by sentence when **Companion › Voice › Speak Martlet's replies aloud** is on. If voice fails or is muted, text still appears and the character bubble can still show the words.

## Context

Martlet sends recent conversation that fits **Companion › Replies › Context size**. Memory and lore can add notes. Older exchanges are omitted when needed.

## Thinking steps

**Companion › Replies › Thinking steps** is Off by default so replies start sooner. A model that refuses the control is retried with its own default.

## Important boundaries

Opening setup or status does not test keys, record audio, enumerate devices or make provider requests. Listening, watching and tool use each require their own controls or confirmations.

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Voice latency](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_LATENCY.md), [Messaging](https://github.com/throndir2/Martlet/blob/main/docs/MESSAGING.md).
