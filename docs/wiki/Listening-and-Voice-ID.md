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

### When another computer or OpenAI can't hear you

If Listening uses another of your computers or OpenAI and it can't hear you (the computer is off, its service stopped, the key was removed), a Parakeet model downloaded on this PC hears you instead, on the processor. Nothing is sent anywhere. For the next minute Martlet goes straight to Parakeet, then tries your choice again. The **Now** card on Companion › Listening says which model stands in, or offers to download one without changing Listening.

## Audio model

Speech-to-text writes down what you say. Martlet can also hear *how* you say it, and what this PC plays. By default, the Thinking model hears the recordings itself, if it can (Gemma 4 E2B does). To pair a text-only Thinking model with one that hears, choose an **Audio model** on **Companion › Listening**: Ollama on this PC, a cloud provider or server, or the same model as your image model. It describes each recording in words, and Thinking still writes every reply. A reply never waits for the audio model.

The card says what hears the recordings now, what the model is known to do and what is sent where. **Test hearing** sends the model one word said by a Windows voice (never your voice) and checks that it hears it. More detail: [Image and audio models](https://github.com/throndir2/Martlet/blob/main/docs/SENSE_MODELS.md).

## People

Open **Companion › People**. Voice recognition is on by default and bundled. You can name voices and give each as many other names as they go by (up to 40), hear the last few clips of a voice you haven't named yet, mark **This is me**, merge voices, inspect memories for that person, forget one voice or forget all voices.

Voice recognition is convenience, not authentication. Similar voices, recordings, illness, distance and microphone changes can fool it.

## Voice ID

Voice ID is separate from People. Voice ID filters who Martlet answers before upload; People recognizes voices after speech-to-text and helps Memory attribute facts.

## Sharing

With **Devices › Settings for all devices › Keep Martlet the same on all my computers** on, Voice ID settings and whether Martlet recognizes voices sync through your paired hosts.

Recognized voices and their names are always shared with all your computers, so Martlet learns everyone's voice. They sync through your own paired hosts even while that switch is off. Hosts a friend shares with you never get them. Companion › People › **Your computers** shows when they last synced.

## Privacy

Audio is not kept after recognition. `voices.json` stores voiceprints and names, not raw audio. Voices are learned only from microphone audio, never from what the PC plays.

More detail: [Voices and People](https://github.com/throndir2/Martlet/blob/main/docs/VOICES.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Memory](https://github.com/throndir2/Martlet/blob/main/docs/MEMORY.md).
