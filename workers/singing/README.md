# Martlet singing worker

The `singing` host role's loopback song service: ACE-Step 1.5 writes the music, Demucs separates the vocals, SoulX-Singer-SVC
(or, optionally, VevoSing) matches the singing to a voice from the shared voice library. See [Singing](../../docs/SINGING.md).

- `martlet_singing/host.py`: the HTTP front (`serve`, `provision [--fixture]`): jobs (start, status, track pages,
  cancel), one at a time with a waiting queue; it starts `martlet_singing/worker.py` as a separate process with the first
  job, restarts it if it dies and stops it after an idle period (`MARTLET_SINGING_IDLE_SECONDS`, default 300).
  `provision` downloads the choices' models: `SINGING_VOICE_MATCHES` (`soulx` or `soulx-vevosing`) and
  `SINGING_MUSIC_MODELS` (`standard`, the default, or `standard-xl`, which adds ACE-Step 1.5 XL SFT: about 20 GB more).
  `GET /status` lists the job kinds (`"kinds"`) and the music models set up (`"music_models"`: `ace-step-v15` and, with
  `standard-xl`, `ace-step-v15-xl`; `[]` before provisioning). `POST /jobs` takes a `kind`:
  - `song` (the default): the whole song. Tracks `mix`, `vocals` (the matched voice) and `backing`.
  - `music`: the music only, with no voice (`lyrics`, `style`, `duration_seconds`, `language`, `quality`, optional
    `bpm`, `key`, `seed`). Tracks `mix`, `vocals` (the separated vocals, mono) and `backing`, with the beat grid and the
    lyric and word times.
  - `match`: the voice match only (`voice_match`, `voice_id`, `reference`, `input_frames` up to 200 s). The job starts in
    state `receiving`. Send the vocals (signed 16-bit little-endian mono PCM at 48 kHz, 2 bytes to 4.8 MB a page) with
    `POST /jobs/<id>/input?offset=<frames already received>`. The job queues when all `input_frames` arrived and fails
    when they do not arrive in 300 s (`MARTLET_SINGING_RECEIVE_SECONDS`). Its only track is `vocals`, the same length.
  - `separate`: a finished song made elsewhere (for example by a cloud music service), separated and timed here
    (`lyrics`, `language`, `input_frames` up to 200 s, `input_channels` 1 or 2, optional `bpm`, `key`). It receives its
    song like a match job: interleaved signed 16-bit little-endian PCM at 48 kHz with `input_channels` channels, in whole
    frames up to 4.8 MB a page. Demucs separates it, and the beat grid (at `bpm` when given) and the lyric and word times
    come from its vocals' phrases. Tracks `mix`, `vocals` (mono) and `backing`, as long as the input.

  `song` and `music` jobs take an optional `model`: `ace-step-v15` (the default: turbo, or SFT for `high_quality`),
  `ace-step-v15-xl` (ACE-Step 1.5 XL SFT, always 50 steps), or an added model: `heartmula`, `diffrhythm` or `yue`
  (`singing.model_unavailable` when it is not set up). `song` and `match` jobs take a `voice_match`: `soulx`, `vevosing`,
  `vevo2` or `seedvc` (`singing.voice_match_unavailable` when it is not set up). The result's `engine.model` names the
  model used. The added models are set up with `SINGING_HEARTMULA`, `SINGING_DIFFRHYTHM`, `SINGING_YUE`, `SINGING_VEVO2`
  and `SINGING_SEEDVC` (`yes` or `no`; build arguments and provisioning choices).

  The desktop can then make a song on two computers: a `music` job on one, a `match` job with its vocals on another, and
  it mixes the matched vocals over the backing itself.
- `martlet_singing/engines.py`: the real pipeline (each stage moves its model onto the graphics card and back; the
  `music` and `match` kinds run its first and second half, `separate` the separation and timing) and the FIXTURE - NOT AI
  tone engine. One music model is loaded at a time; it loads with int8 weights unless the card has 11 GB free (XL: 22 GB).
- `martlet_singing/timing.py`: the beat grid (fitted to the backing's onsets, ACE-Step's planned tempo preferred) and the
  lyric lines' sung starts (ACE-Step's LRC snapped to the vocals' onsets, or the vocals' phrases).
- `martlet_singing/pins.py`: the pinned source commits and model files (sizes, SHA-256, licences).
- `martlet_singing/extras.py`: the added models, each in its own process (one song per process) with its own packages
  (`host/requirements-<name>.lock`, in `/opt/martlet-singing/extras/<name>`): HeartMuLa, DiffRhythm (with lyrics timed as
  LRC by Martlet) and YuE2 write music; Vevo2 and Seed-VC match the voice.
- `martlet_singing/shims/pyworld.py`: stands in for pyworld, which Amphion imports but VevoSing never calls.
- `host/Dockerfile`, `host/requirements.in`, `host/requirements.lock`: the hash-locked image the role builds;
  `host/requirements-match.in`/`.lock`: the Transformers 4.46.3 overlay only the voice-match process
  (`martlet_singing.match`) uses.
- `tests/test_host.py`: plumbing checks with the fixture engine
  (`python -m unittest discover -s tests -t .` in `workers/singing` with `PYTHONPATH=workers/singing`; standard library only).

Run it outside a container with `MARTLET_SINGING_ROOT` (models and config), `MARTLET_SINGING_PORT`,
`MARTLET_SINGING_SOURCES` (a folder with `ace-step`, `soulx-singer` and `amphion` checkouts at the pinned commits) and
`MARTLET_SINGING_DEVICE` in an environment that has the lock's packages.