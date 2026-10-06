# First Steps

After installing, Martlet helps you choose this PC's role and how to start.

![Home page](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/home.png)

## 1. Choose this PC's role

- **Talk with my companion here**: this is the PC where you type, talk, hear replies and show the character.
- **Lend this PC to Martlet**: this PC is a host that lends compute to another Martlet companion.

Change it later in **Settings › This PC's role**.

## 2. Set it all up

On **Home** (or the tour's last step), choose **Set it all up for me**. Martlet reads this PC's graphics card and, after one confirmation, sets up:

| Job | Default |
| --- | --- |
| **Thinking** | The smallest local model that also hears your voice (Gemma 4 E2B) in Ollama on this PC. |
| **Voice** | A voice engine (Chatterbox Turbo) on the NVIDIA graphics card when it has room beside Thinking; otherwise a Windows voice on the processor. It speaks with a Windows voice until the engine is ready. |
| **Listening** | Your Windows default microphone, with Parakeet on the processor, or Whisper on the graphics card when room is left after the voice. |

The voice gets the graphics card before listening. A PC paired with your other computers uses their setup instead. To choose yourself, use **Set up thinking** or **Get a recommendation**:

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
