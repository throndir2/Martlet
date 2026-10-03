# Martlet Dia worker

The `dia` host role's loopback voice service (Nari Labs' Dia). See [Dia voice engine](../../docs/DIA_VOICE.md).

- `martlet_dia/host.py`: the HTTP front (`serve`, `fetch-source`, `provision [--fixture]`, `warm`); it starts
  `martlet_dia/worker.py` as a separate process, waits for it while it is busy and restarts it if it dies.
- `martlet_dia/pins.py`: the pinned Dia source commit and file hashes, model and codec files, and Dia's nonverbal cues.
- `martlet_dia/text.py`: single-speaker prompts (`[S1] <reference transcript> <reply>`; reply text is passed through
  unchanged apart from speaker tags, so cues such as `(laughs)` reach Dia).
- `host/Dockerfile`, `host/requirements.in`, `host/requirements.lock`: the hash-locked image the role builds.
- `tests/test_host.py`: plumbing checks with the FIXTURE - NOT AI tone engine
  (`python -m unittest discover -s workers/dia/tests -t workers/dia` with `PYTHONPATH=workers/dia`).

Run it outside a container (for example on CPU) with `MARTLET_DIA_ROOT` (models and config), `MARTLET_DIA_PORT` and
`MARTLET_DIA_DEVICE=cpu` in an environment that has the lock's packages and the fetched Dia source on `PYTHONPATH`.
