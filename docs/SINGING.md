# Singing

Martlet can write a song and sing it in a voice from the owner's voice library (Companion > Voice > Voices). The
**singing** host role runs the pipeline on an NVIDIA graphics card, on this PC or another of the owner's computers:

1. **Write the music:** [ACE-Step 1.5](https://github.com/ace-step/ACE-Step-1.5) turns the lyrics (with section tags such
   as `[verse]` and `[chorus]`), a style caption and a length into a full song with a singer and backing music.
2. **Separate the voice:** the vocals model of [Demucs](https://github.com/facebookresearch/demucs) `htdemucs_ft` splits
   the song into vocals and backing.
3. **Match the voice:** [SoulX-Singer-SVC](https://github.com/Soul-AILab/SoulX-Singer) converts the vocals to the chosen
   voice from its recording alone (zero-shot: no training, a few seconds of speech are enough), keeping the tune and the
   words. VevoSing ([Amphion Vevo1.5](https://github.com/open-mmlab/Amphion)) is an optional second voice match.
4. **Mix and time:** the matched vocals (only the sung phrases, so no backing bleed) go back over the backing; the song
   gets a beat grid and the time of every lyric line and every sung word.

The result is three sample-aligned 48 kHz tracks (the mix, the dry vocals and the backing), the lyric lines with their
sections and sung starts, the sung words (for lip sync), the beat grid, and the time each stage took. Songs are only
ever performed by Martlet itself in conversation; the owner's "Creations" library keeps and shares them. Songs are made
ahead of time, not in real time.

**Measured** (MCP `singing_check` with the 6.1 s Jane Doe speech clip, turbo + SoulX-Singer, tempo and key in the request
so the planner is skipped; RTX 4070 12 GB, a PC short of system memory; seconds):

| Song | Total | Loading | Music | Lyric timestamps | Separating | Voice match | Mixing + aligning | Peak VRAM |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 30 s, cold | 273 | 123 | 16 | 7.6 | 4.6 | 102 (50 loading SoulX-Singer) | 5.3 | 6.0 GB |
| 30 s, warm | **20** | 0 | 6.7 | 0.1 | 2.3 | 9.7 | 0.6 | 7.2 GB |
| 60 s, cold | 332 | 141 | 38 | 13.5 | 9.8 | 115 (53 loading) | 5.9 | 6.0 GB |
| 60 s, warm | **30** | 0 | 6.9 | 0.2 | 1.9 | 18.8 | 1.0 | 7.2 GB |

Warm means the worker's models are still loaded from the previous song (until five idle minutes pass). A cold start is
mostly loading from disk; on this PC it is slow because system memory is short. The first real song, before these
changes (planner on the PyTorch backend, full-precision model spilling into shared memory beside 5.6 GB of other roles),
took 705 s for 30 s.

**In the host role, set up through Martlet** (Docker Desktop on the same RTX 4070 PC, its WSL VM 15.6 GB of memory shared
with the speaking, listening and lip-sync roles, which hold about 7 GB of the card; MCP `singing_check` through the host's
own gateway with the Jane Doe voice from the shared voice list; seconds):

| Song | Total | Loading | Music | Lyric timestamps | Separating | Voice match (loading, pitch, converting) | Mixing + aligning | Worker peak VRAM |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| SoulX-Singer, 30 s, new worker | 110 | 23.6 | 11.4 | 5.6 | 3.3 | 45.9 (18.0, 14.2, 9.2) | 15.0 | 5.1 GB |
| SoulX-Singer, 30 s, next song, new worker | 95 | 26.1 | 18.3 | 5.5 | 2.2 | 35.2 (17.8, 3.2, 9.5) | 2.5 | 5.1 GB |
| SoulX-Singer, 30 s, music model kept, Chatterbox speaking meanwhile | **60** | 0 | 11.5 | 3.7 | 1.6 | 40.2 (18.3, 2.6, 14.0) | 0.8 | 5.2 GB |
| VevoSing, 30 s, new worker | 109 | 27.1 | 17.4 | 5.6 | 2.3 | 49.0 (21.5 loading, 11.9 converting) | 2.6 | 6.8 GB |
| VevoSing, 30 s, next song | 104 | 28.0 | 15.4 | 2.2 | 2.4 | 46.0 (19.6, 10.3) | 3.0 | 6.8 GB |

The music model loads with int8 weights there (the card never has 11 GB free beside the other roles). The VM is too short of
memory to keep the models between songs, so the worker usually ends after each song and the next one loads again (about
25 s plus 18-21 s for the voice match). When the card had room for the music model to stay (the other roles were holding
4.4 GB rather than 7 GB), the next song took 60 s. Setting the role up there through Companion › Singing took
25 minutes for the first attempt, which stopped at Whisper base (fixed since: the image left a root-owned folder in the
models volume), then 9 minutes to finish (it rebuilt the image and checked the files already downloaded). Add VevoSing
there took 14 minutes, including about 4 minutes rebuilding the image. Downloads ran at about 15 MB/s. The models volume
holds 15.9 GB (SoulX-Singer) or 20.3 GB (with VevoSing), and the image 14.7 GB. On Windows, Docker Desktop's disk image
(on C: by default) only grows, so leave room: each image rebuild added about 4 GB of build cache there, and a rebuild
after `docker builder prune` downloads PyTorch again (22 minutes).

## Does Singing need its own graphics card?

No. It shares a card with the speaking, listening and lip-sync roles. The graphics card is used only while a song is being
made, one stage at a time: the music model with int8 weights peaks at about 5 GB, SoulX-Singer at 2.1 GB, VevoSing at
6.8 GB. Five idle minutes later (or right after the song when system memory is short) the worker ends and frees the
card. It needs an NVIDIA card with 6 GB or more and about 5-7 GB free while a song is made. While a song is made, it
competes with the speaking and listening roles and with a local Thinking model.

Measured on the RTX 4070 (12 GB) beside Chatterbox, Whisper large-v3-turbo and Audio2Face:

- **Nothing failed.** With the other roles holding 7.0 GB, a song took the card to 11.7 GB of
  12.3 GB. Windows spilled 0.2-0.36 GB into shared system memory, which is slower than the card's own memory. With them
  holding 4.4-5.3 GB, the card peaked at 10.3 GB and the spill stayed under 0.1 GB.
- **Chatterbox speaking while a song was made** (a reply of about 5 s of audio every 5 s: 11 while the song was being
  made, 11 after): time to the first audio stayed 0.42-0.80 s (0.48-0.80 s with no song). Whole replies took 2.5-4.6 s
  instead of 2.3-3.3 s: up to 1.5 s longer when a reply overlapped the voice match. The song's voice conversion took
  14.0 s instead of 9.3 s. Spoken replies stay prompt; a song is a little slower.
- A local Thinking model on the same card makes this tighter. With a few GB less free, more spills into shared memory
  and everything slows. A second computer's card (pick it in the card's pills) is best when this one is busy, and Singing
  can run on a computer that does nothing else.

## Why this pipeline

A feasibility spike on 2026-10-03 (RTX 4070 12 GB, shared with other running models) compared the options before the
role was built:

- ACE-Step does not copy an arbitrary voice from a short clip (its LoRA needs about eight songs), so a separate singing
  voice conversion matches the voice afterwards. RVC needs minutes of training audio per voice and Seed-VC is GPL-3.0 and
  archived ([Voice Studio](VOICE_STUDIO.md)).
- With a 6.1 s speech recording as the only reference, the speaker-embedding similarity (SpeechBrain ECAPA cosine) of the
  singing to the voice rose from about 0.1 (ACE-Step's own singer) to 0.34-0.48 with SoulX-Singer-SVC (cfg 3) and
  0.40-0.46 with VevoSing, above ECAPA's same-speaker threshold (0.25) and discriminative (converting the same song to
  other voices scored high for those voices and near zero for this one). That is "a singer with that voice's timbre",
  not certainly the person; listen before relying on it.
- SoulX-Singer kept the melody (0.81-0.82 of frames within half a semitone) and the words best; VevoSing was a little
  closer to the voice on two of three songs and cleaner, but drifted off-key more (0.66-0.75) and its weights are
  non-commercial. So SoulX-Singer is the default and VevoSing a choice.
- ACE-Step's SFT model (50 steps) sang the words much more clearly than turbo (8 steps) for a few more seconds of
  generation: the **High quality** choice.

## Licences and consent

| Part | Source | Licence |
| --- | --- | --- |
| Music model code | [`ace-step/ACE-Step-1.5`](https://github.com/ace-step/ACE-Step-1.5) at commit `ca1e85f` (with its `nano-vllm`), fetched by the image build and checked by commit | MIT |
| Music models | [`ACE-Step/Ace-Step1.5`](https://huggingface.co/ACE-Step/Ace-Step1.5) revision `19671f4` (turbo DiT, VAE, Qwen3-Embedding-0.6B text encoder), [`ACE-Step/acestep-5Hz-lm-0.6B`](https://huggingface.co/ACE-Step/acestep-5Hz-lm-0.6B) `148d8ea` (planner), [`ACE-Step/acestep-v15-sft`](https://huggingface.co/ACE-Step/acestep-v15-sft) `c410d24` | MIT |
| Separator | [Demucs](https://github.com/facebookresearch/demucs) 4.0.1 and the `htdemucs_ft` vocals model `04573f0d-f3cf25b2.th` (84 MB) from dl.fbaipublicfiles.com | MIT |
| Voice match | [`Soul-AILab/SoulX-Singer`](https://github.com/Soul-AILab/SoulX-Singer) at commit `81aeb3a`; [`Soul-AILab/SoulX-Singer`](https://huggingface.co/Soul-AILab/SoulX-Singer) `model-svc.pt` (2.8 GB) and the RMVPE pitch model from `Soul-AILab/SoulX-Singer-Preprocess`; [`openai/whisper-base`](https://huggingface.co/openai/whisper-base) (its content encoder) | Apache-2.0 |
| VevoSing (optional) | [`open-mmlab/Amphion`](https://github.com/open-mmlab/Amphion) at commit `26f6883` (code); [`amphion/Vevo1.5`](https://huggingface.co/amphion/Vevo1.5) `f4053ca` (tokenizer, flow-matching transformer, vocoder); OpenAI Whisper medium | code MIT; **weights CC-BY-NC-ND-4.0** (personal, non-commercial use only; nothing made from them may be shared as a derivative); Whisper MIT |

Every model file is pinned by size and SHA-256 in `workers/singing/martlet_singing/pins.py` (about 16 GB without
VevoSing). Nothing is downloaded until the owner sets the role up: its terms (shown by Martlet before **Set up**, whose
click confirms them, and by `martlet-host add singing`) name every download, source and licence, VevoSing's only when it
is chosen. The voice-rights confirmation each recording already carries is the owner's assertion, not legal clearance:
do not use singing to imitate people without their permission or to deceive. ACE-Step asks that songs be checked for
originality and disclosed as AI-generated. SoulX-Singer's preprocessing bundle also carries a karaoke separator whose own
model card states no licence and a GPL-3.0 dereverb model; Martlet uses neither (Demucs separates instead).

## How it runs

- **Host role `singing`** (`deploy/host/roles/singing`, port **50085**, NVIDIA GPU with 6 GB+). The image is built on the
  host from `workers/singing/host/Dockerfile`: `python:3.11` with hash-locked PyTorch 2.10 CUDA 12.8 and ACE-Step's own
  locked dependencies (`host/requirements.lock`, from ACE-Step's `uv.lock` plus SoulX-Singer's, Demucs' and VevoSing's
  modules), then the pinned sources. SoulX-Singer and VevoSing were written for Transformers 4.4x, while ACE-Step needs
  4.57 (whose Llama attention requires rotary embeddings SoulX-Singer does not pass), so the voice-match stage runs in its
  own process with a hash-locked Transformers 4.46.3 overlay (`host/requirements-match.lock`, `MARTLET_SINGING_MATCH_SITE`;
  `MARTLET_SINGING_MATCH_PYTHON` names another interpreter instead). Its own container and volume
  (`martlet-singing-models`). It is not a voice engine (`exclusive=voice` is not set): it runs beside the speaking role.
- **Provisioning** (`martlet-singing provision`) downloads and verifies the pinned files. **Nothing is downloaded at run
  time:** `HF_HUB_OFFLINE`, ACE-Step's downloader is replaced by a refusal, Whisper base sits in the Hugging Face cache
  layout and Whisper medium where `whisper.load_model` looks.
- **Service** `martlet_singing.host` listens on 127.0.0.1:50085 only. A song is a **job**: `POST /jobs` starts one (the
  request and the voice's recording), `GET /jobs/<id>` reports its stage and progress, `GET /jobs/<id>/tracks/<track>`
  pages a finished track's PCM out, and `POST /jobs/<id>/cancel` stops it. One song is made at a time and up to four more
  wait in order (`singing.busy` beyond that); finished songs are kept for 30 minutes. `GET /status` reports the state,
  engine, pinned models, voice matches set up, the queue and the graphics card's memory.
- **Worker process** `martlet_singing.worker` starts with the first song and runs the stages **one at a time**. The
  graphics card's free memory is read from nvidia-smi (every process; on Windows CUDA's own figure ignores the others and
  spills into shared memory instead of failing):
  - The music model loads with **int8 weights** unless the card has 11 GB free (a full-precision song peaked at 9.6-10.7 GB,
    int8 at 6-7.2 GB; `MARTLET_SINGING_QUANTIZATION`: auto, none, int8_weight_only).
  - It **stays on the card** from the music through the lyric timestamps when the card has room for it plus 4.5 GB, instead
    of moving there and back for each; otherwise ACE-Step's CPU offload moves it per call.
  - After the music it **stays warm on the card** while 3 GB remain free for separating and matching; otherwise it waits in
    system memory when more than 8 GB is available, or is released.
  - The voice match runs in `martlet_singing.match`, a child process with its own Transformers that keeps SoulX-Singer in
    system memory (on the card only while converting) for the next song while more than 4 GB of system memory is
    available; VevoSing's process ends after each song.
  - `MARTLET_SINGING_KEEP_MODELS` (auto, keep, release) overrides the system-memory rules. The worker is **restarted** for
    the next song if it dies (the song it was making fails with that reason), and **exits after five idle minutes**
    (`MARTLET_SINGING_IDLE_SECONDS`), which frees the card and system memory for the speaking and listening roles. When
    system memory was too short to keep the music model after a song, the worker ends right after that song (a released
    model doesn't always give its memory back, and loading it again beside it once ran the system out of memory), so the
    next song starts a new worker; the worker and voice-match processes end without the interpreter's teardown, which
    could abort and leave a 4 GB core dump (WSL keeps them under `%TEMP%\wsl-crashes` on Windows). ACE-Step's
    own fallback to a CPU decode when the card looks full is not used.
- **Stages** (reported as progress): loading, writing the music (ACE-Step 1.5 turbo, 8 steps, or SFT, 50 steps for High
  quality; its 0.6B planner, which rewrites the caption and plans tempo, key and audio codes, runs only for a request with
  neither a tempo nor a key, since Martlet's own model writes them and the planner took 35-320 s on the PyTorch backend:
  `MARTLET_SINGING_PLANNER` auto, none or lm), separating (Demucs vocals; backing = song minus vocals),
  matching the voice (RMVPE pitch, SoulX-Singer-SVC cfg 3 and 32 steps in fp16, shifted only by whole octaves so the
  backing never needs re-pitching, toward about four semitones above the voice's speaking pitch, so a singer already in
  the voice's range is not moved: an octave too high costs most of the likeness), mixing (the matched
  vocals, silenced outside the phrases the separated original sings (with 20 ms fades) so no backing bleed or conversion
  noise reaches the vocals track, levelled to the original's loudness over the backing), aligning (below).
- **Aligning:** ACE-Step's own lyric timestamps (LRC, one decoder pass over the same generation, timed separately as
  `lyric_timestamps`) are matched to the request's lines in order and each start is snapped to the matched vocals' onset;
  without usable LRC, or when its words sit more than a second from any vocal onset (as when a short song crams its
  lines), the vocals' phrases (energy above a threshold) are used. librosa's beat tracker on the backing gives the tempo,
  which is halved or doubled toward the planned or requested tempo, then a straight grid is fitted and extended over the whole song; the downbeat is the beat in
  the bar with the most low-frequency onset strength, and the bar length is ACE-Step's time signature. **Words** come
  from ACE-Step's own alignment of the same generation (its token and sentence timestamps behind the LRC): a word starts
  with its first token and ends with its last, and each start is snapped to a vocal onset within 120 ms. The result says
  which source timed the words (`ace-step-alignment`, or `vocal-phrases`, the line spread evenly, when the alignment is
  unavailable) and the host reports the median distance from word starts to vocal onsets as a sanity check.
- **FIXTURE - NOT AI** engine (`martlet-singing provision --fixture`): a deterministic sine melody over chord pads with a
  click on every beat, lines on downbeats, for plumbing checks without a GPU.

## Gateway

`Martlet.Gateway.Singing.SongRelayWorker` relays route **`martlet.gateway.song.v1`** at `/martlet/v1/inference/song`
(contract `martlet.song-relay` 1.0, role `voice`, model `ace-step-v15-soulx-svc` pinned to ACE-Step's revision and turbo
weights) to the loopback service. Each request is one operation, answered with JSON text events:

| Operation | Payload | Answer |
| --- | --- | --- |
| `start` | `lyrics`, `style`, `duration_seconds` (15-180), `language`, `quality` (`fast`, `high_quality`), `voice_match` (`soulx`, `vevosing`), `voice_id`, optional `bpm`, `key`, `seed`, `reference_audio_base64` | `{job_id, state, queue_position}` or `{error: {code, summary}}` |
| `status` | `job_id` (without it: the service's status) | `{state, stage, fraction, queue_position, error?, result?}` |
| `result` | `job_id`, `track` (`mix`, `vocals`, `backing`), `offset_frames`, `maximum_frames` | one page (at most 4.8 MB of PCM) as events of `{track, sample_rate, channels, total_frames, offset_frames, frames, pcm_base64}` |
| `cancel` | `job_id` | `{job_id, state}` |

The voice is named by its ID in the shared speaking-voice list (`SpeakingVoice.Id`); the gateway hands the service that
voice's recording from its own copy (`voice.missing` for a voice the list lacks, `reference.missing` when the host has
no copy yet, after which the client sends it once with `reference_audio_base64`, checked against the voice's SHA-256).
No path or URL is ever accepted. Host configuration kind: `singing`.

## Desktop

**Companion > Singing** (an optional extra, on its own page) has the standard page order:

1. **Now**: whether Martlet sings, where and with what, and any problem (no voice to sing with).
2. **Where Martlet sings**: the singing pool list ([below](#the-singing-pool)), the shared list control of every pooled
   area. It holds the computers that make songs, in order: this PC, your paired computers (not hosts friends share, and
   only those that can run Singing) and single graphics cards. There is no separate Off: with no computer in the list on,
   Martlet doesn't sing, and the role stays set up on your computers. Each computer's **Settings** say where Singing
   stands there (not set up, setting up, ready with the voice matches set up there, failed with the reason, or why that
   computer can't sing), offer **Set up Singing here** (after a confirmation naming the downloads, licences and terms) or
   **Add VevoSing here**, and choose that computer's own **Quality** (the song choice, Fast or High quality). A computer
   added that doesn't sing yet opens its Settings. The list is `pools.json`, shared with your other computers.
3. **What Singing is**: the role's facts compared with the catalog's numbers: an NVIDIA graphics card, about 5.1 GB of
   graphics memory and up to 7.2 GB (a 6 GB+ card), the download (about 31 GB with the image), about 1 to 2 minutes for
   a 30-second song, free, the licences, and that the lyrics, the style and the voice's recording go to the computer that
   sings; and whether Singing needs a graphics card of its own ([below](#does-singing-need-its-own-graphics-card)).
4. **Song choices**: **Quality** (Fast, High quality) and **Voice match**, a second option picker that compares
   SoulX-Singer (the default; Apache-2.0, any use) and VevoSing (closer to the voice but may drift off-key;
   CC-BY-NC-ND-4.0, personal, non-commercial use only). Both are kept in `singing.json` on this PC. A computer with its
   own quality in the list uses that instead.

The first time the page opens, it makes the list once from the older choice (the computer Martlet sang on, then your
other computers that run Singing; all off when singing was off), so nothing chosen is lost. It does this only when this
PC sang on one of your computers: an empty list made here would turn singing off on your other computers.

There is no play button: Martlet performs its songs itself in conversation. The Devices map lists the role as "Singing"
("Ready. Martlet makes its songs here when you ask it to sing.").

### Setting it up

Singing installs like every other role, through `martlet-host add singing`:

- **Set up Singing here** (this PC) opens the same run window as a voice engine: Docker Desktop and the host service first when this
  PC has none, then the role. Its status line follows the role's own progress: building the singing image (its steps),
  starting the service, then each pinned model file's download ("Singing: downloading model-svc.pt, 45% of 2730 MiB...").
  On another computer the run goes through Martlet there or SSH, as for any role. The computer's Settings say *Setting
  Singing up here...* until the gateway offers the song route and the singing service answers set up, then *Singing is
  ready here with SoulX-Singer.*; a failed run says why and Set up tries again (downloads already verified are kept).
- A plain Set up installs **ACE-Step, Demucs and SoulX-Singer only** (`SINGING_VOICE_MATCHES=soulx`, about 16 GB). The
  page reads the voice matches set up on each computer in the list from its singing service, through the gateway.
- **VevoSing is optional.** Using it under Voice match while no computer in the list has it says so ("Songs use
  SoulX-Singer until you add it"). A computer's Settings offer **Add VevoSing here**, with its own confirmation naming CC-BY-NC-ND-4.0
  (personal, non-commercial use only) and its downloads (Vevo1.5 and Whisper medium, about 4.5 GB), which happen only
  then (`martlet-host add singing` again with `soulx-vevosing`; SoulX-Singer's files are kept). A VevoSing song goes to a
  computer in the list with VevoSing; with none, it is sung with SoulX-Singer instead.
- The Devices map's *Install Singing* and the Martlet hosts window's role cards run the same `martlet-host add singing`
  with the role's install dialog (`martlet-host describe singing`: its terms, the model and voice-match choices with
  SoulX-Singer preselected, and VevoSing's terms only when it is chosen). *Remove Singing* stops it and keeps the
  downloads.
- The host image carries the singing sources (`/opt/martlet/source/workers/singing`) the role's image is built from,
  like every role that builds on the host; the role's image tag (`martlet-singing:2`) is bumped whenever those sources
  change, so `martlet-host update` (Martlet updating its hosts) rebuilds it.

For other code (the conversation's `sing_song` tool), `Martlet.Core.Singing` holds the contract: `ISongMaker`
(`GetAvailabilityAsync`, `GenerateAsync(request, progress, cancellationToken)`), `SongRequest`, `SongProgress`,
`SongResult` (mix, vocals and backing tracks, `LyricTimestamps` with sections, `Words` with `WordTimingSource`, `Bpm`,
`BeatsPerBar`, `Beats`, `Downbeats`, `StageTimings`), `SongLyrics.Parse`, `SongException` codes and the FIXTURE - NOT AI
`FixtureSongMaker`. The desktop's implementation is `SongClient` (`SongClient.For(dataDirectory)`, also
`MainWindow.SongMaker`); `MARTLET_SINGING_FIXTURE=1` makes it the fixture. `SongClient.IsSetUp(dataDirectory)` answers
without the network whether singing is set up (the fixture is on, or the singing pool list has a member on that is paired
here, or, without a list yet, a computer still paired with this PC ran Singing when
the desktop last checked its computers: `host` in `singing.json`, kept while that computer is unreachable and cleared once
it answers without Singing), `SongClient.SpeakingVoiceId(dataDirectory)` is the voice Martlet speaks with, and
`SingingPreferences.Load(dataDirectory)` holds the card's choices.

### The singing pool

Songs go to a pool of the owner's own computers that run the singing role, not to one computer (`SingingPool` in
`Martlet.Core.Singing`, used by `SongClient`). Hosts that friends share are never in it.

1. **Members.** With a singing pool list (the `singing` area of `pools.json`,
   [Pools](CLUSTER.md#pools-one-ordered-list-of-members-per-area)), its members that are on, kept for this PC and paired
   here, in the owner's order: `this-pc` is this PC's own host service, a card is its computer. An empty list, or one with
   no member on, is off. Without a list yet, the older choices: the computer Martlet sings on (`host` in `singing.json`)
   first, then the computers the shared plan says run Singing (fewest plan jobs first), then the other paired computers
   (`SongClient.Members`).
2. **Choosing.** The song goes through `WorkQueue.Shared` (lane `singing`, background priority; the member key is the
   computer). Each member in order is asked for its singing status. The first that sings nothing now and has the song's
   voice match takes it; while the first member is free, nothing else is asked.
3. **Passed over.** A member that sings another song, doesn't answer, doesn't sing, isn't set up, or lacks the voice
   match (VevoSing is set up only on some computers) is passed over for the next.
4. **All busy.** The song waits in line on the member with the fewest songs before it (the first of those in order).
   The singing host keeps the line, so this PC doesn't ask again and again. A full line (`singing.busy`) passes the song
   to the next; when every line is full the song fails with `singing.busy`.
5. **During the song.** A member that stops answering is replaced by the next, and the song is made again there. A song
   that fails on its computer (`song.failed`) is not made again elsewhere. The voice recording goes to whichever member
   takes the song, when it lacks it.
6. **Availability.** `GetAvailabilityAsync` reads every member's status at once. Its host is where the next song would
   go (`SingingPool.Pick`), and its voice matches are those set up on any member, so a VevoSing song goes to a member
   with VevoSing.

The song job holds the member that makes the song from background thinks (it starts with the computer Martlet sings on
and moves when `SongProgress.Host` names another). The desktop log says where each song went
(`Singing: m4-host makes the song (number 2 of 3 in the singing pool).`, and `Sharing work: singing went to host:m4-host
(1 busy).` when the first member was passed over). A member with its own quality (`quality` in its settings: `fast` or
`high_quality`, `SingingPool.Quality`) makes the song in that quality; without one, the song choice. Every member that
sings is kept from background thinks (`BackgroundDuties`).

## Verification

- `python -m unittest discover -s tests -t .` in `workers/singing` (with `PYTHONPATH=workers/singing`) runs the service
  with the fixture: every stage, aligned paged tracks, sections and beat grid, refusals, cancel, queue, the idle release
  and the restart of a killed worker.
- `tests/Martlet.Gateway.Tests/SongRelayTests.cs` makes a song through a real gateway and paired client against a
  controlled service: voice resolution (and the one-time recording upload), progress, paging, failures, cancellation and
  the loopback-only relay. `tests/Martlet.Core.Tests/SongContractsTests.cs` checks the contract and the fixture.
- MCP `singing_check` makes a song through the production path (fixture service, or a live one on loopback, or with
  `dataDirectory` a real paired host through its own gateway, as the desktop does); `singing_status` reads a live service,
  and with a paired data directory each host's singing through its gateway, the singing pool (`pool`: the list or the
  older choices, each member's verdict now and where the next song goes) and this PC's Docker side of the role (a setup
  in progress included). `singing_pool_check` rehearses the pool with simulated computers. See [MCP](MCP.md).
- `tests/Martlet.Core.Tests/SingingPoolTests.cs` and `tests/Martlet.Desktop.Tests/SingingPoolMembersTests.cs` check the
  pool's order, list, choices, lines and failures. Songs between real hosts in a pool are **NOT RUN**.

## Singing in conversation

"Martlet, sing me a song": Martlet says it'll work on it, makes the song in the background while the conversation goes on,
brings it up when it's ready and sings it when you say yes. The full behaviour (tools, lead-ins, stops, resuming, lip sync,
latency) is in [Conversation › Singing in conversation](CONVERSATION.md#singing-in-conversation); in short:

- Replies get `sing_song`, `play_song` and `stop_singing` while `SongClient.IsSetUp` says singing is set up (and the
  Thinking route does function calling). A song job (`song-1`, one at a time, 4 an hour) writes the lyrics on Deep thinking
  when there is no lyrics argument, then calls `SongClient.For(dataDirectory)` with the voice Martlet speaks with
  (`SongClient.SpeakingVoiceId`) and the card's quality and voice match (`SingingPreferences.Load`), times the mouth to the
  vocals and keeps the song as a `song` creation ([Creations](CREATIONS.md)): FLAC mix, vocals and backing, its map (lines,
  words, beat grid) and mouth track.
- Only Martlet performs songs (`play_song`, or `perform_creation` with the same `from`): start, resume, a section, `line:N`
  or a time. Anywhere but the top, the band comes in on a downbeat a bar or two before the line with an equal-power fade-in
  and the vocals wait for the line; while Martlet still talks, the band vamps on that bar.
- `stop_singing`, "stop singing" (with Martlet's name or a song word), Stop singing and the talk button stop musically (the
  word ends, the band rings to the next beat); Esc in 300 ms. Where it stopped and why goes at the end of the conversation,
  and `resume` restarts that line.
- The mouth follows the vocals only, from a track made once per song: Audio2Face over the vocals when it answers, else
  visemes from `SongResult.Words`, else loudness, played on the song's own clock. Captions follow word by word.
- MCP: `songs_status` and `song_playback_check` ([MCP](MCP.md)).
- **After the reply.** While the After each exchange check-in ticks the Songs, pictures and creations
  [tool set](CONVERSATION.md#check-in-tool-sets) (`songs-pictures`) and the Thinking pool has a member that calls tools,
  the reply gets only `stop_singing` and a short line instead of `sing_song`, `play_song` and the Singing prompt: it says in
  a few words that it will sing. The check-in then calls `sing_song` or `play_song` on the Thinking pool, with the same
  arguments, limits and gates. Its lyrics continue the reply's request, as the reply's own call does. A song that can't start
  (singing off, busy, the hourly limit) is brought up by Martlet on its own.
