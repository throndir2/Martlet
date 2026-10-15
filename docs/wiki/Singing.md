# Singing

Martlet can write a song and sing it in a voice from **Companion › Voice › Voices**.

## Pipeline

A song has two parts. Each part has its own list of computers on **Companion › Singing**, and each runs on the **singing** host role on an NVIDIA GPU:

1. **Music**: ACE-Step 1.5 writes the song with its own singer, Demucs separates the vocals and the backing, and the lyrics, words and beats get their times. Each computer in the *Music* list chooses its **Quality**: **High quality** (the default, the clearest words) or **Fast**.
2. **Voice match**: SoulX-Singer-SVC (the default) or VevoSing changes the singing into your chosen voice. Each computer in the *Voice match* list chooses its **Model**. These can be other computers than the music's.
3. Your PC puts the matched vocals over the backing and saves a `song` creation.

With nothing on in the *Voice match* list, songs keep the music model's own singer. Songs are made ahead of time, not in real time.

## More choices

- **More music models on your computers**: **ACE-Step 1.5 XL** (clearer words, about 20 GB), **HeartMuLa** (about 22 GB, a 16 GB+ card), **DiffRhythm** (fast, about 9 GB) and **YuE** (strong singing but slow, about 8 GB). Click **Add <model> here** in a computer's settings, then pick it in its **Music model** box.
- **More voice matches on your computers**: **Vevo2** (about 3 GB) and **Seed-VC** (about 2.5 GB). Click **Add <model> here** in a computer's settings in the *Voice match* list, then pick it in its **Model** box.
- YuE, Vevo2, VevoSing and DiffRhythm's style model are for personal, non-commercial use only. Seed-VC is GPL-3.0. Martlet has not measured HeartMuLa, DiffRhythm, YuE, Vevo2 or Seed-VC on a real graphics card yet.
- **Cloud providers**: click **A cloud provider** under a list. The *Music* list takes **ElevenLabs Music**, **Mureka** (it can sing in Martlet's own voice) and **fal.ai** (MiniMax Music or ACE-Step). The *Voice match* list takes **ElevenLabs Voice Changer**. You need your own API key, and each song may cost money. Keys go to your other computers too.
- A cloud song is separated and timed on one of your singing computers when you have one. Without one, it keeps the provider's singer and the voice is not matched.

## Setup

Open **Companion › Singing** (under *Optional extras*). Add your computers to the *Music* list and the *Voice match* list. Each computer's **Settings** show its setup status, **Set up Singing here**, its quality or model, and **Add VevoSing here**. Setup uses the normal host role flow and downloads pinned model files only after you choose it and accept terms. Your other computers follow the same lists.

## Hardware

Docs say Singing needs an NVIDIA GPU with 6 GB+ and roughly 5-7 GB free while a song is made. It shares a card with speaking/listening/lip-sync and frees resources after idle time. A second host helps if your main GPU is busy: it can write the music while another one matches the voice.

## In conversation

Ask: "write a song about rainy Sundays and sing it" or "sing the rain song again." There is no Play button; Martlet performs creations itself.

## Consent

The voice-rights confirmation is your assertion, not legal clearance. Do not imitate people without permission or deceive listeners. Disclose AI-generated songs when sharing.

More detail: [Singing](https://github.com/throndir2/Martlet/blob/main/docs/SINGING.md), [Creations](https://github.com/throndir2/Martlet/blob/main/docs/CREATIONS.md), [Voice Studio](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_STUDIO.md).
