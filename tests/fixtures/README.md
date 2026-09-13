# Synthetic provider fixture provenance

**FIXTURE - NOT AI.** `complete.json`, `refused.json`, and `no-speech.json`
contain text authored for Martlet F03a and fixed generated UUID metadata.
They contain no user conversation, recorded voice, copied upstream response,
audio, model weights, reference asset, secret, or real provider observation.
No external attribution/license is needed for a copied asset because none is
used. This provenance statement grants no project code or asset license; the
project's licensing/distribution decision remains with its owner.

These files are complete `FixtureScenario` inputs decoded by the production
Core serializer. The fixture tests compare their results byte-for-byte with
the corresponding deterministic catalog traces. The remaining 27 named
failure/pressure/temporal scripts live in `src\Martlet.Fixtures\FixtureCatalog.cs`.
Faults and the 120 interruption permutations exercise production
`Core\Streaming`, not an alternative state machine in a test helper.

See `src\Martlet.Fixtures\README.md` for API semantics, the complete scenario
catalog, limits, commands, and the precise partial AC-01/03/08 coverage.
No completed trace is evidence of actual AI, audio playback, installer
qualification or a passed M1/G1 gate.
