# Recommendation design: ask three questions, plan for three situations

Status: **Approved, being built** (2026-10-10). Stage 1, *Preferences*, stage
2, *Situations*, and the first part of stage 3, *the planner on the catalog*,
are done (see [Build stages](#build-stages)). It uses the model catalog and
route facts in [Model catalog](MODEL_CATALOG.md)
([#663](https://github.com/throndir2/Martlet/issues/663)). Today's rules are in
[Recommended setups](RECOMMENDED_SETUPS.md); this page says what changes.

## The idea

Martlet asks the owner three simple questions and finds everything else itself:
the hardware, the other computers, the model apps already running and the saved
keys. Then it makes **three plans at once**:

- **Normal**: the usual setup.
- **While gaming**: the same setup with the gaming PC's graphics card left free
  for the game.
- **Host away**: the setup when a host computer doesn't answer.

Martlet changes between the three plans by itself while it runs. `PcActivity`
already finds games, and the Thinking pool and backup routes already move the
work. The owner makes no choices about models, quantizations, context sizes or
graphics cards. An owner who wants to choose can still do it and lock the
choice.

## What Martlet asks

1. **"Do you play games or use heavy apps on this PC?"** Yes or No. Martlet
   selects Yes when it finds a game library (Steam, Epic, GOG and others).
   This sets `MachineSpecs.KeepGpuForGames`, which exists but nothing sets
   today, and turns on the While gaming plan.
2. **"May Martlet use free online services?"** Never / Only as a backup (the
   default) / Yes, when they're faster or smarter. This replaces the wizard's
   two choices today and maps to `HostingPreference` plus a new backup level.
3. **"What matters more in a conversation?"** Quick replies / Balanced (the
   default) / Smarter replies. This sets the reply quality level (below).

Martlet does not ask about models, memory, graphics cards or hearing.

## Recommendation preferences (a saved setting)

The answers are saved as the owner's **recommendation preferences**. Today the
wizard's answer is not saved, and Recommended setup guesses it from the saved
keys (`RecommendedSetupInputs`). With this setting, Recommended setup always
plans with what the owner chose.

| Preference | Values (default first) | What the planner does with it |
| --- | --- | --- |
| Reply quality | Balanced / Quick replies / Smarter replies | Live Thinking's first-word target: 0.4 s, 0.25 s or 1.0 s. Quick replies also prefers the smaller of two models within one quality step; Smarter replies prefers the smarter one |
| Online services | Only as a backup / Never / Yes, when they're faster or smarter | Never: no hosted option anywhere. Backup: hosted options only in the While gaming and Host away plans and for Deep thinking. Yes: hosted options compete with local ones |
| Games or heavy apps | For each companion PC: Yes or No (Martlet's guess first) | Sets `KeepGpuForGames` for that PC and makes its While gaming plan |
| Prefer models that hear you | On / Off | Off removes the hearing preference; the model gets the transcript |
| Host graphics card share | Up to 90% / Up to 75% / Up to 50% | How much of each host card Martlet may plan with, for hosts that also do other work |
| Use models your apps already run | On / Off | Today's option (#665) |
| Prefer models your hosts already have | Off / On | Today's option (#666) |

- The first-run wizard saves the three answers. The other preferences start at
  their defaults.
- **Recommended setup shows every preference** above its plan, under *Your
  preferences*. A change plans again at once and is saved, so the next
  Recommended setup uses it too.
- The same preferences are in the settings, so the owner can change them
  without opening Recommended setup.
- The games answer belongs to each computer. The other preferences are shared
  by the owner's computers, like the other shared settings.

## How the planner decides

For each job, from the most important to the least (today's order: Thinking;
then the voice, listening and lip-sync; then the optional extras):

1. **Gates** remove the options that can't work:
   - the model fits completely on one card with its context and the headroom;
   - its runtime works on that computer;
   - it takes the inputs the job needs (from the catalog and route facts);
   - the owner's online answer allows it, and the model is not retired.
2. **A score** puts the remaining options in order (below).
3. **Placement** keeps today's rules: one language model per card, the voice on
   its own card on Windows, the live jobs first, companion PCs light, and the
   owner's choices kept.

### Live Thinking (the reply you hear)

- **Target: first word in 0.4 s or less** (Balanced; the reply quality
  preference sets 0.25 s or 1.0 s). Measured on an RTX 4070: Gemma 4 E2B
  0.15 s, Gemma 4 E4B 0.21 s, Qwen3.5 4B 0.33 s. NVIDIA Build is about 0.5 s
  for each reply. Gemma 4 E2B on the processor is about 2.5 s (estimate).
- A live job that meets no target still gets the fastest option that works,
  so Martlet always has a reply.
- Martlet chooses **the smartest model that meets the target**. Smartness
  comes from the catalog (LMArena, the ranking index, then family, size and
  date).
- **A model that hears gets preference within one quality step.** With *Send my
  voice straight to Thinking*, the reply starts without waiting for the
  transcript, and the model hears the tone of voice.
- Bigger and smarter models go to **Deep thinking**, not to the live reply.

### When to use a model that hears (omni)

Use a model that hears for live Thinking when all of these are true:

1. The recording stays on the owner's computers (this PC or a host), or the
   owner allowed a cloud model to hear them.
2. It meets the 0.4 s target.
3. It is no more than one quality step below the best model that doesn't hear.

Otherwise, use the smartest model that doesn't hear with the Listening
transcript. Add a separate Hearing model on a host only when a host card has
room left after the needed jobs.

### When to use NVIDIA Build (free)

| Job | When | Why |
| --- | --- | --- |
| Deep thinking | Online is allowed and no host card fits a model at least one step smarter than live Thinking | Large free models; a few seconds don't matter for Deep thinking |
| Live Thinking | Online is "Yes" and no local card meets the target (the processor would take about 2.5 s) | About 0.5 s; transcript only, because NVIDIA logs inputs |
| Live Thinking backup | Online is "as a backup" or "Yes": in the While gaming and Host away plans | Keeps replies quick when the local card is busy or a host is away |
| Pictures | No graphics card can make pictures | One free key also covers NVIDIA's FLUX models |

A local model on the processor stays as the last backup, because the free
service has request limits and fails at times. The wizard asks for an NVIDIA key
only when the plan uses NVIDIA.

### How much of each graphics card

| Computer | Normal plan | While gaming plan |
| --- | --- | --- |
| Host (nobody sits at it) | Up to 90% of each card (today's 10% or 0.8 GB headroom), or less with the host card share preference | The same |
| Companion PC, no games | Only the live jobs (Thinking, the voice), and only when no host can do them | Not used |
| Companion PC that games | The same as above | No live jobs on its card while a game runs |

- With a host that answers, a companion PC runs no models at all, only the app,
  audio, voice detection and the avatar (today's rule 6, made stricter).
- While a game runs, the live turn moves to a host, then to NVIDIA (if
  allowed), then to the processor. The model stays loaded when the game leaves
  room, so the change back is quick. Martlet unloads it only when the card is
  short of memory.

### Does using less memory make a model faster?

Partly. Martlet uses these facts:

- **Words per second** depend mostly on memory speed. Each new word reads all
  the **active** weights once, so fewer bytes make it faster. A smaller
  quantization helps, and a mixture-of-experts model (for example 26B with 4B
  active) is fast but needs memory for all its weights.
- **The first word** depends on the computing power, the prompt length and the
  prompt cache. Martlet keeps the start of the prompt the same so the cache is
  used again.
- **The largest effect:** a model that doesn't fit on the card puts layers in
  main memory, and it becomes 5-20 times slower. So Martlet never plans a live
  model that doesn't fit completely.
- **Free memory doesn't make a model faster.** It keeps room for games and
  other jobs, and it stops the model from being unloaded and loaded again,
  which costs seconds.

Rules: Q4_K_M quantization by default; Q8 only for small models when the card
has much free room; never split a live model between the card and main memory.

### Room for other jobs

- The optional extras take only the room that the needed jobs leave (today's
  rule). Pictures and singing load when used and unload after a few idle
  minutes, so they don't keep memory.
- The PC the owner uses keeps a quarter of its main memory (at least 4 GB) for
  the owner's apps (today's rule).

## Owners who run their own models

- **Their models come first.** Models the owner's apps already serve are first
  choices (#665, #666). Martlet counts the memory they use (`/api/ps`), so it
  never fills the card past them.
- **Locked choices.** A choice the owner made by hand is locked. Recommended
  setup shows a suggestion next to it, but changes it only when the owner asks.
  Each job gets a lock in Recommended setup.
- **Their own servers.** A new server or host model is checked once (route
  facts) and then counts like any catalog model.
- **Full facts.** The option list shows each option's inputs, memory, first
  word, smartness, cost and source.

## Staying current

- The catalog refreshes once a day. Martlet then plans again in the
  background. When a new plan is clearly better (smarter at the same speed, or
  the same smartness with less memory), the Recommended setup button shows
  *A better setup is available*. Martlet never applies it by itself.
- A retired model is the exception: Martlet proposes its replacement at once.
- Measured numbers replace estimates: the first word for each host and model
  from the `Reply latency` log lines, and the memory from `/api/ps`.

## The first-run wizard

1. Martlet finds this PC's hardware, the other computers, the model apps and
   the keys.
2. Question 1 (games), with Martlet's guess selected.
3. Question 2 (online services).
4. Question 3 (reply quality).
5. One screen with the plan: one line for each job, where it runs and why, for
   example *Thinking: Gemma 4 E4B on your RTX 4070. It hears you, first word
   about 0.2 s.* One **Use this setup** button.
6. A key step only if the plan needs a key.

Recommended setup uses the same planner for the whole network. It shows *Your
preferences*, the three plans, the changes and the reasons, and has a lock on
each job.

## Build stages

Each stage is one pull request.

1. **Preferences** (done). The saved recommendation preferences; the wizard's
   three questions; *Your preferences* in Recommended setup and in the
   settings; the planner uses them (`KeepGpuForGames`, the online level, the
   first-word target, hearing, the host card share). It needs no catalog.
   How it was built:
   - `RecommendationPreferences` (`Martlet.Core.Planning`):
     `recommendation-preferences.json` and the `recommendation-preferences`
     shared setting. Fields: `Quality` (`ReplyQuality` Balanced, Quick,
     Smarter), `Online` (`OnlineServices` Backup, Never, Yes), `PreferHearing`,
     `HostGpuShare` (90, 75, 50), `UseServedModels`, `PreferHostModels` and
     `Games` (`GamesAnswer(Device, Plays)`, one per companion PC by its Martlet
     device ID). An owner who never chose gets the defaults; the two model
     choices come from `recommended-setup.json` until then.
   - The guess from the saved keys is gone. `NetworkSetupRequest.With` and
     `RecommendedSetupInputs` give the planners `Preference`
     (`RecommendationPreferences.Hosting`: Never is `PreferLocal`, Only as a
     backup is the new `HostingPreference.Backup`, Yes is `Balanced`),
     `Quality`, `PreferHearing`, `HostGpuShare` and each companion PC's
     `MachineSpecs.KeepGpuForGames` (its answer, else this PC's game library
     guess, `GameLibraries.Found`).
   - Live Thinking: `LiveThinking.Order` puts the models that meet the target
     first, smartest first (a model that hears counts one step up), then the
     rest fastest first. A new Thinking model is the first of those that also
     leaves room for the voice; else the fastest that fits. Today's model
     stays (rule 7).
   - Online: `HostingRules.Allows`. Only as a backup plans no hosted live job
     but allows hosted Deep thinking and backup Thinking; a hosted Thinking
     provider the owner chose stays.
   - Games: the normal plan doesn't empty the card of a PC that games
     (`PlacementEngine.GpuCapacityGb` gives 0 only with `keepForGames: true`,
     for the While gaming plan). It takes only Thinking and the voice, and it
     is the last companion PC to lend its card.
   - Host card share: `GpuCapacityGb`'s `share`, for host PCs only.
   - Rule 6, stricter: with a host that answers, a companion PC's Deep
     thinking, singing and pictures go, and its own Thinking moves to a host
     with the same model or one as fast.
2. **Situations.** The While gaming and Host away plans, changed at run time
   from `PcActivity` and host status through the Thinking pool and backup
   routes. It follows stage 1. **Done:** `LiveSituations` decides the
   situation and where live Thinking goes (`GameWatch` finds the game,
   `PresenceWatch` the host that is away), `SituationRoutes` gives the
   conversation the moved route from its next reply, and `SituationPlans`
   gives Recommended setup's *Three plans*
   ([Three plans](RECOMMENDED_SETUPS.md#three-plans-normal-while-gaming-and-host-away)).
   The hosted backup is *If Thinking fails*. When no other place can answer,
   the PC's own model in Ollama answers; Ollama keeps one copy of a model, so
   that copy runs on the processor only when Martlet unloaded it because the
   card was short of memory.
3. **Planner on the catalog.** The planner gets its options from the catalog
   and the local facts; today's `FootprintCatalog.Seed` stays as the offline
   fallback. The smartness score, Deep thinking on NVIDIA, measured feedback,
   locks and *A better setup is available*. It follows stage 1 and the catalog
   work. **Part a, the options from the catalog, is done** (part b, measured
   feedback, locks, *A better setup is available* and a replacement for a
   retired model, is the next PR). How it was built:
   - `FootprintCatalog.FromModels(ModelCatalog, bandwidth)` gives the planners
     Martlet's own options with the catalog's facts, plus new options from the
     catalog. `PlanningCatalog` holds them for the desktop: it loads the
     catalog (the daily copy, else the snapshot) and `model-abilities.json` on
     a thread-pool thread at start, before Recommended setup and *Set it all
     up for me* plan, and after a daily refresh. Nothing on the reply path
     loads it. Each option says where it came from (`ComponentOption.Origin`:
     `Seed`, `Catalog`, `LocalFacts` or `Served`).
   - **The gates.** An open-weight model with local facts becomes a Thinking,
     Deep thinking, Vision (it sees) and Hearing (it hears) option when it has
     an install name (an Ollama tag or `hf.co/{repo}:{quant}`) and a memory
     estimate, writes text, is at most 80 GB, and its weights' size agrees with
     its parameters (a broken record, such as a split file's part, never wins a
     card). Unknown is not Yes. The planners still check the card, the headroom
     and the owner's online answer. Martlet's host roles offer a fixed list of
     models (`role.conf`), so a model outside it is `NativeOnly`: only a
     companion PC's own Ollama runs it.
   - **Smartness.** `catalog.Smartness` gives the tier of new options, served
     models and models a host keeps (`ServedModels.Tier` is the fallback).
     Martlet's own options keep the tier Martlet chose, and show the catalog's
     words only when scores back them. The rank is never shown.
   - **Speed.** Where nothing is measured, the first word is 100 ms plus nine
     tokens at 60% of the memory bandwidth's bound (fitted to the measured
     Gemma 4 E2B and E4B; it gives 153 ms and 207 ms), for this PC's best card
     (else an RTX 4070's 504 GB/s). Measured numbers in the seed win; the
     seed's estimates (Gemma 4 12B and 26B) are estimated again.
   - **Deep thinking on NVIDIA Build.** `hosted:nvidia-build-deep` uses the
     smartest free chat model that calls tools on NVIDIA's routes (Kimi K3 in
     the snapshot of 2026-10-10). When online services allow it and no host card fits a model
     one quality step smarter than live Thinking, Recommended setup suggests it
     (`NetworkRecommendation.OnlineDeepThinking`, a note and the Deep thinking
     line); the owner adds it in Companion › Thinking pool, which prefills it.
     A suggestion changes no setup and no fingerprint. The single-PC planner
     (`PlacementEngine`, which the setup advisor uses) places it and suggests
     the free key.
   - **Provider defaults.** Each hosted provider suggests its free route that
     sees, calls tools and isn't retired, the fewest active parameters first
     (`CatalogOptions.SuggestedChat`; route facts Martlet found out come
     first). Today's `ChatCompletionsEndpointCatalog` defaults stay when the
     catalog has none (Google Gemini in the snapshot).
   - **Facts and MCP.** The option picker and Recommended setup show the
     catalog's facts (inputs, smartness words or LMArena's credited rating,
     words a second, where it comes from); Companion › Thinking › This PC also
     lists up to three catalog models that fit the card. MCP
     `recommended_setup_status` has `planning`, and the Doctor has
     `planning.catalog`.

## Decisions made for the owner

The owner approved these defaults on 2026-10-10. Each one is a preference the
owner can change, except the last two.

1. The live target is a first word in 0.4 s or less (Balanced).
2. A model that hears wins when it is no more than one quality step behind.
3. "Only as a backup" is the default answer for online services.
4. While gaming, the live turn moves away, but the model stays loaded when the
   game leaves room.
5. NVIDIA Build is the default place for Deep thinking when no host card fits a
   smarter model and online services are allowed.
