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

### Set up an engine

1. Choose **This PC** or one of your other computers under **Where it runs**.
   For another computer, pick the computer first, then an engine.
2. Choose an engine in the list to see what it needs and what it can do.
3. Click its button. Martlet sets the engine up on that computer and switches
   to it. The engine it replaces stops.

- No other computers paired yet? Click **Add a computer** first.
- When a computer isn't reachable, Martlet checks it again when you pick an
  engine.
- When no engine can run on a computer, Martlet can't speak there. Pick another
  of your computers, or use a cloud voice under **A cloud provider**.
- A computer can still run voice engines that it no longer speaks with. They
  hold graphics card memory that the engine that speaks needs. Click **Stop**
  to free that memory. The downloads are kept, so switching back is quick. Any
  other computer that still speaks with a stopped engine stops speaking until
  it chooses another engine.

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

## Your voices

**Voices** is your voice list. It is the same on all your computers.

- Six starter voices are ready: **Jenny (Dioco)**, **Annie (cute, chatty)**,
  **Bee (cute, bubbly)**, **LJ (female narrator)**, **SLT (US female)** and
  **BDL (US male)**. Martlet starts with Jenny. You can remove them like any
  other voice.
- **Play** a voice to hear it. **Use** makes Martlet speak with it on all your
  computers. **Remove** deletes it on this PC and on your other Martlet
  computers. Your own original recording file isn't touched.
- The engines copy a voice from its recordings at once. No training is needed.

### The voice list

- The voice list holds up to 1,000 voices. When it is full, remove a voice you
  no longer use before you add a new one.
- You can't remove the voice Martlet speaks with now. Switch to another voice
  first, then remove it.
- When the voice Martlet speaks with is no longer in the list (for example, you
  removed it on another computer), the list says so. Use another voice. Martlet
  then removes the old one from this PC.
- When a new version of Martlet no longer includes the voice it spoke with, it
  changes to another voice and tells you which one. Pick a different one under
  **Companion › Voice › Voices** if you like.
- When some voices didn't fit on a computer, remove voices you no longer use
  under **Companion › Voice › Voices**.

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

How it works:

- Martlet converts the recording for you into the sound file that the engines
  use.
- F5-TTS, GPT-SoVITS and Dia read the words along with the recording, so the
  words must match the recording exactly.
- Martlet fills in the words only when speech-to-text runs without the cloud.
  Download Parakeet in **Companion › Listening** to get it. Without it, type
  the words.
- A voice can have up to 10 recordings of the same voice. XTTS-v2 and
  GPT-SoVITS learn from each one. The other engines hear them joined, one after
  another with a short pause, 30 seconds at most in all. **Play joined** lets
  you hear them that way.
- Martlet keeps its own copy and shares it with your paired Martlet computers,
  so any of them can speak with this voice. You can move or delete the
  original file afterwards.

### When a voice can't be added

| The message says | What to do |
| --- | --- |
| The recording is too long to fill in its words, or no words were heard | Type the words yourself. |
| More words than a voice can keep | Type the words, or use a shorter recording. |
| Martlet is still filling in the words | Wait a moment, or type the words. |
| Martlet couldn't read this recording | Try an MP3, M4A, WAV, FLAC or OGG file. |
| The recording is no longer where you chose it | Choose the file again. |
| The recording changed while Martlet was reading it | Try again. |
| That recording can't be used | Choose a clear recording of 1 to 30 seconds. |
| Martlet is busy with your voices | Try again in a moment. |
| The voice list is full, or larger than its size limit | Remove voices you no longer use first. |
| Martlet's copy of this voice is damaged | Remove the voice and add the recording again. |
| Martlet couldn't save the voice on this PC | Check that the disk has free space, then try again. |

### Record your own voice

Click **Record your own voice...** and read four short lines out loud in your
normal voice. Each line stops recording when you pause. You can play a line
back or record it again. Together, the lines hold nearly every sound of spoken
English.

1. Tick the box that says this is your own voice. Martlet then may record it on
   this PC and keep it as a voice, shared with your paired Martlet computers.
   Nothing is recorded before you tick the box.
2. Click **Record** beside a line and read it out loud at your normal pace.
3. Pause at the end of the line. Recording stops by itself.
4. Record each line, then click **Play all** to hear them.
5. Type a name and click **Add voice**.

Tips:

- Use a quiet room and the microphone you talk to Martlet with.
- Together the lines must last less than 30 seconds, with the pauses between
  them.
- When a new recording of a line isn't kept, your earlier recording of it stays.

| The message says | What to do |
| --- | --- |
| Only a few seconds of speech was heard | Move closer to the microphone or speak up, then record the line again. |
| The line went on without a pause | It may be cut off. Record it again and pause when you finish. |
| Together the lines last too long | Record the longest line again a little faster. |
| Another Martlet task is using the microphone | Let it finish, then try again. |
| Recording failed | Try again. |
| The voice list is full | Remove a voice you no longer use first. |
| Martlet couldn't save the voice on this PC | Check that the disk has free space, then try again. |

### ElevenLabs

1. Under **Where it runs**, click **A cloud provider** and choose
   **ElevenLabs**.
2. Choose the model and one of your saved voices.
3. Paste your ElevenLabs API key. You find it on elevenlabs.io under
   **Developers › API keys**. Martlet keeps it in Windows Credential Manager.
   When a key is saved, leave the box empty to keep it, or paste a new key.
4. Read the box and tick it.
5. Click **Clone and use ElevenLabs**.

Martlet uploads the recording only then. ElevenLabs charges your account for
each reply. Martlet follows ElevenLabs' documentation, but nobody has tried it
with a live ElevenLabs account yet.

ElevenLabs copies your voice and speaks replies with it in real time. It adds
tones such as [whispers], [happy] or [sad] and sounds such as [laughs].

**Eleven v4 Turbo** is the fastest model (about 100 ms, as ElevenLabs
documents). If ElevenLabs refuses it, Martlet says so. Then choose **Eleven v3
Conversational**.

#### What you agree to

When you tick the box:

- You choose ElevenLabs for Voice.
- Martlet uploads the chosen recording to your ElevenLabs account once, to
  clone it. The same recording again is used without another upload.
- After that, Martlet sends only reply text to ElevenLabs, and only while it
  speaks.
- ElevenLabs requests cost money from your ElevenLabs account.
- You confirm that the recording is your own voice, a voice you have its
  speaker's permission for, or a published sample anyone may use.

The cloned voice stays in your ElevenLabs account (**My Voices**) until you
delete it there.

#### When ElevenLabs fails

| The message says | What to do |
| --- | --- |
| ElevenLabs didn't accept the API key | Check the key on elevenlabs.io and paste it again. |
| That key may not clone voices or speak | Allow **Voices** and **Text to Speech** for the key on elevenlabs.io. |
| The account is out of credits or its plan doesn't allow this | Check your account and plan on elevenlabs.io. |
| ElevenLabs is busy for your account | Try again in a moment. |
| Martlet couldn't reach ElevenLabs | Check the internet connection and try again. |
| Martlet is still setting up ElevenLabs | Wait for it to finish. |

## Volume and muting

**Companion › Voice › In conversations** has **Speak Martlet's replies aloud**
and **Voice volume**. Voice volume sets how loud Martlet speaks and sings on
this PC. It doesn't change Windows' volume or other apps. **Speakers** chooses
where her voice plays.

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
