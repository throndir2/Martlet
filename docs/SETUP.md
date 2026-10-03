# Resumable setup, Windows credentials and local audio (V02a/V02b)

**Local configuration backup / restore (V07a)** is available from the main
window and Setup / resume. It is same-profile recovery only, not portable
profile import, corrupt-store repair or binary rollback. See the
[installed recovery walkthrough](TROUBLESHOOTING.md#local-configuration-backup--restore-v07a)
and the transaction contract below.

[Troubleshooting](TROUBLESHOOTING.md) is available from Setup and Audio setup
even before configuration succeeds. Opening it is passive; local metadata
recording is OFF until explicitly started. Preview/export never reads keys,
audio or raw settings and does not replace malformed settings.

**Setup is configuration, not live readiness or future spending permission.**
The separate [V04b real API conversation](CONVERSATION.md) now consumes these
saved choices for explicitly authorized typed/PTT actions. Unsupported model/
voice IDs are rejected there without discovery or fallback; its guide lists
the exact supported IDs and account/device gates still NOT RUN.
Open **Setup / resume**
in the existing Desktop. The first tab is **Overview**, which describes using
OpenAI, OpenRouter, NVIDIA Build or an OpenAI-compatible API. Opening setup and
ordinary Desktop/Doctor status are read-only: no credential lookup, device
enumeration, capture, provider discovery or network requests.

Use the **Jobs** tab to save per-job routes without making an inference
request. It sets up one job at a time: **Thinking** (conversation
model, internally the LLM role), **Listening** (speech to text, STT) and
**Speaking** (text to voice, TTS). Each job shows only its own fields: the
provider and base URL appear only for Thinking, the voice only for Speaking.
Listening and Speaking use the named OpenAI origin `https://api.openai.com`
(or a paired host from the Devices map). Internal aliases `openai-stt`,
`openai-llm`, and `openai-tts` are Martlet policy identifiers, not upstream
model names. The home steps open Setup on the matching job. See
[COMPONENTS.md](COMPONENTS.md) for the jobs/placement design.

For Thinking, **Provider** selects one of (the recommended model is prefilled):

| Provider | API base URL | Prefilled model | Key |
| --- | --- | --- | --- |
| OpenAI (Responses API) | `https://api.openai.com` | `gpt-4.1-mini-2025-04-14` | Required |
| OpenRouter | `https://openrouter.ai/api/v1` | `google/gemma-4-26b-a4b-it` (talks, sees, calls tools) | Required (OpenRouter key) |
| NVIDIA Build | `https://integrate.api.nvidia.com/v1` | `google/diffusiongemma-26b-a4b-it` (a fast Free Endpoint that talks, sees and calls tools) | Required (`nvapi-...` key from build.nvidia.com) |
| Custom OpenAI-compatible endpoint | Any canonical HTTPS base such as `https://api.groq.com/openai/v1`, or a loopback server such as `http://127.0.0.1:1234/v1` (LM Studio), `http://127.0.0.1:8080/v1` (llama.cpp) or `http://127.0.0.1:11434/v1` (Ollama) | none (enter the model your server serves) | Optional |

Listening prefills `gpt-4o-mini-transcribe`; Speaking prefills
`gpt-4o-mini-tts-2025-12-15` with voice `alloy`. Switching provider replaces a
prefilled default with the new provider's default but keeps a model you typed.
The Chat Completions providers accept any exact model ID (for example
`openai/gpt-4o-mini` or a `:free` variant on OpenRouter); there is no model
catalog or discovery. Martlet appends `/chat/completions`. HTTP is allowed only for a
literal loopback IP (`localhost` is rejected). Keys are bound to the exact base
URL. Switching the LLM to another destination detaches the previous key and
lists it for explicit removal on **Credentials**. Each reply is capped at 256
tokens; reasoning/thinking models spend part of that on hidden thinking (never
spoken or shown), so prefer instruct/chat models.
A prefilled default is only a suggestion: nothing is saved until you apply the
job and consent, and saving does not prove that an ID exists or is accessible.

Both named defaults also see images, so Companion › Vision works without
changing models. NVIDIA retired Martlet's earlier default,
`meta/llama-3.3-70b-instruct`, on 2026-08-26 (it answers HTTP 410 Gone). Martlet
knows the retired NVIDIA Build IDs it has seen (`ChatCompletionsEndpointCatalog`):
a saved route on one is refused before sending, and Thinking and Vision name the
new default as the fix. A 410 from any provider is reported as `ModelRetired`,
so the talk window says the model was retired and to choose another.

### Thinking fallback

Companion › Thinking › **If Thinking fails** saves an optional second
destination: any OpenAI-compatible Chat Completions endpoint (OpenRouter, NVIDIA
Build, OpenAI's `https://api.openai.com/v1`, Ollama on this PC or a custom
server) and model, with an optional key of its own (bound to that base URL;
leave it empty to reuse Thinking's key on the same endpoint). When a Thinking
request fails before any of its text arrived (an error, a rate limit, no first
token within 15 seconds, or a broken stream), Martlet asks the fallback once
for that reply instead. This covers replies, screen and camera glances, and
memory requests. Ticking its box is the consent to send messages, recent
conversation and any glance image there when that happens; the talk window's
disclosure names it. A reply that already started is never restarted
elsewhere. It must differ from Thinking, and should see images if you use
vision. Each fallback use is logged (*Reply: Thinking failed (RateLimited) on
... the Thinking fallback ... answered instead.*). *Turn off* removes it and
its key. A settings restore drops the fallback and lists its key for removal.

NVIDIA Build's free tier allows about 40 requests a minute per key (shared
across models) and 5 at once, and it also limits requests when its free capacity
is busy, so a 429 can arrive well below that. Martlet's vision sends at most 45
glances an hour. When a glance is rate-limited or doesn't get an answer (after
the fallback, if there is one), vision keeps watching and waits 1, 2, 4, 8 and
then 10 minutes before the next look instead of stopping.

For each job, review the displayed boundary and apply the route:

| Role | Disclosed data destination |
| --- | --- |
| STT | Microphone audio to OpenAI; even an utterance later suppressed can already have been disclosed and charged. |
| LLM | Transcript and conversation text to the selected LLM provider (OpenRouter additionally forwards it to an upstream provider it chooses). |
| TTS | Response text to OpenAI; the returned voice is generated, not a human recording. |

Cloud use can cost money. Current price, quota, API-key validity and model
access are **unknown**, not free or verified. Official
[API pricing](https://openai.com/api/pricing/) and
[data retention policy](https://platform.openai.com/docs/guides/your-data)
are linked in the UI (copy reviewed 2026-09-13; no rates cached). Opening a link
requires an explicit browser action. A ChatGPT subscription is not API quota.
Screen remains OFF; [memory](MEMORY.md) is ON by default for new and migrated
profiles and can be turned off in Companion › Memory. No health test, account login,
provider listing or billable probe is run.

Back/Next and the tabs navigate Overview, Jobs, Credentials and Review.
**Apply** commits route fields to the working checkpoint; **Save checkpoint**
or **Save and exit setup** persists it atomically. Unsaved route fields block
save until applied; changing the selected job discards unapplied fields with
a visible explanation. **Reload** explicitly discards unsaved edits. The
keyboard-focusable status remains available at every step. Missing roles,
consent, key references and audio qualification are explained instead of
displaying a pretend completed voice setup. Reopening resumes the saved step.
Saving a legacy Fixture profile upgrades it to Api.

### Slow or interrupted setup actions

All settings open/read/flush/replace and native vault work runs off the WPF
dispatcher through one app-shared `SetupOperationRunner`. The UI snapshots
configuration, revision, role and masked input before dispatch; the worker owns
the resulting `SecretLease`, not the lifetime of the observing window.

The UI stops waiting after five seconds and requests cooperative cancellation.
**Cancel setup action** and **Close setup (stop observing)** remain available.
Neither timeout nor Close claims the native operation ended or rolled back:
already-started work may finish, and pending recovery metadata must be reviewed.
No overlapping setup action, including one from a reopened setup window, can
start until the actual worker and cancellation callbacks release. Only then is
the secret cleared and the worker slot reusable. Reload is required after
interruption; late results cannot update a closed or retired observation.
Closing setup discards unsaved UI edits, not an already-started transaction.
If the app process exits before work returns, its durable pending-reference
checkpoint remains the restart recovery boundary.

## Consent and schema

`AppSettings` accepts strict schemas v1-v6. Profile schema stays v1.
v3 adds companion personas/styles; v4 adds the local-memory enable/path policy,
not facts; v6 makes memory ON by default (a v4/v5 memory section that is only
OFF by the old default reads as ON until saved as v6). v2 adds versioned `SetupSettings`: a bounded
checkpoint, up to three `SetupRoute` values and at most sixteen pending owned
credential removals. Settings contain no secret or transcript. Unknown fields,
invalid states, newer versions, malformed encodings and oversize files are
rejected without rewriting their original bytes.

`SetupSettings.Begin` only prepares an in-memory edit at the current schema.
Explicit migration from v1-v5 preserves the original bytes; a v1 save
uses `File.Replace` to commit and snapshot the exact original as
`settings.v1.<opaque-id>.bak` in the same directory. `SettingsSaveResult`
explicitly reports `MigratedFromVersion1` and `SnapshotFileName`. Existing
profile ID, kind (unless deliberately changed in the UI) and legacy credential
references are retained. Legacy references are not resolved, reused or deleted
by this setup. Downgrade through Save is rejected. This snapshot is not a
blanket rollback guarantee or V07 lifecycle qualification.

`DestinationConsent` is an immutable versioned record of explicit user
selection, role, provider alias, approved origin, actual model/voice,
credential reference and route configuration revision. It must exactly match
its route. Model/voice/key changes renew that role's revision and clear its
consent; unrelated roles retain their choices. Checkpoint navigation does not
invalidate unchanged choices. **Persisted consent is not per-turn permission,
capture-on-launch permission, or a perpetual paid-request authorization.**

`SettingsStore` retains bounded strict loading, create-only null revision,
SHA-256 optimistic revisions, one cooperating-writer lock, flushed temporary
files and atomic replacement. Stale saves require reload/review. A normal save
cannot silently drop an active credential or a pending cleanup marker.
No app-data directory/file is created by launch or first-run load.

### Schema 5 self-host client foundation (layer 1)

Schema 5 uses setup schema 2 with explicit `OpenAi`, `GatewayOllama`,
`GatewayF5`, and `LocalWhisper` role discriminators. Migrating ordinary API
settings preserves their role/model/voice/credential choices and does not select
self-host routes. Endpoint, model, package and reference snapshots are passive
configuration, not runtime qualification. The current Desktop still offers API
setup only and refuses self-host dispatch. Actual accessible self-host Setup,
typed/PTT/optional-TTS routing and the expanded published
dependency graph are a **mandatory separate layer 2**, not completed by this
foundation.

Gateway bindings use exact HTTPS IP origin, host ID, SPKI pin, device identity,
voice role, route type and a fresh opaque credential reference. The Windows
namespace is `Martlet/v3/<profile>/gateway/<route-type>/<scope-sha256>/<reference>`;
OpenAI targets remain unchanged. No origin/key forwarding, discovery,
trust-on-first-use, vault reads or requests occur on load/save.

Paired devices are permanent: no expiry timer or reboot-triggered re-pairing.
This client binding is distinct from the server's durable device authority and
from per-action data/cost/resource permission. Server revocation or actual key
loss needs deliberate repair; an ordinary connection failure does not delete
the saved association.

Recovery never adopts snapshot active/retained credential references. It uses
only the current destination's exact owned bindings, clears route enablement,
action/destination consent, probe/package/reference evidence, and preserves
matching current references. Mismatches remain in bounded
`RetainedGatewayCredentials`, separate from pending deletion. A retained pairing
can be explicitly reconnected to its exact destination using the settings
writer; that action remains offline and requires fresh probe/action approval
before inference. Explicit detachment moves only the selected binding into
scoped cleanup. Active, retained, legacy and pending IDs cannot overlap.
Capacity overflow is an explicit refusal, never silent eviction. Neither
restoration nor client cleanup changes server device authority.

## Real vault boundary and explicit transactions

Desktop constructs `WindowsCredentialStore`, not a production fake.
`Martlet.Credentials.Windows` uses BCL/PInvoke `CredWriteW`, `CredReadW`,
`CredDeleteW`, and `CredFree`; no added NuGet package, credential enumeration,
elevation, service, permission or protection change. Native buffers are cleared
and freed; `SecretLease` owns bounded token characters, redacts `ToString`,
has no secret serialization property and must be disposed. WPF uses a masked
`PasswordBox`/`SecurePassword`, clears the field after store, and never
automatically copies a key. Managed/OS copies cannot provide a guarantee
against a debugger or malware already running as the signed-in user.

The generic `ICredentialStore` takes `CredentialBinding` with profile UUID,
credential UUID, role, provider alias and exact origin. Targets have the form
`Martlet/v2/<profile>/api.openai.com/<role-alias>/<credential>`; no secret occurs
in a target, command argument, JSON, diagnostic, exception or log. Changing
profile, origin or role cannot resolve the same policy-bound target.
Missing, access denied, unsupported platform, invalid input and unavailable
session/native failure are distinct typed results with authored remedies.

The production Desktop calls `ISetupService` / `SetupService` through the shared
worker for all these actions. `SetupOperation.Completion` describes actual
worker release; ending a UI observation does not complete that task.

1. **Store / replace** asks for scope confirmation and validates configuration
   and input before writing. Under the settings writer lock it saves a durable
   pending reference for a fresh UUID, then writes only that vault target.
   The next atomic settings commit attaches the new reference, invalidates
   consent and queues any old reference for explicit removal.
2. Failure/cancellation after a native write rolls back **only the fresh
   target**. The pending marker survives interruption, failed rollback, and
   restart. Reload and explicitly remove it; a missing target is a safe
   idempotent cleanup result. Metadata may have been saved even when attaching
   the key fails; the UI reports this and requires reload.
3. **Read selected key** requires explicit confirmation, reads and disposes
   the selected key without revealing it. It proves only local vault presence,
   not provider access. Ordinary diagnostics never invoke it.
4. **Detach** saves removal of the selected active reference and invalidates
   consent before any deletion. **Remove selected detached key** is a separate
   irreversible confirmation. It reloads under the writer lock and refuses a
   stale revision, wrong profile, active reference or unrelated credential.
   It deletes that one detached target before clearing the pending marker.
   Failed delete or post-delete save retains the marker for retry. Restoring an
   old settings snapshot never resurrects a deleted secret.

The lock coordinates writers using this settings directory, not arbitrary
external file editors or copies of profiles in other directories. Do not edit
or copy active profiles between running apps. Filesystem power-loss behavior,
hostile same-user manipulation and OS-vault roundtrips are not qualified here.

## Integration and evidence boundaries

### Local configuration recovery transaction (V07a)

The explicit `.martlet-config` output is a bounded 256 KiB format-1 JSON
envelope, not a ZIP/support bundle. Its deterministic manifest records
`Martlet.Configuration`, producer assembly version, minimum reader format,
settings schema, source profile UUID, snapshot UUID, UTC creation time and
source SHA-256. `settings_bytes` is base64 of the **exact** <=128 KiB validated
v1-v6 settings file (base64 is not encryption). Envelope SHA-256 covers the
canonical serialized manifest, including the payload; source SHA-256 covers
the original bytes. A fixed manifest serializes deterministically, but new
snapshots intentionally have new identifiers/times. Integrity detects damage,
not hostile modification or publisher authenticity. Unknown/duplicate fields,
invalid encoding, oversized data and unsupported application/schema versions
are refused, including inside the decoded settings payload.

Creation holds the existing settings writer lock, validates current settings
and rechecks the revision before a create-only staged/flush/rename to the
chosen local destination. It does not copy an unlocked live file, read a vault,
walk directories or include the opt-in support journal. Route/device IDs and
configuration are personal; these LOCAL backups are neither encrypted nor
sanitized diagnostic exports. Version 3 includes named persona text and response-style weights; v1/v2
snapshots contain no persona data. There is currently no persisted F5 voice,
downloaded model to back up. Memory settings are included, but the separately
owned fact store and its exports are never included. Secrets, transient text/audio, environment,
arbitrary files and diagnostic logs are excluded.

Restore requires an **existing valid same-profile** v1-v6 destination. Memory is
always forced OFF for review with a fresh revision: v4+ sources retain their path policy,
older sources preserve the current path policy, and no fact store is opened.
Missing,
malformed, inaccessible or newer destination files are never overwritten as a
repair shortcut. Foreign profile IDs are not remapped. Preview generates a
private immutable candidate byte array and displays its entire JSON, SHA-256,
source digest, destination path/profile, expected revision and exact changes.
Confirmation is default-No and one-use, bound to that plan. Replaced/modified
source bytes or stale destination settings invalidate it. Commit pins the
source with a read-only sharing handle and verifies its frozen digest; reread
bytes are compared only, never substituted for the reviewed candidate.

Imported v3 personas/styles remain inert preferences. An older v1/v2 source
preserves the current v3 personas rather than inventing or erasing settings it
could not have contained. Imported route/model/voice and audio choices remain useful **inert preferences**:
fresh configuration revisions, cleared destination `Consent`/`CredentialId`
and audio `Checkpoint`, and setup returns to Destinations. CURRENT legacy
references and pending removals are preserved; current active OpenAI keys become
pending removals. Current gateway bindings are preserved separately as described
above, not queued for deletion. Imported legacy/active/retained/pending IDs never
become live authority.
If detaching current keys would exceed sixteen pending removals, preview refuses
with an explicit Setup cleanup remedy; it never drops a reference or deletes a
key to make space. Subsequent native removal still requires Setup's exact
current revision, writer lock and separate consent. No capture, logging,
provider call, key read/write/delete or device qualification is restored.

Commit uses the same writer lock and atomic-write helper as ordinary saves.
It first creates and flushes a **new create-only**
`settings.recovery.<uuid>.bak` containing exact current raw bytes, then stages,
flushes and atomically replaces `settings.json`, checking the source/current
revision again just before replacement. Every pre-restore snapshot is retained,
even after a failed replacement; historical `settings.v1.*.bak` files are never
overwritten or pruned. These raw originals remain evidence for compatible
manual recovery/V07b, not importable envelopes. No settings schema downgrade or
old executable activation is offered.

Failed/canceled staging removes only its uniquely owned temporary file. Cleanup
failure retains that exact path and blocks new recovery actions until explicit
retry; the app-lifetime owner survives presentation close/reopen and holds the
shared effect slot through real IO and cancellation callbacks. Five seconds
ends UI observation, not IO ownership. Main Exit waits for recovery ownership/
cleanup, rather than killing a still-running transaction. A crash can leave
owned `.tmp`/`.bak` evidence; launch does not scan, delete or resume it. Preserve
it for manual inspection with the app closed. No multi-step native credential
transaction is introduced, so no second settings journal/authority is needed.

The tested filesystem scope is cooperating writers, bounded reads, flushed
temporary files, create-only same-directory rename and `File.Replace` on the
developer test filesystem. A failure before replacement leaves original bytes;
after successful replacement the original is retained in the prior snapshot.
There is **no filesystem/power-loss durability guarantee**, directory-fsync
claim, cross-process hostile-editor protection or clean-VM qualification.
Physical disk-full/power-cut/N-1/N/OS-vault/real-user trials remain NOT RUN;
controlled IO faults and fake-native/WPF evidence are not substitutes.

`SetupStatus.From` emits only typed checkpoint/role booleans and a pending
count. Desktop/Doctor reports exclude route IDs, origins and credential IDs,
retain the original twelve probes and exit-code semantics, and never turn
saved metadata into provider readiness.

V02b adds the explicit local audio actions below, not physical qualification or
novice evidence. V01b learned VAD/endpointer remains separate. V04b now bridges
`ICredentialStore` to the provider credential source only after explicit fresh
bounded per-turn authorization, matching role/model/origin, request IDs and
limits in its separate app-owned controller. The original V02a slice did not
register adapters. V03 live capability/access evidence and budgets require separate
authorization; saved setup alone cannot issue a request.

Ordinary regression tests exercise the production settings/transaction service
and actual Windows wrapper with an injected native boundary, never the real OS
vault. Actual WPF automation exercises no-key setup, migration, save/exit,
resume/Back and consent invalidation. Separate Windows tests exercise actual
`SetupWindow` events on a WPF dispatcher with blocking fake native Read/Write/
Delete and initial-load/save boundaries: heartbeat, duplicate rejection,
Cancel/Close, timeout, late-result suppression, retained locks/leases/recovery
markers and sanitized failures. No real vault is used by these tests.
Native PInvoke is compiled, **not OS
roundtrip qualified**. Real Credential Manager write/read/delete, real
microphone/speaker, live API, clean VM lifecycle, signing, release and novice
gates remain **NOT RUN**. Do not run a real-key or OS-vault integration test in
the shared Windows profile without separate permission.

## Explicit local audio setup (V02b)

Open **Microphone and speakers** from Companion › *Listening* or *Voice* (or
the Devices map). This is a separate local device-test surface, **not** a
first-conversation wizard, AI/VAD test, provider capability check, or
permission to listen on launch. Opening it loads settings and lists the
microphones and speakers Windows reports, by name only. It does not open
devices, sample a microphone, play output, resolve a key, contact a provider,
or write settings. Status remains usable without audio hardware.

Martlet assumes the Windows default (or chosen) microphone and speakers work.
A device needs attention only when it is missing right now: no microphone or
speakers are found, or a chosen device isn't connected. Home, Companion ›
*Listening* and *Voice*, and this window check which devices are plugged in
and say so; a test is optional and never required to use a device.

The window shows one card per device: a picker, one test button and a
plain-language state (*Ready*, *Checking*, *Not found*, *Testing*, *Working*,
*Needs attention*, *Did you hear it?*) with the next step in one sentence.
*Done* is the primary button. The exact technical evidence (whole-test level,
last result with its remedy, test history and worker ownership) and
*Troubleshooting* sit under **Details**.

| Action | Actual effect and evidence |
| --- | --- |
| Device listing | Automatic off-dispatcher Windows capture/render enumeration, at most 128 of each, when the window opens and again each time a device list is opened, so a device plugged in meanwhile appears without a button. Uses the existing capture discovery factory and pinned NAudio render API on the shared setup worker, waiting for any save. Lists are local snapshots, not privacy permission, working capture, or audible playback. No background discovery loop. |
| Microphone choice | Fixed opaque endpoint identity, or deliberate `FollowDefaultOnNextPress`. Default is resolved on each newly authorized test; a mid-capture default/input/property/format change stops and discards rather than reopening. |
| Output choice | Fixed identity, or `DefaultAtStart`. The actual output binds once; active playback never follows a changed default or falls back. |
| **Test microphone** | Default-No confirmation names the selected configuration and local-only boundary. Fresh IDs/epoch; actual `MicrophoneCapture`, at most 5 seconds with at most 20 seconds of consent including cleanup/transfer. The live meter shows transient PCM levels; the final result measures peak, RMS and level coverage over the completed normalized PCM lease without copying it. No frames, insufficient frames, low or intermittent level, device failure and cancellation are distinct non-pass results. This is a local level advisory, not speech/VAD, permission, audio quality or device readiness. |
| **Play test sound** | Separate default-No confirmation for the existing `SyntheticTone`: 200 ms faded 440 Hz, low amplitude, not speech/TTS. Actual `PcmPlaybackSink`, five-second deadline, exact sample count, selected output only. No system/app volume or default changes. |
| **Yes, I heard it** | Shown and enabled only for this window's successfully drained current output test, never from a loaded checkpoint. Explicit human confirmation is separate from device consumption. Choice changes, interruption, deactivation and reopening invalidate confirmability. |
| Automatic save | Picking a device, a finished test and *Yes, I heard it* each save atomically with the loaded optimistic revision, one save at a time; a pass records a historical checkpoint and any other finished result clears it, while a stopped test changes nothing. *Done* waits for a pending save. Saving does not authorize a later test. **Reload** appears after a conflicting or interrupted save; files are never reset to make setup succeed. |
| **Stop test / Done / Close / deactivation / session lock** | Cancel only the currently owned test handle (never an in-flight save), clear unclaimed capture, and stop observation. Unlock/reopen never rearms. No simultaneous input/output. |

For a local **historical** `SamplesReceived` checkpoint, a completed and released
microphone test must have at least four seconds of canonical PCM, whole-test
RMS at least 1% of full-scale and at least 1.25 seconds of samples at or above
that level within the at-most-five-second capture. This conservative advisory
avoids mistaking a click followed by silence for sustained input; a quiet or
brief valid phrase can fall below it. It does **not** gate typed or push-to-talk
use or establish future Windows permission, audible output, speech, VAD or
physical microphone quality. Zero-valued PCM is different from receiving no
frames, but neither creates a checkpoint. For low level, review the intended
input, hardware mute, Windows input level and microphone privacy/desktop-app
access; for no or too few frames, check the connection or changed default,
reopen the device list and select the intended endpoint. Lost, unavailable,
busy, denied and changed devices retain their specific error remedies; there
is no automatic endpoint fallback or retry. Each retest needs fresh permission.
If input remains unavailable, typed conversation can still be used without
microphone access. These local tests never upload audio.

Capture PCM is never copied to a recording, file, transcript, provider, or
export. The one completed lease is taken only to measure metadata on its owned
normalized PCM, then immediately disposed/zeroed; cancellation/failure also
discards unclaimed PCM. Live meter progress from an interrupted or failed
action cannot become a final level result or checkpoint.
Capture retains its original caller-token, absolute UTC and original monotonic
consent safeguards through transfer. The UI service additionally accounts for
the action's elapsed time before dispatch. Output's adapter wrapper links
native/caller cancellation and directly checks the original token and both
consent clocks before native open/start/write/padding boundaries. An already
in-flight native call cannot honestly be retracted.

### Shared ownership, responsiveness and metadata

`AudioSetupWindow` uses the existing app-shared `SetupOperationRunner`, also
used by credential setup. `AudioSetupService.Start` offers
an action with a snapshotted choice and per-action permission; it returns
`AudioSetupOperation` with metadata-only status and the original worker handle.
`Stop()` targets that handle, not sink-wide current playback. Workers never
capture the observing audio window. Native enumeration and all device work run
off the dispatcher. `IAudioDeviceCatalog`, capture/playback factories and
`IAudioSessionEvents` are controlled-device test seams, not production fakes.

Find/settings observations expire after five seconds; test observations after
nine seconds. Stop/Close do not await blocked callbacks on WPF. A timed-out or
closed observation discards late lists/events/results and leaves the shared
worker reserved until actual native **and cancellation-handler** release.
Failed native release remains quarantined; creating a replacement factory is
not a recovery path. Output tone failure with unproven release conservatively
holds the shared slot because its frozen public terminal cannot prove a late
release. Close Martlet if an owned driver never returns. A terminal report or
responsive window is not a measured physical Stop guarantee.

If an output Open returns only after its five-second authorization expired,
the owned wrapper rejects it before Start/write and disposes the returned
device. Only the wrapper's **own expiry rejection plus proven completed
cleanup** allows the UI service to report `DeadlineExceeded`, release the
shared slot and accept a fresh explicit action. The existing sink remains
conservative about an unreturned/unknown Open; its lifecycle is not relaxed.
The service also drains the sink's bounded event stream through completion,
which follows actual device-token cancellation callbacks, before releasing
ownership. A blocked callback can therefore outlive successful device Dispose
without making the slot reusable. Failed disposal, pending native work and
arbitrary native `AudioPlaybackFailed`/`DeadlineExceeded` errors are not
successful-cleanup evidence and remain quarantined.

Optional v2 `audio` contains versioned `AudioSettings`, two bounded
`AudioChoice` values and optional `AudioCheckpoint` values. Existing v1-v4 files
without audio still load unchanged. v1 explicit save retains the reviewed exact
original snapshot; profile identity, legacy credentials, role routes/consents,
pending removals and setup navigation survive. Unknown audio versions/fields,
corrupt/oversize settings and stale revisions are rejected without rewriting
original bytes. No package, project graph, capture contract, or playback
lifecycle changed.

An endpoint ID is at most 1,024 characters; a friendly label at most 256.
Null identity deliberately means the direction-specific default policy.
Changing identity or label renews that choice's configuration revision and
invalidates its corresponding checkpoint. Save rejects changed identities
reusing a prior revision. A checkpoint records the matching configuration
revision, UTC test time and `SamplesReceived`, `ToneDrained`, or `Heard`.
These are **local historical observations**, not provider evidence or
evergreen device readiness. Default-policy history qualifies only that action's
policy, not today's possibly different default endpoint. Reopened/saved
evidence is always labeled stale for current readiness; it grants no capture,
playback, cloud or vault permission.

`AudioSetupDiagnostics` supplies shared stage descriptions and specific
privacy/busy/missing/changed/format/cleanup/volume remedies. The Desktop local
audio section is separate from the real-provider pipeline.
Doctor's existing `settings.load` metadata includes only selection/default
booleans, historical times/outcomes and `LocalObserved`/`LocalUserReported`
provenance: never endpoint IDs, friendly labels,
configuration IDs, user paths, or PCM. The existing twelve probes, exit codes
and automatic read-only effect policy are unchanged. No new CLI device command
pretends to be implemented.

### V04b seam and remaining gates

V04b may later map the saved choices to input/output policies, but must obtain
fresh bounded per-turn permission and coordinate runtime ownership with these
local actions. It must use reviewed runtime/provider boundaries and a separately
authorized vault-to-provider bridge. This slice references no Conversation or
Providers library, resolves no keys and installs no real provider adapter.
V01b learned VAD/endpointer, acoustic tail/AEC, real AI speech, price/quality and
paid access remain separate.

Controlled-device regressions exercise actual WPF button events, metering,
exact tone bytes, confirmation/invalidation, settings preservation/conflict,
native open/read/disposal/callback blocking, heartbeat, timeout, bounded close,
reopen quarantine, late generations, session-lock signals, and owned-HWND WPF
deactivation. Core tests cover bounds, strict schema, revision binding,
sanitized history and exact v1 snapshots. The executable audio-OFF smoke opens
Audio setup and reads never-tested status **without pressing Find or Test**;
existing owned-HWND Alt+F Stop assertions remain strict and unchanged.

**NOT RUN / not passed:** physical endpoint enumeration/capture/audibility,
USB unplug/reconnect, Bluetooth profile/default/format churn, Windows privacy
denial and exclusive-device matrix, acoustic tails/physical Stop latency,
novice/accessibility witness sessions, clean Windows/installer lifecycle,
signing and G2. No OS-vault roundtrip, live provider/key/model request, cost or
voice-quality test was run. Passing a controlled PCM meter test is not physical
device qualification or learned VAD evidence.

## Voices (F5)

Martlet's voice engines copy a voice from a short recording; nothing is
trained. There are no built-in voices: **Companion > Voice > Voices** lists your
voices, with **Play**, **Use** and **Remove** on each. A new list starts with
seven starter voices. The four **cute voices** come first: two LibriVox readers
voicing Anne of Green Gables' excitable young heroine, each as read and lifted
to a high, anime-like pitch; Martlet starts with *Annie (cute anime girl)*. Then
come LJ Speech (a female narrator) and two CMU ARCTIC speakers (US female and US
male). All are free to use and share: the cute voices and LJ Speech are public
domain (CC0 or public domain) and CMU ARCTIC is free for any use. Their sources
and notices are in `notices\F5-Voices-NOTICES.txt`. Remove any you don't want;
they don't come back. **Add a voice...**
takes a recording of 1 to 30 seconds (5 to 12 seconds of clear speech works
best) in almost any audio format, such as MP3, M4A/AAC, WAV, FLAC, WMA, AIFF,
OGG/Opus (voice messages, for example) or the sound of an MP4, MOV or MKV video,
plus its exact transcript, whose voice it is and your rights confirmation.
Martlet decodes Ogg Vorbis and Ogg Opus files (.ogg, .oga, .opus) itself and
Windows decodes the rest on this PC, and the line under
the file says what Martlet found. Martlet keeps a mono 16-bit PCM WAV: a WAV
already in that form (16/22.05/24/44.1/48 kHz) is kept exactly; anything else is
mixed to mono and, at another sample rate, resampled to the next of those rates
(at most 48 kHz; an Opus file is kept at the rate it was recorded at, so a 16 kHz
voice message stays 16 kHz). **Play** plays that WAV. A longer recording is refused rather
than cut, since the transcript must match it. With Parakeet downloaded
(**Companion > Listening**) or Listening on a paired host, Martlet fills in the
transcript as soon as it has read the recording; check it and fix anything it
misheard. **Add another recording** adds
more recordings of the same voice (up to 10, 30 seconds in all), each converted
the same way and then joined. Only the WAV is stored and shared
with your other computers. The new voice is used right away. Up to 32 voices
are kept.

Earlier versions included F5-TTS's English example clip. It is no longer
included because where its recording comes from couldn't be confirmed, and it
never joins your list. A route still speaking with it moves to your chosen (or
first) voice. Voices earlier versions offered and you used stay in your list.

Your voices are the same on all your computers. The list, the voice you chose
and every recording are shared with your paired Martlet computers (each paired
host keeps a copy, as for [who does what](CLUSTER.md#the-shared-speaking-voices)),
so whichever computer speaks already has the recording and a reply sends only
its transcript, and any of your PCs can be the companion with the same voices.
Martlet stores its own copy of each recording under `f5-voices` in the app data
directory (and on each host beside `host.json`), so the original file can be
moved or deleted after adding it. **Use** switches the voice on all your
computers in one click: when a computer speaks (this PC or another of yours) the
speaking route records the new voice and the next conversation speaks with it;
otherwise it is used once a computer does the speaking. The voice in use cannot
be removed until you switch to another one. Removing a voice removes it on all
your computers, never your original file. The status under the list says with
how many computers your voices are shared.
