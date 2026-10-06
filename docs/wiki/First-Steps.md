# First Steps

After installing, Martlet's welcome wizard sets this PC up in a few clicks.

![Home page](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/home.png)

## 1. Start a new Martlet network, or join yours

- **No, this is my first one**: this PC starts your Martlet network. Add your other computers later.
- **Yes, join my Martlet network**: Martlet looks for your other computers on this network. Press **Join**, then **Allow** on the other computer when both show the same check number.
- **This PC only lends its power to my other computers**: this PC is a host.

Change it later in **Settings › This PC's role**.

## 2. Let Martlet suggest a setup

Martlet reads this PC's graphics card, memory and processor, then asks whether to **keep everything on my computers** or whether **free online services are fine**. It suggests what runs where, with how much of the graphics card, memory and processor each part uses:

| Job | Typically |
| --- | --- |
| **Thinking** | Gemma 4 E2B in Ollama on the graphics card; with free online services, NVIDIA Build (the wizard walks you through the free key). |
| **Voice** | Chatterbox Turbo on an NVIDIA graphics card with room, otherwise a Windows voice. |
| **Listening** | Your default microphone with Parakeet on the processor, or Whisper on a card with room to spare. |
| **Lip-sync** | Audio2Face on a big NVIDIA card, otherwise the mouth follows the voice's loudness. |

Press **Use these suggestions** and confirm once; Martlet installs what's needed and leaves you on Home. **Set it all up for me** on Home does the same for anything still missing. The voice gets the graphics card before listening. A PC paired with your other computers uses their setup instead. To choose yourself, use **Set up thinking** or **Get a recommendation**:

| Choice | Use it when |
| --- | --- |
| **This PC** | You want privacy/no per-request cost and have Ollama or a compatible local model. |
| **Another of your computers** | You have a paired host with a better GPU or model. |
| **Cloud provider** | You want hosted models through OpenAI, OpenRouter, NVIDIA Build or another OpenAI-compatible endpoint. |

Cloud choices may cost money and send text/images to that provider.

## 3. Optional setup

- **Companion › Listening** to change the microphone or speech recognizer.
- **Companion › People** for recognized voices.
- **Companion › Voice** to change the voice.
- **Companion › Character** for the desktop character.
- **Companion › Vision** for screen or camera commentary.

## 4. Start talking and listening

Press **Home › Start listening** to talk hands-free, or **Start talking** to type or use push-to-talk.

![Talk window](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/talk.png)

## 5. Show the character

Press **Show character**. The built-in Live2D Hiyori model appears as a transparent desktop overlay. Add your own Live2D or VRM model later.

## 6. Check Home

Home changes no settings by itself. It shows problems and opens the page that can fix them.

More detail: [README getting started](https://github.com/throndir2/Martlet/blob/main/README.md#-up-and-running-in-minutes), [UI design](https://github.com/throndir2/Martlet/blob/main/docs/UI_DESIGN.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).
