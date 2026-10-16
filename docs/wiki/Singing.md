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

## Set it up

1. Open **Companion › Singing** (under *Optional extras*).
2. Add this PC or one of your computers to the *Music* list and to the *Voice
   match* list.
3. Open the computer's **Settings** and click **Set up Singing here**.
4. Read the confirmation. It names every download, its source and its
   license. Confirm it.
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

## Consent and licenses

- The voice-rights confirmation is your own statement, not legal clearance.
  Do not imitate people without permission or deceive listeners. Say that a
  song is made by AI when you share it.
- VevoSing, Vevo2, YuE and DiffRhythm's style model are for personal,
  non-commercial use only. Seed-VC is GPL-3.0. SoulX-Singer and ACE-Step allow
  any use.

More detail: [Singing](https://github.com/throndir2/Martlet/blob/main/docs/SINGING.md), [Singing in conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md#singing-in-conversation), [Creations](https://github.com/throndir2/Martlet/blob/main/docs/CREATIONS.md), [Pictures and Creations](Pictures-and-Creations).
