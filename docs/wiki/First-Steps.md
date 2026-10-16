# First Steps

After you install Martlet, the welcome tour sets up this PC in a few clicks. You confirm every download, install and key before anything changes.

![Welcome tour](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/welcome.png)

Press **Let's begin**. **Back** returns to the step before, and **Skip for now** closes the tour. To see it again, open **Settings › What this PC is for › Replay the welcome tour**.

## 1. Start a new Martlet network, or join yours

The tour asks: *Is Martlet already on another of your computers?* Your computers with Martlet form your Martlet network and share the work.

- **No, this is my first one**: this PC starts a new Martlet network. You can add your other computers later.
- **Yes, join my Martlet network**: Martlet looks for your other computers on this network. Press **Join**. When both computers show the same check number, press **Allow** on the other computer.
- **This PC only lends its power to my other computers (host PC)**: this PC becomes a host. See [Setting Up a Host](Setting-Up-a-Host).

Martlet finds a network only through a computer that runs a host with **Let my other computers find this PC and ask to use its hosts** on. If your computer isn't listed, choose **It isn't listed: enter its address and pairing code**. See [Multiple Computers](Multiple-Computers).

## 2. See what this PC has

Martlet reads this PC's graphics card, graphics memory, memory and processor. It reads them on this PC only and sends nothing. Press **Next**.

## 3. Answer three quick questions

Martlet chooses the models, memory and graphics cards for you. It only asks:

1. **Do you play games or use heavy apps on this PC?** With **Yes**, Martlet keeps this PC's graphics card mostly free: it takes only Thinking and the voice. Martlet selects **Yes** when it finds a game library, such as Steam, Epic or GOG.
2. **May Martlet use free online services?** Choose **Never: everything stays on my computers**, **Only as a backup (recommended): replies stay on my computers**, or **Yes, when they're faster or smarter**. Free services such as NVIDIA Build need a free sign-up, and what you say goes to them.
3. **What matters more in a conversation?** Choose **Quick replies**, **Balanced (recommended)** or **Smarter replies**. Martlet picks the smartest model whose first word comes in time.

Press **Show my setup**. Martlet saves your answers. Change them later in **Settings › Recommended setup preferences**, or in **Recommended setup** on Home.

## 4. Use the suggested setup

**Here's what fits this PC** shows each part: what does it, where it runs (this PC, another of your computers or online), why, and its share of the graphics memory, memory and processor. A PC that joined your network also uses your other computers. Typical choices:

| Part | Usually |
| --- | --- |
| **Thinking** | A model in Ollama that fits this PC, or one on another of your computers. When you allow online services, NVIDIA Build can think, so the graphics card stays free for the voice. |
| **Voice** | Chatterbox Turbo on an NVIDIA graphics card with room, otherwise Chatterbox Nano on the card or the processor. |
| **Listening** | Your default microphone with Parakeet on the processor, or Whisper on the graphics card when it has room. |
| **Lip-sync** | Audio2Face on an NVIDIA card with room. Otherwise the mouth follows the voice's loudness. |

Then:

1. Press **Use these suggestions**.
2. If Thinking goes to NVIDIA Build and you have no key yet, follow **Get your free NVIDIA key**. Paste the key, accept the consent and press **Save key and set it all up**. Or choose **Skip the key: set up the rest now**.
3. Martlet closes the tour on Home and asks one confirmation, **Set it all up for me**. It lists the downloads. If you already run a model app such as LM Studio, **Use models your apps already run** lets Thinking use that model.
4. Press **Set it up**. Martlet installs what is needed. Thinking and listening come first, so you can talk within minutes. Martlet speaks once the voice is ready. A voice engine on this PC runs in Martlet's host service, which needs Docker Desktop. Docker Desktop shows its own terms first.

To choose yourself instead, press **Ask me three questions instead** (the setup advisor) or **I'll choose myself** (opens **Companion › Thinking**).

## 5. Check Home

![Home page](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/home.png)

Home tells you what needs attention and opens the page that fixes it. It never changes settings by itself.

- **Set it all up for me** sets up anything still missing for Thinking, Listening and the voice on a PC that isn't in a Martlet network.
- **Recommended setup** plans the best use of all your computers. You review the changes before **Reconfigure** applies them. When Martlet finds a better setup later, the button says **A better setup is available**.
- **Plan a setup from scratch** opens the setup advisor.

## 6. Say hi

1. Press **Start talking** to open the talk window. Type, or talk with push-to-talk.
2. Or press **Start listening** to talk hands-free.
3. Press **Show character**. The built-in Hiyori character appears on top of your windows.

The first reply can take a moment while Martlet loads its model. Home and the talk window say what is still warming up. See [Talking to Martlet](Talking-to-Martlet).

![Talk window](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/talk.png)

## Next

Every part of Martlet is on the **Companion** page. For example:

- **Companion › Voice** to choose or clone a voice. See [Voices](Voices).
- **Companion › Listening** for the microphone and speech recognition, and **Companion › People** for the voices Martlet knows. See [Listening and Voice ID](Listening-and-Voice-ID).
- **Companion › Character** for your own Live2D or VRM character. See [The Character](The-Character).
- **Companion › Vision** to let Martlet watch your screen. See [Vision](Vision).
- **Companion › Personality** and **Profiles** to make her your own. See [Personality and Characters](Personality-and-Characters).

More detail: [README getting started](https://github.com/throndir2/Martlet/blob/main/README.md#-up-and-running-in-minutes), [Welcome wizard](https://github.com/throndir2/Martlet/blob/main/docs/WELCOME_WIZARD.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).
