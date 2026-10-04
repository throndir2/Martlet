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
    (`MARTLET_SINGING_IDLE_SECONDS`), which frees the card and system memory for the speaking and listening roles. ACE-Step's
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

**Companion > Voice > Singing** (below the voice engine) works like a voice engine row: chips (NVIDIA GPU 6 GB+, Docker,
sings in your cloned voice, with backing music, a few minutes per song, the licences), where it stands on the shown
computer (not set up, setting up, ready, failed with the reason, or why that computer can't sing), and one **Set up**
button for this PC or the computer picked in its pills, after a confirmation naming the downloads, licences and terms.
**Quality** (Fast, High quality) and **Voice match** (SoulX-Singer, VevoSing) are kept in `singing.json`; choosing VevoSing
where only SoulX is set up offers **Add VevoSing there**. There is no play button: Martlet performs its songs itself in
conversation. The Devices map lists the role as "Singing".

For other code (the conversation's `sing_song` tool), `Martlet.Core.Singing` holds the contract: `ISongMaker`
(`GetAvailabilityAsync`, `GenerateAsync(request, progress, cancellationToken)`), `SongRequest`, `SongProgress`,
`SongResult` (mix, vocals and backing tracks, `LyricTimestamps` with sections, `Words` with `WordTimingSource`, `Bpm`,
`BeatsPerBar`, `Beats`, `Downbeats`, `StageTimings`), `SongLyrics.Parse`, `SongException` codes and the FIXTURE - NOT AI
`FixtureSongMaker`. The desktop's implementation is `SongClient` (`SongClient.For(dataDirectory)`, also
`MainWindow.SongMaker`); `MARTLET_SINGING_FIXTURE=1` makes it the fixture. `SongClient.IsSetUp(dataDirectory)` answers
without the network whether singing is set up (the fixture is on, or a computer still paired with this PC ran Singing when
the desktop last checked its computers: `host` in `singing.json`, kept while that computer is unreachable and cleared once
it answers without Singing), `SongClient.SpeakingVoiceId(dataDirectory)` is the voice Martlet speaks with, and
`SingingPreferences.Load(dataDirectory)` holds the card's choices.

## Verification

- `python -m unittest discover -s tests -t .` in `workers/singing` (with `PYTHONPATH=workers/singing`) runs the service
  with the fixture: every stage, aligned paged tracks, sections and beat grid, refusals, cancel, queue, the idle release
  and the restart of a killed worker.
- `tests/Martlet.Gateway.Tests/SongRelayTests.cs` makes a song through a real gateway and paired client against a
  controlled service: voice resolution (and the one-time recording upload), progress, paging, failures, cancellation and
  the loopback-only relay. `tests/Martlet.Core.Tests/SongContractsTests.cs` checks the contract and the fixture.
- MCP `singing_check` makes a song through the production path (fixture service, or a live one on loopback);
  `singing_status` reads a live service. See [MCP](MCP.md).

## Singing in conversation

Reserved for the conversation side (`sing_song`, playing a song while listening and stopping when asked), which builds
on the contract above.
