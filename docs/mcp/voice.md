# MCP: Voice and listening

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Voice and listening* area of `martlet_guide` (voices, listening, voice recognition, singing, echo and the talk window). The [index](../MCP.md#pages) lists the other pages.

## Local MCP control (Windows)

`voices_status` reads [voice recognition and Parakeet](../VOICES.md) state from a data
directory (optional absolute `dataDirectory`, default the current user's): the
recognition choice (`on (default)` until it is turned off; a shared setting) and
`sharing` (people are always shared: the voice list syncs with every paired
host of yours whatever *Keep Martlet the same on all my computers* says;
`state` *on*, *no paired hosts yet* or *hosts unreadable*, `always: true`,
`hosts` from `hosts.json` and `friendHostsNeverUsed`, the hosts a friend shares,
which never get the list),
whether a Martlet folder (optional absolute `martletDirectory`, default the
installed release's `Desktop` folder; `Invoke-MartletMcp.ps1` passes this
checkout's Desktop build when it exists) includes the sherpa-onnx runtime and
the voice models (`included`: `found`, `runtime`, `voiceModels`), whether any
Parakeet model is downloaded (`parakeet`), `parakeetModels` (Companion ›
Listening › *Parakeet in Martlet*: `listening`, the Listening route type with
its `parakeetModel`, whether this Martlet `known`s it and whether it is
`downloaded`; `standIn`, the Parakeet model that hears an utterance on this
PC's processor when Listening's own route (a paired host or OpenAI) fails, as
the desktop chooses it (`model`, `name`), or why none does (`reason`:
*Listening isn't set up*, *Listening already runs on this PC*, *no Parakeet
model hears Windows' display language (ja-JP)* or *parakeet-tdt-110m-en isn't
downloaded in speechDirectory*); `displayLanguage` and the `recommended` model for it,
`parakeet-tdt-110m-en` for English and `parakeet-tdt-0.6b-v3-int8` otherwise;
and `models`, each with `id`, `name`, `languages`, `englishOnly`,
`downloadMb`, `revision`, `downloaded`, `notice` (its NOTICE file is there),
`recommended` and `inUse`), and counts from `voices.json` (voices, named, owner
(the legacy *This is me* flag, kept for older desktops), `linked`: voices linked
to a person's account, `accounts`: how many accounts they link to, never which,
`yours`: how many link to the account signed in on this PC (`accounts\session.json`;
null before accounts), with
learned names, `withCompanionName`: voices that learned one of the companion's
own names from the saved personas, which Martlet drops when it next hears them,
`withPlaceholderName`: voices that learned a placeholder such as "no name
yet", dropped the same way, `mostNames`: the most names one voice has, merged,
tombstones) and `clips` (`keep`: People's *Keep the last 20 clips* choice,
`voices` and `clips`: how many clips of voices not named yet are kept). It never
returns names, voiceprints or audio and runs no model.

`voices_status`'s `recognitionWhere` is Companion › People › *Where it runs*
([where voice recognition runs](../VOICES.md#where-voice-recognition-runs)): the
area facts (`area`, `page`, `file` *pools.json*, `shared` and `required` true,
`whenEmpty` *This PC's processor*, `hostRole` and `lane` *voice-recognition*),
`list` (*saved* or *none saved: this PC's processor*), its `members` (`key`,
`name`, `kind`, `on`, `recognizes`, `onlyFor`, `paired`), `tries` (the places
this PC tries in order, each `key`, `hostId` and `fallback`: this PC's
processor added last because the list doesn't have it on), a `summary` line and
`desktop`: the running desktop's `voice-recognition-state.json` (null before
the desktop wrote one). `desktop` has `enabled`, `problem` (People's problem
line, or null), `engine` (`state` *notloaded*, *ready* or *failed*, `loaded`,
`problem`, `failures` in a row, `failed_at`, `retry_at`, `worked_at`,
`reloads` and `ms_per_second`, this PC's measured speed), `last` (the last
recognition's `key`, `where`, `milliseconds`, `seconds` of speech, `raced`,
`passed`: the places passed over and why, and `at`), `hosts` (each computer
tried since the desktop started: `host_id`, `answered_at`, `milliseconds`,
`problem` and `rest_until`) and `tries`.

`voice_recognition_check` rehearses the Voice recognition list with the
production code (`VoiceRecognitionPool`, `VoiceRecognitionRouter`,
`VoiceEngineKeeper`, `SpeakerAnswers` and the host request
`VoiceRecognitionPayload`) and SIMULATED computers and engine, NOT real hosts.
Its `steps` (each `passed` with `detail`; `ok` is all of them) check the order
(each computer once, this PC's processor always last as the fallback, also with
no list, an empty list or nothing on), the list shared by two simulated
computers, the request (`operation`, `sample_rate` 16000, `pcm16_base64`), the
answer checks (a good answer is read; 255 numbers, a voiceprint not of length 1,
a time after the audio, fewer voices than speakers or an error answer are
refused), routing (a computer that answers in time recognizes and this PC does
nothing; a busy one passes it to this PC at once; a bad answer, an unreachable
computer or a slow one is passed over and rests, and a slow one loses to this
PC after its head start) and keeping the engine running (one failure: loaded
again at once; repeated failures: it rests, tries again in 5 s, a computer after
this PC recognizes, the failure is reported while it rests, and the check
brings it back). With an optional absolute `martletDirectory`, `real` runs that
folder's engine through the keeper: `loadMs`, `analyzeMs` for 3 s of a tone,
`routedMs` (the same through the router with this PC alone, the default list),
`msPerSecond` and `headStartMs`. Nothing is recorded, played or sent.

`voice_recognition_engine_check` (optional `endpoint`, default
`http://127.0.0.1:50091/`, and absolute `martletDirectory`) runs Voice
recognition on another computer through a REAL `voice-recognition` host role's
service ([Voice recognition host](../VOICE_RECOGNITION_HOST.md)) on a numeric
loopback address. Two Windows voices (one female, one male; System.Speech
rendered to memory, never played) speak alone, in turns, over each other for
2 s and for 1.4 s only. Martlet.NodeLinkCheck's `voice-recognition-engine` mode
sends each clip through the production path: the role's relay
(`Martlet.Gateway.VoiceRecognition`) inside a real gateway on 127.0.0.1 (pinned
TLS, pairing) and the desktop's paired client (`HostVoiceRecognition`), which
reads every answer with `SpeakerAnswers` as People's list does. It returns
`ok`, the service's own `/status` (`service`), the route's `model` and
`modelRevision`, the role's `state` and `provider` (`cuda` or `cpu`) through the
gateway, `readyMs` (how long until it said `ready`) and per clip (`clips`) the
`voices`, the recognized `speakers` with `cleanSeconds`, `start` and `end`,
`overlap`, `speechSeconds`, whether a `wholeVoiceprint` and a `voiceprint` came
back, and `analyzeMs`, `voiceprintMs` and the host's own `hostMs`. With a
`martletDirectory` (a Martlet folder with its `voice-recognition` files) it
also runs this PC's engine on the same audio: `comparedWithThisPc` has
`matches` (the same voices, speakers in the same order, times within 0.05 s,
voiceprints with a cosine of at least 0.995), `lowestCosine`,
`largestTimeDifference` and `differences`, and `ok` needs `matches`. To check a
role in Docker on this PC, publish a forwarder in the role container's network
namespace to 127.0.0.1:50091 (the role itself listens only on its own
loopback). Nothing is recorded, played or kept.

`voices_naming_check` rehearses [learning names](../VOICES.md#what-happens-in-a-conversation)
with the production checks and changes (`CompanionNames`, `VoiceUpdates` and the
voice list's name rules) on a fixture voice list in memory: V1 is the owner
(typed Robert, learned Bob) and not heard; V2 (typed Sammy, learned Sam and, by
mistake, Jane) and V3 (new, speaking) are heard; the fixture companion is the
persona "Jane Doe" ("You are Jane, ..."). Its `scenarios` (each `passed` with a
`detail`; `passed` is all of them) check that the companion's names, a word of
them and a name Martlet's reply gives itself are refused, a heard voice drops
Jane, a voice learns a name, keeps five names and shows the one it asked for
(CALL), a wrong learned name is dropped (NOT) but a typed one is kept, a voice
not heard gets no name, and SAME merges V3 into the owner's voice once per
exchange. Its SPOKE scenarios take a message whose speaker Martlet wasn't sure
of (only V2 heard for sure; it sounded most like V1): `SPOKE V1` labels it and
leaves the voice list exactly as it was (`spoke-labels-only`: same names,
centroid, samples and heard count), a NAME line for V1 and a second SPOKE line
are refused (`spoke-gives-no-name`), and SPOKE is refused when Martlet knew who
spoke (`spoke-refused-when-known`) or names a voice that couldn't have said it
(`spoke-refused-for-other-voice`). Its people scenarios run the
[People tools](../VOICES.md#people-tools) on the same fixture: `people-list` (names
with typed or learned, the user, heard counts), `people-private` (the first line
holds counts only and no voiceprint is in the answer), `people-find` (a name
finds V2) and `people-lately`. With a `dataDirectory` whose settings have personas,
`companionNames` lists their names (`companionSource`) and one more scenario
refuses a saved persona's name. An optional `answer` (NAME/CALL/NOT/SAME lines
about V1-V3, at most 8192 characters) and `reply` are checked against the same
fixture: `answer.updates`, `refused` (line and reason), `applied` (what the talk
window would say) and the fixture `voices` afterwards. It never reads or writes
the saved voice list and uses no audio, model or network.

`voices_noticing_check` rehearses how People notices a new voice
([voices heard only a little](../VOICES.md#voices-heard-only-a-little)) with the
production voice list matching (`VoiceRoster.Identify`) and the memory of
voices heard only a little (`UnplacedVoices`), on FIXTURE voiceprints (seeded
random numbers, not real voices). Its `scenarios` (each `passed`; `ok` is all
of them): `long` (a new voice with 2 s or more of clean speech is added at
once), `short remarks` (three short remarks within 30 minutes add the voice on
the third), `two remarks` and `too old` (they wait), `different people` (six
people's single remarks never make one voice), `sounds a little like you`
(someone whose every remark is an uncertain match for a known voice is added
once three agree) and `you, far from the microphone` (a known voice's uncertain
remarks never make a new voice). Each has the matches with the owner's voice,
`newVoiceOn` (which utterance made it), the new voice's `utterances`,
`seconds` and score against the owner, and `waiting`. `rules` gives the window,
the minimum utterances and seconds, the agreement, the margin and the
thresholds. It is in memory only: nothing is read, recorded, saved or sent.
`voices_status`'s `recognitionWhere.desktop.unplaced` says how many voiceprints
wait in the running desktop (a count only).

`voices_people` runs the [People tools](../VOICES.md#people-tools) (the `people`
check-in tool set, which learning names uses too) on a data directory's saved
`voices.json`, exactly as a check-in gets them: `tool` is `people_list`
(optional `query`: a name, part of one, or a tag like V3) or `people_lately`
(optional `minutes`, 1-10080, default 60). It returns `state` (`loaded`, `none`
or `unreadable`), `signedIn` (an account is signed in, `accounts\session.json`,
so "the signed-in user" is the voice linked to it; before accounts the voice
marked as the owner's), `isError`, `firstLine` (what a check-in card shows:
counts only), `shown` (how many voices it listed) and `text`, the tool's whole
answer. The answer holds names, so it reads them only from the data directory
you give (the script's disposable one without `dataDirectory`); it never holds
voiceprints, clips or audio and changes nothing.

`parakeet_check` runs Companion › Listening › *Parakeet in Martlet*'s models
the way the desktop does (optional absolute `dataDirectory`; `speechDirectory`,
default the data directory's `speech` folder, where the desktop downloads
them; `martletDirectory` for the sherpa-onnx runtime, which the script fills
with this checkout's Desktop build). It loads each downloaded model (or those
named in `models`) through the production `ParakeetEngine` and transcribes
`phrases` (default four English sentences without numbers or names; up to 8)
said by a Windows voice (System.Speech rendered to memory, never played) after
0.3 s and before 1 s of faint noise. Per model it returns `loadMs`,
`memoryMb` (process memory the model added), each phrase's `transcript`,
`wordErrors`, `words`, `transcribeMs` and `meanProbability`, the
`wordErrorRate` and `medianTranscribeMs`, and `ok` (at most 20% word errors);
`ok` overall needs every model to pass, and `status` is `voices_status`'s
`parakeetModels`. Without the runtime or a downloaded model, `ran` is false
with the reason. Nothing is downloaded, recorded or played, and nothing leaves
this PC.

`voices_engine_check` runs the voice recognition engine that ships in a Martlet
folder (same optional `martletDirectory`) the way the desktop does: it loads the
bundled runtime and both models, takes a voiceprint of a generated test tone
(`loaded`, `voiceprintDimensions`, `loadMs`) and, for optional `wavFiles`
(absolute paths to at most 16 canonical 16 kHz mono PCM16 WAV files of at most a
minute each), reports for each file how many voices were heard, each
recognizable voice's clean seconds, overlap and speech seconds, plus a
`similarity` matrix of the files' main voices (cosine; Martlet treats 0.70 as
the same person). It returns counts and scores only, never audio or
voiceprints, and uses no data directory, device or network. Without the files
it returns `included: false`.

`voice_recording_check` shows what *Add a voice* does with one file: `path`
(required) is an audio or video file on this PC. It runs the production
converter (`VoiceRecordingImport`) and F5's reference rules on the result and
returns `usable`; when usable, `sourceFormat` (the file's extension in capitals,
for example `MP3`; an Ogg file adds its codec, `OGG (Vorbis)` or `OGG (Opus)`),
`sourceChannels`, `sourceSampleRate` (for Opus, the rate it was recorded at, from
its header), `converted` (false
when the file is already a mono 16-bit PCM WAV Martlet keeps byte for byte),
the kept WAV's `sampleRate` (the source rate when it is 16, 22.05, 24, 44.1 or
48 kHz, else the next one above it, at most 48 kHz), `channels` (1),
`bitsPerSample` (16), `durationMs`, `bytes`, `sha256`, `peakDbfs` and `rmsDbfs`
(how loud that WAV, the one **Play** plays, is: about -96 means silence),
`voiceAlone` (whether
it passes F5's reference rules and so can be a voice by itself, with
`voiceAloneProblem` when not), `engines` (the voice
engines that can clone a recording that long) and `shown` (the line
`F5AddVoiceRecording` shows); otherwise `problem` (too long, too short, silent,
missing, unreadable, or for an Ogg file damaged or another codec). Ogg Vorbis
and Ogg Opus are decoded by Martlet itself, everything else by Windows.
Recordings from 0.5 seconds (the shortest one of several
may be) to 30 seconds are usable; a voice from one recording needs at least 1
second. Decoding stops just past 30 seconds. It never returns
the path or audio, saves nothing, plays nothing and contacts nothing.

`voice_tags` shows how a reply's [voice tags](../CONVERSATION.md#voice-tags) are
handled: `text` (required) is a reply, `engine` an engine key (default the
default engine, `chatterbox`; `none` for a voice without tags such as OpenAI or
Windows) and optional `dataDirectory` whose saved prompt edits are used. It
returns the engine, `supportsTags`, its `tags`, the same split into `sounds`
(non-word sounds such as `[laugh]`) and `tones` (tones of voice such as
`[whispering]`), `cues` (each tag's
engine-independent cue, such as `laugh` for `[laugh]`), `synonyms` (each tag's
other words that also count as it, such as `whisper`, `whispers` and `hushed`
for `[whispering]`), `prompt` (the *Voice
sounds and tones* instructions the Thinking model gets, the sounds and the tones
each under a line saying where they go, or null), `spoken` (the
pieces the real speech segmenter hands that engine for a spoken reply, its own
tags kept), `suppressedPieces` and `shown` (the chat and caption text, every
tag stripped). The pieces break where the persona's [speech
breaks](../CONVERSATION.md#voice-latency-streaming-overlap-and-barge-in) allow:
`dataDirectory`'s saved persona named `persona` (else the one Martlet uses,
else the defaults), with any of `breaks`' `periods`,
`questionMarks`, `exclamationMarks` (booleans) and `shortEndingWords` (0-5)
on top; `persona` and `breaks` (with `isDefault`) say what was used. Commas,
semicolons and dashes never break a piece.
With `characterTags` (the character's [emote and motion
tags](../AVATARS.md#emotes-and-motions), such as `["{blush}"]`), those are stripped
too and `characterCues` lists the cues the character acts on: each one's
`piece` (index in `spoken`, or -1 for a tag after the last words), `tag` (a
character tag or the engine's own voice tag) and character `offset` in the
piece. [Other spellings](../CONVERSATION.md#voice-tags) of a tag count as it
(`[nod]` or `*nods*` for `{nod}`, `(sighs)` for `[sigh]`): `acted` lists what the
reply's tags did, in order (`tag` as the character or engine spells it, `kind`
`Character`, `Sound` or `Emotion`, `name`, and `written`, the other spelling
used, or null), and `note` is the line the talk window shows under the reply,
such as *Tone: happy. Sound: laugh. Emotes: nod, blush.* (null without tags).
For example `{"name":"voice_tags","arguments":{"text":"Oh, look at all that
activity! [nod] What are you working on right now?","characterTags":["{nod}"]}}`
returns both sentences in `spoken` (before, the `[` silenced the second and the
chat showed `[nod]`), the `{nod}` cue at the start of the second, `written`
`[nod]` and `note` *Emote: nod.* It synthesizes and contacts nothing.
A `[pass]` the model adds after words (*I didn't hear any music. [pass]*) is
dropped from `spoken` and `shown`, and `quiet` is false: the reply stands. A
reply that is `[pass]`, or starts with it, keeps it in `shown`, says nothing
and has `quiet` true.

`speaking_voices_selftest` (no arguments) rehearses the
[shared speaking voices](../CLUSTER.md#the-shared-speaking-voices) end to end with
the production code: two real gateways on 127.0.0.1 (pinned TLS, the real
reference-voice relay routes of F5-TTS and XTTS-v2 over fixture voice services, NOT AI, and in-memory
`speaking-voices.json` and recordings) and two simulated desktops that keep real
F5 voice stores in a temporary folder and use the desktop's paired client and
Martlet.F5's reconcile engine. It runs `src\Martlet.NodeLinkCheck` (mode
`voices`, `VoiceRehearsal.cs`) and returns `{exitCode, report}` like
`network_selftest`. Its steps: a new list starts with the starter voices
(revision 1) and an own recording joins it, with every recording in the store;
the list and every recording reach a host; speaking there names the recording
by SHA-256 alone and the engine gets the exact recording; a host that has the
list but not the recording gets it once (`reference.missing`, then kept) and the
next reply names it; a new, empty desktop takes every voice from a host (starter
recordings from Martlet, the own one downloaded); a choice made on one desktop
reaches the other; removing a starter voice deletes its recording on both hosts
and the other desktop's copy; a stale copy can't bring it back; a list from an
older Martlet that still lists the retired "anime" voices loses them in the
next reconcile without waiting for their recordings, the host follows and a
stale copy can't bring them back; a list from before a new starter voice
(Jenny) gets it in the next reconcile, once, with its recording from Martlet,
while a starter the owner removed stays removed; speaking with a
removed voice sends the recording, which the host doesn't keep; a host restart
keeps the list and recordings; a wrong SHA-256, a recording no voice has and a
listed recording that isn't a WAV are refused; reading a missing recording
answers none; one voice made from three recordings (24, 16 and 24 kHz) is joined
at 24 kHz after 0.5 s pauses with each recording's place kept, reaches the other
desktop through a host with those places, and speaking with it hands the XTTS-v2
relay's service the three places (`reference.clips`) and F5-TTS the joined
recording only; a list entry whose recordings don't match its recording or
transcript is refused; a voice service that fails a reply (out of graphics
memory, a model that failed to load and answers 503 with its state and why, a
service that stops mid-reply) gives the desktop `worker.failed` or
`worker.unavailable` and the host's own log, read by the desktop as the
Diagnostics page does, says why (the service's error code, stage and summary,
its state and detail, or that its stream ended unfinished); and a reply with a
pause (full-size frames of near-silent audio, whose base64 is full of `+`) is
spoken whole and the voice keeps working afterwards: the host writes base64
unescaped, so ordinary audio never exceeds the route's 16 KiB event limit
(`stream.limit`), which would quarantine the voice until the host restarts; and
when the host's clock steps back 1.3 s (as Windows' time sync does, which a
WSL2/Docker host follows) the voice keeps working with no `auth.clock_invalid`
in the host's log (the host holds its time through a step of up to 30 s), while
a 45 s step still closes that host's authority (`auth.clock_invalid`, also after
the clock is corrected, until the host service restarts). Nothing leaves loopback, the temporary folder is deleted and
Windows Credential Manager is not touched; it does not cover the desktop window
and its sync, the Linux host's files, a real engine, an older host or a real LAN.

`audio2face_check` animates a short synthesized speech-like test signal (a vowel
pulse train generated in the tool, never microphone audio, nothing played) with
an Audio2Face service on a numeric loopback `endpoint` (default
`http://127.0.0.1:52000`; `seconds` 1-10, default 2; `sampleRate` 16000, 24000,
44100 or 48000, default 24000) through the production `Audio2FaceAdapter`, the
client the host gateway's lip-sync relay uses. It works with either engine of the
`audio2face` host role ([local open-source SDK service](../../workers/audio2face/README.md)
or NVIDIA's NIM) and returns `ok`, `frames`, `framesPerSecond`,
`firstFrameSeconds`/`lastFrameSeconds`, `channels` (blendshapes returned) and
`movingChannels`, `jawOpenPeak`, the `strongest` channels with their peaks,
`firstFrameMs` and `elapsedMs`, or `ok: false` with the client's `failure`
category (for example `DeadlineExceeded` or `TransportFailure` when nothing answers, `InvalidProtocol`
for a malformed reply). A role service on a host listens only inside the host's
own loopback, so check it there or through a forward to this PC's loopback.

`voice_engine_check` speaks one sentence with a self-hosted voice engine's
loopback service through the production path: Martlet.NodeLinkCheck's
`voice-engine` mode starts a real gateway on 127.0.0.1 (pinned TLS, pairing)
with that engine's own relay (the one the Linux host creates for the role) and
speaks through the desktop's paired client, with the first starter voice whose
length the engine accepts as the reference. Nothing is played or recorded.
Arguments: `engine` (`chatterbox` default, `chatterbox-original`,
`chatterbox-nano`, `f5`, `xtts`, `gpt-sovits` or `dia`), a numeric loopback
`endpoint` (default the role's port: 50083, 50089, 50088, 50080, 50081, 50082
or 50084), optional `text` (at most 300 characters; default a sentence with the
engine's first sound tag, such as `[laugh]`, when it has tags, or with an
`[expressive]` second sentence for Chatterbox Original) and optional
`dataDirectory`: for `chatterbox-original` the check sends the style saved there
(`chatterbox-style.json`, as Companion › Voice saves it), else Resemble's
suggestions, exactly as the desktop does. It returns `{exitCode, report}` with `ok` (no failure, at least 0.5 s
of audible audio), `engine`, `route`, `voice`, `text`, `style` (Chatterbox
Original's sent style in words, else null), `statusBefore` and
`statusAfter` (the service's own `/status`: `answered`, `state`, `ready`,
`error`, `model` and `device` (`cuda:0` or `cpu`) and `runtime`, for Chatterbox
its torch, torchaudio and CUDA versions, `decoderSteps` (the decoder steps
each decoding takes on Chatterbox Turbo or Nano: 1 on the CPU, 2 on a GPU),
`streaming` (Turbo and Nano: `on`, whether pieces are spoken as they are made;
`hold_tokens`, the most speech tokens each chunk but the last keeps back for
the next decoding: 8 on the CPU, which keeps back fewer when a piece's timing
needs it, and 3 on a GPU; on the CPU also `first_tokens`, the speech tokens
before a piece's first chunk (55, 0 for whole pieces), and what the service
measured to time its chunks: `token_ms`, T3's time for a speech token, and
`decoding_scale`, decoding times against the expected shape; null on a GPU),
`cpu`
on the CPU (`threads`, PyTorch's threads, at most 8 and never more than the
performance cores, and `pinned_cpus`, the CPUs of the performance cores it is
pinned to on native Linux, empty when not pinned, as always on Docker Desktop; null on a GPU)
and `idleCheck` (`checks`, `every_seconds`, `fastest_ms`, `last_ms` of its
[idle check](../CHATTERBOX_VOICE.md#how-it-runs)) and, for Chatterbox
Original, `style` (`default`, `expressive_parts` and `last`, the style the last
reply asked for, so the owner's values can be checked at the service), or why
it could not be read), `seconds` of 24 kHz audio, `firstAudioMs`,
`elapsedMs`, `realTimeFactor`, `pauses` and `pauseMs` (what a listener who
plays the first audio at once would hear: the pauses longer than 20 ms, when
audio arrives after everything before it has played, and all pauses together;
0 for a piece that streams in time or comes whole), `peakDbfs`, `rmsDbfs`, `audible`,
`voicedShare` (the share of the loud 40 ms frames that have a pitch between 70
and 400 Hz, from `Martlet.Core.Audio.Voicing`; about 0.6-0.9 for ordinary
speech and nearly 0 for a whisper, so a `text` that starts with `[whispering]`
shows if the Chatterbox model itself whispered; Martlet adds no
[whisper of its own](../CHATTERBOX_VOICE.md#tags)), and `failure`
and `problem` (the client's error code and message, for example
`worker.unavailable` when nothing answers or the model could not load). A
loading model can take minutes, so the tool allows six; pass
`-TimeoutSeconds 400` to the script. As with `audio2face_check`, a role
service on a host listens only in the host's loopback, so run it there or
forward the port.

`listening_engine_check` is Listening on another computer, headless: the `stt`
host role's live speech-to-text service on a numeric loopback `endpoint`
(default `http://127.0.0.1:8178/`; whisper.cpp or Martlet's
[Parakeet service](../../workers/parakeet/README.md)) transcribes phrases a
Windows voice says (System.Speech rendered to memory after 0.3 s and before 1 s
of faint noise, never played; optional `phrases`, up to 8 English sentences)
through the production path: Martlet.NodeLinkCheck's `listening-engine` mode
starts a real gateway on 127.0.0.1 (pinned TLS, pairing) with the role's relay
(`SttRelayWorker`, the one the Linux host creates) and transcribes through the
desktop's paired client. Optional `model` names the route's model; by default
the one the service's `/status` names (Parakeet's), else `small`. It returns
`ok` (every phrase back with at most 20% word errors overall),
`wordErrorRate`, `medianTranscribeMs`, `endpoint`, `route`, `model`,
`modelRevision` (`sherpa-onnx-1.13.8` for a Parakeet model, `whisper.cpp-1.9.4`
otherwise), `status` (the service's own `/status`: Parakeet's `engine`,
`model`, `threads` and `runtime` versions; whisper.cpp has none), each phrase's
`said`, `transcript`, `wordErrors` and `transcribeMs`, and `failure` and
`problem` (for example `worker.unavailable` when nothing answers). Nothing is
recorded, played or kept. As with `audio2face_check`, run it where the role
listens or forward the port.

`singing_check` makes one song through the singing role's production path
([Singing](../SINGING.md)): Martlet.NodeLinkCheck's `singing-check` mode starts a
real gateway on 127.0.0.1 (pinned TLS, pairing) with the role's own relay
(`SongRelayWorker`, the one the Linux host creates), merges the starter voices
into the gateway's shared speaking-voice list as a paired desktop does, and makes
the song through the desktop's paired client (`MakeSongAsync`, what `SongClient`
runs), sending the voice's recording once when the gateway lacks it. Arguments:
`endpoint` (`fixture`, the default, starts this checkout's `workers/singing`
service with the FIXTURE - NOT AI engine using the `python` on `PATH` or
`MARTLET_PYTHON`; or a numeric loopback address of a live singing service, for
example `http://127.0.0.1:50085/`), `seconds` (15-180; default 20 for the fixture,
30 otherwise), `quality` (`high_quality`, the default as for songs, `fast`, or `xl`: high quality on ACE-Step 1.5 XL, the request's `model` `ace-step-v15-xl`; a service without XL refuses with `singing.model_unavailable`), `model` (another music model: `heartmula`, `diffrhythm` or `yue`, refused the same way where it is not set up; the fixture makes every music model)
and `voiceMatch` (`soulx`, `vevosing`, `vevo2` or `seedvc`; the fixture has the added ones only when `SINGING_VEVO2` or `SINGING_SEEDVC` is `yes` in the environment, as the role choice sets it), `split` (true: make the song as the
desktop's two pools do, a music job with `MakeMusicAsync`, then a voice match
job on its vocals with `MatchVoiceAsync`, the vocals sent in `input` pages, put
together on this PC with `SongVoice`; the report adds `matchJobId` and
`matchHost`, the voice match job's own report, and `separated`: the music's mix sent back in pages to a `separate` job,
as a cloud provider's song is finished, with its `jobId`, `seconds`, `lines`, `words`, `beats`, `separator` and
`sameLength`), and optionally `voiceRecording` (the absolute path of a copy of a
mono 16-bit PCM WAV, 1-30 s, such as one of the owner's voice recordings) with
its `voiceTranscript`, which the check adds to the gateway's voice list as the
desktop adds a recorded voice and sings in instead of the starter voice, and
`bpm` (default 90; 0 for none) and `key` (default `G major`; empty for none),
sent as Martlet's own model writes them (without either, the host's music
planner runs first). It returns `{exitCode, report}` with `ok` (no failure, three
sample-aligned tracks of the requested length and a beat grid), `stages` (each
stage the client saw, with its fraction and `atMs`), `elapsedMs`,
`realTimeFactor`, `song` (`engine` with `Fixture`, `seconds`, `bpm`, `key`,
`beatsPerBar`, `beats` and `downbeats` counts with the first downbeats, `words`
with `wordTimingSource` and the first timed words, `lines` with the first timed
lines and their sections, `stageTimings` from the host plus `Delivering`, and
`mix`/`vocals`/`backing` with sample rate, channels, seconds, peak and RMS),
`host` (what the host measured beyond the contract: `wordTiming` with the median
distance from word starts to vocal onsets before and after snapping,
`lyricTimingSource`, `vocalBleedDb` (how loud the sound removed outside the sung
phrases was, relative to the singing), `peakVramMib`, `plannedBpm`, the engine's
planner, quantization, whether the music model stayed on the card (`dit_resident`)
and is still warm there (`dit_warm_after`), the voice match's own report (`match`:
octave shift, singer and voice pitch, load, pitch and convert seconds, whether
its process was `warm`), and the raw stage timings, with `lyric_timestamps`), `saved` (with
`saveDirectory`, an absolute disposable folder, the check writes `mix.wav`,
`vocals.wav` and `backing.wav` there), `statusBefore`/`statusAfter` (the service's status through the
gateway: state, engine, voice matches, queue, whether its worker holds the
graphics card, the card's used and total memory, and the number, total size and
licences of its models) and `failure`/`problem` (a `SongException` code such as
`voice.missing`, `singing.busy` or `singing.unavailable`, or the gateway's).
Nothing is played. A real song with models still loading can take minutes, so
the tool allows 20; pass `-TimeoutSeconds 1300` to the script. The .NET 10 runtime
must be where `Martlet.NodeLinkCheck.exe` finds it (set `DOTNET_ROOT` when it is
not installed system-wide).
With `dataDirectory` (the absolute path of a desktop data directory paired with
a host, so it has `hosts.json`) `singing_check` goes through that real paired host
instead, exactly as the desktop's `SongClient` does: the first host in
`hosts.json` whose gateway offers the song route (or the one named by `host`), its
own gateway (pinned TLS, signed with the pairing secret the desktop saved in
Windows Credential Manager, used only to sign the requests and never returned),
its singing role, and `voiceId` (a voice ID from that gateway's shared voice list,
or a unique prefix of 8+ characters such as `7fa3706c`; default the list's first
live voice). Nothing is added to the host's voice list. When the host lacks that
voice's recording it is sent once, from `voiceRecording` or the data directory's
voice store (`f5-voices/audio/<preset>/<voice ID>.wav`), checked against the voice's
SHA-256. This is how to make a song with a singing role that runs in Docker on
this PC or another computer, whose service listens only inside its gateway's
network. The report adds `paired`: the host, route and model, the voice (ID,
name, seconds, rights, whether its recording was already on the host) and the
hosts skipped. `endpoint` is ignored then; `voiceId` and `host` need
`dataDirectory`.

`singing_status` reads a singing service's own `/status` over a numeric loopback
`endpoint` (default `http://127.0.0.1:50085/`): `answered`, `state`
(`not_provisioned`, `ready`, `loading`, `busy`), `ready`, `engine` (`song` or
`fixture`), `qualities`, `voiceMatches`, `kinds` (the job kinds it makes:
`song`, `music` and `match` apart, and `separate`), `musicModels` (`ace-step-v15`, and `ace-step-v15-xl` where XL is set up), `queue`, `running`, `workerRunning`
(whether the worker process holds models), `restarts`, `idleReleaseSeconds`,
`gpu` (used and total MiB), `evidence`, `sources` (pinned commits) and `models`
(each pinned file's ID, revision, licence and size) with `modelBytes`, or why it
could not be read. With a `dataDirectory` (the script passes its disposable one)
it adds `choices` from `singing.json`: `olderVoiceMatch` (the voice match the
page chose before each computer had its own; the voice match list is made from
it once), `host` (the computer the desktop last saw running Singing) and `setUp`
(that computer is still paired: what `SongClient.IsSetUp` answers without the
fixture). When that data directory
is paired with hosts (`hosts.json`), it adds `paired` (Martlet.NodeLinkCheck's
`singing-status` mode): for each paired host, read through its own gateway as the
Singing card reads it (the pairing secret from Windows Credential Manager only
signs the requests), `reachable`, `offersSinging`, `model` and `service` (the same
status fields as above plus the models' count, total bytes and licences), or the
`problem`; `pool`, the two singing pools as the desktop's `SongClient` uses them
(`SingingPool.Members`): `configured` (the `singing` area of `pools.json` has a
list), `off` (that list has no member on), `saved`, `order` (the computers a
song's music tries), `members` (each one's `position`, member `key`, `host`,
`plannedSinger` (the shared plan says it runs Singing), `settings`, `quality`, `musicModel`, `cloud` (a cloud member's name, such as `fal.ai (MiniMax Music)`; it takes part only when this PC has its key in `singing-keys.json`)
and `verdict` now: `takes a job now`, `busy: N jobs before a new one` or
`passed over: ...`), `nextSong` (`host`, `ahead`, `waitsInLine`: where the next
song's music goes, `SingingPool.Pick`; null when no member can make it), and
`voiceMatch` (the `voice-match` area: `configured`, `off` (songs keep the music
model's own singer), `order`, `members` with each one's `model`, `matchesWith`
(SoulX-Singer where its VevoSing isn't set up) and `verdict`, and `next`); and
`thisPcDocker`, what this PC's Docker shows of the role:
`containers` (name, image, state, status), `images` (`martlet-singing` tags,
sizes), `modelsVolume` (whether `martlet-singing-models` exists) and
`setupRunning` with `setup` (a `martlet-host add singing` engine session running
now, its image and how long: a setup or *Add VevoSing there* in progress). A role
in Docker listens only inside its gateway's network, so `paired` is how to read
it from this PC. Read-only. As with `voice_engine_check`, a role service on a host
listens only in the host's loopback, so `endpoint` reads it only there or through
a forwarded port.

`singing_cloud_check` runs Singing's cloud clients (`SingingCloudClient`) against loopback fakes of each provider's API
in this process (FIXTURE - NOT AI: no real provider is called and nothing costs money). Its `steps` check ElevenLabs
Music (a `music_v1` composition plan of the lyric sections, 48 kHz PCM), the ElevenLabs Voice Changer (an instant voice
clone made once and found again by name, then speech to speech as MP3, gated to the sung phrases), Mureka (its vocal
clone, generate, poll, line times, a FLAC song), fal.ai MiniMax Music and ACE-Step (the queue and each model's fields),
a rate limit as `singing.busy` and a refused key as `singing.unavailable`, and a cloud song used without separating
(`SongVoice.Unseparated`, lines spread over the singing). It returns `ok`, `fixture` and `steps`. In-process; reads
nothing.

`singing_pool_check` rehearses the two singing pools
([Singing](../SINGING.md#the-two-singing-pools)) with the production code
(`SingingPool` through `WorkQueue`, what `SongClient` runs: a song's music on
the singing pool, then its voice match on the voice match pool) on simulated
singing computers that answer the singing status (state, line, voice
matches, job kinds) and make a job as a host does (a full line refuses with
`singing.busy`; a computer that is off doesn't answer). It returns `ok`,
`fixture` (`simulated singing computers (NOT real hosts or models)`) and
`steps`, each with `name`, `passed` and `detail`: the order without a list
(the computer Martlet sings on, then the plan's singers, then the other
paired computers), a `pools.json` list winning (this PC as its own host
service, a card as its computer; off, unpaired and kept-for-others members
left out; an empty list off), a free first computer taking the job with no
other asked, a busy one passed over at once, the voice match list's own
computers and models (SoulX-Singer unless a member chooses VevoSing; high
quality music unless a member chooses fast), a voice match passing over a
computer whose Martlet can't make it apart (that computer still writes music,
as whole songs), a VevoSing member matching with SoulX-Singer where VevoSing
isn't set up, the music and the voice match put together (`SongVoice`: mix =
backing + matched vocals, scaled together against clipping), computers that
don't answer or don't sing passed over, every computer busy so the job waits
on the shortest line, every line full (`singing.busy`), a failed job not made
again elsewhere, and a computer that stops answering during the job replaced
by the next. In-process; reads nothing.

`hearing_check` shows whether the Thinking model can hear the user's
recording (Companion › Listening › **Let Thinking hear my voice**; optional
absolute `dataDirectory`, default the current user's, and optional
`modelId` to classify instead of the saved Thinking model): `model`,
`source` (`argument`, `settings` or `default`), `settings` (`none`,
`loaded` or `unreadable`), `routeType`, `localOllama`, `modelHearing` (the
name-based `Supported`, `Unsupported` or `Unknown`), `routeHearing` (the saved
route's, the production decision `HearingModelCatalog.ForRoute`: only Chat
Completions endpoints take audio, Ollama on this PC included, then what
`model-abilities.json` says about the model, then its name; null with
`modelId`), `savedAbility` (what Martlet found out about the saved model:
`Hears`, `Sees`, `Source` and `CheckedAt`, or null), `audioRoute` (the audio
model from `sense-models.json`: `file`, `source`, `path` `Thinking`, `Described`
or `None`, `model`, `unknown` and `why`; [Image and audio
models](../SENSE_MODELS.md#recordings-the-audio-model)), `voiceGoes` (`thinking`,
`audioModel` or `transcriptOnly`), `hearVoice` (whether a model hears your
recording as replies decide it: your choice, or, never chosen, on only while
the recording stays on this PC where it goes, Thinking or the audio model of its
own), `hearVoiceChoice` (`on`,
`off` or `unset`; a `false` saved before talk-preferences version 4 counts as
`unset`), `staysOnThisPc` (the recording's destination, the audio model of its
own or else the saved route, is Ollama on this PC,
`http://127.0.0.1:11434/v1`, with a model that isn't a `:cloud` or `-cloud` tag), `hearVoiceWhy`
(in words), `voicePath` (Companion › Listening › **When Thinking
can hear you**: `straight`, the default, or `transcribeFirst`),
`straightApplies` (always listening sends the recording alone right away: the
choice is on, straight, the route hears and no audio model of its own takes
recordings) and `lastTurn`: which way the newest
spoken reply's message went, from the desktop log (`path` `straight`,
`transcribeFirst` or `described` and its *Voice path* `line`; for a straight one
`transcriptReadyAfterReplyStartMs` and `speechToTextMs` from the *Background
transcript ready* line, `wordsLine`, the *Straight to Thinking:* line saying
where the words went: the conversation, its record, remembering; never the
words, and `notWords`: `dropped` when the quick check of something short found
it wasn't words and the reply was dropped before it played, `tooLate` when the
reply had begun, with its `notWordsLine`; for a described one
`descriptionTaken` (the reply took the audio model's words),
`descriptionReadyAfterSpeechMs` and `descriptionLine`, the *Voice
description:* line of words that came late), or null before one. Its `fixture`
rehearses the production Chat Completions adapter against a canned endpoint
on 127.0.0.1 (NOT AI) with a 1.5 s synthesized speech-like clip (never
microphone audio, nothing played): `withRecording` (outcome, the user
message's `contentParts` `text` and `input_audio`, `audioFormat` `wav`,
`wavValid`, `audioSeconds`, `audioBytes`), `withoutAudioPermission`
(`ConsentMissing` with `requestsSent` 0: text permission never covers the
recording) and `transcriptOnly` (the retry without the recording sends a plain
text message); `ok` is true when all three hold. It reads no credentials and
nothing leaves loopback. On the Hearing page (Companion › Hearing, under
Optional extras; Listening links to it with `ListeningOpenHearing`),
`HearingNow` says whether a model hears your voice (*Off. Martlet gets only the
words you say, not how you say them.* or what hears it), `TalkHearVoiceStatus`
(in the Now card) reads whether the saved Thinking model hears and where the
recording goes, or what to change, and `TalkHearVoiceChoice` which applies: *On: you turned it on.*, *Off:
you turned it off...*, *On: your voice stays on this PC...* or *Off until you
tick it: your recording would leave this PC for ...*. With an audio model of its
own, both name it instead (*On: your voice stays on this PC (the audio model
runs here), so the audio model hears it unless you turn this off.*, *Your
recording goes to Ollama on this PC (gemma3n:e4b), the audio model, which
describes how you sound for Thinking; ...*), the check box reads *Let the audio
model hear my voice*, and the straight-path choice and Test hearing are hidden.
The `TalkHearVoice` check
box shows the effective value; ticking or unticking it saves your choice, so it
needs `--allow-ui-effects`. While Thinking hears and the choice is on, the page shows
**When Thinking can hear you**: the radio buttons `TalkVoicePathStraight` and
`TalkVoicePathTranscribeFirst` (`ui_snapshot`'s `selected`; choosing one saves
`talk-preferences.json`, so it needs `--allow-ui-effects`) and
`TalkVoicePathStatus`, which says which way your voice goes and when a message
is transcribed first anyway. A real reply with a recording needs a microphone (or
the simulated one below) and a model that hears; the talk window then notes
*Thinking heard your voice.* (*... straight away.* on the straight path, or that
it got the transcript only) under what you said.

`straight_voice_check` rehearses **Send my voice straight to Thinking**
headless with the production pieces: the conversation runtime and Chat
Completions adapter, and the desktop's own `SpokenWords` (the words of a
recording sent alone, transcribed beside the reply) and
`ConversationContextBuffer` (what each next request carries). Three utterances
said by a Windows voice (never a microphone, nothing played) go one turn at a
time as the recording alone. `straight.turns` gives per turn the request's
`requestHadAudio`, `wavValid`, `audioSeconds`, `requestText` (only the stand-in
*(spoken: listen to the recording)*) and `transcriptInRequest` (false), the
earlier messages it carried (`historyMessages`, `historyRecordings` 0),
`firstWordsMs`, `transcriptReadyAfterReplyStartMs`, `speechToTextMs`, the
`transcript` and word check, `inputTokens`/`cachedTokens`/`cachedShare` and the
`reply`. `history` shows the user messages the conversation now carries (the
transcripts, never the stand-in) and the request after the last turn
(`recordingsInHistory` 0, `standInLeft` false). `transcribeFirst` runs the same
utterances the other way (speech-to-text, then the transcript and recording) and
`compare` gives both medians from the end of the recording to the first words.
`refusal` uses a fixture endpoint that refuses any recording: the reply waits
for the words and asks again with them (`retry.text` is the transcript,
`retry.audio` false, `audioRejected` true). `quickCheck` runs the quick check of
something short (the desktop's, beside the request of what went straight with
less than `belowVoiceMs` of voice) on fixtures (a hum, coughs, *Mmm.*, *Yes,
please.*, *Stop.*): per fixture the voice the production detector measured
(`voicedMs`, `quickCheck` when it is short enough), Parakeet's `transcript` and
`parakeetMs`, whether the production word check counts it as `words`, and
`replyDropped`; `ok` needs every short non-word dropped and no real words ever
dropped. With `live: true`, `contention` sends the same short straight request
(*Yes, please.*, new after the instructions each time) in rounds: alone, with
Parakeet started at the request's start (what the desktop does) and with it
started at the model's first words, giving the median first words, reply end and
when the check finished. Speech-to-text is Parakeet on this
PC when it is downloaded (`speechDirectory`, default the current user's; the
sherpa runtime from `martletDirectory`, which the script fills in with this
checkout's Desktop build), else a fixture transcriber. With `live: true`
Thinking is Ollama on this PC (`model`, default `gemma4:e2b`, which Ollama must
say hears) through a loopback relay that records each request and sends it on as
the desktop does (Thinking steps Off, usage with the prompt cache); otherwise a
fixture endpoint answers (canned replies, NOT AI). `ok` needs every straight
request to carry the recording and only the stand-in, the history to carry the
transcripts and no recordings, the refusal to be answered from the words and the
quick check to be right on every fixture.
Nothing leaves this PC and no credentials are read.

`spoken_reply_check` rehearses a spoken reply whose voice fails partway, end to
end with the production conversation runtime (`ConversationRuntime`, the Chat
Completions adapter, the Martlet host voice stream and the playback sink). A
fixture endpoint on 127.0.0.1 streams a canned four-sentence reply (NOT AI) a
sentence at a time, the way OpenRouter streams; a fixture Martlet host voice (a
quiet tone, NOT AI) fails on the `failAt`-th piece (1-4, default 1) it is asked
to say, as `voiceFailure`: `server` (default; the host's voice worker failed,
`worker.failed`), `unavailable` (it is reloading, `worker.unavailable`),
`stall` (no audio until the voice's time runs out, shortened to a few seconds),
`slow` (every piece slower than real time: half of its audio, a 1.5 s pause,
then the rest, as Chatterbox streams on a busy host's graphics card)
or `none`; `muted` instead has the user mute Martlet's voice (what the
character's *Mute voice* does, `ConversationTurn.MuteVoice`) as the
`failAt`-th piece is asked, and `text-only` sends the reply with no voice at
all (*Speak Martlet's replies aloud* off); `stopped` has the user stop the
reply (Stop, or talking over it: `ConversationTurn.StopAsync`) as the
`failAt`-th piece starts playing; a fixture speaker opens no device and plays nothing. It returns
`reply` (`state`, `failure`, `textComplete`, `fullText`, `characters` of
`servedCharacters`, and the fixture `text`) and `voice` (`stopped`, `why` (the
turn's `SpeechFailure`), `muted` (the turn's `VoiceMuted`), `provider` and `failedJob`, `piecesAsked`,
`piecesSpoken`, `speechLimitReached`, `speakerOpens`, `samplesPlayed`,
`mayHavePlayed`, and `pauses` and `pausedMs`: how often and how long the
speakers ran dry mid-piece waiting for the voice's next audio) and `captions`, what the speech bubble and subtitles were
given (`complete`, `shown`, `spoken`, `unsaid` and each line's `text`, `atMs`
and `spoken`): a line as each piece starts playing and, after the voice
failed, every sentence it couldn't say, one after another for its reading
time (2-20 s). `ok` is true when the reply completed with all of its text,
only the voice stopped, at the chosen piece with the expected provider code
(or, with `none`, every piece was spoken), and the captions together showed
the whole reply. With `muted`, `ok` needs the voice muted at that piece
(`voice.muted`, no `SpeechFailure`, nothing more asked of the voice) and, with
`text-only`, no voice request or speaker at all; both still need the whole
text and captions that show all of it (every line unsaid for `text-only`).
With `stopped`, `ok` needs the reply `Canceled` instead of completed, no voice
failure and the `failAt`-th piece made; the text and the captions stop there.
It also returns `cutOff`, what the conversation does with a reply the user
stopped while Martlet said it (`CutOffReply`, as the talk window does):
`saidAloud` (the turn's `SaidAloud`), `kept` (what the conversation keeps:
only that, ending with the cut-off marker ` —`), `unsaid` (the rest of the
reply's text so far) and `note` (the one-request note with the rest, from
Companion › Prompts › *Cut off: what you hadn't said*, `prompt`), with
`boardSource` and `noteAgeSeconds`. `cutOff.ok` (part of `ok`) needs `kept`
to end with the marker, `saidAloud` to be the start of the reply's words and
`saidAloud` and `unsaid` together to be all of them.
Before the fix this reported `Partial` with only the text up
to the failed sentence, and the captions then showed nothing past the last
spoken piece. With `reply` (up to 1,024 characters of text; line breaks are
allowed) that text
is streamed a word at a time instead, like a model's tokens, and spoken with
speech breaks chosen like `voice_tags`' (`dataDirectory`'s `persona`, else the
one Martlet uses, else the defaults; `breaks` on top); `voice.pieces` lists
exactly what the voice was asked to say, in order, with `voice.persona` and
`voice.breaks`. When the `reply` goes on into prompt text (a line that starts
with `User:` or `Owner:`, `[MARTLET_NOTES]` or a chat template token such as
`<|im_start|>`), the reply stops before it, as a desktop reply does
([Replies stop at prompt text](../CONVERSATION.md#replies-stop-at-prompt-text)):
`promptText` returns `cutAt` (the marker), `streamedCharacters` and
`keptCharacters`, and `promptText.ok` (part of `ok`) needs the marker out of
`reply.text` and `voice.pieces` and the Thinking trace saying so. For example
`"reply":"Sounds fun! Let's play a round tonight.\nUser: okay and what about
tomorrow?"` returned `cutAt` `User:`, the text *Sounds fun! Let's play a round
tonight.* and two spoken pieces. Use `voiceFailure` `none` to hear every piece. With `slow`,
`ok` needs every piece spoken whole, nothing stopped, and the reply latency
line saying *The voice paused N times for X ms in all, waiting for its next
audio.* with at least one pause, together at least about one gap (the gap
less the quarter second of audio sent before it; how long each pause is
depends on how late its audio reached the speakers, so a busy PC shortens
them). Before the
playback fix, a pause that long ended the voice after 1 s
(`PlaybackFailed`, `StreamTruncated`): a paired host making speech slower than
real time cut each reply short. With `paused`, the reply pauses once its first
audio has played (`ConversationTurn.Pause`, as *Pause and decide* does when you
talk over it), stays paused for 1 s and plays on (`Resume`): `ok` also needs
`voice.hold` to show no samples played while paused (`SamplesBefore` equals
`SamplesAfter`), the next piece made by the end of the pause (`MadeAfter` at
least 2: synthesis runs one piece ahead of playback and goes on while paused),
every piece said once (nothing made again: the pieces together say the reply's
words once), `timings` with one
pause and one resume, and the latency line ending *, paused N ms when you
talked over it, then resumed*. `voice.hold` also has `PausedAtMs` and
`ResumedAtMs`, on the same clock as the character cues. It reads no
credentials and nothing leaves loopback. A real
paired host's voice failing is NOT reproduced; the talk window then notes
*The voice failed, so this wasn't spoken.* or *The voice stopped partway, so
only the beginning was spoken.* under the reply, and a reply muted partway
*Muted partway, so only the beginning was spoken.*
With `reasoningMs` (0-5000) the fixture endpoint first streams a hidden
reasoning delta (as OpenRouter streams a reasoning model's thinking) and waits
that long before the words; with `voiceDelayMs` (0-5000) the fixture voice
takes that long to make each piece. `latency` returns the reply's step timings
(`timings`: Thinking request, response headers, first reasoning, first piece,
voice request, first voice audio, first piece made and how much speech it held,
playback start; plus `firstWordsMs` and `firstAudioMs`), the desktop log's
reply latency line for this reply (`line`, counted from sending the message)
and that line read back as `latency_report` reads it (`totalMs`, `steps`,
`stepsSumMs`, `missingSteps`). With `voiceFailure` `none`, `ok` also needs
every step from *Thinking authorization* to *speakers*, the steps adding up to
the total within a millisecond each, *hidden reasoning* at least 80% of
`reasoningMs` and *voice synthesis* at least 80% of `voiceDelayMs`. For
example `{"name":"spoken_reply_check","arguments":{"voiceFailure":"none",
"reasoningMs":600,"voiceDelayMs":400}}` returned *hidden reasoning 590* and
*voice synthesis 469* out of 1,215 ms. Like the desktop, the reply is spoken
with speech breaks (the defaults unless chosen), so each piece waits for the
next few words before it goes to the voice: the canned sentences arrive 300 ms
apart, which adds about that much to *first sentence* here (a real model's
next words come within tens of milliseconds); `"breaks":{"shortEndingWords":0}`
measures without that wait.

`thinkingTrace` returns the reply's
[Thinking trace](../VOICE_LATENCY.md#thinking-trace-each-thinking-request-step-by-step)
lines as the desktop log gets them (`lines`), and `ok` also needs its start, its
first request's line and its end with the state the reply ended in. With
`reasoningMs` of 2000 or more, it also has a *still waiting* line. For example,
`{"voiceFailure":"none","reasoningMs":2500}` returned *still waiting at 2018 ms
for request 1's answer (2010 ms so far; sent at 34 ms, response at 51 ms,
hidden reasoning since 56 ms)*.
With `thinkingSteps` (`off` or `on`) the reply carries Companion › Replies ›
Thinking steps (a loopback server gets the chat template's `enable_thinking`);
with `refuseThinking` the fixture endpoint answers a request carrying it with
HTTP 400 *Reasoning is mandatory...*, as a model that always thinks does.
`thinking` returns `steps`, `refused`, `requests`, `sentControl` (whether each
request carried the control, in order) and `reasoningRejected` (the turn's
snapshot). `ok` then also needs one request with the control when it isn't
refused, none without `thinkingSteps`, and with `refuseThinking` the reply
asked once more without it (`sentControl` `[true, false]`,
`reasoningRejected`) and still completed; before this, a refused Off failed
the reply (or handed it to the Thinking fallback when one was set).
With `chattiness` the reply is offered the chattiness tags the desktop offers
while [Martlet decides how chatty it is](../SCREEN_COMMENTARY.md#martlet-decides-how-chatty-it-is)
(`ConversationRequest.ControlTags`). `chattiness` returns `offered`, `found`
(the tags the reply wrote, in order, from `ConversationTurn.Controls`),
`switchesTo` (the level the last one picks) and `hidden`; `ok` also needs every
tag out of `reply.text`, `voice.pieces` and the captions, and `reply.fullText`
compares with the served text without its tags. For example
`{"voiceFailure":"none","chattiness":true,"reply":"Sure, I will keep it down. Tell me if you need me. [chattiness: quiet]"}`
returned `switchesTo` *quiet*, the two sentences as the only pieces, and the
same `firstWordsMs` (about 2 ms) and `firstAudioMs` (340-470 ms) as the reply
without its tag: a tag at the end never holds back the first words.
With `characterTags` (such as `["{nod}","{blush}"]`) the reply is offered the
desktop character's tags as while the character shows
(`ConversationRequest.CharacterTags`), and the runtime gets a
`CharacterCueFeed`. `character` returns `offered`, `acted` (what the reply's
tags did, from `ConversationTurn.Acted`, as `voice_tags` lists it: `tag`,
`kind`, `name`, `written`), `note` (the line the talk window shows under the
reply), `cues` (each cue the character got: `tag`, `atMs` when its sentence
started playing, `delayMs` into that sentence, and `actedMs` when the
character acted it or `dropped` when the reply stopped first), `dropped` (how
many), `cuesOk`, `stoppedAtMs` (with `stopped`, when the stop came) and
`hidden`. The check waits for each cue through `CharacterCueLine.ReachedAsync`,
as the desktop's character does. `ok` also needs
every [spelling](../CONVERSATION.md#voice-tags) of the tags out of `reply.text`
and `voice.pieces` and, with `voiceFailure` `none`, `slow` or `text-only`, a
cue for every tag acted (`everyCue`) and every cue acted (`cuesOk`). With
`paused`, `cuesOk` needs every cue acted and none while the reply was paused:
a cue that falls in the pause waits for it. With `stopped`, `cuesOk` needs no
cue acted after the stop: the cues still waiting are dropped. For example
`{"voiceFailure":"none","characterTags":["{nod}","{shake_head}","{blush}"],"reply":"Oh, look at all that activity! [nod] What are you working on right now? I bet it's cool *blushes* tell me everything."}`
spoke all three sentences, with `{nod}` (written `[nod]`) at the start of the
second and `{blush}` (written `*blushes*`) 1071 ms into the third, and `note`
*Emotes: nod, blush.* In the desktop the same note shows under the reply in
the talk window (chat text isn't returned by `ui_snapshot`), and `logs_tail`
`contains` `Reply acted` reads *Reply acted: {nod} (written [nod]).* next to
*Character gesture 'nod' played for {nod}.*

For example, `{"voiceFailure":"paused","characterTags":["{wink}"],"reply":"Hey cutie (winks), I like you. Tell me all about your day."}`
paused the reply from 330 to 1349 ms. The wink was due 643 ms into a sentence
that started at 315 ms, inside the pause. It acted at 1989 ms, at the same
point in the speech. With the cue clock's pause and stop taken out, the same
call had the wink act at 1047 ms, during the pause (`cuesOk` false). With
`stopped` and *Hey cutie, I like how you are (winks). Tell me all about your
day.*, the stop came at 372 ms and the wink, due 2071 ms into its sentence, was
`dropped`; without the cue clock it acted at 2446 ms, after the stop. With
`failAt` 2 and *(winks) Hey cutie, I like you. Okay, I see you better now
{wink} so tell me more.*, the first wink acted at once and the second, due
151 ms after the stop, was dropped. In the desktop a dropped cue logs
*Character cue {wink} wasn't acted: the reply stopped before it got there.*

`discord_voice_check` rehearses [Discord](../DISCORD.md) voice without Discord. It
loads `libdave.dll` (Discord's DAVE end-to-end voice encryption, which NetCord
calls) beside `Martlet.Mcp` and in `martletDirectory` (the script passes this
checkout's Desktop build, where it ships) and runs an offline DAVE session
(`natives` and `shipped`: `LibDaveLoaded`, `DaveProtocolVersion`, `DaveSession`,
`KeyPackageBytes` of the MLS key package, `FrameCrypto` for the frame encryptor
and decryptor, `OpusManaged` for the managed Opus codec, `Ok`). Then one
utterance a Windows voice says (rendered to memory, never played) goes through
the production path (`DiscordVoiceConversation`) with a fake transport: 48 kHz
stereo Opus packets as a Discord client sends them, per-speaker ordering,
decoding, 16 kHz downsampling and endpointing, FIXTURE speech-to-text and a
FIXTURE reply engine (NOT AI), and the reply spoken by a Windows voice into
memory and encoded back to Opus. `utterance` has `saidMs`, `heardMs`,
`heardLevel` and the `turn` (`Addressed`, `source` `Voice`, owner); `reply` has
the frames sent, `spokenMs`, their decoded `level` and the Speaking flag turned
on and off; `bargeIn.stopped` says a second person talking over a long reply
stopped it (`framesBeforeStop` of `longReplyFrames`). `ok` is all of these. No
Discord connection, network, microphone, speaker, provider or credential.

The desktop's Companion › Discord `DiscordVoiceStatus` line (in `SafeValues`)
reads like *Voice: in General (My server) · 2 speakers heard, 3 utterances
transcribed, 2 replies spoken · DAVE on · libdave loaded (DAVE v1) · Last
problem: ...* (counts only, never what was said). The live bot logs *Discord
voice: joined a channel (2 people there).*

### ElevenLabs voice
`elevenlabs_check` rehearses [ElevenLabs as the Voice](../ELEVENLABS_VOICE.md)
(the owner's cloned voice with tones) end to end against `ElevenLabsFixture`, a
local stand-in on 127.0.0.1 that follows ElevenLabs' documented protocol.
FIXTURE, NOT ElevenLabs, NOT AI: its voice is a quiet tone, and its first audio
waits 100 ms to stand in for model latency (not a measurement). It runs three
steps:
1. **Cloning.** Instant Voice Cloning with a synthetic WAV. `clone.sent` shows
   the form Martlet sent (`name`, `fileName` `voice.wav`, `contentType`
   `audio/wav`, `wave`, `removeBackgroundNoise` `false`, `description`) and
   whether the key matched.
2. **One piece** straight through `ElevenLabsDialogueClient`. `segment` gives
   its `audioBytes`, any `failure` and `detail`, and the fixture timings
   `connectMs`, `firstAudioMs` and `totalMs`.
3. **A whole spoken reply** through the production conversation runtime. A
   fixture Chat Completions endpoint streams `reply` a word at a time (by
   default one with `[laughs]`, `[whispers]` and `[happy]`), and a fixture
   speaker plays nothing.

`ok` needs all of these:
- `thinkingPrompt.listsElevenLabsTags`: the Thinking prompt lists ElevenLabs'
  own tags (`tags` gives each tag, its kind and its cue).
- `tags.reachedElevenLabs`: every tag the reply wrote reaches ElevenLabs as
  written.
- `tags.chatClean` and `tags.captionsClean`: the chat text and every caption
  show no tag.
- `protocol.ok`: each connection carries `model_id`, `output_format=pcm_24000`,
  the key in the `xi-api-key` header (`keyInHeader`, never `keyInBody`), the
  one cloned voice and `close_socket`.

`scenario` is `reply` (default), `model-refused` or `bad-key`. With
`model-refused`, the WebSocket refuses the model with `param: model_id`, as its
API reference describes. The voice then fails as `ModelUnsupported`, and
`segment.detail` says to choose Eleven v3 Conversational. The text still
completes. With `bad-key`, cloning and speech fail as `Authentication`. `model`
is `eleven_v4_turbo` (default) or `eleven_v3_conversational`. With
`dataDirectory`, `saved` reports the saved ElevenLabs choice: `voiceRoute`,
`usesElevenLabs`, `model`, `enabled`, `confirmed`, `keySaved`, `clonedVoice`,
`requiresVerification` and `keysFromBefore`. It never gives the key, the voice
ID or the voice's name. Nothing is sent to ElevenLabs: `live` is always NOT
RUN, because there is no ElevenLabs account or key.
```powershell
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[
  {"name":"elevenlabs_check"},
  {"name":"elevenlabs_check","arguments":{"scenario":"model-refused"}},
  {"name":"elevenlabs_check","arguments":{"scenario":"bad-key"}}]'
```

On Companion › Voice › *A cloud provider*, *Cloud voice* is an option picker:
`Picker-CloudVoice-openai` (OpenAI's voices) and `Picker-CloudVoice-elevenlabs`.
ElevenLabs' details are readable: its facts (`PickerFact-CloudVoice-<fact>`:
voice cloning, laughs and sighs, emotions, streaming, languages, cost),
`ElevenLabsStatus` (*Not in use yet: ...* or *In use: Eleven v4 Turbo with
your cloned voice; key saved.*, and whether ElevenLabs asked to verify the
voice; never the voice's name), `ElevenLabsKeyStatus` (what the key field will
do; never the key) and `ElevenLabsModel` (the chosen model). Its abilities read
through `VoiceEngineAbilities-elevenlabs`. `ElevenLabsVoice` holds the owner's voice names,
so it is not readable. `ElevenLabsKey` and `ElevenLabsConsent` are inputs. The *?* marks `Help-ElevenLabsAbout`,
`Help-ElevenLabsModel`, `Help-ElevenLabsCosts` and `Help-ElevenLabsConsent` (the rights statement of the box) link to the
wiki's *ElevenLabs* and *What you agree to*.
`ElevenLabsSave` uploads a recording and saves the route, so it needs
`--allow-ui-effects`. Never press it in a check: it would contact ElevenLabs.

`quick_sounds_status` reads Companion › Voice › *Quick sounds while Martlet
thinks* from a data directory (optional absolute `dataDirectory`; the script
gives a disposable one): `on` (off by default) and `delayMs` (500, 700, 1000 or
1500; 700 by default) from `talk-preferences.json` with their `source`,
`cooldownSeconds` (20), the `voice` replies speak with (`words`, whether it is
`paid` (OpenAI's cloud voice) and its `key` per voice and character, or null
without a voice), whether its clips are `ready` (each clip's `text` and
`milliseconds`) or it `needsClick` (a paid voice waits for *Make quick sounds
now*), every set `kept` in `quick-sounds\` (`key`, `voice`, `madeAt`, `clips`)
and the newest desktop `log` lines about quick sounds (*Quick sound: "Mm," 712
ms after the reply was confirmed (...)*, *Quick sounds: made 4 with ...*).
Read-only; it never plays anything.

`voice_sounds_status` reads the touch zones' voice sounds (`sound:<cue>`
entries in a zone's reaction list) from a data directory (optional absolute
`dataDirectory`; the script gives a disposable one): the `voice` replies speak
with (`words`, its `engine`, whether it is `paid` and its `key` per voice and
character, or null), `noSounds` (why no sound plays, when the voice makes none),
each of the voice's `sounds` (`entry`, `cue`, `label`, the engine's `tag`,
whether it is `made` on this PC and its `milliseconds`), every zone in `zones`
that lists sounds (`model`, `zone`, `enabled`, and for each sound its `entry`
and whether it `plays` with this voice), the clips `kept` in `voice-sounds\`,
the `last` sound played or skipped (*Voice sound: laugh for a touch on hair not
played (Martlet is speaking).*) and the newest desktop `log` lines about voice
sounds. Read-only; it never plays anything.

`voice_sounds_check` rehearses voice sounds with the production code and
fixtures (a fixture voice makes a quiet tone, NOT AI; fixture speakers take
audio at a real speaker's pace and play nothing). Scenarios (`scenario` runs
one): `sounds` (the sounds each engine offers, and the words for a voice that
makes none), `rules` (`VoiceSoundGate`: no sound with no voice, *Speak replies
aloud* off, the conversation paused or muted, a sound the voice can't make,
while Martlet speaks, while you talk or over another sound; a clip not made yet
is made instead), `make` (the engine's tag alone through
`ConversationRuntime.SynthesizeAsync`, trimmed and kept), `play`
(`ConversationRuntime.PlayClipAsync` plays the clip whole with lip sync and
refuses a second one meanwhile) and `cut` (a reply's own voice cuts a sound
still playing; `replyFirstAudioMs` must be no later than
`sameReplyWithoutSoundFirstAudioMs` plus 150 ms).

`quick_sounds_check` rehearses quick sounds (see
[Voice latency](../VOICE_LATENCY.md#quick-sounds-while-martlet-thinks)) with the
production rules (`QuickSoundGate`, `QuickSoundWatcher`,
`ConversationTurn.PlayQuickSound`) on fixture turns through the production
conversation runtime, Chat Completions adapter, host voice stream and playback
sink. FIXTURES, NOT AI: a Chat Completions endpoint on 127.0.0.1 answers one
sentence after a set wait, a host voice makes a quiet tone and the speakers
open no device. `scenario` (default all): `slow` (first words after 1.8 s: the
quick sound plays once the reply had no audio for the delay, the whole clip
before the reply, which follows on its own run uncut), `fast` (none: its own
audio was ready), `cooldown` (a second slow reply within 20 s gets none),
`early` (a reply started early is held for 1 s, then taken: counted from when
it was taken), `let-go` (held, then let go: none, nothing played),
`reasoning` (hidden reasoning streams before the words: after 300 ms) and
`paused` (paused because you talked over it: none). `delayMs` is 500, 700,
1000 or 1500 (700 by default). Per turn it returns `played`, `why`, `clip`,
`quickSoundAtMs` (from the turn's start), `afterConfirmedMs`, `releasedAtMs`
(a reply started early), `firstAudioAtMs`, `playbackRuns`,
`replyFollowedUncut` (the clip's run closed before the reply's opened, and it
played whole), `state` and the reply `latencyLine`; `ok` when every scenario
does what it should. Nothing is recorded, played or sent off this PC.

`turn_judge_check` checks always listening's end-of-turn judge (Companion ›
Listening › *Judge when I finish talking*, see
[Voice latency](../VOICE_LATENCY.md#the-end-of-turn-judge)). It loads the Smart
Turn v3.2 model and ONNX Runtime bundled in `martletDirectory` (optional
absolute path; `Invoke-MartletMcp.ps1` uses this checkout's Desktop build)
through the production `SmartTurnEngine`. It judges six phrases a Windows voice
says (three finished, three that trail off; rendered to memory, never played),
each cut 260 ms into the pause that follows, as always listening asks it. It
returns `loadMs`, per phrase `expected`, `verdict`, `probability` and `judgeMs`,
then `agreed` and `medianJudgeMs`. Its `gate` part steps the production
`EndOfTurnGate` with the plain 800 ms pause, frame by frame: a complete answer
ends the turn at 300 ms, an incomplete one waits for 1600 ms, and a slow or
failed judge leaves it to 800 ms (`endedAtMs`, `ok` each). `ok` is a median
judge time of at most 100 ms and every gate case as expected; agreement on a
synthetic voice is informative only. Without the model or the runtime it
returns `ran: false` with the reason. Nothing is recorded, played, downloaded
or sent.

`early_reply_check` rehearses Companion › Listening › *Start replies early*
(see [Voice latency](../VOICE_LATENCY.md#starting-replies-early)) headless, in
real time (20 ms frames), with the production `EndOfTurnGate`,
`EarlyReplyGate`, request comparison (`EarlyAsk`) and the conversation
runtime's held turn (`ConversationRuntime.StartEarly`,
`ConversationTurn.Release`) through the Chat Completions adapter, a paired
host's voice stream and the playback sink. FIXTURES, NOT AI: a quick
transcript after `sttMs` (default 90), a judge answer after `judgeMs` (26), a
Chat Completions endpoint on 127.0.0.1 whose first words come after
`thinkingMs` (200) and stream for 1.5 s, and a host voice whose first audio
comes after `voiceMs` (350); the speakers open no device. `scenario` (default
`all`): `incomplete` (the judge finds the pause unfinished and the 1.6 s pause
ends the turn), `plain` (no judge: the 800 ms pause), `complete` (the judge
ends the turn at about 300 ms), `resumed` (you go on talking 700 ms into the
pause, then pause again) or `changed` (the final transcript differs from the
quick one). Each runs twice, with replies started early (`withEarlyReplies`)
and `without`, and returns per run `decisions` (each with `atMs` from when you
stopped talking), `turnEndedMs`, `firstAudioAfterTurnEndMs`, `startedEarly`,
`cancelled`, `outcome` (`promoted` or `changed`) and `reason`,
`thinkingRequests`, `abortedRequests`, `askedWith`, `letGo` (each reply let
go: its `state`, `mayHavePlayed` and `textCharacters`), `voicePieces`,
`firstVoiceAskedMs`, `playedBeforeTurnEnded` and `shownBeforeTurnEnded` (both
0), `liveFloor` (the production `LiveFloor`, fed as the desktop feeds it:
`atTurnEnd`, its level when the turn ended, `repliesAtTurnEnd` and
`repliesWhenDone`, how many replies held it then and once the reply was done;
its changes are among the `decisions`) and the reply `latencyLine`, plus
`savedMs` per scenario. `ok` when each
scenario does what it should: promoted with one request (`incomplete`,
`plain`), let go once with its request aborted and the second start promoted
(`resumed`), let go and started again with the final words (`changed`),
nothing started or promoted for `complete`; nothing heard or shown before
the turn ended, every reply let go `Canceled` with nothing played, the live
floor `Live` and held by the one reply started early when the turn ended and
by no reply once the reply was done, and for
`incomplete`, `plain` and `resumed` the first audio sooner by at least 60% of
`thinkingMs` + `voiceMs`. Nothing is recorded, played or sent off this PC.

`lip_sync_pool_status` shows lip-sync's pool (optional absolute
`dataDirectory`, default the current user's; the script gives a disposable
one; optional `deviceId`, default this PC's): `avatar` (`loaded`, `none` or
`unreadable`), `lipSync` (the mode in `avatar.json`), `assigned` (the host it
names), `paired` and `ownHost` (`hosts.json`), `area` (the pool contract's
lip-sync area: `id`, `page`, `required`, `kinds`, `whenEmpty`), `list`
(`source`: `pools.json`, or `older choices (not saved yet)`; and each member's
`key`, `name`, `kind`, `off`, `onlyFor` and `settings`), `tries` (the member
keys this PC tries for each chunk with the production `PoolRouting.Order`),
`fallback` ("Voice loudness on this PC" when no member can take a sentence),
`sharingWork` (lip-sync's `shares`, `order`, `never` and the plan's `runs`,
which make the list from the older choices) and `planHost` (the shared plan's
lip-sync host). Host and device IDs only. Read-only.

`lip_sync_pool_check` rehearses the production lip-sync pool
(`LipSyncSharing` on `WorkQueue`) with simulated Audio2Face computers that
animate one chunk at a time and turn another away at once as a host's gateway
does (`job.busy`), **NOT real hosts or models**. Each step reports `passed`
and its detail: shared by default with the assigned computer first and no
Devices card; a free first computer taking a chunk with no wait; two companion
PCs at once, the second moved to the next computer once and each reply kept on
its computer; the next reply choosing again; every computer busy, so a
one-second chunk waits about half a second and is skipped (the voice's
loudness moves the mouth); a computer freeing within the wait; an unanswering
computer passed over at once; a kept or never-used computer left out; a saved
list's order without a member turned off or kept for another companion PC; an
empty list meaning voice loudness; and the list made from the older choices.
`ok` is true when every step passed. In-process; reads nothing.

`songs_status` shows [singing in conversation](../CONVERSATION.md#singing-in-conversation)
(optional absolute `dataDirectory`, default the current user's):
`backgroundWork` (Thinking longer, which the song tools come with), `creations`
(the song creations in the shared Creations library: `count`, and each song's
`id` (its key), `durationSeconds`, `lines`, `words`, `wordsEstimated`,
`wordTimingSource`, `bpm`, `titleCharacters`, `lyricsCharacters`, `generator`,
`converter`, `quality`, `voiceMatch`, `fixture`, `mouthSource` (*Audio2Face*,
*Visemes* or *Loudness*) and `mouthNote`, its `assets` (name, media type, bytes),
whether they are all `here` on this computer, `createdBy` and `createdAt`; never
a title or words), `desktop` (the desktop's
`songs-status.json`: `offered`, `songs`, `output` (*Martlet's voice output*, or
the silent fixture output under `MARTLET_SINGING_FIXTURE=1`), `playing` with
`songId`, `state` (*Starting*, *LeadIn*, *Singing*, *Stopping*...),
`positionSeconds`, `durationSeconds`, `line`, `lines`, `section`, `from`,
`leadInBars`, `leadInSeconds`, `fadeInMs`, `vamps`, `ducked`, `lipSync` (the
mouth track's `source`, `frames` and `channels`, how many mouth frames were
`sent` to the character, their `averageSendMs`, and the `route`: *mapped mouth
shapes* through the character's mouth mapping or *mouth opening*), its `stop` plan
(`musical`, `requestedSeconds`, `vocalsEndSeconds`, `vocalsFadeMs`,
`backingFromSeconds`, `backingFadeMs`, `silentAfterSeconds`) and `failure`;
`lastStop` with `songId`, `atSeconds`, `line`, `lines`, `section`, `nextLine`,
`cause` (*UserWords*, *Button*, *Martlet*, *Ended*, *Failed*), `ended`,
`wordsCharacters` and a button's `reason`; and `noteWaiting`), the song job
`kind` (one at a time, 4 an hour, 15 minutes, `offer`, *Making a song*), the
three `tools` exactly as the Thinking model gets them, the filled Singing
`prompt` and `afterReply` (the Songs, pictures and creations
[check-in tool set](../CONVERSATION.md#check-in-tool-sets): its `set`, its
`tools`, the reply tools it `replaces` and the `replyGuidance` line a reply gets
in their place) and `withoutTools` (singing through reply tags where the
Thinking route carries no tools: the Thinking `routeType`, whether the route
`routeCarriesTools`, whether the model `toolsRejected` them, whether replies
`usesTags`, the three `tags`, the filled *Singing without tools* `prompt` and a
`sample` reply run through the speech filter: what is `shown` and `spoken`, the
`tags` it found, `tagsHidden`, the tool `calls` they stand for with their
arguments and what the conversation `kept`). Read-only.

`song_playback_check` runs the production playback (`SongTransport`,
`SongMixer`, `SongPlayer` pumping a fixture output that plays ten times faster
than real time and keeps what it is given; nothing is played aloud) on the
FIXTURE - NOT AI tone song (40 s, 96 BPM, ten lines in verse, chorus, verse 2 and
chorus 2), or on a song creation (`songId`: its key, with its `dataDirectory`), and measures
what it produced. `resolve`: where `from` points for start, a section, *second
verse*, `line:3`, a time and two misses. `leadIn` (resuming line 4, rendered from
the backing alone and the vocals alone): `entrySeconds` on a downbeat,
`leadInBars`, `fadeInMs`, the backing's gain in its first 10 ms, at half the fade
(equal power: 0.707) and after it, and the vocals' peak before the gate (0) and
level after the onset. `vamp`: Martlet still talking at the line, the band
repeats the bar twice and the vocals are first heard two bars later
(`vocalsFirstHeardAfterSeconds` against `expectedAfterSeconds`). `duck`: -12 dB.
`played`: sung from the top and stopped musically mid-line by the user's words
(`musicalStop`: the stop record's line and section, the word's end, the beat the
band fades from and over how long, the planned and measured silence and the
note), then resumed (`resume`: the line where it stopped, its lead-in, 2 vamps,
the states *LeadIn*, *Singing*, *Stopped*) and stopped with Esc (`quickStop`: a
300 ms fade from where the audio already handed to the output ends). `lipSync`:
the mouth tracks Martlet makes from the vocals stem and how far each opens from
the vocal onsets (`onsets`, `matched`, `medianOffsetMs`, `meanAbsoluteOffsetMs`,
`p90AbsoluteOffsetMs`, `good`): `audio2Face` (run once over the vocals when a
service answers on 127.0.0.1:52000, otherwise *NOT RUN* with why), `visemes`
(from the sung words, `words.estimated` when spread over each line's singing),
`loudness`, a song creation's own `stored` track, which one was `used`, and
`playback`: the player resuming line 4 with its lead-in on the vocals alone,
the mouth it sends on the playback clock (`mouthUpdates`), whether it stayed
`closedDuringLeadIn`, and the offsets between the mouth opening and the onsets of
the vocals it actually played. Each part has an `ok`; on this PC the musical stop
went silent 0.88 s after the request (planned 9.375 s, measured 9.370 s), Esc
0.4 s after it, the viseme track opened +10 ms from all 10 onsets and, played
after a lead-in, within 1-4 ms median (90% within 20 ms).

`echo_check` checks [echo reduction](../CONVERSATION.md#echo-reduction)
(Companion › Listening › **Reduce echo from my speakers**; optional absolute
`dataDirectory`, default the current user's, and optional `delayMs` 0-300,
default 60): `reduceEcho` (the saved choice, on by default) with
`reduceEchoSource` (`saved` or `default`), `bargeIn` (*Let me interrupt Martlet
by talking*, optional and off by default) with `bargeInSource` (`saved`,
`default`, or `reset` for a file saved before barge-in became opt-in that had
it on only by the old default), `wordCheck` (*Word check*: `Relaxed`, `Normal`
by default, or `Sensitive`) with `wordCheckSource` (`saved` or `default`), and
`canceller` (`WebRTC AEC3` once
the native canceller loads, otherwise null with `cancellerProblem` and `ok`
false). Its `rehearsal` runs the production microphone path
(`MicrophoneCapture`, `EchoReducer`, the WebRTC canceller, the capture
normalizer) twice on one synthesized scene, with and without echo reduction,
using a fixture microphone and fixture speaker loopback (48 kHz stereo float
with device-style timestamps and a pause in playback) on a simulated clock: no
microphone or speaker is opened and nothing plays. A synthesized Martlet voice
reaches the microphone through a simulated room (`delayMs`, reflections, about
6 dB down) beside the user's own synthesized voice: 0-4 s only Martlet speaks
(`martletOnly`), 4.5-6 s only the user (`userOnly`), 6-8 s both (`bothTalking`,
barge-in). It returns `state` (the reducer's report, `Active`), `frames`,
`speakerFrames`, `deviceReducedDb` (the device's own measure over every frame
while the speakers played, the user's voice included), and per part the levels
`withoutDb`/`withDb` (dBFS), `martletOnly.reducedDb` and
`firstSecondsReducedDb` (0-1.5 s, while the canceller learns the room),
`userOnly.keptDb` and `bothTalking.userAloneDb`, plus `speechFrames*`: the
20 ms frames Martlet's own voice-activity detector counted as speech. Its
`talkOver` runs the voice gate (`TalkOverDetector`, `requiredMs` 1000 and
`gapMs` 500, with the capture's own `EchoTimeline`, `speakersRemovedDb` 10),
which tells the user's own voice from the speakers' sound (what actually stops
Martlet is real words: `utterance_filter_check`),
over the cleaned recording the way always listening does, for `martletOnly`,
`userOnly`, `shortSound` (the first 0.8 s of the user alone) and
`bothTalking`: `userFrames` and `speakerFrames` (loud 20 ms frames that were
a voice or the speakers' sound), `timeline` (`room`, `user`, `speakers`
10 ms frames) and `talkedOver` with `afterMs`. `ok` is true when the reducer
was active, Martlet's echo got at least 20 dB quieter, the detector heard it
without reduction but not with it, it still heard the user alone (kept within
3 dB) and over Martlet, and `talkOver.ok`: Martlet's echo and the short sound
never talked over it, while the user over Martlet did, no sooner than
`requiredMs`. `listensWhileSpeaking` says whether always listening goes on
while Martlet speaks with the saved choices: `bargeIn`, or `reduceEcho` with
echo reduction that works (`EchoReducer.Works` with the rehearsal's own
timeline; `rehearsal.reducing` is that capture's echo state: `true` while the
canceller cleaned it, `false` once lost, `null` if it never started); `ok`
also requires the rehearsal's echo reduction to work. Without a canceller
`listensWhileSpeaking` is `bargeIn`. It contacts nothing.

`utterance_filter_check` checks [listening for words](../CONVERSATION.md#listening-for-words)
and barge-in (Companion › Listening › **Word check**; optional absolute
`dataDirectory`, default the current user's, `sensitivity` `relaxed`, `normal`
or `sensitive` to override the saved choice, `audio` default true, absolute
`speechDirectory` where Parakeet is downloaded, default the current user's,
`parakeetModel` to choose the model, default Listening's when it is downloaded,
else v3, else the one downloaded,
and absolute `martletDirectory` for the sherpa-onnx runtime, which the script
fills with this checkout's Desktop build). It runs the production
`UtteranceFilter` and `BargeInPolicy` on `samples` (up to 64 of `text` with
optional `voicedMs`, `speechMs` (how long the voice went on; `voicedMs` when
absent), `meanProbability`, `minimumProbability`,
`noSpeechProbability`, `averageLogProbability`, `engine`, `afterQuestion`,
`persona`, `playback` `reply` or `song`, and `expectKeep`/`expectInterrupt`),
by default a fixed set with the outcome Normal must give (fillers, laughter,
sound tags, "Thank you." from noise or said clearly, subtitle credits, lone
words, short answers, stop words, backchannels, too many words for the speech,
"I'm gonna make it public." with little loud voice in a second of speech, a
whisper loop, Martlet's name, a song that only stops when asked, and the
"Yeah." Parakeet and whisper.cpp wrote for coughs on this PC with their
measured evidence; another sensitivity only reports what it makes of them).
Each sample returns `keep`, `kind`, `Reason`, `Words`, `shown` (the talk
window's *Ignored ...* note), `interrupts` and `interruptReason`. It returns
`wordCheck` (the one used) with `savedWordCheck`/`savedWordCheckSource`,
`bargeIn` (the saved choice), `limits` (`UtteranceFilter.For`),
`bargeInPolicy` (`voiceBeforeCheckMs`, `firstRecheckMs`, `recheckMs`,
`pauseMs`, `wordsToInterrupt`) and `filterCost` (`microsecondsPerCall` over
thousands of calls: what the filter adds to a reply). With `audio` and
Parakeet downloaded, `audio` runs fixtures through the real local
speech-to-text path (`ParakeetEngine` with the chosen model, named in `model`
and `engine`): "Stop!", "Wait,
hold on a second.", "Can you tell me more about that?", "Yes." (after a
question), "Yeah.", "Mmmmmm.", "Hmm." and "Ha ha ha ha!" said by a Windows
voice (System.Speech, rendered to memory, never played), "I'm gonna make it
public." said quietly over a fan's hum (`quiet-room`: only about half of it is
as loud as a voice must be to start), plus a hum, two
coughs, a breath, typing, music and noise, each after 0.3 s and before 1 s of
faint noise. Per fixture: `voicedMs` (loud frames by the production
voice-activity detector) and `speechMs` (from its onset to the silence after
it), Parakeet's `transcript`, `evidence` and
`transcribeMs`, the filter's verdict, and `bargeIn`: the production
`BargeInGate` fed 20 ms at a time as if Martlet were speaking, each quick check
transcribing the stretch so far (no other check starts while one runs, as in
the listener) with its `quick` transcripts, probabilities, voice and speech
(`VoicedMs`, `SpeechMs`) and milliseconds,
`interrupted`, `reason` and `afterMs` (from the start of the voice to the
decision). `stopDelayMs` summarizes the fixtures that stopped Martlet. `ok`
needs every expectation met: words kept, non-words and noise dropped, stop
words and the question stopping Martlet, backchannels and non-words never.
Nothing is recorded or played and nothing leaves this PC; without Parakeet,
`audio.ran` is false with the reason.

`barge_in_check` checks [pause and decide](../CONVERSATION.md#voice-latency-streaming-overlap-and-barge-in)
(Companion › Listening › **When you talk over Martlet**; optional absolute
`dataDirectory`, default the current user's). It simulates words said over a
reply and returns the production verdict (`BargeInJudging.RuleAsync` with
`RulesBargeInJudge`) for `samples` (up to 64 of `heard`, with optional
`sentence` (what Martlet is saying), `recentReply`, `voicedMs` (default 900)
and `expectVerdict` `interrupt` or `notForMe`), by default a fixed set: a stop
word and Martlet's name (a clear cue, never judged), "Yeah.", agreeing,
laughing along and Martlet's own sentence heard back (not for Martlet), and a
question and a new request (for Martlet). Each sample returns `verdict`,
`reason`, `source` (`Cue` or `Judge`), `judge`, `judgeMs`,
`quickCheckInterrupts`, `cue` and `action` with the saved choice (*keeps
playing*, *stops at once*, *pauses, then stops* or *pauses, then plays on*).
`deadlines` runs a fixture model judge (NOT AI) slower than the deadline
(`judgeDelayMs`, default 1000; `deadlineMs`, default 400): the local rules
must decide (`source` `Timeout`) within about the deadline; a judge in time
must be used (`source` `Judge`). `holds` runs `BargeInHold` frame by frame on
a simulated clock: quiet after a not-for-Martlet verdict plays on, talking on
past 1.5 s stops, an interrupt verdict stops, and no verdict plays on at the
pause's 4 s limit; each with `outcome`, `why`, `source`, `pausedMs` and
`voiceMs`. It returns `behavior` (`PauseAndDecide` or `StopAtOnce`) with
`behaviorSource`, `bargeIn`, `wordCheck`, `timings` (`deadlineMs`,
`keepTalkingLimitMs`, `quietToResumeMs`, `maximumPauseMs`,
`voiceBeforeCheckMs`, `resumeFadeMs`), `modelJudge` (the Thinking pool's model
judge, `ModelBargeInJudge`, with fixture answers (NOT AI) in place of a pool
member: a verdict is used (`source` `Judge`, `judge` *Thinking pool*), no
member lets the rules decide at once (*no Thinking pool judge was available*)
and an answer without a verdict lets them decide; each with `answer`,
`verdict`, `source`, `reason`, `tookMs` and `promptLines`) and `ok` when every
expectation held. `thinking_pool_status` says whether the pool has a member
that can run the judge. `unprompted` (with `unpromptedOk`) checks [what
Martlet says on its own](../CONVERSATION.md#voice-latency-streaming-overlap-and-barge-in)
(`UnpromptedSpeech`): `talkOver` has one entry for each kind (`Reminder`,
`FinishedWork`, `CheckIn`, `Remark`) with `pausedForJudge` (false for a
remark: it is dropped at once) and what a not-for-Martlet verdict does after a
600 ms and a 3000 ms pause (`Resume` or `Drop`; `resumeWithinMs` is the
limit). `notices` starts a fixture check-in notice and a fixture reminder on
the production `BackgroundJobs` with a simulated clock and checks them again
as just before they are said: `action` `Say`, `Wait` (`code` `mid_utterance`
or `live`) or `Drop` (`too_old` after `checkInMaxWaitMinutes`, `moved_on`
after a kept exchange), a reminder never dropped, and `DropStale` dropping
only the old check-in. Each has `why` in words, never the notice's text.
Nothing is recorded or played and nothing leaves this PC. `spoken_reply_check`
`paused` rehearses the pause and resume through the production runtime; the
talk window's `LiveBargeIn` shows the last real decision.

`sound_digest_check` checks [describing PC sounds](../CONVERSATION.md#describing-pc-sounds)
(Companion › Listening › Watch along › **Describe PC sounds**; optional absolute
`dataDirectory`, `martletDirectory` and `wavFile`). `saved` has `HearPc` (off
by default), `DescribePcSounds` (on by default) and `Source` (`saved` or
`default`). `judges` says which judges describe PC sounds, in order, as the
desktop picks them: `audioRoute` (the audio model from `sense-models.json`:
`file`, `path`, `model`, `unknown`, `why`) and `order` (`audiomodel`, `pool`,
`cpu` while an audio model of its own takes recordings, else `pool`, `cpu`).
`status` is the desktop's `sound-digest.json` or null: `on`, the
active `judge` and `judgeKind` (`audiomodel`, `pool` or `cpu`), `runs`, `lines`, `dropped`,
`skipped`, `lastStep`, `lastJudge`, `lastAgeSeconds`, `lastMs`,
`maximumAgeSeconds`, `everySeconds` and `clipSeconds`; it never holds a line
or a sound. `tagger` says whether `martletDirectory` (by default the installed
release; `Invoke-MartletMcp.ps1` passes this checkout's Desktop build) has the
bundled sound tagger and how long it took to load. `rehearsal` plays a
**FIXTURE** clip (synthesized music with hand claps for 10 s, then silence)
through a fixture loopback on a simulated clock, `PcAudioCaptureFactory`,
`MicrophoneCapture` and the capture normalizer into `PcSoundBuffer`, then runs
one `SoundDigestScheduler` tick with `CpuSoundJudge`: `bufferedSeconds`,
`activeShare`, the tagger's top `tags` with scores, `tagMs`, `step`, the
`line` and `bufferClearedWhenOff`. `file` tags `wavFile` (16 kHz mono 16-bit)
the same way. `ok` needs the tick to start a judge, a line and the buffer
cleared when the digest goes off. It records, plays, sends and saves nothing.

`pc_audio_check` checks [hearing what this PC plays](../CONVERSATION.md#hearing-what-this-pc-plays)
(Companion › Listening › Watch along › **Hear what this PC plays**; optional
absolute `dataDirectory`, default the current user's): `hearPc` (the saved
choice, off by default) with `hearPcSource` (`saved` or `default`),
`handsFree` and `reduceEcho` (it works only with always listening, and through
speakers wants echo reduction on). `windows` says whether this Windows can hear
the PC without Martlet's own sound (`withoutMartlet`, with the process
loopback's `format`, or `problem` and the `fallback`): a process loopback that
leaves out the MCP server's own process is set up and closed again without
starting, so `recorded` is always false. It also reads the outputs' sessions
(never their sound): `output` (the output you hear, Windows' default),
`elsewhere` (another output an app other than Martlet streams to right now,
such as a voice changer's or microphone app's virtual cable, or null) and
`hears` (every app's sound except Martlet's, or only what plays on `output`
while listening to the PC holds off for Martlet's voice, which is what
happens whenever `elsewhere` is set). Its `rehearsal` runs the production
path (`PcAudioCaptureFactory`, `MicrophoneCapture`, the capture normalizer and
the voice-activity detector with the defaults the PC listener uses) on a
fixture loopback and a simulated clock: a synthesized video voice 0-3 s, the
video paused 3-6 s with no packets at all (as a real loopback), the voice
again 6-9 s, then nothing, 12 s in all. It returns `recordedSeconds` (12 when
the gaps were filled), `segments` (`startS`, `endS`, `endedAtS`),
`endedInPause` (the pause ended the first utterance, so always listening sends
it), `resumed` and `pauseSpeechFrames`. `yourVoice` runs the production matcher
(`PcEcho`) that leaves out your own voice when this PC plays it back: its
`rule`, `share` (the share of the PC's words, in order, that must be yours),
`ok` and `samples` (`scene`, `spoken`, `played`, `expected`, `leftOut`: your
voice played back, including the two reported pairs, transcribed the same or
differently or in part, is left out; a video playing while you talk or one you
quote is kept). `ok` is true when all hold, every sample came out as expected
and the fixture's Martlet-free source was used. It reads no credentials and
contacts nothing.

**Muting Martlet's voice**: the overlay menu's `CharacterMuteVoice` (in
`SafeValues`: its name, *Mute voice* or *Unmute voice*, carries the state)
asks Martlet to mute (*The character's menu chose 'mute'.*) or unmute it. It is
the same choice as Companion › Voice's `SpeakReplies` check box (*Speak
Martlet's replies aloud*), so it needs `--allow-ui-effects`: it saves
`talk-preferences.json` (`character_status`'s `voice.muted`), is shared with the
paired computers like the check box, and the desktop log records *Martlet's
voice is muted: replies show as text only.* (or *... unmuted ...*; a choice
from the menu also logs the window's *Status: Martlet's voice is muted: ...*
or *Status: Martlet's voice is on again: ...*) while
`avatar-renderer` records *Martlet's voice is muted.* once the overlay's menu
follows; muting or unmuting there, in Companion or from another computer
changes the menu's item too, and a newly shown character starts with it.
Muting silences a reply Martlet is saying at once (its turn's `VoiceMuted`;
`spoken_reply_check` `voiceFailure` `muted` rehearses it), and the rest of its
words, like every reply while muted, show in the talk window and as speech
bubble and subtitle captions, one sentence per reading time
(`spoken_reply_check` `text-only`).

**Quick sounds while Martlet thinks**: Companion › Voice's card has the
`VoiceQuickSounds` check box (off by default), `VoiceQuickSoundsDelay` (*After
0.5 s*, *After 0.7 s (recommended)*, *After 1 s* or *After 1.5 s*),
`VoiceQuickSoundsStatus` (*Off.*, *On: making the quick sounds with ...*, *On:
4 quick sounds in chatterbox-nano on gpu-pc ...*, *On, but OpenAI ... is a paid cloud
voice, so none play until you press Make quick sounds now ...*, why they couldn't be made, or *On, but
Martlet has no voice to make them with yet ...*) and `VoiceQuickSoundsMake`
(*Make quick sounds now*, shown once they are on and a voice is set up). All
three values are in `SafeValues`; changing the box or the delay saves
`talk-preferences.json` (`QuickSounds`, `QuickSoundDelayMs`; this PC only), and
the button makes the clips with the voice, so they need `--allow-ui-effects`.
`quick_sounds_status` reads the same choice headless, and `quick_sounds_check`
rehearses the rules.

Companion › Singing (`CompanionTab-Singing`, under *Optional extras*;
[Singing](../SINGING.md)) has the standard order. `SingingNow` says whether and
where Martlet sings, in one sentence ("Off: Martlet doesn't sing, because no computer in the music list
is on; Singing stays set up on your computers.", "Not set up yet: add one of your computers ...",
"Martlet writes the music on gpu-pc (or the first free one of 2 computers) in
high quality, and matches the voice on gpu-pc with SoulX-Singer.", or "..., but voice
match is off, so songs keep the music model's own singer."). `Help-SingingAbout` (*?*) explains what asking
for a song does and links to the wiki's *How a song is made*. A song has two
parts, each with its own shared pool list
([the two singing pools](../SINGING.md#the-two-singing-pools)): *Music: where
Martlet writes songs* (`Pool-singing-...`; with none on, Martlet doesn't sing)
and *Voice match: where Martlet matches the singing to its voice*
(`Pool-voice-match-...`; with none on, songs keep the music model's own
singer). Each list's intro shows one sentence with `Help-Pool-singing-Intro` and
`Help-Pool-voice-match-Intro` (the whole intro, with a link to the wiki's *The music and voice match lists*).
Each list has the shared IDs (summary, each member's line with its
state, such as "1. gpu-pc. A paired computer; runs Singing with SoulX-Singer and
VevoSing; high quality." or "...; SoulX-Singer.", On, Up, Down, Remove,
Settings, kept-for and the Add buttons; only computers that aren't shared by
friends and can run Singing can be added). A member's Settings
(`Pool-<area>-Settings-<i>`, passive) shows `Pool-<area>-State-<member key>`
("Singing is ready here with SoulX-Singer.", "Setting Singing up here...",
"Singing isn't set up here yet.", "Setting Singing up failed: ..." or why that
computer can't sing, such as "Needs an NVIDIA graphics card with 6 GB+; this PC
has ..."; the voice matches come from that computer's singing service, read
through its gateway once the page shows it; a computer whose Martlet is too old
to make the music and the voice match apart says so) and
`Pool-<area>-SetUp-<member key>` (*Set up Singing here* while it isn't ready
there; disabled with the reason as help text when the computer can't sing). A
music member adds `Pool-singing-Quality-<member key>` (*High quality*, the
default, or *Fast*: that computer's own quality, saved in the list),
`Pool-singing-MusicModel-<member key>` (*ACE-Step 1.5*, the default, or *ACE-Step 1.5 XL*, saved in the list; where XL
isn't set up it uses ACE-Step 1.5; also *HeartMuLa*, *DiffRhythm* and *YuE*) and one *Add <model> here* button for each music
model not set up there: `Pool-singing-AddXl-`, `Pool-singing-AddHeartMuLa-`, `Pool-singing-AddDiffRhythm-` and
`Pool-singing-AddYuE-<member key>` (each needs `--allow-ui-effects`; the voice match list has `Pool-voice-match-AddVevo-`,
`Pool-voice-match-AddVevo2-` and `Pool-voice-match-AddSeedVc-<member key>`, and its model box offers *Vevo2* and *Seed-VC*). Under each list,
`Pool-singing-AddCloud` and `Pool-voice-match-AddCloud` (*A cloud provider*, safe clicks) show the Singing cloud form:
`SingingCloudProvider-<area>`, `SingingCloudModel-<area>`, `SingingCloudKey-<area>` (a password box; never read),
`SingingCloudKeyStatus-<area>`, `SingingCloudConsent-<area>` (what goes to the provider and that it may cost money),
`SingingCloudAdd-<area>` (needs `--allow-ui-effects`) and `SingingCloudState-<area>`. A cloud member's settings show
`Pool-<area>-CloudKeyStatus-<member key>`, a key box with `Pool-<area>-CloudSaveKey-<member key>` and, for Mureka,
`Pool-singing-OwnVoice-<member key>` (*Sing in Martlet's voice*) and `Help-Pool-<area>-CloudModel-<member key>`
(how to use another model of the provider; wiki *Cloud providers*). A voice
match member adds `Pool-voice-match-AddVevo-<member key>` (*Add VevoSing here*
where it runs without VevoSing) and `Pool-voice-match-Model-<member key>`
(*SoulX-Singer*, the default, or *VevoSing*: that computer's own model, saved in
the list; where VevoSing isn't set up it matches with SoulX-Singer). A computer
added that doesn't sing yet opens its Settings. The lists, the quality, the
model and both buttons save `pools.json` or install, so they need
`--allow-ui-effects`. The first time the page opens with a music list and no
voice match list, it makes the voice match list from the music list. *What
Singing is* shows the role's details only (`Picker-Singing-Role`): its facts
(`PickerFact-Singing-<fact>`: `vram`, `download`, `song`, `license`, `cost`,
`data`) and `SingingGpu` (fixed text: its first sentence, with all of it in
`help` and in `Help-SingingGpu`, which links to the wiki's *Hardware*), which answers whether Singing needs a graphics card of its own. *Voice
match models* has `Help-SingingVoiceMatchModels` (wiki *More choices*) and compares the two models: `Picker-SingingVoiceMatch-SoulX` and
`Picker-SingingVoiceMatch-VevoSing` (passive; *in use* is the first voice match
computer's model) compare their footprint and license
(`PickerFact-SingingVoiceMatch-license`: VevoSing is "CC-BY-NC-ND-4.0:
personal, non-commercial use only ..."). *Set up Singing here* asks one
confirmation: the question and one sentence with the graphics card, the download size, the licences and the
voice rule (the wiki's *What each part downloads* lists every model) and sets the role up
through `martlet-host add singing` (a run window on this PC whose
`HostRunStatus` follows the image build, the service starting and each model
file's download, for example "Singing: downloading model-svc.pt, 45% of 2730
MiB..."); *Add VevoSing here* asks its own confirmation naming VevoSing's
CC-BY-NC-ND-4.0 terms and downloads. There is no play button: songs are only
performed by Martlet in conversation, so make and inspect real songs headlessly
with `singing_check` (`split` makes the song as the two pools do: a music job,
then a voice match job) and rehearse the two pools with `singing_pool_check`.
Companion › Motion (`CompanionTab-Motion`, under *Optional extras*;
[the motion pool](../MOTION_HOST.md#the-motion-pool)) has the standard order.
`MotionNow` says whether and where Martlet makes body motions ("Martlet makes
body motions on m3-host (or the first free one of 2 computers).", "Off. Martlet
makes no new body motions; the ones it kept still play. No computer in the list
is on.", "Not set up yet. ..."). `MotionCharacter` says whether the character
shown (else the chosen one) can play them: only a VRM can ("Your character is a
Live2D model. Body motions play only on VRM characters, ..."). `MotionOpenEmotes`
(*Kept motions and Make a motion*, passive) opens Companion › Emotes and
motions. There is no Off choice and no single-computer choice: *Where Martlet
makes motions* is the shared pool list with the `Pool-motion-...` IDs (summary,
each member's line with its state, such as "1. m3-host. A paired computer; runs
Motion with HY-Motion-1.0-Lite.", On, Up, Down, Remove, Settings, kept-for and
the Add buttons; only computers that aren't shared by friends and can run Motion
can be added). A member's Settings (`Pool-motion-Settings-<i>`, passive) shows
`Pool-motion-State-<member key>` ("Motion is ready here with ...", "Setting
Motion up here...", "Motion isn't set up here yet.", "It isn't reachable right
now." or why that computer can't make motions, such as "Needs an NVIDIA
graphics card with 24 GB+; this PC has ...") and `Pool-motion-SetUp-<member
key>` (*Set up Motion here*; disabled with the reason as help text when the
computer can't make motions). It runs `martlet-host add motion` with the role's
own dialog (the model and Tencent's license terms), so it needs
`--allow-ui-effects`, as do the list's On, Up, Down, Remove, Add and kept-for
choices, which save `pools.json` and share it. `MotionAbout` (fixed text) says
in one sentence what the Motion role needs and where its license doesn't apply;
its *?* `Help-MotionAbout` has the rest. The Companion character pages
(Character, Speech bubbles, Lip-sync, Motion, Eyes, Touch, Profiles) and the
Character, Find a character and Personality windows put their longer
explanations behind *?* marks that link to the wiki's *The Character* and
*Personality and Characters* pages, such as `Help-SetupCharacterModelAbout`,
`Help-SpeechBubbleOffsets`, `Help-LipSyncThisPcSetUp`, `Help-MotionWhat`,
`Help-CharacterGazeUsual`, `Help-TouchZonesStartOver`,
`Help-TouchTemperamentZones`, `Help-HouseholdCharacters`,
`Help-AvatarWindowAbout`, `Help-CharacterFinderAbout`,
`Help-CompanionTextHelp` and `Help-CompanionVoicePauses`. A pool's intro
(`Pool-<area>-...`) shows its first sentence with the rest behind
`Help-Pool-<area>-Intro`.
On Companion › Tools (`CompanionTab-Tools`), the Now line `ToolsNow` comes first, then the Terminal card with
the page's main choice: the radio buttons `ToolsTerminalOn` (*Let Martlet run terminal commands*) and
`ToolsTerminalOff` (*Off*, the default) report which one is `selected`, and
`ToolsTerminalAskFirst` (*Ask before every command*, on by default) reports its
state as `checkedState`; `ToolsTerminalStatus` reads the state in words (*Off.
Martlet can't run commands on this PC.*, *On. Windows PowerShell, asks before
every command, stops a command after 30 seconds.*, or what keeps it from working:
the shell isn't installed, the start folder is gone, Thinking isn't set up or
can't use tools, or the model turned tools down); `ToolsTerminalShell` and
`ToolsTerminalTimeLimit` return the chosen shell and time limit. Everything
there saves `terminal.json`, so it needs `--allow-ui-effects`: `ui_click` on the
radio buttons, `ui_toggle` on the check box, `ui_select` on `ToolsTerminalShell` (*Windows PowerShell*,
*PowerShell 7*, *Command Prompt*, with *(not installed)* when missing) and
`ToolsTerminalTimeLimit` (*15 seconds*, *30 seconds*, *1 minute*),
`ToolsTerminalHome` (*Use my home folder*, shown only for a chosen folder) and
`ToolsTerminalFolder` (*Choose folder...*, a Windows folder picker MCP can't
drive; the folder's path is never returned). Turning *Ask before every command*
off asks first: the dialog's `ToolsTerminalNoAskQuestion` is returned, and
`ConfirmationYes` (*Run without asking*) or `ConfirmationNo` (*Keep asking*)
answers it. `terminal_status` reads the saved result. In the talk window, a
waiting call shows `LiveToolApproval` with `LiveToolApprovalTitle` (*Allow this
tool?*, or *Run this command?* for the terminal) and `LiveToolApprovalText`
(*Martlet wants to run this in Windows PowerShell, on this PC as you.
Automatically denied in 52 s.*); the command or arguments
(`LiveToolApprovalArguments`) are never returned, and `LiveStatus` reads *Allow
the command? Answer above.*, then *Running a command…*. `LiveToolDeny` is a
passive click (it only declines the call); `LiveToolAllow` and `LiveToolAlways`
run it, so they need `--allow-ui-effects`. With the terminal on, Home's
`HealthCheck-tools` tile shows (*Tools: OK. Terminal on*).

Status fields include the talk window's `LiveStatus` (its status line),
`LiveMic` (the Start listening / Stop listening icon button; like `LiveVision`
and `LiveStop` it shows only an icon, and its tooltip starts with what it
does; its value starts with
the state: *Not listening* until it is pressed, then *Listening*, *Can't
listen* or *Mic unavailable* with the reason; while Martlet speaks it reads
*Listening. Martlet listens for you, even while it speaks…* when echo
reduction works (or barge-in is on), and *Not listening while Martlet speaks,
so it doesn't hear itself* when listening pauses instead (echo reduction off
or not running, without barge-in). Clicking it opens the microphone, so it
needs `--allow-ui-effects`; errors, Stop and `LiveStop` never stop listening,
only the button or *Pause Martlet* in the notification-area menu does, and
listening that can't start yet, such as Voice ID not set up, reads *Can't
listen* and keeps retrying), Home's `OpenLiveConversation` (*Start talking*,
or *Show conversation* while a conversation runs, shown or hidden), Home's
`HomeListen` (*Start listening* / *Stop listening*, shown with always
listening; it runs the conversation hidden, without the talk window, so it
needs `--allow-ui-effects`) and `HomeListeningStatus` (the listening
indicator: *Not listening*, *Getting ready to listen…*, *Listening, so just start
talking.*, *Hearing you…*, *Martlet is replying…*, *Martlet is speaking…*,
*Paused…* or why it can't listen, such as *Set up Thinking in Companion, then
come back to talk.*, or, when the microphone fails, why and how often Martlet
tries it again, such as *Martlet can't open the microphone. Check that it is
connected and enabled. Martlet keeps trying every 10 s.*; `logs_tail` with
`contains` *Always listening* shows each failure's error code), Home's `HomeWatch` (*Start watching* / *Stop watching*,
shown while vision is on in Companion; it runs the conversation hidden and
starts screen or camera capture, so it needs `--allow-ui-effects`; Stop watching
only stops it) and `HomeWatchingStatus` (the watching indicator: *Not
watching*, *Getting ready to watch…*, *Watching your active window.* (or *your
whole screen*, a camera), *Taking a look…*, *Paused…* or why Martlet can't see,
such as *Choose a camera in Companion › Vision.*). Listening and watching are
separate: neither button starts or stops the other, and opening the talk window
or turning vision on in Companion starts neither. Home's `HomeWarmupStatus`
(the Warming up indicator, shown while something still loads for at least a
second: *Warming up: loading gemma4:e4b on this PC (12 s of about 20 s).
Martlet answers once the model has loaded.*, with *getting the conversation
ready*, *loading Parakeet TDT 0.6B v3 for listening* or *starting the
character* for the other parts, then *Warmed up in 18 s, and Martlet is ready to
talk.* for 8 s; the desktop log says *Martlet is warming up: ...* and *Martlet
warmed up in 18.0 s.*) and the talk window's `LiveWarmup` (the banner's line
under its *Warming up* title: *Loading gemma4:e4b on this PC (3 s of about
12 s).*; `LiveWarmupProgress` is its progress bar) show what still loads; the
talk window's `LiveStatus` reads *Warming up, but you can talk or type now.*, or
*Waiting for gemma4:e4b to load; Martlet answers once it's ready.* while a
reply waits for the model. Home's Martlet on this PC card has `HomePcTitle`
(*Martlet on this PC*, *Freeing up this PC…* or *Resting*), `HomePcStatus`
(what Martlet holds on this PC now: *Martlet uses this PC for gemma4:e4b (4.9
GB of graphics memory), Parakeet TDT 0.6B v3 for listening, the character and
the conversation (listening).*, or *Martlet holds no models on this PC now.
...*; while resting, what Free up this PC did: *Martlet ended the conversation
and unloaded gemma4:e4b (4.9 GB of graphics memory). Start talking or Start
listening warms it up again.*) and `HomeFreeUp` (*Free up this PC*, disabled
while Martlet holds nothing; it ends the conversation, hides the character and
unloads every model Ollama on this PC has loaded, Parakeet and the
voice-recognition engine, so it needs `--allow-ui-effects`; the desktop log
says *Free up this PC: ...*). Home reads Ollama's `/api/ps` on loopback every
5 s while it shows. To check these without Ollama, run a fixture Ollama on
`127.0.0.1:11434` that answers `/api/ps`, `/api/tags` and `/api/generate`
(loading slowly, and unloading on `keep_alive` 0), and seed the disposable
data directory's `settings.json` with Thinking on Ollama on this PC; say in
the results that it is a fixture, NOT Ollama. While Martlet listens or watches, the talk window's close
button only hides it (`ui_snapshot` stops listing *Talk with Martlet*;
`OpenLiveConversation` shows it again),
`LiveVision` (the talk window's *Start watching* / *Stop watching* button, or
*Can't see* with the reason; its value starts with the state: *Not watching*,
*Watching*, *Looking* while a look is with the model, or *Can't see*, then when
it last checked the screen; it checks every 3 s, and with the whole screen it
also says it looks right away at pop-up notifications and flashing taskbar
buttons; clicking it to start captures the screen or camera, so it needs
`--allow-ui-effects`), `LiveVisionStatus` (while
vision is on, kept short: what it sees, for example *Watching your whole
screen.* or *Watching the window behind Martlet.*, then only a look in progress
(*Taking a look…*), why it is holding off (*Waiting until you're back.*,
*Taking a break from looking.*, *The provider is busy; waiting before the next
look.*, *Martlet noticed a notification and looks once you're done talking.*)
or a look that failed; its `help` (the tooltip) says how many monitors the
whole screen spans (*Your whole screen is 2 monitors.*), the program in front
as the Thinking model is told it (*Active app: Google Chrome (full screen).*),
how the last look went
(*Last look 10:17 PM (a flashing taskbar button): nothing to say.*, followed by
*To fit Thinking's context size, it went without the text read on the screen.*
when the look had to leave something out) and what
wanted your attention but wasn't looked at (*Noticed a notification at 10:17 PM
but didn't look: you seem away.*); whether a message went with the picture is
the note on its bubble (*Ivy saw your whole screen.*, by the persona's name); neither contains
window titles; to rehearse a flash, show any test window minimized and call
`FlashWindowEx` on it), `LiveGaze` (while Martlet decides where the character
looks and watches your screen: what its eyes are on now, *Looking at your
mouse: nothing new on screen.*, *Glancing at something new at the bottom right
of your screen.* or *Looking at the top right of your screen, where Martlet
chose to look.*, and when they last looked away; never what is on screen),
`LiveScreenSummary` (while Martlet watches and Companion › Vision › *Screen
summary over time* runs: *Screen summary: 4 pictures kept; last summary 12 s
ago (took 2.1 s).*, *…; summarizing…* or why the last one failed; its `help`
is the last summary, one or two lines on what changed on the screen),
`VisionScreenSummary` (the check box; `ui_toggle` with `--allow-ui-effects`
saves talk-preferences.json) and `VisionScreenSummaryStatus` (*On: while
Martlet watches, ...* with the model that makes them, *Off: ...*, or *Off for
now: no image model and no Thinking pool model that sees takes them, ...* with
what to do),
`LiveContext` (*Keeps the last N exchanges in mind.*, or *Keeps the last N
exchanges in mind; replies send the newest that fit.* once they outgrow the
context; its `help` (the tooltip) says *About T tokens of its C-token context.*
(or *About T tokens, more than fit its C-token context.*), followed by *Last
reply: P% of its N input tokens came from the model's cache.* once the Thinking
model reported its cache use: how many exchanges
of the open talk window the next reply can see, their estimated tokens and the
context size from Companion › Replies; absent when none, and unchanged when a
settings change is picked up; beside it,
`LiveRefreshContext` (*Refresh context*, a passive click, disabled mid-reply)
forgets them so the next reply starts fresh, adds the note *Context refreshed, so ...*
to `LiveHistory` and hides `LiveContext`), `LiveTurnInputs` (once Martlet has
replied or looked: what its newest reply, report or look took together, from
[one moment](../CONVERSATION.md#one-moment-everything-in-one-reply): *Last reply
took your words, 1 line this PC played and the picture.*, *Last reply took 2
lines this PC played, the picture (a notification) and 2 finished jobs,
counted as a look.*, *Last report took the picture and 1 finished job.* or
*Last look took the picture.*; counts only, never what was said, seen or
found; *... and 2 context notes* when it carried\n[context board](../CONVERSATION.md#context-board) notes; the desktop log has the\nsame as *Turn took: ...* lines),
`LiveTasks` (the header's background
tasks chip, shown once Martlet starts a task in the conversation: its name reads
*Background tasks: 2 running*, *1 running · 1 ready*, *1 ready* or *3 done*; a
passive click that only opens and closes the task list `LiveTasksPanel` over the
conversation, which `LiveTasksClose`, Esc or a click in the conversation also
close), `LiveReadMore` (the header's *Read more* chip, shown while the app in
front, or the one the last reply used a guide about, has a guide: its name reads
*Read more about Elden Ring* and its help text says how many pages the guide has;
pressing it reads more of the app's wiki on the web, so it needs
`--allow-ui-effects`), and in that list `LiveJobs` (*Martlet keeps working on these while you
talk, and Stop (Esc) doesn't end them.*, *Finished work comes up as soon as Martlet
is free.* or *... when you talk next.*; never what a task is about), each task's
card `LiveTask-<id>` with `LiveJob-<id>` (what the task is about, so snapshots
don't return it), `LiveJobState-<id>` (*Checking it fits beside Thinking.*,
*Done after 1:02. Martlet brought it up.*, *You stopped it.*, *Couldn't finish:
it failed on this PC.*), `LiveJobResultToggle-<id>` (*Show result*, a passive
click that shows `LiveJobResult-<id>`, which isn't a readable value) and
`LiveJobCancel-<id>` (needs `--allow-ui-effects`: it stops that task, and the
next thing you say tells Martlet). Setting `MARTLET_BACKGROUND_FIXTURE=1` before
Martlet starts makes opening the talk window start one *FIXTURE - NOT AI* task
(`fixture-1`) that works until canceled, so these can be checked without a
model. Then the song panel
`LiveSongPanel` (shown while Martlet sings or has a song to offer; it has no Play
button, since only Martlet performs songs): `LiveSong`
(*Singing 3fa2c19b0d71 · 0:22 of 1:00 · verse line 4 of 12.*, *Starting
3fa2c19b0d71 line 4 where you stopped: a bar of the band first · 0:08 of 0:40.*,
*Stopping 3fa2c19b0d71 at 0:10: finishing the word, then the band rings out on the
beat.*, *Stopped 3fa2c19b0d71 at 0:17 (verse 2 line 7 of 10): you pressed Stop
singing. Ask Martlet to pick up where it left off.* or *Song 3fa2c19b0d71 is ready
(0:40, 10 lines), and Martlet will offer it.*; never the title or words, which
`LiveSongLine` holds, word by word as they are sung) and `LiveSongStop` (*Stop
singing*, a passive click: it only ends the song musically), with the status
line reading *Starting to think it over in the background…* while
`think_longer` runs, *Starting a song in the background…* while `sing_song`
starts a song job, and *Martlet is bringing up what it worked on…* while
Martlet's own report is on its way; Companion › **Thinking pool**'s
`DeepThinkingNow` (*The Thinking pool thinks on diva (gemma4:27b), in parallel
with the conversation.*, *On, but the pool can't think on ..., so Martlet
doesn't offer to think things over; add a machine below, or tick Long jobs on
one.*, or *Off: Martlet answers everything right away and never thinks in the
background.*) and `DeepThinkingParallel` (why: *Thinking's model (gemma4:e4b)
runs on this PC and can't think something over while it answers you. ...*,
*It runs as a second model beside Thinking's gemma4:e4b on this PC, ...*, *diva
does none of the conversation's jobs, so a think runs there alongside the
conversation.*; both count only the members ticked for Long jobs), the
`ThinkLongerOn` box (*Let Martlet think things over in the background*,
`checkedState`; `ui_toggle` saves the reply settings, so it needs
`--allow-ui-effects`), `ThinkLongerStatus`
(*On: when a task needs it, Martlet says it'll think it over and works on it in
the background (Medium effort, no time limit, no limit on how many) while you keep
talking, then brings it up as soon as it's free.*, *Off: ...*, or what keeps it
from working: no Thinking, a paired host's model, a model that turned tools
down, nowhere to think in parallel) and the choices `ThinkLongerEffort` and
`ThinkLongerDelivery` (returned; a think has no time limit or hourly limit, so
there is no choice for either;
`ui_select` on them saves the reply settings, so it needs
`--allow-ui-effects`); *Web research*'s `WebResearchStatus` (*Off. Martlet
never searches the web or reads web pages.*, *Off, because Deep thinking is
off. ...*, *On. When you ask, Martlet researches it in the background (up to 30
searches and 60 pages, no time limit), then offers the report.* or *On, but
Martlet can't look things up yet: ...*), its fixed `WebResearchDisclosure` (what
leaves this PC and the budget) and the `WebResearchOn` check box, on by default
(`checkedState`; `ui_toggle` saves the reply settings, so it needs
`--allow-ui-effects`); there is no *Where it thinks* choice: the *Machines*
list right after Now is the pool (each machine's `ThinkingPoolOn-<n>`; with no
machine on, the pool is off; see [Thinking pool](thinking.md#thinking-pool)); in
*Machines*, each paired computer that isn't a
member: `DeepThinkingHost-<host ID>` (*diva: Its Thinking pool role runs
qwen3-8b; it joins the pool by itself at its next check.*, *diva: Ollama runs
gemma4:27b; ...*, *Its Ollama (...) does Thinking for the conversation; add
the Thinking pool role there to join the pool beside it.*, *Kept out of
the pool because you unticked it (...); tick In the pool to add it again.*, *Not
reachable right now.*) and, when the pool role there would share one
graphics card with the computer's Thinking model,
`DeepThinkingShare-<host ID>` (*diva: diva already runs a Thinking model
(gemma4:e4b) on its only graphics card. ... We recommend one graphics card for
each Thinking model ...*), and, for a reachable computer without the role, its
`DeepThinkingAddRole-<host ID>` button (returned: *Add the Thinking pool role
on diva*; clicking it first asks `DeepThinkingShareQuestion` when the card is
shared, then installs the role in a run window and then adds it to the pool,
so it needs `--allow-ui-effects`) or, for one with the role, its
`DeepThinkingChangeModel-<host ID>` button (returned: *Change the Thinking pool
model and slots on diva (now gemma4:e4b)*; clicking it opens the role's
settings there with that model selected, so it needs `--allow-ui-effects`).
*Add a machine* › *Another of your computers* (`Fold-ThinkingPoolAddComputer`)
has `DeepThinkingAutoJoin` and, when no computer
is paired, `DeepThinkingHosts`; *A model in Ollama on this PC*
(`Fold-ThinkingPoolAddLocal`) and *A cloud provider or server*
(`Fold-ThinkingPoolAddCloud`) have:
`DeepThinkingLocalStatus` (what Ollama on this PC has downloaded),
`DeepThinkingLocalFit` (whether the model in `DeepThinkingLocalModel` fits
beside Thinking's on the graphics card, read from Ollama's `/api/ps` and
`/api/tags` and the NVIDIA driver without loading anything: *Fits: gemma4:e2b
(about 5.3 GB) fits beside ...*, *Doesn't fit: ... Choose a smaller model.*,
*... is Thinking's own model ...*, or *Thinking runs elsewhere, so ... has
Ollama on this PC to itself ...*), `DeepThinkingLocalShare` (shown when Thinking
uses Ollama on this PC and this PC has fewer than two graphics cards: *This PC
already runs a Thinking model (...) on its only graphics card. ... We recommend
one graphics card for each Thinking model ...*) and `DeepThinkingKeyStatus` (what the key
field will do; never a key or typed base URL). `DeepThinkingUseLocal`
(refuses Thinking's own model, and asks `DeepThinkingShareQuestion` first when
`DeepThinkingLocalShare` shows) and `DeepThinkingSaveCloud` (with
`DeepThinkingProvider`, `DeepThinkingBaseUrl`, `DeepThinkingModel`,
`DeepThinkingKey` and `DeepThinkingConsent`) add a member to
`thinking-pool.json` and need `--allow-ui-effects`; an open conversation's
next think uses it.
Companion › Replies' `RepliesOpenDeepThinking` (passive) opens the page
and Companion › Lip-sync's `LipSyncNow` (what moves the mouth for the next
sentence: the first member that is on in lip-sync's list, or voice loudness)
and `LipSyncNowProblem`. Lip-sync's places are its pool list
(`Pool-lip-sync-*`, the shared list control above; there is no other on/off
switch). `Pool-lip-sync-Settings-<i>` is a safe click: it shows *This PC*'s
address box `Pool-lip-sync-Endpoint` (saved by `Pool-lip-sync-SaveEndpoint`)
or a computer's `Pool-lip-sync-Install-<host>`,
`Pool-lip-sync-Change-<member key>` and `Pool-lip-sync-Check-<host>`. Under
the list, *Audio2Face on this PC* reads `LipSyncDockerAbout` (what this PC's
graphics card means for Audio2Face) with `SetupLipSyncHostThisPc` (no host
service yet) or `SetupLipSyncUseLocal` and `SetupLipSyncCheckLocal`. Saving
the address, On, Up, Down, Remove, Add and the set-up buttons change lip-sync
and need `--allow-ui-effects`; `lip_sync_pool_status` then shows the list and
the profile's `lipSync` and `assigned` following its first member that is on.
When Thinking runs in Ollama on this PC, the talk window has Ollama load the
model as it opens (and again on activity after a few quiet minutes), and
`LiveStatus` says *Ollama is loading <model> on this PC (N s)…* while it loads,
or why it can't (Ollama not running, model not downloaded, Ollama's own error);
the desktop log records each load's duration
(`{"name":"logs_tail","arguments":{"contains":"Ollama on this PC"}}`).
When a load fails because a Gemma 4 model's speculative-decoding draft model
doesn't fit in graphics memory (*Gemma4Assistant requires ctx_other ... error
loading model: vector*), the talk window and *Test model* save
`draft_num_predict 0` on that model in Ollama (`/api/create`, same tag) and load
it again; the log records *Turned off the speculative-decoding draft model*
and the test run's output says so.
The talk window is modeless: `ui_snapshot`'s `windowStates` lists each window
with `enabled` (false while a modal dialog such as Audio setup blocks it), and
the main window stays enabled while the talk window is open. It is also a
window of its own (`ownedBy` null, with its own taskbar button): the main
window can come in front of it, so you can watch the conversation while you
change settings. Focus a main-window control (for example `ui_click` on
`NavSettings` with `focus` true) and `windows` lists *Martlet* before the talk
window; `OpenLiveConversation` (*Show conversation*) brings the talk window
back in front, also when Windows doesn't let Martlet take the focus.
Opening the talk
window never opens the microphone or looks at the screen; `LiveMic` does the
first with always listening on, `LiveVision` the second with vision on. For
verification, save a fixed microphone that does not exist in the disposable
data directory, so listening starts after `LiveMic`,
fails without capturing real audio and shows *Mic unavailable* while it keeps
retrying (it never stops by itself). The talk window's `LiveStop` (Stop, Esc)
is a passive click: it only stops a reply, recording, vision or a song (with a
quick fade). Changing How
you talk on Companion › Listening (`TalkModePushToTalk`, `TalkModeAlways`)
applies to an open talk window at once (`LivePtt` replaces `LiveMic`). With
always listening, the same card has `TalkWordCheck` (*Word check*: *Relaxed*,
*Normal (recommended)* or *Sensitive*; returned as the chosen option, and
`ui_select` on it needs `--allow-ui-effects` because it saves
`talk-preferences.json`; an open talk window restarts listening with it) and
`TalkWordCheckAbout` (returned: its first sentence, that Martlet ignores sounds that aren't words
and words speech-to-text makes up from noise; its `help` adds what Relaxed and Sensitive change
and that ignored sounds show faded in the talk window, and `Help-TalkWordCheck` links to
`Talking-to-Martlet#how-martlet-takes-turns`; `utterance_filter_check` runs the filter itself),
then `TalkJudgeTurns` (*Judge when I finish talking (recommended)*, on by
default; its `checkedState` is the saved choice, and `ui_toggle` on it needs
`--allow-ui-effects` because it saves `talk-preferences.json`; an open talk
window restarts listening with it) and `TalkJudgeTurnsStatus` (returned, never
words or audio: *Off: the pause above alone decides when you finished
talking.*, *On, but the judge can't run here (...), so the pause above decides until it can.*, *On, but Smart Turn can't run here (...), so a
Thinking-pool model judges instead...*, or *On: Smart Turn v3.2 on
this PC (loaded in 1519 ms); last 4 pauses: 2 finished, 1 unfinished, 1 left
to the pause, judge median 30 ms; last: complete after 280 ms of silence.*,
updated after each decision; the desktop log has one *End of turn: ...* line
per decision, and `turn_judge_check` runs the judge headless),
then `TalkEarlyReplies` (*Start replies early (recommended)*, on by default),
with it on `TalkEarlyRepliesCloud` (*Also for cloud models (may add a small
cost)*, off by default) and `TalkEarlyVoice` (*Prepare the voice early too*,
on by default); their `checkedState` is the saved choice, and `ui_toggle` on
any of them needs `--allow-ui-effects` because it saves
`talk-preferences.json` (an open talk window restarts listening with it).
`TalkEarlyRepliesStatus` (returned, never words or audio: *Off: Martlet starts
each reply once you finished talking.*, *On, but replies start early only with
Parakeet on this PC as Listening.*, *On, but not with this setup: Thinking is a
cloud model; turn on Also for cloud models ...*, or *On: Thinking runs on your
own computer; the first spoken words are prepared early too; last 3: 1 taken as
the reply, 1 let go because you went on talking, 1 let go for another reason;
last: changed after 900 ms.*, updated after each reply started early; the
desktop log has its *Early reply: ...* lines, and `early_reply_check`
rehearses it headless) and `TalkEarlyRepliesAbout` (returned: its first sentence, what it does;
its `help` adds that it needs Parakeet on this PC and why cloud models need the second box, and
`Help-TalkEarlyReplies` links to `Talking-to-Martlet#how-martlet-takes-turns`),
then `TalkBargeIn` (*Let me interrupt Martlet by
talking*, optional and off by default; its `checkedState` is the saved choice, and
`ui_toggle` on it needs `--allow-ui-effects` because it saves
`talk-preferences.json`) and `TalkBargeInAbout` (returned: its first lines, that it is optional
and off by default and that Martlet keeps listening while it speaks; its `help`, also behind its *?*, has all of it: that
Martlet keeps listening while it speaks either way
(with echo reduction on) and answers what was said after the reply, and what talking over
Martlet takes: real words, a word like "stop" or "wait" right away, never a hum,
a cough, laughter, a quick "yeah" or what this PC plays, checked while you talk
with Parakeet on this PC and otherwise once you pause; `utterance_filter_check`
rehearses it with Parakeet and `echo_check`'s `talkOver` the voice gate), then
`TalkBargeInBehavior` (*When you talk over Martlet*: *Pause and decide
(recommended)*, the default, or *Stop at once*; choosing one with `ui_select`
saves `talk-preferences.json`, so it needs `--allow-ui-effects`) and
`TalkBargeInBehaviorAbout` (returned: its first sentence, that a clear word or Martlet's name still
stops at once; its `help` adds that other words pause Martlet at once and it decides, words for it
stop the reply, a backchannel, agreeing, laughing, side talk or a TV leave it
playing on from where it paused, and talking on stops it; `barge_in_check`
returns the verdicts). In the
talk window, `LiveBargeIn` (shown once you talked over Martlet with barge-in on)
says what happened the last time, never the words: *Talked over at 14:02:11:
paused 430 ms, then resumed (not for Martlet: agreeing or laughing along; rules
judge, 1 ms).* or *... stopped at once (for Martlet: a stop word; a clear
cue).* A remark Martlet started on its own ends *then dropped* when it was
dropped. `LiveUnprompted` (shown after the first drop) counts what Martlet
meant to say on its own but dropped, never the text: *Dropped on its own: 2 (1
too old, 1 the conversation moved on, 0 talked over). Last: checkin-3 (a
check-in) at 14:02:11: too old: ...*. In the
talk window, what always listening ignored shows in `LiveHistory` as a faded
note (*Ignored "Mmm" (not words).*), and the desktop log (`logs_tail`) has
*Always listening ignored what it heard: ...*, *Always listening heard you
while Martlet spoke; ...* (said while a reply played, without barge-in), *Barge-in: Martlet paused its
reply N ms after you started talking over it (...); the rules judge decides
...*, *Barge-in: Martlet resumed its reply after a N ms pause: what you said
wasn't for it (...)* and *Barge-in: Martlet stopped its
reply N ms after you started talking over it (...; paused N ms first, then the
rules judge (N ms) said it was for Martlet; ...)*, never the words. Each
message in `LiveHistory` has an automation ID for whose it is, never its words:
`LiveMessage-You`, `LiveMessage-Martlet`, `LiveMessage-Note` or
`LiveMessage-PcAudio`; so `ui_snapshot` shows, for example, that something
that went straight to Thinking and speech-to-text couldn't transcribe left no
`LiveMessage-You` bubble (the log says *Background transcript: speech-to-text
couldn't transcribe what went straight to Thinking ...*). The talk window calls
the character by the name of the persona Martlet uses (Companion ›
Personality): its title, header, message box and empty conversation, each
reply's label (*Ivy · 10:39 PM*, *Ivy, about your whole screen*) and the notes
in its history (*You touched Ivy (touch: ...)*, *Ivy stayed quiet.*). MCP never
returns that name: the window's accessible name stays *Talk with Martlet*, the
history's words and labels are not values, and the status lines MCP reads
(`LiveStatus`, `HomeListeningStatus`, `TouchZonesNoticed`, `TouchZonesNoticedLast`)
keep saying Martlet; `character_physical_check` returns the touch note with
the data directory's persona name. Below it, the *Speakers and echo* card has
`TalkReduceEcho` (*Reduce echo from my speakers*, on by default; its
`checkedState` is the saved choice and `ui_toggle` needs `--allow-ui-effects`)
and `TalkReduceEchoStatus` (returned: *On: Martlet removes what this PC plays
from the microphone whenever it listens.*, how the last listen went, why echo
reduction couldn't run, or *Off, so through speakers ...*); `echo_check` reads the same saved
choice. The *Watch along* card under it has `TalkHearPc` (*Hear what this PC
plays*, off by default; `checkedState` is the saved choice and `ui_toggle`
needs `--allow-ui-effects` because it saves `talk-preferences.json`) and
`TalkHearPcStatus` (returned: *Off: Martlet hears only your microphone.*, *On:
while Martlet listens, it also hears what this PC plays, without its own
voice.*, *On, but <another output> is in use too (a virtual cable there can carry
your own voice), so Martlet hears only what plays on <your output> and stops
hearing it while it speaks.*, or why it doesn't apply: push-to-talk, echo
reduction off, or Martlet's voice can't be left out; the card reads which
outputs are in use, never their sound); `pc_audio_check` reads the same choice.
Under it, `TalkDescribePcSounds` (*Describe PC sounds*, on by default, enabled
only while *Hear what this PC plays* is on; `ui_toggle` needs
`--allow-ui-effects` because it saves `talk-preferences.json`) and
`TalkDescribePcSoundsStatus` (returned: *Works while Hear what this PC plays is
on.*, *Off: only the words this PC plays reach Thinking.*, or *On.* with the
active judge, a Thinking pool model that hears or the CPU sound tagger, whether
it describes now, and *Last: "<line>" (<age> ago, <judge>, <ms> ms).* or *No
line yet.*); `sound_digest_check` reads the same choice.
The card also has `TalkPcChattiness`, the same choice as Vision's
`VisionChattiness` (*Quiet*, *Normal*, *Chatty* or *Martlet decides*; returned,
and `ui_select` saves `talk-preferences.json`, so it needs
`--allow-ui-effects`), each with its `...Status` line (`TalkPcChattinessStatus`,
`VisionChattinessStatus`: what the level means, or what Martlet decides means
and, while a conversation runs, *Right now it is quiet.*); `chattiness_status`
reads the same choice. While Martlet decides and vision or hearing the PC is
on, the talk window's `LiveChattiness` line (returned) says *Chattiness:
normal (Martlet decides).*, then *Chattiness: quiet (Martlet decides).* once a
reply switched it (its `help` says *Martlet picks how chatty it is about what
it sees and hears and switched to quiet at 10:14 PM.*), and `LiveHistory` gets
a note such as *Martlet went quiet about what it sees and hears.* With it on
and always listening chosen, the talk window's `LivePcAudio` line (returned)
says *Also hears this PC once you start listening.*, *Also hearing this PC.*,
*Hearing this PC play something…* or why it can't hear the PC; its `help` says
how: *Martlet hears everything this PC plays except its own voice.* or
*Martlet hears what plays on <your output>, paused while it speaks.*, then what
you seem to be doing on the PC (*Now: playing a game (Elden Ring), full screen;
in a voice chat in Discord.*, from `PcActivityMonitor`), *The microphone also
heard this PC's speakers; Martlet left out N line(s) of it.* once the
microphone repeated what a video, show, game or music played, and *This PC
plays your voice back too; Martlet left out N line(s) of it.* once
a line the PC played repeated what you said; what the PC played shows in
`LiveHistory` as *Playing on this PC* bubbles, named after where it came from
when Martlet can tell (*Playing on this PC: a YouTube video in Chrome*). Pressing `LiveMic` with it on
records what the PC plays, so leave it off (or don't start listening) when
verifying on a desktop whose sound must not be captured. Each reply writes a
*Reply latency* line to the desktop log (see [Latency](diagnostics.md#latency)), which
`logs_tail` returns and `latency_report` summarizes.
