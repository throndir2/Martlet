# Martlet singing worker

The `singing` host role's loopback song service: ACE-Step 1.5 writes the music, Demucs separates the vocals, SoulX-Singer-SVC
(or, optionally, VevoSing) matches the singing to a voice from the shared voice library. See [Singing](../../docs/SINGING.md).

- `martlet_singing/host.py`: the HTTP front (`serve`, `provision [--fixture]`): song jobs (start, status, track pages,
  cancel), one at a time with a waiting queue; it starts `martlet_singing/worker.py` as a separate process with the first
  song, restarts it if it dies and stops it after an idle period (`MARTLET_SINGING_IDLE_SECONDS`, default 300).
- `martlet_singing/engines.py`: the real pipeline (each stage moves its model onto the graphics card and back) and the
  FIXTURE - NOT AI tone engine.
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