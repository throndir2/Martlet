# Martlet XTTS-v2 worker

The `xtts` host role's loopback voice service. See [XTTS-v2 voice engine](../../docs/XTTS_VOICE.md).

- `host/martlet_xtts_host.py`: the service (`serve`, `provision [--fixture]`, `warm`, `worker`).
- `host/Dockerfile`, `host/requirements.lock`: the hash-locked image the role builds.
- `tests/test_host.py`: plumbing checks with the FIXTURE - NOT AI tone engine
  (`python -m unittest discover -s workers/xtts/tests`).

Run it outside a container (for example on CPU) with `MARTLET_XTTS_ROOT` (models and config),
`MARTLET_XTTS_PORT` and `MARTLET_XTTS_DEVICE=cpu` in an environment that has the lock's packages.
