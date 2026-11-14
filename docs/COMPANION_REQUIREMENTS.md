# Companion controls and behavior requirements

**Status:** Partial
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against 5f40af2d9; settings versions and recovery, persona limits and file bounds, card formats and import behavior, context-buffer caps, and OpenAI model IDs checked. Lorebook rules, persona authorization timing, F5/VLM integration, automatic listening, barge-in and hardware qualification remain unchecked.
**Code:** `src/Martlet.Audio/EnergyVoiceActivityDetector.cs`, `src/Martlet.Conversation/EndOfTurn.cs`, `src/Martlet.Core/Lorebooks/`, `src/Martlet.Core/Settings/AppSettings.cs`, `src/Martlet.Core/Settings/CompanionSettings.cs`, `src/Martlet.Core/Settings/CharacterCard.cs`, `src/Martlet.Core/Settings/SettingsRecovery.cs`, `src/Martlet.Core/Settings/SettingsStore.cs`, `src/Martlet.Core/Sync/SharedSettings.cs`, `src/Martlet.Desktop/CompanionWindow.xaml.cs`, `src/Martlet.Desktop/ConversationContextBuffer.cs`, `src/Martlet.Desktop/LorebookWindow.xaml.cs`, `src/Martlet.Providers/TextGenerationContracts.cs`, `src/Martlet.Providers/OpenAiTextGenerationCatalog.cs`

Requirements under **Planned** headings are not implemented. They do not
authorize listening, provider spending, model downloads, voice uploads, native
VAD execution, or changes to the user's machines.

## Planned experience and current gap

Martlet should feel like a conversational participant: listen, retain enough
recent context to understand what is happening, sometimes say nothing, and
yield when a person starts talking. The user controls its persona, voice,
models and participation frequency without editing
source code. Silence and processing states must be understandable, not look
like a broken connection.

| Requirement | User-facing outcome | Current boundary |
| --- | --- | --- |
| R19: Editable personas | Edit persona text, save several named profiles, import a replacement text file, or bring in a SillyTavern/Chub character card | **V05a/V05b implemented internally:** local named profiles, UTF-8 import/export, character card import (PNG/JSON/CHARX, V1-V3) persist; fresh explicit turns use the fixed selected revision |
| R19b: Lorebooks | SillyTavern-style World Info: keyword-triggered lore entries added to a reply, imported from SillyTavern or character cards | **Implemented:** local lorebooks with persona scope, keyword/regex/secondary-key activation, always-on entries, budget, recursion, SillyTavern import/export and a local test; see [Lorebooks](LOREBOOKS.md) |
| R20: Replaceable F5 voice | Select or replace reference audio and its matching transcript, then apply or preview the new voice | No app-integrated F5 worker or reference-voice picker; installing upstream F5 alone does not integrate it |
| R21: Replaceable LLM and VLM | Independently select compatible models from settings, without rebuilding Martlet | V02c exposes exact compatible LLM catalog choices and validates fresh route consent; no Desktop VLM route yet |
| R22: Listen-first participation | Collect bounded recent context and decide whether/when a reply is useful instead of answering every utterance | Explicit completed turns now supply bounded ephemeral context; automatic listening/observation collection remains unavailable |
| R23: Speech barge-in | Detect a person speaking during playback and promptly stop Martlet's voice | Stop/cancellation foundations exist; the energy detector and end-of-turn judge find speech, but they are not live barge-in |
| R24: Adjustable response mix | **Removed:** the persona text sets how Martlet talks | No style sliders and no per-reply style pick; see [R24](#r24-response-style-mix-removed) |

See [the implemented conversation](CONVERSATION.md),
[participation policy](../src/Martlet.Participation/README.md) and
[voice activity detection](VAD.md) for exact current behavior. Existing library foundations
do not satisfy these new end-user requirements.

## R19: Editable persona profiles

### Implemented settings slice

**Implemented settings slice:** Desktop now exposes **Companion personas**.
It can create, duplicate, select and delete up to 1,000 named profiles; edit
bounded persona text; and import/export UTF-8 text. The current settings schema
(v6) owns this data; schema v3 first added companion settings. The settings
store snapshots the prior schema during migration. Configuration recovery
carries forward current Companion settings when an imported snapshot has none.
Import and export use explicit selected files; export is
create-only and never overwrites an existing file.

The follow-on V05b slice sends the fixed active persona revision only after
an explicit typed/PTT action passes participation.
The combined user/persona input must fit the existing byte/token
reservation and is never silently truncated. The subsequent bounded explicit
context slice includes recent completed exchanges within that same budget;
neither slice enables automatic listening, provider permission, capture or preview.

### Current settings and file bounds

Martlet supports up to 1,000 personas and 1,000 character profiles. Each
persona allows 100,000 characters; all persona text together allows 1,000,000
characters. The settings file is limited to 8 MiB, and each shared setting
value is limited to 16 MiB. Persona text import accepts UTF-8 files up to
300,000 bytes. Export creates a new file and does not overwrite an existing file.
Invalid or unreadable imports do not replace the text in the editor.

### Planned: Persona identity and participation boundaries

Add persona aliases and keep participation controls separate from persona
selection. Changing a persona must not arm a microphone, increase capture
permission or change provider routes.

### Implemented character cards

Martlet reads the character cards SillyTavern, Chub (CharacterHub), RisuAI and
other Tavern-compatible apps share: a PNG/APNG card image (the `ccv3` text
chunk, else `chara`, as base64 UTF-8 JSON; image pixels are never decoded), a
JSON card or a CHARX archive's `card.json`, in TavernAI V1, Character Card V2
(`chara_card_v2`) or V3 (`chara_card_v3`) form. **New persona from a character
card** adds the card as a new persona (also by dropping a card onto the
Companion window, or *Import a character card* on Companion › Personality);
Martlet uses it immediately. **Update this persona from a character card**
loads it into the selected persona's editor and saves it, keeping its identity
and where its voice pauses.

The card's name (its V3 nickname when present) becomes the persona name, made
unique. The persona text joins the system prompt, description, personality,
scenario, always-on (constant) lorebook entries, post-history instructions,
example dialogue and greeting. `{{char}}`/`<BOT>` become the character's name,
`{{user}}`/`<USER>` become "the user", and `{{original}}`/`<START>` are
removed. Text must fit the per-persona and combined limits above: sections are
kept by priority (description, personality, scenario, system prompt, then the
rest) and the result names any section shortened or left out. Creator notes,
tags, assets and extensions are not used. The card's keyword-triggered lorebook
entries go to a [lorebook](LOREBOOKS.md) named after the card and used only
with that persona; it is saved together with the persona.

Save is atomic. Apply while busy requires stopping the current turn and waiting
for owned cleanup; otherwise apply when idle. The next fresh authorized turn
uses a fixed persona revision, with bounded prompt/token accounting. A persona
edit must not alter an already authorized request or restart canceled speech.
Persona text is user content, not a permission source or an override of safety,
data routing, Stop, capture controls or provider budgets. Explain that the
active persona text will be sent to the selected LLM; exclude it from ordinary
logs/support exports. Imported profiles contain no credentials or permissions.

## R20: F5 reference-voice selection and replacement (Planned)

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

### Implemented LLM selection

V02c provides a catalog-backed **Conversation model (LLM)** choice for the named
OpenAI adapter. The pinned `gpt-4.1-mini-2025-04-14` and
`gpt-4.1-2025-04-14` snapshots use the existing bounded Responses wire
contract. Changing the model invalidates destination consent and the next fresh
action binds its credential and request to that exact selection. Unsupported
IDs leave the prior working route intact. Catalog presence remains **NOT RUN**,
not account availability, price, quality, or live readiness.

### Planned: Independent LLM and VLM settings

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

### Planned: Conversational listening

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

### Implemented explicit-turn context

Completed typed/PTT exchanges of the
open talk window are retained in memory only (at most 2,048 user/assistant
pairs and 8 MiB UTF-8). Each fresh action's displayed authorization covers
this context; oldest pairs are omitted until current input, persona, style and
context fit the context size (Companion › Replies; 100,000 estimated tokens by
default for a cloud model, within the model's known limit). Failed, refused and
policy-suppressed turns are not retained. Refresh context, pause, lock,
configuration load/change and closing the conversation clear the buffer; Stop
keeps it. This does not enable automatic capture, unsolicited replies,
persistent memory or an observation backlog.

### Planned: Observation and reply handling

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

## R23: VAD-driven interruption (Planned)

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

## R24: Response-style mix (removed)

Martlet no longer has response-style weights. Earlier versions had sliders for
**helpful**, **sarcastic**, **silly**, **distracted** and **playful teasing** on
each persona. Each reply picked one style at random by these weights and sent
that style's prompt line with the persona. No character card format has these
fields, and the persona text can say the same thing. Thus the persona text (for
example from a SillyTavern character card) alone now sets how Martlet talks.

Settings that hold saved weights or edits to the style prompts still load.
Martlet ignores them and does not save them again. A Persona prompt edit that
still has the old `{style}` line loses that line.

## Planned delivery order and acceptance

Preserve the installation -> configure -> PTT -> spoken reply -> reliable Stop
priority. Next independent slices are persona/settings controls and LLM model
selection, followed by runtime persona/style/context integration. Automatic
listening and interruption need authorized live VAD/feedback integration; F5
reference controls land with the self-hosted F5 adapter; VLM selection lands
with opt-in vision. None requires an avatar or persistent memory first.

An earlier PTT-only release may ship without
automatic listening/barge-in, but must not advertise those capabilities.
Future implementation must add production-backed local tests plus separately
authorized real-device/model evidence; this requirements change supplies neither.
