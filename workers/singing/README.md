# Martlet singing worker

The `singing` host role's loopback song service: ACE-Step 1.5 writes the music, Demucs separates the vocals, SoulX-Singer-SVC
(or, optionally, VevoSing) matches the singing to a voice from the shared voice library. See [Singing](../../docs/SINGING.md).

- `martlet_singing/host.py`: the HTTP front (`serve`, `provision [--fixture]`): jobs (start, status, track pages,
  cancel), one at a time with a waiting queue; it starts `martlet_singing/worker.py` as a separate process with the first
  job, restarts it if it dies and stops it after an idle period (`MARTLET_SINGING_IDLE_SECONDS`, default 300).
  `GET /status` lists the job kinds (`"kinds"`). `POST /jobs` takes a `kind`:
  - `song` (the default): the whole song. Tracks `mix`, `vocals` (the matched voice) and `backing`.
  - `music`: the music only, with no voice (`lyrics`, `style`, `duration_seconds`, `language`, `quality`, optional
    `bpm`, `key`, `seed`). Tracks `mix`, `vocals` (the separated vocals, mono) and `backing`, with the beat grid and the
    lyric and word times.
  - `match`: the voice match only (`voice_match`, `voice_id`, `reference`, `input_frames` up to 200 s). The job starts in
    state `receiving`. Send the vocals (signed 16-bit little-endian mono PCM at 48 kHz, 2 bytes to 4.8 MB a page) with
    `POST /jobs/<id>/input?offset=<frames already received>`. The job queues when all `input_frames` arrived and fails
    when they do not arrive in 300 s (`MARTLET_SINGING_RECEIVE_SECONDS`). Its only track is `vocals`, the same length.

  The desktop can then make a song on two computers: a `music` job on one, a `match` job with its vocals on another, and
  it mixes the matched vocals over the backing itself.
- `martlet_singing/engines.py`: the real pipeline (each stage moves its model onto the graphics card and back; the
  `music` and `match` kinds run its first and second half) and the FIXTURE - NOT AI tone engine.
- `martlet_singing/timing.py`: the beat grid (fitted to the backing's onsets, ACE-Step's planned tempo preferred) and the
  lyric lines' sung starts (ACE-Step's LRC snapped to the vocals' onsets, or the vocals' phrases).
- `martlet_singing/pins.py`: the pinned source commits and model files (sizes, SHA-256, licences).
- `martlet_singing/shims/pyworld.py`: stands in for pyworld, which Amphion imports but VevoSing never calls.
- `host/Dockerfile`, `host/requirements.in`, `host/requirements.lock`: the hash-locked image the role builds;
  `host/requirements-match.in`/`.lock`: the Transformers 4.46.3 overlay only the voice-match process
  (`martlet_singing.match`) uses.
- `tests/test_host.py`: plumbing checks with the fixture engine
  (`python -m unittest discover -s tests -t .` in `workers/singing` with `PYTHONPATH=workers/singing`; standard library only).

Run it outside a container with `MARTLET_SINGING_ROOT` (models and config), `MARTLET_SINGING_PORT`,
`MARTLET_SINGING_SOURCES` (a folder with `ace-step`, `soulx-singer` and `amphion` checkouts at the pinned commits) and
`MARTLET_SINGING_DEVICE` in an environment that has the lock's packages.