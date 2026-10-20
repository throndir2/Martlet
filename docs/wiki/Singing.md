# Singing

Martlet can write a song and sing it in the voice she speaks with, from
**Companion › Voice › Voices**. Her lips move with the words while she sings.

Singing is an optional extra. It needs an NVIDIA graphics card on this PC or
on one of your computers, or a cloud provider.

## How a song is made

A song has two parts. Each part has its own list of computers on **Companion ›
Singing**. When the first computer in a list is busy or doesn't answer, the
next one takes the job.

1. **Music** (*Music: where Martlet writes songs*): ACE-Step 1.5 writes the
   song with its own singer. Demucs then separates the vocals from the
   backing, and Martlet times the lyric lines, the words and the beats. Each
   computer chooses its **Quality here**: **High quality** (the default, the
   clearest words) or **Fast**.
2. **Voice match** (*Voice match: where Martlet matches the singing to its
   voice*): SoulX-Singer (the default) or VevoSing changes the singing into
   Martlet's voice. It needs only the voice's recording, with no training.
   Each computer chooses its **Model here**. These can be other computers than
   the music's.
3. Your PC puts the matched vocals over the backing and keeps the song in
   **Creations**.

With nothing on in the *Voice match* list, songs keep the music model's own
singer. With nothing on in the *Music* list, Martlet doesn't sing. Songs are
made ahead of time, not live. A 30-second song takes about 1 to 2 minutes.

### The music and voice match lists

- Each list is in order. A song goes to the first computer in the *Music*
  list that isn't making another song. When all of them are busy, the song
  waits on the computer with the shortest line.
- The *Voice match* list can name other computers than the *Music* list.
- With nothing on in the *Music* list, Martlet doesn't sing. With nothing on in
  the *Voice match* list, songs keep the music model's own singer.
- Open a computer's **Settings** in a list to set Singing up there, to choose
  its **Quality here** (High quality by default) or its **Model here**
  (SoulX-Singer by default), or to add more models.
- **Now** at the top of the page tells where Martlet writes the music and
  matches the voice, with which quality and model.

## Set it up

1. Open **Companion › Singing** (under *Optional extras*).
2. Add this PC or one of your computers to the *Music* list and to the *Voice
   match* list.
3. Open the computer's **Settings** and click **Set up Singing here**.
4. Read the confirmation. It names the graphics card that Singing needs, the
   download size, the licenses and the voice rule. Confirm it.
   [What each part downloads](#what-each-part-downloads) lists every model and
   its license.
5. Wait until the computer's Settings say that Singing is ready.

Nothing downloads before you confirm. The download is about 31 GB with the
image. On Windows, Singing runs in Martlet's host service, which needs Docker
Desktop. Your other computers follow the same lists, with each computer's
quality and model. **What Singing is** and **Voice match models** on the same
page compare the choices.

## Hardware

- An x86_64 computer with an NVIDIA graphics card of 6 GB or more. About 5-7 GB
  must be free while a song is made.
- Singing shares the card with the voice, listening and lip-sync. It uses the
  card only while it makes a song. It frees the card after five idle minutes.
- Spoken replies stay quick while a song is made. The song takes a little
  longer.
- A second computer helps when your main card is busy. One computer can write
  the music while another matches the voice.

## More choices

- **More music models**: in a computer's Settings in the *Music* list, click
  the model's button, such as **Add HeartMuLa here**. Then pick the model in
  the computer's **Music model here** box.
  - **ACE-Step 1.5 XL**: clearer words, about 20 GB more.
  - **HeartMuLa**: about 22 GB, needs a 16 GB+ card.
  - **DiffRhythm**: fast, about 9 GB.
  - **YuE**: strong singing but slow, about 8 GB, needs a 16 GB+ card.
- **More voice matches**: in a computer's Settings in the *Voice match* list,
  add VevoSing, Vevo2 (about 3 GB) or Seed-VC (about 2.5 GB). Click the
  model's button, such as **Add Vevo2 here**. Then pick it in the computer's
  **Model here** box.
- **Cloud providers**: click **A cloud provider** under a list. The *Music*
  list takes **ElevenLabs Music**, **Mureka** and **fal.ai** (MiniMax Music or
  ACE-Step). The *Voice match* list takes the **ElevenLabs Voice Changer**.
  You need your own API key, and each song may cost money.
- **Mureka in Martlet's voice**: in Mureka's Settings, tick **Sing in the voice
  Martlet speaks with**. Mureka then clones the voice from its recording, which
  goes to Mureka. Its songs need no voice match.
- Singing keeps one key for each provider in Windows Credential Manager, and
  your other computers get it too. ElevenLabs can use the ElevenLabs key from
  **Companion › Voice**.
- A cloud song is separated and timed on one of your singing computers when you
  have one. Without one, it keeps the provider's singer, and the voice is not
  matched.

### Cloud providers

- A cloud provider's model is part of its place in the list. To use another
  model of the same provider, add the provider again with that model.
- One key for each provider serves both of Singing's lists and all your
  computers. Change it in the provider's **Settings** in either list.
- When you add a provider, you agree that each song's lyrics and style go
  there and that each song may cost money. The voice match also sends the
  recording of the voice Martlet speaks with; ElevenLabs keeps a voice made
  from it on your account.
- A new provider goes last in its list. Move it up to use it sooner.

Martlet has not yet run HeartMuLa, DiffRhythm, YuE, Vevo2, Seed-VC or ACE-Step
1.5 XL on a real graphics card. It has not made a real song with a cloud
provider yet.

## In conversation

- Ask: "write a song about rainy Sundays and sing it". Martlet says she will
  work on it and makes the song in the background while you keep talking. She
  tells you when it is ready and sings it when you say yes.
- Ask "sing the rain song again", or ask for the chorus or a line. There is no
  Play button: Martlet performs her songs herself.
- She makes one song at a time, and at most four an hour.
- A song keeps going while you talk. To stop it, say "Martlet, stop singing",
  click **Stop singing** in the talk window, or press **Stop** or Esc. She
  remembers where she stopped and can go on from there.
- Her mouth follows the sung words, and the talk window shows the line she
  sings. **Companion › Voice › Voice volume** sets how loud she sings.
- It works also when the Thinking model runs on one of your computers or can't
  call tools.
- Each song belongs to the person who asked for it. It is in **Creations** on
  the computers where that person is signed in.

## When setting it up doesn't work

The computer's **Settings** and the line at the bottom of the window tell what
happened:

- *Singing is still being set up on ...*: Martlet sets up one computer at a
  time. Wait until its window says it is done, then set up the next one.
- *Singing can't run on this PC*: this PC has no NVIDIA graphics card, or one
  with less than 6 GB. Add another of your computers to the lists instead.
- *Setting it up ... stopped* or *... wasn't set up*: open the run window (in
  **Background tasks**) and read why it stopped. Fix that, then click **Set up
  Singing here** again.
- *Setup finished, but Martlet can't see Singing yet*: the singing service is
  still starting. Look again in a minute. If it stays like this, restart the
  computer's host service.
- *Its Martlet is too old to make the music and the voice match apart*: update
  Martlet on that computer. Until then it makes whole songs.
- Singing's keys *were saved by a newer Martlet*: update Martlet on this PC to
  use them.
- *This PC's Singing keys couldn't be saved*: check that Martlet can write to
  its data folder and to Windows Credential Manager.

## Consent and licenses

- The voice-rights confirmation is your own statement, not legal clearance.
  Do not imitate people without permission or deceive listeners. Say that a
  song is made by AI when you share it.
- VevoSing, Vevo2, YuE and DiffRhythm's style model are for personal,
  non-commercial use only. Seed-VC is GPL-3.0. SoulX-Singer and ACE-Step allow
  any use.
- ACE-Step asks you to check that songs are original and to say that they are
  made by AI.
- Songs are sung in a voice from your voice library. Its recording goes to the
  computer that matches the voice.

### What each part downloads

| Part | Download | Licenses |
|---|---|---|
| Singing (always) | about 16 GB, in a container | ACE-Step 1.5 turbo and SFT with its planner and text encoder (MIT); SoulX-Singer and SoulX-Singer-SVC, RMVPE (Apache-2.0); Demucs and its vocals model (MIT); OpenAI Whisper base (Apache-2.0); PyTorch |
| ACE-Step 1.5 XL | about 20 GB more | its SFT model, huggingface.co/ACE-Step/acestep-v15-xl-sft (MIT); needs a 12 GB+ card, 16 GB+ beside the voice |
| HeartMuLa | about 22 GB | heartlib, github.com/HeartMuLa/heartlib; HeartMuLa-oss-3B, HeartCodec and their tokenizer, huggingface.co/HeartMuLa (all Apache-2.0); needs a 16 GB+ card |
| DiffRhythm | about 9 GB | DiffRhythm 1.2 full, github.com/ASLP-lab/DiffRhythm (Apache-2.0); its VAE (Stability AI Community License); MuQ-MuLan and MuQ (CC-BY-NC-4.0: non-commercial use only); XLM-RoBERTa base (MIT); espeak-ng; needs an 8 GB+ card |
| YuE | about 8 GB | YuE2-3B and its VAE, huggingface.co/m-a-p (CC-BY-NC-4.0: non-commercial use only; the licensors let individual creators use what they make); needs a 16 GB+ card, 24 GB for full precision |
| VevoSing | about 4.5 GB more | Amphion's Vevo1.5, huggingface.co/amphion/Vevo1.5 (CC-BY-NC-ND-4.0: personal, non-commercial use only, and nothing made from it may be shared as a derivative); OpenAI Whisper medium (MIT) |
| Vevo2 | about 3 GB more | Amphion's Vevo2, huggingface.co/RMSnow/Vevo2 (CC-BY-NC-ND-4.0, as VevoSing); OpenAI Whisper medium (MIT) |
| Seed-VC | about 2.5 GB | Seed-VC, github.com/Plachtaa/seed-vc, and its singing model (GPL-3.0, no longer maintained); RMVPE (MIT); CAMPPlus (Apache-2.0); NVIDIA BigVGAN v2 (MIT); OpenAI Whisper small (Apache-2.0) |

An added model keeps what the computer already has. ACE-Step 1.5 and
SoulX-Singer stay set up, and each computer's **Music model here** or **Model
here** switches between them.

More detail: [Singing](https://github.com/throndir2/Martlet/blob/main/docs/SINGING.md), [Singing in conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md#singing-in-conversation), [Creations](https://github.com/throndir2/Martlet/blob/main/docs/CREATIONS.md), [Pictures and Creations](Pictures-and-Creations).
