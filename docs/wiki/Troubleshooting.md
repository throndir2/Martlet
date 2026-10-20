# Troubleshooting

Start on **Home**. It lists what needs attention and opens the page that fixes it. It never changes settings by itself.

## Martlet can't reply

Home says why. Common causes:

- Thinking isn't set up. Choose **Set it all up for me**, or **Set up thinking** to open **Companion › Thinking**.
- The provider retired your model. Home offers a replacement with **Use**.
- Home offers the fix when a key was removed, Ollama isn't running or the model isn't downloaded, for example **Download** *model*.
- None of your computers has room for Thinking. Home offers **Get a free key** and **Add your key** for NVIDIA Build, so Martlet can think online. NVIDIA then gets what you say.
- A host doesn't answer. See [A host doesn't answer](#a-host-doesnt-answer).

## The first reply is slow

- **Warming up**: Home and the talk window say what still loads (the model, listening, the character) and for how long. You can type or talk at once. The reply starts when the model is ready. Then Home shows how long warming up took.
- Ollama unloads a model after five idle minutes. Loading it again takes from about 15 seconds to a few minutes.
- If the model thinks before it answers, set **Thinking steps** to **Off** on **Companion › Replies**. **Off** is the default.

## Ollama on this PC doesn't answer

1. Open **Companion › Thinking › This PC**.
2. Choose **Test model**. It starts Ollama, checks the download, loads the model and asks for a short reply. Each step shows in a window, with Ollama's own error text.
3. If the model doesn't fit in the free graphics memory, choose a smaller model, or close programs that use the graphics card.

The error *Gemma4Assistant requires ctx_other to be set … error loading model: vector* also means that the model doesn't fit in the free graphics memory.

## A game is slow while Martlet runs

The **Martlet on this PC** card on Home shows what Martlet keeps in memory now. **Free up this PC** ends the conversation, hides the character and unloads Martlet's models, so the game gets the memory back. Martlet rests until you talk to it again: your settings stay, and **Start talking** or **Start listening** loads the models again. When Martlet holds nothing, the card says so. With **Do you play games or use heavy apps on this PC?** set to **Yes**, **Recommended setup** keeps this PC's graphics card mostly free. The answer is saved for all your computers, and the next Recommended setup plans with it.

## Martlet forgets the start of a long conversation

Each reply sends as much of the conversation as fits the **Context size** on **Companion › Replies**. Ollama on this PC holds only what its own context length setting allows (often 4,096 tokens). Raise it in the Ollama app's settings, then choose **Check model limit**. A larger context uses more graphics memory. If a cloud model says *context length exceeded*, set a smaller context size.

## A host doesn't answer

- The **Devices** map shows which computer stopped answering. Home says so too.
- If the host's computer is on but its host service isn't running, select the host and choose **Update host**. The host service also starts again by itself after the computer restarts.
- An older host can lack newer features. **Settings › App updates › Update hosts** updates all of them.

## The character doesn't show

1. Press **Show character** on Home.
2. Check **Companion › Character** for a problem message.
3. Open **Settings › Tools › Prerequisites** and install the **WebView2 runtime** if it is missing.
4. If the character is off screen, choose **Reset character position** on Home.

## Martlet doesn't hear you, or hears itself

- Check that Windows lets desktop apps use the microphone. **Settings › Tools › Prerequisites** opens that Windows setting.
- Choose your microphone on **Companion › Listening**.
- If Martlet stops itself while it speaks, turn on **Reduce echo from my speakers** on **Companion › Listening** (on by default), or use headphones.
- **Google Gemini** hears your voice only when you tick **Let Thinking hear my voice**. On Google's free tier, Google's reviewers may read what you send, so don't send anything private. To get a free key, open [aistudio.google.com/apikey](https://aistudio.google.com/apikey), sign in with a Google account, accept the Gemini API terms, then copy the key (or choose **Create API key**).
- **Describe PC sounds** needs a judge: a Thinking pool model that hears, or the sound tagger that comes with Martlet. If the sound tagger is missing, reinstall Martlet.

## Docker Desktop or WSL won't start

Martlet's host service on Windows needs Docker Desktop and WSL 2. **Settings › Tools › Prerequisites** checks WSL 2, Virtual Machine Platform, Docker Desktop, a waiting Windows restart and hardware virtualization.

- When Windows must restart, Martlet asks first, then continues the setup after you sign in.
- If virtualization is off in the PC's firmware (UEFI or BIOS), Martlet can restart into the firmware settings. Turn on *Intel Virtualization Technology (VT-x)* or *SVM Mode* (AMD) yourself.
- Don't use Docker Desktop's *Reset to factory defaults* to fix this. It deletes Docker's data and doesn't turn on virtualization.

When two setup windows need the same step (for example, starting Docker Desktop), one waits for the other. Its window says what it waits for, and it continues once that step is done.

## Martlet closed or showed an error

- An unexpected error shows once in a window. **Copy** takes the whole report, and **Open logs folder** opens the logs. Martlet keeps running, but the last action may not have finished.
- When Martlet ended without a record from Windows, Task Manager, a sign-out, a shutdown or a power loss probably ended it, or Windows Error Reporting is off.
- After a crash, Home says **Martlet closed unexpectedly last time**, with **Open Diagnostics**.

## Get help

1. Open **Diagnostics** in the left rail. It shows the logs of all your computers, with filters and a search box. Every computer in your Martlet network shares its logs with the others, so each one shows them all.
2. Choose **Save logs to share**. It saves one ZIP file and shows it in File Explorer.
3. Review the file. Logs never hold keys or what you said, but they can hold local paths, computer names and provider error text.
4. Attach the file to a new [issue](https://github.com/throndir2/Martlet/issues).

Diagnostics shows only this PC's logs until you pair a host (**Devices › Add a computer**). If the logs folder doesn't open, check that you can open Martlet's data folder. If copying fails, another app is using the clipboard: try again.

### The Troubleshooting window

**Settings › Tools › Troubleshooting** records Martlet's checks while you reproduce a problem, then exports them to a ZIP file on this PC.

1. Choose **Refresh status** to see the checks and the last conversation activity, with a suggested action.
2. Start recording, then do what goes wrong. Recording never keeps what you say or type.
3. Choose **Preview** and review the files. If you change the selection or the destination, preview and confirm again.
4. Choose **Export**. Martlet never overwrites a file and never uploads anything. Choose a new file name when the file already exists.
5. Choose **Stop** when you are done. Cleanup can take a moment. The records stay on this PC until you remove them.

The export leaves out settings, secrets, provider answers, transcripts, audio, screenshots, history, memory, model and voice names, device and user names, paths and keys. It replaces trace, turn and journal IDs with new names for each export. Times and counts stay. This lowers what the files show, but it doesn't guarantee privacy.

If the window says that troubleshooting is busy, timed out, was closed or failed, wait for the current action and try again, or choose **Retry cleanup**. If its storage is damaged, keep the folder for review. If access was denied, choose a folder that you can write to.

**Settings › Tools › Troubleshooting** shows Martlet's own checks on this PC and suggestions. **Settings › Tools › Backup / restore** saves your settings to a file and restores them.

More detail: [Troubleshooting](https://github.com/throndir2/Martlet/blob/main/docs/TROUBLESHOOTING.md), [Diagnostics](https://github.com/throndir2/Martlet/blob/main/docs/DIAGNOSTICS.md), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Voice latency](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_LATENCY.md).
