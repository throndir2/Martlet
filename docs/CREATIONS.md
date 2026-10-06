# Creations

Martlet makes things: songs and pictures now, other kinds later. Everything it makes is a
**creation**, kept the same on all your Martlet computers, and Martlet performs,
shows or activates any of them itself when you ask in conversation ("sing the rain
song again", "start from the chorus"). There is no Play button anywhere: the
**Creations** page lists what Martlet made so you can read, rename or delete it.

## What a creation is

A creation (`Martlet.Core.Creations.Creation`) has:

| Part | What |
| --- | --- |
| `id` | 128 random bits as 32 hex digits; its first 12 (`key`) are what Martlet's tools and the page use |
| `kind`, `kind_version` | A registered kind such as `song`, and which version of that kind's metadata it has |
| `title`, `summary`, `text` | Its title (one line, at most 120 characters), a short summary and its readable text, such as a song's lyrics with `[verse]`/`[chorus]` tags (at most 16 KiB) |
| `duration_ms` | How long it lasts when performed, for kinds that have a length |
| `created_at`, `created_by` | When, and which Martlet made it: the computer's device ID and name, and the voice and personality Martlet had |
| `metadata` | The kind's own JSON object (at most 16 KiB): for a song its tempo, key and lines; larger data such as word timings goes in an asset |
| `assets` | Named parts (`vocals`, `backing`, `mix`, `mouth`...), each stored once by the SHA-256 of its bytes, with its media type (`audio/flac`, `audio/wav`, `application/json`, `text/plain`, `image/png`, `image/jpeg`, `image/webp` or `application/octet-stream`; nothing in an asset is ever run) and the SHA-256 of each 3 MiB piece |
| `auto_cleanup` | Whether Martlet may delete it by itself, oldest first, when a new creation needs the room (the kind's choice, kept in the entry so every computer applies the same rule) |

### Kinds

The feature that makes a kind registers it once when Martlet starts
(`CreationRegistry.Shared.Register`), before any conversation:

- its name, what one and many are called and what Martlet does with one (`sing`,
  `show`, `activate`), and its icon;
- its assets: names, media types, size limits and which are required, and the
  size limit of one creation;
- whether old ones may be cleaned up;
- how one is described to the Thinking model (`list_creations`) and shown on the
  Creations page (titled blocks of text, such as a song's sections);
- one fixed sentence on what `perform_creation`'s options mean for it.

How a kind is performed is an `ICreationHandler` the feature attaches with
`CreationRegistry.Shared.Handle(kind, handler)` while it can do it (songs: the
conversation that sings them) and detaches when it can't. A handler gets the
creation, the options the Thinking model passed and the creation's assets on this
computer (checked against their SHA-256), starts the work and returns at once with
a sentence for the model.

A creation of a kind this Martlet doesn't know (made by a newer Martlet) is kept
and passed on like any other; the page says to update Martlet to use it here.

**Songs** (`SongCreations.Kind`, registered by the desktop at startup): assets
`mix`, `vocals` and `backing` (FLAC, sample-aligned), `map` (JSON: the lines with
their sections and times, the sung words with their times and the beat grid) and
`mouth` (JSON, optional: the mouth track made once from the vocals for lip sync);
metadata with what it is about, its style, voice, tempo, key, counts, where its
word times and mouth track came from and the engine. `perform_creation` options:
`{"from": "start"}`, or `resume`, a section such as `chorus`, `line:N` or a time
like `1:05` (the same as `play_song`). Each conversation attaches the handler
that sings it ([singing in conversation](CONVERSATION.md#singing-in-conversation)).

**Reports** (`ResearchReports.Kind`, registered by the desktop at startup): web
research reports ([web research](CONVERSATION.md#web-research)). One asset,
`report` (text/plain: the report in Markdown with its numbered sources, at most
128 KiB); the title, the one- or two-sentence summary and the report as text (at
most 16 KiB) in the entry, and the number of sources in metadata. Martlet may
clean old ones up. `perform_creation` takes no options: the desktop's handler
writes the report as a plain web page (no scripts, links only to http and https)
to `research-reports\<key>.html` in the data folder and opens it in the default
browser.

**Pictures** (`PictureCreations.Kind`, registered by the desktop at startup): one
asset, `image` (PNG, JPEG or WebP, at most 24 MiB); its text is the description it
was drawn from and its summary what you asked for; metadata with its shape, size,
engine, model, where it was drawn, seed and seconds. Martlet *shows* a picture: the
conversation's handler puts it in the talk window (no options), and the Creations
page shows it with its details ([Pictures](PICTURES.md)).

### Audio

Audio assets are FLAC (`Martlet.Core.Audio.FlacCodec`, built into Martlet: 16-bit
mono or stereo, every frame's CRC and the audio's MD5 checked; files play in any
FLAC player and libFLAC's files decode in Martlet). FLAC is lossless: the song
plays exactly as it was made.

A 60-second song at 48 kHz has a stereo mix, stereo backing and mono vocals:
**27.5 MiB as WAV** (11.0 + 11.0 + 5.5 MiB). Measured on FIXTURE - NOT AI
music-like signals, FLAC keeps a stereo track at about 60% (6.6 MiB) and mono
vocals with silences at about 30% (1.7 MiB), so a 60-second song is **about
15 MiB as FLAC** (13-19 MiB depending on the music), plus a small lip-sync
asset. A real song's sizes are NOT RUN yet.

### Limits

| Limit | Value |
| --- | --- |
| Creations | 256 live, 1,024 deletions remembered |
| Size of all creations | 2 GiB (about two hours of songs) |
| One creation | 512 MiB (a kind may set less) |
| One asset | 256 MiB (a kind may set less), travelling in 3 MiB pieces |
| The list | 8 MiB of JSON |

When a new creation doesn't fit, Martlet deletes the oldest creations whose kind
allows it (deletions travel like any other); when that isn't enough, it isn't
added and the owner is asked to delete some in Creations. If two computers fill
the list while apart, the merged list keeps the creations Martlet may not clean
up first, newest first, so every copy agrees.

## On all your computers

Creations follow the same design as the
[shared character models](CLUSTER.md#the-shared-character-models): a list of
last-writer-wins entries with tombstones (`CreationLibrary`, the same hybrid
revisions and merge rules as the cluster plan, so copies converge in any order)
and assets moved piece by piece, content-addressed by SHA-256.

- **Each desktop** keeps `creations.json` in Martlet's data folder and each asset
  once in `creations\<sha256>.bin`. Pieces copied from another computer wait in
  `creations-incoming` until the asset is complete and its SHA-256 checks; then it
  moves into place in one step. Files no live creation uses are deleted.
- **Each host** keeps `creations.json` and every piece of every live creation as
  `creation-chunk-<sha256>.bin` beside `host.json` (0600, gateway service owner;
  not part of the approved configuration). A host performs nothing; it keeps
  everything so a desktop that was off when something was made catches up from any
  host. Pieces no live creation uses are deleted.
- **`creations-sync.json`** on each desktop records the last sync: when, what it
  said and each paired host's state and which creations it holds completely (IDs
  only), for the Creations page and MCP's `creations_status`.

| Endpoint (paired devices only, over the pinned, signed connection; API keys may not) | What |
| --- | --- |
| `GET /martlet/v1/creations` | The host's list and the SHA-256 of each piece it holds (`present`) |
| `GET /martlet/v1/creations/digest` | The list's digest and the digest of `present`, so desktops read the list only when something changed |
| `POST /martlet/v1/creations` | Merge a desktop's list in; returns the merged list and `present` |
| `GET /martlet/v1/creations/chunks/<sha256>` | One piece (`chunk.missing` when the host has none) |
| `POST /martlet/v1/creations/chunks/<sha256>` | Send `{"data_base64": ...}` for a live creation; refused (`request.invalid`) for a wrong SHA-256, a piece no live creation has, a wrong length or anything else in the body |

Every 30 seconds while any host is paired and Martlet is the same on all your
computers (and two seconds after a creation is made, renamed or deleted here) the
desktop's `CreationSync` reads each host's digests and, when they changed (or
something here is still copying), its list; merges every list into its own;
copies each asset it lacks from hosts that hold its pieces (an interrupted copy
continues where it stopped); deletes what was deleted elsewhere; gives every host
whose list differs the merged list and sends each host every piece it lacks.
Desktops and host PCs alike keep everything, so Martlet on any of your computers
can perform any creation. Nothing is written while no host is paired or while the
switch is off. A host older than creations refuses with `request.invalid`; the
page asks you to update it.

Creations are the owner's own, like memories: only paired devices may read them,
nothing in them is a secret Martlet uses, MCP never returns a title, text, voice
or personality, and the log names counts and short IDs only.

## Martlet's tools

While at least one kind is registered, every reply on a Thinking route that does
function calling gets two of Martlet's own tools, after `think_longer`,
`cancel_thinking` and `search_conversations`, always the same two in the same order with the same texts
(built from the registered kinds, never from the creations), so the start of
every request stays the same for prompt caches:

- `list_creations(kind?, query?)`: the creations newest first (at most 30): short
  id, kind, title, the kind's description, length, the day it was made, whether
  Martlet can perform it here now and why not.
- `perform_creation(id, options?)`: hands the creation to its kind's handler. An
  unknown id, a kind this Martlet doesn't know, no handler here or a creation
  still copying to this computer get a clear sentence for the model instead.

Neither asks first. The creations list is read from disk only when a tool is
called, so a reply that doesn't use them costs nothing more, and with no kind
registered the request is exactly as before.

## The Creations page

**Creations** sits between Companion and Diagnostics. It shows how many
creations there are and how large, whether they are shared with your other
computers, and each creation as a card: its kind's icon, title, kind, length,
size, when and on which computer it was made and where it is (this PC, and how
many hosts hold it). The selected creation shows its summary, its text in its
kind's sections, its details (metadata and parts), what to ask Martlet ("Ask
Martlet to sing it.") and **Rename** and **Delete** (deletion travels to every
computer as a tombstone). A note reads "Ask Martlet to sing or show any of
these.", and the empty page "Things Martlet makes, like songs, appear here."

## Qualification

`creations_check` (MCP) checks the tools in process and rehearses the sync with
two real gateways and three simulated desktops on loopback with the production
store, sync engine and paired client, using the FIXTURE - NOT AI test-tone kind:
FLAC sizes and exact decoding, sharing every piece, skipping unchanged hosts, a
new desktop and a relay host, an interrupted copy, performing a creation on
another computer, an unknown kind passing through, rename and delete everywhere,
a stale copy, a host restart, piece checks and an unsigned request refused. The
Creations page was checked through MCP on a disposable data folder (empty state,
listing, selecting, renaming and deleting test tones). Unit tests cover the codec
(against itself; libFLAC was checked both ways by hand), the merge rules, cleanup
and the store. The desktop's sync with real paired hosts, the Linux host's files,
two real computers, a real song, a kind's real handler and a Thinking model
calling the tools are **NOT RUN**.
