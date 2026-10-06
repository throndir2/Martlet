# Recommendations: what runs where

Martlet looks at every computer in your Martlet network (graphics cards and
their memory, main memory, processor threads, operating system) and decides
where each part of Martlet should run: on one of your computers, on a free or
paid hosted service, or not at all. This page is the design behind that
decision. The code is `Martlet.Core.Planning` (`ComponentRanking`,
`FootprintCatalog`, `PlacementEngine`); the setup advisor, the welcome wizard and
the Devices view all use it, and `recommend_plan` in [Martlet MCP](MCP.md)
runs it from the command line.

The sizes it plans with are in [Resource footprints](RESOURCE_FOOTPRINTS.md);
what each hosted Thinking endpoint can do and how reliable it is, in
[Hosted Thinking](HOSTED_THINKING.md).

## The ranking

| Rank | Component | Necessity | Can a free hosted service do it? |
|---|---|---|---|
| 1 | Thinking | Required | Yes (NVIDIA Build, Google Gemini) |
| 2 | Voice | Core | No (only paid OpenAI, and free tiers ask you not to send voices) |
| 3 | Listening | Core | No (paid OpenAI only); Parakeet on the processor is enough |
| 4 | Character | Core | No; it is drawn where you sit, and it is tiny |
| 5 | Lip-sync | Core | No; loudness lip-sync always works |
| 6 | Deep thinking | Optional | Yes (NVIDIA Build) |
| 7 | Singing | Optional | No |
| 8 | Pictures | Optional | Yes (NVIDIA Build FLUX) |

**Required** parts always get something. Martlet cannot talk without Thinking.
**Core** parts always get at least their processor baseline (Windows voices,
Parakeet, loudness lip-sync). **Optional** parts are dropped, with a reason, when
nothing has room.

### Voice or Thinking first?

Thinking matters most, but it is also the only big part a free hosted service
can do well, so it does not need your hardware the most. The rank above is how
much each part matters. The order in which parts claim your graphics cards is
different (`ComponentRanking.ClaimOrder`):

1. **Character** on the PC you talk to (tiny).
2. **Voice.** A natural local voice (Chatterbox Turbo, about 0.4 s to the first
   audio) is the biggest difference you hear, nobody offers it for free, and a
   hosted voice adds a network trip to every sentence. It gets the first claim
   on an NVIDIA card. Without one, Windows voices speak on the processor.
3. **Listening** baseline: Parakeet on the processor (about 0.1-0.3 s for a
   short sentence). It is as accurate as Whisper in English, so it takes no card.
4. **Lip-sync:** advanced lip-sync (Audio2Face-3D) when an NVIDIA card still has
   about 4 GB free, otherwise loudness. The owner's call: a face that moves well
   matters more than hosting the model locally when a free endpoint can think.
5. **Thinking, the primary:**
   - a local model on a graphics card when one still fits (the fastest model
     that hears, Gemma 4 E2B: about 0.15 s to its first sentence, private, never
     rate-limited);
   - otherwise the best hosted endpoint the preference allows (NVIDIA Build by
     default: about 0.5 s per reply, free);
   - otherwise a local model on the processor (seconds per reply).
6. **Thinking fallbacks** when the primary is a free endpoint that can fail
   (below).
7. **Listening upgrade:** Whisper on a card only when it is better for the user
   or Parakeet's processor is overcommitted.
8. **Deep thinking, Singing, Pictures**, in that order, with what is left.

So on an 8 GB card Martlet runs the voice locally and thinks with NVIDIA Build;
on a 12 GB card it adds advanced lip-sync; on 16 GB or more the voice, lip-sync
and a local Gemma all fit and nothing leaves your computers.

Two settings change the order:

- **Keep everything local** (`HostingPreference.PreferLocal`): nothing hosted
  is planned, even when configured, so Thinking claims the card first (before
  the voice). With only a gaming card, Thinking uses it anyway with a warning,
  because the processor takes seconds per reply.
- **Fastest replies** (`PlanRequest.ThinkingFirst`, the setup advisor's goal):
  a local Thinking model claims the card before the voice; hosted stays allowed.

### Free endpoints are flaky: the fallback chain

Free endpoints rate-limit (NVIDIA Build: 40 requests a minute), retire models
without notice, and go down. When the primary Thinking is a free endpoint whose
reliability is not High, the plan adds a fallback chain, tried in order:

1. A local model on a graphics card, if one fits after everything above: fast
   and immune to outages, so the chain ends there.
2. Otherwise another company's endpoint (Google Gemini behind NVIDIA Build): one
   outage never takes both down. Never NVIDIA behind NVIDIA.
3. A local model on the processor as the last resort: slow (about 2.5 s to the
   first word), but it works offline.

When nothing can stand in, the plan says so in its notes.

### Hearing

Omni models hear the recording themselves, so no transcript has to come first.
The hosted ranking barely counts hearing, because the free tiers' terms ask you
not to send voices: Martlet plans the transcript path by default, and Listening
is always placed locally. Locally, the smallest model that hears (Gemma 4 E2B) is
the default Thinking model.

## Upgrade tiers within a component

Each option has a quality tier from 1 (basic) to 5 (best) inside its component.
The engine picks the best option that fits in claim order and, after the core
parts are placed, suggests the next tier up with what it needs.

| Component | Tiers (low to high) |
|---|---|
| Thinking, local | Gemma 4 E2B (1, hears) · Qwen3.5 4B, Gemma 4 E4B (2) · Gemma 4 12B (3, hears) · Gemma 4 26B (4) |
| Thinking, hosted | Gemini Flash-Lite, Nemotron omni (3, hear) · NVIDIA Build, OpenRouter (4) · OpenAI (5, paid) |
| Voice | Windows / macOS voices (1) · XTTS-v2 (3) · Chatterbox Turbo (4) · OpenAI voice (4, paid) |
| Listening | Parakeet (4) · Whisper large-v3 turbo on a card (4) · OpenAI transcription (5, paid) |
| Lip-sync | Loudness (1) · Audio2Face-3D (4) |
| Deep thinking | Gemma 4 E4B (2) · 12B (3) · 26B (4) · NVIDIA Build (4, hosted) |

Local Thinking deliberately stays on the fastest model that hears, because
latency is king in conversation (see [AGENTS.md](../AGENTS.md#never-add-conversation-latency)).
When a smarter model also fits, the plan suggests it and says how much later
its first word comes.

## Local or hosted: the score

Within a step, options are ranked by a score:

- quality tier × 10;
- minus 10 for Low and 5 for Medium reliability;
- **local audio-path work** (voice, listening, lip-sync, character) +20: no
  network trip per sentence, no cost, the voice stays in your network;
- other local work +5 (Balanced) or +0 (happy with hosted);
- hosted on a provider you have not set up −3 (it means signing up).

Which hosted options are allowed at all:

| Preference | Hosted options planned |
|---|---|
| Keep everything local | none |
| Balanced (default) | configured providers; free sign-up providers for Thinking, Deep thinking and Pictures |
| Happy with hosted | same as Balanced, and Thinking goes hosted first |

Free sign-up providers are never planned for the audio path (voice, listening,
lip-sync): their terms ask not to receive voices. A plan that uses a free
provider you have not set up includes a **SignUp** suggestion.

## Packing machines

Every local option has a footprint: graphics memory at its peak (plus one
context for language models), main memory at its peak and processor threads
while it works (`ComponentOption.Reserve`). Each machine keeps headroom:

- **Graphics cards:** at least 0.8 GB or 10% for the driver and desktop, or
  what is already used when the Desktop reports it (games, other programs). A
  card kept for games is left alone.
- **Main memory:** a quarter (at least 4 GB) on the PC you talk to, 15% (at
  least 2 GB) on hosts. On Apple Silicon the graphics card shares main memory,
  so anything on it counts against both.
- **Processor:** two threads on the PC you talk to and one on hosts are kept
  free; the rest may be shared up to 150%, since parts rarely work at the same
  moment.

Where an option lands, in order of preference:

1. where it runs today (no needless moves);
2. an always-on machine over one on battery;
3. a host over the PC you talk to (games and the desktop stay smooth; a LAN hop
   costs about a millisecond);
4. audio work on a card without a language model (generation competes for the
   card's compute);
5. an idle card over a busy one;
6. AMD, Intel or Apple cards for any-vendor work (Ollama), so NVIDIA stays free
   for NVIDIA-only engines;
7. language models on the roomiest card (room for context and upgrades);
   everything else on the tightest fit, so big cards stay free.

Optional parts never share a card with the voice: singing, pictures and deep
thinking burst the card's compute and would make the voice start late.

`PlacementEngine.Afford(plan, option)` counts how many more copies of an option
fit in what is left ("2 more Deep thinking models fit"), per machine or across
the network, with the same rules.

## A machine joins or leaves

`SuggestForJoiningMachine` plans the network with the new machine, keeping
what runs today where it runs, and compares:

- **RunLocally:** a hosted part now fits locally ("gpu-box can run Gemma 4 E2B
  locally instead of NVIDIA Build: its first word comes sooner, your
  conversation stays on your computers and it keeps working when the free
  endpoint is down").
- **Upgrade:** a better option now fits (advanced lip-sync on a new 8 GB laptop
  rather than a local model; a smarter hearing model on a new 24 GB box).
- **Add:** an optional part that had no room now has some.
- **Move**, **AddFallback**, **SignUp**, **Drop**.

`SuggestForLeavingMachine` replans without the machine; parts it ran move,
**Downgrade** (Chatterbox to Windows voices) or **Drop** with their reasons.
`Measure` reports today's setup as it is, without replanning, for the Devices
view's bars.

## Gaps and assumptions

- **Peaks, not concurrency.** Memory is reserved at each part's peak, so the
  plan is safe but leaves room unused while parts idle. Processor threads are
  shared (150%) on the assumption that parts rarely work at the same moment;
  speaking while listening does overlap a little.
- **Always-on app work** (voice activity detection, speaker ID, the idle
  Desktop: about 0.4 GB and short 4-thread bursts) is covered by the PC's
  memory and processor headroom rather than listed as parts.
- **GPU sharing on Windows.** Windows pages graphics memory to main memory
  instead of failing, so an overfilled card makes the voice start seconds late
  ([SharedGpu](../src/Martlet.Core/Installation/SharedGpu.cs)). The plan notes
  when a Windows machine's voice shares its card.
- **CPU-only machines** run Windows voices, Parakeet and, when you keep
  everything local, a slow Thinking model; hosted Thinking is far faster.
- **AMD and Intel cards** run Thinking and Deep thinking (Ollama). Voice
  engines, Whisper on a card, Audio2Face, singing and ComfyUI need NVIDIA.
- **Apple Silicon** counts graphics as part of main memory (70% usable) and has
  no NVIDIA engines; it speaks with macOS voices.
- **Laptops on battery** are used last for host work, and the plan notes that
  their parts stop when they sleep.
- **Gaming:** a card marked for games is not used, except for Thinking when you
  keep everything local and nothing else can think.
- **The setup advisor** asks only about graphics cards; it plans every
  computer as 32 GB of memory and 16 threads.
- **Not modelled yet:** network bandwidth and latency between machines, power
  limits, VRAM fragmentation across several models on one card, model download
  time, and per-language quality (Whisper covers more languages than Parakeet).
