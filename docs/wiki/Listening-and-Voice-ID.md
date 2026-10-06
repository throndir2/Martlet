# Listening and Voice ID

Martlet can hear you through push-to-talk or always listening, and can recognize who is speaking.

## Listening

Open **Companion › Listening** for microphone route, push-to-talk/always listening, OpenAI transcription, Parakeet on this PC or a paired host route.

Listening starts only when you press **Start listening** or use push-to-talk. Launching Martlet does not start capture.

## Parakeet on this PC

**Companion › Listening › This PC** offers **Parakeet in Martlet**. It runs inside Martlet with the bundled sherpa-onnx runtime. Models download only when you choose them.

| Model | Purpose |
| --- | --- |
| Parakeet TDT 110M | Fastest English default. |
| Parakeet TDT 0.6B v2 | More accurate English, better in noise. |
| Parakeet TDT 0.6B v3 | 25 languages; default for non-English display languages. |

## People

Open **Companion › People**. Voice recognition is on by default and bundled. You can name voices and give each as many other names as they go by (up to 40), hear the last few clips of a voice you haven't named yet, mark **This is me**, merge voices, inspect memories for that person, forget one voice or forget all voices.

Voice recognition is convenience, not authentication. Similar voices, recordings, illness, distance and microphone changes can fool it.

## Voice ID

Voice ID is separate from People. Voice ID filters who Martlet answers before upload; People recognizes voices after speech-to-text and helps Memory attribute facts.

## Sharing

With **Devices › Settings for all devices › Keep Martlet the same on all my computers** on, recognized voices and Voice ID settings sync through your paired hosts.

## Privacy

Audio is not kept after recognition. `voices.json` stores voiceprints and names, not raw audio. Voices are learned only from microphone audio, never from what the PC plays.

More detail: [Voices and People](https://github.com/throndir2/Martlet/blob/main/docs/VOICES.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Memory](https://github.com/throndir2/Martlet/blob/main/docs/MEMORY.md).
