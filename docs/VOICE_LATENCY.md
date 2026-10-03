# Voice latency: from you stopping talking to Martlet's voice

**Findings and plan, 2026-10-03.** Goal: Martlet starts speaking less than
**800 ms** after you stop talking, while always keeping voice cloning and
non-verbal sounds (`[sigh]`, `[laugh]`). Measured on IMOUTO (RTX 5080, the
desktop and the `imouto-host` Chatterbox Turbo voice in Docker Desktop) with
DIVA (RTX 4070, `diva-host` lip-sync), OpenRouter `x-ai/grok-4.3` for Thinking
and Parakeet on this PC for speech-to-text. Numbers are this setup's, not
qualification.

**Short answer.** Today a spoken reply starts about **5.5-10 s** (sometimes
24 s) after you stop. Most of it is two stages: the Thinking model's hidden
reasoning before its first word (3-8 s) and synthesizing each whole sentence
before any of it plays (1-3.5 s, plus about 7 s on the first reply after the
voice service starts). Both can be cut a lot without giving up cloning or
sighs. Under 800 ms needs every stage near its floor at once (see
[the budget](#a-budget-under-800-ms)); 1.5-2.5 s is realistic soon with a cloud
model, and under 1 s needs streaming speech, a smarter end-of-turn detector
and a fast or local Thinking model.

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
Companion › Replies › **Thinking steps** › *Off* now sends OpenRouter
`reasoning.effort: none` (see [Conversation](CONVERSATION.md)).
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

## What this change does

1. **Reply latency line** and MCP `latency_report`, above.
2. **Faster Chatterbox service** (image `martlet-chatterbox:3`, see
   [Chatterbox](CHATTERBOX_VOICE.md#how-it-runs)): voice conditionals computed
   once per reference recording; the first-call warm-up paid while loading;
   T3 decoded by replaying one captured CUDA graph per token over a static KV
   cache, with the library's own sampling and identical logits (checked
   against eager decoding). Same words, same voice, same watermark, same tags.

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

| Stage | Today | Floor with this pipeline | How |
| --- | --- | --- | --- |
| End of speech | 800 ms | 200-300 ms | Smart Turn v3 with a shorter pause; 500 ms exists today |
| Speech-to-text | logged now | 50-150 ms | Parakeet int8 on a short utterance; streaming STT overlaps it with speaking |
| Desktop prep | about 115 ms | 30-50 ms | Event-driven talk window instead of its 100 ms tick, faster memory recall |
| Thinking to first clause | 3-8 s | 150-400 ms | Reasoning off; a fast provider or a small local model on the other PC's GPU; preemptive start |
| Voice to first audio | 0.9-3.5 s | 250-350 ms | CUDA graph (done) plus streaming the first 10-15 tokens of each piece |
| Playback | 30-50 ms | 30 ms | |
| **Total** | **5.5-10 s** | **about 0.7-1.3 s** | |

Under 800 ms is reachable only with every row at its floor, and with a cloud
model the network and provider queue alone can use half of it. Speaking a
backchannel at once would make most of the rest feel instant.

## Recommendations, biggest win first

1. **Turn hidden reasoning off for conversation**: Companion › Replies ›
   **Thinking steps** › *Off* (OpenRouter `reasoning.effort: none`, which
   grok-4.3 accepts). Expected to remove most of the 3-8 s before the first
   word; *hidden reasoning* disappears from the latency line, which names the
   choice next to the model. A model that always thinks refuses Off; Martlet
   then asks again with the model's default, logs it and keeps the default for
   that model, so pick a model that can skip thinking.
2. **Update hosts to this version** so `imouto-host` rebuilds Chatterbox with
   the speed-ups (30-60% less synthesis time, no 7 s first reply).
3. **Stream each piece.** Emit the first 10-15 T3 tokens through S3Gen as soon
   as they exist (250-360 ms measured with the graph) and the rest in
   overlapping chunks with a short crossfade; the protocol and the desktop
   already play frames as they arrive.
4. **Shorter, smarter end of turn.** Choose the 500 ms pause today; add a
   turn-detection model (Smart Turn v3) so 200-300 ms doesn't cut you off.
   Martlet already restarts a reply when you keep talking.
5. **Start Thinking early.** Send the transcript at a short pause and keep the
   reply if nothing else is said (the restart already exists).
6. **A faster Thinking route.** A non-reasoning or fast model, OpenRouter
   provider sorting by latency, or a 7-8B model on DIVA's GPU (no internet
   round trip). Keep the voice on the GPU that isn't rendering the character.
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
  the line for a typed message.
- The Chatterbox numbers come from the container on IMOUTO, with the service
  started from this change on a side port next to the running one.
- **NOT RUN:** the always-listening steps with a real microphone (no audio
  capture in agent verification), hidden reasoning on OpenRouter with a real
  key (paid), listening to the CUDA-graph voice (no one listened; logits were
  identical to the library's), and GPUs other than the RTX 5080.
