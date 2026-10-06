# Hosted Thinking: cloud models when Thinking can't run on this PC

When a computer is too weak to run Thinking (Martlet's local default is Gemma 4
E2B in Ollama), Martlet can send Thinking to a hosted model instead. This page
says which hosted models to use, which of them can **hear** (take the user's
recording, so Martlet doesn't have to transcribe first), what the free tiers
allow, how reliable they are, and exactly how to get each key. The welcome
wizard uses it for its *get a free key* guidance and the placement engine
(`src/Martlet.Core/Planning`, its `FootprintCatalog` hosted options) for its
external options.

Checked on **2026-10-06**. Providers change models and quotas often; recheck
before relying on a number. Each fact is marked:

- **Verified**: read from the provider's own documentation, API schema or
  public API, or measured by Martlet.
- **Observed**: public live data (for example OpenRouter's uptime figures) on
  the date above.
- **Community**: third-party write-ups only.
- **NOT RUN**: needs a real API key; nobody has sent such a request from Martlet
  yet. The exact command is under [Checks the owner can run](#checks-the-owner-can-run).

## Short answers

- **Is there an NVIDIA Build endpoint for Gemma 4 E2B?** No (Verified).
  NVIDIA Build's model list (`GET https://integrate.api.nvidia.com/v1/models`)
  has no Gemma 4 E2B or E4B, and `build.nvidia.com/google/gemma-4-e2b-it` is
  not a model page. The audio-hearing Gemma models it had, `google/gemma-3n-e2b-it`
  and `google/gemma-3n-e4b-it`, were switched off on **2026-07-27**, and
  `microsoft/phi-4-multimodal-instruct` on 2026-07-15 (both pages say so). No
  other host checked offers a hearing Gemma either: Google's Gemini API hosts only
  `gemma-4-31b-it` and `gemma-4-26b-a4b-it`, and OpenRouter, Hugging Face's
  router and DeepInfra list only the 31B and 26B A4B Gemma 4 models. Those
  large Gemma 4 models see but don't hear (Verified: their listed input
  modalities are text and image, plus video on OpenRouter).
- **Do the free endpoints support audio input?** Some do (Verified from their
  API schemas and docs):
  - NVIDIA Build's **`nvidia/nemotron-3-nano-omni-30b-a3b-reasoning`** (a
    "Free Endpoint") takes `input_audio` `{data, format: wav|mp3}` in its
    published OpenAPI schema for `https://integrate.api.nvidia.com/v1/chat/completions`.
    This is exactly what Martlet sends.
  - Google's **Gemini API** free tier: Gemini Flash and Flash-Lite models take
    `input_audio` (`format: "wav"`) on its OpenAI-compatible endpoint, per
    Google's own example.
  - **OpenRouter** takes `input_audio` for any model listed with audio input;
    its free audio models are `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free`,
    `thinkingmachines/inkling:free` and `thinkingmachines/inkling-small:free`.
  - **Groq** and **Cloudflare Workers AI**: no chat model that hears; they only
    offer separate speech-to-text (Community).
- **Does it work reliably?** Not NVIDIA's free omni endpoint (Observed).
  OpenRouter's free Nemotron 3 Nano Omni, which NVIDIA itself serves, was marked
  degraded with **72.6%** uptime over the last day and 84.6% over the last 30
  minutes. Google's Gemini Flash-Lite endpoints showed 99.5-100% on the same
  dashboard (their paid route; Google publishes no free-tier uptime), and
  OpenRouter's free Inkling Small 100%. Nobody has measured Nemotron Omni's
  reply latency from Martlet yet (NOT RUN).
- **And the privacy terms?** Both big free tiers ask you not to send personal
  data. NVIDIA's API trial: *"Please do not upload any confidential information
  or personal data (such as voices or faces of people)"*, and inputs and outputs
  *"will be recorded ... to improve NVIDIA products and services, including AI
  models"*. Google's unpaid Gemini API: *"human reviewers may read, annotate,
  and process your API input and output ... Do not submit sensitive,
  confidential, or personal information to the Unpaid Services."* A recording
  of your voice is personal data, so on a free tier Martlet sends text, not your
  voice, unless you tick **Let Thinking hear my voice** yourself (it is already
  off for every cloud model until ticked).

## Recommendation table

| Provider | Model ID | Hears your voice? | Sees? | Free quota | Reliability | Key |
|---|---|---|---|---|---|---|
| NVIDIA Build | `google/diffusiongemma-26b-a4b-it` (Martlet's NVIDIA Build default) | No: gets the transcript | Yes | Up to 40 requests a minute, 10,000 a day (Verified, build.nvidia.com page text; *"may vary by model and traffic"*) | Good: about 0.5 s per reply, reads screenshots, calls tools (measured by Martlet 2026-10-01) | [NVIDIA Build](#nvidia-build-key) (`nvapi-...`) |
| NVIDIA Build | `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning` | **Yes** (`input_audio` wav/mp3, up to 1 hour, Verified schema; NOT RUN from Martlet) | Yes | Same as above | Poor: 72.6% day uptime for the same NVIDIA-served model on OpenRouter (Observed); latency NOT RUN | Same `nvapi-` key |
| Google Gemini API (AI Studio) | `gemini-3.5-flash-lite` | **Yes** (`input_audio`, Verified doc example; NOT RUN from Martlet) | Yes | Free of charge on the free tier; per-model limits are shown only in AI Studio, not published (Verified); community figures vary | Good: Google's endpoints 99.5-100% (Observed on OpenRouter, paid route) | [Google AI Studio](#google-gemini-key) |
| Google Gemini API | `gemini-2.5-flash-lite` | **Yes** | Yes | As above | As above | Same key |
| OpenRouter | `thinkingmachines/inkling-small:free` | **Yes** (listed audio input; NOT RUN) | Yes | 20 a minute, **50 a day** until you have bought $10 of credits, then 1,000 a day (Verified) | Good: 100% day uptime (Observed) | [OpenRouter](#openrouter-key) |
| OpenRouter | `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free` | Yes | Yes | As above | Poor (served by NVIDIA, 72.6%) | Same key |
| OpenRouter (paid) | `google/gemma-4-26b-a4b-it` (Martlet's OpenRouter default) or any `google/gemini-*-flash-lite` | Gemma: no; Gemini: yes | Yes | Paid per token | Good | Same key, with credit |
| Groq, Cloudflare Workers AI | any chat model | No | Varies | Varies | n/a | Not recommended for Thinking that hears |

Fifty requests a day is too few for a companion (every reply, glance and memory
is a request), so OpenRouter's `:free` models are only useful after the $10
credit purchase. Martlet's audio is at most 90 seconds of WAV (under 9 MB), well
inside every limit above (Gemini's inline limit is 20 MB a request).

## What Martlet should recommend

**Default when Thinking can't run on this PC:** NVIDIA Build with
`google/diffusiongemma-26b-a4b-it`, and Listening (Parakeet speech-to-text) on
this PC's CPU so Thinking gets the transcript. One free `nvapi-` key also
covers Pictures (NVIDIA's FLUX models). It is Martlet's fastest measured free
hosted model, sees screenshots and calls tools, and no recording leaves the PC.

**When the owner wants Thinking to hear** (no transcription before the reply,
and tone of voice): Google Gemini `gemini-3.5-flash-lite` on the free tier (the
**Google Gemini** provider, see below), with the owner ticking **Let Thinking
hear my voice** after reading Google's free-tier terms. It is the only free
hearing option on a reliable endpoint. NVIDIA's Nemotron 3 Nano Omni is the
free hearing option on the same NVIDIA key, but it is unreliable today and
NVIDIA's trial terms ask you not to upload voices; offer it, don't default to it.

**Fallback order** for the placement engine when Thinking can't run locally:

1. A computer on the owner's Martlet network that runs Thinking (local, no
   quota, the voice stays in the network).
2. NVIDIA Build `google/diffusiongemma-26b-a4b-it` with the transcript (free).
3. Google Gemini `gemini-3.5-flash-lite` (free; hears when the owner allows it).
   Put it in Companion › Thinking › **If Thinking fails** behind NVIDIA Build:
   a different company, so one outage doesn't take both down.
4. NVIDIA Build `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning` (free; hears;
   flaky). Don't make it the fallback for another NVIDIA model: both depend on
   NVIDIA's free service.
5. OpenRouter, paid (`google/gemma-4-26b-a4b-it`, or a Gemini Flash-Lite model
   to hear), or OpenRouter `:free` models once the owner has bought $10 of
   credits.
6. OpenAI (paid; Martlet's OpenAI route never takes audio).

Countries: Google lets only its paid service be used for apps offered to users
in the EEA, Switzerland or the UK, and NVIDIA's free API isn't offered in some
countries. When the wizard can't tell, it should still show NVIDIA Build first.

## When the hosted model can't hear

Martlet already handles this; nothing extra is needed:

- A model that doesn't hear (by its listing, a test or its name; see
  [Thinking models that hear and see](CONVERSATION.md#thinking-models-that-hear-and-see))
  gets the transcript only. Speech-to-text runs on this PC: Parakeet on the CPU
  works on weak PCs, or a paired host's.
- A model that refuses a recording gets the request again with the transcript,
  and Martlet remembers that it can't hear.
- A cloud model never gets the recording until the owner ticks **Let Thinking
  hear my voice**; until then the transcript goes.

What Martlet knows by name (the hosted APIs here don't publish what a model
takes, except OpenRouter, whose listing Martlet reads): every Gemini from 1.5
hears and sees; Nemotron 3 Nano Omni hears and sees (added 2026-10-06); the
large Gemma 4 models and `diffusiongemma` see but don't hear; NVIDIA's Gemma 3n
and Phi-4 multimodal models are retired, so Martlet tells the owner to choose
another model. **Test hearing** (Companion › Listening) settles it for any model
with one small request.

Known gap (NOT RUN): NVIDIA's playground lets a message carry audio *or*
pictures, not both. If the API also refuses a recording together with a
screenshot, Martlet would retry with the transcript and then treat the model as
not hearing; **Test hearing** (audio only) sets it right.

## Getting a key

### NVIDIA Build key

Free, no card. Inputs are logged to improve NVIDIA's products; don't send
personal data.

1. Open [build.nvidia.com](https://build.nvidia.com) and sign in, or create a
   free NVIDIA account with your email; confirm the email NVIDIA sends.
2. Open the model you will use, for example
   [build.nvidia.com/google/diffusiongemma-26b-a4b-it](https://build.nvidia.com/google/diffusiongemma-26b-a4b-it),
   and choose **Generate API Key** (or go to
   [build.nvidia.com/settings/api-keys](https://build.nvidia.com/settings/api-keys)).
   Accept NVIDIA's trial terms if asked; some accounts are asked to verify a
   phone number.
3. Copy the key now: it starts with `nvapi-` and isn't shown again.
4. In Martlet: Setup (or Companion) › Thinking › **A cloud provider** ›
   **NVIDIA Build**. The model box is filled with the recommended model; to
   hear, type `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning` instead. Paste the
   key (Martlet keeps it in Windows Credential Manager), tick the consent and
   save.

### Google Gemini key

Free tier, no card. Free-tier content may be read by human reviewers and used
to improve Google's products; don't send personal data unless you accept that.

1. Open [aistudio.google.com/apikey](https://aistudio.google.com/apikey) and
   sign in with a Google account (18 or older).
2. Accept the Gemini API terms. A new user gets a default Google Cloud project
   and a key automatically; otherwise choose **Create API key** and pick (or
   import) a project.
3. Copy the key. Keys made since 2026-05-28 are *authorization keys* tied to
   the project; they work the same way here.
4. In Martlet: Setup (or Companion) › Thinking › **A cloud provider** ›
   **Google Gemini**. The model box is filled with `gemini-3.5-flash-lite` and
   the base URL is `https://generativelanguage.googleapis.com/v1beta/openai`;
   the hint under it repeats these key steps. Paste the key, tick the consent
   and save. For hearing, also tick Companion › Listening › **Let Thinking hear
   my voice** (it stays off until you do). To use Gemini as the cross-company
   fallback instead, choose **Google Gemini** under Companion › Thinking › **If
   Thinking fails**.
5. To see the free limits for each model, open
   [aistudio.google.com/rate-limit](https://aistudio.google.com/rate-limit);
   Google's service status is at [aistudio.google.com/status](https://aistudio.google.com/status).

### OpenRouter key

1. Open [openrouter.ai](https://openrouter.ai) and sign in.
2. Open [openrouter.ai/settings/keys](https://openrouter.ai/settings/keys),
   choose **Create key**, and copy it (`sk-or-...`).
3. Free models need no credit but allow only 50 requests a day; buying $10 of
   credits once raises that to 1,000 a day. In
   [privacy settings](https://openrouter.ai/settings/privacy) allow free
   endpoints that may train on your data if a `:free` model is refused.
4. In Martlet: Thinking › **A cloud provider** › **OpenRouter**, the model ID,
   the key, consent, save. Martlet reads OpenRouter's listing to know whether
   the model hears and sees.

## Checks the owner can run

These need a real key, so Martlet hasn't run them (NOT RUN). Each sends one
short request. `hello.wav` is any short WAV of speech; on Windows PowerShell,
`$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes('hello.wav'))`.

NVIDIA Build, Nemotron 3 Nano Omni hearing, thinking off (what Martlet sends):

```bash
B64=$(base64 -w0 hello.wav)
curl -sS https://integrate.api.nvidia.com/v1/chat/completions \
  -H "Authorization: Bearer $NVIDIA_API_KEY" -H "Content-Type: application/json" \
  -d '{"model":"nvidia/nemotron-3-nano-omni-30b-a3b-reasoning","max_tokens":200,
       "chat_template_kwargs":{"enable_thinking":false},
       "messages":[{"role":"user","content":[
         {"type":"text","text":"What did I say?"},
         {"type":"input_audio","input_audio":{"data":"'"$B64"'","format":"wav"}}]}]}'
```

Google Gemini hearing:

```bash
curl -sS https://generativelanguage.googleapis.com/v1beta/openai/chat/completions \
  -H "Authorization: Bearer $GEMINI_API_KEY" -H "Content-Type: application/json" \
  -d '{"model":"gemini-3.5-flash-lite","reasoning_effort":"none",
       "messages":[{"role":"user","content":[
         {"type":"text","text":"What did I say?"},
         {"type":"input_audio","input_audio":{"data":"'"$B64"'","format":"wav"}}]}]}'
```

Easier: set the route up in Martlet and use Companion › Listening › **Test
hearing**, then talk and compare the desktop log's `Reply latency` lines.

## For the placement engine

External Thinking options, in fallback order, with what the engine needs. The
preset ids are `ChatCompletionsEndpointCatalog.NamedEndpoints[].Id`
(`ChatCompletionsEndpointCatalog.ById`); `HearingOptIn` marks a preset whose
default hears but sends the recording only after the owner allows it.

| Option | Preset id | Base URL | Model ID | Hears | Sees | Tools | Free | Same key covers |
|---|---|---|---|---|---|---|---|---|
| NVIDIA Build default | `nvidia-build` | `https://integrate.api.nvidia.com/v1` | `google/diffusiongemma-26b-a4b-it` | no | yes | yes | yes (40/min) | Pictures (FLUX) |
| Google Gemini | `google-gemini` | `https://generativelanguage.googleapis.com/v1beta/openai` | `gemini-3.5-flash-lite` | yes (opt-in) | yes | yes | yes (limits in AI Studio) | - |
| NVIDIA Build omni | `nvidia-build` | `https://integrate.api.nvidia.com/v1` | `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning` | yes (opt-in) | yes | listed | yes (40/min) | Pictures (FLUX) |
| OpenRouter | `openrouter` | `https://openrouter.ai/api/v1` | `google/gemma-4-26b-a4b-it` | no | yes | yes | no (`:free` 50/day) | Pictures (paid) |

A Thinking option that doesn't hear needs Listening (speech-to-text) placed on
this PC or the network; one that hears still needs it whenever the owner hasn't
allowed the recording to leave the PC.

## Sources

- NVIDIA Build model list: `GET https://integrate.api.nvidia.com/v1/models` (no key needed).
- Nemotron 3 Nano Omni: [build.nvidia.com page](https://build.nvidia.com/nvidia/nemotron-3-nano-omni-30b-a3b-reasoning)
  (embedded OpenAPI schema with `InputAudio` `{data, format: wav|mp3}`, rate
  limits, privacy text), [API reference and model card](https://docs.api.nvidia.com/nim/reference/nvidia-nemotron-3-nano-omni-30b-a3b-reasoning),
  [NIM API examples](https://docs.nvidia.com/nim/vision-language-models/1.7.0/examples/nemotron-3-nano-omni-30b-a3b-reasoning/api.html).
- Deprecations: [gemma-3n-e4b-it](https://build.nvidia.com/google/gemma-3n-e4b-it),
  [gemma-3n-e2b-it](https://build.nvidia.com/google/gemma-3n-e2b-it),
  [phi-4-multimodal-instruct](https://build.nvidia.com/microsoft/phi-4-multimodal-instruct).
- Google: [OpenAI compatibility](https://ai.google.dev/gemini-api/docs/openai) (audio example),
  [pricing](https://ai.google.dev/gemini-api/docs/pricing) (free tier, *used to improve our products*),
  [rate limits](https://ai.google.dev/gemini-api/docs/rate-limits),
  [Gemma on the Gemini API](https://ai.google.dev/gemma/docs/core/gemma_on_gemini_api),
  [API keys](https://ai.google.dev/gemini-api/docs/api-key),
  [terms](https://ai.google.dev/gemini-api/terms).
- OpenRouter: [audio input](https://openrouter.ai/docs/guides/overview/multimodal/audio),
  [limits](https://openrouter.ai/docs/api/reference/limits),
  `GET https://openrouter.ai/api/v1/models` and
  `GET https://openrouter.ai/api/v1/models/<id>/endpoints` (uptime, no key needed).
- Hugging Face router `GET https://router.huggingface.co/v1/models` and DeepInfra
  `GET https://api.deepinfra.com/v1/openai/models` (no hearing Gemma listed).
