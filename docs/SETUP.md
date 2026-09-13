# Resumable setup and Windows credentials (V02a)

**Configuration only, not a working voice connection.** Open **Setup / resume**
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
Screen and persistent memory remain OFF. No health test, account login,
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

`AppSettings` accepts the original strict v1 schema and the new strict v2
schema. Profile schema stays v1. v2 adds versioned `SetupSettings`: a bounded
checkpoint, up to three `SetupRoute` values and at most sixteen pending owned
credential removals. Settings contain no secret or transcript. Unknown fields,
invalid states, newer versions, malformed encodings and oversize files are
rejected without rewriting their original bytes.

`SetupSettings.Begin` only prepares an in-memory edit. Explicit v1-to-v2 save
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

`SetupStatus.From` emits only typed checkpoint/role booleans and a pending
count. Desktop/Doctor reports exclude route IDs, origins and credential IDs,
retain the original twelve probes and exit-code semantics, and never turn
saved metadata into provider readiness.

V01b/V02b must integrate explicitly permissioned input/output selection,
real local qualification and accessibility/novice evidence. V04 must bridge
`ICredentialStore` to the provider credential source only after explicit fresh
bounded per-turn authorization, matching role/model/origin, request IDs and
limits. This PR references no provider library and registers no STT/LLM/TTS
adapter. V03 live capability/access evidence and budgets require separate
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
