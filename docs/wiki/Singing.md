# Singing

Martlet can write a song and sing it in a voice from **Companion › Voice › Voices**.

## Pipeline

A song has two parts. Each part has its own list of computers on **Companion › Singing**, and each runs on the **singing** host role on an NVIDIA GPU:

1. **Music**: ACE-Step 1.5 writes the song with its own singer, Demucs separates the vocals and the backing, and the lyrics, words and beats get their times. Each computer in the *Music* list chooses its **Quality**: **High quality** (the default, the clearest words) or **Fast**.
2. **Voice match**: SoulX-Singer-SVC (the default) or VevoSing changes the singing into your chosen voice. Each computer in the *Voice match* list chooses its **Model**. These can be other computers than the music's.
3. Your PC puts the matched vocals over the backing and saves a `song` creation.

With nothing on in the *Voice match* list, songs keep the music model's own singer. Songs are made ahead of time, not in real time.

## Setup

Open **Companion › Singing** (under *Optional extras*). Add your computers to the *Music* list and the *Voice match* list. Each computer's **Settings** show its setup status, **Set up Singing here**, its quality or model, and **Add VevoSing here**. Setup uses the normal host role flow and downloads pinned model files only after you choose it and accept terms. Your other computers follow the same lists.

## Hardware

Docs say Singing needs an NVIDIA GPU with 6 GB+ and roughly 5-7 GB free while a song is made. It shares a card with speaking/listening/lip-sync and frees resources after idle time. A second host helps if your main GPU is busy: it can write the music while another one matches the voice.

## In conversation

Ask: "write a song about rainy Sundays and sing it" or "sing the rain song again." There is no Play button; Martlet performs creations itself.

## Consent

The voice-rights confirmation is your assertion, not legal clearance. Do not imitate people without permission or deceive listeners. Disclose AI-generated songs when sharing.

More detail: [Singing](https://github.com/throndir2/Martlet/blob/main/docs/SINGING.md), [Creations](https://github.com/throndir2/Martlet/blob/main/docs/CREATIONS.md), [Voice Studio](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_STUDIO.md).
