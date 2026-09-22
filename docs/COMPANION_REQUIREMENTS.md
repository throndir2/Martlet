# Companion controls and behavior requirements

**Product requirements added 2026-09-19; not implemented features.** These
requirements extend the installation-first [development plan](../DEVELOPMENT_PLAN.md).
Detailed controls and initial tuning values below are proposed implementation
choices. They do not authorize listening, provider spending, model downloads,
voice uploads, native VAD execution, or changes to the user's machines.

## Intended experience and current gap

Martlet should feel like a conversational participant: listen, retain enough
recent context to understand what is happening, sometimes say nothing, and
yield when a person starts talking. The user controls its persona, voice,
models, participation frequency, and mix of response styles without editing
source code. Silence and processing states must be understandable, not look
like a broken connection.

| Requirement | User-facing outcome | Current boundary |
| --- | --- | --- |
| R19: Editable personas | Edit persona text, save several named profiles, or import a replacement text file | **V05a/V05b implemented internally:** local named profiles, UTF-8 import/export and weights persist; fresh explicit turns use the fixed selected revision |
| R20: Replaceable F5 voice | Select or replace reference audio and its matching transcript, then apply or preview the new voice | No app-integrated F5 worker or reference-voice picker; installing upstream F5 alone does not integrate it |
| R21: Replaceable LLM and VLM | Independently select compatible models from settings, without rebuilding Martlet | V02c exposes exact compatible LLM catalog choices and validates fresh route consent; no Desktop VLM route yet |
| R22: Listen-first participation | Collect bounded recent context and decide whether/when a reply is useful instead of answering every utterance | Explicit completed turns now supply bounded ephemeral context; automatic listening/observation collection remains unavailable |
| R23: Speech barge-in | Detect a person speaking during playback and promptly stop Martlet's voice | Stop/cancellation foundations exist; standalone post-capture VAD is production-blocked and is not live barge-in |
| R24: Adjustable response mix | Tune helpful, sarcastic, silly, distracted and playful trolling/teasing styles | Per-persona controls persist and the explicit conversation path selects one bounded dominant style; human-perceived style qualification remains |

See [the implemented conversation](CONVERSATION.md),
[participation policy](../src/Martlet.Participation/README.md) and
[VAD boundary](VAD.md) for exact current behavior. Existing library foundations
do not satisfy these new end-user requirements.

## R19: Editable persona profiles

**Implemented settings slice:** Desktop now exposes **Companion personas**.
It can create, duplicate, select and delete up to 16 named profiles; edit
bounded persona text; import/export UTF-8 text; and edit validated style
weights. Settings v3 owns this data, atomically snapshots v1/v2 on migration,
and includes personas in same-profile configuration recovery. A v1/v2 recovery
source preserves current v3 personas because those older snapshots contain no
persona data. Import and export use explicit selected files; export is
create-only and never overwrites an existing file.

The follow-on V05b slice sends the fixed active persona revision and one
weighted style only after an explicit typed/PTT action passes participation.
The combined user/persona/style input must fit the existing byte/token
reservation and is never silently truncated. It adds no history, automatic
listening, provider permission, capture or preview action.

Provide a **Companion** settings page with a multiline persona text editor and
named profiles. Create, duplicate, rename, select, save and delete profiles;
keep one active persona at a time initially, not multiple autonomous agents.
Each profile holds the persona text, companion name/aliases and response-style
weights. Participation controls remain separately labeled; switching personas
must not arm a microphone, increase capture permission or change provider routes.

Support explicit UTF-8 plain-text import/export of the persona text so a user
can edit it externally and swap files. Show the imported draft before Apply;
do not silently watch files or execute scripts, templates or embedded commands.
Each profile is limited to 8,192 characters and 16 KiB UTF-8; all profile texts
together are limited to 16,384 characters and 32 KiB UTF-8 so every accepted
profile collection remains persistable within the bounded settings document.
Version and bound persisted profile data in the existing settings ownership
and migration system; do not introduce a second competing settings store.
Reject unreadable, malformed, oversized or unsupported files with a remedy,
preserving the last saved profile and the user's draft.

Save is atomic. Apply while busy requires stopping the current turn and waiting
for owned cleanup; otherwise apply when idle. The next fresh authorized turn
uses a fixed persona revision, with bounded prompt/token accounting. A persona
edit must not alter an already authorized request or restart canceled speech.
Persona text is user content, not a permission source or an override of safety,
data routing, Stop, capture controls or provider budgets. Explain that the
active persona text will be sent to the selected LLM; exclude it from ordinary
logs/support exports. Imported profiles contain no credentials or permissions.

## R20: F5 reference-voice selection and replacement

For a configured compatible F5 adapter, offer **Reference audio**, **Reference
transcript**, **Apply voice**, and a separately authorized **Preview voice**
action. Allow several named reference presets. Changing the reference is a
voice-conditioning change, not training or changing the F5 model weights.
A full upstream F5 installation is one possible runtime; Martlet still needs
the named, version-qualified adapter and explicit connection configuration.

The user can choose a different file, or edit/replace the existing file and
explicitly reload it. Validate the actual audio format, readability, duration,
nonempty usable content and adapter limits before activation. Require a
matching transcript supplied or reviewed by the user; do not silently invoke
Whisper, download auxiliary models or use a billable transcription fallback.
Obtain reference-voice rights confirmation and disclose storage and the exact
local/remote processing destination.

Bind activation to a validated snapshot/revision of audio and transcript, not
only a mutable file path. Replacing bytes at the same path invalidates cached
conditioning and requires revalidation/Apply; it must not change a running
turn's voice. Apply uses the same stop/cleanup/idle boundary as persona changes.
Missing, changed or invalid references visibly block use until resolved;
never substitute an unrelated/default voice or silently keep using an old
voice after a failed Apply. Preserve the previous preset for explicit reselect.
Preview is bounded and requires fresh synthesis/playback permission; opening
the picker or saving a preset must not synthesize, upload or play anything.

Keep reference audio/transcripts out of default logs and support bundles.
Document how to remove saved references and derived conditioning caches without
deleting the user's original external file. F5 code, weights, auxiliary models
and voice rights remain separate gates; a user-supplied file grants no model
license or redistribution permission.

## R21: Independent LLM and VLM model selection

V02c provides a catalog-backed **Conversation model (LLM)** choice for the named
OpenAI adapter. The pinned `gpt-4.1-mini-2025-04-14` and
`gpt-4.1-2025-04-14` snapshots use the existing bounded Responses wire
contract. Changing the model invalidates destination consent and the next fresh
action binds its credential and request to that exact selection. Unsupported
IDs leave the prior working route intact. Catalog presence remains **NOT RUN**,
not account availability, price, quality, or live readiness.

Provide separate **Conversation model (LLM)** and **Vision model (VLM)** settings
showing adapter, destination, actual model ID/revision, capabilities, readiness
and relevant resource/cost information. Support known catalogs and an explicit
model-ID entry for named adapters that can validate it. The intent is to switch
compatible models without a Martlet rebuild, not to accept arbitrary endpoints
or promise every model/runtime format works.

Selection is separate from downloading, loading, inference and authorization.
Check role capabilities and context/output/image limits, invalidate stale
probe results, and obtain fresh consent for changed model/destination/data/cost
scope. A catalog listing is not readiness. Downloads, warmup or paid probes
require their own permission; no automatic cloud/model fallback.

Apply at an idle, owned boundary; if busy, require stopping and finishing
cleanup first. Snapshot the route/model revision for each new action and
invalidate old pending intents/authorizations and old-model optional results.
Re-budget persona and recent context for the new model; expose omitted context
instead of silently exceeding limits. Do not wipe local persona/preferences
or consented memory just because a model changes, and do not upload the entire
memory store. Incompatible/unavailable selections show an actionable error
without overwriting the last valid saved choice or claiming a successful switch.

LLM changes must not change the VLM or voice; VLM changes must not change the
LLM or enable screen capture. Vision may remain disabled/unavailable while
voice works. Local model fit/loading latency is visible; "change at will"
means user-controlled selection, not instant GPU hot-swapping. LLM selection
now lands before VLM integration; the common role-selection design must serve
both.

## R22: Listen first, respond selectively

Keep explicit typed/PTT as the default and reliable way to request an answer.
Add an opt-in **Conversational listening** mode with visible listening,
collecting context, waiting for a gap, responding and suppressed states.
Silence is normal: no reply and no LLM/TTS request for each ignored input.
Suppress irrelevant/unaddressed/no-speech/self-audio inputs; allow an eligible
reply only after current permission, address/relevance, gap, cooldown and
rate-budget checks. Start with deterministic policy, not a second paid LLM
evaluating every utterance.

During an authorized listening session, retain only eligible, consented recent
transcripts and already-enabled observations as bounded in-memory context.
Do not retain known self-audio, no-speech or rejected uncertain transcripts as
facts. Initial proposed caps are 120 seconds, 32 observations and 16 KiB UTF-8
text, whichever is reached first; evict oldest entries and further trim to the
selected model's input budget. This is not persistent memory or an audio archive.
Clear on session end, pause/lock or consent revocation; new routes require
explicit authorization for any retained context they would receive.

**Implemented explicit-turn foundation:** completed typed/PTT exchanges retain
at most eight user/assistant pairs, 16 KiB UTF-8 and two minutes in memory.
Each fresh action's displayed authorization covers this context; oldest pairs
are omitted until current input, persona, style and context fit the existing
LLM byte/token reservation. Failed, refused and policy-suppressed turns are not
retained. Pause, lock, configuration load/change, Stop and closing the
conversation clear the buffer. This does not enable automatic capture,
unsolicited replies, persistent memory or an observation backlog.

An observation is not a pending reply. Several utterances can inform one later
response, but expired intents cannot be replayed and old provider permission
cannot be renewed by retaining text. Reevaluate fresh activity before dispatch;
new speech resets the conversational gap. Never queue a delayed answer to each
utterance or burst out a backlog after the user stops talking.

Expose participation frequency/rate, minimum quiet gap, cooldown and context
retention separately from personality/style. Begin with the existing proposed
1.2 s unsolicited gap, 8 s cooldown and ceiling of two unsolicited replies per
minute, zero until opted in. A "no unsolicited replies" setting sets that rate
to zero while preserving explicit typed/PTT requests. Direct explicit requests
must not be randomly ignored by a personality weight. Longer listening means
gathering useful context, not artificial delay after every deliberate request.

The reason timeline is metadata-only. Local VAD monitoring is not permission
to upload: cloud STT can receive every qualifying utterance even when Martlet
stays silent, and that cost/disclosure must be shown before enabling the mode.
No background capture on launch, implicit remote-participant audio, new screen
capture, or persistent memory follows from a persona/listening selection.

## R23: VAD-driven interruption

In explicitly armed and qualified barge-in mode, keep local speech detection
active during Martlet playback. Confirmed external speech onset must stop
playback promptly, flush queued PCM and unsaid TTS segments, advance the output
epoch, request upstream cancellation where supported and discard late output.
Do not wait for end-of-utterance, STT, a name match or an LLM decision to go quiet.
Target p95 at most 250 ms from **confirmed speech-onset event** to the last
rendered sample; measure acoustic-onset-to-detection latency separately.
Neither threshold is presently a measured result.

Continue handling the interrupting utterance only within existing capture and
processing permission. Interruption alone does not authorize STT/LLM, guarantee
a reply, release retained runtime ownership, or resume the old answer. After
cleanup, a fresh eligible turn may use that utterance; canceled output never
replays automatically. Manual Stop remains immediate and independently usable.

Detecting sound is not detecting another speaker. Qualify speech/noise onset,
echo/self-voice rejection, game audio and overlap on each supported headset/
speaker topology. VAD alone is not AEC or speaker identification. Where feedback
protection or native privacy qualification is missing, leave automatic barge-in
unavailable, explain why and retain manual Stop/PTT. The existing post-capture
VAD library cannot monitor live playback without new reviewed integration.
This is a required companion capability, deferred behind real qualification,
not permission to bypass the current VAD hold or drop the requirement.

## R24: Response-style mix, separate from response frequency

Provide labeled weights/sliders for **helpful**, **sarcastic**, **silly**,
**distracted**, and **playful trolling/teasing**. Store them per persona and show
the normalized intended mix. Proposed representation: integer weights 0-100,
at least one positive; reject all-zero/invalid input rather than invent a mix.
Initial conservative preset is helpful 100, others 0; users can edit it freely.

After participation allows a reply, the V05b runtime chooses one dominant style using these
relative weights, then supply it with the active persona and bounded context.
Zero-weight styles are never sampled; a single nonzero weight always wins.
Inject the random source for reproducible production-path tests. These are
long-run selection proportions, not guaranteed percentages in a short chat
or proof that an LLM's wording actually expresses the selected style.

Style never changes permission, response rate, Stop behavior or factual
correctness. A serious request or explicit request for help takes precedence
over comedic flourish. "Distracted" is a conversational style, not fabricated
screen awareness, ignored controls or intentionally broken task execution.
"Trolling" means opt-in harmless banter, not harassment, deceptive factual
answers or sabotage. Verify perceived style on consented held-out conversations
separately from deterministic weight-selection tests.

## Delivery order and acceptance

Preserve the installation -> configure -> PTT -> spoken reply -> reliable Stop
priority. Next independent slices are persona/settings controls and LLM model
selection, followed by runtime persona/style/context integration. Automatic
listening and interruption need authorized live VAD/feedback integration; F5
reference controls land with the self-hosted F5 adapter; VLM selection lands
with opt-in vision. None requires an avatar or persistent memory first.

[Delivery](DELIVERY.md#8-requirements-to-delivery-traceability) maps R19-R24 to
owned work and AC-18 through AC-23. An earlier PTT-only release may ship without
automatic listening/barge-in, but must not advertise those capabilities.
Future implementation must add production-backed local tests plus separately
authorized real-device/model evidence; this requirements change supplies neither.
