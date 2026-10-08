# Singing

Martlet can write a song and sing it in a voice from **Companion › Voice › Voices**.

## Pipeline

The **singing** host role runs on an NVIDIA GPU and:

1. writes music with ACE-Step 1.5;
2. separates vocals and backing with Demucs;
3. matches the vocals to your chosen voice with SoulX-Singer-SVC by default, or VevoSing;
4. mixes and aligns tracks, lyrics, words and beat grid;
5. saves a `song` creation.

Songs are made ahead of time, not in real time.

## Setup

Open **Companion › Singing** (under *Optional extras*). The card shows the selected computer, setup status, GPU suitability, **Set up**, **Quality** and **Voice match**. Setup uses the normal host role flow and downloads pinned model files only after you choose it and accept terms.

## Hardware

Docs say Singing needs an NVIDIA GPU with 6 GB+ and roughly 5-7 GB free while a song is made. It shares a card with speaking/listening/lip-sync and frees resources after idle time. A second host helps if your main GPU is busy.

## In conversation

Ask: "write a song about rainy Sundays and sing it" or "sing the rain song again." There is no Play button; Martlet performs creations itself.

## Consent

The voice-rights confirmation is your assertion, not legal clearance. Do not imitate people without permission or deceive listeners. Disclose AI-generated songs when sharing.

More detail: [Singing](https://github.com/throndir2/Martlet/blob/main/docs/SINGING.md), [Creations](https://github.com/throndir2/Martlet/blob/main/docs/CREATIONS.md), [Voice Studio](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_STUDIO.md).
