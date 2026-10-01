using System.IO;
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
    /// <summary>Why memory could not be read for this turn (the reply went ahead without it).</summary>
    internal string? MemoryProblem { get; set; }
    internal ListeningOptions? Listening { get; init; }
    internal Voiceprint? Voiceprint { get; init; }
    internal SpeakerCheck? SpeakerCheck { get; set; }
    /// <summary>An unprompted screen glance rather than a reply to the user.</summary>
    internal bool Commentary { get; init; }
    /// <summary>The glance ended in silence: the model answered [pass].</summary>
    internal bool Passed { get; set; }
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
        if (Authorization.IsCanceled) run.CancelAsync().Forget();
        else if (Volatile.Read(ref releasedPress) != 0) run.ReleaseAsync().Forget();
    }
    internal void Attach(ConversationTurn run)
    {
        Volatile.Write(ref turn, run);
        if (Authorization.IsCanceled) run.StopAsync().Forget();
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
    private const int MaximumPendingCaptures = 4;
    private readonly object gate = new();
    private readonly SetupOperationRunner operations;
    private readonly ISetupService settings;
    private readonly ICredentialStore vault;
    private readonly ICaptureDeviceFactory captureDevices;
    private readonly ConversationRuntime runtime;
    private readonly OpenAiTranscriptionAdapter transcription;
    private readonly HostTranscriptionAdapter hostTranscription;
    private readonly ParticipationPolicy policy;
    private readonly ConversationContextBuffer context;
    private readonly TimeProvider clock;
    private readonly Func<int, int> nextStyle;
    private readonly Action? revokeAvatar;
    private readonly DesktopMemoryService? memory;
    private readonly VoiceIdentity? voiceIdentity;
    private readonly TaskCompletionSource quarantine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // What Martlet said while watching the screen (last 30 minutes), so it does not repeat itself. In memory only.
    private readonly Queue<(long At, string Text)> remarks = new();
    // Remembering runs after a reply on its own text-only runtime, one exchange at a time, so it never delays the next turn.
    private readonly Func<IProviderCredentialSource, TimeProvider, ConversationRuntime>? runtimeFactory;
    private readonly ConversationCredentialSource captureCredentials;
    private ConversationRuntime? captureRuntime;
    private ConversationAuthorization? captureAuthorization;
    private CancellationTokenSource captureCancel = new();
    private Task captureTail = Task.CompletedTask;
    private int capturesPending;
    private bool captureQuarantined;
    private LiveConversationOperation? active;
    private LiveConversationConfiguration? configuration;
    private long revision, captureEpoch;
    private bool paused, muted, locked, disposed;

    internal bool IsRunning => operations.IsRunning;
    internal int ContextTurns { get { lock (gate) return context.Count; } }
    internal LiveConversationConfiguration? Configuration { get { lock (gate) return configuration; } }
    internal ParticipationSnapshot PolicySnapshot => policy.Snapshot;
    internal (bool Paused, bool Muted, bool Locked) Controls { get { lock (gate) return (paused, muted, locked); } }
    /// <summary>Raised off the dispatcher after a finished exchange changed memory or could not be remembered.</summary>
    internal event Action<MemoryCaptureReport>? MemoryCaptured;
    /// <summary>Tests turn background remembering off to inspect only the reply request.</summary>
    internal bool AutoCapture { get; set; } = true;
    internal Task MemoryCaptureIdle { get { lock (gate) return captureTail; } }
    internal LiveConversationController(SetupOperationRunner operations, ISetupService settings, ICredentialStore vault,
        ICaptureDeviceFactory captureDevices, IPlaybackDeviceFactory playbackDevices, TimeProvider? clock = null,
        Func<IProviderCredentialSource, TimeProvider, ConversationRuntime>? runtimeFactory = null,
        Func<IProviderCredentialSource, TimeProvider, OpenAiTranscriptionAdapter>? transcriptionFactory = null,
        Func<int, int>? nextStyle = null,
        DesktopMemoryService? memory = null,
        GeneratedSpeechObserver? generatedSpeech = null, Action? revokeAvatar = null, VoiceIdentity? voiceIdentity = null,
        IHostTranscriptionClient? hostListener = null, string? dataDirectory = null, SpokenTextFeed? spokenText = null)
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
        this.runtimeFactory = runtimeFactory;
        context = new(this.clock);
        captureCredentials = new(() => Volatile.Read(ref captureAuthorization));
        var credentials = new ConversationCredentialSource(() => Volatile.Read(ref active)?.Authorization);
        runtime = runtimeFactory?.Invoke(credentials, this.clock) ??
            ConversationRuntime.Create(credentials, playbackDevices, clock: this.clock, generatedSpeech: generatedSpeech,
                hostText: new HostTextClient(), hostSpeech: dataDirectory is null ? null : new HostSpeechClient(dataDirectory),
                spokenText: spokenText, windowsVoice: new WindowsVoiceClient());
        transcription = transcriptionFactory?.Invoke(credentials, this.clock) ??
            OpenAiTranscriptionAdapter.Create(credentials, this.clock);
        hostTranscription = new(hostListener ?? new HostTranscriptionClient(), this.clock);
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
            ClearContextLocked();
            if (configuration?.Revision != next?.Revision) CancelCapturesLocked();
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
            ClearContextLocked();
            if (pause || mute || sessionLocked) CancelCapturesLocked();
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
            ClearContextLocked();
            if (value) CancelCapturesLocked();
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
            ClearContextLocked();
            CancelCapturesLocked();
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
        ListeningOptions? listening = null)
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
            long acceptedRevision = revision = checked(revision + 1);
            var authorization = new ConversationAuthorization(selected, voice, microphone, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller);
            operation = new(authorization, caller)
            {
                MemoryRequested = memory is not null && selected.Memory is { Enabled: true },
                Listening = listening, Voiceprint = voiceprint
            };
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
        SuperviseAsync(operation).Forget();
        return operation;
    }

    private void ClearContextLocked()
    {
        context.Clear();
        remarks.Clear();
    }

    internal static TimeSpan RemarkMemory => TimeSpan.FromMinutes(30);

    /// <summary>One unprompted screen glance: the image, the window title and recent context go to the Thinking model,
    /// which either answers [pass] (silence) or one short remark that is spoken like any reply. It bypasses the
    /// participation policy (that decides whether to answer the user); the caller's pacer decides when to look.</summary>
    internal LiveConversationOperation StartCommentary(BoundedImage image, string windowTitle, Chattiness chattiness, bool voice,
        bool screenApproved, WatchSource? source = null, CancellationToken caller = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!screenApproved) throw new LiveActionException("conversation.permission_required");
        caller.ThrowIfCancellationRequested();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConversationOperation operation;
        lock (gate)
        {
            if (disposed || paused || muted || locked) throw new LiveActionException("conversation.controls_blocked");
            if (operations.IsRunning) throw new LiveActionException("conversation.ownership_busy");
            var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
            if (selected.Unavailable(voice, false) is not null) throw new LiveActionException("conversation.configuration_unsupported");
            if (selected.Vision() == VisionSupport.Unsupported) throw new LiveActionException("commentary.vision_unsupported");
            long acceptedRevision = revision = checked(revision + 1);
            var authorization = new ConversationAuthorization(selected, voice, false, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller, screen: true);
            operation = new(authorization, caller) { Commentary = true };
            active = operation;
            var camera = source is { IsScreen: false };
            var prompt = CommentaryPromptLocked(windowTitle, camera);
            var worker = operations.TryStart(async token =>
            {
                await published.Task.ConfigureAwait(false);
                authorization.BindWorker(token);
                return await RunCommentaryAsync(operation, prompt, image, chattiness, camera, token).ConfigureAwait(false);
            });
            if (worker is null)
            {
                authorization.Revoke();
                throw new LiveActionException("conversation.ownership_busy");
            }
            operation.Worker = worker;
        }
        published.SetResult();
        SuperviseAsync(operation).Forget();
        return operation;
    }

    private string CommentaryPromptLocked(string windowTitle, bool camera = false)
    {
        while (remarks.TryPeek(out var oldest) && clock.GetElapsedTime(oldest.At) >= RemarkMemory) remarks.Dequeue();
        var title = new string(windowTitle.Where(c => !char.IsControl(c) && c != '"').Take(80).ToArray()).Trim();
        var prompt = camera ? "(Camera glance." + (title.Length > 0 ? $" Camera: \"{title}\"." : "")
            : "(Screen glance." + (title.Length > 0 ? $" Active window: \"{title}\"." : "");
        if (remarks.Count > 0)
            prompt += " What you already said while watching, oldest first: " + string.Join(" | ", remarks.Select(r => $"\"{r.Text}\"")) + ".";
        return prompt + $" Reply [{LiveConversationConfiguration.SilentReply}] or one short remark.)";
    }

    internal static bool IsSilentReply(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith("[" + LiveConversationConfiguration.SilentReply, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed.Trim('[', ']', '(', ')', '<', '>', '*', '"', '\'', '.', '!', ' '),
                LiveConversationConfiguration.SilentReply, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SetupWorkResult> RunCommentaryAsync(LiveConversationOperation operation, string prompt, BoundedImage image,
        Chattiness chattiness, bool camera, CancellationToken worker)
    {
        try
        {
            await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
            ConversationTurn turn;
            lock (gate)
            {
                operation.Authorization.Check(worker);
                var configured = operation.Authorization.Configuration;
                var persona = configured.Persona;
                ResponseStyle? style = persona is null ? null : ResponseStyleSelector.Select(persona.Styles, nextStyle);
                var history = context.Snapshot();
                var request = configured.Request(new(prompt), operation.Authorization.Voice, style, history, null,
                    out var usedHistory, out _, image, LiveConversationConfiguration.CommentaryInstructions(chattiness, camera),
                    LiveConversationConfiguration.SilentReply);
                operation.PersonaRevision = persona?.ConfigurationRevision;
                operation.ResponseStyle = style;
                operation.ContextMessages = usedHistory;
                operation.ContextMessagesOmitted = history.Count - usedHistory;
                operation.Authorization.BindInput(request.Input);
                operation.Publish(new("commentary.looking"));
                turn = runtime.Start(request, operation.Authorization, operation.OriginalCaller);
                operation.Attach(turn);
            }
            var terminal = await turn.Completion.ConfigureAwait(false);
            var text = turn.Content.Text;
            var passed = terminal.State == ConversationState.Completed && IsSilentReply(text);
            operation.Passed = passed;
            if (terminal.State == ConversationState.Completed && !passed)
            {
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled)
                    {
                        var remark = text.Trim();
                        context.Add("(You glanced at my screen.)", remark);
                        remarks.Enqueue((clock.GetTimestamp(), remark.Length > 200 ? remark[..200] : remark));
                        while (remarks.Count > 4) remarks.Dequeue();
                    }
                }
            }
            operation.Publish(new(passed ? "commentary.passed" : "runtime." + terminal.State, Finished: true,
                Quarantined: terminal.Quarantined, ProviderFailure: terminal.ProviderFailure, AudioFailure: terminal.Playback?.Error?.Code));
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
        catch (ContractException error)
        {
            operation.Publish(new("conversation.invalid_input", Finished: true, AudioFailure: error.Code));
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
                operation.FinishExecution();
                if (ReferenceEquals(active, operation)) RevokeLocked();
            }
        }
    }

    // keepContext: a brief hand-over (an idle listen yielding to a screen glance, or a glance yielding to the user),
    // not the user ending the conversation.
    internal void Stop(LiveConversationOperation operation, string reason = "conversation.canceled", bool keepContext = false)
    {
        lock (gate)
        {
            if (!ReferenceEquals(operation, active)) return;
            memory?.Invalidate();
            if (!keepContext) ClearContextLocked();
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
                    var stt = operation.Authorization.Configuration.Route(SetupRole.Stt);
                    // Listening handed to a paired host: the utterance goes only to its pinned gateway.
                    result = operation.Authorization.Configuration.SttHostTarget() is { } listener
                        ? await hostTranscription.TranscribeAsync(context, listener, stt.ModelId, audio,
                            LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false)
                        : await transcription.TranscribeAsync(context, stt.ModelId, audio,
                            LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false);
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

            DesktopMemoryRecall? memoryResult = null;
            if (operation.MemoryRequested)
            {
                operation.Publish(new("memory.recalling"));
                memoryResult = await RecallAsync(operation, input!.UserText, worker).ConfigureAwait(false);
                operation.Authorization.Check(worker);
                await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
                operation.MemoryStoreRevision = memoryResult?.StoreRevision;
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
                operation.MemoryFactsOmitted = (memoryResult?.Facts.Count ?? 0) - usedMemory;
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
                    {
                        var earlier = context.Snapshot();
                        context.Add(input!.UserText, turn.Content.Text);
                        if (operation.MemoryRequested)
                            EnqueueCaptureLocked(operation.Authorization.Configuration, earlier, input.UserText, turn.Content.Text);
                    }
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

    // Memory helps but is never required: if the store can't be read right now, the reply goes ahead without it.
    private async Task<DesktopMemoryRecall?> RecallAsync(LiveConversationOperation operation, string query, CancellationToken worker)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await memory!.RecallAsync(operation.Authorization.Configuration.Memory!, query,
                    DesktopMemoryService.MaximumRecalledFacts, worker).ConfigureAwait(false);
            }
            catch (DesktopMemoryException error) when (error.Code == "memory.retrieval_invalidated" && attempt == 0)
            {
                // A fact changed while it was read; read the current facts once more unless this action was revoked.
                operation.Authorization.Check(worker);
            }
            catch (Exception error) when (error is DesktopMemoryException or MemoryException or ContractException or
                IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                operation.Authorization.Check(worker);
                operation.MemoryProblem = error switch
                {
                    DesktopMemoryException app => app.Code,
                    MemoryException store => "memory." + store.Failure,
                    _ => "memory.unavailable"
                };
                operation.Publish(new("memory.unavailable"));
                return null;
            }
        }
    }

    private void EnqueueCaptureLocked(LiveConversationConfiguration configured, IReadOnlyList<TextHistoryMessage> earlier,
        string user, string reply)
    {
        if (!AutoCapture || memory is null || disposed || captureQuarantined || configured.Memory is not { Enabled: true } ||
            capturesPending >= MaximumPendingCaptures)
            return;
        capturesPending++;
        var job = new MemoryCaptureJob(configured,
            earlier.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text,
            earlier.LastOrDefault(message => message.Role == TextHistoryRole.Assistant)?.Text,
            user, reply, captureCancel.Token);
        captureTail = CaptureAfterAsync(captureTail, job);
    }

    private sealed record MemoryCaptureJob(LiveConversationConfiguration Configuration, string? EarlierUser, string? EarlierReply,
        string User, string Reply, CancellationToken Token)
    {
        public override string ToString() => nameof(MemoryCaptureJob);
    }

    private async Task CaptureAfterAsync(Task previous, MemoryCaptureJob job)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        MemoryCaptureReport? report;
        try
        {
            report = await Task.Run(() => CaptureAsync(job)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ErrorLog.Warn("Remembering a conversation exchange failed.", error);
            report = new(Failure: "memory." + error.GetType().Name);
        }
        finally
        {
            lock (gate) capturesPending--;
        }
        if (report is not null) MemoryCaptured?.Invoke(report);
    }

    private async Task<MemoryCaptureReport?> CaptureAsync(MemoryCaptureJob job)
    {
        var token = job.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var expected = job.Configuration.Memory!;
            var known = await memory!.KnownFactsAsync(expected, job.User, MemoryCapture.MaximumShownFacts, token).ConfigureAwait(false);
            var prompt = MemoryCapture.Prompt(job.EarlierUser, job.EarlierReply, job.User, job.Reply, known.Facts);
            var request = job.Configuration.MemoryCaptureRequest(prompt.Input);
            var capture = CaptureRuntime();
            var authorization = new ConversationAuthorization(job.Configuration, voice: false, microphone: false, clock,
                () => !token.IsCancellationRequested, settings.LoadAsync, vault, token);
            authorization.BindInput(request.Input);
            Volatile.Write(ref captureAuthorization, authorization);
            string answer;
            try
            {
                var turn = capture.Start(request, authorization, token);
                var terminal = await turn.Completion.ConfigureAwait(false);
                await turn.OwnershipRelease.ConfigureAwait(false);
                if (turn.Snapshot.Quarantined)
                    lock (gate) captureQuarantined = true;
                if (terminal.State != ConversationState.Completed)
                    return token.IsCancellationRequested ? null
                        : new(Failure: terminal.ProviderFailure?.ToString() ?? "runtime." + terminal.State);
                answer = turn.Content.Text;
            }
            finally
            {
                Interlocked.CompareExchange(ref captureAuthorization, null, authorization);
            }
            var operations = MemoryCapture.Parse(answer, prompt.ShownFacts);
            if (operations.Count == 0) return null;
            var changes = await memory.RememberAsync(expected.ConfigurationRevision,
                known.Facts.Take(prompt.ShownFacts).ToArray(), operations, token).ConfigureAwait(false);
            return changes.Count == 0 ? null : new(changes);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is LiveActionException or DesktopMemoryException or MemoryException or
            ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Turning memory off, changing settings or revoking live work simply drops what was still being remembered.
            var code = error switch
            {
                LiveActionException live => live.Code,
                DesktopMemoryException app => app.Code,
                MemoryException store => "memory." + store.Failure,
                _ => "memory.unavailable"
            };
            return token.IsCancellationRequested || code is "memory.disabled" or "memory.configuration_changed" or
                "conversation.configuration_changed" or "conversation.revoked" ? null : new(Failure: code);
        }
    }

    private ConversationRuntime CaptureRuntime()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return captureRuntime ??= runtimeFactory?.Invoke(captureCredentials, clock) ??
                ConversationRuntime.Create(captureCredentials, clock: clock, hostText: new HostTextClient());
        }
    }

    private void CancelCapturesLocked()
    {
        var previous = captureCancel;
        captureCancel = new();
        previous.CancelAsync().Forget();
    }

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
            ClearContextLocked();
            CancelCapturesLocked();
            owned = RevokeLocked();
        }
        owned?.Cancel("conversation.closed");
        DisposeCaptureRuntimeAsync().Forget();
        // Never wait for native cleanup on the dispatcher. The shared slot remains reserved until real exit.
        await runtime.DisposeAsync().ConfigureAwait(false);
        if (owned is null || owned.Worker.Completion.IsCompleted) transcription.Dispose();
        else DisposeAfterReleaseAsync(owned).Forget();
    }
    private async Task DisposeCaptureRuntimeAsync()
    {
        Task tail;
        lock (gate) tail = captureTail;
        await tail.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        ConversationRuntime? owned;
        lock (gate) owned = captureRuntime;
        if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
    }
    private async Task DisposeAfterReleaseAsync(LiveConversationOperation operation)
    {
        await operation.Worker.Completion.ConfigureAwait(false);
        transcription.Dispose();
    }
}
