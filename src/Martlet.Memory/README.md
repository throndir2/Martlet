# Martlet.Memory

Portable `net10.0` P03a/P03b foundation for an explicit, consented local fact
store and bounded lexical retrieval. It has no project references or runtime
packages and is intentionally absent from Desktop, Core settings, conversation,
gateway, packaging and `Martlet.slnx`.

The public surface has no default path and no automatic ingestion:

1. `MemoryStoreActivationPreview.Create` is pure and defaults OFF.
2. A path-bound one-use `Allow` authorization is required to `Open`.
3. `SaveAsync`, `InspectAsync`, `EditAsync`, `DeleteAsync` and
   `PurgeExpiredAsync` are the only source/retention actions.
4. `RetrieveAsync` uses a bounded in-memory lexical index only.
5. `CreateExportPreviewAsync` freezes exact versioned bytes; destination-bound
   export authorization defaults to No and is one use.

The fixed store document is strict schema-1 JSON, atomically replaced from one
owned pending file under an exclusive local lock. Mutations rebuild the lexical
index, clear the revision-bound cache and invalidate in-flight results. No
backup, network, provider, embedding, vector database, credential field, logger
or transcript API exists.

See [the full contract, limits and remaining gates](../../docs/MEMORY.md).
