# Martlet.Perception

Isolated P01 selected-window capture and P02 OCR/VLM worker contracts,
gateway-adapter boundary and latest-frame resource scheduler.
Production native capture is intentionally unavailable; only friend
test/qualification code can inject the selected-window native interface.

Reused from original P01 commit
`29b50b1b3638be490980a1a50367afc7b6a44c40`, with deterministic regression fixes
for per-copy consent/freshness, rollback-safe remaining frame age, owner-scoped
enumeration consent, cancellation before source publication and immediate
frame invalidation on native failure before cleanup. Device pairing
does not authorize capture.

P02 additions reuse `9e117c10ac000a0392b8d7fd5e20c0d66ba6b822` followed by
the failure-domain split `4ef7eac9e82c08fe452fd59fdc32456b7b7f4f7a`, with
bounded allocation/enumeration, immutable output, redacted diagnostics,
rollback-safe freshness, late-publication and scheduler-retirement fixes.
P01 source files remain unchanged. Inner worker protocol 1.0 is distinct from
permanent Gateway authentication protocol 2; no auth client is included.

`DeterministicPerceptionWorkerTransport` is a synthetic fixture, **not AI**.
No production capture adapter, P01-to-worker bridge, Python worker, network,
model download, application wiring or installation auto-enablement is supplied.
Per-action capture/disclosure/vision consent, native privacy and runtime/rights
qualification remain mandatory; optional perception failure must not block voice.

See [the contracts, synthetic evidence and remaining gates](../../docs/PERCEPTION.md).
