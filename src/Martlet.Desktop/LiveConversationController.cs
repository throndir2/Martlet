using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Participation;
using Martlet.Providers;

namespace Martlet.Desktop;

internal sealed record LiveConversationStatus(string Code, bool Finished = false, bool Quarantined = false,
    PolicyReason? Policy = null, ProviderFailureCode? ProviderFailure = null, ErrorCode? AudioFailure = null);

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
    internal PersonaTurnSelection? Persona { get; set; }
    [JsonIgnore] internal string? Transcript { get; set; }
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

internal sealed class PersonaTurnSelection
{
    internal Guid PersonaId { get; }
    internal Guid ConfigurationRevision { get; }
    internal ResponseStyle Style { get; }
    [JsonIgnore] internal string Instructions { get; }

    private PersonaTurnSelection(Guid personaId, Guid configurationRevision, ResponseStyle style, string instructions)
    {
        PersonaId = personaId;
        ConfigurationRevision = configurationRevision;
        Style = style;
        Instructions = instructions;
    }

    internal static PersonaTurnSelection Create(PersonaProfile persona, Func<int, int> sample)
    {
        persona.Validate();
        var style = persona.Styles.Select(sample);
        var guidance = style switch
        {
            ResponseStyle.Helpful => "Be especially useful, clear, and considerate.",
            ResponseStyle.Sarcastic => "Use light sarcasm without hostility, deception, or obscuring the answer.",
            ResponseStyle.Silly => "Use playful silliness while keeping the answer accurate and understandable.",
            ResponseStyle.Distracted => "Use a mildly distracted conversational tone without ignoring the request or claiming unobserved context.",
            _ => "Use harmless playful teasing without harassment, deception, or sabotage."
        };
        var instructions = $"Use the saved persona named \"{persona.Name}\" for this response.\n" +
            $"Saved persona instructions:\n{persona.Text}\n\nDominant response style for this turn: {style}. {guidance}\n" +
            "Style affects tone only. Preserve factual correctness, safety, permissions, serious-request priority, and the user's ability to stop.";
        return new(persona.Id, persona.ConfigurationRevision, style, instructions);
    }

    public override string ToString() => nameof(PersonaTurnSelection);
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
    private readonly TimeProvider clock;
    private readonly Func<int, int> styleSample;
    private readonly TaskCompletionSource quarantine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LiveConversationOperation? active;
    private LiveConversationConfiguration? configuration;
    private long revision, captureEpoch;
    private bool paused, muted, locked, disposed;

    internal bool IsRunning => operations.IsRunning;
    internal LiveConversationConfiguration? Configuration { get { lock (gate) return configuration; } }
    internal ParticipationSnapshot PolicySnapshot => policy.Snapshot;
    internal (bool Paused, bool Muted, bool Locked) Controls { get { lock (gate) return (paused, muted, locked); } }
    internal LiveConversationController(SetupOperationRunner operations, ISetupService settings, ICredentialStore vault,
        ICaptureDeviceFactory captureDevices, IPlaybackDeviceFactory playbackDevices, TimeProvider? clock = null,
        Func<IProviderCredentialSource, TimeProvider, ConversationRuntime>? runtimeFactory = null,
        Func<IProviderCredentialSource, TimeProvider, OpenAiTranscriptionAdapter>? transcriptionFactory = null,
        Func<int, int>? styleSample = null)
    {
        this.operations = operations;
        this.settings = settings;
        this.vault = vault;
        this.captureDevices = captureDevices;
        this.clock = clock ?? TimeProvider.System;
        this.styleSample = styleSample ?? RandomNumberGenerator.GetInt32;
        var credentials = new ConversationCredentialSource(() => Volatile.Read(ref active)?.Authorization);
        runtime = runtimeFactory?.Invoke(credentials, this.clock) ??
            ConversationRuntime.Create(credentials, playbackDevices, clock: this.clock);
        transcription = transcriptionFactory?.Invoke(credentials, this.clock) ??
            OpenAiTranscriptionAdapter.Create(credentials, this.clock);
        policy = new(runtime.SessionId, new ParticipationConfiguration(), new ParticipationState(), this.clock);
    }

    internal void Configure(SettingsLoadResult loaded)
    {
        var next = LiveConversationConfiguration.From(loaded);
        LiveConversationOperation? stop;
        lock (gate)
        {
            configuration = next;
            stop = RevokeLocked();
        }
        stop?.Cancel("conversation.configuration_changed");
    }

    internal void SetControls(bool pause, bool mute, bool sessionLocked)
    {
        LiveConversationOperation? stop;
        lock (gate)
        {
            if (paused == pause && muted == mute && locked == sessionLocked) return;
            paused = pause;
            muted = mute;
            locked = sessionLocked;
            stop = RevokeLocked();
        }
        stop?.Cancel(sessionLocked ? "conversation.locked" : pause ? "conversation.paused" : "conversation.muted");
    }

    internal void SetSessionLocked(bool value)
    {
        LiveConversationOperation? stop;
        lock (gate)
        {
            if (locked == value) return;
            locked = value;
            stop = RevokeLocked();
        }
        stop?.Cancel("conversation.locked");
    }

    internal void Revoke(string code)
    {
        LiveConversationOperation? stop;
        lock (gate) stop = RevokeLocked();
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
        bool localCaptureApproved = false, bool uploadApproved = false, CancellationToken caller = default)
    {
        if (!approved || microphone && (!localCaptureApproved || !uploadApproved))
            throw new LiveActionException("conversation.permission_required");
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
            long acceptedRevision = revision = checked(revision + 1);
            var authorization = new ConversationAuthorization(selected, voice, microphone, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller);
            operation = new(authorization, caller);
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
            if (!ReferenceEquals(operation, active) || operation.OwnershipReleased || operation.ExecutionFinished) return;
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
            await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
            operation.Authorization.Check(worker);
            ConversationTurn turn;
            lock (gate)
            {
                operation.Authorization.Check(worker);
                // Receipt is NOW for a newly received transcript. Never renew a queued/busy/expired intent.
                var intent = policy.CreateIntent(new(operation.Authorization.Microphone ? InputSource.PushToTalkControl : InputSource.TypedControl,
                    new Transcript(input!.UserText, confidence: operation.Transcription?.Confidence),
                    trustedTypedAddress: !operation.Authorization.Microphone));
                var decision = policy.Evaluate(intent);
                var commit = policy.TryCommit(decision);
                operation.Publish(new("policy." + commit.Reason, Policy: commit.Reason, Finished: !commit.Accepted));
                if (!commit.Accepted) return new(SetupWorkOutcome.Completed);
                lease = commit.Lease;
                var persona = PersonaTurnSelection.Create(operation.Authorization.Configuration.Persona, styleSample);
                var exactInput = new BoundedTextInput(input!.UserText, persona.Instructions);
                operation.Persona = persona;
                operation.Authorization.BindInput(exactInput);
                var request = operation.Authorization.Configuration.Request(exactInput, operation.Authorization.Voice);
                // Exact-content commit, pause/consent state and immediate Start share this short, non-awaiting gate.
                turn = runtime.Start(request, operation.Authorization, operation.OriginalCaller);
                operation.Attach(turn);
            }
            var terminal = await turn.Completion.ConfigureAwait(false);
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
        catch (ContractException)
        {
            operation.Publish(new("conversation.invalid_input", Finished: true));
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
        operation.Publish(new("mic.capturing"));
        try
        {
            var terminal = await run.Completion.ConfigureAwait(false);
            permission.Check();
            using var utterance = run.TakeUtterance();
            if (terminal.State != CaptureState.Completed || utterance is null)
            {
                operation.Publish(new("mic." + terminal.State, Finished: true, AudioFailure: terminal.Error?.Code));
                return null;
            }
            byte[] pcm = new byte[utterance.ByteCount];
            BoundedWaveAudio wave;
            try
            {
                permission.Check();
                utterance.CopyPcmTo(pcm);
                wave = BoundedWaveAudio.FromPcm(CapturedUtterance.Format, pcm);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pcm);
                utterance.Dispose();
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
        lock (gate) { disposed = true; owned = RevokeLocked(); }
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
