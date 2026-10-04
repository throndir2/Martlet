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
| recording | Closing the recording and cutting out the speech |
| Voice ID, speech-to-text | Checking it's you (when on), then transcribing (waits behind an earlier utterance) |
| voice recognition | Waiting for who spoke (at most 3 s) |
| waiting to answer | Collected by the talk window (100 ms tick), waiting for more when it sounds unfinished (1.5 s), or for the app slot |
| preparing, memory, lore, tools, Home Assistant | Settings and policy checks, memory recall, lorebooks, MCP tools, Home Assistant's Assist |
| building the request | Persona, history and budgets into one request |
| Thinking authorization, connection | Per-request permission; then until the provider's response headers (network, TLS, queueing) |
| Thinking before reasoning, hidden reasoning | A reasoning model's thinking before its first word (shown only when the provider streams it) |
| Thinking first words | Until the first word when no reasoning was streamed |
| first sentence | Until the first piece the voice can say (a clause of 24+ characters or a sentence) |
| voice authorization, voice synthesis | Per-piece permission; then until the voice's first audio arrives |
| playback start, speakers | Handing audio to the speakers until Windows plays it |

Push-to-talk counts from letting go of the talk button, typed messages from
sending them. MCP's `latency_report` summarizes the newest lines (median and
90th percentile of the total and of every step, the slowest steps, the models);
see [MCP](MCP.md#latency). The Chatterbox service also logs, per reply, how
much speech it made and how long it took (never the words).

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
   - When the think shares the conversation's hardware (Thinking's own model
     on this PC or a paired computer, or any model on this PC while Thinking or
     the voice runs here: one request at a time and one graphics card) it stops
     the moment you talk or a reply, glance or after-reply request starts, and
     starts again from the latest exchange once it's quiet; replies never
     queue behind it.
   - **Deep thinking** (Companion › Deep thinking › *Where it thinks*) can put
     it on another machine instead: a paired computer's Ollama, Ollama on this
     PC while the conversation runs elsewhere, or a cloud provider. Then it
     runs in parallel and is never paused, and the conversation's model,
     cache and graphics card are left alone. Its request doesn't carry the
     reply's tools, so it doesn't share the conversation's cache (it is on
     another server anyway).
   - A model that turns tools down is remembered on this PC for a week, so it
     isn't asked with tools (and again without) on every first reply.

Thinking longer, measured through a disposable desktop with Thinking on a
single-slot loopback fixture (one request at a time with a one-slot prompt
cache and about 10,000 tokens a second of prompt reading; canned text, NOT AI),
typed messages, warm:

| | *Reply latency* first words (ms) | *Thinking input* (Reply) from the cache |
| --- | --- | --- |
| Thinking longer off (no tools) | 55, 56, 59, 45 (median 56) | 99% of 865-940 tokens |
| On, nothing running | 67, 53, 57, 55 (median 56) | 99% of 773-888 tokens |
| On, a think running (it paused for each reply) | 55, 57, 65, 61 (median 59) | 99% of 976-1,089 tokens |

Each reply during the think reached the fixture 1 ms after the think's request
was stopped (it paused 4 times, then finished from 91% cached input and
Martlet brought it up on its own from 85%). An earlier build that resumed the
think from the reply that started it, against a fixture that noticed a hang-up
only every 100 ms, measured 93-138 ms with 917 of 937-1,013 tokens cached: why
a resumed think continues the latest exchange.

Deep thinking on a second single-slot loopback fixture (Custom server on
127.0.0.1, so the plan still waits for quiet moments because both share this
PC): typed messages, warm, *Reply latency* first words 59 and 60 ms with nothing
running, then 59, 45, 55 and 46 ms during the think, each reply 98-99% from the
cache (the think went to the second fixture without tools, paused 4 times and
finished in 57 s; Martlet brought it up from the first, 83% from the cache). With
a destination of its own (`think_longer_check`'s `parallel` part: the
production plan says parallel, the think is never stopped) three replies beside
it answered in 2 ms each, as without it.

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
whisper.cpp and Audio2Face host roles were resident (about 7.4 GB of the card
idle). Clips: 73 LibriSpeech utterances (accuracy) and 16 short companion
prompts spoken by Chatterbox in the starter voices (about 3 s each). Numbers are
this PC's, not qualification.

**Answer.** The fastest flow that keeps a cloned voice with sighs and laughs
is **Gemma 4 E2B hearing the recording itself, streaming into Chatterbox
Turbo**: about **760 ms** from the end of the recording to the first audio
(median, 16 turns), against 954 ms for the cascade (Parakeet, then text). Add
the end-of-speech pause (800 ms today) for the time after you stop:
1.56 s against 1.75 s. The omni flow saves the speech-to-text step but hears
less exactly than Parakeet (5-7% word errors against 2.3%), and Martlet still
needs a transcript for history, memory and voice ID. So the shape to build is
omni for the reply, with Parakeet transcribing **in parallel**, off the
critical path. A cascade with Parakeet 110M (86 ms on short turns) is close
behind on stage times, about 790 ms (estimated from stage medians, not run
end to end). The 800 ms pause is now the largest single wait; a turn detector
at 300 ms would put the omni flow near 1.06 s after you stop.

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
| Phi-4-multimodal 5.6B (PyTorch 4-bit, 2 turns) | 5,543 | 6,697 | 8,075 | | Good, too slow here |
| MiniCPM-o 2.6 8B (PyTorch 4-bit) | NOT RUN | | | | Installed; not run because the card was busy |

Hearing the audio adds 55-95 ms of prefill to Gemma 4 against text, less
than the speech-to-text it replaces. llama.cpp is as fast as Ollama, or
faster, for the same Gemma model. Every model writes its own reply; none of
them speaks in a cloned voice, so the voice stays Chatterbox.

**Voice** (Chatterbox Turbo host role on the 4070, streaming, starter voice
*Annie*, three runs): first audio 437-485 ms for sentences, including
`[sigh]` and `[laugh]` (real-time factor 0.43-0.63), and 800 ms for a
two-word piece.

**Whole turn** (Gemma 4 E2B on Ollama, Chatterbox role, 16 turns, medians):

| Flow | Speech-to-text | First piece | Voice first audio | First audio | After you stop (+800 ms) |
| --- | --- | --- | --- | --- | --- |
| Cascade (Parakeet v3, CPU) | 248 | 163 | 538 | 954 | 1,754 |
| Hearing (transcript and recording) | 255 | 252 | 549 | 1,083 | 1,883 |
| **Omni (recording only)** | | 222 | 542 | **762** | **1,562** |
| Cascade (faster-whisper large-v3-turbo, GPU) | 180 | 203 | 2,973 | 3,409 | 4,209 |

**The graphics card is the bottleneck on this PC.** The 90th percentiles
include 32-52 s stalls in the first turns, and the GPU speech-to-text run
slowed the voice five-fold. Gemma, Chatterbox, whisper.cpp, Audio2Face and the
desktop together exceed 12 GB, and Windows pages memory instead of failing.
During the runs a live Martlet session was also using the same roles, which
kept the card at 100% even after the benchmarks stopped. On a card shared with
the voice: keep speech-to-text on the processor (Parakeet), remove the
whisper.cpp role from a host whose desktop uses Parakeet (about 2 GB back),
and choose E2B (3.3 GB) over E4B.

**For Martlet.** Ollama 0.35 takes `input_audio` for Gemma 4 E2B, E4B and 12B
(a 12B request transcribed and answered a test clip). Martlet now lets Ollama
on this PC hear for models it says hear, and finds out what any Thinking model
hears and sees from its server's metadata or a test word
([Thinking models that hear and see](CONVERSATION.md#thinking-models-that-hear-and-see)),
the first step toward the omni flow. The Qwen, Voxtral,
Phi-4 and MiniCPM-o models are in the bench only: Martlet's local selector
runs Ollama, which serves none of them, and none beat Gemma 4 E2B here.

**NOT RUN:** a cloud Thinking model (OpenRouter `x-ai/grok-4.3`, the current
route: no key in this environment; the bench takes one with `--allow-cloud`),
MiniCPM-o 2.6 and the full Phi-4-multimodal set (the card was busy with a live
session), the Parakeet 110M cascade end to end, real microphone recordings
(`vb clips record`), and the RTX 5080 on IMOUTO. No one listened to the replies
in the comparison; the voice audio is saved with each run for that.

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
| End of speech | 800 ms | 800 ms (500 ms setting) | 200-300 ms | Smart Turn v3 with a shorter pause |
| Speech-to-text | not logged | logged | 50-150 ms | Parakeet int8 on a short utterance; streaming STT overlaps it with speaking |
| Desktop prep | about 115 ms | about 115 ms | 30-50 ms | Event-driven talk window instead of its 100 ms tick, faster memory recall |
| Thinking to first clause | 3-8 s | provider's time to first words with Thinking steps Off | 150-400 ms | A fast provider or a small local model on the other PC's GPU; preemptive start |
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
3. **Shorter, smarter end of turn.** Choose the 500 ms pause today; add a
   turn-detection model (Smart Turn v3) so 200-300 ms doesn't cut you off.
   Martlet already restarts a reply when you keep talking.
4. **Start Thinking early.** Send the transcript at a short pause and keep the
   reply if nothing else is said (the restart already exists).
5. **A faster Thinking route.** A non-reasoning or fast model, OpenRouter
   provider sorting by latency, or a 7-8B model on DIVA's GPU (no internet
   round trip). Keep the voice on the GPU that isn't rendering the character.
6. **Mask the rest** with a cached backchannel in the cloned voice.
7. Small desktop wins: wake the talk window when a transcript arrives
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
  `think_longer_check` rehearsing the scheduler, the background request and its
  pause and resume headlessly).
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
  work and kept swapping models, so local timings would have measured that) and
  a cloud provider's background think (paid), and Deep thinking on a real
paired computer (none is paired here) or a cloud provider (paid).
