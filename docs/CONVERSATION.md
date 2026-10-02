# Explicit Desktop API conversation (V04b)

**Internal functional integration, not account/device/release qualification.**
**Start talking** on Home opens the talk window: the conversation history, what
you said and a message box, nothing else. How Martlet listens, speaks and sees
is chosen in Companion (Listening, Voice and Vision); the window starts what was
chosen there. Opening setup never resolves a key, enumerates devices, records,
plays, discovers a model or makes an API request. Ordinary Doctor/status remains
read-only and is not a live connection test.

The character is shown and hidden from the main window. Its [feature guide](../src/Martlet.Avatar.Hosting/README.md)
describes separately permitted local renderer inspection and generated-speech
analysis. Opening it is passive; activation is never inherited from conversation
permission or persisted. Only accepted generated TTS PCM is observed, never mic
capture or token arrival. Avatar backpressure, missing actual device clock,
renderer failure and Audio2Face unavailability do not delay or fail voice.
Only explicit A2F mouth/expression mapping is currently wired; alternatives and
other aspects require explicit omission, not automatic fallback.

**Vision** (Companion › Vision, off by default) lets Martlet glance at your
active window, screen or a camera while the talk window is open and occasionally
comment; it needs a Thinking model that can see images. See
[Screen commentary](SCREEN_COMMENTARY.md).

## First configured action

1. In **Setup / resume**, choose the cloud API profile. Apply explicit
   supported model IDs, store each role's key in its scoped Windows vault target,
   then review that role's destination choice again (changing a key invalidates
   the choice). Save the checkpoint. Do not put keys in model fields or files.
2. The OpenAI LLM route supports `gpt-4.1-mini-2025-04-14` and
   `gpt-4.1-2025-04-14`. Alternatively the LLM can use OpenRouter, NVIDIA Build
   or any OpenAI-compatible Chat Completions endpoint with the exact model ID
   you enter (see [Setup](SETUP.md)); a local loopback server may be keyless.
   STT supports `gpt-transcribe`,
   `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`,
   `gpt-4o-mini-transcribe-2025-12-15`, or `whisper-1`.
   Optional TTS supports `gpt-4o-mini-tts-2025-12-15`, voice `alloy` or `coral`,
   raw mono 24 kHz PCM16. Setup copies exact entries from local adapter catalogs
   and refuses unsupported IDs without replacing the prior route. These are
   adapter allowlists, **not verified account
   access, quality recommendations or automatically chosen defaults**.
3. For PTT or voice output, explicitly save the intended policies in **Audio
   setup (local only)**. Fixed input/output is recommended for predictable
   routing. A default-input choice resolves again only on a fresh press and
   stops on mid-capture change. Default output binds once and never moves an
   active stream. Local tests require their own permission and are not mandatory
   live-readiness gates.
4. Open **Start talking**. The destinations were confirmed when each job was
   chosen in Companion, so the window asks nothing more: pressing **Send**
   (or Enter), holding the talk button, or speaking while always listening is
   on is the action. Replies are spoken when a voice is set up and *Speak
   Martlet's replies aloud* is on (Companion › Voice); otherwise they are text
   only, with no TTS request and no output device.
5. Type and press Enter (Shift+Enter for a new line). With **Always listening**
   (Companion › Listening, the default once the microphone is tested) just
   speak; with **Push-to-talk**, hold the talk button with the mouse or Space,
   then release to send (invoking it starts a recording and invoking it again
   sends). **Stop (Esc)** stays in the header at every size: it stops the reply,
   discards a recording instead of sending it, and pauses vision. It never
   pauses always listening, so Martlet doesn't miss what you say next; only the
   **Listening** button pauses it.
   Escape works anywhere in the window and does not close it or send anything.
6. The history shows your messages, what you said (the transcript) and
   Martlet's replies as they stream in. A refusal is shown as such and never
   spoken as ordinary speech; a stopped or failed reply keeps its text with a
   *Cut short* note. Replies are kept short by asking, not by cutting: every
   reply to what you type or say ends its instructions (after persona, lore and
   memory) with a fixed instruction to answer in one or two short sentences at
   most, with no lists, second paragraph or closing offers (longer only when
   you explicitly ask for detail, steps or a list), and to finish its last
   sentence. The max reply length (Companion › Replies, 1,024 tokens
   by default) is only a ceiling against a runaway answer. When a spoken reply
   outgrows the speech budget below, Martlet stops saying it aloud but still
   shows all of it, with an *Only the start was said aloud* note.

STT receives only the selected microphone's completed bounded utterance. LLM
receives the typed text or that final transcript plus the fixed active persona
revision, one weighted response style selected only after participation
accepts the turn, and the fixed reply-length instruction. Persona/style and user input share the existing byte/token
reservation; an over-budget combination is rejected without truncation or a
provider call. Valid legacy v1/v2 profiles upload no implicit persona/style
instruction until settings v3 is explicitly
saved. Up to eight completed explicit exchanges from the prior two minutes may
be supplied from volatile memory; oldest pairs are omitted until the whole request fits
the unchanged budget. Failed/refused/suppressed turns are excluded, and pause,
lock, configuration load/change or closing the talk window clears the buffer;
Stop keeps it, so the conversation continues after an interruption.

[Memory](MEMORY.md) is ON by default (Companion › Memory turns it off). When
on, each explicit typed/PTT/hands-free turn automatically recalls up to twelve
saved facts (best lexical matches for the current input, then the newest) as one
labeled background block inside the same input budget; the full store, path and
consent UUIDs are never uploaded. If the store can't be read, the reply goes
ahead without memory and the status says why. After a completed reply, the
exchange is sent once more, as one extra text-only request, to the same Thinking
model, which picks out lasting facts to save locally (shown under the reply and
listed in Memory). Screen glances are never remembered. The volatile exchange
buffer itself is still not persisted. TTS receives only eligible
generated segments. All provider routes have the fixed HTTPS origin
`https://api.openai.com`; there is no custom endpoint, model discovery,
fallback provider, retry loop or hidden continuation.

**Typed input, push-to-talk or always listening only.** Learned VAD, acoustic
wake words, automatic name/group listening and unsolicited participation are OFF. Transcript
words cannot grant trusted-control provenance. Unknown STT confidence stays
unknown. A PC microphone does not automatically capture remote participants or
game/call audio. Capturing other people requires their permission.

## The accepted envelope

| Boundary | Hard app choice for each explicit action |
| --- | --- |
| Overall permission | Original monotonic and absolute expiry within 150 seconds, including scheduling/capture/authorization; never restored or extended |
| Capture | At most 25 seconds / 800,000 bytes, canonical mono 16 kHz PCM16; original capture permission at most 30 seconds including cleanup and transfer |
| STT | At most one request, 800,044 WAV bytes, 30-second request, 4096 transcript characters |
| LLM | At most one request, 4096 user characters; current user + persona + style + reply-length instruction + bounded explicit history at most 16,384 UTF-8 bytes and 16,640 input-token reservation, 1,024 requested output tokens by default as a ceiling (16-2,048 via Companion > Replies, which also sets optional sampling: temperature, top P/K, min P, repetition penalties and a paired host's context size, each sent only to routes whose API accepts it), 16,384 response characters, 45-second request |
| Conversation runtime | At most 90 seconds; existing bounded two-segment pending queue, one active TTS/playback segment |
| TTS | At most eight requests, 1536 input UTF-8 bytes each / 12,288 total; 10 seconds / 240,000 samples reserved per request, 80 seconds / 1,920,000 samples total; at most 20 seconds per request. Reaching this budget ends speech for the reply, not the reply's text |
| Content and timeline | Current bounded input/transcript/answer/refusal in memory; 32 metadata timeline entries, existing bounded engine event rings; no audio/transcript files or ordinary content logs |

These are admission and request limits, **not a measured latency promise or a
currency/invoice ceiling**. Input-token reservations are conservative local
units, not measured upstream usage. Price, quota, taxes, model availability,
final cost and other clients' activity are **UNKNOWN**. A failed/canceled
upload or request may already have cost money. A ChatGPT subscription does not
prove API quota. Review official [pricing](https://openai.com/api/pricing/) and
[data retention](https://platform.openai.com/docs/guides/your-data) separately.
No-retention or free-service promise is made.

Each message or utterance is its own bounded action. Internal TTS callbacks do not
prompt for every sentence: they derive one-use permissions and exact
`OperationBudget` reservations only for actual segments inside this accepted
envelope. The runtime enforces its own original stage/turn clocks as well.
Speech limits can end what is said aloud before all possible segments (the
rest of the reply is still shown); unused reservation is not silently recycled.

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

Stop, losing the held control, session lock and Close stop only this operation.
Unlocking resumes the listening and vision chosen in Companion (unless paused
in the window); a paused mic or vision button stays paused until clicked. Native/credential/HTTP work and cleanup
run off the dispatcher; the UI remains responsive. Noncooperative native work
or callbacks can outlive a timeout or closed observer. The shared slot remains
reserved; failed cleanup is quarantined rather than replaced with a fresh
factory. Closing the main window exits the app, not a background tray listener.
This is not a measured 250 ms physical-stop guarantee.

The fixed **Stop (Esc)** control also drops a typed message still waiting to be
sent, and what always listening heard that was still waiting for a reply, and
pauses vision; listening itself carries on. Escape works from the message box, the
history and the held talk button. Releasing Space after Escape cannot send that
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

## Hands-free voice activity and Voice ID

**How you talk** in Companion › Listening offers **Always listening** (default)
or **Push-to-talk**. The choice, sensitivity, pause length, Voice ID toggle,
*Speak replies* and Vision choices are remembered in `talk-preferences.json` in
the data folder.

- With always listening, opening the talk window opens the microphone chosen in
  Companion › Listening (the Windows default unless another is picked; testing
  it there is optional). If listening isn't set up, the mic button says *Can't
  listen* and why. If the microphone can't be opened (absent, busy, denied), the
  mic button says *Mic unavailable* with the fix, Martlet tries it again every
  5 seconds, and you can type meanwhile.
  An adaptive energy detector (`EnergyVoiceActivityDetector`,
  20 ms frames read from the capture's own buffer through `TryCopyMonoFrame`)
  waits for speech, then releases the capture after your chosen pause
  (0.5/0.8/1.2 s). Only the detected speech plus 300 ms pre-roll and 200 ms tail
  is uploaded, not the idle wait before it. Sounds shorter than 450 ms (coughs,
  clicks) are ignored. **Sensitivity** trades missed quiet speech against false
  triggers from noise.
- Listening never stops by itself. It runs on its own slot beside replies
  (`LiveListener`): it records one utterance at a time and transcribes each, in
  order, while it already listens for the next, so nothing said while Martlet
  thinks is lost. Each utterance is still its own action: a fresh authorization,
  capture epoch, Voice ID check and STT request. It holds off only while Martlet
  speaks (a reply or a remark, plus 300 ms for the room's echo), so it never
  hears itself, and while other setup work (a microphone test, Voice ID
  enrollment) owns the app slot. Idle listening restarts the bounded capture
  every 12 seconds; nothing is uploaded when nobody spoke.
- What it hears appears in the history right away. Once you pause, everything
  heard since the last reply goes to the Thinking model as one message
  (`InputSource.HandsFreeListening`, reason `ExplicitHandsFree`) with
  instructions that it hears an always-on microphone: it answers what is meant
  for it and replies `[pass]` (never shown or spoken; the message is marked
  *Martlet stayed quiet*) when it wasn't meant for it or needs no answer. When
  what you said trails off ("so, um", "and", a trailing comma or dash), Martlet
  waits 1.5 s longer for the rest.
- If you keep talking before Martlet says anything, that reply is dropped and,
  once you pause, asked again with everything you said (at most three times in
  a row, so background talk can't loop it). A reply that already acted through
  Home Assistant or a tool finishes, and what you added is answered after it.
- Typed messages go to the same slot and are always answered; listening carries
  on beside them. It continues while the window is in the background. Only the
  **Listening** button pauses and resumes it; Stop/Esc quiets Martlet but
  leaves listening on; session lock and Close end it (unlocking resumes it).
  Reply, provider and speech-to-text failures are shown and never pause
  listening; nothing is retried automatically. When listening can't start
  (for example *Only respond to my voice* is on but Voice ID isn't set up), the
  button says *Can't listen* with why and Martlet keeps trying, so it listens
  again as soon as that is fixed.

**Voice ID** (Companion › Listening › **Set up Voice ID**) recognizes the enrolled user locally:

- Enrollment records three read-aloud phrases with a separate local-only
  permission. A bundled speaker encoder (a managed port of Resemblyzer's GE2E
  LSTM, Apache-2.0; see `packaging\windows\DEPENDENCIES.txt`) turns speech into a
  256-number voiceprint. Recordings stay in memory and are zeroed; only the
  voiceprint, threshold and consistency score are saved in `voice-id.json`.
  **Test** reports the score, the threshold slider tunes strictness, and
  **Delete voiceprint** removes the file.
- With **Only respond to my voice** checked (PTT or hands-free), each utterance
  is compared with the voiceprint on this PC *before* upload. Another voice, TV
  audio or too little speech (<0.8 s) is discarded and never sent to STT. Each
  ~1.6 s part is also scored, so a turn that is mostly you but includes another
  voice is flagged ("another voice may also be in this recording").
- Voice ID is a convenience filter, not authentication: recordings of you or a
  similar voice can pass, and a cold or a new microphone can lower your score.
  Same-person clean speech typically scores 0.80-0.95 and other people
  0.45-0.75; enrollment suggests a threshold from how consistent your phrases were.

**Recognizing who is talking** (Companion › **People**, off until you download
it) tells several people at the microphone apart with AudioTranscriber's
sherpa-onnx speaker recognition, names the speaker to the Thinking model, labels
earlier messages with who said them, and learns the names each voice goes by
from the conversation. The list of voices can follow you to every computer
through your paired hosts. **Parakeet** (Companion › Listening › This PC) is
AudioTranscriber's more accurate speech-to-text, running inside Martlet with no
Docker. See [Recognizing people by voice, and Parakeet](VOICES.md).

## Troubleshooting

| Visible condition | Meaning and next action |
| --- | --- |
| Setup required / unsupported role | Review the displayed exact catalog IDs; store each role key, reselect its destination and save. No automatic fallback or capability request occurs. |
| Configuration changed | Loaded revision/role/key/output no longer matches this action. Close and reopen the talk window to use the new choices. External profile editing/copying while running is unsupported. |
| Credential missing / access denied | Review the signed-in Windows user and selected role reference. Explicit setup retrieval can check local readability only. Do not elevate or disable protection. |
| STT no speech | No LLM/TTS followed. Review intended input and local microphone test; start a fresh PTT action or type instead. Silence samples are not VAD evidence. |
| Hands-free never hears me / triggers on noise | Raise or lower **Sensitivity**; watch the level bar while speaking. Choose a longer pause if it cuts you off mid-sentence (talking on before Martlet answers also merges what you say into one message). |
| Martlet doesn't answer something it heard | The message is marked *Martlet stayed quiet*: the Thinking model decided it wasn't meant for it. Say its name or ask directly, or type. |
| Voice ID ignores me | Run **Test** in Set up Voice ID. Lower the threshold slightly or re-enroll with your usual microphone and distance. Turn Voice ID off to talk meanwhile. |
| Mic access/busy/lost/default-change/format | Use Audio setup's specific privacy/device remedy. Always listening shows *Mic unavailable* and tries the same chosen microphone again every 5 seconds; there is no loopback or device fallback. Typed input remains available. |
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
disabled and voice/permission OFF, then exercises existing no-key setup.
Package smoke launches actual self-contained
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
