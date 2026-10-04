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
its own local store. There is no embedding, vector database, cloud copy,
backup or background timer beyond that sync. Screen and
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
| Facts | 512 |
| Fact content | 4,096 UTF-16 characters and 8,192 UTF-8 bytes |
| Authoritative/export JSON | 8 MiB |
| Query | 256 characters, 512 UTF-8 bytes and 24 distinct terms |
| Retrieval result | 1-20 facts |
| Derived query cache | 64 revision-bound queries |
| Expiring retention | At most 366 days when saved/edited |

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

## Lexical retrieval and exact invalidation

Retrieval uses only a deterministic in-memory lexical index rebuilt from the
authoritative facts. Unicode letter/digit tokens are lowercased; overlong token
runs are not indexed. Scores combine term frequency and inverse document
frequency. Results are bounded, deterministic and return the full fact plus its
creation/edit provenance and the exact store revision. There are no embeddings,
semantic summaries, external rerankers, vector stores or provider calls.
The full ranking (score, matched terms, newest modification time, then UUID)
is applied before the requested result limit.

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

The held-out synthetic fixture at
`tests\Martlet.Memory.Tests\Fixtures\retrieval-held-out.json` keeps its facts and
queries outside the retrieval implementation. It checks bounded top-result
behavior and provenance across server-region, preference, schedule, device and
care facts. It is deterministic contract evidence, not a real-user usefulness
score or justification for embeddings.

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
best matches for that user input and fills up to twelve facts with the most
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
it wouldn't fit the context.
The model answers in a strict line format: `REMEMBER: <fact>`,
`REMEMBER V<n>: <fact>` (a fact about another voice heard), `UPDATE <n>: <fact>`,
`FORGET <n>` or `NOTHING`, at most three lines. Desktop
validates every line (single line, <=300 characters, real words, in-range
numbers, no memory labels), skips near-duplicates of the same person's facts,
applies updates/forgets only
to the exact fact revisions it showed, and saves new facts with `conversation`
provenance until deleted. A full store drops its oldest conversation fact, never
a fact the user typed. The conversation window shows what was remembered
(*Remembered: … (Sam)*), and Memory lists it.

A model that declines or answers with nothing simply has nothing to remember;
an answer cut off by the max reply length keeps the lines it finished. If the
store is briefly in use (recall, the Memory page) or a fact changed meanwhile,
remembering waits and tries twice more. When it still fails, the conversation
says why once (*Couldn't update memory.* plus the reason, such as the
provider limiting requests or the memory folder being unusable); the same
problem on later exchanges is only logged (`Remembering failed (<code>)`) until
remembering works again.

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
  belong to. The rest of the twelve is filled with the speaker's facts and
  everyone's before other people's, newest first (by recency alone when nobody
  was recognized). Each recalled fact starts with whose it is: the voice's name,
  else its tag (`[V3]`), or `[a forgotten voice]`. When one does, the block also
  says what that means (Companion › Prompts › *Whose memories*: the name in
  brackets is whose the fact is; keep whose fact is whose and be discreet with
  someone's personal facts while another person talks). It is sent only with a
  block that has such a fact, in that message's notes, so it never changes the
  start of a request.
- **Memory window.** *Belongs to* chooses whose a fact is when you add or update
  it: *Everyone*, or a voice Martlet knows. A new fact is yours (the voice marked
  *This is my voice* on People) unless *Show* lists one voice's facts; then it is
  that voice's. *Show* lists all facts, everyone's, one voice's, or those of
  forgotten voices. The list starts each fact with whose it is, and the details
  say *Belongs to*. The status line (`MemoryFactStatus`) counts the facts, how
  many belong to how many people and how many to forgotten voices, never a
  name or a fact.
- **People.** *What Martlet remembers about them* on each voice opens Memory
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

## Conversation history

Facts are what Martlet distills; the **record of conversations** is what was
actually said. While memory is ON and **Keep a record of my conversations**
(Companion › Memory › Conversation history, on by default) is on, every
finished exchange of a typed, push-to-talk or always-listening turn (what you
typed or said, who said it when Martlet recognized a named voice, and Martlet's
reply) is kept on this PC, as is a reply Martlet starts on its own to bring up
finished background work. A conversation runs from the talk window opening, or
**Refresh context**, until the exchanges kept in mind are cleared (Refresh
context, pause, lock, closing the talk window). Not recorded: screen and camera
glances, what this PC plays (only the user's own words of such a message), a
`[pass]` (what always listening heard wasn't meant for Martlet), failed,
refused or stopped replies, and anything while memory or the choice is off.
Turning it off keeps what is already recorded until you delete it.

**On disk.** The data folder's `conversations` folder holds one JSON Lines file
per month (`history-2026-10.jsonl`, by UTC month), one line per exchange:
`{"v":1,"id":…,"conversation":…,"at":…,"kind":"typed|spoken|report","speaker":…,"user":…,"reply":…}`.
Each exchange is appended (and flushed to disk) in the background after its
reply, one at a time, so the next reply never waits for the disk; exiting
Martlet waits a moment for the last one. Earlier lines are never rewritten by
appending; a line cut short by a crash is skipped when the record is read again
and the next exchange starts on its own line. Text is kept up to 8,192
characters of what you said and 16,384 of the reply (the reply's own bound),
with control characters other than line breaks and tabs made spaces. Like the
fact store it is plain local data in your profile: not encrypted, not synced to
your other computers, not uploaded and not part of configuration backup.
`conversation-history.json` beside it holds this PC's two choices.

**Reading it.** The record is read once, in the background, when a
conversation opens (or the history window or the Memory page needs it) and
kept in memory with a lexical (BM25) index of what was said; nothing is
embedded or sent anywhere. The newest 100,000 exchanges are indexed; older
ones stay in their files. With 20,000 synthetic exchanges (7.3 MB) reading takes
about half a second, once, and a search about 7 ms (`conversation_history_check`).

**Bringing back what was said.** When your message refers to an earlier
conversation (*do you remember…*, *remember when…*, *what did we talk about
yesterday?*, *did I tell you about…*, *in our last conversation…*, or something
said or talked about with a time such as *last week* or *3 days ago*; English
phrasing), up to three matching exchanges of other conversations go in that
message's notes (see [Conversation › Prompt caching](CONVERSATION.md#prompt-caching-and-the-request-layout))
between `[MARTLET_PAST_CONVERSATIONS]` labels, with today's date and each
exchange's day and time (each side cut to 280 characters, brackets made
parentheses so recorded text can't open or close a block), introduced by
Companion › Prompts › *Past conversations*. The best matches for the message's
own words come back (common words and words about remembering, talking or time
left out; about half of the remaining words, at most three, must match, so one
common word alone brings back nothing), within the time it names; with only a
time (*what did we talk about yesterday?*) the latest exchanges of that time do.
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
conversations with their dates (as data, never instructions) or says nothing
was found and not to guess. It is off by default because its description (458
bytes, about 153 estimated tokens) goes at the start of every request: providers
cache it after the first reply of a conversation, but that first reply reads it
too.

**Seeing and deleting it.** Companion › Memory › **Open conversation history**
lists the conversations, newest first, shows what was said in the one selected,
searches everything said (matching exchanges marked ▶) and deletes one
conversation or everything, each asked first (No by default). Deleting rewrites
only that conversation's month files (through a temporary file and an atomic
replace) or removes the files; facts remembered from a conversation stay in
Memory until you delete them there. Deletion is not a secure wipe of old disk
blocks.

MCP: `conversation_history_status` (choices, what replies are offered and
counts, never content) and `conversation_history_check` (the production record,
recall and search on synthetic conversations); `HistoryStatus` and
`HistoryWindowStatus` read as text and `OpenHistory` opens the window
([MCP](MCP.md)).

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
  case, schema 1, at most 12 MiB, 1,024 facts, 4,096 tombstones and 64 KiB per
  fact. Hosts never look inside a fact, so a newer Martlet's facts pass through
  them and through this version's desktops unchanged.
- **The sync** (`Martlet.Core.Sync.MemorySyncNode`, every 30 seconds and a few
  seconds after the Memory window closes): the desktop reads each paired host's
  copy when its digest changed, then, only while nobody else uses the store, opens
  it once: a fact saved or edited here since the last sync is a change made here,
  and a fact gone from the store (deleted, forgotten by remembering, expired or
  dropped to make room) is forgotten everywhere. It merges in the hosts'
  copies, forgets expired facts and, when every computer's facts together don't
  fit one store (512), the oldest facts picked out of conversations (never one
  the owner typed), and makes the store hold exactly the newest version of every
  fact in one commit (`MemoryStore.MergeAsync`). Then it gives every host whose
  copy differs the merged one.
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
everyone's), a new memory folder, more facts than one store holds, no fact in desktop data folders and an unsigned request refused. The
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

The project uses only the runtime/BCL; the test project uses existing central
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
- Reassess embeddings/reranking only after a labeled evaluation demonstrates
  material benefit and model/license/index deletion lifecycle is reviewed.
  No embedding/vector adapter is present in this slice.
