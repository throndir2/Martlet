# Listening and Voice ID

Martlet hears you through always listening or push-to-talk. Speech-to-text
writes down what you say. Martlet can also hear *how* you say it, and it learns
who is talking.

## Listening

Open **Companion › Listening**. **Where it runs** is a list of the places that
listen, in order. The first one that is on does the job. The next ones help when
it is busy or doesn't answer. Use **Add to the list** to add **This PC**, one of
your computers, a graphics card or **A cloud provider**.

| Choice | Where it runs | Good to know |
| --- | --- | --- |
| **Parakeet in Martlet** | This PC's processor | No Docker and no setup. A model downloads only when you choose it. |
| **Whisper** | This PC's graphics card or processor | Runs in Martlet's host service, which needs Docker Desktop. Martlet installs it first. |
| Whisper or Parakeet on another computer | One of your computers: Windows, Linux, Mac or ARM64 | This PC stays free. See [Setting Up a Host](Setting-Up-a-Host). |
| **OpenAI** transcription | OpenAI's servers | Needs your API key. Your recordings leave this PC, and requests cost money. |

**How it listens on this PC** offers three Parakeet models:

- **Parakeet TDT 110M**: the fastest, English only. The default when Windows
  is in English.
- **Parakeet TDT 0.6B v2**: the most accurate in English. It is better in
  noisy rooms and with a distant microphone.
- **Parakeet TDT 0.6B v3**: 25 European languages, found by itself. The
  default when Windows is in another language.

With Parakeet, your speech stays on this PC and recordings are not saved. The
**Microphone** card shows the microphone that Martlet uses. Click **Test or
change microphone** to change it.

A Parakeet model chosen on another of your computers doesn't download by
itself. Download it on this PC too, in **Companion › Listening › Parakeet in
Martlet**. A model chosen on a newer Martlet needs that newer Martlet on this
PC.

Whisper on the graphics card needs a recent NVIDIA driver. When the driver is
too old, Martlet tells you the version it needs and the version this PC has.
Update the driver, then click **Check the graphics card again**.

### Test your microphone and speakers

Click **Test or change microphone** to open the **Microphone and speakers**
window.

1. Pick a microphone and your speakers or headphones. The Windows defaults
   work unless you pick something else. Each time you open a list, Martlet
   looks again, so a device that you just plugged in shows up.
2. Click **Test microphone** and approve the test. Talk or hum for up to 5
   seconds. The bar shows the level. The level checks only the volume, not
   speech recognition.
3. Check your volume. Click **Play test sound** and approve it. Martlet plays
   a short, quiet beep. If you heard it, click **Yes, I heard it**. If not,
   check your volume and the device that you picked, then play it again.

Good to know:

- Your choices save as you go. Test a device again any time.
- Martlet lists device names without opening the devices.
- A test runs only after you approve it. No recording is saved or sent.
- Martlet never changes your volume or your Windows default devices.
- With no microphone, you can still type to Martlet.
- **Details** shows the last result and the test history.
- When your settings changed in another window, or saving didn't finish,
  click **Reload**. Then check your choices and pick again.

### When an audio test fails

| Martlet says | What to do |
| --- | --- |
| No sound came through | Check that the microphone is plugged in. Then open Windows **Settings › Privacy & security › Microphone** and let desktop apps use the microphone. |
| Windows is blocking the microphone | Open Windows **Settings › Privacy & security › Microphone** and let desktop apps use the microphone. |
| Too quiet | Check that the microphone isn't muted, also with a switch on the device. Turn up its input level in Windows **Sound settings**. |
| The microphone stopped sending sound partway through | Check the cable or the wireless connection, then test again. |
| Only a short sound came through, or a brief click isn't enough | Keep talking or humming for the whole test. |
| No microphone found, or no speakers or headset found | Plug the device in or turn it on in Windows **Sound settings**. Then open the list again. |
| *Device* isn't connected | Plug it in, or pick another device in the list. |
| Another app is using this device | Close that app, for example a call app, then try again. |
| This device uses an audio format Martlet can't open | Pick another device. |
| The device was unplugged or changed | Reconnect it, then pick it again from the list. |
| The test took too long, or the device didn't respond in time | Wait a moment, then try again. |
| The device stopped responding | Close the apps that use the device and try again. If tests stay unavailable, restart Martlet. |
| Another setup task is still finishing | Wait for the other task to finish. The test buttons come back by themselves. |
| Windows is locked | Unlock Windows, then start a new test. |
| Couldn't list devices | Martlet uses your Windows defaults. Open a list to look again. |

### When the computer that listens goes away

Sometimes another computer or OpenAI can't hear you. For example, the computer
is off, its service stopped or the key was removed. Then a Parakeet model
downloaded on this PC hears you instead, on the processor. Nothing is sent
anywhere. For the next minute,
Martlet goes straight to Parakeet, then tries your choice again. The **Now**
card says which model stands in. When none is downloaded, it offers to download
one without changing Listening.

## How you talk

**Companion › Listening › How you talk** chooses **Always listening
(recommended)** or **Push-to-talk**. It also sets the **Sensitivity**, the
pause before a reply (**Reply after**) and the **Word check**. [Talking to
Martlet](Talking-to-Martlet#how-martlet-takes-turns) explains its turn-taking
and interrupting choices. [Voice ID](#voice-id) is below.

**Reduce echo from my speakers** (on by default) removes what this PC plays
from the microphone. So Martlet works without headphones and keeps listening
while she speaks. That sound is never saved or sent.

**Hear what this PC plays** (off by default) lets Martlet hear a video, a
stream, a call or a game, so she can watch along. It never goes to memory or
to voice recognition.

## Audio model

Speech-to-text writes down *what* you say. Martlet can also hear *how* you say
it: your tone, a laugh or a sigh, other voices and the sounds around you. These
settings are on **Companion › Hearing**, under *Optional extras*. **Companion ›
Listening › How Martlet hears your tone** opens that page.

- With an empty **Audio models** list (the default), the Thinking model hears
  your recording itself, if it can. Gemma 4 E2B can.
- Your Thinking model may only read text. Then add an audio model that hears:
  Ollama on this PC, one of your computers, a server you run or a cloud
  provider. It describes each recording in words for Thinking. A reply never
  waits for it.
- **Hear how you say it** has the consent box (**Let Thinking hear my voice**
  or **Let the audio model hear my voice**). A recording that would leave this
  PC waits until you tick it. **Send my voice straight to Thinking (fastest)**
  starts the reply the moment you stop talking.
- **Test hearing** sends the model one word said by Windows speech, never your
  voice, and checks that the model hears it. **Turn hearing off** gives
  Thinking only your words.

More detail: [Image and audio models](https://github.com/throndir2/Martlet/blob/main/docs/SENSE_MODELS.md).

## People

Open **Companion › People**. **Recognize voices in conversations** is on by
default. Its files come with Martlet, so nothing downloads. Voice matching
happens on this PC. The voiceprints come from this PC or from a computer in
the **Where it runs** list. When you turn recognition on, open conversations
use it from your next message. When you turn it off, Martlet doesn't check
who is talking, and it keeps the saved voices.

- **Voices Martlet knows** lists the voices Martlet has heard, up to 1,000.
  Type a name to say who a voice is. Add all the other names the person goes by
  (up to 40). Click a name to make it the one Martlet uses. A name with **?**
  was only heard in conversation. Changes sync to all your computers.
- Martlet also learns names from what people say, such as "my name is Sam".
  It never learns a voice from a guess.
- **Hear them** plays the last clips of a voice you haven't named, so you can
  tell who it is. The clips stay on this PC and go when you name the voice.
  To keep no clips, untick **Keep the last 20 clips of voices you haven't
  named**.
- **This voice is** with your name after it links a voice to your account, so
  Martlet knows it is you talking. Each voice belongs to at most one person in
  the household. A voice never signs anyone in. See [Accounts and Household](Accounts-and-Household).
  Before you sign in, the box is called **This is me**. When a voice belongs
  to another person's account, Martlet asks before it makes the voice yours.
  Then Martlet treats that voice as you on all your computers.
- **Same person as...** and then **Merge** join two voices of one person.
  Martlet then treats them as one person on all your computers. You can't undo
  a merge. When two voices have the same name, People tells you, so you can
  merge them.
- **Memories** shows what Martlet remembers about that person.
- **Forget** deletes one voice and its names from all your computers. If
  Martlet hears that voice again, it shows up as a new voice. **Forget all
  voices** clears the list; recognition stays on and starts over. What Martlet
  remembers about a forgotten voice stays in **Memory** under **Forgotten
  voices**, where you can delete it.
- The voice list has a size limit. When it is full, forget or merge some
  voices.
- A guest who says only a little shows up after a few short remarks within 30
  minutes.
- **Where it runs** lists this PC's processor and your computers that run the
  **Voice recognition** role, in order. Home's **Recommended setup** can set
  this up on one of your computers. Another computer uses its NVIDIA graphics
  card when it has one. A computer that answers before this PC would have
  finished recognizes the voice. When no computer answers in time, this PC
  recognizes the voice itself, so a slow computer never holds it up. The list
  is the same on all your computers. **Check it now** sends three seconds of a
  test tone through the list, in order, the way your voice goes. It learns and
  keeps nothing.

Voice recognition is a convenience, not a lock. Similar voices, recordings, a
cold, distance or a new microphone can fool it. It can also split one person
into two voices. If it does, merge them.

## Voice ID

Voice ID is a filter that makes Martlet answer only you. People is different:
it recognizes everyone after speech-to-text, and it helps Memory know who said
what.

1. Open **Companion › Listening › How you talk**.
2. Click **Set up Voice ID**.
3. Tick **Allow microphone recording**. Martlet records up to 10 seconds for
   each phrase. Nothing is uploaded, and the recordings are never saved.
4. Record three phrases. Use your usual microphone and your normal voice.
   Recording stops when you pause. Each phrase needs at least 1.5 seconds of
   speech: if Martlet heard less, move closer or speak up, then record it
   again.
5. Click **Test voice** and say anything for at least a second. Move **Match
   threshold** lower only if Martlet often misses you.
6. Close the window and tick **Only answer my voice**.

After the third phrase, Martlet saves your voiceprint and suggests a match
threshold. It also shows the speech that it used and how consistent the
phrases were. When the phrases sounded different, start over somewhere
quieter if the test misses you. **Start over** doesn't change the saved
voiceprint until you finish all three phrases again.

**Test voice** shows a score. When the score is at or above the match
threshold, Martlet recognized you. A higher threshold is stricter: fewer other
voices get through, but Martlet may miss you more often. When your score is
often just below the threshold, lower it a little.

When Voice ID can't read, save or delete your voiceprint, check that Martlet
can write to its data folder, then enroll again. When another Martlet action
uses the microphone, stop it and try again.

With it on, Martlet checks each recording on this PC before it sends it
anywhere. Another voice, the TV or too little speech is dropped and never goes
to speech-to-text. People still learns the other voices. **Delete voiceprint**
removes your voiceprint.

## Sharing with your computers

- The people Martlet recognizes and their names are always shared with all
  your computers, even when **Keep Martlet the same on all my computers** is
  off. Hosts a friend shares with you never get them. **Companion › People ›
  Your computers** shows when they last synced, with **Sync now**.
- Your Listening choices, Voice ID and whether Martlet recognizes voices travel
  with your other settings. This happens while **Devices › Settings for all
  devices › Keep Martlet the same on all my computers** is on. The microphone
  stays on each computer.

## Privacy

- Listening starts only when you press **Start listening** or use
  push-to-talk.
- Parakeet, Voice ID and voice recognition run on your own computers. Nothing
  is uploaded for them.
- Martlet keeps no audio after recognition, apart from the clips of voices you
  haven't named. `voices.json` keeps voiceprints and names.
- Martlet learns voices only from the microphone, never from what this PC
  plays.

More detail: [Voices and People](https://github.com/throndir2/Martlet/blob/main/docs/VOICES.md), [Voice recognition host](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_RECOGNITION_HOST.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Memory](https://github.com/throndir2/Martlet/blob/main/docs/MEMORY.md).
