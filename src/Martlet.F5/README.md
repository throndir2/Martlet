# Martlet.F5

Isolated H04a/H04b managed-code foundation for the versioned Martlet F5 worker,
reference preset store, gateway client adapter, and deterministic fake worker.
See [F5 worker and reference-voice foundation](../../docs/F5_VOICE.md) for the
wire/lifecycle bounds, validation command, evidence, and deferred gates.

This project has no external package dependency; it references Core for the
shared strict WAV inspector. The Desktop uses its reference preset store for the
voices a Martlet host's F5 role clones (Devices > the Speaking row's *Done by*), and
the gateway uses its contracts for the F5 route. It contains no Python, F5-TTS,
model weights, CUDA, Docker, network client, audio device integration, default
voice, automatic transcription, model download, or fallback route.
