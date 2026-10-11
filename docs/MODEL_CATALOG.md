# Model catalog: sources, conflicts and design

This page is research for a model catalog that keeps itself up to date
([#663](https://github.com/throndir2/Martlet/issues/663)). Today Martlet names
models in code: the provider defaults and NVIDIA's retired models
(`ChatCompletionsEndpointCatalog`), the local models and their sizes
(`FootprintCatalog.Seed`), the name guesses (`VisionModelCatalog`,
`HearingModelCatalog`) and the host role choices (`deploy/host/roles/*/role.conf`).
The goal is a catalog that knows, for each model:

- which inputs it takes: text, image, audio and video, each one apart;
- whether it can run locally, and how much memory it needs;
- how smart and how fast it is;
- anything else the sources give (context, tool calls, reasoning, license, dates).

Checked on **2026-10-10**. Each fact is marked:

- **Verified**: Martlet's developer fetched it from the source on that date.
- **From docs**: read in the source's documentation or terms, not fetched as data.
- **Proposed**: a design suggestion, not built yet.
- **Built**: Martlet does it now.

## The catalog Martlet has now (Built)

Martlet keeps an internal model catalog in `Martlet.Core.Planning`
(`src\Martlet.Core\Planning\Catalog\`). Nothing chooses models from it yet:
Recommended setup, the welcome wizard and the provider defaults still use their
own lists, and a later recommendation design will use the catalog.

- **Where it comes from.** A snapshot ships inside Martlet
  (`model-catalog.json`, embedded in `Martlet.Core`), so the catalog works offline
  and on first start. A daily copy goes in the PC folder: `%ProgramData%\Martlet`
  for the default data folder, shared by every Windows user and account, because
  the data is public. A disposable data folder keeps its own. Martlet uses the
  newer of the two.
- **The file.** One block for each source, with the date Martlet read it and
  what it said about each model (`ModelCatalogData`). The catalog is worked out
  from the blocks when it loads (`ModelCatalog.Build`), so a source that fails
  keeps its last good block.
- **The daily refresh.** The desktop checks three minutes after start and then
  every 15 minutes whether a day has passed since the last refresh on this PC.
  If so, `ModelCatalogRefresh` (`Martlet.Providers.ModelCatalogs`) reads every
  source in the background, with no key. Each request has a time limit and a
  size limit. It never starts while Martlet replies or hears you, and a reply
  that starts stops its request until the reply ends, so it adds no conversation
  latency. The status is in `model-catalog-status.json` next to the daily copy.
- **Looking a model up.** `ModelCatalogStore.For(dataDirectory).Load()` gives the
  catalog (keep it off the reply path: it takes a moment the first time).
  `ModelCatalog.Find(name)` takes any name: a Hugging Face repository, an
  OpenRouter, NVIDIA Build, models.dev or provider model ID (`:free` too), an
  Ollama tag (`gemma4:26b`, by family and size when Martlet's own list doesn't
  name it), `hf.co/{repo}:{quant}` or an LMArena name. `ModelCatalog.Route`
  finds a route by provider ID or base URL, and `ModelCatalog.Smartness` says how
  smart a model is.
- **Local models.** The refresh also looks up what running open-weight models
  locally takes (`LocalModelFactsReader`, [#760](https://github.com/throndir2/Martlet/pull/760)):
  16 models a day, Martlet's own local models first, then those never looked up,
  then the oldest. `CatalogModel.Local` keeps the facts,
  `CatalogModel.LocallyHostable` says whether the model can run on the owner's
  computers, and `CatalogModel.Memory()` estimates its memory at 8,192 tokens.
- **Checking it.** The MCP tools `model_catalog_status`, `model_catalog_lookup`
  and `model_catalog_refresh` ([MCP](MCP.md)) show the catalog, the last refresh
  and a fixture rehearsal of the refresh.
- **The snapshot before a release.** Run `.\scripts\Update-ModelCatalog.ps1`. It
  builds the MCP server, reads every source with the production refresh (about
  five minutes: NVIDIA's pages are slow) and writes the snapshot only when every
  source was read. Commit the changed `model-catalog.json` with the release PR.

The snapshot of 2026-10-10 has 624 models and 730 routes: 458 OpenRouter, 46
NVIDIA Build and 226 more from models.dev's rows for other providers. 279 models
have open weights, 350 see, 62 hear, 118 take video and 463 call tools.

## Short answers

- **Is there one catalog that already has everything?** No (Verified). Each
  source covers part of it. models.dev comes nearest for inputs and open
  weights, but it has no parameter counts or memory sizes. Hugging Face has
  sizes but no hosted providers. Martlet must join a few sources.
- **Is there a list of all models that can run locally?** No single list
  (Verified). Hugging Face holds every open-weight model. models.dev marks
  `open_weights` on about 1,250 distinct models. OpenRouter gives a
  `hugging_face_id` for 170 of its 458 models. Ollama's library has no bulk
  list that its terms allow Martlet to read.
- **Is every NVIDIA Build model free?** Yes. NVIDIA's
  [llms.txt](https://build.nvidia.com/llms.txt) says: *"All models offer a free
  trial tier with no credit card required"* (Verified).
- **Can Martlet list NVIDIA's current models without a key?** Yes, but not from
  its API. The keyless `GET https://integrate.api.nvidia.com/v1/models` is old.
  It lists 80 models, but only 18 of the 46 current chat endpoints. It also
  still lists retired models such as `meta/llama2-70b` (Verified).
  NVIDIA's [models.md](https://build.nvidia.com/models.md) and its pages for
  each model are current. They are made for machines to read (see
  [NVIDIA Build](#nvidia-build)).
- **Do the sources say video apart from images?** Yes (Verified). OpenRouter
  lists video input for 84 models, models.dev on 1,424 provider rows, and
  vLLM's table on 39 rows.
- **Is there a free source of how smart a model is?** Yes, with a limit. OpenRouter's
  public model list carries Artificial Analysis scores for 284 models (Verified).
  Artificial Analysis's terms limit showing raw scores in a product (From docs).
  The LMArena ratings are CC-BY-4.0, so Martlet can show them with credit.

## Sources

| Source | Key | Terms | What it gives | Use it for |
| --- | --- | --- | --- | --- |
| OpenRouter `GET https://openrouter.ai/api/v1/models` | No | Public, cacheable (`max-age=120`) | 458 models: inputs (text, image, audio, video, file), context, prices, `supported_parameters` (tools, reasoning), `hugging_face_id` (170), `expiration_date` (18), `knowledge_cutoff` (172), `benchmarks` (298) | OpenRouter routes; Hugging Face links; scores |
| models.dev `https://models.dev/api.json` and `/models.json` | No | MIT | 226 providers, 8,443 provider-model rows, 475 model records: inputs (text, image, audio, video, pdf), `open_weights`, `tool_call`, `reasoning`, `limit.context`, `release_date`; `license` (80) and `weights` links (195) | First guess for any provider; open weights |
| NVIDIA [models.md](https://build.nvidia.com/models.md) and its model pages | No | Made for machine reading (`llms.txt`) | 100 models; 46 chat pages marked `type: "endpoint"`; each page states inputs in words, the Hugging Face repository and the context | NVIDIA Build routes |
| NVIDIA `GET https://integrate.api.nvidia.com/v1/models` | Keyless list is old; use the key | NVIDIA trial terms | IDs only | Check that a model exists for this key |
| Hugging Face `GET https://huggingface.co/api/models/{repo}` | No (500 requests in 5 minutes) | Hugging Face terms | `pipeline_tag`, license, `safetensors.total` (parameters), file sizes for each quantization (`?blobs=true` or `/tree/main`), `gguf` (context, architecture) | Sizes, licenses, local install |
| Hugging Face `{repo}/raw/main/config.json` | No | Model license | `vision_config`, `audio_config` (or `sound_config`), layers, KV heads, head size, experts and active experts | What the weights take; memory and speed estimates |
| vLLM [supported_models.md](https://raw.githubusercontent.com/vllm-project/vllm/main/docs/models/supported_models.md) | No | Apache-2.0 | Inputs for each architecture: `T`, `I`, `V`, `A` (for example `Gemma 4: T + I + V + A*`) | Check inputs by architecture |
| llama.cpp [multimodal.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/multimodal.md) | No | MIT | Supported image, audio and video models and their `mmproj` files | What llama.cpp and Ollama can load |
| Ollama registry `https://registry.ollama.ai/v2/library/{model}/manifests/{tag}` | No | Same requests as `ollama pull` | Exact download size of each layer; an `image.projector` layer means it sees | One Ollama tag at a time |
| The model app itself: Ollama `/api/show` and `/api/ps`, LM Studio `/api/v0/models`, llama.cpp `/props` | No | Owner's app | Capabilities (`vision`, `audio`, `tools`, `thinking`), loaded context, memory in use | The truth for that host |
| LMArena [leaderboard dataset](https://huggingface.co/datasets/lmarena-ai/leaderboard-dataset) | No | CC-BY-4.0 | Ratings for text, vision, coding and more; updated 2026-10-10 | A smartness score Martlet can show |
| Epoch AI [all_ai_models.csv](https://epoch.ai/data/all_ai_models.csv) | No | CC-BY (From docs) | 3,626 models: parameters, open weights, dates, Hugging Face owner | Parameters and dates when others don't say |
| LiteLLM `model_prices_and_context_window.json` | No | MIT | 4,040 rows with `supports_vision`, `supports_audio_input`, `supports_video_input` | Not useful here: no OpenRouter rows, 3 NVIDIA rerank rows, 29 Ollama rows |
| Artificial Analysis API | Yes | Restrictive (see [How smart](#how-smart-and-how-fast)) | Scores, speed, first-token time | Not used directly |

Not useful for Martlet: ollama.com itself, because its terms forbid
*"automated means to access our services without permission"* and its
`robots.txt` disallows `/api/` (Verified). Other catalogs (Vercel AI Gateway,
the Hugging Face router, genai-prices, catwalk, tokencost) repeat what the
sources above give or cover only paid providers.

The daily refresh reads (Built), in this order: OpenRouter's list; models.dev's
`models.json` and `api.json` (only the rows of 15 providers: OpenRouter, NVIDIA
Build, Google Gemini, OpenAI, Anthropic, Groq, DeepInfra, Together, Mistral,
Cerebras, Fireworks, xAI, DeepSeek, Ollama Cloud and the Hugging Face router);
NVIDIA's `models.md` and each page it links (four at a time, at most 200);
vLLM's table; LMArena's `text` and `vision` ratings (the `overall` rows of the
`latest` split, through the datasets server's `/rows`, 100 at a time); and
Hugging Face and the Ollama registry for local models. It doesn't read Epoch
AI, LiteLLM, llama.cpp's `multimodal.md` or NVIDIA's keyless `/v1/models`.

### NVIDIA Build

1. Read `https://build.nvidia.com/models.md`. It links each model page twice;
   remove the copies.
2. Read each page it links (for example
   `/qc69jvmznzxy/diffusiongemma-26b-a4b-it.md`; use the links as given). Its
   header has `type: "endpoint"` for a hosted chat model (46 pages), `publisher`
   and `canonical` (the model ID). The text gives the inputs, the Hugging Face
   link and the context. The other 54 pages (speech, video, biology and other
   services) have no `type` line.
3. When the owner saves a key, read `GET /v1/models` with the key. It lists what
   that key can use.
4. Test only the models Martlet would suggest (they see and call tools) with a
   one-token request, and save the answer.

models.dev's NVIDIA list is not a good substitute: 70 of its 106 rows are on
neither of NVIDIA's current lists, including retired models (Verified).

## Conflicts found

These were measured on 2026-10-10 (Verified).

1. **One model, different providers.** models.dev lists 480 models on three or
   more providers. For the same model, providers disagree on image input for
   101 models, video for 93 and audio for 51. For `gemma-4-31b-it`, 42
   providers list it: 5 list text only, 10 list video and 1 lists audio.
2. **A catalog that leaves out an input.** models.dev's own record for
   `gemma-4-26b-a4b-it` lists text and image. Google's model card, vLLM and
   OpenRouter all include video. Gemma 4 understands video as a series of
   frames.
3. **A catalog that adds an input.** Nine models.dev providers list audio for
   `qwen3.5-122b-a10b`. vLLM lists Qwen3.5 as text, image and video, and
   OpenRouter agrees.
4. **One architecture, different sizes.** vLLM marks Gemma 4's audio with a
   footnote, because only some sizes hear. Each model's `config.json` settles
   it: `audio_config` is set for E2B and is `null` for 26B A4B.
5. **Old lists.** NVIDIA's keyless list and models.dev's NVIDIA rows both keep
   retired models (above).
6. **A copy matches its source.** models.dev's OpenRouter rows matched the live
   OpenRouter list on every input, tool and context for 379 models. It was
   missing 79 newer models. A copy is only as new as its last update.

## What to do about conflicts (Built)

Keep two kinds of facts, because a provider can serve less than the weights
can do:

- **Model facts**: what the weights take (`CatalogModel.Facts`). They come
  first from the model's maker: `config.json`, the model card and vLLM's table.
- **Route facts**: what one server takes for that model (`CatalogRoute.Facts`;
  a route is a server and a model ID, the same key as `ModelAbilities`). They
  come from that server.

`CatalogResolver` works out each model fact from the first level where a source
answers:

1. The maker: the model's `config.json` (its `vision_config`, `audio_config`
   and video tokens), its Hugging Face repository (parameters, license,
   context), the Ollama registry's projector layer, and vLLM's table when the
   model's repository is one of the examples vLLM names.
2. vLLM's table by family name (`google/gemma-4-26B-A4B-it` is in the family of
   vLLM's example `google/gemma-4-E2B-it`). When the matching architectures
   answer differently, as Gemma 4's two architectures do for audio, vLLM gives
   no answer.
3. Catalogs and servers that describe the model: models.dev's own record,
   OpenRouter's listing and NVIDIA's page.
4. The model's name (parameters only: "26B-A4B" is 26 billion, 4 billion active).

For each route fact, `CatalogResolver.ResolveRoute` uses the first source that
says it:

1. A Martlet test on that route: a test request (**Test hearing**, **Test
   vision**, **Test tools**), a refused input or an HTTP 410. This is what
   `model-abilities.json` holds, each fact with its source and date
   (`ModelAbility.Sources`); give it to `ModelCatalog.RouteFact`.
2. The server's own metadata for that model: OpenRouter's listing or NVIDIA's
   page in the catalog (Ollama's `/api/show`, llama.cpp's `/props` and LM
   Studio's type are read by `ModelContextProbe`, not the catalog).
3. The model facts (no route type in the catalog carries less than the model).
4. models.dev's row for this provider only.
5. The name guesses in `VisionModelCatalog` and `HearingModelCatalog` (callers
   apply them; they are not in the catalog).

Rules:

- Keep each source's answer with its name and date (`CatalogFact.Answers`). Work
  out the answer to use from them; never overwrite one source with another.
- "Only some variants" (vLLM's `*` footnote or brackets) is no answer.
- When two sources at the same level disagree, the answer is **Unknown**
  (`CatalogFact.Disagree`). Numbers within 5% agree, dates agree to the month,
  and licenses agree when they differ only in spaces and dashes. Martlet then
  offers a short test when the owner chooses the model, as Test hearing does
  now. The test uses a made-up picture or sound, never the owner's voice or
  screen (Built for hearing, vision and tools: Test hearing, Test vision and Test tools; not yet for video).
- Video has two forms. `input.video` says the model or route takes a video (the
  server or the model may turn it into frames); `input.video_frames` says Martlet
  can send frames as pictures, because the model sees.
- An expiration date that has passed marks an OpenRouter route retired
  (`CatalogRoute.Retired`), and models.dev's `deprecated` marks a row
  `Deprecated`. An HTTP 410 from a route marks the model retired on that route
  in `model-abilities.json`, with the date (Built). This comes before the
  hard-coded `RetiredModelIds`, which stay only as the fallback. An HTTP 404
  doesn't count: Ollama answers 404 for a model that isn't downloaded yet, and
  a wrong base URL answers 404 too.
- Catalog facts refresh at most once a day. A test result stays until the model
  changes (for example, a new Ollama digest).

The measured conflicts come out this way in the snapshot (and in the tests):
Gemma 4 26B A4B takes video and doesn't hear (both from its `config.json`;
models.dev's record leaves out video); Qwen3.5 122B A10B doesn't hear (vLLM by
family, although models.dev's record and some providers list audio); and each
`gemma-4-31b-it` route keeps only its own provider's row, while the model itself
doesn't hear because one provider lists audio.

## Memory and running locally

Martlet already estimates memory the same way in
[Resource footprints](RESOURCE_FOOTPRINTS.md). The sources give each part:

| Part | Source |
| --- | --- |
| Weights | The size of the quantization file: Hugging Face file sizes, or the Ollama registry's layer sizes |
| Picture or sound encoder | The `mmproj` file (Hugging Face) or the `image.projector` layer (Ollama) |
| KV cache (the memory for the context) | 2 × layers × KV heads × head size × context tokens × bytes for each value, from `config.json`. Sliding-window layers keep only their window ([Resource footprints](RESOURCE_FOOTPRINTS.md)) |
| Buffers | About 0.5-1 GB |

- A mixture-of-experts (MoE) model keeps **all** its experts in memory. Its
  **active** parameters set its speed, not its memory.
- [gguf-parser](https://github.com/gpustack/gguf-parser-go) reads a remote GGUF
  file's header without the full download and estimates memory to about
  100 MB (From docs). Martlet can do the same arithmetic itself.
- When a model runs, Ollama's `/api/ps` reports the memory it really uses. Save
  that for each host and prefer it to the estimate. (Built: this PC after a
  model loads, and each paired host's `loaded_models` in its machine report;
  `FootprintCatalog.WithMeasured` plans with it.)
- Ollama installs any Hugging Face GGUF with `hf.co/{repo}:{quant}`, so a new
  model needs no new Martlet release.

**Built.** `LocalModelFactsReader.LookupAsync` (`Martlet.Providers.LocalModels`)
reads these sources for a Hugging Face repository, an Ollama tag or both, and
returns `LocalModelFacts` (`Martlet.Core.Planning`): parameters, inputs from
`config.json`, each quantization's files and install name, and
`Estimate(quantization, contextTokens)`. GGUF repositories are found with
Hugging Face's `filter=base_model:quantized:{repo}&filter=gguf`, preferring
ggml-org, unsloth, lmstudio-community and bartowski. Measured memory is kept in
`model-memory.json` (`MeasuredModelMemory`). The estimate is within 6% of the
measured Gemma 4 and Qwen3.5 numbers
([Resource footprints](RESOURCE_FOOTPRINTS.md#estimating-a-model-martlet-doesnt-list));
MCP `local_model_facts` shows it all.

## How smart and how fast

- **OpenRouter** carries `benchmarks.artificial_analysis` (intelligence,
  coding and agentic indexes) on 284 models, with `hugging_face_id` to join
  them. Examples (Verified): Gemini 3.5 Flash-Lite 22.2, Gemma 4 26B A4B 16.7,
  Gemma 4 31B 14.7, Qwen3.5 9B 13.3.
- **Artificial Analysis**'s own data terms forbid putting raw data in a
  customer-facing product (§2.4(c)). They allow only brief, credited citations
  and not in a table or machine-readable form (§2.3) (From docs). The terms for
  reusing the copy in OpenRouter's list are not stated. Until that is checked, use the
  index only to rank models inside Martlet, and show words such as "smarter".
- **LMArena** ratings are CC-BY-4.0 and can be shown with credit. Small new
  models are often missing (no Gemma 4 E2B or Qwen3.5 4B row today).
- **When no source scores a model:** use a scored model of the same family and
  size, then active parameters and release date. Keep today's quality tier
  (`ServedModels.Tier`) as the last fallback.
- **What Martlet does (Built).** The catalog never keeps Artificial Analysis's
  index. When it reads OpenRouter's list it keeps only where each model's
  intelligence index ranks among the scored models (0 to 100), and it does the
  same with LMArena's text rating. `CatalogModel.Rank` is their average.
  `ModelCatalog.Smartness` uses it, or a scored model of the same family and
  size, or the median rank of at least three scored models with active
  parameters within 1.5 times and a release date within a year, or the quality
  tier from the name. It gives a tier (1 to 5) and words ("among the smartest",
  "smarter than most", "about average", "below average", "basic"), and
  LMArena's ratings with the credit "LMArena (CC-BY-4.0)". Show the words, the
  tier or the LMArena rating with its credit; never show the rank as a number.
  LMArena names join by name without effort words and dates
  (`claude-opus-4-6-high` is `anthropic/claude-opus-4.6`); a model with several
  rows keeps its best rating.
- **Speed:** no source measures the owner's own hardware. Estimate words per
  second from the card's memory bandwidth divided by the bytes of the active
  parameters. Then measure: the desktop log's `Reply latency` lines already
  give real first-word times. (Built: Martlet keeps each reply's Thinking first
  word in `model-speed.json`, and Recommended setup plans with it.)

## Checking a model when the owner adds it (Proposed)

This extends what Martlet does today (`ModelContextProbe` and
`model-abilities.json`). None of it runs while a reply is on its way, so it adds
no conversation latency.

1. Look the model up in the catalog: the snapshot shipped with Martlet, then
   the daily cache.
2. Ask the route's own metadata (step 2 of the order above), now for video and
   tools as well as hearing and seeing.
3. On a host, ask its Ollama `/api/show` for capabilities and `/api/ps` for the
   memory in use.
4. If an input is still Unknown, offer a short test with made-up content.
5. Save the answers in `model-abilities.json`, which other computers already
   share, with their source and date.

## The record (Built)

One **model** record for each model (`CatalogModel`), keyed by its Hugging Face
repository when it has open weights, else by the provider's ID:

- `Names`: Hugging Face repository, OpenRouter ID, NVIDIA Build ID, models.dev
  ID, Ollama tags (Martlet's own list and the local facts), LMArena names;
- `Facts`, each with every source's answer (source, value, note, date) and the
  answer worked out from them: `input.text`, `input.image`, `input.audio`,
  `input.video`, `input.video_frames`, `output.text`, `output.image`,
  `output.audio`, `open_weights`, `license`, `release_date`, `knowledge_cutoff`,
  `parameters.total` and `parameters.active` (billions), `context`, `tools`,
  `reasoning`;
- `Family` and `Size` from the name;
- `Local` (`LocalModelFacts`): for each quantization the install name and file
  size (plus the encoder and draft), and `config.json`'s layers, KV heads, head
  size and experts; `Memory()` estimates it at Martlet's 8,192-token context
  (measured memory for each host is kept in `model-memory.json`, not here);
- smartness: `Rating` and `VisionRating` (LMArena), `Rank` (inside Martlet only).

One **route** record for each server and model (`CatalogRoute`): `Provider`,
`ModelId`, `BaseUrl`, `Free` (OpenRouter's prices, NVIDIA Build's free trial
tier, models.dev's cost), `Context`, `Expires`, `Retired`, `Deprecated` and the
route `Facts`. What Martlet finds out on a route (`Video`, `Tools` and
`Retired`) stays in `ModelAbility`, which the route order above puts first. The
measured first word of each server and model is kept in `model-speed.json`
(`MeasuredFirstWords`), beside the measured memory in `model-memory.json`
([Recommended setups](RECOMMENDED_SETUPS.md#your-choices-and-a-better-setup)). A
route past its `Expires` date counts as retired: Martlet keeps that in
`model-abilities.json` and proposes a replacement at once (`RetiredModels`).

## Next steps

These match the tasks in [#663](https://github.com/throndir2/Martlet/issues/663):

1. Add `Video`, `Tools` and `Retired` to `ModelAbility`, and read them from the
   sources `ModelContextProbe` already asks. Add NVIDIA's model pages.
   **Built:** each fact keeps its own source and date (`ModelAbility.Sources`),
   a test outranks metadata, Test tools finds out about tool calls, and
   [Thinking models that hear and see](CONVERSATION.md#thinking-models-that-hear-and-see)
   describes it. A test for video itself isn't built yet: Martlet sends no
   video.
2. Add the catalog schema, a snapshot made by a local script at release time,
   and a loader in `Martlet.Core.Planning` (Built, above).
3. Add the daily background refresh (OpenRouter, models.dev, NVIDIA's
   `models.md`, vLLM, LMArena), with a size limit, a timeout and the last good
   copy, plus an MCP status tool (Built; no Doctor probe yet: the MCP status
   tool says what the refresh did).
4. Add Hugging Face sizes and `config.json` facts for open-weight models, and
   memory estimates for each host (Built in
   [#760](https://github.com/throndir2/Martlet/pull/760) and joined into the
   catalog above).
5. Replace the fixed lists in `FootprintCatalog.Seed`, the provider defaults and
   the `role.conf` choices with catalog picks, keeping today's entries as the
   offline fallback (Proposed: the recommendation design).
