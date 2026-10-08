# Voices

Martlet can speak through Windows voices, OpenAI voices, ElevenLabs or self-hosted voice engines. Custom voices live in **Companion › Voice**.

![Companion Voice](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-voice.png)

## Voice library

Open **Companion › Voice › Voices** to add recordings, transcripts and rights confirmation; play voices; choose **Use**; or remove voices. Martlet keeps its own copy and can share voices with paired computers.

## Engines

| Engine | Notes |
| --- | --- |
| **Chatterbox Turbo** | Default cloning engine; supports tone/sound tags like laughs and sighs. |
| **F5-TTS** | Voice cloning from a reference; official weights are non-commercial. |
| **XTTS-v2** | Streaming cloned voice; model license is non-commercial. |
| **GPT-SoVITS** | Good for anime-style voices from a 3-10 second reference. |
| **Dia** | English, nonverbal cues such as laughs/sighs/coughs; not streaming. |
| **Windows voices** | Local installed voices. |
| **OpenAI TTS** | Cloud generated voices; text goes to OpenAI and may cost money. |
| **ElevenLabs** | Cloud voice cloned from one of your saved voices, with tones such as whispers, happy or sad; uses your own ElevenLabs key and costs money. Set up under **Companion › Voice › A cloud provider**. |

Only one self-hosted voice engine runs on a host at a time because each uses GPU memory.

## Add a cloned voice

1. Open **Companion › Voice › Voices**.
2. Add a recording you have rights to use.
3. Provide or review the transcript.
4. Choose the engine under **Voice engine**.
5. Pick where it runs.
6. Choose **Use** when ready.

Do not clone someone without permission.

## When voice fails

Text still streams. The talk window notes that voice failed, stopped or was muted. The speech bubble can still show text.

More detail: [Voice Studio](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_STUDIO.md), [Chatterbox](https://github.com/throndir2/Martlet/blob/main/docs/CHATTERBOX_VOICE.md), [F5](https://github.com/throndir2/Martlet/blob/main/docs/F5_VOICE.md), [XTTS-v2](https://github.com/throndir2/Martlet/blob/main/docs/XTTS_VOICE.md), [GPT-SoVITS](https://github.com/throndir2/Martlet/blob/main/docs/GPT_SOVITS_VOICE.md), [Dia](https://github.com/throndir2/Martlet/blob/main/docs/DIA_VOICE.md), [ElevenLabs](https://github.com/throndir2/Martlet/blob/main/docs/ELEVENLABS_VOICE.md).
