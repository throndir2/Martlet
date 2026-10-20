# MCP: Other

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Other* area of `martlet_guide` (situations, greetings, search and other checks). The [index](../MCP.md#pages) lists the other pages.

## Local MCP control (Windows)

`long_talk_check` shows how long one turn of talking and one spoken reply may
be, and hears a long recording the way the conversation does. `limits` gives
`longestTurnSeconds` (600: a safety stop for a microphone left open, not a
limit on talking), `captureHardMaximumSeconds` (900),
`speechToTextPieceSeconds` (25: the most one speech-to-text request hears),
`recordingHardMaximumBytes` (25,000,000, OpenAI's upload limit),
`messageCharacters` (262,144), `replyLifetimeSeconds` (1,200),
`replySpokenSentences` (512), `replySpokenTextBytes` (262,144) and
`messageRecordingSeconds` (30: a longer turn goes to models that hear as its
transcript only). It puts `phrases` (default `parakeet_check`'s; up to 8) said
by a Windows voice (System.Speech rendered to memory, never played) one after
another into `seconds` (30-600, default 70) of talking, and cuts it with the
production splitter (`BoundedWaveAudio.Split`). It returns
`recordingSeconds`, `phrasesSaid`, each piece's `seconds`, each cut's
`atSeconds` and `quieterThanRecordingDb`, and `everyPieceFitsOneRequest`,
`piecesCoverEverySample` and `everyCutInAPause` (at least 20 dB quieter than
the recording). With a Parakeet model downloaded in `speechDirectory` (default
the data directory's `speech` folder) and the sherpa-onnx runtime in
`martletDirectory`, `transcription` also has each piece transcribed through the
production `ParakeetEngine`: the joined `transcript`, its `wordErrorRate` and
`ok` (at most 20%); otherwise `ran` is false with the reason. `ok` needs more
than one piece, all three checks and a passing transcription when it ran.
Nothing is downloaded, recorded or played, and nothing leaves this PC.

`search_languages_check` (no arguments) rehearses search in languages other
than English and lorebook word forms with the production code (`SearchTerms`
in `src\Martlet.Core\Text`, `MemoryStore`, `ConversationHistory`,
`PastConversations` and `LorebookScanner`) on synthetic text in disposable
folders, and returns `{passed, failures, steps, note}`. The steps: English text
gets the search terms it always got (also with web addresses in it, which never
make it read as another language); Spanish, French, German, Portuguese and
Italian sentences read as their language; their light stems join word forms
(*gatti* and *gatto*, *Drachen* and *Drache*); Chinese and Japanese text is read
as pairs of characters (*東京大学* is *東京*, *京大* and *大学*); a question in each of
those languages and in Chinese, Japanese and Korean recalls the right memory
fact first and brings back the right earlier exchange (*¿Te acuerdas de lo que
hablamos sobre las tomateras?*, *你还记得我们聊过的黄山吗？*); ordinary messages
in those languages, and *Remember to buy milk.*, don't look in the record;
times such as *hace 3 días*, *vor 3 Tagen*, *3天前* and *어제* name the right
days; lorebook keywords match exactly with word forms off and other forms with
them on (*dragon* finds *dragons*, *red dragon* finds *Red Dragons*, a
case-sensitive *Rose* finds *Roses* but not *the sun rose*, *Drache* finds
*Drachen*, a `/regex/` key is unchanged, an entry's own choice wins); and the
SillyTavern export is unchanged without the choice and carries an entry's
choice back on import. It also times reading the terms of English text
(`microsecondsPer1000Characters`, under 50), recognizing a reference to an
earlier conversation (`microsecondsPerMessage`, under 50) and scanning 2,000
entries with word forms on (under 50 ms). It never opens the owner's data and
contacts nothing.

`search_engine_check` (no arguments) shows and checks the shared
[search engine](../SEARCH.md) that memory, app guides and past conversations use,
and returns `{passed, failures, steps, profiles, note}`. `profiles` has each
source's search profile (`memory`, `appGuides`, `pastConversationsRecall`,
`pastConversationsFind`): `fields` (`name`, `weight`, `lengthNormalization`),
`k1`, `positions`, `candidates`, `refined`, `scoring` (`combination` and each
feature's weight, `recencyHalfLifeDays`), `cutoff` and `diversity`. The steps,
on synthetic documents: each source has a profile; the first stage keeps
exactly the best candidates, best first, and leaves out what a filter refuses;
BM25F ranks a title match above a text match; a second field finds a document
by words it doesn't say (search words); an index without positions grows
while it is searched; reciprocal rank fusion puts a document both lists have
first and adds one only the second list has; and the first stage and ranking
of a query on 100,000 documents (`medianMicroseconds`, `p95Microseconds`;
passes under 10,000). Each source's own ranking is in `memory_recall_check`,
`app_guides_check` and `conversation_history_check`. It reads and writes nothing.

`meaning_search_check` (optional `measure`, a boolean, and `model`) rehearses
[finding by meaning](../SEARCH.md#finding-by-meaning) with the production code
(`VectorSet` and `FixtureEmbedder` in `src\Martlet.Core\Search`, `MemoryStore`,
the desktop's `MemoryUpkeep.cs`, `GuideIndex` and `OllamaEmbedder` in
`src\Martlet.Providers\Ollama`) on synthetic text in a disposable folder, and
returns `{passed, failures, steps, measured, note}`. The embeddings are
`FixtureEmbedder`'s (FIXTURE, NOT AI). The steps: a vector set lists the
nearest first, embeds a changed text again, drops a deleted document, is saved
and read back and refuses another model's file; 10,000 vectors of 768 numbers
are searched in milliseconds (`medianMilliseconds`, under 50); 7 held-out
questions in other words (*What should I cook tonight?* for *The user is
vegetarian.*) find their fact in the first 3 when fused but not by words
(`lexicalRecallAt3`, `fusedRecallAt3`); a question that names *Rex* or *Ana*
still finds that fact first; tidying groups *lives in Seattle* with *moved to
Portland last spring* by meaning only; a forgotten fact's vector is dropped;
`search_guide`'s index finds the blacksmith's upgrades for *How do I make my
weapons stronger?* with its meaning as relevance; the Ollama embedder, against a
loopback fixture, asks `/api/embed` with `num_gpu` 0, its threads,
`keep_alive` `1h`, `truncate` and nomic-embed-text's `search_query: ` start, and
tells a model that isn't downloaded apart; and loading is held back while
Ollama has 3 models loaded or one runs on the processor. With `measure: true`,
`measured` times the real model (`model`, default `nomic-embed-text`) in this
PC's Ollama on the same questions: `firstRequestMilliseconds`,
`queryMedianMilliseconds`, `queryMaximumMilliseconds`, `documentMilliseconds`
(per fact), `lexicalRecallAt3` and `fusedRecallAt3`. It runs only when the model
is downloaded and loading it holds nothing back, and otherwise says NOT RUN and
why; it never downloads. It never opens your data.

`conversation_sync_selftest` (no arguments) rehearses
[one record of conversations on every computer](../MEMORY.md#one-record-on-every-computer)
end to end with the production code (`src\Martlet.NodeLinkCheck`, mode
`conversations`, `ConversationRehearsal.cs`; returns `{exitCode, report}`): two
real gateways (`lab-record-1`, `lab-record-2`; Kestrel, pinned TLS, signed
requests, the household's account directory and in-memory conversation
storage) and four simulated desktops (`lab-desktop-a..c` signed in as Sam,
`lab-desktop-d` as Alex), each with a real record of conversations
(`ConversationHistory`) in a temporary folder, the desktop's paired client
(`HostConversationPeer`) and the real record sync (`ConversationRecordSync`).
Its steps: A records a trip conversation and a joke and both hosts keep the
month; B takes them, recalls the trip when asked (*Do you remember what we said
about Kyoto?*) and `search_conversations` finds the ryokan; B edits a reply and
A takes it; A blanks a message with a PIN and deletes the joke conversation, B
does the same and they never come back after more syncs, with no PIN left on
the hosts; offline edits of one reply on A and B end as B's later edit
everywhere; a host that was stopped while A recorded an exchange restarts with
its saved copies and gets it on A's next sync; new computer C takes the whole
record; an exchange a newer Martlet changed passes through B untouched and B
never writes its older line over it; D, where only Alex is signed in, is
refused Sam's record (`conversations.account_denied`); C deletes everything and
A and B do too; and the sync state keeps no conversation text while an unsigned
request is refused. Synthetic conversations; loopback only; the folder is
deleted. The desktop window and its 30-second sync, the Linux host's files and
two real computers are not covered.

`situation_status` shows the situation a companion PC is in
([Three plans](../RECOMMENDED_SETUPS.md#three-plans-normal-while-gaming-and-host-away);
optional absolute `dataDirectory`, default the current user's). It reads
`situation.json`, which the desktop writes when it starts on a companion PC
and when the situation changes. It gives `report` (`loaded` or `none`),
`updatedAt`, `situation` (`Normal`, `Gaming` or `HostAway`) and its `title`,
`normal` (where live Thinking runs normally), `now`, `nowKind` (`Host`,
`Hosted` or `ThisPc`) and `nowHost` (where it runs now; null in the Normal
plan), the whole `order`, `why`, `applied` (the conversation uses the moved
route from its next reply), `playsGames`, `game`, `away` (the hosts that went
missing), `backupAllowed`, `online`, `card` (while gaming, the graphics card
note, such as *2.4 GB free of 12.0 GB: gemma4:e2b stays loaded, so the change
back is quick.*) and `simulated` (FIXTURE). `rules` gives the game look (every
3 seconds), the game hold (90 seconds), the host missing and back times (30
seconds), the short-of-memory limit (1 GB) and the order. Host IDs, model and
provider names and a game's name only.

`situation_check` rehearses the production rules (`LiveSituations`,
`SituationRoutes`, `GameWatch`, `SituationPlans`) on FIXTURE facts, apps and
computers, **NOT a real game, host or model**. Its steps: Normal with no game;
a game on a PC used for games moves live Thinking to a host that answers, then
to the hosted backup when the host is away, then keeps this PC's own model when
online services are *Never*; a PC not used for games and Thinking on a host
move nothing; the game ends and live Thinking comes back; a host that is away
moves live Thinking to another host, the hosted backup, then this PC's own
model, or waits when nothing else can think; the host answers again; the card
is short of memory only below 1 GB free; the moved routes are valid, consented,
the same each time and keep *If Thinking fails*' key scope; the game watch
finds ELDEN RING from a Steam library, keeps it 60 seconds after a switch to
the browser and ends it after 90 seconds; and Recommended setup's three plans
on a FIXTURE network. `ok` is true when every step passed.

On the desktop, Companion › Thinking's *Now* card reads `ThinkingSituationNow`
(*Situation: While gaming — A game (ELDEN RING) runs on this PC, so live
Thinking leaves its graphics card for gpu-box (qwen3:8b). ...*). To drive it
without a game or a host that goes away, set `MARTLET_SIMULATE_SITUATION`
before you launch the desktop (FIXTURE): `1` uses this PC's own setup;
`gaming` (live Thinking on this PC's gemma4:e2b, gpu-box with qwen3:8b, NVIDIA
Build as the backup) and `host` (live Thinking on gpu-box, desk-host with
gemma4:e4b) use made-up computers, so their decisions only show and the
conversation keeps its route. Add `,game` or `,away` to start that way.
Companion › Thinking then shows `SituationSimulateGame` (*Simulate a game*,
*End the simulated game*) and `SituationSimulateAway` (*Simulate the Thinking
host away*, *The simulated host answers again*). Both are safe clicks: they
look at no real game or host and save nothing. `situation_status` then reads
`simulated: true`, and `logs_tail` `contains` `Situations` gives each change.
```powershell
$env:MARTLET_SIMULATE_SITUATION = 'gaming'
.\scripts\Invoke-MartletMcp.ps1 -Desktop -Calls '[{"name":"ui_click","arguments":{"id":"TourSkip"}},{"name":"ui_click","arguments":{"id":"NavCompanion"}},{"name":"ui_click","arguments":{"id":"CompanionTab-Thinking"},"waitMs":1500},{"name":"ui_click","arguments":{"id":"SituationSimulateGame"},"waitMs":1500},{"name":"ui_snapshot","arguments":{"idPrefix":"ThinkingSituation"}},{"name":"situation_status"}]'
```

`greeting_check` rehearses [the greeting when Martlet
starts](../CONVERSATION.md#greeting-when-martlet-starts) with the production code
(`Greetings`, `ConversationHistory`, `BackgroundJobs`, `UnpromptedSpeech`) and
fixture facts: an empty record giving no last conversation, the newest
conversation on this PC and a newer Telegram chat (only when it is newer) read
from a real conversation record in a temporary folder (deleted afterwards),
which names count as a real name (*Sam Rivera* gives *Sam*; *mozar*,
*xXDarkLordXx*, *gamer_42*, *sam@example.com*, *Admin* and *J. Smith* give
none), the time, part of the day and weekend, a special day (*Halloween*), a
reminder missed while Martlet wasn't running, only the reminders due in the
next 12 hours, a Martlet update, the open apps with the one in front first and
each once, the counts-only line the log and `GreetingStatus` use (*the time of
day, your name, your last conversation, your last chat in a messaging app, 1
missed reminder, 1 reminder coming up, the update and 3 open apps*), the
message when Martlet greets on its own (in character, or `[pass]` when the
character wouldn't greet) and the notes when the user talks first, said once,
and let go (*too_old*) after waiting 10 minutes. `passed` and each step's
`passed` and `detail`. No model, network or credentials.
