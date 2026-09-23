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
in the existing Desktop. Fixture is the safe first-run choice; it needs no
account, key, terminal, device, network or saved profile. Existing offline
fixture, refusal, Stop and status actions are unchanged. Opening setup and
ordinary Desktop/Doctor status are read-only: no credential lookup, device
enumeration, capture, provider discovery or network requests.

Choose **Named OpenAI API profile** to save role-specific routes without making
an inference request. The only approved origin is `https://api.openai.com`;
there is no editable endpoint or generic `/v1` compatibility mode. Enter the
actual upstream model ID, and an upstream voice ID for TTS, explicitly.
Internal aliases `openai-stt`, `openai-llm`, and `openai-tts` are Martlet policy
identifiers, not upstream model names. No model/voice is silently selected or
discovered, and saving does not prove that an entered ID exists or is accessible.

For each role, review the displayed boundary and apply the route:

| Role | Disclosed data destination |
| --- | --- |
| STT | Microphone audio to OpenAI; even an utterance later suppressed can already have been disclosed and charged. |
| LLM | Transcript and conversation text to OpenAI. |
| TTS | Response text to OpenAI; the returned voice is generated, not a human recording. |

Cloud use can cost money. Current price, quota, API-key validity and model
access are **unknown**, not free or verified. Official
[API pricing](https://openai.com/api/pricing/) and
[data retention policy](https://platform.openai.com/docs/guides/your-data)
are linked in the UI (copy reviewed 2026-09-13; no rates cached). Opening a link
requires an explicit browser action. A ChatGPT subscription is not API quota.
Screen remains OFF; [local memory](MEMORY.md) is separately OFF by default,
never enabled by Setup or an installation plan. No health test, account login,
provider listing or billable probe is run.

Back/Next and the tabs navigate Choice, Destinations, Credentials and Review.
**Apply** commits route fields to the working checkpoint; **Save checkpoint**
or **Save and exit setup** persists it atomically. Unsaved route fields block
save until applied; changing the selected role discards unapplied fields with
a visible explanation. **Reload** explicitly discards unsaved edits. The
keyboard-focusable status remains available at every step. Missing roles,
consent, key references and audio qualification are explained instead of
displaying a pretend completed voice setup. Reopening resumes the saved step.
Switching to fixture preserves stored API routes/keys but does not use them.

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

`AppSettings` accepts strict schemas v1-v5. Profile schema stays v1.
v3 adds companion personas/styles; v4 adds separately consented OFF-by-default
local-memory enable/path policy, not facts. v2 adds versioned `SetupSettings`: a bounded
checkpoint, up to three `SetupRoute` values and at most sixteen pending owned
credential removals. Settings contain no secret or transcript. Unknown fields,
invalid states, newer versions, malformed encodings and oversize files are
rejected without rewriting their original bytes.

`SetupSettings.Begin` only prepares an in-memory edit at the current schema.
Explicit migration from v1-v4 preserves the original bytes; a v1 save
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
configuration, not runtime qualification. The current Desktop still offers
Fixture/OpenAI setup only and refuses self-host dispatch. Actual accessible
self-host Setup, typed/PTT/optional-TTS routing and the expanded published
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
v1-v5 settings file (base64 is not encryption). Envelope SHA-256 covers the
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

Restore requires an **existing valid same-profile** v1-v5 destination. Memory is
always forced OFF with a fresh revision: v4 sources retain their path policy,
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
The executable smoke sends the existing Stop access key only to its verified
owned HWND, after observing an active/cancelable fixture. This avoids an
observed two-second UIA Invoke RPC delay on the existing two-second deadline
scenario, without changing fixture timing or accepting ordinary completion.
It requires a finished canceled outcome, empty text/queue and fresh subsequent
IDs/output; no global keyboard input or direct handler invocation is used.
Native PInvoke is compiled, **not OS
roundtrip qualified**. Real Credential Manager write/read/delete, real
microphone/speaker, live API, clean VM lifecycle, signing, release and novice
gates remain **NOT RUN**. Do not run a real-key or OS-vault integration test in
the shared Windows profile without separate permission.

## Explicit local audio setup (V02b)

Open **Audio setup (local only)** from Desktop. This is a separate local
device-test surface, **not** a first-conversation wizard, AI/VAD test, provider
capability check, or permission to listen on launch. Opening it only loads
settings. It does not enumerate endpoints, open devices, sample a microphone,
play output, resolve a key, contact a provider, or write settings.
Status and the audio-OFF fixture remain usable without audio hardware.

| Action | Actual effect and evidence |
| --- | --- |
| **Find devices** | Explicit off-dispatcher Windows capture/render enumeration, at most 128 of each. Uses the existing capture discovery factory and pinned NAudio render API. Lists are local snapshots, not privacy permission, working capture, or audible playback. Refresh only by pressing Find again; no automatic discovery loop. |
| Microphone choice | Fixed opaque endpoint identity, or deliberate `FollowDefaultOnNextPress`. Default is resolved on each newly authorized test; a mid-capture default/input/property/format change stops and discards rather than reopening. |
| Output choice | Fixed identity, or `DefaultAtStart`. The actual output binds once; active playback never follows a changed default or falls back. |
| **Test microphone** | Default-No confirmation names the selected configuration and local-only boundary. Fresh IDs/epoch; actual `MicrophoneCapture`, at most 5 seconds with at most 20 seconds of consent including cleanup/transfer. Peak/RMS and counts come only from its real PCM events. Silence with samples is not VAD/no-speech; no samples or device failure is not a pass. |
| **Test output** | Separate default-No confirmation for the existing `SyntheticTone`: 200 ms faded 440 Hz, low amplitude, not speech/TTS. Actual `PcmPlaybackSink`, five-second deadline, exact sample count, selected output only. No system/app volume or default changes. |
| **I heard it** | Enabled only for this window's successfully drained current output test, never from a loaded checkpoint. Explicit human confirmation is separate from device consumption. Choice changes, interruption, deactivation and reopening invalidate confirmability. |
| **Save audio choices and historical checkpoints** | Explicit atomic settings save with the loaded optimistic revision. It does not authorize a later test. Reload is required after conflicting/interrupted saves; files are never reset to make setup succeed. |
| **Stop / Pause / Close / deactivation / session lock** | Cancel only the currently owned test handle, clear unclaimed capture, and stop observation. Unlock/reopen never rearms. No simultaneous input/output and no silent contention with the fixture tone. |

Capture PCM is never copied to a recording, file, transcript, provider, or
export. The one completed lease is taken solely to verify nonempty samples and
immediately disposed/zeroed; cancellation/failure also discards unclaimed PCM.
Capture retains its original caller-token, absolute UTC and original monotonic
consent safeguards through transfer. The UI service additionally accounts for
the action's elapsed time before dispatch. Output's adapter wrapper links
native/caller cancellation and directly checks the original token and both
consent clocks before native open/start/write/padding boundaries. An already
in-flight native call cannot honestly be retracted.

### Shared ownership, responsiveness and metadata

`AudioSetupWindow` uses the existing app-shared `SetupOperationRunner`, also
used by credential setup and fixture actions. `AudioSetupService.Start` offers
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
not a recovery path. Fixture tone failure with unproven release conservatively
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
These are **local historical observations**, not provider/fixture evidence or
evergreen device readiness. Default-policy history qualifies only that action's
policy, not today's possibly different default endpoint. Reopened/saved
evidence is always labeled stale for current readiness; it grants no capture,
playback, cloud or vault permission.

`AudioSetupDiagnostics` supplies shared stage descriptions and specific
privacy/busy/missing/changed/format/cleanup/volume remedies. The Desktop local
audio section is separate from the fixture and real-provider pipeline.
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

## Voice Library: local preparation (VS01)

From the main window, open **Voice Library**. Choose any of the five engines
to inspect its reference requirements, training availability and license
caveats. No models or files are loaded merely by opening or changing engines.
This does not replace the existing API setup described above.

Use **Browse for WAV**, enter a name and matching reviewed transcript, choose
reference or training-material purpose, select speaker rights and explicitly
confirm local storage. **Import local copy** preserves the source and saves
an immutable versioned bundle under `voice-library` in the app data directory.
Accepted input is non-silent mono PCM16 WAV at 16/22.05/24/44.1/48 kHz, up to
64 MiB: references are 1-30 seconds, training material 1-600 seconds. A format
check does not establish speech, single-speaker content or voice quality.
No decoder/model download, conversion, cropping or transcription is automatic.

**Load / reload saved assets** explicitly reads and verifies local copies.
Selecting a saved asset and another engine shows preparation guidance without
changing either the asset or the active conversation. **Remove selected
imported copy** requires confirmation and preserves the original file.
Files are not encrypted by Martlet, not included in settings backup/support
export, and are bounded to 64 assets / 512 MiB. Keep any desired originals.
An interrupted `.pending` file blocks further imports/listing with a visible
repair message; preserve saved `.voice` files and remove only the identified
staging data after confirming no import is running.

Close/Cancel requests cancellation without releasing the shared app worker
early. Other setup/audio/conversation effects cannot overlap this IO.
Normal app Exit also waits for outstanding voice IO/cleanup; after the
operation finishes, Exit again. A crash or forced process termination can
still leave staging data, handled as an explicit recovery condition.
**Installation, worker upload, reference preprocessing, training, A/B speech
previews and applying a self-hosted voice are not implemented by VS01.**
See [VS02-VS06](VOICE_STUDIO.md#delivery-slices-and-acceptance).
