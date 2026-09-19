# Explicit Desktop API conversation (V04b)

**Internal functional integration, not account/device/release qualification.**
Open **real API conversation** from Desktop. This is separate from **Try fixture
(audio OFF)**, which still works without settings, credentials, microphone,
network or GPU. Opening either conversation or setup never resolves a key,
enumerates devices, records, plays, discovers a model or makes an API request.
Ordinary Doctor/status remains read-only; `self-test` is still a fixture command,
not a live connection test.

## First configured action

1. In **Setup / resume**, choose the named OpenAI API profile. Apply explicit
   supported model IDs, store each role's key in its scoped Windows vault target,
   then review that role's destination choice again (changing a key invalidates
   the choice). Save the checkpoint. Do not put keys in model fields or files.
2. LLM currently supports `gpt-4.1-mini-2025-04-14`. STT supports `gpt-transcribe`,
   `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`,
   `gpt-4o-mini-transcribe-2025-12-15`, or `whisper-1`.
   Optional TTS supports `gpt-4o-mini-tts-2025-12-15`, voice `alloy` or `coral`,
   raw mono 24 kHz PCM16. These are adapter allowlists, **not verified account
   access, quality recommendations or automatically chosen defaults**.
3. For PTT or voice output, explicitly save the intended policies in **Audio
   setup (local only)**. Fixed input/output is recommended for predictable
   routing. A default-input choice resolves again only on a fresh press and
   stops on mid-capture change. Default output binds once and never moves an
   active stream. Local tests require their own permission and are not mandatory
   live-readiness gates.
4. Return to the live window and **Reload saved choices**. Text-only is the
   default: it needs only the configured LLM role, does not make TTS requests
   and does not open an output device. Review the actual selected model,
   origin, output and bounds in the displayed envelope.
5. For typed input, enter text, explicitly authorize the next action and select
   **Send typed text**. For PTT, additionally permit local microphone capture
   and separately permit the STT upload. Hold PTT with the mouse or Space key,
   then release to send. Accessible Invoke starts a bounded recording; **Finish
   recording and send** seals it. **Stop** always discards instead of sending.
   Stop stays above the scrolling form, including at the minimum window size.
   **Escape** anywhere in the conversation window performs the same Stop;
   it does not close the window or submit typed text/audio.
6. Real normalized response text streams into the answer field. Refusal has a
   separate field and is never ordinary speech. Selecting voice explicitly
   authorizes only bounded eligible response segments for this same action.
   Progress reports actual runtime/PCM accounting, not an animated success.

STT receives only the selected microphone's completed bounded utterance. LLM
receives the typed text or that final transcript, with no stored conversation
history or personality in this slice. TTS receives only eligible generated
segments. All provider routes have the fixed HTTPS origin
`https://api.openai.com`; there is no custom endpoint, model discovery,
fallback provider, retry loop or hidden continuation.

**PTT and explicit typed controls only.** Learned VAD, acoustic wake words,
automatic name/group listening and unsolicited participation are OFF. Transcript
words cannot grant trusted-control provenance. Unknown STT confidence stays
unknown. A PC microphone does not automatically capture remote participants or
game/call audio. Capturing other people requires their permission.

## The accepted envelope

| Boundary | Hard app choice for each explicit action |
| --- | --- |
| Overall permission | Original monotonic and absolute expiry within 150 seconds, including scheduling/capture/authorization; never restored or extended |
| Capture | At most 25 seconds / 800,000 bytes, canonical mono 16 kHz PCM16; original capture permission at most 30 seconds including cleanup and transfer |
| STT | At most one request, 800,044 WAV bytes, 30-second request, 4096 transcript characters |
| LLM | At most one request, 4096 user characters / 16,384 UTF-8 bytes, 16,640 input-token reservation, 256 requested output tokens, 16,384 response characters, 45-second request |
| Conversation runtime | At most 90 seconds; existing bounded two-segment pending queue, one active TTS/playback segment |
| TTS | At most eight requests, 1536 input UTF-8 bytes each / 12,288 total; 10 seconds / 240,000 samples reserved per request, 80 seconds / 1,920,000 samples total; at most 20 seconds per request |
| Content and timeline | Current bounded input/transcript/answer/refusal in memory; 32 metadata timeline entries, existing bounded engine event rings; no audio/transcript files or ordinary content logs |

These are admission and request limits, **not a measured latency promise or a
currency/invoice ceiling**. Input-token reservations are conservative local
units, not measured upstream usage. Price, quota, taxes, model availability,
final cost and other clients' activity are **UNKNOWN**. A failed/canceled
upload or request may already have cost money. A ChatGPT subscription does not
prove API quota. Review official [pricing](https://openai.com/api/pricing/) and
[data retention](https://platform.openai.com/docs/guides/your-data) separately.
No-retention or free-service promise is made.

The next action requires a new acceptance; the checkboxes are cleared on use
and on configuration/output/lifecycle changes. Internal TTS callbacks do not
prompt for every sentence: they derive one-use permissions and exact
`OperationBudget` reservations only for actual segments inside this accepted
envelope. The runtime enforces its own original stage/turn clocks as well.
Limits can end a response before all possible segments; unused reservation
is not silently recycled.

## Stop, ownership and privacy

`LiveConversationController` is app-lifetime composition, not another inference,
capture or playback implementation. It reserves the existing
`SetupOperationRunner` for the entire live action. Credential setup, local audio
tests and the fixture share that same slot. There is no pending-turn queue:
busy input is rejected and requires a fresh deliberate action after cleanup.
An old Stop/release handle cannot cancel a newer action.

`ConversationAuthorization` validates the exact snapshotted settings revision,
profile, role, origin, model/voice and opaque credential reference before and
after native retrieval. It resolves a fresh secret only after the matching
one-use role permission/reservation exists. Production uses
`WindowsCredentialStore`; no placeholder account, environment-variable key,
exported secret or fake-native production default is supplied. Key readability
is not API validity. Native/managed `SecretLease` values and returned provider
credentials are disposed on all owned exits. The provider's header interface
requires a managed string: neither HTTP-internal copies nor garbage-collected
strings can be promised securely erased.

PTT uses the existing `MicrophoneCapture`, fresh IDs/epochs and original capture
expiry through `TakeUtterance`. The transferred lease and intermediate PCM copy
are promptly cleared; `BoundedWaveAudio.FromPcm` supplies the existing validated
canonical WAV, not an alternate codec/transcription stack. Its bounded private
managed WAV is dropped after the owned request; no secure erasure of provider/
OS/HTTP copies is claimed. No-speech, failed or canceled STT never dispatches LLM.

`ParticipationPolicy` evaluates the exact typed/final-STT content before runtime
Start. Commit and Start are serialized with current pause/consent state. The
final transcript intent is issued at its actual receipt time, not by renewing
an intent that waited through STT. Policy consent signals are not provider
permission. The policy lease remains owned until actual runtime
`OwnershipRelease`, not merely `Completion`.

Stop, pause, mute, losing the held control, deactivation, session lock and
Close revoke pending permissions and stop only this operation. Unlocking,
unmuting or reopening never rearms. Native/credential/HTTP work and cleanup
run off the dispatcher; the UI remains responsive. Noncooperative native work
or callbacks can outlive a timeout or closed observer. The shared slot remains
reserved; failed cleanup is quarantined rather than replaced with a fresh
factory. Closing the main window exits the app, not a background tray listener.
This is not a measured 250 ms physical-stop guarantee.

The fixed **Stop / revoke (Esc)** control also clears accepted but unused
action/capture/upload permissions. Escape works from the typed input, response
fields and held PTT control. Releasing Space after Escape cannot send that
discarded recording or rearm PTT. Stop during settings loading or a slow worker
requests cancellation without releasing the shared ownership slot early.
Partial response text remains available; stopped speech is not replayed.
The shortcut is local to this conversation window, not a system-wide hotkey.

The STT adapter's backwards-compatible two-token overload retains the original
caller and app-operation tokens independently through credentials, serialization,
send and result acceptance. A blocking newer cancellation callback cannot hide
either original cancellation flag. LLM/TTS keep their reviewed original
caller/enumerator guards; no application bridge substitutes linked-only
permission for those original sources.

## Troubleshooting

| Visible condition | Meaning and next action |
| --- | --- |
| Setup required / unsupported role | Review the displayed exact catalog IDs; store each role key, reselect its destination and save. No automatic fallback or capability request occurs. |
| Configuration changed | Loaded revision/role/key/output no longer matches this action. Stop, Reload and explicitly approve the new selection. External profile editing/copying while running is unsupported. |
| Credential missing / access denied | Review the signed-in Windows user and selected role reference. Explicit setup retrieval can check local readability only. Do not elevate or disable protection. |
| STT no speech | No LLM/TTS followed. Review intended input and local microphone test; start a fresh PTT action or type instead. Silence samples are not VAD evidence. |
| Mic access/busy/lost/default-change/format | Use Audio setup's specific privacy/device remedy. No automatic recapture, loopback or device fallback. Typed input remains available. |
| Provider auth/model/quota/rate/network failure | Inspect the stable provider code; review account/model availability and current limits outside Martlet. A failed request is not a safe automatic retry. |
| Refused / partial answer | Refusal is separate from answer text. Partial answer remains visible; unfinished/unsupported speech is discarded, not replayed. |
| TTS/output failed | Read the response text. For the next new action choose text-only, or review the selected output/model/voice. Earlier speech may have played. |
| Cleanup pending / quarantined | No new effectful action may take the slot. Wait for actual release; close Martlet if the native worker never returns. Do not start a replacement factory to evade quarantine. |

Submitted samples, device-consumed samples and observed drain are different
measurements and **none proves that a person heard sound**. "REAL provider
response" describes this production attempt, not readiness inferred from a
catalog, saved checkpoint or unit test. In-process fixtures remain visibly
`FIXTURE ... NOT inference`.

## Fixture-safe developer reproduction

Use SDK 10.0.401 and the committed normal locks:

```powershell
dotnet restore Martlet.slnx --locked-mode --artifacts-path $artifacts
dotnet build Martlet.slnx --no-restore -c Release --artifacts-path $artifacts
dotnet test Martlet.slnx --no-build -c Release --artifacts-path $artifacts
.\scripts\Smoke-Desktop.ps1 -ExecutablePath "$artifacts\bin\Martlet.Desktop\release\Martlet.Desktop.exe"
```

On the pooled-drive developer host, use one unique session-owned **C:** artifacts
directory consistently for restore/build/test, `CI=true`, process-only SDK PATH/
DOTNET_ROOT, own CLI home, telemetry off and ASP.NET certificate generation
disabled. Do not build in the primary checkout or change test/SDK pins.
Dedicated provider/runtime/policy project commands remain available for focused
local runs even though their projects are now also in the root solution.
Hosted validation is removed under the
[local-only repository policy](../README.md#local-only-validation-policy).

`LiveConversationTests` drives actual WPF controls and the app controller,
Windows credential wrapper with injected fake native access, selected capture,
STT serializer/parser, participation policy, runtime LLM/TTS parsers and real
PCM sink with controlled devices. There are no real accounts, vault entries,
recordings, physical endpoints or billable requests in these tests. Native
Desktop smoke opens the live surface without a profile/key, requires Send/PTT
disabled and voice/permission OFF, then exercises existing no-key setup and
offline fixture behavior. Package smoke launches actual self-contained
Desktop/Doctor apphosts; it does not run the installer.

In-process WPF regression cases exercise fixed Stop bounds and hit testing at
minimum/default window sizes and top/middle/bottom scroll positions, routed
Escape from input/response/PTT, discarded capture and late Space release,
controlled playback cleanup, unused consent revocation, and retained ownership
during blocked settings/fake-vault work. These are managed UI and controlled
HTTP/audio/vault evidence, not physical keyboard/device or apphost qualification.

## Separately authorized manual qualification (NOT RUN)

An owner must separately authorize the account, data, audience, device and cost
before a real trial. Do not send a key to a developer/chat or infer permission
from this checklist.

1. Record exact internal build/OS and intended input/headset; review publisher/
   installer trust first. Do not bypass Windows protection for unsigned artifacts.
2. Using the intended signed-in user, explicitly store/check the real role keys;
   record only outcomes, never key values or exported vault contents.
3. Review current provider pricing/retention/account/model access and each
   displayed action envelope. Set appropriate provider-side spending controls
   where available; do not assume a guaranteed hard cap.
4. Confirm local microphone/output selection with separate local test permission.
   Trial typed text-only first and confirm no TTS/output-device activity.
5. Separately select voice, approve one short typed action, inspect response/
   playback progress and have the listener confirm whether it was heard.
6. With permission from all audible people, approve one short PTT action and
   inspect transcript -> policy -> LLM -> TTS -> selected output. Record exact
   stage outcomes, unknowns, real charges if known and observed quality.
7. Exercise Stop/revocation, lock/deactivation, missing/changed devices and
   typed fallback. Do not replay partial speech automatically or promote one
   successful run into a full device, latency or group-listening qualification.

Actual API account/model quality/cost/performance, real OS-vault roundtrip,
physical audio, full first-conversation novice trial, clean Windows installer
lifecycle, signing, rights and release gates remain **NOT RUN / NOT PASSED**.
V06b adds [local Troubleshooting](TROUBLESHOOTING.md) using the existing Support
engine. Optional typed stage metadata uses a separate bounded worker, not the
conversation effect slot; no input, response or audio enters its journal.
Screen/memory capture, model download, host service/driver change, deployment
or release is not included.
