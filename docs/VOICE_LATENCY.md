# Voice latency: from you stopping talking to Martlet's voice

**Findings and plan, 2026-10-03.** Goal: Martlet starts speaking less than
**800 ms** after you stop talking, while always keeping voice cloning and
non-verbal sounds (`[sigh]`, `[laugh]`). Measured on IMOUTO (RTX 5080, the
desktop and the `imouto-host` Chatterbox Turbo voice in Docker Desktop) with
DIVA (RTX 4070, `diva-host` lip-sync), OpenRouter `x-ai/grok-4.3` for Thinking
and Parakeet on this PC for speech-to-text. Numbers are this setup's, not
qualification.

**Short answer.** Before these changes a spoken reply started about
**5.5-10 s** (sometimes 24 s) after you stopped. Most of it was two stages:
the Thinking model's hidden reasoning before its first word (3-8 s) and
synthesizing each whole sentence before any of it played (1-3.5 s, plus about
7 s on the first reply after the voice service started). Both are now cut
without giving up cloning or sighs: Thinking steps *Off* skips the reasoning,
and the voice streams its first audio about 0.35-0.4 s after it is asked,
whatever the sentence's length. What's left is mostly the end-of-speech pause
(0.5-0.8 s), speech-to-text and the Thinking model's own time to its first
clause. Under 800 ms needs every stage near its floor at once (see
[the budget](#a-budget-under-800-ms)); about 1.5-2 s is realistic now with a
cloud model, and under 1 s needs a smarter end-of-turn detector and a fast or
local Thinking model.

## Measure first: the reply latency line

Every reply now writes one line to the desktop log, from the moment that counts
for you to the first audio, step by step (each number is the wait that ended
at that step; they add up to the total):

```text
Reply latency: first audio 6620 ms after you stopped talking (end of speech 800, recording 12,
speech-to-text 180, voice recognition 2, waiting to answer 96, preparing 6, memory 5,
building the request 2, Thinking authorization 4, Thinking connection 230,
Thinking before reasoning 10, hidden reasoning 2390, first sentence 120, voice authorization 3,
voice synthesis 1050, playback start 10, speakers 31). First words after 3300 ms and first audio
after 5585 ms from the reply's start, 2 spoken pieces. First piece: 1.20 s of speech made in
1069 ms. Models: Thinking x-ai/grok-4.3, voice chatterbox-turbo, speech-to-text parakeet-tdt-0.6b-v3-int8.
```

| Step | What the time was spent on |
| --- | --- |
| end of speech | The pause always listening waits for before it decides you stopped (Companion › Listening; 800 ms by default) |
| end-of-turn wait, end-of-turn judge | Instead of *end of speech* when the [end-of-turn judge](#the-end-of-turn-judge) decided: the 260 ms pause before it is asked, then its answer (when it judged the turn unfinished or was slow, *end of speech* follows) |
| recording | Closing the recording and cutting out the speech |
| Voice ID, speech-to-text | Checking it's you (when on), then transcribing (waits behind an earlier utterance) |
| voice recognition | Waiting for who spoke (at most 3 s) |
| waiting to answer | Collected by the talk window (100 ms tick), waiting for more when it sounds unfinished (1.5 s), or for the app slot |
| preparing, memory, lore, tools, Home Assistant | Settings and policy checks, memory recall, lorebooks, MCP tools, Home Assistant's Assist |
| building the request | Persona, history and budgets into one request |
| Thinking authorization, connection | Per-request permission; then until the provider's response headers (network, TLS, queueing) |
| Thinking before reasoning, hidden reasoning | A reasoning model's thinking before its first word (shown only when the provider streams it) |
| Thinking first words | Until the first word when no reasoning was streamed |
| first sentence | Until the first piece the voice can say (a sentence; commas, semicolons and dashes never end a piece) |
| voice authorization, voice synthesis | Per-piece permission; then until the voice's first audio arrives |
| promoted | A reply [started early](#starting-replies-early) is taken as the reply: what it wrote shows, and its first piece, made meanwhile, plays |
| playback start, speakers | Handing audio to the speakers until Windows plays it |

Push-to-talk counts from letting go of the talk button, typed messages from
sending them. MCP's `latency_report` summarizes the newest lines (median and
90th percentile of the total and of every step, the slowest steps, the models);
see [MCP](MCP.md#latency). The Chatterbox service also logs, per reply, how
much speech it made and how long it took (never the words).

A voice made slower than real time (Chatterbox streams in growing chunks, and a
busy or smaller graphics card can take longer to make each chunk than it lasts)
leaves the speakers waiting mid-sentence. The voice then pauses until the next
audio arrives, for up to 10 s, instead of being cut short (it used to stop after
1 s with `PlaybackFailed, audio StreamTruncated`, dropping the rest of the reply),
and the line ends with *The voice paused 2 times for 3120 ms in all, waiting
for its next audio.* A Martlet host's own log (Diagnostics, *Host gateway*)
says, for each reply it spoke, how much speech it made, when its first audio
left and whether that was slower than real time. Pauses don't change the time
to the first audio.

When you talk over a reply with *Pause and decide* on (Companion › Listening ›
*When you talk over Martlet*, see [pause and decide](CONVERSATION.md#voice-latency-streaming-overlap-and-barge-in)),
the line also says how long the reply was paused and what happened next:
*, paused 430 ms when you talked over it, then resumed* or *, paused 380 ms,
then stopped when you talked over it*. While paused, the Thinking text and the
voice's synthesis go on and buffer, so playing on starts at once from where it
paused with nothing made again; the voice (Chatterbox) is usually the slowest
step, so making the rest again would add seconds. Pausing adds nothing before
the first audio: the judge runs only while you talk over a reply that is
already playing, the local rules judge takes well under a millisecond, and a
model judge (a Thinking pool member) has at most 400 ms before the rules decide.
The pool judge never uses the conversation's own Thinking route, so it never
evicts the conversation from a local model's prompt cache.

### The live turn comes first (live floor)

Background work used to compete with the reply for the same graphics card: a
screen summary started the moment you began to speak, a think or a Thinking
pool job kept running on the card the reply needed, and remembering after a
reply on a paired host's own model made the next reply wait in line
(`job.busy`). Windows has no graphics card priority between processes, so
Martlet now schedules it itself with the
[live floor](CONVERSATION.md#the-live-floor-the-live-turn-comes-first): your
voice holds new pool work on members that share the conversation's computer
(Listening), and real words or a reply stop running summaries, remembering and
thinks there (Live) until the reply's voice is made; Ollama stops a stopped
stream within one token, and a think goes on later from what it wrote. A live
request never waits behind this PC's own background request on a paired host:
it stops it. Paired hosts that serve a live route keep those graphics cards
free of pool work for the whole turn (a hold renewed every 5 seconds). When the
floor held or stopped anything during a turn, the line says so:

```text
... 2 spoken pieces. Live floor: held 2 pool jobs, stopped 1 (think longer). Models: Thinking qwen3:8b, voice chatterbox-turbo.
```

The floor's own work is bookkeeping: a level change takes a lock and a list
walk on the caller's thread, and its log line, status file and host holds run
on other threads, so it adds nothing to the time to the first audio. MCP's
`live_floor_status` and `live_floor_check` show it ([MCP](MCP.md#live-floor-the-live-turn-first)).

## Thinking trace: each Thinking request step by step

The reply latency line tells how long a reply took. It comes only when the
reply ends, and only for a reply that had words. The desktop log also follows
each Thinking request while it runs, so a slow or stuck request shows what it
waits for. Each line starts with the name of the turn, for example *Thinking
turn 12 (reply)*. A turn is one use of the Thinking model: a reply (also a
reply started early, a background report, a touch reaction or a reply to what
this PC played), a screen or camera glance, a background think
(*think-longer-1*), a Thinking pool job (*digest pool job on gpu-box*), a sense
job, a Discord reply or a memory request. Times are in milliseconds from the
start of the turn:

```text
Thinking turn 12 (reply) started: Chat Completions https://openrouter.ai/api/v1, model x-ai/grok-4.3; input of about 6012 tokens
(23456 bytes) with 14 earlier messages, 7 tools; spoken; Thinking steps off; Backup Thinking after 1500 ms; at most 1024 output
tokens; first words within 15 s, no words for at most 10 s, each request within 60 s, the turn within 90 s.
Thinking turn 12 (reply): prepared 1004 ms after you stopped talking (end of speech 800, recording 12, speech-to-text 150,
preparing 3, memory 35, building the request 4).
Thinking turn 12 (reply): still waiting at 2004 ms for request 1's answer (2001 ms so far; sent at 3 ms, response at 340 ms,
hidden reasoning since 800 ms).
Thinking turn 12 (reply): request 1 (first) answered with 1 tool call at 3400 ms: authorized at 1 ms, sent at 3 ms, response at
340 ms, hidden reasoning from 800 ms, first words at 2700 ms, 85 characters; 6012 input tokens (5800 from the prompt cache), 230
output tokens.
Thinking turn 12 (reply): tool round 1: search_conversations 210 ms; 212 ms in all.
Thinking turn 12 (reply): request 2 (after tool round 1) answered at 4800 ms: began at 3612 ms, authorized at 3613 ms, ...
Thinking turn 12 (reply) ended at 5600 ms: Completed; 2 requests, 1 tool call, first words at 2700 ms, first audio at 3100 ms,
340 characters, 3 spoken pieces.
```

| Line | What it says |
| --- | --- |
| started | The route (OpenAI Responses, Chat Completions and its endpoint, or a Martlet host), the model, the size of the input (estimated tokens, bytes, earlier messages, tools, a recording, a picture), spoken or text only, started early, Thinking steps, Backup Thinking's wait, the Thinking fallback and the time limits |
| prepared | Replies only: each wait before the request, from the moment that counts for you (the same steps as the reply latency line) |
| request N (why) | One line when each request ends: why it was sent (first; after tool round N; again without the tools, recording, picture or Thinking steps choice the model refused; the Thinking fallback after a failure), how it ended, and when it was authorized, sent (after its key was read and its body made), got the response headers, started hidden reasoning and had its first words; then its characters and the tokens the provider reported |
| tool round N | Each tool call and how long it took |
| Backup Thinking | What [Backup Thinking](CONVERSATION.md#backup-thinking-a-hedged-request) did for the first request |
| still waiting | What the turn waits for, after 2, 5, 10, 20 and 30 s and then every 30 s: a request's authorization, its answer (with when it was sent, the response and hidden reasoning so far), the next words once words came, the voice to take the next piece (its queue is full: a long reply, a reply started early that isn't taken yet, or a reply paused for you), a tool, speech-to-text's words for a recording sent alone, or the talk window to take a reply started early |
| ended | The state and the failure (if any), the number of requests and tool calls, the first words and first audio, the characters and spoken pieces, and what the turn dropped or fell back to |

Each request for the Thinking pool's slots (the
[Thinking requests](MCP.md#thinking-requests) page's `tr-N`) also writes its way
through the line. The pool job's own turn then follows as *Thinking turn 13
(memory pool job on gpu-box)*:

```text
Thinking request tr-7 (memory, memory-3): waits in line: a pool job, priority 20, needs Text, within 120000 ms.
Thinking request tr-7 (memory, memory-3): started on gpu-box (qwen3:8b) after 1200 ms in line.
Thinking request tr-7 (memory, memory-3): let go of gpu-box after 800 ms: the conversation needed gpu-box; it waits (paused) again.
Thinking request tr-7 (memory, memory-3): started on gpu-box (qwen3:8b) after 4100 ms waiting again (try 2).
Thinking request tr-7 (memory, memory-3): Succeeded after 9000 ms: waited 5300 ms, ran 3700 ms, 2 tries (last on gpu-box),
stopped 1 time for other work, an answer of 240 characters.
```

The lines never hold what was said or written: only counts, the names of
routes, models, members and tools, and times. They are INFO lines of
`desktop.log`, so the Diagnostics page shows them and log sharing sends them to
your other computers.

The trace adds no wait to a reply. While a request runs, the turn only notes
times in memory, and it makes its start line on a background thread. The log
writes the lines on a background thread, so a slow disk never delays a reply.
The *still waiting* notices come from the turn's supervisor, which checks every
10 ms beside the reply. With the production runtime and a fixture endpoint
(180 turns with the trace on and 180 with it off, four runs), the median time
to the first words differed by -0.01 to +0.06 ms, which is noise. MCP's
`thinking_trace` puts the lines back together ([MCP](MCP.md#latency)).

## Where the time goes today

From the desktop log before this change (it measured from the reply's start
only), 23 spoken replies on 2026-10-03 between 02:02 and 04:03:

| | Median | Range |
| --- | --- | --- |
| First words after the reply started | 4.0 s | 2.9-8.2 s |
| First audio after the reply started | 7.3 s | 5.5-23.8 s |
| First audio after the first words (mostly the voice) | 3.2 s | 1.5-15.7 s |

Plus, before the reply starts: the 800 ms end-of-speech pause, speech-to-text
and the waits above (now logged).

**Thinking.** `x-ai/grok-4.3` is a reasoning model. OpenRouter's model list
says it reasons by default (`default_effort: low`) and also accepts `none`
(`mandatory: false`), but Martlet sent no `reasoning` setting, so every reply
waited for hidden thinking first. That is most of the 3-8 s to the first word.
Companion › Replies › **Thinking steps** is now *Off* by default, which sends
OpenRouter `reasoning.effort: none` (see [Conversation](CONVERSATION.md)).
OpenRouter's `provider` block only sets `allow_fallbacks: false`, so it is not
asked to prefer the lowest-latency provider (grok-4.3 has one, xAI).

**Voice (Chatterbox Turbo).** Measured inside the `imouto-host` container on
the RTX 5080 (median of 5, reference voice cached by the OS, nothing else on
the GPU unless noted):

| Step | Cost |
| --- | --- |
| Voice conditionals (`prepare_conditionals`), recomputed for **every sentence** | 137 ms; **7.4 s on the first call** after the service starts (librosa/numba warm-up, which `warm` didn't cover) |
| T3 speech tokens (GPT-2 medium, 25 tokens per second of speech) | 17-25 ms a token, almost all kernel-launch overhead (logits and sampling about 2 ms) |
| S3Gen + vocoder | 180-220 ms flat for 0.4-4 s of speech |
| Perth watermark (kept) | 10-75 ms |
| Whole piece before any of it plays (non-streaming) | about 1.1 s for 1.2 s of speech and 2.5-3.5 s for 4 s |

The live reply logs agree: the first audio arrives 1.5-5 s after the first
words, and up to 16 s when the first reply after a start pays the warm-up.

**Other findings.**

- Loading a second copy of the model on the 16 GB card spilled into shared
  system memory (Windows pages VRAM silently) and made every step 3-5x slower.
  Keep one voice engine per GPU and leave headroom.
- The GPU is shared with the character renderer, NVIDIA Broadcast and games.
  With the character showing, the same synthesis took about twice as long as
  on an idle card: Windows time-slices the GPU between them.
- When the card's memory is overfilled, Windows moves the idle programs'
  memory out instead of failing, and the voice's next reply waits for it
  (0.6 s measured on the RTX 4070, 51 s once in use). The Chatterbox service's
  idle check brings it back first, and Martlet warns about a Windows computer
  whose voice shares its card with other roles
  ([Chatterbox](CHATTERBOX_VOICE.md#sharing-the-graphics-card)).
- The desktop's own work before the request is small: 115 ms in a fresh
  profile (preparing 29, memory 45, building 17, authorization 24).

## What changed

1. **Reply latency line** and MCP `latency_report`, above.
2. **Faster Chatterbox service** (image `martlet-chatterbox:4`, see
   [Chatterbox](CHATTERBOX_VOICE.md#how-it-runs)): voice conditionals computed
   once per reference recording; the first-call warm-up paid while loading;
   T3 decoded by replaying one captured CUDA graph per token over a static KV
   cache, with the library's own sampling and identical logits (checked
   against eager decoding). Same words, same voice, same watermark, same tags.
3. **Streaming pieces**: each piece's audio is sent as it is made, the first
   chunk after about 12 speech tokens, with crossfaded seams and the watermark
   on every chunk (details and checks in
   [Chatterbox](CHATTERBOX_VOICE.md#how-it-runs)).
4. **Thinking steps** (Companion › Replies) turns a reasoning model's hidden
   thinking off, and is Off by default; a model that refuses Off is asked again
   with its default.
5. **Thinking longer** (Companion › Deep thinking, on by default) gives the hard
   tasks their thinking back without making any reply wait for it: Martlet
   says it'll think it over and works on the task in a background request
   ([Thinking longer](CONVERSATION.md#thinking-longer-and-background-work)).
   Its cost on the conversation path, and what keeps it there:
   - `think_longer` and `cancel_thinking` and the *Thinking longer* prompt are
     in every request while it is on (about 530 estimated tokens with the
     tools prompt), always the same, so they come from the prompt cache after
     the first reply; turning it on or off changes the start once.
   - The tool returns at once; what Martlet said before calling it is spoken
     while it runs.
   - The background request continues a reply's request, so a cloud provider
     reads it from the cache and a model on this PC keeps the conversation in
     its cache.
   - A think always runs in parallel and is never paused, so it runs only where
     it has a model of its own (a machine that is on in Companion › Thinking
     pool's machine list): a paired computer's Ollama, a cloud provider, a second
     model in Ollama on this PC, or Thinking's own model when its provider
     answers several requests at once. Thinking's own model on this PC or a
     paired computer answers one request at a time and keeps one conversation
     in its cache, so there `think_longer` isn't offered at all (and its tools
     and prompt aren't in the request), and Thinking longer's own box turns it off everywhere.
   - On another machine the conversation's model, cache and graphics card are
     left alone. A second model in Ollama on this PC runs in its own process
     beside Thinking's, so Thinking's cache stays, and a think starts only when
     both fit on the graphics card (Ollama would otherwise unload Thinking's
     model or make a reply wait for the think); if loading it pushed Thinking's
     off the card after all, the think stops and Thinking's is loaded again.
     The two share the card's compute, so replies may start a little later while
     such a think runs. A separate destination's request doesn't carry the
     reply's tools, so it doesn't share the conversation's cache (it is another
     model anyway).
   - A model that turns tools down is remembered on this PC for a week, so it
     isn't asked with tools (and again without) on every first reply.
6. **Latency-first defaults.** The recommended model in Ollama on this PC is
   Gemma 4 E2B on every graphics card (it was the largest the card fits: E4B on
   12 GB, 12B on 16 GB, 26B on 24 GB); Companion › Thinking still offers the
   largest that fits as *smartest that fits here*, smarter but slower. The setup
   advisor's *Fastest* goal now suggests a small model beside the cloned voice
   on one graphics card and Parakeet on this PC for speech-to-text (it
   suggested sizing the model up to the card and the planned Windows
   recognizer). Chatterbox Turbo, streaming, stays the default voice; the
   800 ms end-of-speech pause, Thinking steps Off and Thinking longer On are
   unchanged.
7. **Short first sentence** (Companion › Replies, on by default): every spoken
   reply is asked to begin with a few words and then go on
   ([how it works](CONVERSATION.md#voice-latency-streaming-overlap-and-barge-in)).
   The voice gets nothing until the first sentence ends, so a reply that opens
   with a 15-word sentence makes the voice wait for 15 words; *"Hmm, good
   question."* makes it wait for three, plus the few words the short-ending
   rule waits for. OpenAI's
   [realtime prompting guide](https://developers.openai.com/cookbook/examples/realtime_prompting_guide)
   gives voice agents the same kind of advice: short, speakable sample phrases
   and a variety rule so the openings don't sound the same. The prompt is about
   80 tokens, the same text in every spoken reply just before *Reply length*,
   so it comes from the prompt cache after the first reply. Its effect on the
   *first sentence* step depends on the model and wasn't measured here (no
   Thinking model on the test PC; see the change's pull request).
8. **Backup Thinking** (Companion › Thinking pool, off by default): a reply
   whose Thinking model has no first words after a wait also asks a pool member
   the owner allowed, and the first to start gives the reply
   ([below](#backup-thinking-a-second-model-for-slow-starts)).

Thinking longer, measured through a disposable desktop with Thinking on a
single-slot loopback fixture (one request at a time with a one-slot prompt
cache and about 10,000 tokens a second of prompt reading; canned text, NOT AI),
typed messages, warm:

| | *Reply latency* first words (ms) | *Thinking input* (Reply) from the cache |
| --- | --- | --- |
| Thinking longer off (no tools) | 55, 56, 59, 45 (median 56) | 99% of 865-940 tokens |
| On, nothing running | 67, 53, 57, 55 (median 56) | 99% of 773-888 tokens |

A Thinking model on this PC with *Same as Thinking* (the default) now gets the
first row: Deep thinking can't run there, so the tools are left out. With a
destination of its own (`think_longer_check`'s `parallel` part: the think is
never stopped) three replies beside it answered in 2-7 ms each, as without it.
How much a second model in Ollama on this PC slows a reply's first words while
it thinks depends on the graphics card and both models, and wasn't measured
here (see below).
Two measurements: one process on an idle GPU (graph against the library's own
decoding), and the running service against this change's service on a side
port, interleaved through `/synthesize` while the character was showing
(kept conditionals included, so the "after" side is the whole new service):

| Piece | Library, idle GPU | CUDA graph, idle GPU | Service before → after, character showing |
| --- | --- | --- | --- |
| "Oh, hey there." (1.4 s) | 1,069 ms | 410 ms | 1,271 → 901 ms |
| A 4 s sentence | 2.5-3.5 s | 841 ms | 2,933 → 1,988 ms |
| "[sigh] Fine, I'll help you, but you owe me one." (2.9 s) | | 747 ms | 2,132 → 1,470 ms |
| A 7.7 s sentence | | 1,482 ms | 4,793 → 3,499 ms |
| First reply after the service starts | +7.4 s | | +0 (paid while loading) |

T3 alone went from 17-25 ms to **6 ms a token**. With a busy, nearly full card
(both services and the character) the gain was 1.5-3.5x. `torch.compile`
isn't usable in the image (no C compiler), so the graph is captured by hand.

With streaming, the same service A/B (character showing, five of each):

| Piece | First audio before → after | Whole piece before → after |
| --- | --- | --- |
| "Oh, hey there." (1.4 s) | 1,215 → 401 ms | 1,219 → 779 ms |
| A 4 s sentence | 2,430 → 368 ms | 2,432 → 1,542 ms |
| "[sigh] Fine, I'll help you, but you owe me one." (3 s) | 2,066 → 359 ms | 2,069 → 1,314 ms |
| A 7.7 s sentence | 4,180 → 358 ms | 4,185 → 2,707 ms |

Streamed speech was checked against a whole-piece decode of the same tokens:
the same length, less difference (log-mel 0.1-0.39) than between two whole
decodes with different noise (0.46-0.82), no larger sample jumps at the seams
than elsewhere, and the watermark detected on every streamed piece checked.

## Local options measured (voicebench)

**2026-10-03, DIVA (RTX 4070 12 GB, i7-10700K)**, with `scripts/voice-bench`
([how to run it](../scripts/voice-bench/README.md)). The Chatterbox Turbo,
whisper.cpp and Audio2Face host roles were resident. The whole-turn and messy
audio numbers were re-measured at 21:25-22:05 with the graphics card otherwise
quiet (other GPU jobs paused); the first comparison ran beside a live session
and stalled (see below). Clips: 73 LibriSpeech utterances (accuracy), 16 short
companion prompts spoken by Chatterbox in the starter voices (about 3 s each),
those prompts degraded to a desk microphone, and 32 real spontaneous turns
each from the AMI Meeting Corpus' headset and room microphones (CC BY 4.0).
Numbers are this PC's, not qualification.

**Answer.** Two flows tie for the fastest time to first audio, both with the
cloned voice and its sighs and laughs:

| Option (all into Chatterbox Turbo, streaming) | Clean prompts | p90 | Desk mic | AMI headset | AMI room | Transcript errors (desk mic / AMI headset / room) |
| --- | --- | --- | --- | --- | --- | --- |
| **Cascade: Parakeet TDT 110M (CPU) → Gemma 4 E2B (Ollama)** | **734** | **838** | 788 | 760 | 688 | 11% / 17% / 43% |
| **Omni: Gemma 4 E2B (Ollama) hears the recording** | 810 | 1,083 | **750** | **730** | 698 | (no transcript; 9-36% when asked to transcribe) |
| Cascade: faster-whisper large-v3-turbo (GPU) → Gemma 4 E2B | 806 | 890 | | | | 1% / 14% / 28% |
| Cascade: Gemma 4 E2B Q8 on llama.cpp, Parakeet 110M | 821 | 988 | | | | |
| Omni: Gemma 4 E2B Q8 on llama.cpp | 850 | 923 | | | | |
| Cascade: Parakeet TDT 0.6B v2 (CPU) → Gemma 4 E2B | 846 | 928 | | | | 1% / 9% / 20% |
| Omni: Gemma 4 E4B Q4 on llama.cpp (smarter) | 894 | 1,002 | | | | |
| Cascade: Parakeet v3 (Martlet today) → Gemma 4 E2B | 931 | 1,527 | 1,048 | | | 11% / 8% / 29% |
| Hearing: Parakeet v3, then the transcript and recording | 1,039 | 1,572 | 841 (110M) | | | |
| Cascade: Gemma 4 E4B Q4 on llama.cpp, Parakeet 110M | 946 | 1,018 | | | | |

Milliseconds from the end of the recording to the first audio, medians of 32
turns (16 on AMI); add the end-of-speech pause (800 ms today) for the time
after you stop: 734 ms is 1.53 s. On clean, close speech the Parakeet 110M
cascade is about 75 ms faster than omni. On messier audio omni is as fast or
faster, because transcribing gets slower with padding and noise, and its
reply doesn't depend on a transcript: Parakeet 110M mis-hears 11-43% of the
words there, and the cascade answers those words. So the shape to build is
**omni for the reply**, with an accurate transcriber running **in parallel**
off the critical path for history, memory and voice ID (Parakeet 0.6B v2 or
whisper.cpp: about 1% on the desk-mic set), and a cascade with Parakeet 110M
for Thinking models that can't hear. A smarter model (E4B, 12B) costs
100-200 ms. The 800 ms pause is now the largest single wait; a turn detector
at 300 ms would bring both fastest flows to about 1.03-1.11 s after you stop.

**Speech-to-text** (median ms per utterance after a warm-up; word error rate
with Whisper's normalizer):

| Engine | Where | LibriSpeech ms | WER % | Short prompts ms | GPU memory |
| --- | --- | --- | --- | --- | --- |
| Parakeet TDT 0.6B v3 int8 (Martlet today) | CPU | 406 | 3.51 | 234 | none |
| Parakeet TDT 0.6B v2 int8 (English) | CPU | 367 | **2.57** | 223 | none |
| Parakeet TDT 110M int8 (English) | CPU | **133** | 3.76 | **86** | none |
| Moonshine base int8 | CPU | 189 | 7.01 | | none |
| Qwen3-ASR 0.6B int8 | CPU | 1,493 | 4.45 | 901 | none |
| faster-whisper small.en | GPU | 142 | 5.56 | | 0.8 GB |
| faster-whisper distil-large-v3 | GPU | 154 | 4.36 | 137 | 2.3 GB |
| faster-whisper large-v3-turbo | GPU | 171 | 3.42 | 147 | 2.4-3.0 GB |
| faster-whisper large-v3 (fp16) | GPU | 3,084 | 2.82 | | 2.8 GB, spilled |
| faster-whisper large-v3 (int8) | GPU | 547 | 3.08 | 416 | 1.9 GB |
| Whisper large-v3, PyTorch fp16 | GPU | 827 | 2.48 | | 3.7 GB |
| whisper.cpp large-v3-turbo (the host role) | GPU | 289 | 3.08 | 210 | resident |

Whisper large is the most accurate, but on a shared 12 GB card its full
weights page into system memory, and it is never the fastest. faster-whisper
large-v3-turbo is the fastest accurate engine, but it takes 2.4-3 GB that the
voice and Thinking need (see below). Parakeet on the processor costs no
graphics memory, and on 3 s turns it is within 100 ms. Moonshine v2 (2026-02)
fails in sherpa-onnx 1.13.8.

**Messier audio** (word error rate %, and median ms per turn):

| Engine | Desk mic (16 generated prompts, reverb, 10-20 dB SNR, padded) | AMI headset (32 real turns, 12 speakers) | AMI room microphone (32 real turns) |
| --- | --- | --- | --- |
| Parakeet v3 (CPU, Martlet today) | 10.7 (330 ms) | 8.3 (329) | 28.6 (260) |
| Parakeet 0.6B v2 (CPU) | **1.2** (340) | 8.6 (278) | **20.3** (242) |
| Parakeet 110M (CPU) | 11.2 (**110**) | 14.2 (**91**) | 28.3 (**90**) |
| faster-whisper distil-large-v3 (GPU) | 6.5 (144) | 13.8 (143) | 22.8 (142) |
| faster-whisper large-v3-turbo (GPU) | 1.2 (155) | 13.8 (155) | 27.8 (155) |
| whisper.cpp large-v3-turbo (host role) | **0.6** (219) | 13.3 (224) | 27.2 (220) |
| Gemma 4 E2B hearing, asked to transcribe (Ollama) | 9.5 (196) | 15.2 (184) | 36.3 (188) |
| Gemma 4 E4B hearing, asked to transcribe (llama.cpp) | 8.3 (281) | 12.6 (290) | 34.6 (279) |

Real meeting speech (fillers, false starts) is harder than any generated set,
and a distant room microphone is hard for everything. Parakeet v3 and 110M
lose the most to noise and reverb; Parakeet v2 and the Whisper engines hold
up. Gemma 4 hears roughly as well as the small engines, which is what matters
for an omni reply.

**Thinking and models that hear** (median ms to the first piece the voice can
say, Thinking steps Off; *text* is the transcript, *audio* only the
recording, *both* is Martlet's *Let Thinking hear my voice*; hearing is the
word error rate when asked to transcribe):

| Model (runtime) | Text | Audio | Both | Hearing WER % | Replies from audio |
| --- | --- | --- | --- | --- | --- |
| Gemma 4 E2B (Ollama) | 157 | 237 | 241 | 7.2 | Good, in character, uses tags |
| Gemma 4 E2B Q8 (llama.cpp) | 154 | 227 | 204 | | Good |
| Gemma 4 E4B (Ollama) | 231 | 325 | 299 | | Good |
| Gemma 4 E4B Q4 (llama.cpp) | 212 | 267 | 279 | 5.4 | Good |
| Voxtral Mini 3B (llama.cpp) | 139 | 362 | 360 | 24.6 (paraphrases) | Good, terse |
| Qwen2.5-Omni 3B (llama.cpp) | 101 | 329 | 337 | | Sometimes parrots the prompt's example |
| Qwen2.5-Omni 7B (llama.cpp) | 182 | 1,302 | 1,291 | 53.2 (mishears) | Fair |
| Qwen2-Audio 7B (llama.cpp) | 692 | 6,347 | 6,637 | | Describes the audio instead of answering |
| Qwen3-Omni 30B-A3B (llama.cpp) | 610 | 4,628 | 4,464 | | Good, but prefixes "Assistant:"; doesn't fit, experts in system memory |
| Phi-4-multimodal 5.6B (PyTorch 4-bit) | 7,037 | 8,777 | 7,977 | | Good, far too slow here (16 turns) |
| MiniCPM-o 2.6 8B (PyTorch 4-bit) | 12,955 | 16,188 | 16,152 | | Good, in character, but leaks its `<\|tts_eos\|>` token; far too slow here |

Hearing the audio adds 55-95 ms of prefill to Gemma 4 against text, less
than the speech-to-text it replaces (on the desk-mic set too: E2B's first
piece 140 ms from text, 219 ms from the recording). llama.cpp is as fast as
Ollama, or faster, for the same Gemma model. Phi-4-multimodal and MiniCPM-o
run through PyTorch with 4-bit bitsandbytes weights (no GGUF of either takes
audio in llama.cpp), which is far slower than llama.cpp or Ollama would be;
their numbers show they don't fit this PC today, not how fast the models
are. Every model writes its own reply; none of them speaks in a cloned voice,
so the voice stays Chatterbox.

**Voice** (Chatterbox Turbo host role on the 4070, streaming, starter voice
*Annie*, three runs): first audio 437-485 ms for sentences, including
`[sigh]` and `[laugh]` (real-time factor 0.43-0.63), and 800 ms for a
two-word piece.

**Voice engines compared.** Chatterbox Turbo against Dia 1.6B, each **alone
on an otherwise empty card** (2026-10-04 02:13-02:25, RTX 4070, Ollama stopped,
about 1 GB in use before each; starter voice *Annie*, the tts bench texts with
Dia's own `(sighs)` and `(laughs)` cues, three runs after a warm-up; medians in
ms, p90 in brackets), with F5 from an earlier run beside the resident roles:

| Engine (how Martlet runs it) | Short | Sentence | Sigh | Laugh | Long | Real-time factor | GPU memory | Sighs and laughs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Chatterbox Turbo** (role image `:5`, streaming, CUDA graph) | **522** (600) | **451** (505) | **417** (479) | **458** (480) | **560** (566) | 0.43-0.75 | 3.7 GB loaded, 4.2 GB at peak | Yes (`[sigh]`, `[laugh]`) |
| Dia 1.6B 0626 (Martlet's worker code, float16, whole pieces) | 16,969 (17,132) | 19,538 (19,851) | 28,193 (31,263) | 38,210 (41,994) | 60,363 (68,889) | 5.5-6.6 | 9.8 GB at peak | Yes (`(sighs)`, `(laughs)`) |
| F5-TTS v1 base (role image `martlet-f5:1`, whole pieces) | 1,409 | 1,469 | 1,582 | 1,990 | 2,169 | 0.20-1.66 | about 0.9 GB | No (no cues) |

Chatterbox Turbo starts every piece in about half a second, 30-100x sooner
than Dia, and stays the default. Dia doesn't stream within a piece, so its
first audio is the whole piece, and here it made audio at about a sixth of real
time (3 s of speech in 17 s, 11 s in 60 s): the graphics card was busy only
about 30% of the time, so its step-by-step decoding is held back by the
processor (no `torch.compile` in this Windows bench; Dia's README gives 1.3x
real time on an RTX 4090 without it). It also took 9.8 GB at its peak, so it
can't share a 12 GB card with Thinking or the character. Beside the resident
roles and a model in Ollama it ran out of memory entirely (an earlier run: 15
minutes for the first piece). F5 makes whole pieces quickly but has no sighs
or laughs. The audio is saved with each run (`results\20261004-021450-tts` for
Chatterbox, `20261004-021539-tts-dia` for Dia, `20261004-013029-tts` for F5)
for a listening check. XTTS-v2 and GPT-SoVITS: NOT RUN (no image or weights on
this PC; building them means installing a new role).

**Whole turn** (Gemma 4 E2B on Ollama, Chatterbox role, medians in ms). First
beside a live session using the same card (16 turns), then again with the
card quiet (32 turns):

| Flow | Speech-to-text | First piece | Voice first audio | First audio | p90 | After you stop (+800 ms) |
| --- | --- | --- | --- | --- | --- | --- |
| Cascade (Parakeet v3), busy card | 248 | 163 | 538 | 954 | 34,270 | 1,754 |
| Omni, busy card | | 222 | 542 | 762 | 39,072 | 1,562 |
| Cascade (faster-whisper large-v3-turbo), busy card | 180 | 203 | 2,973 | 3,409 | 12,803 | 4,209 |
| Cascade (Parakeet v3), quiet card | 242 | 169 | 514 | 931 | 1,527 | 1,731 |
| Hearing (transcript and recording), quiet | 268 | 227 | 539 | 1,039 | 1,572 | 1,839 |
| **Omni, quiet** | | 241 | 572 | **810** | **1,083** | **1,610** |
| **Cascade (Parakeet 110M), quiet** | 74 | 152 | 501 | **734** | **838** | **1,534** |
| Cascade (Parakeet 0.6B v2), quiet | 208 | 139 | 495 | 846 | 928 | 1,646 |
| Cascade (faster-whisper large-v3-turbo), quiet | 149 | 138 | 512 | 806 | 890 | 1,606 |

On llama.cpp, Gemma 4 E2B Q8 measured 821 ms (cascade, Parakeet 110M) and
850 ms (omni); E4B Q4 946 and 894 ms. The model's own first piece is the same
in both runtimes; llama.cpp's voice started about 100 ms later beside it.
Thinking model memory with the baseline taken before it loads: Gemma 4 E2B
3.3 GB in Ollama, 3.5 GB (Q8) in llama.cpp, E4B Q4 4.1 GB.

**Small models in Ollama** (2026-10-04 02:30-03:05, Ollama 0.35.1, Thinking
steps Off as Martlet sends it (`reasoning_effort: none`), the 16 companion
prompts as text, two runs; GPU memory is the model's own runner process at the
8,192-token context Martlet uses; *first audio* is the whole turn into
Chatterbox Turbo with Parakeet 110M, median and p90):

| Model | Hears | First word | First piece | First audio (p90) | GPU memory | Words per reply (median) | Replies |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **gemma4:e2b** | Yes | 77 | 154 | 841 (974) cascade, 826 (1,004) omni | 3.3 GB | 18 | Short, in character, tags in half, no Markdown |
| gemma4:e4b | Yes | 117 | 208 | 894-946 (earlier run) | 4.9 GB | 17 | Short, in character |
| qwen3.5:2b | No | 75 | 225 | 932 (1,120) | 3.3 GB | 45 | Rambles (18 of 32 over three sentences), tags in 84%, some Markdown, ignores "exactly three words" |
| **qwen3.5:4b** | No | 133 | 328 | 1,105 (1,400) | 4.1 GB | 40 | In character, no Markdown, follows "exactly three words", right sums |
| qwen3.5:9b | No | 166 | 511 | | 6.6 GB | 34 | Tags in nearly every reply; too slow to the first piece |
| ministral-3:3b | No | 30 | 109 | | 4.0 GB | 56 | Markdown emphasis and stage directions in 56%, wrong sums; called the weather tool |
| ministral-3:8b | No | 34 | 242 | (didn't fit beside the voice) | 6.4 GB | 44 | Markdown in 62%, thinks aloud, names its maker; called the weather tool |
| qwen3-vl:8b | No | 9,355 | 9,702 | | | | Keeps thinking with Thinking steps Off (`think: false` too): no words for seconds |

Thinking steps Off works for Qwen3.5 in Ollama: no reasoning in any of the 32
replies, and none with the native `think: false`. Asked about the weather with
a `get_weather` tool offered (once each, with the companion prompt), only
Ministral 3 called it; Gemma 4 and Qwen3.5 answered in conversation instead.
None of the new models beats
Gemma 4 E2B to the first audio while replying as well: Qwen3.5 2B is about
90 ms slower and rambles; Qwen3.5 4B replies well but is about 260 ms slower;
Ministral 3 starts fastest but writes Markdown and stage directions a voice
can't say. Companion › Thinking keeps **gemma4:e2b** as the recommendation and
now suggests **qwen3.5:4b** in place of `qwen3-vl:8b`: a smarter small model
that sees and calls tools but doesn't hear, so its replies always take the
transcript (the Parakeet cascade). Gemma 4 E2B, E4B and 12B are the suggestions
that hear. The ministral-3:8b turn run is NOT RUN cleanly: another client
reloaded Gemma 4 E2B into Ollama during it, the card filled and requests timed
out (with the voice it needs about 11 GB on its own). Results:
`20261004-023122-think`, `20261004-030232-think` (qwen3-vl),
`20261004-024436-pipeline` (Gemma 4 E2B), `-024758-` (qwen3.5:2b),
`-024934-` (qwen3.5:4b) and `small-models-quality.jsonl`.

**The graphics card is the bottleneck on this PC.** Beside a live session (and
later other GPU experiments) the 90th percentiles reached 12-52 s, and GPU
speech-to-text slowed the voice five-fold: Gemma, Chatterbox, whisper.cpp,
Audio2Face and the desktop together exceed 12 GB, and Windows pages memory
instead of failing. On a quiet card every flow's p90 stays under 1.6 s and
GPU speech-to-text is as fast as Parakeet. On a card shared with the voice:
keep speech-to-text on the processor (Parakeet), remove the whisper.cpp role
from a host whose desktop uses Parakeet (about 2 GB back), and choose E2B
(3.3 GB) over E4B.

**For Martlet.** Ollama 0.35 takes `input_audio` for Gemma 4 E2B, E4B and 12B
(a 12B request transcribed and answered a test clip). Martlet now lets Ollama
on this PC hear for models it says hear, and finds out what any Thinking model
hears and sees from its server's metadata or a test word
([Thinking models that hear and see](CONVERSATION.md#thinking-models-that-hear-and-see)),
and sends the recording straight to a model that hears, with the transcript
beside the reply (*Send my voice straight to Thinking*). The Qwen, Voxtral,
Phi-4 and MiniCPM-o models are in the bench only: Martlet's local selector
runs Ollama, which serves none of them, and none beat Gemma 4 E2B here.

## Straight to Thinking (measured)

**2026-10-04, DIVA (RTX 4070), Martlet's own pipeline.** Companion ›
Listening › When Thinking can hear you › **Send my voice straight to Thinking**
([how it works](CONVERSATION.md#straight-to-thinking)) against **Transcribe
first, then send both**, each in a disposable desktop (Thinking `gemma4:e2b` in
Ollama 0.35.1 on this PC, Thinking steps Off; Listening Parakeet TDT 0.6B v3 on
the processor; voice recognition on; Voice the Windows voice Zira). The
microphone and speakers were the desktop's fixture devices
(`MARTLET_SIMULATE_MICROPHONE`, `MARTLET_SIMULATE_SPEAKERS`, see
[MCP](MCP.md)): eight short questions said by the Windows voice David, heard in
real time 10 s apart, and replies taken at real-time pace into a silent sink;
the end-of-speech pause is the default 800 ms. Medians of the desktop log's
*Reply latency* lines, after the first (warm-up) reply and without one outlier
each way where another job took the graphics card:

| | Straight (n=6) | Transcribe first (n=5) |
| --- | --- | --- |
| First audio after you stopped talking | **1,475 ms** | 1,693 ms |
| First Thinking words after you stopped talking | **1,306 ms** | 1,417 ms |
| From the end of the pause (minus 800 ms) to the first audio | **675 ms** | 893 ms |
| Speech-to-text before the request | none | 281 |
| Voice recognition (who spoke) before the request | 76 | hidden behind speech-to-text |
| Waiting to answer (talk window's 100 ms tick) | 94 | 18 |
| Thinking connection (Ollama's prefill, to its first bytes) | 346 | 275 |
| Prompt cache (`Thinking input`, replies 2-8) | 78-92 % | 71-95 % |
| Background transcript ready after the reply started | 511-656 ms (speech-to-text 116-205 ms) | |

Straight is about **220 ms sooner to the first audio**: it skips
speech-to-text (281 ms), but the reply now waits for who spoke (76 ms, which
used to overlap speech-to-text), meets the talk window's tick more often, and
Ollama takes about 70 ms longer to read a request whose message is the recording
alone. The words reach the talk window about 0.6 s after the reply started:
with Parakeet and Ollama both on this PC, speech-to-text waits for the reply's
first audio, so it never competes with the reply for the processor (in an
earlier, smaller run with speech-to-text beside the request, Ollama's first
bytes came about 130 ms later: 482 against 346 ms median).

Headless, MCP's `straight_voice_check` with `live: true` (the same model and
Parakeet, three utterances, no voice) measured 223 ms to the first words from
the end of the recording sent straight against 482 ms transcribing first (281
ms of speech-to-text, then the request), and showed every straight request
carrying the recording and only the stand-in text, never the transcript.

**Prompt cache.** Ollama with Gemma 4 reuses its cache only for a request
that continues a whole earlier one (a request diverging anywhere reads all of
it again: 0 cached tokens). A reply that carried a recording is never continued
by the next reply on either path, because the recording isn't sent again; the
after-reply request (remembering and learning names), which continues the
reply's request with the transcript in place of the recording, is what the next
reply continues, so replies 2-8 read 78-92 % of their input from the cache. With
remembering off, every reply after a recording reads its whole input again on
both paths. Sending each earlier recording again would keep the reply-to-reply
cache, at the cost of re-sending audio (about 30 tokens a second) for the whole
conversation; Martlet drops recordings after their turn instead.

**Still on the path:** voice recognition (76 ms; it could start during the
end-of-speech pause), the talk window's 100 ms tick (recommendation 8), and the
800 ms pause itself (recommendation 3).

**NOT RUN:** a real microphone and speakers (fixture devices stood in), a
cloud model that hears (paid; OpenRouter lists no audio input for the current
route), Chatterbox or another host voice (a Windows voice spoke), and Parakeet
110M (not downloaded on this PC).

### Hearing on by default, and the quick check of short non-words

**2026-10-04, DIVA (RTX 4070), Martlet's own pipeline** (same setup as above:
`gemma4:e2b` in Ollama on this PC, Parakeet TDT 0.6B v3, a Windows voice,
fixture microphone and speakers). *Let Thinking hear my voice* is now on by
default while the recording stays on this PC ([how it
works](CONVERSATION.md#thinking-models-that-hear-and-see)), so this setup
takes the straight path without ticking anything; before, it transcribed first
until the box was ticked.

**Real words are not slower.** Something short that went straight (less than
1 s of voice) now gets Parakeet beside the request, from the moment the request
has started. Eight short answers (*Yes, please.*, *Stop.*, *Okay, thanks.*,
*Sure, go on.*, *No thanks.*, *Good morning!*, *Really?*, *Tell me more.*), all
short enough for the check, each run alternately on `main` (with the box ticked)
and on this change (never chosen), two runs each after a 30 s settle, medians of
the *Reply latency* lines without each run's first reply and one reply each where
another program made Ollama reload the model:

| | Before (main, n=10) | After (n=12) |
| --- | --- | --- |
| Request start after you stopped talking | 936 ms (p90 971) | 913 ms (p90 948) |
| First audio after you stopped talking | 1,888 ms (p90 2,146) | 1,804 ms (p90 1,952) |
| Thinking connection | 713 | 643 |

Another program shared the graphics card during these runs (Thinking connection
600-800 ms instead of about 250), equally for both. Headless, MCP's
`straight_voice_check` (`live: true`, the quick check's `contention`, eight
rounds of the same short straight request, new each time) measured the model's
first words at 78 ms alone, 69 ms with Parakeet started at the request's start
and 75 ms with it started at the first words: no measurable cost. Parakeet
finished about 320 ms after the request started there, and 90-500 ms in the
desktop (the desktop trims the silence around the voice first).

**Non-words are dropped before they play.** Twelve fixture clips in one
conversation (MCP's own synthesized cough and breath, and *Hmm.*, *Mmm.*,
*Ha ha ha!*, *Uh.*, *Mm-hmm.* said by a Windows voice, between real
questions): the six non-words that reached a request (two coughs, a breath,
*Mmm.*, laughter, *Mm-hmm.*) were all dropped before their first audio (the
check decided 89-497 ms after the request started; *Not words: Martlet dropped
its reply before it played*), none was recorded or remembered, and the talk
window showed *Ignored a sound (no speech).* and *Ignored "MMM." (not words).*
instead. *Hmm.* and *Uh.* never reached a request (shorter than the 0.45 s
gate). All four real utterances were answered (*Stop.* with `[pass]`), none
dropped. On `main`, the same *Mmm.* and a hum got a spoken reply. A hum longer
than 1 s of voice isn't checked; the model stayed quiet about it with
`[pass]`.

**NOT RUN:** a real microphone, coughs and hums from a person (synthesized ones
stood in), Chatterbox (its slower first audio leaves the check more time), and
a quiet graphics card for the desktop comparison (another program used it).

**NOT RUN:**
- **A cloud Thinking model** (OpenRouter `x-ai/grok-4.3`, the current route):
  no key is set up for the bench. Run
  `$env:OPENROUTER_API_KEY = '<key>'; vb relay start; vb pipeline --think openrouter:x-ai/grok-4.3 --stt parakeet-110m-en --modes cascade --clips prompts-voice --allow-cloud`.
  It can't use omni: OpenRouter lists its inputs as text, image and file.
- **The RTX 5080 on IMOUTO**: the developer's validation hosts list only
  Docker on this PC, so it isn't reachable from here.
- **The owner's own microphone** (`vb clips record`): the AMI and desk-mic
  sets stand in for it.
- **Listening:** no one listened to the replies; the voice audio is saved with
  each run for that.

## The end-of-turn judge

**2026-10-07.** The plain pause (800 ms by default) was the largest single
wait. Always listening now asks an end-of-turn judge after **260 ms** of
silence (`EndOfTurnGate` in Martlet.Conversation):

1. Smart Turn v3.2 (Pipecat, BSD 2-Clause; the 8.7 MB int8 CPU model ships in
   `Desktop\turn-detection\`) hears the last 8 s of what you said, from just
   before your voice began to now, and gives the chance that you finished. It
   runs through the ONNX Runtime that ships with sherpa-onnx, on two processor
   threads, with Martlet's own Whisper-style features (`WhisperFeatures`, the
   same numbers as Pipecat's to 3 decimals).
2. With Parakeet on this PC as Listening, a quick transcript of exactly the
   speech that would be kept (pre-roll to the pause plus the 200 ms tail)
   starts at the same moment.
3. **Complete** (above 0.5): the turn ends now. Speech-to-text reuses the quick
   transcript when the kept audio is byte for byte what it transcribed, so
   there is no second transcription (*End of turn: speech-to-text reused the
   quick transcript ...* in the log).
4. **Incomplete**: listening goes on for up to twice the plain pause (at least
   1.6 s, at most 5 s), so trailing off mid-thought is cut off less. Talking
   again asks again at the next pause.
5. Smart Turn missing or failed: a Thinking-pool member reads the quick
   transcript instead (`PoolTurnJudge`, an `EndOfTurnJudge` job with a 500 ms
   budget). No judge at all, an error, or no answer before the plain pause
   ends: the plain pause decides, exactly as with the judge off. The log says
   why.

Each decision writes *End of turn: complete (Smart Turn v3.2, 0.93) after 280
ms of silence; judge 31 ms.* (or *incomplete*, *you went on talking*, *slow*,
*failed*, *not judged*). The reply latency line then starts with *end-of-turn
wait 260, end-of-turn judge 40* instead of *end of speech 800*. Companion ›
Listening shows the newest decisions (`TalkJudgeTurnsStatus`), and MCP's
`turn_judge_check` runs the bundled model and the gate headless
([MCP](MCP.md#latency)). Other judges plug in behind Smart Turn through
`IEndOfTurnJudge` and `EndOfTurnJudges.WithFallback`, as the Thinking-pool one
does. Smart Turn runs once while it loads, so the first pause isn't slower.

**Measured on this PC** (Intel i7-13700K, no NVIDIA card; MCP
`turn_judge_check` on the Release build): the model loads in about 1.5 s in
the background when listening starts (60 s once on a cold disk right after the
build), and judges in **26 ms median** (25-40 ms, features included). It got
all six Windows-voice phrases right: three finished questions at 0.92-0.98,
three that trail off ("... and", "... we could", "... about is") at 0.01-0.19.

| End of speech | Before | After |
| --- | --- | --- |
| A finished turn | 800 ms (the plain pause) | 260 ms + the judge (26 ms median) + up to 20 ms for the next frame: about 300-330 ms |
| Speech-to-text with Parakeet on this PC | starts after the pause | started at 260 ms; reused, so usually done when the turn ends |
| An unfinished turn | 800 ms (and a cut-off) | up to 1,600 ms, to let you finish |
| Judge off, missing, failed or slow | 800 ms | 800 ms |

**NOT RUN:** *Reply latency* lines before and after with a real conversation.
This PC has no microphone, no Thinking model or voice it can use without real
credentials (no Ollama, no local model) and no Parakeet download, so no reply
ran. The judge, the gate and the transcript reuse were measured and tested
separately (above, and the tests in Martlet.Conversation.Tests,
Martlet.Audio.Tests and Martlet.Desktop.Tests). The Thinking request is
unchanged, so the *Thinking input* prompt-cache numbers can't move.

## Starting replies early

**2026-10-07.** When the judge says *Incomplete* but you are done, the turn
ends only at the longer pause (1.6 s), and the whole reply pipeline (Thinking's
first sentence, then the voice's first audio) still follows. With the judge off
or missing, the plain pause (800 ms) waits the same way. Companion › Listening ›
**Start replies early** (on by default) starts the reply at the end-of-turn
check point instead, like LiveKit's *preemptive generation*, Deepgram's
*speculative replies* and Pipecat's speculation gate:

1. At the short pause (260 ms, `EndOfTurnOptions.JudgeAfter`), the quick
   transcript of the speech so far starts (Parakeet on this PC; without a
   judge it starts now too, but only when a reply may start early: a cloud
   Thinking model without *Also for cloud models* gets none). When it comes back with real words
   (`EarlyReplyGate.Worth`: the word check keeps it, and it isn't only a quick
   backchannel such as *yeah*), the reply starts at once, built exactly as the
   talk window will ask for it (`EarlyReplyPlan`), without waiting for the
   verdict. It runs on the app slot like any reply, *held*
   (`ConversationRuntime.StartEarly`): Thinking streams, and with *Prepare the
   voice early too* the first spoken piece is made, but nothing shows, plays,
   acts (character cues, captions) or calls a tool.
2. The turn ends in the same pause and the talk window asks for the same
   request (`EarlyAsk.Differs`: the same words, voice, recording, chattiness,
   call and who spoke): the reply is **promoted**
   (`ConversationTurn.Release`). What it wrote shows at once and its first
   piece plays at once. There is no second request.
3. Your own voice comes back during the pause (never what the speakers play,
   with echo reduction, and never what *Hear what this PC plays* hears), the
   turn ends in a later pause, the words differ,
   or something else goes with them (a picture, what this PC played, typed
   text): the reply is **let go**. Its Thinking stream and voice work stop,
   nothing of it was shown or said, and the next pause starts another with the
   longer words. At most three start in one turn
   (`EarlyReplyOptions.MaximumStarts`).
4. Only a reply that is taken does what a reply does: committing to answer
   (the participation policy is only *asked* before), letting go of old
   history, consuming the [context board](CONVERSATION.md#context-board)'s
   consume-on-read notes (`MarkSent` runs at promotion, so a reply let go
   leaves them for the next request), the history, memory and after-reply
   work, the talk window's bubbles and the reply latency line. The request
   bytes are the ones the turn would have sent, so prompt caches hold; a reply
   let go leaves the same start in the cache for the next one.
5. A cloud Thinking model charges for the input of a request that is let go,
   so *Also for cloud models (may add a small cost)* is a separate choice, off
   by default: without it, replies start early only with a Thinking model on
   your own computers (this PC, a paired Martlet host, or a server on your
   home network). A paid cloud voice (OpenAI speech) is prepared early only
   with that choice too; a voice on this PC or a paired host is free. When
   Thinking fails, a held reply asks the Thinking fallback (Companion ›
   Thinking › *If Thinking fails*, often a cloud model) only once it is taken,
   as a reply that started after your turn would.
6. Barge-in never sees a held reply as Martlet speaking (`Speaking`,
   `Held`), and listening goes on as usual. Home Assistant's Assist acts as it
   answers, so a reply that would ask it never starts early.
7. A held reply holds the [live floor](CONVERSATION.md#the-live-floor-the-live-turn-comes-first)
   from its start, so background work on the conversation's hardware stops
   for it as for any reply, and its request goes to a paired host as live
   work. A reply let go ends its hold at once; one that is taken keeps it until
   its voice is made.

**What to expect.** On an *Incomplete* turn that you meant to end, the reply is
ready when the longer pause ends the turn: the time to the first audio after
the turn ended drops by about Thinking's first sentence plus the voice's first
audio (0.6-0.8 s with a fast local model and Chatterbox). With the 800 ms plain
pause it drops by about 0.5 s. A *Complete* turn ends about 300 ms into the
pause, usually before the quick transcript comes back, so the gain there is
small or none.

**Observability.** The desktop log says *Early reply: started 262 ms into your
pause (start 1 of 3; the quick transcript, the voice prepared too).* and, when
it ends, *Early reply: promoted after 1340 ms; it started 262 ms into your pause
(start 1), and its first piece was ready.* or *Early reply: let go after 420 ms
(you went on talking); ...*. The reply latency line adds *Started early at 262
ms, promoted.* (or *(started early 2 times, 1 cancelled)*, or for a reply that
started normally after its turn let go of every start, *Started early 1 time, 1
cancelled.*), just before its *Live floor* part; the reply's own steps before the turn ended show among the end of
the turn's, in the order they happened, then *promoted* when it was taken.
Companion › Listening's `TalkEarlyRepliesStatus` counts the newest outcomes,
and MCP's `latency_report` sums them (`early`). MCP's `early_reply_check`
rehearses the gates, the held turn and the comparison in real time with
fixtures ([MCP](MCP.md#latency)).

**Measured on this PC** (MCP `early_reply_check`, Release build, FIXTURES, NOT
AI: quick transcript after 90 ms, judge after 26 ms, Thinking's first words
after 200 ms, the voice's first audio after 350 ms; real time, 20 ms frames).
First audio after the turn ended:

| Scenario | Without | Started early | Sooner by |
| --- | --- | --- | --- |
| *Incomplete*, then silence until the 1.6 s pause | 662 ms | 15 ms | 647 ms |
| No judge, the plain 800 ms pause | 577 ms | 139 ms | 438 ms |
| *Complete* at about 300 ms | 621 ms | 638 ms (nothing started early) | none |
| You go on talking at 700 ms, then *Incomplete* again | 574 ms | 15 ms (2 starts, 1 let go, its request aborted) | 559 ms |
| The final words differ from the quick transcript | 575 ms | 576 ms (let go, started again) | none |

Nothing played or showed before the turn ended in any scenario, and every
reply let go ended *Canceled* with nothing played. When the turn ended, the
live floor was *Live* and held by the one reply started early; no reply held
it once the reply was done. Where nothing changes (*Complete*, *changed*),
runs differ by about 20 ms either way from timer noise. One run of *changed*
had the first audio 84 ms later with early replies, when the fixture's own
frame loop ran 190 ms late; three more runs had it 1 ms later, 19 ms sooner
and 138 ms sooner.

**NOT RUN:** *Reply latency* and *Thinking input* lines before and after with a
real conversation: this PC has no microphone, no Thinking model or voice it can
use without real credentials and no Parakeet download (starting replies early
needs Parakeet on this PC as Listening). The desktop path was tested with
fixtures instead (Martlet.Desktop.Tests: `EarlyReplyDesktopTests` and the talk
window's `AlwaysListeningStartsTheReplyEarlyAndShowsItOnlyOnceYourTurnEnds`).
The requests are the ones a turn sends, so prompt caching can't get worse; a
request let go costs a local model only the work it did.

## Quick sounds while Martlet thinks

**2026-10-07, opt-in, off by default.** Companion › Voice › *Quick sounds while
Martlet thinks*. When a reply is slow to start, Martlet first says a short sound
in its own voice (*"Mm,"*, *"Hmm..."*, *"Oh,"*, *"Ah,"*; with Dia also a soft
breath in) and the reply follows it. ElevenLabs Agents do the same with a
filler message when the model is slow
([soft timeout](https://elevenlabs.io/docs/eleven-agents/customization/conversation-flow)),
and OpenAI's realtime guide calls the short sentence before a slow step a
*preamble*. A quick sound doesn't make the reply's words come sooner: it fills
the silence, so the wait feels shorter.

**When one plays** (`QuickSoundGate`, `QuickSoundWatcher` in
Martlet.Conversation; checked every 25 ms beside the reply, never on its path):

- Only on a spoken reply to you that is confirmed: at once for a typed or heard
  reply, or the moment a [reply started early](#starting-replies-early) is
  taken. Never while it is held, never for one that is let go, never for a
  song.
- Only when it is slow: its own first audio isn't there 700 ms after it was
  confirmed (0.5, 0.7, 1 or 1.5 s, the owner's choice), or 300 ms after when
  the Thinking model thinks before it answers (Thinking steps on, or hidden
  reasoning streams before any words).
- Never when the reply's own audio is ready, never twice in one reply, at most
  one every 20 seconds, never while the reply is paused because you talked over
  it. The clips take turns.
- The clip plays on its own playback run ahead of the reply
  (`ConversationTurn.PlayQuickSound`); the reply's first piece waits for it to
  end and then plays, so neither is cut. It is never in the reply's text,
  captions or history.

**The cost.** The reply's own first audio can wait for the rest of a clip that
already started: at most 1.2 s (the longest clip kept), usually less than
0.5 s. That is why it is off by default; turned on, the first sound you hear
still comes sooner than the reply's first words would on a slow turn.

**Made once per voice and character.** The clips are said by the reply's own
voice through the same path and one-use permission as a reply's pieces
(`ConversationRuntime.SynthesizeAsync`), never beside a reply, and kept in
`quick-sounds\<key>\` in the data folder (the silence around them cut, at most
1.2 s each). A paired host's voice makes them as soon as the
choice is on or the voice changes; a paid cloud voice (OpenAI's) makes them
only when you press *Make quick sounds now* (one short request each).

**Measured** with MCP's `quick_sounds_check` (fixture turns through the
production runtime, Chat Completions adapter, host voice stream and playback
sink; the fixture model answers after a set wait, the fixture voice is a tone,
NOT AI):

| Scenario | Quick sound | From confirmation |
| --- | --- | --- |
| First words after 1.8 s | *"Mm,"*, then the reply on its own run, uncut | 701 ms |
| First words after 40 ms | none: its own first audio was ready | |
| A second slow reply 2 s later | none: one played 2 s ago | |
| Started early, held for 1 s, then taken | played 1,747 ms after the start | 717 ms after it was taken |
| Started early, then let go | none, nothing played | |
| Hidden reasoning before the words | *"Mm,"* | 313 ms |
| Paused because you talked over it | none | |

The reply latency line then ends with *Quick sound at 712 ms.* (from the same
moment as its total), and MCP's `latency_report` counts the replies with one.

**NOT RUN:** a real voice making the clips and a person listening to them: this
PC has no paired voice host, and a Windows voice was tested only through a
fixture (Martlet.Desktop.Tests `QuickSoundDesktopTests`); no speakers played
anything.

## Backup Thinking: a second model for slow starts

**2026-10-07, opt-in, off by default.** Companion › Thinking pool › *Backup
Thinking*. A Thinking model is sometimes slow to start: it is busy with another
request, loading, or a cloud provider's queue is long. Most replies start in
the usual time, but the slow ones are the ones you remember. Dean and Barroso
("The Tail at Scale", Communications of the ACM 56(2), 2013) call the fix a
*hedged request*: when a request takes longer than most do, send the same
request to a second server and use whichever answers first. Waiting until
about the 95th percentile keeps the extra requests to about 1 in 20.

**What Martlet does** (details:
[Backup Thinking](CONVERSATION.md#backup-thinking-a-hedged-request)):

- It waits for the reply's first words for the 95th percentile of the last 20
  replies' first words (never under 900 ms; 1.5 s until 3 replies are known),
  or a fixed 0.5 s to 3 s.
- Then the same request also goes to the first Thinking pool member the owner
  ticked *Backup for slow replies* that shares no hardware with the
  conversation and can take the request as it is. A paid cloud member is asked
  only when ticked, and only for a reply that is taken.
- The stream with words first gives the reply; the other is stopped at once,
  off the reply's path, so stopping it never delays the words.
- The conversation's own request doesn't change, so its prompt cache is kept.
  The time to the first words can only stay the same or get shorter: the
  backup is asked only after the wait, and its words are used only when they
  come first.

**Measured** with MCP's `backup_thinking_check` (the production race,
`ConversationRuntime.OpenTextAsync` and member choice; two fixture Chat
Completions endpoints on 127.0.0.1 answer after set waits, canned words, NOT AI;
900 ms wait):

| Scenario | Reply from | Member asked | First words | The other stream |
| --- | --- | --- | --- | --- |
| The conversation's model needs 3 s; the member 100 ms | the member | at 962 ms | 1,091 ms | stopped at 1,121 ms |
| The conversation's model needs 200 ms | the conversation's model | not asked | 193 ms | |
| The conversation's model needs 1.3 s; the member 1.5 s | the conversation's model | at 911 ms | 1,303 ms | the member stopped at 1,305 ms |
| The conversation's model fails at 1.1 s; the member needs 600 ms | the member | at 911 ms | 1,513 ms | |
| No member may answer | the conversation's model | none | 1,515 ms | |
| Started early, held 1.5 s, only a paid cloud member | the member | at 1,505 ms, once taken | 1,609 ms | stopped at 1,610 ms |
| Started early, then let go at 1.5 s | nothing | at 908 ms (home network) | | both stopped at 1,501 ms |

The reply latency line then says *Backup Thinking won at 1104 ms (diva
(qwen3-8b), asked at 912 ms).* (from the same moment as its total) or *... the
conversation's model won.*, and MCP's `latency_report` counts the results.

**NOT RUN:** a real slow Thinking model and a real pool member racing it: this
PC has no Ollama and no paired Thinking host, and no paid request was made.
The *Reply latency* and *Thinking input* lines before and after the change on a
real model are NOT RUN for the same reason. The race runs only when the owner
turns it on; with it off, nothing is raced and a reply's request is read as
before.

## How others get fast

Research summary (sources checked 2026-10-03; vendor claims marked):

- **Turn detection, not silence.** Pipecat's Smart Turn v3 (BSD-2, 8 MB ONNX)
  decides end-of-turn from the audio in about 12 ms on a CPU, replacing
  500-800 ms silence timers
  ([Daily](https://www.daily.co/blog/announcing-smart-turn-v3-with-cpu-inference-in-just-12ms/)).
  LiveKit's turn detector does the same on the transcript.
- **Start early.** LiveKit's *preemptive generation* starts the LLM on the
  transcript before end-of-turn is confirmed and keeps it if the final
  transcript matches ([LiveKit](https://docs.livekit.io/agents/logic/turns/)).
- **Stream every stage.** Pipecat starts each stage on partial data and
  measures VAD-stop → STT → TTFT → TTFA per turn
  ([Pipecat](https://docs.pipecat.ai/api-reference/server/utilities/observers/user-bot-latency-observer)).
- **No reasoning, fast silicon.** Voice agents turn thinking off; Groq and
  Cerebras serve open models with 100-200 ms to the first token. OpenRouter
  can sort providers by latency (`provider.sort: "latency"`,
  [OpenRouter](https://openrouter.ai/docs/guides/best-practices/latency-and-performance)).
- **Speech-to-speech models** (Moshi: 160-200 ms; OpenAI gpt-realtime, Gemini
  Live: about 0.4-0.75 s) skip the cascade, but none clones an arbitrary
  reference voice with sighs and laughs: Moshi has a fixed voice, OpenAI's
  custom voices are an approval program, Sesame CSM conditions on context
  rather than a reference clip. Character.AI publishes no voice-latency
  architecture. **Keep the cascade.**
- **Streaming TTS with cloning and non-verbals** (time to first audio):
  Chatterbox Turbo itself is not streaming upstream (a community fork reports
  about 470 ms on an RTX 4090); Qwen3-TTS (Apache-2.0, 3 s cloning,
  instructable emotion; about 97 ms claimed, unverified); CosyVoice 2/3 (about
  150 ms claimed); Orpheus (`<laugh>`, `<sigh>`; 100-200 ms); Fish Speech /
  OpenAudio S1 (emotion markers; weights non-commercial); IndexTTS2;
  Dia/Dia2 (`(laughs)`, `(sighs)`). Cloud: ElevenLabs Flash (about 75 ms
  model, 190 ms measured), Cartesia Sonic (`[laughter]`, about 90 ms claimed),
  Hume Octave.
- **Mask what's left.** A short backchannel in the cloned voice ("mm", a
  breath, a sigh) played right after you stop makes the wait feel shorter.

## A budget under 800 ms

| Stage | Before | Now | Floor with this pipeline | How |
| --- | --- | --- | --- | --- |
| End of speech | 800 ms | about 300-330 ms when the judge hears a finished turn (260 ms pause + 25-50 ms judge); 800 ms with it off | 200-300 ms | Done: Smart Turn v3.2 ([the end-of-turn judge](#the-end-of-turn-judge)) |
| Speech-to-text | not logged | logged | 0-150 ms | Parakeet 110M (about 90 ms) on a short utterance, or none: a Thinking model that hears takes the recording |
| Desktop prep | about 115 ms | about 115 ms | 30-50 ms | Event-driven talk window instead of its 100 ms tick, faster memory recall |
| Thinking to first sentence | 3-8 s | provider's time to first words with Thinking steps Off; started at the 260 ms pause when [replies start early](#starting-replies-early), so often ready when the turn ends | 150-250 ms | Gemma 4 E2B on this PC measured 140-240 ms; a fast provider; done: preemptive start |
| Voice to first audio | 1.2-4.2 s | 0.35-0.4 s | 0.25-0.3 s | Done: CUDA graph and streaming; a GPU the character doesn't share |
| Playback | 30-50 ms | 30-50 ms | 30 ms | |
| **Total** | **5.5-10 s** | **about 1.5-2.5 s** (estimate) | **about 0.7-1.2 s** | |

Under 800 ms is reachable only with every row at its floor, and with a cloud
model the network and provider queue alone can use half of it. Speaking a
backchannel at once would make most of the rest feel instant.

## Recommendations, biggest win first

1. **Hidden reasoning is off for conversation by default**: Companion ›
   Replies › **Thinking steps** is *Off* unless you choose *On* (OpenRouter
   `reasoning.effort: none`, which grok-4.3 accepts). Expected to remove most of
   the 3-8 s before the first word; *hidden reasoning* disappears from the latency line, which names the
   choice next to the model. A model that always thinks refuses Off; Martlet
   then asks again with the model's default, logs it and keeps the default for
   that model, so pick a model that can skip thinking.
2. **Update hosts to this version** so `imouto-host` rebuilds Chatterbox
   (image `:4`) with the speed-ups and streaming: the first audio of every
   piece after about 0.35-0.4 s, and no 7 s first reply.
3. **Shorter, smarter end of turn.** Done: Companion › Listening › **Judge
   when I finish talking** (on by default) asks Smart Turn v3.2 after a 260 ms
   pause and answers a finished turn at once, reusing the quick transcript it
   started then ([the end-of-turn judge](#the-end-of-turn-judge)). Martlet
   already restarts a reply when you keep talking.
4. **Start Thinking early.** Done: Companion › Listening › **Start replies
   early** (on by default) starts the reply on the quick transcript at the
   260 ms pause, keeps it hidden and promotes it when the turn ends with the
   same words ([starting replies early](#starting-replies-early)).
5. **A faster Thinking route.** A non-reasoning or fast model, OpenRouter
   provider sorting by latency, or a small model on this PC or DIVA's GPU (no
   internet round trip): Gemma 4 E2B in Ollama gives its first speakable piece
   in about 150 ms from text and 220-240 ms from a recording (see
   [Local options measured](#local-options-measured-voicebench)). Keep the
   voice on the GPU that isn't rendering the character.
6. **Omni for the reply, a transcript in parallel.** Done: with a Thinking
   model that hears (Gemma 4 in Ollama on this PC; Martlet detects it), Companion
   › Listening › When Thinking can hear you › **Send my voice straight to
   Thinking** (the default once Let Thinking hear my voice is on, which it is by
   default while Thinking runs on this PC) sends the
   recording alone and transcribes beside the reply for the talk window, history
   and memory; about 220 ms sooner to the first audio here ([measured](#straight-to-thinking-measured)).
   For a model that can't hear, a smaller
   transcriber (Parakeet 110M, about 90 ms) saves 150-250 ms over Parakeet v3,
   at the cost of more mistakes on noisy audio.
7. **Mask the rest** with a cached backchannel in the cloned voice.
8. Small desktop wins: wake the talk window when a transcript arrives
   instead of on its 100 ms tick; keep provider connections warm (pooled
   connections idle out after a minute, so a pause costs a new TLS handshake,
   visible as *Thinking connection*).

## How this was verified

- `spoken_reply_check` (MCP) runs the production conversation runtime with a
  fixture Chat Completions endpoint that streams hidden reasoning, a fixture
  voice and a silent fixture speaker: the line shows *hidden reasoning* and
  *voice synthesis* close to the simulated delays and its steps add up to the
  total; `latency_report` reads the new lines and the older ones.
- A disposable desktop (`-Desktop`) with Thinking on a loopback fixture logged
  the line for a typed message, and the Thinking longer table above (with
  `think_longer_check` rehearsing the scheduler, the background request, the
  Deep thinking plan and the side-by-side fit check headlessly).
- The Chatterbox numbers come from the container on IMOUTO, with the service
  started from this change on a side port next to the running one; streamed
  speech was compared with whole-piece decodes by measurement.
- **NOT RUN:** the always-listening steps with a real microphone (no audio
  capture in agent verification), hidden reasoning on OpenRouter with a real
  key (paid), listening to the CUDA-graph or streamed voice (no one listened;
  logits were identical to the library's and streamed speech measured within
  the model's own variation), streaming through a real paired gateway to the
  speakers, and GPUs other than the RTX 5080. For Thinking longer: a real
  model calling think_longer (Ollama on this PC was shared with other running
  work and kept swapping models, so local timings would have measured that), a
  second model in Ollama on this PC thinking beside Thinking's (the graphics
  card here was already mostly in use by other programs, so the pair didn't
  fit; how much it slows a reply's first words is not measured), a cloud
  provider's background think (paid), and Deep thinking on a real
  paired computer (none is paired here) or a cloud provider (paid).
