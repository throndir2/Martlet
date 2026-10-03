# Recognizing people by voice, and Parakeet

Martlet can tell the people around its microphone apart, learn the names they
go by from the conversation, and keep that list the same on every computer you
own. It reuses AudioTranscriber's speaker recognition: sherpa-onnx with the
pyannote segmentation 3.0 and WeSpeaker ResNet34-LM models. The same engine
also runs **Parakeet**, AudioTranscriber's default speech-to-text, as a
Listening choice on this PC.

Both are optional and off until you ask for them. Nothing is downloaded,
recorded or uploaded for them by itself. Voices are recognized and learned only
from the microphone, never from [what the PC plays](CONVERSATION.md#hearing-what-this-pc-plays).

## Companion › People

- **Recognize who is talking.** *Download and turn on* asks once, then downloads
  sherpa-onnx 1.13.8 with ONNX Runtime (from the official NuGet runtime package)
  and the two voice models, about 41 MB, into `speech\` in Martlet's data
  folder. Every file is checked against a pinned size and SHA-256; a file that
  doesn't match is deleted. The checkbox turns recognition on and off
  (`voice-recognition.txt`).
- **On all my computers.** On by default (`voice-sharing.txt`). See
  [sharing](#sharing-between-your-computers).
- **Voices Martlet knows.** Every voice it has heard, the owner's first, then
  named ones, then the most recently heard. For each voice you can:
  - type its **Name** (always wins) and **Also called** (other names,
    comma-separated; removing one here drops it). There is no Save button:
    names save when you leave the field or press Enter (or two seconds after
    you stop typing), then sync to your other computers, so a half-typed name
    isn't shared;
  - tick **This is my voice**;
  - **Merge** it into another entry that is the same person (the names,
    voiceprints and counts combine; it can't be split again);
  - **Forget this voice** (its voiceprint and names are deleted).
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
3. **Telling the Thinking model.** The reply's instructions get one labeled
   block (`MARTLET_VOICES`, background data, never instructions): who is
   speaking now (name or voice tag, other names, whether it is you or heard for
   the first time) and anyone else heard. Earlier messages in the short
   conversation history start with `[name]`, and so does what Memory reads, so
   replies and remembered facts know who said what. The talk window labels the
   message with the speaker's name.
4. **Learning names.** After a completed reply, if a voice in it has no name
   yet, or the words suggest a name came up ("my name is", "call me",
   "thanks, Sam"...), the exchange is sent once more to the same Thinking model
   in one extra text-only request (like remembering). It answers
   `NAME V3: Sam` lines; only listed voices, real names (at most three words,
   not Martlet's or the persona's name, not "Voice N") are accepted. Each name
   is added to that voice with a use count, so a voice collects every name it
   goes by; the most used one is shown until you type a name yourself. The
   talk window notes what was learned.

Recognition waits at most 3 seconds beyond speech-to-text; a slow or failed
recognition only means nobody is named for that message. Audio is never kept:
the utterance's samples are a private copy that is cleared when recognition
ends. `voices.json` holds only voiceprints (256 numbers per sample) and names.

## Sharing between your computers

The list is shared like [who does what](CLUSTER.md), through your paired
Martlet hosts:

- Each host keeps a copy in `voices.json` beside `host.json` (0600, gateway
  service owner; not part of the approved configuration) and serves
  `GET /martlet/v1/voices` and `POST /martlet/v1/voices` (merge a copy in,
  return the merged result) to paired devices over the pinned, signed
  connection. Hosts never use the list.
- While Martlet runs with sharing on, every 30 seconds and a few seconds after
  any change, the desktop reads each paired host's copy, merges it into its
  own and posts the merged list to every host whose copy differs.
- Each voice is a last-writer-wins entry stamped with the same hybrid revision
  as the cluster plan; forgetting and merging leave tombstones so a voice does
  not come back from an older copy. The merge is commutative, associative and
  idempotent. At most 64 voices and 64 tombstones, 1 MiB.
- So when another computer becomes your companion PC, it already knows the
  same people and their names. Sync runs whichever role this PC has, and works
  without the who-does-what sync.

Hosts older than this version answer that they don't know the list; the
People page names them and suggests **Update host**. Turning sharing off stops
all voice sync; copies already on your hosts stay there until you forget the
voices (which syncs tombstones) or remove the host.

## Parakeet on this PC (Listening)

Companion › Listening › This PC now offers **Parakeet in Martlet** next to
whisper. NVIDIA Parakeet TDT 0.6B v3 (int8 ONNX export) runs inside Martlet on
the processor through the same sherpa-onnx runtime: no Docker, no host
service. *Download and use Parakeet* asks once, downloads about 670 MB (plus
the 9 MB runtime if voice recognition hasn't downloaded it yet) from Hugging
Face at a pinned revision, checks each file and switches Listening to the
`LocalParakeet` route (`local-parakeet`, model `parakeet-tdt-0.6b-v3-int8`).
It needs about 1 GB of memory while Martlet runs.

AudioTranscriber measured it on 48 public English clips: 6.85% word error rate
against 10.56% for whisper large-v3-turbo, 14x faster than real time on two
threads. It detects 25 European languages by itself (bg, cs, da, de, el, en,
es, et, fi, fr, hr, hu, it, lt, lv, mt, nl, pl, pt, ro, ru, sk, sl, sv, uk);
use whisper for others. The utterance is transcribed in memory on this PC with
the same one-use audio authorization as the other routes, bound to
`local://windows` and the model; nothing is sent anywhere and there is no
charge. Locally, a 6-7 s utterance transcribed in about 0.8 s on an
i7-13700K.

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

To run the native tests, point `MARTLET_SPEECH_ROOT` at a speech folder with
the runtime, voice models and Parakeet installed and `MARTLET_SPEECH_FIXTURES`
at a folder with `a1.wav`, `a2.wav` (one speaker) and `b1.wav` (another),
16 kHz mono PCM16, then run `LocalSpeechNativeTests`. They skip otherwise and
never download.
