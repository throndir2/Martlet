# Recognizing people by voice, and Parakeet

Martlet can tell the people around its microphone apart, learn the names they
go by from the conversation, and keep that list the same on every computer you
own. It reuses AudioTranscriber's speaker recognition: sherpa-onnx with the
pyannote segmentation 3.0 and WeSpeaker ResNet34-LM models. The same engine
also runs **Parakeet**, AudioTranscriber's default speech-to-text, as a
Listening choice on this PC.

Voice recognition is part of Martlet and on by default: the engine and its two
voice models ship with Martlet, so there is nothing to download, and Martlet
starts learning who is talking from your first conversation. Turn it off on
People at any time (your saved voices are kept). Parakeet stays optional and is
downloaded only when you ask. Nothing is uploaded for either, and the only audio
kept is a few clips of voices you haven't named yet (see below). Voices
are recognized and learned only from the microphone, never from
[what the PC plays](CONVERSATION.md#hearing-what-this-pc-plays).

## Companion › People

- **Recognize voices in conversations.** On unless you turned it off
  (`voice-recognition.txt`, the same on all your computers). Martlet's Desktop folder includes sherpa-onnx
  1.13.8 with ONNX Runtime 1.28.2 (from the official NuGet runtime package) and
  the two voice models (about 41 MB together, in `voice-recognition\`). The
  build downloads the models once and keeps each only if it matches its pinned
  SHA-256; packaging checks the shipped bytes again. If the files are missing
  (a damaged installation), People says so and asks you to reinstall Martlet.
- **Your computers.** The list is always the same on all your computers, so
  Martlet learns everyone's voice everywhere. It doesn't depend on **Keep
  Martlet the same on all my computers**. See
  [sharing](#sharing-between-your-computers).
- **Keep the last 5 clips of voices you haven't named.** On unless you turn it
  off (`voice-clips.txt`, this PC only; turning it off deletes every clip). See
  [clips](#clips-of-voices-you-havent-named).
- **Voices Martlet knows.** Every voice it has heard, the owner's first, then
  named ones, then the most recently heard. Each voice is one compact card:
  - its **names** as chips, the one Martlet uses first (outlined). A voice goes
    by up to 40 names. Click a name to make it the one Martlet uses; a name
    marked **?** was only heard in conversation, and clicking it confirms it.
    **×** removes a name. Type in the box and press Enter (or **Add**) to add
    one; on a voice you haven't named, what you type becomes the name Martlet
    uses (it always wins over learned names). Changes save at once and sync to
    your other computers;
  - **Hear them**: the voice's last few clips, newest first, while you haven't
    named it. Play one to hear who it is;
  - **This is me**;
  - **Same person as...** then **Merge** (the names, voiceprints, counts and
    clips combine, and so do the facts Memory keeps for them; it can't be
    split again);
  - **Memories** opens Memory showing that voice's facts
    ([Whose memories](MEMORY.md#whose-memories));
  - **Forget** (its voiceprint, names and clips are deleted; what Martlet
    remembers about them stays in Memory under *Forgotten voices* until you
    delete it).
  When two voices go by the same name, the page suggests merging them.
  **Forget all voices** clears the list.

Companion › Listening links to People and says how many voices are known.

## What happens in a conversation

For each push-to-talk or hands-free utterance (after Voice ID, when that is
on), while speech-to-text runs:

1. **Who spoke.** pyannote segmentation finds who spoke when (up to three
   people per ten seconds; longer utterances are clustered). For each person,
   only stretches where nobody else talks are used: edges trimmed by 120 ms, at
   least 1.5 s each, up to three stretches of at most 8 s. Each gives a
   WeSpeaker voiceprint; at least 2 s of such clean speech is needed, and
   stretches that disagree are dropped rather than blended.
2. **Matching.** Each voiceprint is scored against every known voice (centroid
   agreement averaged with the closest sample; merged voices use their closest
   sample). **Known** needs a score of at least 0.70 and a 0.08 lead over the
   runner-up, and the voice learns a little (its centroid moves and it may keep
   the sample among up to five diverse ones). **New** (below 0.45 against
   everyone) adds *Voice N*. Anything in between stays unattributed: Martlet
   never forces a guess. An utterance too short for clean evidence is matched
   on all its speech, but only a confident match counts and nothing is learned
   or added. When the list is full (64 voices), the longest-unheard voice with
   no name that isn't yours makes room.
3. **Telling the Thinking model.** The reply's instructions say what the
   voices block means (Companion › Prompts › *Who is talking*), and the notes
   on the message carry the labeled block (`MARTLET_VOICES`, background data,
   never instructions): who is speaking now (name or voice tag, every other
   name, whether it is you or heard for the first time) and anyone else heard. The
   block is noted only when who is talking changed since the last one in the
   conversation sent, and holds until the next. What Memory reads starts with
   `[name]`, so remembered facts know who said what: a fact remembered from
   the message belongs to the speaker (or to another voice heard that it is
   about), recall puts the speaker's facts first, and each recalled fact says
   whose it is ([Whose memories](MEMORY.md#whose-memories)). The talk window labels
   the message with the speaker's name.
4. **Learning names.** After a completed reply, if a voice in it has no name
   yet, the words suggest a name came up ("my name is", "call me", "wrong
   name", "thanks, Sam"...; greeting the companion by its own name doesn't
   count) or someone says two voices are them ("that was me", "it's me, Sam"),
   the exchange is sent once more to the same Thinking model in one extra
   text-only request, the same one as [remembering](MEMORY.md) when both are
   due. The request lists the companion's own names, every name each heard
   voice goes by and, only when someone says they are the same person as
   someone Martlet knows, up to eight other named voices. It answers at most
   six lines:
   - `NAME V3: Sam`: a name the voice goes by. A voice collects every name it
     goes by (up to 40, names you typed first), each with a use count; the most used one is shown
     until you type a name yourself.
   - `CALL V3: Sammy`: the name they ask to be called from now on; it becomes
     the learned name shown (a name you typed still wins).
   - `NOT V3: Jane`: a name it learned that they say isn't theirs; it is
     dropped. Names you typed are never dropped this way.
   - `SAME V9: V3`: they said both voices are them. The two merge like
     **Merge** on People (it can't be undone), into your own voice, else a
     named one, else the one heard most; at most one merge per exchange, at
     least one of the voices must be in the message, and never two voices you
     named differently.

   Names go only to listed voices heard in the message, and only real names
   (at most three words, not "Voice N" or a placeholder such as "no name yet",
   "unknown" or "anonymous"; one learned by mistake before this check is
   dropped like the companion's own names below). **The companion's own names never
   become a voice's**: "Martlet", every persona's name and each word of it
   ("Jane" and "Doe" for *Jane Doe*), a name a persona's text gives it ("You
   are Jane", "Your name is Jane") and a name Martlet's reply gives itself
   ("I'm Jane"). The people talking to it are almost never called that, so
   someone saying "Hey Jane" is talking to Martlet. A voice that learned one
   of these by mistake (before this check) drops it whenever Martlet reads its
   settings (when it starts and after you change a persona) and when the voice
   is heard; a name you typed yourself on People is kept. The talk window notes what was
   learned, dropped or merged, and the log says why lines were left out
   (never the names).

Recognition waits at most 3 seconds beyond speech-to-text; a slow or failed
recognition only means nobody is named for that message. The utterance's
samples are a private copy that is cleared when recognition ends; the only audio
kept is a voice's clips while you haven't named it (below). `voices.json` holds
only voiceprints (256 numbers per sample) and names.

## Clips of voices you haven't named

So you can tell who an unknown voice is, Martlet keeps what it said the last 5
times it was recognized (or added), each at most 8 seconds: the stretch where
that person spoke, or the whole utterance when it was too short to split. They
are 16 kHz mono WAV files in `voice-clips\<voice ID>\` in Martlet's data
folder, on this PC only: never synced to your other computers or hosts, never
uploaded. The sixth clip replaces the oldest. Copies are taken while
recognition runs and written in the background, so replies never wait for them.

A voice keeps clips until you say who it is: type a name for it (or confirm a
learned one), tick **This is me**, merge it into a voice you named, or forget
it, and its clips are deleted. A name only learned in conversation keeps the
clips, so you can check it. Merging moves the clips to the kept voice.
Unticking **Keep the last 5 clips** deletes every clip and keeps no more.

## Sharing between your computers

The list is part of [one Martlet on all your computers](CLUSTER.md), through your paired
Martlet hosts. People are always shared in the household (see
[ACCOUNTS](ACCOUNTS.md)), so it syncs even while **Keep Martlet the same on all
my computers** is off:

- Each host keeps a copy in `voices.json` beside `host.json` (0600, gateway
  service owner; not part of the approved configuration) and serves
  `GET /martlet/v1/voices` and `POST /martlet/v1/voices` (merge a copy in,
  return the merged result) to paired devices over the pinned, signed
  connection. Hosts never use the list.
- While Martlet runs and this PC has paired hosts of your own, every 30 seconds
  and a few seconds after any change, the desktop reads each paired host's
  copy, merges it into its own and posts the merged list to every host whose
  copy differs. There is no switch for it: **Keep Martlet the same on all my
  computers** (Devices › Settings for all devices) does not change it. Hosts a
  friend shares with this PC are never used for it.
- Each voice is a last-writer-wins entry stamped with the same hybrid revision
  as the cluster plan; forgetting and merging leave tombstones so a voice does
  not come back from an older copy. The merge is commutative, associative and
  idempotent. At most 64 voices and 64 tombstones, 1 MiB.
- So when another computer becomes your companion PC, it already knows the
  same people and their names. Sync runs whichever role this PC has.
- Whether Martlet recognizes voices at all (`voice-recognition.txt`) and Voice
  ID with the owner's voiceprint (`voice-id.json`) are
  [shared settings](CLUSTER.md#one-martlet-on-every-computer): they are the
  same on every computer only while **Keep Martlet the same on all my
  computers** is on.

Hosts older than this version answer that they don't know the list; the
People page names them and suggests **Update host**. Copies already on your
hosts stay there until you forget the voices (which syncs tombstones) or remove
the host. Before people were always shared, turning the switch off stopped all
voice sync. Older Martlet versions had their own *Sync across my computers*
switch (`voice-sharing.txt`); it is no longer read.

## Parakeet on this PC (Listening)

Companion › Listening › This PC offers **Parakeet in Martlet** next to
whisper: NVIDIA Parakeet runs inside Martlet on the processor through the same
sherpa-onnx runtime that ships with Martlet, with no Docker and no host
service. It offers three models (`ParakeetModels` in Martlet.Sherpa); each
downloads once, on request, after one confirmation that names it and its size:

| Model (route `ModelId`) | What it is for | Download | Memory |
| --- | --- | --- | --- |
| **Parakeet TDT 110M** (`parakeet-tdt-110m-en`, English) | *Fastest in English*: about 0.1 s for a short turn, the default for an English Windows display language. Less accurate with noise or a distant microphone | 477 MB (fp32 ONNX) | about 0.6 GB |
| **Parakeet TDT 0.6B v2** (`parakeet-tdt-0.6b-v2-int8`, English) | *Most accurate in English (about 0.2 s slower)*: holds up best in noisy rooms and on distant microphones | 661 MB (int8 ONNX) | about 0.9 GB |
| **Parakeet TDT 0.6B v3** (`parakeet-tdt-0.6b-v3-int8`) | *25 languages*: bg, cs, da, de, el, en, es, et, fi, fr, hr, hu, it, lt, lv, mt, nl, pl, pt, ro, ru, sk, sl, sv, uk, detected by itself; the default when Windows' display language isn't English | 670 MB (int8 ONNX) | about 0.9 GB |

The card marks the recommended model (110M for English, v3 otherwise) and the
one in use. Martlet downloads from Hugging Face at a pinned revision into
`speech\models\<model ID>\` in Martlet's data folder, checks each file's exact
size and SHA-256 (a file that doesn't match is deleted), writes the model's
NOTICE beside it (`speech\models\Parakeet-TDT-110M-NOTICE.txt`,
`Parakeet-TDT-0.6B-v2-NOTICE.txt`, `Parakeet-NOTICE.txt` for v3) and switches
Listening to the `LocalParakeet` route (`local-parakeet`) with that model.
Choosing a model that is already downloaded switches at once. One model is
loaded at a time; switching unloads the other. A Listening route saved before
these choices keeps v3: Martlet never changes a model you chose.

Why these three (measured with sherpa-onnx 1.13.8 on this PC's processor; see
[voice latency](VOICE_LATENCY.md#local-options-measured-voicebench)): 110M took 86-140 ms
per short turn and brought a cascade into Gemma 4 E2B to its first audio in
734 ms against 931 ms with v3, but misheard 11% of the words on a desk
microphone and 14% on AMI's headsets; v2 misheard 1.2% and 8.6% (20% on AMI's
room microphone) in 208-420 ms; v3 took 234-454 ms with 10.7% on the desk
microphone. Speed comes first for the default, so English starts with 110M.

The utterance is transcribed in memory on this PC with the same one-use audio
authorization as the other routes, bound to `local://windows` and the model;
nothing is sent anywhere and there is no charge. With Parakeet as Listening,
talking over Martlet is checked with the same model (see
[Conversation](CONVERSATION.md)). *Keep Martlet the same on all my computers*
shares the chosen model with your other computers; one that hasn't downloaded
it keeps listening as before and *Settings for all devices* says which model
to download in Companion › Listening (nothing downloads by itself), and a
model a newer Martlet added waits for the update.

### When Listening's own choice can't hear you

When Listening uses a paired host (whisper or Parakeet on another computer) or
OpenAI, and that route fails for an utterance, Parakeet on this PC hears the
same utterance on the processor, so the turn goes on. Examples: the host is off
or asleep, its service is down, the OpenAI key was removed, or the route took
too long. The audio is still in memory. Nothing is sent anywhere and nothing
costs money. A route that answers is never held up: the stand-in runs only
after the route failed (`LiveConversationController.TranscribeAsync`, with a
second, local-only permission from `ConversationAuthorization.AuthorizeStandIn`).

- **Which model** (`LocalSpeechSetup.ListeningStandIn`): a Parakeet model that
  is downloaded on this PC. The model already loaded comes first when it fits.
  For an English Windows display language the next is the fastest one
  downloaded (110M, then v2, then v3). For one of v3's 25 languages only v3
  fits. For any other display language no Parakeet stands in, because none
  hears it.
- **Nothing downloads by itself.** The Listening *Now* card says what stands in
  (*If gpu-pc can't hear you, Parakeet TDT 110M (English) hears you on this
  PC's processor instead.*). When nothing does, it offers **Download Parakeet
  TDT 110M (477 MB)** (v3 for other languages). That button downloads the
  model after one confirmation and does not change Listening. The model loads
  the first time the route fails, not before.
- **Speed.** The first stand-in loads the model: about 1-2 s. After that, a
  short turn takes about 50-100 ms. For 60 s after the route failed
  (`StandInFor`), the next turns go straight to Parakeet. They don't wait for
  the route to fail again, because a computer that is off takes up to 5 s to
  time out. After that the route is asked again. Once it answers, turns go
  back to it. Measured through MCP on this repository's development PC: the
  first turn's speech-to-text took 1,244 ms (the model loaded), the next turn's
  53 ms, with first audio 517 ms after the end of speech.
- **What you see.** Home keeps *The last transcription failed* until the route
  answers again, and it says that Parakeet heard you instead. While a
  Listening host doesn't answer, Home and Devices say that listening works in
  a reduced way (Parakeet hears you meanwhile), not that Martlet can't hear
  you. The desktop log says *Transcription failed (outcome ...; Parakeet
  &lt;model&gt; on this PC heard it instead in N ms)* when the route failed,
  and *Listening: Parakeet &lt;model&gt; on this PC heard it instead in N ms,
  without asking Listening's own route ...* while the route is skipped. The
  reply latency line names *speech-to-text &lt;model&gt; on this PC, standing
  in for &lt;route model&gt;*.
- **Not covered.** Discord voice and filling in a recording's words still use
  only Listening's own choice when it is a paired host.

## Limits

Voice recognition is a convenience, not authentication: recordings, similar
voices, colds, distance and a new microphone can fool it or split one person
into two voices (merge them). Scores are cosine similarities, not calibrated
probabilities, and the thresholds are AudioTranscriber's conservative
defaults. Voice ID (Companion › Listening) is a separate, bundled filter that
discards other voices before upload; People recognizes everyone after it.

## Qualification

Real native inference ran locally on Windows x64 with the pinned downloads:
two synthetic (Windows SAPI) speakers were added as two voices, a second
utterance matched its speaker at 0.93, a 13 s utterance with both speakers was
split and both were recognized, and Parakeet transcribed both speakers'
sentences. Through Martlet MCP on a disposable data directory (with that real
voice list), the People and Listening pages showed their status, a voice was
renamed and merged, and `voices_status` reported the changed counts. Unit tests
cover the voice list merge rules, naming and the gateway endpoint. Live
microphone conversations, real people, rooms, many speakers, the name-learning
request against a real Thinking model and multi-computer sync on real hosts are
**NOT RUN** (no audio capture or provider calls in agent verification, and no
second paired host here).

Since voice recognition became part of Martlet, MCP's `voices_engine_check`
loaded the runtime and models from the Desktop build's own folder (as the
desktop does) and, on three synthetic SAPI recordings, scored the same speaker
at 0.94 and the other speaker at 0.38-0.42. On a fresh disposable data
directory People showed recognition on with nothing to download, and turning
it off and on was reflected by `voices_status`.

`voices_engine_check` runs the bundled engine on any 16 kHz mono PCM16 WAV
files you give it. To run the native tests, point `MARTLET_SPEECH_FIXTURES` at
a folder with `a1.wav`, `a2.wav` (one speaker) and `b1.wav` (another, saying
"sister"), 16 kHz mono PCM16, and for Parakeet `MARTLET_SPEECH_ROOT` at a
speech folder with one or more Parakeet models downloaded, then run
`LocalSpeechNativeTests`: it transcribes `b1.wav` with each downloaded model in
turn through the desktop's listener, which unloads one before loading the
next. They use the runtime and models the build places beside the tests, skip
otherwise and never download. MCP's `parakeet_check` loads every downloaded
model through the production engine and transcribes Windows-voice phrases with
it, and `voices_status` lists the models, which are downloaded and which one
Listening uses.

When the three Parakeet models were added, the 110M and v2 models were
downloaded through Companion › Listening on a disposable data directory (MCP
clicked *Download and use* and confirmed; each file matched its pin), Listening
switched to each and back without another download, `parakeet_check` ran all
three on four phrases (no word errors from v2 and v3, one from 110M, which
wrote "to morrow"; 110M transcribed about 2.5 times faster), and
`utterance_filter_check` passed with each model as Listening's.
