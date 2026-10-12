# Memory (P03a/P03b/P03c)

**Memory is ON by default.** Martlet remembers lasting things that are talked
about and recalls them in later conversations. New profiles, v1-v5 migrated
profiles and v4/v5 profiles whose memory was only OFF by the old default all
read as ON; an OFF saved under settings v6 is an explicit choice and stays OFF.
Configuration restore still forces memory OFF for review (like routes).

When memory is ON, each explicit conversation turn recalls saved facts
automatically, and after each completed reply the same Thinking model picks out
lasting facts to save (see [Automatic recall and remembering](#automatic-recall-and-remembering)).
Martlet also keeps a record of every conversation on this PC and brings back
what was said when you mention an earlier conversation (see
[Conversation history](#conversation-history)).
Martlet remembers the same things on every computer of yours: facts travel
through your paired hosts ([One memory on every computer](#one-memory-on-every-computer)),
like the rest of Martlet ([CLUSTER](CLUSTER.md)), and each computer keeps them in
its own local store. There is no embedding, vector database, cloud copy or
backup. Besides that sync, the only background work is the
[memory brief](#the-memory-brief): a short profile written from the facts. Screen and
camera glances are never remembered, and neither is
[what the PC plays](CONVERSATION.md#hearing-what-this-pc-plays): recall and
remembering read only the user's own words. This is internal functional evidence, not
completed remote P03, AC-16, P04 or G4 qualification. Memory-store
backup/restore, real-user usefulness/privacy comprehension, clean-machine and
physical crash/power-loss evidence remain separate gates.

The foundation was reused from `fc1fd57f59fed0899ccbd5991797f1699d4e9bfb`
in PR #39 with finalization-time expiry and consistent top-K ranking fixes.
This slice adapts only the memory delta of
`b80920ad2fde41fdb3456a992786f642a7e22887`, plus the named memory fixture argument
from `68bd6c8264a7215dc24f8fb8115d903910925cfb`, onto PR #44's canonical
schema-3 companion baseline. It preserves PR #39 fixes and PR #44 historical
receipt/snapshot semantics. Source avatar/listen-first dependencies are excluded;
no Gateway or installation schema/activation changes are included.
AC-16 is unchanged. Source package hashes are historical, not new evidence.

## Desktop enablement and fact management

**Memory (ON by default)** exposes an accessible management window. Opening it
loads the policy and, when memory is ON, lists the remembered facts newest
first with where each came from. The default scope is the app-owned `memory`
child of the selected Martlet local-data directory. A user may instead enter or
browse to a custom absolute local directory. The shared foundation rejects
roots, UNC/network/unknown-volume, alternate-stream and reparse/link scopes.
The enable checkbox turns memory on or off and saves at once (there is no Save
button; a typed custom folder saves when you leave the field or press Enter,
and settings from before memory existed record the default when the window
opens). Each save goes into the newest saved settings, so other changes made
meanwhile are kept. Configuration validation reads path metadata but creates
no directory, lock or store document.

Strict memory settings own `enabled`, the app-local/custom policy, optional
normalized custom path and a fresh configuration revision. Settings v6 marks
memory as ON by default: reading a v1-v5 file treats an OFF memory section as
the old default (ON) until the file is saved as v6, and v1-v5 migration is
explicit and atomic and creates the existing exact original-file snapshot.
Configuration backup includes only this enable/path policy, never facts/store/
exports. Restore accepts compatible v1-v6 settings but always forces memory OFF
for review: v4+ sources retain their storage policy; older sources preserve the
current policy. No restore action opens, copies or validates a fact store.
Changing configuration invalidates an in-flight app retrieval.

Facts come from two places: the dedicated editor (**Add fact**, with an
explicit `until_deleted` or 30/90/365-day expiry, whose it is
(*Belongs to*, see [Whose memories](#whose-memories)) and fresh `user_entry`
provenance) and automatic remembering from conversations (`conversation`
provenance, kept until deleted). Inspect shows fact/store revisions, creation
and last-modified source/times/consent IDs and exact expiry. Delete uses an
explicit Yes/No dialog whose default is No; purge expiry is also an explicit
action. Management work runs off the WPF dispatcher under the app-shared effect
owner, and every store open in the process (recall, remembering, management)
is serialized behind one store gate.

## Explicit activation and actions

The library still has no default directory or automatic `Open`. The Desktop
owner resolves its settings policy, then creates a pure path-bound
preview. `ValidateLocalScope` checks the local path without creating anything.
The activation decision defaults to `No`; the Desktop owner produces a one-use
`Allow` authorization only while memory is ON:

```csharp
var activation = MemoryStoreActivationPreview.Create(selectedLocalDirectory);
// activation.Authorize() refuses: the default decision is No.
var approval = activation.Authorize(MemoryConsentDecision.Allow);

using var store = MemoryStore.Open(activation, approval);
```

Preview creation performs no filesystem IO. Authorized `Open` validates the
absolute non-root local path, requires a known local fixed/removable/RAM volume,
rejects UNC/network/unknown-volume/reparse/alternate-stream locations, creates
only that selected scope and acquires its exclusive owner lock. A second owner
is refused. An authorization is bound with ordinal equality to the exact preview
and normalized path and cannot be replayed, including by case-folding it.

The fact-producing library operations are `SaveAsync(SaveFactRequest)` and,
for the [memory sync](#one-memory-on-every-computer) only, `MergeAsync`, which
puts in facts other computers made exactly as they are.
The library has no transcript, conversation, provider, watcher, ingestion
callback or generic object API; Desktop decides what to save. Each save
requires bounded fact content, typed provenance, a consent UUID and a retention
choice. Supported provenance is:

- `user_entry`
- `user_reviewed_import`
- `conversation` (picked out of a finished exchange while memory was ON)

`InspectAsync` returns exact current facts and provenance. `EditAsync` and
`DeleteAsync` require the fact UUID and expected fact revision; a stale or
missing fact fails rather than reporting a success-shaped result. Edits retain
creation provenance and record a fresh last-modified provenance. Every durable
mutation increments one authoritative store revision.

## Owned format and bounds

The selected directory has a finite owned layout:

| Name | Purpose |
| --- | --- |
| `.martlet-memory.v1.json` | Authoritative strict schema-1 fact document |
| `.martlet-memory.v1.lock` | Empty exclusive ownership lease |
| `.martlet-memory.v1.pending` | Exact same-directory atomic-write stage; never an authority |

Unrelated files are not opened, renamed or removed. There is no `.bak` file,
directory sweep or automatic backup. The document records a store UUID, monotonic
store revision, UTC update time (never behind any fact's own time, so a fact
another computer stamped ahead of this clock keeps its times) and exact facts.
JSON rejects duplicate
properties, unknown members, numeric/unknown enums, malformed UTF-8, missing
required fields, future schema versions, duplicate fact UUIDs and inconsistent
timestamps. A malformed or newer authoritative file is preserved and refused;
it is never reset to an empty store.

| Bound | Value |
| --- | --- |
| Facts | 100,000 (a full store saves no new fact and says so; nothing is deleted to make room) |
| Fact content | 32,768 UTF-16 characters (the UTF-8 byte bound, 3 bytes for each character, never refuses less) |
| Authoritative/export JSON | 256 MiB (the file is read whole into memory, so it keeps a bound) |
| Query | 256 characters, 512 UTF-8 bytes and 24 distinct terms |
| Retrieval result | 1-20 facts |
| Derived query cache | 64 revision-bound queries |
| Expiring retention | Any future time (no maximum) |

Retention is an explicit `until_deleted` or `expires_at` choice; there is no
implicit permanent/default policy in a save request. The library has no idle
timer. Expired facts are unavailable and are physically removed from the
authoritative document, lexical index and cache on the next explicit store
operation or `PurgeExpiredAsync`. An unused/offline scope can therefore retain
expired bytes until it is explicitly opened and operated. Clock rollback and
physical media retention are not qualified.

## Atomicity, cancellation and interruption

Writes serialize behind the store owner. A mutation builds and validates its
complete next document and derived index, writes the fixed pending file with
write-through and disk flush, checks the original cancellation token, then
performs a same-directory atomic move/replace. Before that move, cancellation
or IO failure removes only the exact owned stage and preserves the previous
authority. Once the move commits, cancellation cannot turn the committed
mutation into a reported cancellation.

On explicit reopen, a leftover exact pending filename is discarded as
uncommitted; it is never promoted over the current authority. Controlled tests
cover cancellation before commit, a fault at the commit boundary, a leftover
partial stage, exclusive ownership and concurrent saves. This is ordinary
local-filesystem atomic-write evidence, not proof against physical power loss,
controller caches, hostile same-user races, filesystem failure or secure
erasure of old disk blocks.

`HasPendingCleanup` and `RetryCleanupAsync` retain ownership if an exact staging
file cannot be removed. No broad cleanup or unrelated-file deletion is used.
All library failures have fixed messages and retain no raw IO exception, path
or fact content.

## Lexical retrieval and reranking

Retrieval uses only a deterministic in-memory index of the authoritative facts.
Every change builds it again. Nothing on this path calls a model, makes
embeddings or uses a vector store: recall runs on the reply's path, and it must
not make a reply slower (see
[Never add conversation latency](../AGENTS.md#never-add-conversation-latency)).

**Words.** A fact and a query are read as search terms (`SearchTerms` in
`Martlet.Core`, which app guides and past conversations also use): runs of
letters and digits in lower case, without common English grammar words (*the*,
*is*, *my* ...), each with a light English stem. So *cats* finds *cat*,
*running* finds *runs* and *studying* finds *studies*. Words longer than 40
characters and terms without a letter or digit (emoji) are left out. Other
languages work too:

- A text that is clearly Spanish, French, German, Portuguese or Italian (at
  least two different grammar words of that language, more of them than
  English ones, and at least an eighth of its words) also leaves out that
  language's grammar words and adds each word's stem by its light rules, so
  *gatti* finds *gatto*, *Drachen* finds *Drache* and *perros* finds *perra*.
  English text gets exactly the terms it always got, and words common in
  English text (*AI*, *plus*, *com* of a web address) never make a text read
  as another language.
- Chinese, Japanese, Korean, Thai, Lao, Khmer and Myanmar have no spaces
  between words, so each pair of neighboring characters is a term (*東京大学*
  is *東京*, *京大* and *大学*); Chinese grammar characters (*的*, *了*, *我*...)
  and Japanese and Korean particles are left out.

`search_languages_check` (MCP) shows this with synthetic facts in each of those
languages. A query
made from a message (`MemoryQuery.TryFromBoundedSource`) keeps the message's
own words, one word for each new term, so a stem is never stemmed again. It
holds at most 24 terms, 256 characters and 512 UTF-8 bytes. A query without a
search term (for example, only grammar words) is refused with `LimitExceeded`,
like an empty query; desktop recall then fills with the newest facts.

**Stage 1: BM25.** An inverted index (for each term, the facts that hold it and
where) finds every fact that holds a query term. BM25 scores them (k1 = 1.2,
b = 0.75; Robertson and Zaragoza, 2009). A rare term counts more than a common
one, a term repeated in a fact counts less each time, and a long fact counts a
little less than a short one.

**Stage 2: the reranker.** The reranker scores the 64 best BM25 candidates
again. Each of these features multiplies the BM25 score:

- **Coverage** (up to ×1.5): the part of the query's term weight that the fact
  holds. A fact that answers more of the question comes first.
- **Proximity** (up to ×1.25): 1 divided by the smallest distance between two
  different query terms in the fact (Tao and Zhai, 2007).
- **Phrases** (×1.125 for one, up to ×1.25): query word pairs that stand next
  to each other in the fact, in the query's order.
- **Recency** (up to ×1.1): a gentle boost that halves every 30 days before the
  store's newest fact. It uses that fact's time, not the clock, so a cached
  result stays exact, and an old fact that answers the question still wins.

Then a greedy selection takes the best fact, then the best of the rest, and so
on (the idea of maximal marginal relevance; Carbonell and Goldstein, 1998). A
near-duplicate of a fact already taken counts a quarter of its score. Two facts
are near-duplicates when the Jaccard similarity of their terms is at least 0.8,
the rule that remembering uses to skip a near-duplicate. It is exact for facts
of up to 32 different terms and estimated from 32 hashed terms (MinHash) for
longer facts. As in remembering, the same words for two people are two facts:
only facts of the same person, or a fact about no one in particular, are
near-duplicates. Scores only go down in this selection, so the results are in
score order and a shorter result list is always the start of a longer one.

The full ranking (score, then more matched terms, then the newest modification
time, then UUID) is applied before the result limit. Results are bounded
(1-20), deterministic, and return the full fact with its creation/edit
provenance and the exact store revision. A `MemorySnapshot` leaves out the facts
that expired before it ranks, and otherwise ranks exactly like its store.

**Speed.** On the developer PC (shared with other work, so numbers vary), a
query on a store of 512 facts takes this long (median):

| Facts of | Before (TF-IDF) | Now (BM25 and the reranker) |
|---|---|---|
| 6-16 words | 154 µs | 38 µs |
| 60-120 words | 351 µs | 83 µs |
| 600-700 words | 266 µs | 161 µs |

A full store of 100,000 facts of 6-16 words, with a small vocabulary where every
query word is in thousands of facts, takes about 2.9 ms (median; 512 facts:
40 µs). The reranker keeps the 64 best BM25 candidates with a heap instead of
sorting every candidate, and a query uses per-thread working arrays and clears
only the facts it touched, so its cost follows what it matches, not the size of
the store.

`memory_recall_check` ([MCP](MCP.md)) measures both on any PC.
`RankingAFullStoreStaysWellUnderAMillisecond` keeps the median for 512 facts
under a millisecond and for a full store under 10 milliseconds.

### Exact invalidation

Every save, edit, delete or expiry swaps in a freshly built index and clears the
bounded query cache. A retrieval snapshots one revision, computes outside the
writer boundary, then samples expiry under the writer boundary and rechecks
that revision before returning or caching. Facts expired at that final sample
are purged and invalidate the query, including cache hits. A
delete that commits while retrieval is in flight makes that retrieval fail with
`QueryInvalidated`; it cannot return the deleted fact. Tests also inspect the
real derived index/cache after deletion. Old managed strings can remain in
process memory until reclaimed, and atomic replacement is not a secure disk
wipe; the guarantee is exact logical source/index/cache removal and stale-result
suppression.

If a private store/export partial cannot be removed after a failed or canceled
Desktop action, the original store and app effect slot remain owned. Memory
shows the pending cleanup and **Retry owned cleanup** retries only the engine's
recorded paths, even from a reopened Memory window. Cancellation and closing
an observer never release this quarantine; no new app effect is admitted until
cleanup and disposal finish. There is no automatic cleanup sweep or retry timer.
Exiting the process can still leave private partial bytes; this is not secure
erasure or crash recovery of an export.

### Evaluation

The held-out synthetic fixture at
`tests\Martlet.Memory.Tests\Fixtures\retrieval-held-out.json` keeps its facts and
queries outside the retrieval implementation. It has 26 facts (server region,
preferences, schedules, devices, care, pets, family, work and more) and 22
queries: the same words as a fact, plurals, verb forms and a question of mostly
grammar words. `HeldOutSyntheticCorpusReturnsExpectedFactWithProvenance` checks
the top result and its provenance. `HeldOutFixtureRecallAndMeanReciprocalRank`
writes recall@k (the expected fact is in the first k results) and MRR (mean
reciprocal rank, 1 / the expected fact's place) to the test output:

| Ranking | recall@1 | recall@3 | recall@5 | MRR |
|---|---|---|---|---|
| Before: TF-IDF over lower-case words | 0.64 | 0.77 | 0.82 | 0.703 |
| Now: BM25 over stems and the reranker | 1.00 | 1.00 | 1.00 | 1.000 |

Plurals went from MRR 0.361 to 1.000, verb forms from 0.729 to 1.000 and the
grammar-word question from 0.200 to 1.000; queries with the same words stayed
at 1.000. The new queries were written before the new ranking. The fixture is
still synthetic: it is deterministic contract evidence, not a real-user
usefulness score or a reason for embeddings. `RerankingTests` checks each
reranker feature on hand-made facts.

## Frozen export and default-No authorization

`CreateExportPreviewAsync` freezes one readable schema-1 JSON document containing
the exact current facts/provenance, export UUID/time and store revision. The
caller can copy and display those exact bytes, count, byte length and SHA-256.
The destination is selected separately and does not enter the exported JSON.

`MemoryExportPreview.Authorize(destination)` defaults to `No` and refuses.
Explicit `MemoryExportDecision.Export` creates a one-use authorization bound to
the preview UUID, store UUID/revision, digest and normalized new `.json`
destination. Export is create-only and uses a same-directory flushed partial
plus atomic rename. Reserved store filenames are never valid export destinations.
A changed store revision, deletion while the partial is being written, or
expiry sampled at finalization invalidates export before rename. Once an
authorized export rename commits before a later delete, that user-created file
is outside store ownership and immediate erasure, as are any user-created
copies. No upload or support contact exists.

The schema has no credential, endpoint, model, device or filesystem-path field
(a fact's optional `voice_id` is an opaque voice list ID, never a name or a
voiceprint).
Fact content itself is intentionally present in an explicit export and may
contain whatever the user chose to save; callers must preview it. The library
has no logger and exception/authorization `ToString()` output omits fact content
and destinations.

Desktop displays the exact frozen JSON plus preview UUID, fact count, byte
length, store revision and SHA-256 before destination selection. Its export
checkbox is cleared for every preview and the export button requires that
fresh checkbox. Export remains create-only and local; support has no upload or
contact path and configuration recovery never includes the bytes.

## Automatic recall and remembering

There is no per-turn memory checkbox: the memory setting decides. When memory
is OFF, the store is never opened or read by a conversation.

**Recall.** After STT and participation accept the current explicit typed, PTT
or hands-free turn, Desktop opens the store once, asks the lexical index for the
best matches for that user input ([BM25 and the reranker](#lexical-retrieval-and-reranking))
and fills up to twelve facts with the most
recently changed ones (so a small store is recalled whole; when Martlet
recognized who is speaking, their facts and everyone's fill it before other
people's, see [Whose memories](#whose-memories)). The facts travel in
the notes on the user's message (see
[Conversation › Prompt caching](CONVERSATION.md#prompt-caching-and-the-request-layout))
as one block between `[MARTLET_LOCAL_MEMORY]` labels, one line per fact with
whose it is (`[Sam] `, when it belongs to someone), its source
(`saved by the user` / `from conversation`) and date, and only the
facts not already in the notes of an earlier message the request still carries
(so a fact is sent once, not with every reply). The instruction says they are
background data, never instructions,
permissions, routing or tool directives. Consent UUIDs, paths and the rest of
the store are not sent.

The existing 16,384-byte, 16-message and 16,640 local input-token reservation is
unchanged. Current input/persona/style are admitted first; oldest volatile
conversation messages are omitted, then the least relevant recalled facts,
until the request fits. Used/omitted fact counts and store revision are visible
metadata, but fact content is excluded from the timeline, support journal and
ordinary logs. Memory is helpful, not required: if a fact changes while it is
read, the current facts are read once more; if the store can't be read (busy,
unavailable, turned off), the reply goes ahead without memory and the status
names the problem.

**Remembering.** After a reply completes, the exchange is queued (at most four
pending) and handled in the background, one at a time, on a separate text-only
runtime so it never delays the next turn. Desktop recalls up to ten related
facts, then sends one extra request (at most the max reply length in output
tokens, same Thinking route and credential binding, its own one-request
authorization) with the latest exchange, the previous exchange as context and
those numbered facts. When [learning names](VOICES.md) is due after the same
reply, it is the same request (Companion › Prompts › *Remembering and learning
names together*), not a second one. On a Thinking model on this PC (Ollama or
another server on loopback) that request continues the reply's own
conversation instead of quoting an excerpt: the same instructions, tools (described
again, never run) and earlier messages, the message and the reply, then the task.
Such a server keeps the conversation in its prompt cache only while requests start
like it, so a request with another start would make the next reply read the whole
conversation again. It quotes the persona-free excerpt instead elsewhere, when
what this PC played is in the conversation (remembering never reads that) or when
it wouldn't fit the context. The excerpt marks the character's lines with the
name of the persona Martlet uses (*Ivy: ...*; *Martlet: ...* without one), with a
line that says those are the companion's own words.
When the [Thinking pool](#helper-jobs-on-the-thinking-pool) has a free member that
reads text, the pool takes this request instead (a low-priority Memory job). The
member gets the short excerpt, because it has no copy of this conversation in
its cache, and the conversation's own model gets no request. Without such a
member, the request goes to the conversation's Thinking model as described
above, but only after the reply finishes speaking and while no other reply runs.

<a id="helper-jobs-on-the-thinking-pool"></a>
**Helper jobs on the Thinking pool.** Remembering, naming a character's emotes,
deciding its touch temperament, finding its touch zones, measuring its eyes and
writing the [memory brief](#the-memory-brief) are helper jobs. Each one goes first to a free [Thinking pool](CONVERSATION.md#the-thinking-pool)
member that can take it (text, or vision for touch zones and the eyes), so it does not
compete with the reply or its prompt cache. Memory, naming, temperament and the
eyes are low priority, and the memory brief is the lowest; touch zones are higher, because you wait for them. When no member
can take a job, or the pool can't finish it, Martlet uses the conversation's
Thinking model, after the reply finishes speaking (the memory brief only when that model is in the cloud). Tidying
memory is the exception: it runs only on the pool, and without a free member it waits
([Search words, history and tidying](#search-words-history-and-tidying)). A job with a picture goes to
the image model of its own instead while pictures go to it ([Image and audio
models](SENSE_MODELS.md#helper-jobs-with-a-picture)). The desktop log names the
route of each job, and MCP's `helper_jobs_status` shows the last route of each
kind (`helper-jobs.json`).
The model answers in a strict line format: `REMEMBER: <fact>`,
`REMEMBER V<n>: <fact>` (a fact about another voice heard), `UPDATE <n>: <fact>`,
`FORGET <n>` or `NOTHING`, at most three of them. A `WORDS: <words>` line after a
`REMEMBER` or `UPDATE` line gives that fact its hidden search words
([Search words, history and tidying](#search-words-history-and-tidying)). Desktop
validates every line (single line, <=300 characters, real words, in-range
numbers, no memory labels), skips near-duplicates of the same person's facts,
applies updates/forgets only
to the exact fact revisions it showed, keeps the fact an update replaces as
history, and saves new facts with `conversation`
provenance until deleted. A full store (100,000 facts) saves no new fact and
deletes nothing to make room: the conversation window says *Couldn't update
memory. Memory is full. Delete some facts in Companion › Memory.* and the log
says how many facts weren't saved. The conversation window shows what was
remembered (*Remembered: … (Sam)*), and Memory lists it.

A model that declines or answers with nothing simply has nothing to remember;
an answer cut off by the max reply length keeps the lines it finished. If the
store is briefly in use (recall, the Memory page) or a fact changed meanwhile,
remembering waits and tries twice more. When it still fails, the conversation
says why once (*Couldn't update memory.* plus the reason, such as the
provider limiting requests or the memory folder being unusable); the same
problem on later exchanges is only logged (`Remembering failed (<code>)`) until
remembering works again.

### Asking Martlet to change its memory

Remembering after a reply only sees the ten related facts and can't give a fact
to someone else, so while memory is on, replies on a route that does function
calling are also offered Martlet's own `manage_memories` tool (always last among
Martlet's own tools, with the same text, so the start of every request stays the
same). The model uses it when the user asks what it remembers or asks it to
remember, correct, reassign or forget something:

- `find` (`query` words and/or `person`) lists up to 20 facts with short ids,
  whose each is and when it changed, best matches first.
- `remember` (`fact`, optional `person`) saves a fact with `conversation`
  provenance, as the speaker's unless `person` says otherwise; a near-duplicate
  of the same person's fact is not saved twice.
- `update` (one id, a new `fact` and/or `person`) corrects a fact or gives it to
  someone else, keeping its retention. A corrected fact's earlier version stays
  as history ([Search words, history and tidying](#search-words-history-and-tidying)).
- `forget` (up to 50 ids) deletes those facts and their earlier versions in one
  commit.

`person` is a name or alias of a voice Martlet knows, its tag (`V3`), `me` (the
one speaking, else your own voice: one linked to the account signed in here) or `everyone` (about no one in particular).
An unknown name or id is refused with what the model can use instead; nothing is
guessed. Changes show in the conversation like remembering's (*Forgot: …*) and
in the Tools page's log (`Martlet > manage_memories: forgot 2`, never a fact).

**After the reply instead.** The **Memory** check-in tool set (`memory`, see
[Check-in tool sets](CONVERSATION.md#check-in-tool-sets)) does the same work on
the Thinking pool, so the reply doesn't wait for it. Its tools each run one
action: `memory_find`, `memory_remember` (`fact`, optional `person`),
`memory_update` (`id`, a new `fact` and/or `person`) and `memory_forget`
(`ids`). `me` is whoever spoke last. The set takes over `manage_memories`
(`Replaces`): while an After each exchange check-in that is on ticks it and the
Thinking pool has a member that calls tools, the reply is not offered
`manage_memories`. It is offered the read-only `find_memories` (`query` and/or
`person`) in its place, so it can still answer *what do you remember about
me?*, and it is told to say briefly that it will change its memory. Both are
always worded the same, so the start of every request stays the same. Changes
show in the conversation and the Tools log as before
(`Martlet > memory_forget: forgot 1`). The first line of each answer, which
the check-in's card and `check-ins-status.json` show, is the outcome only
(*Memory: forgot 1.*), never a fact or a name. Automatic recall and
remembering after a reply do not change.

### Search words, history and tidying

Facts are short, and a question often uses other words than the fact: "What
should I cook?" does not hold a word of *The user is vegetarian.* The same
problem lets an old fact stay: *I moved to Seattle* possibly does not find
*The user lives in Portland*. Three changes help, all made away from the reply,
so the time to the first spoken word does not change.

- **Hidden search words** (`search_words`, document expansion as in
  [doc2query](https://arxiv.org/abs/1904.08375)). Each fact can carry up to 16
  topics, synonyms and related words, such as *diet, food, meat, cooking*.
  Recall indexes them with the fact, so the question finds it. They are never
  sent to the reply as the fact's text, and near-duplicate checks compare only
  what the fact says. Remembering writes them in the same request (the
  `WORDS:` line). A fact without them (one you typed, an older fact, a
  corrected fact) gets them from tidying. `manage_memories` `find` and the
  Memory window's *Search* also match them. Giving a fact words never changes
  when it last changed.
- **Replaced facts stay as history** (`replaced_at_utc`, `replaced_by`, as in
  [Zep](https://arxiv.org/abs/2501.13956)). A correction (remembering's
  `UPDATE`, `manage_memories` `update`, `memory_update`) keeps the earlier
  version as a fact of its own, dated when it stopped being true, and the fact
  keeps its ID. Tidying can also mark a fact a newer fact made outdated. History
  is never recalled, shown to remembering or listed by `find`, and it can't be
  changed. Forgetting a fact forgets its earlier versions too. A full store
  keeps the correction and skips the history copy. Corrections typed in the
  Memory window keep no history (they are usually typing mistakes).
- **Tidying memory** (a helper job, `memory_upkeep`, Companion › Prompts ›
  *Tidying memory*; cleanup in a separate background step, as in
  [Letta](https://arxiv.org/abs/2504.13171), and add, update or no change
  decisions, as in [Mem0](https://arxiv.org/abs/2504.19413)). About 30 seconds
  after facts change, and again while work is left, each store this device may
  change gets one pass: the facts changed since the last pass are looked up with
  the store's own search (search words included), and facts of one person that
  share a good part of their words form a group (at most 6 groups of 5). Facts
  without search words are listed too (at most 16, newest first). A free
  Thinking pool member that reads text answers `SAME <n> <m>: <fact>` (merge
  duplicates into the first; their history moves to it), `OUTDATED <n> BY <m>`
  (keep the older fact as history) and `WORDS <n>: <words>`. Lines across
  groups, about unknown numbers or calling the newer fact outdated by the older
  are left out. One commit (`MemoryStore.TidyAsync`) applies the rest, only to
  the exact revisions listed. Tidying runs **only on the Thinking pool** at low
  priority, never on the conversation's own model, and touches a store only
  while no reply runs and nobody else holds it. Without a free member it waits
  for a later pass. `memory-upkeep.json` in the data folder keeps how far each
  store is checked for duplicates and what the last pass did (counts, times and
  the member; never a fact).

**Sync and compatibility.** The new fields travel inside the fact like
`voice_id`, so every computer has the same words and history. A fact without
them is written exactly as before (no new keys). An older Martlet can't read a
fact that has one: its sync passes the fact through untouched (like any newer
fact, counted as needing a newer Martlet), so it never drops the fields, and it
keeps its own earlier version of that fact. Giving words to a fact is a new
revision with the same last-changed time, so an edit made at the same revision
on another computer wins over it.

Checked locally: `memory_upkeep_check` (MCP; fixture answers, NOT AI),
`Martlet.Memory.Tests` (`SearchWordsAndHistoryTests`) and the desktop's
`MemoryUpkeepTests`, `HelperJobsTests` and `MemoryWindowTests`. A real Thinking
pool member tidying a real store, and an older Martlet syncing with a newer one
between two real computers, are **NOT RUN**.

## Whose memories

Several people can talk to Martlet through one microphone, and
[voice recognition](VOICES.md) tells them apart. Each fact can belong to one of
them: `voice_id` in the fact is the ID of a voice in the voice list
(`voices.json`), the person who said it or who it is about. A fact without one is
everyone's (about no one in particular), as every fact was before voices.

- **Remembering.** When Martlet recognized who spoke, the request lists the
  voices heard (by tag, such as `V3`, with the names they go by), says which one
  spoke, and says that a new fact is saved as the speaker's; the related facts it
  shows start with whose each is (`1. [Sam] The user likes tea.`). A fact about
  another voice heard is answered `REMEMBER V2: <fact>`; a tag that wasn't heard
  counts as the speaker's. Nobody recognized means no one's fact, as before. An
  update keeps whose the fact is. The same words for two people are two facts;
  a near-duplicate of the same person's fact, or of an everyone's fact, is
  skipped.
- **Recall.** The best matches for what was said come first, whoever they
  belong to (a near-duplicate of a better match of the same person, or of
  everyone's, moves below the other matches). The rest of the twelve is filled with the speaker's facts and
  everyone's before other people's, newest first (by recency alone when nobody
  was recognized). Each recalled fact starts with whose it is: the voice's name,
  else its tag (`[V3]`), or `[a forgotten voice]`. When one does, the block also
  says what that means (Companion › Prompts › *Whose memories*: the name in
  brackets is whose the fact is; keep whose fact is whose and be discreet with
  someone's personal facts while another person talks). It is sent only with a
  block that has such a fact, in that message's notes, so it never changes the
  start of a request.
- **Memory window.** Facts are listed newest first, each with whose it is, where
  it came from and when it changed. *Search* finds facts by their words, their
  hidden search words or whose they are, and *Show* lists all facts, everyone's, one voice's or those of
  forgotten voices. *Show replaced facts* (`MemoryShowHistory`) lists the facts
  kept as history too, each starting with when it was replaced; their details
  say what replaced them, and a current fact's details count its earlier
  versions and show its search words. History can be deleted, not changed. Select one fact to change it on the right (*Save changes*),
  or several (Ctrl or Shift) to delete them together. *Delete all shown* deletes
  every fact listed now (one person's facts, or what the search found), and
  *Delete everything* forgets all facts; each asks first. Storage and export
  are folded away below. The window shows the facts the moment it opens, even
  while Martlet is replying (it reads them directly, not on the setup slot a
  reply holds), and keeps up on its own: whatever adds, changes or forgets
  facts while it is open (remembering after a reply, `manage_memories`, its own
  actions) makes it read them again, with no *Refresh* needed. A read that finds
  the same store version changes nothing, and a refresh never changes the
  fact you are writing or changing, its *Belongs to* choice or the selection.
  *Belongs to* chooses whose a fact is when you add or update
  it: *Everyone*, or a voice Martlet knows. A new fact is yours (the voice linked
  to your account with *This voice is ...* on People) unless *Show* lists one voice's facts; then it is
  that voice's. The details say *Belongs to*. The status line (`MemoryFactStatus`) counts the facts, how
  many belong to how many people, how many to forgotten voices and how many replaced facts it keeps as history, never a
  name or a fact.
- **People.** *Memories* (what Martlet remembers about them) on each voice opens Memory
  showing that voice's facts. Merging voices keeps every fact of both (a merged
  voice's ID leads to the voice it joined). Forgetting a voice keeps its facts,
  under *Forgotten voices*, until you delete them or give them to someone else.
- **Sync and compatibility.** The voice ID travels inside the fact like the rest
  of it, so every computer agrees whose each fact is; the voice list is shared the
  same way ([sharing](VOICES.md#sharing-between-your-computers)). A fact without a
  voice is written exactly as before (no `voice_id` field). An older Martlet
  can't read a fact that has one: it passes through its sync untouched (like any
  newer fact) and its own store refuses a file that holds one rather than
  dropping it.

The library checks only the ID's form (1-64 ASCII letters, digits, `-` or `_`);
it never reads the voice list. `memory_status` (MCP) counts the facts and whose
they are from a data directory without opening the store.

## The memory brief

Facts are short, and a question often uses other words than the fact, so
recall can miss a personal detail that matters. So Martlet also keeps a
**memory brief**: a short profile of the user and the people they talk about,
written from the remembered facts. It goes with the first message of each
conversation, so the reply knows the important things even when recall finds
nothing.

- **What it is.** At most 1,500 characters of plain lines (the prompt asks for
  under 200 words). First lines about the user, then one line for each other
  person, starting with their name in brackets (`[Sam] ...`), as recall marks
  whose a fact is. Facts of a forgotten voice are left out.
- **How it goes with messages.** It goes in the notes of the message, after
  the conversation's cached start, between `[MARTLET_MEMORY_BRIEF]` labels and
  opened by Companion › Prompts › *Memory brief, with your message*. It goes
  only when no earlier message that the request carries has it, so once a
  conversation (again after *Refresh context*, or when the oldest messages are
  let go). It never changes the instructions, so the start of every request
  stays the same. The reply's path only reads the brief that is in memory:
  nothing is read or waited for, so the first word comes no later. A Discord
  call never gets it, because other people are in it. A message too long to
  fit goes without it (after it lets go of earlier conversations and the app
  guide's sections).
- **Writing it.** A background job (`MemoryBriefService`) writes it with
  Companion › Prompts › *Writing the memory brief*. The model gets the facts
  the user saved first, then the newest, at most 300 facts and 9,000 bytes,
  each with whose it is and its date, and the names the user goes by (their
  own voices). The job is a helper job (`memory_brief`) at the Thinking pool's
  lowest priority. Without a free pool member, it runs on the conversation's
  Thinking model only once no reply runs or speaks and you aren't talking,
  and only when that model is in the cloud. A Thinking model on this PC or on
  a paired host keeps its prompt cache for the conversation, so then no brief
  is written until a pool member can take it.
- **When it is written.** About 30 seconds after facts change (remembering,
  the Memory window, the memory sync), and 45 seconds after Martlet starts,
  the job reads the facts while the conversation is quiet. It writes the
  first brief once there are at least 3 facts. It writes it again when 3 facts
  are new, when a person in it has a new name, or when a fact it was written
  from is forgotten or changed. It writes at most once in 10 minutes (1 minute
  after a forgotten fact) and 6 times an hour on each computer, and not within
  2 minutes after it took a brief from another computer.
- **A forgotten fact.** The brief records the facts it was written from (their
  ID and a digest of their words and whose they are, so search words that
  tidying adds change nothing). When one of them is forgotten or changed
  (including a fact replaced by a newer one, which becomes history), the brief
  is not sent again until it is written again without it. When no fact is left,
  the brief is cleared.
- **Companion › Memory › Memory brief.** *Send a short profile of me with the
  first message of each conversation* (on by default) turns it on or off: off,
  it is neither written nor sent. The box shows the brief, and you can change
  it: *Save my changes* keeps your own text, which Martlet never changes on
  its own; clear the box and save to let Martlet write it again. *Write it
  again* writes it from your memories now, also over your own text. The line
  above says when it was written and from how many memories, or why it waits.
- **One brief on all your computers.** The brief is kept in
  `memory-brief.json` in the data folder and travels as each person's shared
  setting `memory-brief` ([CLUSTER](CLUSTER.md#one-martlet-on-every-computer)):
  the text, on or off, who wrote it and the facts it was written from. A
  computer that takes another's brief sends it at once. With accounts, each
  person has their own brief, and switching person gives this PC that person's
  brief. Like the facts on the hosts, the brief is readable by every paired
  device and is not encrypted end to end between desktops.
- **The log** has counts and times only (`Memory brief: written from 42 facts
  (812 characters).`, `Memory brief: went with your message`), never the
  brief or a fact.

Checked locally: `MemoryBriefTests` (Martlet.Desktop.Tests) and
`MemoryBriefShareTests` (two simulated computers, Martlet.Core.Tests). Through
MCP: `memory_brief_check` runs the production brief, helper-job router and
shared setting on a disposable store with a fixture pool member (FIXTURE - NOT
AI), and `memory_brief_status` reads `memory-brief.json` without the text. A
brief written by a real model and two real computers are **NOT RUN**.

## Conversation history

Facts are what Martlet distills; the **record of conversations** is what was
actually said. While memory is ON and **Keep a record of my conversations**
(Companion › Memory › Conversation history, on by default) is on, every
finished exchange of a typed, push-to-talk or always-listening turn (what you
typed or said, who said it when Martlet recognized a named voice, and Martlet's
reply) is kept in the record of the person signed in, as is a reply Martlet
starts on its own to bring up finished background work. The record is the same
on every computer where that person is signed in
([One record on every computer](#one-record-on-every-computer)). A conversation runs from the talk window opening, or
**Refresh context**, until the exchanges kept in mind are cleared (Refresh
context, pause, lock, closing the talk window). Not recorded: screen and camera
glances, what this PC plays (only the user's own words of such a message), a
`[pass]` (what always listening heard wasn't meant for Martlet), failed,
refused or stopped replies, and anything while memory or the choice is off.
Turning it off keeps what is already recorded until you delete it.

**On disk.** The household owner's record is in the data folder's
`conversations` folder; another person's is in their account folder,
`accounts\<32 hex>\conversations`. It holds one JSON Lines file
per month (`history-2026-10.jsonl`, by UTC month), one line per exchange:
`{"v":1,"id":…,"conversation":…,"at":…,"kind":"typed|spoken|report","speaker":…,"user":…,"reply":…}`,
plus `"source":{"app","chat","server","name","userIds","replyIds"}` for an
exchange from a messaging app and `"edited"` once it was edited here.
Each exchange is appended (and flushed to disk) in the background after its
reply, one at a time, so the next reply never waits for the disk; exiting
Martlet waits a moment for the last one. Earlier lines are never rewritten by
appending; a line cut short by a crash is skipped when the record is read again
and the next exchange starts on its own line. Text is kept up to 8,192
characters of what you said and 16,384 of the reply (the reply's own bound),
with control characters other than line breaks and tabs made spaces. Like the
fact store it is plain local data in your profile: not encrypted, not uploaded
anywhere but your paired hosts and not part of configuration backup.
`conversation-history.json` beside the data folder's settings holds the two
choices, which are the same on all your computers (the `conversation-history`
shared setting, each person's own).

**Reading it.** The record is read once, in the background, when a
conversation opens (or the Conversations page or the Memory page needs it) and
kept in memory with a lexical (BM25) index of what was said: each word in lower
case and reduced to a light English stem, with common grammar words left out
(the shared `SearchTerms` in `Martlet.Core`, which also reads Spanish, French,
German, Portuguese, Italian, Chinese, Japanese and Korean, as
[above](#lexical-retrieval-and-reranking) for facts), so *plants* finds *plant*
and *baking* finds *baked*. Nothing is embedded or sent anywhere. The newest
100,000 exchanges are indexed; older ones stay in their files. With 20,000
synthetic exchanges (7.3 MB) reading takes about a third of a second, once, and
a recall about 2 ms (at most about 3 ms; `conversation_history_check`).

**Bringing back what was said.** When your message refers to an earlier
conversation (*do you remember…*, *remember when…*, *what did we talk about
yesterday?*, *did I tell you about…*, *in our last conversation…*, or something
said or talked about with a time such as *last week* or *3 days ago*; also
in Spanish, French, German, Portuguese, Italian, Chinese, Japanese and Korean,
such as *¿te acuerdas…?*, *tu te souviens…*, *weißt du noch…*, *você
lembra…*, *ti ricordi…*, *你还记得…*, *覚えてる？* and *기억나?*, with times such
as *hace 3 días*, *la semaine dernière*, *vor 3 Tagen*, *上周*, *昨日* and
*어제*), up to three matching exchanges of other conversations go in that
message's notes (see [Conversation › Prompt caching](CONVERSATION.md#prompt-caching-and-the-request-layout))
between `[MARTLET_PAST_CONVERSATIONS]` labels, with today's date and each
exchange's day and time (each side cut to 280 characters, brackets made
parentheses so recorded text can't open or close a block), introduced by
Companion › Prompts › *Past conversations*. The best matches for the message's
own words come back (common words and words about remembering, talking or time
left out; about half of the remaining words, at most three, must match, so one
common word alone brings back nothing), within or near the time it names; with
only a time (*what did we talk about yesterday?*) the latest exchanges of that
time do, an exchange nearly the same as a later one left out.

**How matches are ranked.** Ranking runs in memory on the reply's path, with no
model, embedding or other process (`PastConversationRanking`): BM25 over the
stems finds up to 200 candidates, then a fixed reranker scores each one on:

- *Coverage*: how much of what was asked the exchange has, each word weighted
  by how rare it is in the record (a word never said counts as the rarest). A
  recognized speaker's name counts as said (*what did Ana want…* prefers
  Ana's exchanges), and words that the exchanges up to two turns before or
  after it in its conversation have count half, so a follow-up such as *Where
  should we stay?* belongs to the trip it follows.
- *Proximity and phrases*: the asked words close together on one side of the
  exchange, and two asked words one right after the other (*Red Dragon*).
- *Time*: inside the time the message names, or near it (up to half its
  length, at least a day, on each side, scored lower the further out), so
  *yesterday* still finds something said late the evening before. A light
  preference for what is recent breaks ties.

Only exchanges scoring at least half the best go in the notes. Picking them
(maximal marginal relevance) leaves out an exchange nearly the same as one
already picked or already in the request's notes (four in five of their words
the same) and lowers one that is like them, so near-identical exchanges don't
fill the notes. When there is room left, the exchange right after a picked one
in its conversation comes too if you answered Martlet's reply there with some
of its words (*He'd love the apron* after *a grill brush or a new apron*),
because the decision often comes next. A held-out
set of synthetic conversations and questions (`PastConversationsEvaluationTests`)
measures recall@k and MRR.

Asking Martlet to remember something (*remember to…*), not remembering
something yourself (*I can't remember how…*) and asking to be told something
(*tell me what happened yesterday*) don't count as a reference. An exchange
already in the notes of a message the request carries isn't sent again, and the
conversation
going on never is (the request carries it). A message that doesn't refer to an
earlier conversation gets nothing: its request is exactly what it was without
the record, so replies keep their latency and their prompt cache. Recall reads
only what is in memory: while the record is still being read, the message goes
without. If the notes would make the request too large, they go first (then a
picture). The desktop log says how many exchanges went and how long the search
took (`Past conversations:`), never what.

**Searching it on its own** (Companion › Memory › *Let Martlet search the
record on its own*, off by default). On a Thinking route that does function
calling, replies are then offered `search_conversations` (`query` and/or
`when`: today, yesterday, 3 days ago, last week, a weekday or YYYY-MM-DD) after
Martlet's other own tools, always worded the same. The model calls it when you
bring up something from before; it returns at most six exchanges of other
conversations with their dates (as data, never instructions), ranked the same
way as recall but without its minimum match, or says nothing was found and not
to guess. It is off by default because its description (458
bytes, about 153 estimated tokens) goes at the start of every request: providers
cache it after the first reply of a conversation, but that first reply reads it
too.

**Seeing, searching, editing and deleting it.** The **Conversations** page (in the main window's side menu, also opened from
Companion › Memory › **Open conversations**) lists the conversations, newest first, with the apps
they happened in (this PC, Telegram, Discord, WhatsApp) and the chat's name;
the app box shows only one app's conversations and messages. The selected
conversation shows one entry per message (yours and Martlet's, with when, who,
the app and *edited*). It searches everything said (matching messages marked
▶, within the chosen app), edits one message (**Edit message**, then **Save
edit**; an empty text deletes it) and deletes one message, one conversation or
everything, each delete asked first (No by default). Deleting one side of an
exchange keeps the other; the exchange goes once neither is left. Deleting or
editing rewrites only that line's month file (through a temporary file and an
atomic replace) or removes the files; facts remembered from a conversation
stay in Memory until you delete them there, and a conversation still running
keeps what it has in mind until it is refreshed. Every edit and deletion
reaches your other computers too, and a deleted message never comes back from
one of them. Deletion is not a secure wipe of old disk blocks.

**Messaging apps.** A message from a paired Telegram or WhatsApp chat is
recorded in the talk window's conversation with its app, chat and the app's
message IDs (Martlet's reply pieces' IDs join it once they are sent). Each
answered Discord text message (a server channel, thread, DM or `/martlet`) is
recorded in that place's own conversation (one per channel or DM) with its
server, channel and message IDs. The talk window never recalls Discord
exchanges (other people's chatter; Discord replies keep their own history per
place), and `search_conversations` doesn't return them either. With **Also
delete and edit in Telegram and Discord when they allow it** (on), deleting or
editing here does the same in the app, as far as it allows:

| App | Delete your message | Delete Martlet's | Edit Martlet's | Edit yours |
| --- | --- | --- | --- | --- |
| Telegram | private chat, within 48 hours | within 48 hours | yes | no |
| Discord | in a server (Manage Messages); never in a DM | yes | yes | no |
| WhatsApp | no (the Cloud API can't) | no | no | no |

An edited reply is split the way it was sent; extra pieces are deleted, and a
reply that now needs more messages than it had is cut to fit (the window says
so). What an app won't do is said in the window and stays there as it is. The
changes go through a queue (`conversations\platform-changes.json`, kept over a
restart) one at a time per app at its pace (Telegram every half second, Discord
every second): a "slow down" (429) is waited out for as long as the app asks,
an app that can't be reached is tried again with a growing pause (given up on
after 8 tries), a refusal (too old, already gone, missing permission) is
dropped and said, and changes for an app that isn't connected wait until its
bot runs. The queue belongs to the computer where you made the change (in the
data folder's `conversations` folder, whoever is signed in): the record changes
on every computer, but only that computer asks the app. Make such changes on
the computer that runs your bots. The window's last line shows what waits (`HistoryPlatformStatus`);
**Stop waiting changes** drops it.

MCP: `conversation_history_status` (choices, what replies are offered, counts
per app, the changes waiting for apps and the last sync, never content),
`conversation_history_check` (the production record, recall, search, how
recall ranks, sources, single-message edits and deletes, what each app is asked
and the queue with a fixture app) and `conversation_sync_selftest` (below);
`HistoryStatus`, `HistoryWindowStatus`, `HistoryPlatformStatus` and
`HistorySyncStatus` read as text and `OpenHistory` opens the window
([MCP](MCP.md)).

### One record on every computer

Your record of conversations is the same on every computer where you are signed
in, so recall and `search_conversations` find what you said on any of them, and
a computer you add later gets the whole record. It travels through your paired
hosts while **Keep Martlet the same on all my computers** is on, the same way
as [your memories](#one-memory-on-every-computer). Each person's record is
their own: it goes only to the computers where that person is signed in.

- **The network copy** (`Martlet.Core.Sync.SharedConversations`): one copy
  per account and UTC month, with one last-writer-wins entry per exchange: the
  exchange's line exactly as the month file keeps it, its revision (1 when
  recorded, +1 for each edit), the time and the computer that wrote that
  version. A deleted exchange is a tombstone: the revision it deleted plus one.
  Merging keeps, per exchange, the highest (revision, time, deleted, writer,
  content), so an edit made on top of another wins by its revision, two edits
  of the same revision made apart keep the later one, and a deletion wins over
  the version it deleted. JSON, snake case, schema 1, at most 256 MiB, 100,000
  exchanges and 100,000 tombstones and 256 KiB per exchange. Hosts never look
  inside an exchange.
- **The sync** (`Martlet.Conversation.ConversationRecordSync`, every 30
  seconds and five seconds after an exchange is recorded, edited or deleted):
  the desktop asks each paired host for the digests of the months it keeps.
  A month is looked at only when its file changed here (its length, write time
  and this run's own writes) or a host's copy differs from the last merge. For
  such a month it reads the file once: an exchange added or changed here since
  the last sync is a change made here, and an exchange gone from the file
  (deleted on the Conversations page) is deleted everywhere. It merges in each
  different host copy (one read per distinct copy), writes the newest version
  of every exchange into the month file in one atomic replace, adds what is
  new to the in-memory index (or builds it again when something was edited or
  deleted), and gives every host with another copy the merged one.
- **Never in a conversation's way**: the sync skips its turn while Martlet
  replies or you talk, runs off the window's thread, and holds the record's
  files only while it reads or changes one month. A reply never waits for it:
  recall reads only what is in memory, and appending an exchange waits at most
  for one month to be written.
- **A newer Martlet's exchange** (a line this version can't read) is kept as it
  came, in the month's sync state, and passes on to the hosts unchanged. This
  computer keeps its older line of that exchange and never writes it over the
  newer one; the status line says how many need a newer Martlet.
- **What this PC keeps** (`sync\<yyyy-MM>.json` beside the month files): for
  each exchange its ID, revision, a SHA-256 digest, the computer that wrote it
  and the time, the agreed tombstones, the month file's stamp and the merged
  copy's digest; `sync\status.json` holds the last sync's counts. No
  conversation text is kept there (only a newer Martlet's exchanges, above).
  Deleting the `conversations` folder by hand makes this computer a new one:
  it takes the whole record again and deletes nothing elsewhere.
- **Each host** keeps `conversations-<32 hex>-<yyyy-MM>.json` beside
  `host.json` (0600, gateway service owner; at most 8,192 months in all) and
  serves `GET /martlet/v1/conversations/accounts/<32 hex>` (the months and
  their digests), and `GET` and `POST` (merge and return)
  `/martlet/v1/conversations/accounts/<32 hex>/<yyyy-MM>` only to paired
  member devices where that account is signed in, over their pinned, signed
  connection; another device gets `conversations.account_denied`, and friends
  and API keys may not use them. Like memories, the record is readable by
  those devices and not encrypted end to end between desktops.
- **The choices** *Keep a record of my conversations* and *Let Martlet search
  the record on its own* are the `conversation-history` shared setting, each
  person's own, so they are the same on all your computers. With *Keep a
  record* off, nothing new is recorded anywhere; the record you have still
  syncs, so a deletion reaches every computer.

The Conversations page shows the sync's line (`HistorySyncStatus`): how many
exchanges are kept the same on how many hosts, when it last checked, what came
from or was deleted because of your other computers, hosts that don't answer,
are too old or don't know you on this PC yet.

Checked locally with `conversation_sync_selftest` (MCP): two real gateways and
four simulated desktops with real records on loopback, covering recall and
search on another computer, an edit, a deleted message and conversation that
never come back, offline edits of one message on two computers, a host that
missed a change and restarts with its copies, a new computer, an exchange from
a newer Martlet, another account's device refused, deleting everything, no
conversation text in the sync state and an unsigned request refused. The
desktop's sync with real paired hosts, the Linux host's files and two real
computers are **NOT RUN**.

## One memory on every computer

What Martlet remembers is the same on all your computers, so whichever one is
your companion PC recalls what was said on any of them. The facts travel
through your paired hosts, the same way as [the rest of Martlet](CLUSTER.md), while
memory is ON (the `memory` shared setting, the same everywhere) and **Keep
Martlet the same on all my computers** is on. Each computer still keeps its own
local store, in the folder chosen on that computer.

- **The network copy** (`Martlet.Core.Sync.SharedMemories`): one
  last-writer-wins entry per fact ID with the fact exactly as the store keeps
  it (content, provenance, retention and times, as `MemoryFactJson`), its
  revision, the time and the computer that wrote that version. A forgotten fact
  is a tombstone: the revision it forgot plus one. Merging keeps, per fact, the
  highest (revision, time, forgotten, writer, content): an edit made on top of
  another wins by its revision, two edits of the same revision made apart keep
  the later one, and a deletion wins over the version it deleted. The merge is
  commutative, associative and idempotent, so every copy converges. JSON, snake
  case, schema 1, at most 512 MiB, 200,000 facts (two full stores), 200,000
  tombstones and 256 KiB per fact. The whole copy is read into memory, so it
  keeps a byte bound. Hosts never look inside a fact, so a newer Martlet's
  facts pass through them and through this version's desktops unchanged.
- **The sync** (`Martlet.Core.Sync.MemorySyncNode`, every 30 seconds and a few
  seconds after the Memory window closes): the desktop reads each paired host's
  copy when its digest changed, then, only while nobody else uses the store, opens
  it once: a fact saved or edited here since the last sync is a change made here,
  and a fact gone from the store (deleted, forgotten by remembering or expired)
  is forgotten everywhere. It merges in the hosts' copies in one pass, forgets
  expired facts and, when every computer's facts together don't fit one store
  (100,000), the oldest facts picked out of conversations (never one the owner
  typed). It counts the facts that didn't fit, and the status line and the log
  say how many (`Memory is full: N didn't fit`). Then it makes the store hold
  exactly the newest version of every fact in one commit
  (`MemoryStore.MergeAsync`). Then it gives every host whose copy differs the
  merged one.
- **Never in a conversation's way**: the sync skips its turn while Martlet replies,
  while you are talking to it or while the Memory window is open, and it never
  waits for the store: if recall, remembering or the Memory window holds it, the
  sync tries on the next check. Network reads and writes happen outside the
  store, which the sync holds only for the moment it takes to read and update it
  on disk, off the window's thread.
- **What this PC keeps** (`memory-sync.json` in Martlet's data folder): which
  store it synced (a new memory folder is a new store: it takes every fact again
  and forgets nothing), and for each fact only its ID, revision, a SHA-256 digest
  and which computer wrote it, plus the agreed tombstones. No fact text is kept
  there; the facts stay in the memory store.
- **Each host** keeps `memories.json` beside `host.json` (0600, gateway service
  owner; not part of the approved configuration) and serves
  `GET /martlet/v1/memories`, `GET /martlet/v1/memories/digest` and
  `POST /martlet/v1/memories` (merge and return) to paired devices only, over
  their pinned, signed connection; API keys for other apps may not use them.
  Like the shared API keys and voiceprints, the facts are readable by every
  paired device and not encrypted end to end between desktops.
- Memory OFF on all computers stops remembering and recall everywhere and pauses
  the sync (the store isn't opened); facts already kept on hosts stay there.

*Devices > Settings for all devices* shows `MemorySyncStatus`: how many facts
Martlet remembers, on how many hosts they are the same, when it last checked and
how many facts it took from or forgot because of your other computers.

Checked locally with `memory_sync_selftest` (MCP): two real gateways and three
simulated desktops with real memory stores on loopback, covering recall on
another computer, an edit, a deletion everywhere that never comes back, offline
edits on two computers, a host that missed a change, a new computer, an expiring
fact, a newer Martlet's fact, a fact that belongs to a voice (and is then made
everyone's), a new memory folder, more facts than one store holds, a fact in
one account's memory space that stays apart from other spaces and the old
document and survives a host restart, no fact in desktop data folders and an unsigned request refused. The
desktop window's status line was checked through `-Desktop`. The desktop's sync
with real paired hosts, a conversation recalling and remembering around a sync,
the Linux host's file and two real computers are **NOT RUN**.

Delete, save/edit/purge, memory configuration, pause, lock, Stop,
output/configuration change, conversation close and app exit advance the app
retrieval generation and cancel the old operation. The foundation store
revision check separately invalidates a source/index/cache result changed in
flight. Lock, pause, mute, a configuration change, conversation close and app
exit cancel pending remembering; Stop and window deactivation do not, because
the exchange already finished. Turning memory off or changing its configuration
drops anything still being remembered.

### Memory spaces on every host

For [accounts and households](ACCOUNTS.md#memory-spaces), each host also keeps
one memory document per **memory space**, so every host keeps every person's
memories apart. Desktops use them as [Memory spaces on the desktop](#memory-spaces-on-the-desktop)
says.

- **Space IDs** (`Martlet.Core.Sync.MemorySpaceId`): `household`,
  `account-<32 hex>` or `character-<32 hex>` (the ID in lowercase hex). Hosts
  refuse any other ID with `request.invalid`.
- **Routes**: `GET /martlet/v1/memories/spaces/{space}`,
  `GET /martlet/v1/memories/spaces/{space}/digest` and
  `POST /martlet/v1/memories/spaces/{space}` (merge and return). Each space is
  today's `SharedMemories` document with the same limits and merge. The answers
  name the `space`. A space the host never kept reads as an empty copy; a merge
  that brings no fact makes no space.
- **Who may use them**: paired member devices over their pinned, signed
  connection. Friends get `access.friend` and API keys get `key.scope`, as for
  the single document. An access hook (`GatewayMemorySpaces.Access`) then
  decides which device may read or write which space; a refusal is
  `memories.space_denied` (403). The household's account directory on the
  host (`accounts.json`, see [accounts](ACCOUNTS.md)) decides it
  (`GatewayMemorySpaceAccess.cs`):
  - `household`: every paired member device.
  - `account-<id>`: only a device where that account is signed in (a device
    binding in the directory). An account the host doesn't know yet, or a
    removed account, is refused. The account's settings on hosts use the same
    rule.
  - `character-<id>`: a device where someone of the household is signed in.
    The directory doesn't record yet whose a character is or how it is shared;
    sharing narrows this.

  A POST needs read and write access, because it returns the merged space.
- **Limits**: a host keeps at most 64 spaces. A new space past that is
  `memories.spaces_full` (409); the spaces it keeps still take changes.
- **On a Linux host** each space is `memories-<space>.json` beside `host.json`
  (0600, gateway service owner).
- **Client** (`HostMemories.cs`): `ReadMemorySpaceAsync`,
  `ReadMemorySpaceDigestAsync` and `MergeMemorySpaceAsync`. They refuse a bad
  space ID before they send anything. Hosts older than spaces answer
  `request.invalid`.
- The old `/martlet/v1/memories` document and `memories.json` hold the owner's
  memories from before accounts. A device where the owner is signed in may use
  them, and so may a device where no account is signed in yet (a desktop on an
  older Martlet). A device where only other people are signed in gets
  `memories.space_denied`. The host never copies between the old document and a
  space: the owner's desktops keep them the same (below).

### Memory spaces on the desktop

Each person's memories are their own ([accounts](ACCOUNTS.md#memory-spaces)).
A desktop keeps **one memory store per memory space** of the account signed in,
and the space tells who remembers a fact. `voice_id` still tells whom the fact
is about.

- **The spaces of an account** (`MemorySpaces.Resolve`): the **active space**,
  where remembering writes, is the account's own `account-<id>` (or the active
  character's own `character-<id>`, when that character remembers on its own or
  together with others). Recall also reads `household`. (Character spaces that
  other people share are a seam, `MemoryAccount.Shared`; sharing leaves it empty,
  so only a character shared together reads its own space.) An account's own space is never
  shared, because the same rule guards the account's personality and prompts on
  hosts. This device may change the active space, the household and its own
  character spaces. Facts that other people share are read-only here.
- **Where each store is** (`Martlet.Core.Sync.MemorySpaceFolders`): the account's
  own space follows its memory setting. By default it is
  `<data>\accounts\<32 hex>\memory`; a custom folder stays the custom folder.
  Every other space is `<data>\memory-spaces\<space>`. The sync state of each
  space is `<data>\memory-sync\<space>\memory-sync.json`.
- **Recall stays as fast as before.** Recall opens only the active space's
  store, as it did with one store. The other spaces are read-only snapshots in
  memory (`MemorySnapshot`, the store's facts and lexical index). They are loaded
  at start, at sign-in or a switch (`DesktopMemoryService.UseAccount`, then
  `LoadSpacesAsync`, in the background) and again after anything changes that
  space. A recall before they load reads the active space only and never waits.
  The best matches of all spaces come first (by score, then the active space),
  then the newest facts of all spaces (the speaker's and everyone's first). The
  memory block in the request is the same as before: no space label, so the
  start of each request doesn't change. Each store has its own lock, so a sync of
  the household never holds up recall.
- **Remembering and the reply's tools**: a new fact goes to the active space (a
  fact about you goes to `household` while *Share new memories about me* is on). An
  update or a forget acts in the fact's own space when this device may change
  it; remembering skips a fact it may not change, and `manage_memories` (or a
  `memory_*` check-in tool) says that only its owner can change it. A
  near-duplicate of a fact in any space recall reads isn't saved again. `find`
  searches every space recall reads and names where a fact is kept (`kept`).
  The tool descriptions don't change.
- **The Memory window** lists the facts of every space recall reads. A fact kept
  in another space than the active one says where on its line (*· Household*),
  the details say *Kept in*, and the facts line counts them
  (*3 in Household*). *Add fact* saves in the active space; changes and
  deletions act in each fact's own space; facts shared with you can't be
  changed. *Delete everything* deletes the active space's facts only (*The
  household's memories stay*). *Share selected with* copies or moves facts
  between spaces ([Sharing memories](#sharing-memories)).
- **The sync, by space** (`MainWindow.MemorySync.cs`): every space the account
  reads syncs on its own, with its own `MemorySyncNode` and state, through the
  host's space routes. The owner's space also syncs with the old single
  document, so desktops on an older Martlet keep the owner's memories the same.
  A host older than spaces syncs only the owner's space, through the old
  document; for other spaces the status asks you to update it. A host that
  doesn't know the account on this PC yet (`memories.space_denied`) is tried
  again on the next check. After a switch, the spaces of the account signed in
  before get one more sync (it stays signed in on this device).
  `MemorySyncStatus` counts the facts of each space (*42 yours, 3 household*).
- **Moving in** (once, for the owner): this PC's store from before accounts
  (`<data>\memory`) and its `memory-sync.json` move into the owner's space the
  first time that store opens (`MemoryStore.MoveStore`, one rename while nobody
  holds the store; the store ID stays, so the next sync finds nothing new). A
  custom memory folder is already the owner's space. When the owner's space
  already has a store, the old one stays where it is, and the desktop log says
  so. Other accounts never take it.
- With no account signed in (tests and fixtures), the desktop keeps one store in
  the memory setting's folder, as before accounts, and doesn't sync it.

`memory_status` and `memory_sync_status` (MCP) count each space (`spaces`),
never a fact. `memory_sync_selftest` covers the owner moving in, the household
shared between accounts, refusals of another account's space and of the old
document, and a desktop on an older Martlet. Recall and remembering over spaces
are covered by `MemorySpaceServiceTests`. Real computers signed in as
different people are **NOT RUN**.

### Sharing memories

People in one home can share memories with each other ([accounts](ACCOUNTS.md#sharing)).
Nothing here runs during a reply.

- **The household's memories** (`household`) are read by every character of
  every person. Put a fact there to make it known to the whole household.
- **Share selected with** (Memory window, shown with accounts): select facts,
  choose *The household's memories*, another person (*Alex's memories*) or a
  character shared together, then **Copy** or **Move**. A copy has the same
  words, the same person it is about and the same keeping. A space this PC
  keeps (the household, the character in use) gets new facts here, and the
  memory sync takes them to your other computers. Another person's memories go
  through your paired hosts: the host's give route
  (`POST /martlet/v1/memories/spaces/{space}/give`) only adds new facts and
  never shows that person's facts. That person's computers take them on their
  next sync. Without a paired host, Martlet says so and shares nothing.
  **Move** deletes the facts here only after the copy is made; facts shared
  with you can only be copied. `MemoryShareStatus` says what happened, with
  counts and the kind of place only.
- **Share new memories about me with the household** (Memory window): when it
  is on, a new fact Martlet picks out of a conversation about you (a voice
  People links to your account) is saved in the household's memories instead
  of yours, so every character remembers it. Facts you type, and changes to
  existing facts, stay where they are. It is your choice on all your computers
  (the household entry `sharing.<account>`, `new_facts_about_me`).
- **A character shared together** remembers in its own space, `character-<id>`,
  for everyone who talks to it. Each fact keeps whom it is about, so it knows
  whose each fact is. Your other characters don't read that space. When its
  owner makes it private again, it keeps the space and what it remembers. The
  hosts then let only the owner's devices use it.

Checked locally: `MemorySharingTests` (where a new fact about you goes, the
copies given to another space) and `memory_sync_selftest`. The selftest covers
a fact given to another account's space through the give route on two real
gateways, and a character space that follows the household sharing entry.
The Memory window's sharing was checked through `-Desktop` on a disposable
data folder (copy to the household; a move to another person without a host is
refused and keeps the fact). A gift between two real computers and a
conversation that remembers a fact about you are **NOT RUN**.

## Local validation

Use the exact SDK in `global.json`, committed project locks, an isolated CLI
home and a unique local `C:` artifact directory:

```powershell
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet restore tests\Martlet.Memory.Tests --locked-mode --artifacts-path $artifacts
dotnet build tests\Martlet.Memory.Tests --no-restore -c Release --artifacts-path $artifacts
dotnet test tests\Martlet.Memory.Tests --no-build -c Release --artifacts-path $artifacts `
  --results-directory "$artifacts\results"
```

The project uses only the runtime/BCL and `Martlet.Core` (for `SearchTerms`);
the test project uses existing central
test pins. Both now join `Martlet.slnx`; Desktop and package graphs reference
the runtime assembly. Direct Memory tests remain useful for focused storage
validation. Desktop/Core/Updates suites cover production-path WPF, settings
migration/recovery, automatic recall/remembering/input encoding and lifecycle invalidation.
All validation remains local; no remote workflow was added or run.

## Remaining gates

- Decide whether the record of conversations should travel to your other
  computers (it stays on the PC where each conversation happened) and whether
  it, like facts, should be encrypted at rest; evaluate recall's reference
  phrasing beyond English with real conversations.
- Qualify the memory sync between real computers and real paired hosts, and
  decide whether facts should be encrypted end to end between desktops.
- Design explicit backup/restore semantics and prove compatible restore and
  deletion behavior; this foundation intentionally makes no backup.
- Run physical interruption/filesystem/storage tests and clean-machine
  lifecycle tests; controlled IO interruption is not power-loss qualification.
- Evaluate usefulness, false retrievals and privacy comprehension with consented
  held-out real-user tasks and resource budgets before G4.
- Reassess embeddings or a model reranker only after a labeled evaluation with
  consented real conversations demonstrates material benefit over the in-process
  lexical reranker, and the model/license/index deletion lifecycle is reviewed.
  Neither may run on the reply's path. No embedding/vector adapter is present in
  this slice.
