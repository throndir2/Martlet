# Installed Windows speech synthesis

`Martlet.Speech.Windows` is a real, same-machine Windows TTS adapter using
Microsoft `System.Speech` 10.0.12 and installed SAPI synthesis voices. It needs
no model download, cloud key, HTTP client or billable-provider authorization.
It does not replace optional OpenAI speech or the separate F5 work. It does
not install voices, open an audio endpoint, access a microphone, or play audio.
Installed engine code is an OS/local dependency, not a sandbox or attestation
of arbitrary third-party voice plug-ins.

## Host integration contract

The Desktop/settings/package integration is a separately coordinated change;
this module alone does not add a visible conversation selection.

- Persist the explicit `LocalWindowsTts` route, provider `windows-speech`,
  model `windows-installed`, origin `local://windows`, and exact installed
  `VoiceId`. These route fields belong to Core, not a dependency on this DLL.
  LLM, STT and TTS selections are independent.
- Construct `WindowsSpeechSynthesisAdapter` passively. Call
  `GetInstalledVoicesAsync` only from an explicit discovery action. It returns
  enabled `WindowsSpeechVoice` records (`Id`, display `Name`, `Culture`).
  Empty means no available voices; errors are explicit, not an empty-list
  fallback. IDs are opaque OS strings, not cloud model tokens.
- Obtain fresh local output and generated-voice disclosure consent. Bind the
  selected playback endpoint and action separately in the existing host.
  Create `WindowsSpeechAuthorization(context, voiceId, input, limits,
  expiresAt, allowGeneratedSpeech: true)` only for that exact segment. The
  authorization is one-use and checks object identity for `BoundedSpeechInput`,
  request context, exact voice, limits and expiry. It has no text-upload or
  potential-charge flags. This trusted-caller scope check cannot authenticate
  the host or authorize playback itself.
- Await `SynthesizeAsync(context, voiceId, input, limits, authorization, token)`.
  Its `WindowsSpeechResult` includes `Outcome`, sanitized `Failure`,
  `Provenance` and nullable `Audio`. Only `Completed` has audio.
  Native construction, discovery, synthesis, cancellation and disposal run
  off the calling dispatcher. No voice-name fallback: the exact enabled ID is
  resolved, ambiguous names fail, and the selected native ID is checked again.
- This is **buffered synthesis**, not incremental generation. System.Speech
  is asked to produce raw **24,000 Hz mono signed 16-bit little-endian PCM**,
  including its own conversion when needed. The write boundary enforces the
  smaller byte/duration cap from `SpeechSynthesisLimits`, checks first/idle/
  total deadlines and rejects empty or unaligned output. No WAV header is
  delivered or misrepresented as PCM.
- After completed synthesis, `Audio.Format`, `Audio.SampleCount` and one-use
  `Audio.Frames` expose at most the authorized bytes in 20 ms `PcmFrame`s
  (short final frame allowed), with original IDs/epoch and contiguous
  sequence/sample offsets. Frames recheck original cancellation, shutdown
  and monotonic/absolute action/authorization deadlines before delivery.
  The host must also check its current epoch/action/output permission before
  each submission. A previously returned buffer is not permission to replay.
- Submit through the existing selected-device `PlaybackRun`. Tee only frames
  accepted by playback into the action's generated-speech/avatar producer.
  Complete the producer with the actual sample count only after successful
  input completion. Never use provider-side direct output or a separate
  animation clock.

## Cancellation and ownership

There is one native operation per adapter; overlapping discovery/synthesis
fails `Busy` rather than queuing stale requests. Caller cancellation and adapter
shutdown are observed on the worker and on PCM writes; native cancellation is
requested on the owning worker, then the completion callback and disposal are
awaited before returning. Cancellation never yields partial speech.

`SynthesizeAsync` does **not** detach a native task and report release early.
`DisposeAsync` cancels and awaits the owned task; repeated disposal shares that
same task. A noncooperative installed engine may delay both tasks. The host
must keep its existing bounded Stop/retained-ownership quarantine around them;
it must not start replacement native work after a timeout. This module cannot
forcefully terminate an in-process COM engine safely.

`OwnershipReleased` is true on a returned synthesis result except
`CleanupFailed`; that error withholds audio and quarantines the adapter.
Unexpected programming failures propagate after cleanup rather than being
silently turned into successful results. No physical Stop latency or audible
completion is inferred from buffered synthesis.

## Local evidence

Developer-host evidence on 2026-09-25 with SDK 10.0.401: locked restore and
Release build succeeded; 21 managed cases passed. Three explicit native cases
passed: five installed voices discovered, a public phrase produced 90,108
non-silent PCM samples, a missing voice failed without fallback, and a two-byte
output cap failed with definitive cleanup and no partial audio. The ordinary
test run explicitly skips all three native cases.

Use the pinned SDK and normal committed locks:

```powershell
dotnet restore .\tests\Martlet.Speech.Windows.Tests\Martlet.Speech.Windows.Tests.csproj --locked-mode
dotnet test .\tests\Martlet.Speech.Windows.Tests\Martlet.Speech.Windows.Tests.csproj -c Release --no-restore --filter "Category!=NativeWindowsSpeech"
```

Managed tests use an explicitly injected fake engine and report `Fixture`,
never `Live`. Native tests are skipped by default. The following explicit
memory-only run discovers installed voices and synthesizes a public test
phrase; it never selects a playback device or microphone:

```powershell
$env:MARTLET_NATIVE_WINDOWS_SPEECH = "1"
dotnet test .\tests\Martlet.Speech.Windows.Tests\Martlet.Speech.Windows.Tests.csproj -c Release --no-restore --filter "Category=NativeWindowsSpeech" --logger "console;verbosity=detailed"
```

No available installed voice means the native synthesis test fails
truthfully; do not install/download one to manufacture passing evidence.
Native in-memory evidence is not listening, microphone, physical-device,
clean-machine, release, novice-flow, or full Desktop/avatar qualification.
Desktop normal/RID locks, payload inventory, dependency notices, SBOM and
provenance must include the new assembly and System.Speech when the host adds
its reference; no standalone package or release is published here.
