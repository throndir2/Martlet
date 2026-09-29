using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Participation;
using Martlet.Providers;
using Martlet.Memory;

namespace Martlet.Desktop;

internal sealed record LiveConversationStatus(string Code, bool Finished = false, bool Quarantined = false,
    PolicyReason? Policy = null, ProviderFailureCode? ProviderFailure = null, ErrorCode? AudioFailure = null);

// HandsFree: voice activity endpoints each utterance. RequireVoiceId: only the enrolled voice is uploaded.
internal sealed record ListeningOptions(bool HandsFree, VoiceActivitySettings Activity, bool RequireVoiceId)
{
    internal static TimeSpan IdleRestart => TimeSpan.FromSeconds(12);
    internal static TimeSpan MinimumUtterance => TimeSpan.FromMilliseconds(450);
}

internal sealed class LiveConversationOperation
{
    private readonly object gate = new();
    private readonly Queue<string> timeline = new();
    private LiveConversationStatus status = new("conversation.authorizing");
    private CaptureRun? capture;
    private ConversationTurn? turn;
    private int releasedPress;
    private int executionFinished;
    private string? cancellationReason;
    private TranscriptionWindow? transcriptionWindow;
    private sealed record TranscriptionWindow(TimeProvider Clock, long Timestamp, DateTimeOffset Deadline, TimeSpan Duration);
    internal ConversationAuthorization Authorization { get; }
    internal SetupOperation Worker { get; set; } = null!;
    internal CancellationToken OriginalCaller { get; }
    internal Guid Id { get; } = Guid.NewGuid();
    internal LiveConversationStatus Status => Volatile.Read(ref status);
    internal CaptureRun? Capture => Volatile.Read(ref capture);
    internal ConversationTurn? Turn => Volatile.Read(ref turn);
    internal TranscriptionResult? Transcription { get; set; }
    [JsonIgnore] internal string? Transcript { get; set; }
    internal Guid? PersonaRevision { get; set; }
    internal ResponseStyle? ResponseStyle { get; set; }
    internal int ContextMessages { get; set; }
    internal int ContextMessagesOmitted { get; set; }
    internal bool MemoryRequested { get; set; }
    internal int MemoryFactsUsed { get; set; }
    internal int MemoryFactsOmitted { get; set; }
    internal long? MemoryStoreRevision { get; set; }
    internal ListeningOptions? Listening { get; init; }
    internal Voiceprint? Voiceprint { get; init; }
    internal SpeakerCheck? SpeakerCheck { get; set; }
    private double voiceLevel = -100;
    internal double VoiceLevel { get => Volatile.Read(ref voiceLevel); set => Volatile.Write(ref voiceLevel, value); }
    internal bool HandsFree => Listening?.HandsFree == true;
    internal bool OwnershipReleased => Worker.Completion.IsCompleted;
    internal bool ExecutionFinished => Volatile.Read(ref executionFinished) != 0;
    internal void FinishExecution() => Interlocked.Exchange(ref executionFinished, 1);
    internal void BeginTranscription(TimeProvider clock, DateTimeOffset deadline) =>
        Volatile.Write(ref transcriptionWindow, new(clock, clock.GetTimestamp(), deadline, deadline - clock.GetUtcNow()));
    internal void EndTranscription() => Volatile.Write(ref transcriptionWindow, null);
    internal bool TranscriptionExpired => Volatile.Read(ref transcriptionWindow) is { } window &&
        (window.Clock.GetUtcNow() >= window.Deadline || window.Clock.GetElapsedTime(window.Timestamp) >= window.Duration);
    internal string Timeline { get { lock (gate) return string.Join(Environment.NewLine, timeline); } }
    internal LiveConversationOperation(ConversationAuthorization authorization, CancellationToken caller)
    {
        Authorization = authorization;
        OriginalCaller = caller;
    }
    internal void Publish(LiveConversationStatus value)
    {
        lock (gate)
        {
            if (Volatile.Read(ref cancellationReason) is { } reason && !value.Quarantined)
                value = value with { Code = reason, Finished = true };
            var previous = status;
            if (previous == value || previous.Finished && !value.Finished) return;
            Volatile.Write(ref status, value);
            if (timeline.Count == 32) timeline.Dequeue();
            timeline.Enqueue($"{value.Code}; policy={value.Policy}; provider={value.ProviderFailure}; audio={value.AudioFailure}");
        }
    }
    internal void Attach(CaptureRun run)
    {
        Volatile.Write(ref capture, run);
        if (Authorization.IsCanceled) _ = run.CancelAsync();
        else if (Volatile.Read(ref releasedPress) != 0) _ = run.ReleaseAsync();
    }
    internal void Attach(ConversationTurn run)
    {
        Volatile.Write(ref turn, run);
        if (Authorization.IsCanceled) _ = run.StopAsync();
    }
    internal void ReleasePress()
    {
        Interlocked.Exchange(ref releasedPress, 1);
        if (!Authorization.IsCanceled) _ = Capture?.ReleaseAsync();
    }
    internal void Cancel(string code)
    {
        Interlocked.CompareExchange(ref cancellationReason, code, null);
        Authorization.Revoke();
        Publish(new(code, Finished: true, Quarantined: Status.Quarantined));
        Worker.RequestCancellation();
        _ = Capture?.CancelAsync();
        _ = Turn?.StopAsync();
    }
    public override string ToString() => nameof(LiveConversationOperation);
}

// App-lifetime owner; setup, fixture and live work all reserve the SAME reviewed operation runner.
internal sealed class LiveConversationController : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SetupOperationRunner operations;
    private readonly ISetupService settings;
    private readonly ICredentialStore vault;
    private readonly ICaptureDeviceFactory captureDevices;
    private readonly ConversationRuntime runtime;
    private readonly OpenAiTranscriptionAdapter transcription;
    private readonly ParticipationPolicy policy;
    private readonly ConversationContextBuffer context;
    private readonly TimeProvider clock;
    private readonly Func<int, int> nextStyle;
    private readonly Action? revokeAvatar;
    private readonly DesktopMemoryService? memory;
    private readonly VoiceIdentity? voiceIdentity;
    private readonly TaskCompletionSource quarantine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LiveConversationOperation? active;
    private LiveConversationConfiguration? configuration;
    private long revision, captureEpoch;
    private bool paused, muted, locked, disposed;

    internal bool IsRunning => operations.IsRunning;
    internal int ContextTurns { get { lock (gate) return context.Count; } }
    internal LiveConversationConfiguration? Configuration { get { lock (gate) return configuration; } }
    internal ParticipationSnapshot PolicySnapshot => policy.Snapshot;
    internal (bool Paused, bool Muted, bool Locked) Controls { get { lock (gate) return (paused, muted, locked); } }
    internal LiveConversationController(SetupOperationRunner operations, ISetupService settings, ICredentialStore vault,
        ICaptureDeviceFactory captureDevices, IPlaybackDeviceFactory playbackDevices, TimeProvider? clock = null,
        Func<IProviderCredentialSource, TimeProvider, ConversationRuntime>? runtimeFactory = null,
        Func<IProviderCredentialSource, TimeProvider, OpenAiTranscriptionAdapter>? transcriptionFactory = null,
        Func<int, int>? nextStyle = null,
        DesktopMemoryService? memory = null,
        GeneratedSpeechObserver? generatedSpeech = null, Action? revokeAvatar = null, VoiceIdentity? voiceIdentity = null,
        string? dataDirectory = null)
    {
        this.operations = operations;
        this.settings = settings;
        this.vault = vault;
        this.captureDevices = captureDevices;
        this.clock = clock ?? TimeProvider.System;
        this.nextStyle = nextStyle ?? RandomNumberGenerator.GetInt32;
        this.revokeAvatar = revokeAvatar;
        this.memory = memory;
        this.voiceIdentity = voiceIdentity;
        context = new(this.clock);
        var credentials = new ConversationCredentialSource(() => Volatile.Read(ref active)?.Authorization);
        runtime = runtimeFactory?.Invoke(credentials, this.clock) ??
            ConversationRuntime.Create(credentials, playbackDevices, clock: this.clock, generatedSpeech: generatedSpeech,
                hostText: new HostTextClient(), hostSpeech: dataDirectory is null ? null : new HostSpeechClient(dataDirectory));
        transcription = transcriptionFactory?.Invoke(credentials, this.clock) ??
            OpenAiTranscriptionAdapter.Create(credentials, this.clock);
        policy = new(runtime.SessionId, new ParticipationConfiguration(), new ParticipationState(), this.clock);
    }

    internal void Configure(SettingsLoadResult loaded)
    {
        var next = LiveConversationConfiguration.From(loaded);
        LiveConversationOperation? stop;
        bool changed;
        lock (gate)
        {
            changed = configuration is not null && configuration.Revision != next?.Revision;
            memory?.Invalidate();
            context.Clear();
            configuration = next;
            stop = RevokeLocked();
        }
        if (changed) revokeAvatar?.Invoke();
        stop?.Cancel("conversation.configuration_changed");
    }

    internal void SetControls(bool pause, bool mute, bool sessionLocked)
    {
        if (pause || mute || sessionLocked) revokeAvatar?.Invoke();
        LiveConversationOperation? stop;
        lock (gate)
        {
            if (paused == pause && muted == mute && locked == sessionLocked) return;
            memory?.Invalidate();
            context.Clear();
            paused = pause;
            muted = mute;
            locked = sessionLocked;
            stop = RevokeLocked();
        }
        stop?.Cancel(sessionLocked ? "conversation.locked" : pause ? "conversation.paused" : "conversation.muted");
    }

    internal void SetSessionLocked(bool value)
    {
        if (value) revokeAvatar?.Invoke();
        LiveConversationOperation? stop;
        lock (gate)
        {
            if (locked == value) return;
            memory?.Invalidate();
            context.Clear();
            locked = value;
            stop = RevokeLocked();
        }
        stop?.Cancel("conversation.locked");
    }

    internal void Revoke(string code)
    {
        revokeAvatar?.Invoke();
        LiveConversationOperation? stop;
        lock (gate)
        {
            memory?.Invalidate();
            context.Clear();
            stop = RevokeLocked();
        }
        stop?.Cancel(code);
    }

    private LiveConversationOperation? RevokeLocked()
    {
        revision = checked(revision + 1);
        var owned = active is { OwnershipReleased: false } ? active : null;
        owned?.Authorization.Revoke();
        // SetState may require StopActiveTurn; ownership is deliberately NOT released here.
        var change = policy.SetState(new() { AuthorizationRevision = revision, Paused = paused || locked || disposed, Muted = muted });
        return change.Action == PolicyAction.StopActiveTurn || owned is not null ? owned : null;
    }

    internal LiveConversationOperation Start(string? text, bool voice, bool microphone, bool approved,
        bool localCaptureApproved = false, bool uploadApproved = false, CancellationToken caller = default,
        bool memoryApproved = false, ListeningOptions? listening = null)
    {
        if (!approved || microphone && (!localCaptureApproved || !uploadApproved))
            throw new LiveActionException("conversation.permission_required");
        if (listening is not null && !microphone) throw new LiveActionException("conversation.invalid_input");
        listening?.Activity.Validate();
        Voiceprint? voiceprint = null;
        if (listening?.RequireVoiceId == true)
            voiceprint = voiceIdentity?.Current ?? throw new LiveActionException("voiceid.not_enrolled");
        caller.ThrowIfCancellationRequested();
        BoundedTextInput? input = microphone ? null : new(text ?? "");
        if (input is { UserText.Length: > 4096 }) throw new LiveActionException("conversation.input_limit");
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConversationOperation operation;
        lock (gate)
        {
            if (disposed || paused || muted || locked) throw new LiveActionException("conversation.controls_blocked");
            if (operations.IsRunning) throw new LiveActionException("conversation.ownership_busy");
            var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
            if (selected.Unavailable(voice, microphone) is not null) throw new LiveActionException("conversation.configuration_unsupported");
            if (memoryApproved && (memory is null || selected.Memory is not { Enabled: true }))
                throw new LiveActionException("memory.disabled");
            long acceptedRevision = revision = checked(revision + 1);
            var authorization = new ConversationAuthorization(selected, voice, microphone, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller);
            operation = new(authorization, caller) { MemoryRequested = memoryApproved, Listening = listening, Voiceprint = voiceprint };
            active = operation;
            var worker = operations.TryStart(async token =>
            {
                await published.Task.ConfigureAwait(false);
                authorization.BindWorker(token);
                return await RunAsync(operation, input, token).ConfigureAwait(false);
            });
            if (worker is null)
            {
                authorization.Revoke();
                throw new LiveActionException("conversation.ownership_busy");
            }
            operation.Worker = worker;
            policy.SetState(new()
            {
                AuthorizationRevision = acceptedRevision, CaptureAuthorized = microphone, TranscriptionAuthorized = microphone,
                TextDestinationAuthorized = true, SpeechOutputRequested = voice, SpeechDestinationAuthorized = voice
            });
        }
        published.SetResult();
        _ = SuperviseAsync(operation);
        return operation;
    }

    internal void Stop(LiveConversationOperation operation, string reason = "conversation.canceled")
    {
        lock (gate)
        {
            if (!ReferenceEquals(operation, active)) return;
            memory?.Invalidate();
            context.Clear();
            if (operation.OwnershipReleased || operation.ExecutionFinished) return;
            RevokeLocked();
        }
        // Exact handle, never a delayed sink-wide Stop or cancellation of the next setup/turn.
        operation.Cancel(reason);
    }

    private async Task SuperviseAsync(LiveConversationOperation operation)
    {
        while (!operation.Worker.Completion.IsCompleted && !operation.ExecutionFinished)
        {
            try { operation.Authorization.Check(); }
            catch (OperationCanceledException) { Stop(operation); return; }
            catch (LiveActionException error) { Stop(operation, error.Code); return; }
            if (operation.TranscriptionExpired) { Stop(operation, "stt.deadline_exceeded"); return; }
            var turn = operation.Turn;
            if (turn is not null && !operation.Status.Finished)
            {
                var snapshot = turn.Snapshot;
                operation.Publish(new("runtime." + snapshot.State, ProviderFailure: snapshot.ProviderFailure,
                    AudioFailure: snapshot.Playback?.Error?.Code));
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled)
                        policy.SetState(new()
                        {
                            AuthorizationRevision = revision, CaptureAuthorized = operation.Authorization.Microphone,
                            TranscriptionAuthorized = operation.Authorization.Microphone, TextDestinationAuthorized = true,
                            SpeechOutputRequested = operation.Authorization.Voice, SpeechDestinationAuthorized = operation.Authorization.Voice,
                            Activity = snapshot.State == ConversationState.Playing ? ResponseActivity.Playing : ResponseActivity.Responding
                        });
                }
            }
            await Task.WhenAny(operation.Worker.Completion, Task.Delay(TimeSpan.FromMilliseconds(25), clock)).ConfigureAwait(false);
        }
    }

    private async Task<SetupWorkResult> RunAsync(LiveConversationOperation operation, BoundedTextInput? input, CancellationToken worker)
    {
        DispatchLease? lease = null;
        try
        {
            await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
            if (operation.Authorization.Microphone)
            {
                var audio = await CaptureAsync(operation).ConfigureAwait(false);
                if (audio is null) return new(SetupWorkOutcome.Completed);
                operation.Authorization.Check(worker);
                await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
                var context = new ProviderRequestContext
                {
                    Ids = Ids(), Epoch = operation.Capture!.Snapshot.Epoch,
                    Deadline = operation.Authorization.Deadline(TimeSpan.FromSeconds(30))
                };
                var permission = operation.Authorization.AuthorizeAudio(context);
                operation.Publish(new("stt.uploading"));
                operation.BeginTranscription(clock, context.Deadline);
                TranscriptionResult result;
                try
                {
                    result = await transcription.TranscribeAsync(context, operation.Authorization.Configuration.Route(SetupRole.Stt).ModelId,
                        audio, LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false);
                }
                finally { operation.EndTranscription(); }
                operation.Authorization.Check(worker);
                operation.Transcription = result;
                if (result.Outcome != TranscriptionOutcome.Completed)
                {
                    operation.Publish(new("stt." + result.Outcome, Finished: true, ProviderFailure: result.Failure?.Code));
                    return new(result.Outcome == TranscriptionOutcome.NoSpeech ? SetupWorkOutcome.Completed : SetupWorkOutcome.Failed);
                }
                operation.Transcript = result.Text;
                input = new(result.Text!);
            }
            operation.Authorization.Check(worker);
            ConversationTurn turn;
            PersonaProfile? persona;
            ResponseStyle? style;
            IReadOnlyList<TextHistoryMessage> history;
            lock (gate)
            {
                operation.Authorization.Check(worker);
                // Receipt is NOW for a newly received transcript. Never renew a queued/busy/expired intent.
                var source = !operation.Authorization.Microphone ? InputSource.TypedControl
                    : operation.HandsFree ? InputSource.HandsFreeListening : InputSource.PushToTalkControl;
                var intent = policy.CreateIntent(new(source,
                    new Transcript(input!.UserText, confidence: operation.Transcription?.Confidence),
                    trustedTypedAddress: !operation.Authorization.Microphone));
                var decision = policy.Evaluate(intent);
                var commit = policy.TryCommit(decision);
                operation.Publish(new("policy." + commit.Reason, Policy: commit.Reason, Finished: !commit.Accepted));
                if (!commit.Accepted) return new(SetupWorkOutcome.Completed);
                lease = commit.Lease;
                persona = operation.Authorization.Configuration.Persona;
                style = persona is null ? null :
                    ResponseStyleSelector.Select(persona.Styles, nextStyle);
                history = context.Snapshot();
            }

            DesktopMemoryRetrieval? memoryResult = null;
            if (operation.MemoryRequested)
            {
                operation.Publish(new("memory.retrieving"));
                memoryResult = await memory!.RetrieveAsync(
                    operation.Authorization.Configuration.Memory!,
                    input!.UserText,
                    worker).ConfigureAwait(false);
                operation.Authorization.Check(worker);
                await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
                operation.MemoryStoreRevision = memoryResult.StoreRevision;
            }

            lock (gate)
            {
                operation.Authorization.Check(worker);
                var request = operation.Authorization.Configuration.Request(
                    input!, operation.Authorization.Voice, style, history, memoryResult,
                    out var usedHistory, out var usedMemory);
                operation.PersonaRevision = persona?.ConfigurationRevision;
                operation.ResponseStyle = style;
                operation.ContextMessages = usedHistory;
                operation.ContextMessagesOmitted = history.Count - usedHistory;
                operation.MemoryFactsUsed = usedMemory;
                operation.MemoryFactsOmitted = (memoryResult?.Hits.Count ?? 0) - usedMemory;
                operation.Authorization.BindInput(request.Input);
                // Exact-content commit, pause/consent state and immediate Start share this short, non-awaiting gate.
                turn = runtime.Start(request, operation.Authorization, operation.OriginalCaller);
                operation.Attach(turn);
            }
            var terminal = await turn.Completion.ConfigureAwait(false);
            if (terminal.State == ConversationState.Completed && !string.IsNullOrWhiteSpace(turn.Content.Text))
            {
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled)
                        context.Add(input!.UserText, turn.Content.Text);
                }
            }
            operation.Publish(new("runtime." + terminal.State, Finished: true, Quarantined: terminal.Quarantined,
                Policy: PolicyReason.DispatchAccepted, ProviderFailure: terminal.ProviderFailure, AudioFailure: terminal.Playback?.Error?.Code));
            await turn.OwnershipRelease.ConfigureAwait(false);
            if (turn.Snapshot.Quarantined)
            {
                operation.Publish(operation.Status with { Code = "conversation.cleanup_quarantined", Quarantined = true });
                await quarantine.Task.ConfigureAwait(false);
            }
            return new(terminal.State is ConversationState.Completed or ConversationState.Refused ? SetupWorkOutcome.Completed : SetupWorkOutcome.Failed);
        }
        catch (OperationCanceledException) when (worker.IsCancellationRequested || operation.OriginalCaller.IsCancellationRequested)
        {
            operation.Publish(new("conversation.canceled", Finished: true));
            return new(SetupWorkOutcome.Canceled);
        }
        catch (LiveActionException error)
        {
            operation.Publish(new(error.Code, Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        catch (DesktopMemoryException error)
        {
            operation.Publish(new(error.Code, Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        catch (MemoryException error)
        {
            operation.Publish(new($"memory.{error.Failure}", Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        catch (ContractException error)
        {
            operation.Publish(new("conversation.invalid_input", Finished: true, AudioFailure: error.Code));
            return new(SetupWorkOutcome.Failed);
        }
        catch (PolicyValidationException)
        {
            operation.Publish(new("policy.invalid_input", Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        finally
        {
            if (operation.Turn is { } turn)
            {
                await turn.StopAsync().ConfigureAwait(false);
                await turn.OwnershipRelease.ConfigureAwait(false);
                if (turn.Snapshot.Quarantined) await quarantine.Task.ConfigureAwait(false);
            }
            lock (gate)
            {
                if (lease is not null) policy.Release(lease);
                operation.FinishExecution();
                if (ReferenceEquals(active, operation)) RevokeLocked();
            }
        }
    }

    private CorrelationIds Ids() => new() { SessionId = runtime.SessionId, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    // Reads the capture's own 20 ms frames (no second audio queue) and releases it when the speaker pauses.
    // Returns the speech range to send, or null when nobody spoke before the idle restart.
    private async Task<SpeechRange?> EndpointAsync(LiveConversationOperation operation, CaptureRun run)
    {
        var settings = operation.Listening!.Activity;
        var detector = new EnergyVoiceActivityDetector(settings);
        var minimumFrames = (int)(ListeningOptions.MinimumUtterance.TotalMilliseconds / 20);
        var frame = new byte[EnergyVoiceActivityDetector.FrameBytes];
        var started = clock.GetTimestamp();
        int index = 0, accepted = -1;
        try
        {
            while (!run.Completion.IsCompleted)
            {
                bool copied;
                do
                {
                    try { copied = run.TryCopyMonoFrame(index, frame); }
                    catch (OperationCanceledException) { copied = false; }
                    if (!copied) break;
                    index++;
                    var transition = detector.Process(frame);
                    operation.VoiceLevel = detector.LastLevelDb;
                    if (transition == VoiceActivityTransition.SpeechStarted)
                    {
                        if (accepted < 0) operation.Publish(new("mic.hearing_speech"));
                    }
                    else if (transition == VoiceActivityTransition.SpeechEnded)
                    {
                        // A cough or click is ignored; keep listening for real speech.
                        if (accepted < 0 && detector.SpeechEndFrame - detector.SpeechStartFrame < minimumFrames)
                        {
                            operation.Publish(new("mic.listening"));
                            continue;
                        }
                        if (accepted < 0) accepted = detector.SpeechStartFrame;
                        await run.ReleaseAsync().ConfigureAwait(false);
                        return Range(accepted, detector.SpeechEndFrame);
                    }
                    if (detector.Speaking && accepted < 0 &&
                        index - detector.SpeechStartFrame >= minimumFrames) accepted = detector.SpeechStartFrame;
                }
                while (true);
                if (accepted < 0 && !detector.Speaking && clock.GetElapsedTime(started) >= ListeningOptions.IdleRestart)
                    return null;
                await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromMilliseconds(20), clock)).ConfigureAwait(false);
            }
            // Duration limit or Finish: send everything from the onset to the end of the recording.
            if (accepted < 0 && detector.Speaking) accepted = detector.SpeechStartFrame;
            return accepted < 0 ? null : Range(accepted, 0) with { EndSampleExclusive = int.MaxValue };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
            operation.VoiceLevel = -100;
        }

        SpeechRange Range(int startFrame, int endFrame) => new(
            Math.Max(0, startFrame * EnergyVoiceActivityDetector.FrameSamples - EnergyVoiceActivityDetector.Samples(settings.PreRoll)),
            endFrame * EnergyVoiceActivityDetector.FrameSamples + EnergyVoiceActivityDetector.Samples(settings.Tail));
    }

    private async Task<BoundedWaveAudio?> CaptureAsync(LiveConversationOperation operation)
    {
        var permission = operation.Authorization;
        permission.Check();
        var selected = permission.Configuration.Audio!.Input;
        await using var microphone = new MicrophoneCapture(runtime.SessionId, captureDevices,
            new() { MaximumDuration = LiveConversationConfiguration.CaptureDuration, MaximumPcmBytes = 800_000 }, clock);
        var request = new CaptureRequest(Ids(), Interlocked.Increment(ref captureEpoch),
            new(selected.EndpointId is null ? InputPolicy.FollowDefaultOnNextPress : InputPolicy.FixedEndpoint, selected.EndpointId),
            LiveConversationConfiguration.CaptureDuration, permission.Deadline(LiveConversationConfiguration.CapturePermission, fromAcceptance: true));
        var run = microphone.Press(request, new(request, true), operation.OriginalCaller);
        operation.Attach(run);
        operation.Publish(new(operation.HandsFree ? "mic.listening" : "mic.capturing"));
        try
        {
            SpeechRange? heard = null;
            if (operation.HandsFree)
            {
                heard = await EndpointAsync(operation, run).ConfigureAwait(false);
                if (heard is null)
                {
                    await run.CancelAsync().ConfigureAwait(false);
                    await run.Completion.ConfigureAwait(false);
                    permission.Check();
                    operation.Publish(new("mic.no_speech", Finished: true));
                    return null;
                }
            }
            var terminal = await run.Completion.ConfigureAwait(false);
            permission.Check();
            using var utterance = run.TakeUtterance();
            if (terminal.State != CaptureState.Completed || utterance is null)
            {
                operation.Publish(new("mic." + terminal.State, Finished: true, AudioFailure: terminal.Error?.Code));
                return null;
            }
            byte[] pcm = new byte[utterance.ByteCount];
            BoundedWaveAudio? wave = null;
            try
            {
                permission.Check();
                utterance.CopyPcmTo(pcm);
                var total = pcm.Length / 2;
                // Hands-free uploads only the detected speech (with pre-roll/tail), not the idle wait before it.
                var start = Math.Min(heard?.StartSample ?? 0, total);
                var end = Math.Min(heard?.EndSampleExclusive ?? total, total);
                var speech = pcm.AsSpan(start * 2, Math.Max(0, end - start) * 2);
                if (operation.Voiceprint is { } voiceprint)
                {
                    operation.Publish(new("speaker.checking"));
                    var check = SpeakerVerifier.Check(VoiceIdentity.Encoder, voiceprint.Embedding, voiceprint.Threshold, speech);
                    permission.Check();
                    operation.SpeakerCheck = check;
                    if (check.Verdict != SpeakerVerdict.User)
                    {
                        operation.Publish(new(check.Verdict == SpeakerVerdict.TooShort ? "speaker.too_short" : "speaker.not_user", Finished: true));
                        return null;
                    }
                    operation.Publish(new("speaker.verified"));
                }
                if (speech.Length >= 2) wave = BoundedWaveAudio.FromPcm(CapturedUtterance.Format, speech);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pcm);
                utterance.Dispose();
            }
            if (wave is null)
            {
                operation.Publish(new("mic.no_speech", Finished: true));
                return null;
            }
            operation.Publish(new("mic.transferred_and_cleared"));
            return wave;
        }
        finally
        {
            await run.CancelAsync().ConfigureAwait(false);
            var release = await run.DeviceRelease.ConfigureAwait(false);
            if (!release.Released)
            {
                operation.Publish(new("mic.cleanup_quarantined", Finished: true, Quarantined: true, AudioFailure: release.Error?.Code));
                await quarantine.Task.ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        LiveConversationOperation? owned;
        lock (gate)
        {
            disposed = true;
            memory?.Invalidate();
            context.Clear();
            owned = RevokeLocked();
        }
        owned?.Cancel("conversation.closed");
        // Never wait for native cleanup on the dispatcher. The shared slot remains reserved until real exit.
        await runtime.DisposeAsync().ConfigureAwait(false);
        if (owned is null || owned.Worker.Completion.IsCompleted) transcription.Dispose();
        else _ = DisposeAfterReleaseAsync(owned);
    }
    private async Task DisposeAfterReleaseAsync(LiveConversationOperation operation)
    {
        await operation.Worker.Completion.ConfigureAwait(false);
        transcription.Dispose();
    }
}
