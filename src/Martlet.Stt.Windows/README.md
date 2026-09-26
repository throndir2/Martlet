# Installed Windows offline speech recognition

`WindowsOfflineSttAdapter` uses `System.Speech` 10.0.12 and the explicitly selected
installed Windows desktop SAPI recognizer. This is **not Windows online dictation,
cloud STT, whisper.cpp, or a background microphone listener**. It requires neither
Docker nor a GPU, API key, account, model download or separate server. Construction
is passive. Discovery is an explicit call, not a startup scan or installation.

The selected recognizer must already be installed for the desired language and
visible to the application's architecture. An empty catalog or missing exact ID
returns `RecognizerUnavailable` with a remedy. Martlet does not install speech or
language packs, select a default recognizer, change Windows speech settings, or
fall back to another provider. The catalog's `Id` is the persisted identity;
`Name` and `Culture` describe the installed engine/language. Each action resolves
only that exact ID again, never the process's current culture or default engine.
Replacing an installed recognizer is an operating-system configuration change,
not an artifact provenance verification performed by Martlet.

## Integration contract

The caller owns microphone capture and its permission. Release push-to-talk,
finish conversion through Martlet's existing canonical audio path, and pass one
mono 16 kHz PCM16 `CanonicalWaveAudio` (maximum 25 seconds). This adapter only reads
the provided bounded WAV stream; it never calls `SetInputToDefaultAudioDevice`.
Discovery and recognition share one nonqueueing owner:

```csharp
await using var stt = new WindowsOfflineSttAdapter();
var discovery = await stt.GetInstalledRecognizersAsync(cancellationToken);
// Display discovery.Failure/Canceled, or let the user select an exact item.
// selected.Id is the user's explicit choice, not automatically the first item.
var request = new WindowsOfflineSttRequest(
    operationId, deadline, selected.Id);
var permission = new WindowsOfflineSttAuthorization(
    operationId, deadline, selected.Id, audio.Sha256, audio.ByteLength,
    allowLocalRecognition: true); // only after fresh caller-owned consent
var result = await stt.TranscribeAsync(request, audio, permission, cancellationToken);
```

The permission binds the exact operation, recognizer, original deadline and audio
hash/size and is consumed once before native engine access. It is not saved in
settings and is not itself proof of human consent. `Completed` returns bounded
text, `NoSpeech` returns no text, and failures/cancellation never return a partial
or late transcript. Results identify actual installed-engine versus test-fixture
provenance; they make no `DeniedEgressVerified` claim.

Recognition loads the selected engine's dictation grammar and consumes the
complete provided utterance with `RecognizeMode.Multiple`, joining recognized
segments until stream completion. Rejected/silent input is not invented text.
Transcript limits are 4,096 UTF-16 code units / 16 KiB UTF-8, with invalid control
characters and malformed Unicode rejected. Engine exception details and
transcripts are not logged or included in remedies.

The maximum action deadline is 30 seconds including native initialization.
Original cancellation, shutdown, absolute UTC expiry and monotonic elapsed
expiry suppress late output. Stop requests SAPI cancellation and **joins completion
and disposal before releasing the slot**; it does not abandon a worker, reopen a
new engine, or falsely report that a blocked native component has stopped.
SAPI is in-process and its cancellation/disposal are cooperative, so the deadline
is an acceptance/abort deadline, **not a guaranteed hard native termination
latency**. A hung native call retains ownership; disposal failure quarantines the
adapter. The conversation must discard canceled text immediately while awaiting
cleanup, and must not create a replacement owner to bypass an outstanding action.

The adapter snapshots WAV bytes in memory; caller disposal after admission does
not invalidate the worker's audio. Its owned bytes are zeroed after cleanup, and
no temporary audio/transcript file is created. `CanonicalWaveAudio.CopyWave()`
returns an independent caller-owned copy; other consumers must clear their copies
when finished. Managed strings and Windows engine-internal memory are not promised
to be securely erased. Windows and installed recognition components remain
trusted local software; this is not a network sandbox or a denied-egress audit
for arbitrary third-party SAPI components. Martlet adds no network transport,
upload, firewall rule, service, registry setting or system-policy change.

## Setup and evidence boundaries

The Desktop/setup owner composes this library as a separate `LocalWindowsStt`
route (`local://windows`, `windows-recognition`, exact recognizer ID), independent
of LLM and TTS choices. The original whisper manifest, importer, rights/egress
contracts and `PackageUnqualified` public guard remain unchanged. Choosing this
Windows route is not permission to enable an unqualified whisper package.

Local validation uses the pinned .NET SDK from `global.json`:

```powershell
dotnet restore tests\Martlet.Stt.Windows.Tests\Martlet.Stt.Windows.Tests.csproj --locked-mode --artifacts-path $artifacts
dotnet build tests\Martlet.Stt.Windows.Tests\Martlet.Stt.Windows.Tests.csproj -c Release --no-restore --artifacts-path $artifacts
dotnet test tests\Martlet.Stt.Windows.Tests\Martlet.Stt.Windows.Tests.csproj -c Release --no-build --no-restore --artifacts-path $artifacts --filter 'Category!=InstalledRecognizer'
```

The separate, explicitly requested native probe uses
`MARTLET_WINDOWS_STT_NATIVE=1` and `--filter 'Category=InstalledRecognizer'`.
Without that opt-in it is reported **skipped**, not passed. It runs only installed
recognizer discovery and a one-second synthetic silent WAV in memory, never a
microphone, playback device or external asset. It fails if no recognizer is
installed rather than silently claiming native evidence.

On the development Windows host this real probe passed through the production
adapter and returned `NoSpeech` with installed-recognizer provenance. Controlled
managed tests cover selection/permission binding, no-speech/text/errors, limits,
busy ownership, Stop/deadline/disposal, late text suppression and cleanup failure.
Those tests are not native speech accuracy, microphone-to-conversation evidence,
hard Stop latency, game-load performance, clean-machine or release qualification.
Those gates remain **NOT RUN** for this isolated library change.

`System.Speech` is the pinned Microsoft MIT-licensed .NET package; its package
license and notices travel with the dependency inventory. Windows recognition
engines/language components are supplied by the user's Windows installation,
not redistributed or licensed by this project.
