# Voices

Pick a ready-made voice or clone one from a few seconds of audio. Martlet's
voices can laugh, sigh, gasp and whisper. Everything about her voice is on
**Companion › Voice**.

![Companion Voice](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-voice.png)

## Where she speaks

**Where it runs** is a list of the places that speak, in order. The first one
that is on speaks. The next ones help when it is busy or doesn't answer. Use
**Add to the list** to add **This PC**, one of your computers, a graphics card
or **A cloud provider**. **Show its setup below** opens a place's settings.

- **This PC** or one of your computers: choose an engine under **Voice
  engine**. Its button sets the engine up there and switches to it. A computer
  runs one voice engine at a time, so the old one stops. On this PC, engines
  run in Docker Desktop. Martlet installs it on the first setup.
- **A cloud provider**: **OpenAI** voices, or **ElevenLabs** with your cloned
  voice. No graphics card is needed, but the reply text leaves this PC and
  requests cost money.

## Voice engines

Martlet starts with **Chatterbox Turbo**. Home's **Recommended setup** chooses
Chatterbox Turbo when it fits on your graphics card, and Chatterbox Nano when it
doesn't.

| Engine | What it is good at | Needs |
| --- | --- | --- |
| **Chatterbox Turbo** | Natural and quick. Laughs, sighs and gasps. | NVIDIA graphics card, 6 GB+ |
| **Chatterbox Original** | Calm or expressive, sentence by sentence. No laughs. | NVIDIA, 6 GB+ |
| **Chatterbox Nano** | Small. Laughs and sighs. | NVIDIA, 4 GB+, or the processor |
| **F5-TTS** | The closest likeness to the recording. English and Chinese. | NVIDIA, 6 GB+ |
| **XTTS-v2** | Starts speaking soonest. 17 languages. | NVIDIA, 4 GB+ |
| **GPT-SoVITS** | Anime-style voices from a 3-10 second recording. English and Japanese. | NVIDIA, 4 GB+ |
| **Dia** | Lifelike, conversational. Many sounds, such as laughs, coughs and hums. English. | NVIDIA, 8 GB+ |
| **ElevenLabs** (cloud) | Your cloned voice with tones: whispering, happy, sad or angry. | Your ElevenLabs key. Costs money. |
| **OpenAI** (cloud) | Ready-made voices. No cloning. | Your OpenAI key. Costs money. |

Click **Compare them** under the engine list to see the engines side by side.
It shows what each one can copy, whether it laughs, its languages, its memory
use and its license. The F5-TTS and XTTS-v2 models are for non-commercial use
only.

### Sounds and tones

The Thinking model adds sounds and tones to a reply when the moment calls for
it. Martlet offers only the ones your engine can make.

- Chatterbox Turbo and Nano laugh, chuckle, sigh and gasp. They whisper only
  now and then, the way the model itself does.
- Dia makes the most sounds, such as laughs, sighs, coughs and hums.
- ElevenLabs can whisper and sound happy, sad or angry, and it laughs and
  sighs.
- Chatterbox Original says each sentence calmly or expressively. Set how calm
  and how expressive in **Companion › Voice › Chatterbox Original style**.
  **Use Resemble's suggestions** goes back to the defaults.

Your character laughs, sighs and moves along with these sounds. See
[The Character](The-Character).

A touch zone can also make a voice sound (**Add a reaction** on the zone's
page). Martlet's own voice makes it when you touch the zone, never while
Martlet speaks or you talk. A paid voice, such as ElevenLabs, makes a sound
only when you click **▶** on it. When you change to a voice that says words
only, the sounds stay in the zone's list for a voice that makes them.

## Your voices

**Voices** is your voice list. It is the same on all your computers.

- Six starter voices are ready: **Jenny (Dioco)**, **Annie (cute, chatty)**,
  **Bee (cute, bubbly)**, **LJ (female narrator)**, **SLT (US female)** and
  **BDL (US male)**. Martlet starts with Jenny. You can remove them like any
  other voice.
- **Play** a voice to hear it. **Use** makes Martlet speak with it on all your
  computers. **Remove** deletes it.
- The engines copy a voice from its recordings at once. No training is needed.

### Clone a voice

1. Click **Add a voice...**.
2. Choose a clear recording. MP3, M4A, WAV, FLAC, OGG, Opus, the sound of a
   video and most other audio files work.
3. Check the words. Martlet fills them in with Parakeet on this PC or with the
   speech-to-text on one of your computers. Fix anything it misheard.
4. Optional: click **Add another recording** for more recordings of the same
   voice, up to 30 seconds in all.
5. Type a name, choose **Whose voice is it?** and tick the permission box.
6. Click **Add voice**, then **Use**.

Some engines need recordings of a certain length. For example, Chatterbox needs
more than 5 seconds and GPT-SoVITS 3-10 seconds. The list says when a voice has
the wrong length for your engine.

### Record your own voice

Click **Record your own voice...** and read four short lines out loud in your
normal voice. Each line stops recording when you pause. You can play a line
back or record it again. Together, the lines hold nearly every sound of spoken
English.

### ElevenLabs

1. Under **Where it runs**, click **A cloud provider** and choose
   **ElevenLabs**.
2. Choose the model and one of your saved voices.
3. Paste your ElevenLabs API key.
4. Read the box and tick it.
5. Click **Clone and use ElevenLabs**.

Martlet uploads the recording only then. ElevenLabs charges your account for
each reply. Martlet follows ElevenLabs' documentation, but nobody has tried it
with a live ElevenLabs account yet.

## Volume and muting

**Companion › Voice › In conversations** has **Speak Martlet's replies aloud**
and **Voice volume**. Voice volume sets how loud Martlet speaks and sings on
this PC. A reply or song that plays now changes at once. It doesn't change
Windows' volume or other apps. **Speakers** chooses where her voice plays.

When **Speak Martlet's replies aloud** is off, replies show as text in the talk
window and in the character's speech bubble. **Mute voice** and **Unmute
voice** on the character's right-click menu change the same setting.

## When the voice can't speak

- When no computer has room for your engine, Martlet speaks with Chatterbox
  Nano: on a graphics card with room, or else on the processor. Your engine
  comes back when a computer has room again.
- When no computer can run a voice, Home says so and how to fix it.
- When the voice fails or is muted, the text still shows in the talk window and
  the speech bubble.
- Discord voice calls speak only with a voice engine on one of your computers.

## Consent

Clone only your own voice, or a voice whose speaker gave you permission. The
permission box is your own statement, not legal clearance. Do not use a voice
to deceive people. Chatterbox voices carry an invisible watermark.

More detail: [Voice engines](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_STUDIO.md), [Chatterbox](https://github.com/throndir2/Martlet/blob/main/docs/CHATTERBOX_VOICE.md), [F5-TTS](https://github.com/throndir2/Martlet/blob/main/docs/F5_VOICE.md), [XTTS-v2](https://github.com/throndir2/Martlet/blob/main/docs/XTTS_VOICE.md), [GPT-SoVITS](https://github.com/throndir2/Martlet/blob/main/docs/GPT_SOVITS_VOICE.md), [Dia](https://github.com/throndir2/Martlet/blob/main/docs/DIA_VOICE.md), [ElevenLabs](https://github.com/throndir2/Martlet/blob/main/docs/ELEVENLABS_VOICE.md), [Resource footprints](https://github.com/throndir2/Martlet/blob/main/docs/RESOURCE_FOOTPRINTS.md).
