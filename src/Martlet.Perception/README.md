# Martlet.Perception

The worker contracts for OCR and visual question answering (VLM) on a host. The
gateway uses them for its perception inference routes. The library has no
screen capture, network client or model.

`DeterministicPerceptionFixtureIdentity` is a synthetic worker identity for
tests, **not AI**.

See [Perception worker contracts](../../docs/PERCEPTION.md).