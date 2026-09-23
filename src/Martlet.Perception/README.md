# Martlet.Perception

Isolated P01 selected-window capture contracts and managed ownership engine.
Production native capture is intentionally unavailable; only friend
test/qualification code can inject the selected-window native interface.

Reused from original P01 commit
`29b50b1b3638be490980a1a50367afc7b6a44c40`, with deterministic regression fixes
for per-copy consent/freshness, rollback-safe remaining frame age, owner-scoped
enumeration consent, cancellation before source publication and immediate
frame invalidation on native failure before cleanup. Device pairing
does not authorize capture. No later native adapter or worker is included.

See [the P01 boundary and remaining gates](../../docs/PERCEPTION.md).
