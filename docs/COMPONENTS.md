# Components, jobs and placement (design)

This note redesigns how a user chooses what Martlet uses and where each piece
runs. It replaces the old "How Martlet thinks" Setup page, which used one
*Configure role* dropdown (STT, LLM, TTS) above the same set of fields for
every role. That page mixed three unrelated questions:

1. **What does each job use?** (provider, model, voice)
2. **Where does the job run?** (cloud, this PC, or a paired host)
3. **Which hardware plays and records sound?** (microphone and speakers)

Phase 1 (below) is shipped. Phase 2 shipped as the Home setup pages (*How
Martlet thinks*, *Its voice*, *How it listens*, *Character*; see
[UI_DESIGN.md](UI_DESIGN.md#2-companion-home-main-pc)), which combine
placement (This PC by default, another computer, or cloud) with provider,
model and key. The later phases are the agreed direction.

## Vocabulary

| Term | Meaning | Examples |
| --- | --- | --- |
| **Job** | One thing Martlet needs done in a conversation. | Thinking, Listening, Speaking, Lip-sync, later Seeing (vision) |
| **Engine** | A provider + model (+ voice) that can do a job. | OpenAI `gpt-4.1-mini`, OpenRouter `meta-llama/llama-3.3-70b-instruct`, Ollama `llama3.2:3b`, F5 voice, Whisper |
| **Place** | Where the engine runs. | Cloud API, This PC, a paired Martlet host |
| **Audio devices** | Local hardware only: which microphone and speakers this PC uses. | Headset mic, desktop speakers |
| **Credential** | A key bound to one provider origin, reused by every job that uses that provider. | One OpenAI key for Thinking, Listening and Speaking |

Internal names stay as they are (`SetupRole.Llm/Stt/Tts`, route types,
aliases); only the user-facing words change.

## Target layout

```text
Home: Your setup
  Thinking   ->  [Cloud: OpenRouter llama-3.3-70b]   (Change)
  Listening  ->  [Cloud: OpenAI gpt-4o-mini-transcribe] (Change)
  Speaking   ->  [gpu-pc: F5 voice "Ava"]            (Change)
  Lip-sync   ->  [gpu-pc: Audio2Face]                 (Change)
  Microphone and speakers -> Tested on this PC        (Test)
```

Each job row opens one **job editor** with two questions, in this order:

1. **Where should it run?** Cloud API / This PC / a paired host (listed by
   name, with readiness). This is the same choice the Devices map's
   *Who does what* makes today; both surfaces edit one source of truth.
2. **Which engine?** Depends on the place:
   - *Cloud API*: provider presets (OpenAI, OpenRouter, NVIDIA Build, Custom
     OpenAI-compatible) with the recommended model prefilled, the key status
     for that provider, and the data/cost disclosure + consent.
   - *This PC* or *a host*: the engines that place can run (installed roles),
     with a recommended model list and an install/download action where needed.

Audio devices never appear inside a job editor; they stay on *Microphone and
speakers*. A job editor only links there ("Listening needs a microphone").

Credentials move from a separate step into the provider section of the job
editor (status + *Add key* / *Replace key*), keyed by provider origin, so one
OpenAI key serves all OpenAI jobs. The Credentials tab remains for listing and
removing detached keys.

## Defaults

- Every named provider has a recommended default model, prefilled when chosen
  (and the TTS default voice). Switching provider swaps a prefilled default but
  keeps a model the user typed.
- Prefill is a suggestion only: nothing is saved or sent until the user
  applies and consents; no network probe checks the model.
- Custom endpoints have no default (Martlet cannot know what the server serves).

Current defaults (phase 1): OpenAI Thinking `gpt-4.1-mini-2025-04-14`, OpenRouter
`meta-llama/llama-3.3-70b-instruct`, NVIDIA Build `meta/llama-3.3-70b-instruct`,
Listening `gpt-4o-mini-transcribe`, Speaking `gpt-4o-mini-tts-2025-12-15` with
voice `alloy`. They live next to their catalogs
(`ChatCompletionsEndpointCatalog`, `OpenAi*Catalog.DefaultModelId`).

## Phases

1. **De-overload Setup (shipped).** The Destinations tab became **Jobs**: a
   *Job to set up* picker (Thinking, Listening, Speaking) that shows only the
   fields that job uses (provider/base URL for Thinking, voice for Speaking,
   model list only when there is a real choice), job-specific explanations,
   recommended defaults prefilled, and home steps opening Setup on the matching
   job. "Fixture" is labeled *Demo only*.
2. **Job editor with placement.** One editor per job combining Setup's route
   fields with *Who does what* placement; home rows per job; remove the
   standalone Jobs picker. Keys shown inline per provider.
3. **Local engines on This PC.** See the queued local model hosting item below.
4. **Seeing (vision)** as a fifth job using the same editor and
   `VisionModelCatalog`.

## Queued to figure out

### Demo mode instead of "Fixture"

"Fixture" is a test term. Phase 1 relabels it *Demo only: scripted replies, no
AI model*. Open questions: move Demo out of the Setup path entirely (a *Try a
demo* button on Home/Settings, which already exists as "Try fixture"), and make
*Use AI models* implicit once any job is configured, so Setup no longer starts
with a demo-vs-real choice.

### Host a thinking model locally (This PC) with recommended models

Goal: choosing *This PC* for Thinking offers a short recommended list and the
app installs and downloads what is needed, then wires the route.

Existing pieces to reuse:

- Host role `deploy\host\roles\ollama` (Docker compose binding
  `127.0.0.1:11434`, GPU variant, `post_start` runs `ollama pull {OLLAMA_MODEL}`).
- *Hosts* can already install roles on "this PC via Docker Desktop" or over
  SSH, and the Devices map's *Who does what* offers role installation.
- `VisionModelCatalog`'s `LocalVisionModel(id, size, description)` pattern for
  a recommended list with download size.
- The Thinking route can already target a loopback Chat Completions server
  (`http://127.0.0.1:11434/v1`) or a host via `GatewayOllama`.

Proposed flow:

1. Thinking -> *This PC* shows a `LocalChatModelCatalog`: a few instruct models
   sized by detected VRAM/RAM (for example a ~2 GB 3B model for any PC, a ~5 GB
   7-8B model for 8 GB GPUs, a larger one for 16 GB+), each with download size
   and a short description. Exact tags to be picked when implemented.
2. If no local runtime is present: offer Ollama (native installer or the
   existing Docker role), with explicit consent for the download and install.
3. Download the chosen model with progress and cancel (`ollama pull`), with
   explicit consent showing size.
4. Save the route to the loopback endpoint (no key, no cloud consent needed;
   still disclosed as "stays on this PC").

Open questions: native Ollama vs Docker Desktop as the default runtime; whether
to bundle a runtime or always download it; uninstall/cleanup of models; the
same flow for Listening (local Whisper) and Speaking (local F5) on This PC.

### Other open questions

- Whether Lip-sync belongs with the other jobs in Setup or only on the Devices
  map (it has no cloud option today).
- One key per provider vs per job when users want separate billing.
