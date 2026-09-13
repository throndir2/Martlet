using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Core.Streaming;

/// <summary>
/// Serialized-owner STT/LLM event ingress and bounded text delivery. The owner drives Poll
/// while idle; no background work, transport cancellation, retry, or logging is performed here.
/// </summary>
public sealed class ProviderSequenceValidator : IDisposable
{
    private readonly TimeProvider clock;
    private readonly SequenceLimits limits;
    private readonly Queue<ValidatedTextChunk> text = new();
    private readonly List<byte[]> fingerprints = [];
    private readonly List<TextStreamRequest> attempts = [];
    private TextStreamRequest request;
    private CancellationToken cancellationToken;
    private long startedAt;
    private long lastProgressAt;
    private long epoch;
    private int ingress;
    private int accepted;
    private int characters;
    private int delivered;
    private int peakQueued;
    private bool started;
    private bool hasNonWhitespaceText;
    private bool stopped;
    private bool disposed;
    private ProviderEventKind? terminal;
    private TurnResult? result;
    private SequenceIssue issue;
    private CancellationRequest cancellation;

    public ProviderSequenceValidator(TextStreamRequest request, SequenceLimits? limits = null,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        this.limits = limits ?? new SequenceLimits();
        this.limits.Validate();
        clock = timeProvider ?? TimeProvider.System;
        this.request = request;
        this.cancellationToken = cancellationToken;
        epoch = request.Epoch;
        startedAt = lastProgressAt = clock.GetTimestamp();
        attempts.Add(request);
    }

    public SequenceSnapshot Snapshot => new(request.Ids, request.Epoch, epoch, terminal, result, issue,
        cancellation, request.Capabilities.Cancellation, ingress, accepted, characters,
        delivered, text.Count, peakQueued, stopped);

    public string? RefusalText { get; private set; }

    public SequenceUpdate Poll()
    {
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
            StopCore();
        if (result is not null)
            return Update(SequenceDecision.Terminal);
        var now = clock.GetTimestamp();
        if (clock.GetElapsedTime(startedAt, now) >= limits.TotalTimeout)
            return Fail(SequenceIssue.TotalDeadline, ErrorCode.ProviderFailed);
        if (!started && clock.GetElapsedTime(startedAt, now) >= limits.FirstEventTimeout)
            return Fail(SequenceIssue.FirstEventDeadline, ErrorCode.ProviderFailed);
        if (started && clock.GetElapsedTime(lastProgressAt, now) >= limits.IdleTimeout)
            return Fail(SequenceIssue.IdleDeadline, ErrorCode.ProviderFailed);
        return Update(SequenceDecision.Accepted);
    }

    public SequenceUpdate AcceptJson(ReadOnlyMemory<byte> json, TextOverflowPolicy overflow = TextOverflowPolicy.Backpressure)
    {
        Poll();
        if (result is not null)
            return Update(SequenceDecision.ClosedDiscarded);
        ProviderEvent value;
        try
        {
            value = ContractJson.Read<ProviderEvent>(json);
        }
        catch (ContractException error)
        {
            return Fail(SequenceIssue.InvalidInput, error.Code);
        }
        return Accept(value, overflow);
    }

    public SequenceUpdate Accept(ProviderEvent value, TextOverflowPolicy overflow = TextOverflowPolicy.Backpressure)
    {
        Poll();
        if (result is not null)
            return Update(SequenceDecision.ClosedDiscarded);
        ContractRules.Defined(overflow);
        if (++ingress > limits.MaxIngressEvents)
            return Fail(SequenceIssue.EventLimit, ErrorCode.PayloadTooLarge);
        try
        {
            ContractRules.Require(value is not null, "An event is required.");
            value!.Validate();
            ValidateUtf16(value.Text);
            ValidateUtf16(value.Error?.Summary);
        }
        catch (ContractException error)
        {
            return Fail(SequenceIssue.InvalidInput, error.Code);
        }
        if (IsRetired(value))
            return Update(SequenceDecision.StaleDiscarded);
        if (value.Ids != request.Ids || value.ProviderId != request.Capabilities.ProviderId)
            return Fail(SequenceIssue.WrongCorrelation, ErrorCode.InvalidContract);
        if (value.Epoch != request.Epoch)
            return Fail(SequenceIssue.WrongEpoch, ErrorCode.InvalidContract);
        if (value.Provenance != request.Capabilities.Provenance)
            return Fail(SequenceIssue.WrongProvenance, ErrorCode.InvalidContract);

        var hash = SHA256.HashData(ContractJson.Write(value));
        if (value.Sequence < fingerprints.Count)
            return fingerprints[(int)value.Sequence].AsSpan().SequenceEqual(hash)
                ? Update(SequenceDecision.DuplicateDiscarded)
                : Fail(SequenceIssue.ConflictingDuplicate, ErrorCode.InvalidContract);
        if (value.Sequence != fingerprints.Count)
            return Fail(SequenceIssue.SequenceGap, ErrorCode.StreamTruncated);
        if ((!started && value.Kind != ProviderEventKind.Started) ||
            (started && value.Kind == ProviderEventKind.Started) || value.FinalSampleCount is not null)
            return Fail(SequenceIssue.InvalidOrder, ErrorCode.InvalidContract);
        if ((value.Kind == ProviderEventKind.NoSpeech && (request.Capabilities.Role != ProviderRole.Stt || characters > 0)) ||
            (value.Kind == ProviderEventKind.Refused && request.Capabilities.Role != ProviderRole.Llm) ||
            (value.Error is not null && value.Error.Stage != RequestStage))
            return Fail(SequenceIssue.InvalidOrder, ErrorCode.InvalidContract);
        if (value.Kind == ProviderEventKind.TextDelta)
        {
            var support = request.Capabilities.Role == ProviderRole.Llm
                ? request.Capabilities.LlmTextDeltas : request.Capabilities.SttPartials;
            if (support != CapabilitySupport.Supported)
                return Fail(support == CapabilitySupport.Unknown ? SequenceIssue.UnknownCapability :
                    SequenceIssue.UnsupportedCapability, ErrorCode.ProviderCapability);
        }
        // A completed payload is the sole response, not a replay of previous deltas.
        if (value.Kind == ProviderEventKind.Completed && value.Text is { Length: > 0 } && characters > 0)
            return Fail(SequenceIssue.InvalidOrder, ErrorCode.InvalidContract);
        if (value.Kind == ProviderEventKind.Completed && !hasNonWhitespaceText && string.IsNullOrWhiteSpace(value.Text))
            return Fail(SequenceIssue.EmptyCompletion, ErrorCode.InvalidContract);

        var addedCharacters = value.Text?.Length ?? 0;
        if (addedCharacters > limits.MaxTextCharacters - characters)
            return Fail(SequenceIssue.TextLimit, ErrorCode.PayloadTooLarge);
        var yieldsText = value.Kind is ProviderEventKind.TextDelta or ProviderEventKind.Completed && addedCharacters > 0;
        if (yieldsText && text.Count == limits.MaxQueuedChunks)
            return overflow == TextOverflowPolicy.Backpressure
                ? Update(SequenceDecision.Backpressured)
                : Fail(SequenceIssue.QueueOverflow, ErrorCode.PayloadTooLarge);
        fingerprints.Add(hash);
        accepted++;
        started = true;
        lastProgressAt = clock.GetTimestamp();
        characters += addedCharacters;
        hasNonWhitespaceText |= !string.IsNullOrWhiteSpace(value.Text);
        if (yieldsText)
        {
            text.Enqueue(new(value.Ids, value.Epoch, value.Sequence, value.Text!));
            peakQueued = Math.Max(peakQueued, text.Count);
        }
        if (value.IsTerminal)
        {
            terminal = value.Kind;
            switch (value.Kind)
            {
                case ProviderEventKind.Failed:
                    issue = SequenceIssue.ProviderFailure;
                    Finish(TurnOutcome.Failed, error: value.Error);
                    break;
                case ProviderEventKind.Canceled:
                    Finish(TurnOutcome.Canceled);
                    break;
                case ProviderEventKind.NoSpeech:
                    Finish(TurnOutcome.Suppressed, SuppressionReason.NoSpeech);
                    break;
                case ProviderEventKind.Refused:
                    RefusalText = value.Text;
                    Finish(TurnOutcome.Refused);
                    break;
                default:
                    Finish(TurnOutcome.Completed);
                    break;
            }
            if (value.Kind != ProviderEventKind.Completed)
                text.Clear();
            return Update(SequenceDecision.Terminal);
        }
        return Update(SequenceDecision.Accepted);
    }

    public bool TryReadText(out ValidatedTextChunk? chunk)
    {
        Poll();
        if (!text.TryDequeue(out chunk))
            return false;
        delivered++;
        return true;
    }

    public SequenceUpdate EndOfInput()
    {
        Poll();
        return result is null ? Fail(SequenceIssue.MissingTerminal, ErrorCode.StreamTruncated) : Update(SequenceDecision.Terminal);
    }

    public SequenceUpdate Stop()
    {
        ThrowIfDisposed();
        StopCore();
        return Update(SequenceDecision.Terminal);
    }

    public SequenceUpdate Suppress(SuppressionReason reason)
    {
        Poll();
        ContractRules.Defined(reason);
        if (result is not null)
            return Update(SequenceDecision.Terminal);
        ContractRules.Require(!started, "Policy suppression must precede provider activity.");
        Finish(TurnOutcome.Suppressed, reason);
        return Update(SequenceDecision.Terminal);
    }

    /// <summary>Explicit transitions only. Returns the closed previous attempt, never automatically retries.</summary>
    public SequenceSnapshot Transition(TextStreamRequest next, AttemptTransition transition,
        CancellationToken nextCancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(next);
        next.Validate();
        ContractRules.Defined(transition);
        Poll();
        ContractRules.Require(attempts.Count < limits.MaxAttempts, "Attempt budget exhausted.", ErrorCode.PayloadTooLarge);
        ContractRules.Require(next.Ids.SessionId == request.Ids.SessionId && next.Epoch == epoch + 1 &&
            attempts.All(x => x.Ids.RequestId != next.Ids.RequestId), "Transitions require the next epoch and a fresh request in the same session.");
        if (transition == AttemptTransition.Retry)
            ContractRules.Require(next.Ids.TurnId == request.Ids.TurnId &&
                result?.Outcome is TurnOutcome.Failed or TurnOutcome.Canceled && characters == 0,
                "Retry requires a failed/canceled attempt without accepted text and retains its turn.");
        else
            ContractRules.Require(next.Ids.TurnId != request.Ids.TurnId &&
                attempts.All(x => x.Ids.TurnId != next.Ids.TurnId), "Replacement requires a fresh turn.");

        // The replacement epoch also invalidates the prior attempt: do not increment twice.
        text.Clear();
        RefusalText = null;
        RequestCancellation();
        if (result is null)
            Finish(TurnOutcome.Canceled);
        epoch = next.Epoch;
        stopped = true;
        var previous = Snapshot;
        request = next;
        attempts.Add(next);
        epoch = next.Epoch;
        cancellationToken = nextCancellationToken;
        startedAt = lastProgressAt = clock.GetTimestamp();
        ingress = accepted = characters = delivered = peakQueued = 0;
        started = stopped = hasNonWhitespaceText = false;
        terminal = null;
        result = null;
        issue = SequenceIssue.None;
        cancellation = CancellationRequest.None;
        fingerprints.Clear();
        return previous;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        StopCore();
        fingerprints.Clear();
        attempts.Clear();
        disposed = true;
    }

    private bool IsRetired(ProviderEvent value) => attempts.Take(attempts.Count - 1).Any(x =>
        x.Ids == value.Ids && x.Epoch == value.Epoch && x.Capabilities.ProviderId == value.ProviderId &&
        x.Capabilities.Provenance == value.Provenance);

    private void StopCore()
    {
        if (!stopped)
        {
            epoch++;
            stopped = true;
        }
        text.Clear();
        RefusalText = null;
        RequestCancellation();
        if (result is null)
            Finish(TurnOutcome.Canceled);
    }

    private SequenceUpdate Fail(SequenceIssue reason, ErrorCode code)
    {
        issue = reason;
        text.Clear();
        RequestCancellation();
        Finish(TurnOutcome.Failed, error: new MartletError
        {
            Code = code, Stage = RequestStage, Retryable = false,
            Summary = "The provider text stream did not satisfy its bounded sequence contract.",
            ActionId = "provider.stream.inspect"
        });
        return Update(SequenceDecision.Terminal);
    }

    private void RequestCancellation() => cancellation = request.Capabilities.Cancellation switch
    {
        CancellationCapability.RequestAbort => CancellationRequest.AbortRequest,
        CancellationCapability.CooperativeComputeCancel => CancellationRequest.CooperativeComputeCancel,
        _ => CancellationRequest.LocalDiscardOnly
    };

    private Stage RequestStage => request.Capabilities.Role == ProviderRole.Stt ? Stage.Transcription : Stage.Generation;
    private SequenceUpdate Update(SequenceDecision decision) => new(decision, Snapshot);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private void Finish(TurnOutcome outcome, SuppressionReason? suppression = null, MartletError? error = null)
    {
        result = new TurnResult
        {
            Version = ContractVersion.Current, Ids = request.Ids, Provenance = request.Capabilities.Provenance,
            Outcome = outcome, Suppression = suppression, Error = error
        };
        result.Validate();
    }

    private static void ValidateUtf16(string? value)
    {
        if (value is null)
            return;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i]))
                continue;
            ContractRules.Require(char.IsHighSurrogate(value[i]) && i + 1 < value.Length &&
                char.IsLowSurrogate(value[++i]), "Text contains invalid UTF-16.");
        }
    }
}
