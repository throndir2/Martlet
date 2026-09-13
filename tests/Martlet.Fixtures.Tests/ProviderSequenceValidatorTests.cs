using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using static Martlet.Fixtures.Tests.SequenceTestData;

namespace Martlet.Fixtures.Tests;

public sealed class ProviderSequenceValidatorTests
{
    [Fact]
    public void AcceptedDeltasAreDeliveredExactlyOnceAndCompletionDoesNotReplayThem()
    {
        var request = Request();
        using var stream = new ProviderSequenceValidator(request);
        Assert.Equal(SequenceDecision.Accepted, stream.Accept(Event(request, ProviderEventKind.Started, 0)).Decision);
        var delta = Event(request, ProviderEventKind.TextDelta, 1, "Synthetic text.");
        stream.Accept(delta);
        Assert.Equal(SequenceDecision.DuplicateDiscarded, stream.AcceptJson(ContractJson.Write(delta)).Decision);
        Assert.True(stream.TryReadText(out var chunk));
        Assert.Equal(new ValidatedTextChunk(request.Ids, 0, 1, "Synthetic text."), chunk);
        Assert.False(stream.TryReadText(out _));
        var done = stream.Accept(Event(request, ProviderEventKind.Completed, 2));
        Assert.Equal(TurnOutcome.Completed, done.Snapshot.Result!.Outcome);
        Assert.Equal(ProviderEventKind.Completed, done.Snapshot.ProviderTerminal);
        Assert.Equal(3, done.Snapshot.AcceptedEvents);
        Assert.Equal(4, done.Snapshot.IngressEvents);
        Assert.Equal(SequenceDecision.ClosedDiscarded, stream.Accept(delta).Decision);
        Assert.Equal(done.Snapshot.Result, stream.EndOfInput().Snapshot.Result);
    }

    [Theory]
    [InlineData(ProviderEventKind.Completed, TurnOutcome.Completed, ProviderRole.Llm)]
    [InlineData(ProviderEventKind.Refused, TurnOutcome.Refused, ProviderRole.Llm)]
    [InlineData(ProviderEventKind.NoSpeech, TurnOutcome.Suppressed, ProviderRole.Stt)]
    [InlineData(ProviderEventKind.Canceled, TurnOutcome.Canceled, ProviderRole.Llm)]
    [InlineData(ProviderEventKind.Failed, TurnOutcome.Failed, ProviderRole.Llm)]
    public void TerminalMeaningsSurviveProductionJson(ProviderEventKind kind, TurnOutcome outcome, ProviderRole role)
    {
        var request = Request(role);
        using var stream = new ProviderSequenceValidator(request);
        stream.Accept(Event(request, ProviderEventKind.Started, 0));
        var body = kind is ProviderEventKind.Completed or ProviderEventKind.Refused ? "Authored fixture." : null;
        var result = stream.AcceptJson(ContractJson.Write(Event(request, kind, 1, body))).Snapshot.Result!;
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(result, ContractJson.Read<TurnResult>(ContractJson.Write(result)));
        Assert.Equal(kind == ProviderEventKind.NoSpeech ? SuppressionReason.NoSpeech : null, result.Suppression);
        Assert.Equal(kind == ProviderEventKind.Failed, result.Error is not null);
        Assert.Equal(kind == ProviderEventKind.Refused ? body : null, stream.RefusalText);
        Assert.Equal(kind == ProviderEventKind.Completed, stream.TryReadText(out _));
    }

    [Fact]
    public void RefusedIsAnAppendedExactTokenNotCompletedOrFailure()
    {
        Assert.Equal(0, (int)TurnOutcome.Completed);
        Assert.Equal(3, (int)TurnOutcome.Failed);
        Assert.Equal(4, (int)TurnOutcome.Refused);
        var result = new TurnResult
        {
            Version = ContractVersion.Current, Ids = Request().Ids,
            Provenance = EvidenceProvenance.Fixture, Outcome = TurnOutcome.Refused
        };
        var json = Encoding.UTF8.GetString(ContractJson.Write(result));
        Assert.Contains("\"outcome\": \"refused\"", json);
        foreach (var token in new[] { "Refused", " refused", "4", "completed, refused" })
            Assert.Throws<ContractException>(() => ContractJson.Read<TurnResult>(
                Encoding.UTF8.GetBytes(json.Replace("\"refused\"", $"\"{token}\"", StringComparison.Ordinal))));
        Assert.Throws<ContractException>(() => (result with { Suppression = SuppressionReason.NoSpeech }).Validate());
        Assert.Throws<ContractException>(() => (result with { Error = Event(Request(), ProviderEventKind.Failed, 0).Error }).Validate());
    }

    [Fact]
    public void LocalSuppressionIsAllowedOnlyBeforeProviderActivity()
    {
        using var stream = new ProviderSequenceValidator(Request());
        var result = stream.Suppress(SuppressionReason.NotAddressed).Snapshot;
        Assert.Null(result.ProviderTerminal);
        Assert.Equal(TurnOutcome.Suppressed, result.Result!.Outcome);
        Assert.Null(result.Result.Error);
        Assert.Equal(0, result.IngressEvents);
        using var active = new ProviderSequenceValidator(Request());
        active.Accept(Event(Request(), ProviderEventKind.Started, 0));
        Assert.Throws<ContractException>(() => active.Suppress(SuppressionReason.Cooldown));
    }

    [Theory]
    [InlineData("missing-start", SequenceIssue.InvalidOrder, ErrorCode.InvalidContract)]
    [InlineData("second-start", SequenceIssue.InvalidOrder, ErrorCode.InvalidContract)]
    [InlineData("gap", SequenceIssue.SequenceGap, ErrorCode.StreamTruncated)]
    [InlineData("conflicting-duplicate", SequenceIssue.ConflictingDuplicate, ErrorCode.InvalidContract)]
    [InlineData("empty", SequenceIssue.EmptyCompletion, ErrorCode.InvalidContract)]
    [InlineData("whitespace", SequenceIssue.EmptyCompletion, ErrorCode.InvalidContract)]
    [InlineData("replayed-full-text", SequenceIssue.InvalidOrder, ErrorCode.InvalidContract)]
    [InlineData("audio-count", SequenceIssue.InvalidOrder, ErrorCode.InvalidContract)]
    [InlineData("stage", SequenceIssue.InvalidOrder, ErrorCode.InvalidContract)]
    [InlineData("no-speech-llm", SequenceIssue.InvalidOrder, ErrorCode.InvalidContract)]
    [InlineData("refused-stt", SequenceIssue.InvalidOrder, ErrorCode.InvalidContract)]
    [InlineData("no-speech-after-partial", SequenceIssue.InvalidOrder, ErrorCode.InvalidContract)]
    public void InvalidTemporalVocabularyFailsClosed(string fault, SequenceIssue issue, ErrorCode code)
    {
        var request = Request(fault is "refused-stt" or "no-speech-after-partial" ? ProviderRole.Stt : ProviderRole.Llm);
        using var stream = new ProviderSequenceValidator(request);
        if (fault != "missing-start")
            stream.Accept(Event(request, ProviderEventKind.Started, 0));
        if (fault is "replayed-full-text" or "no-speech-after-partial")
            stream.Accept(Event(request, ProviderEventKind.TextDelta, 1, "Partial."));
        var value = fault switch
        {
            "missing-start" => Event(request, ProviderEventKind.Completed, 0, "Synthetic."),
            "second-start" => Event(request, ProviderEventKind.Started, 1),
            "gap" => Event(request, ProviderEventKind.Completed, 2, "Synthetic."),
            "conflicting-duplicate" => Event(request, ProviderEventKind.Completed, 0, "Changed."),
            "empty" => Event(request, ProviderEventKind.Completed, 1),
            "whitespace" => Event(request, ProviderEventKind.Completed, 1, " \t"),
            "replayed-full-text" => Event(request, ProviderEventKind.Completed, 2, "Partial."),
            "audio-count" => Event(request, ProviderEventKind.Completed, 1, "Synthetic.") with { FinalSampleCount = 1 },
            "stage" => Event(request, ProviderEventKind.Failed, 1) with
            {
                Error = Event(request, ProviderEventKind.Failed, 1).Error! with { Stage = Stage.Playback }
            },
            "refused-stt" => Event(request, ProviderEventKind.Refused, 1),
            "no-speech-after-partial" => Event(request, ProviderEventKind.NoSpeech, 2),
            _ => Event(request, ProviderEventKind.NoSpeech, 1)
        };
        var result = stream.Accept(value).Snapshot;
        Assert.Equal(TurnOutcome.Failed, result.Result!.Outcome);
        Assert.Equal(issue, result.Issue);
        Assert.Equal(code, result.Result.Error!.Code);
        Assert.False(stream.TryReadText(out _));
    }

    [Theory]
    [InlineData("session", SequenceIssue.WrongCorrelation)]
    [InlineData("turn", SequenceIssue.WrongCorrelation)]
    [InlineData("request", SequenceIssue.WrongCorrelation)]
    [InlineData("provider", SequenceIssue.WrongCorrelation)]
    [InlineData("epoch", SequenceIssue.WrongEpoch)]
    [InlineData("provenance", SequenceIssue.WrongProvenance)]
    public void EveryCorrelationFieldIsEnforced(string field, SequenceIssue expected)
    {
        var request = Request();
        var value = Event(request, ProviderEventKind.Started, 0);
        value = field switch
        {
            "session" => value with { Ids = value.Ids with { SessionId = Id(10) } },
            "turn" => value with { Ids = value.Ids with { TurnId = Id(10) } },
            "request" => value with { Ids = value.Ids with { RequestId = Id(10) } },
            "provider" => value with { ProviderId = "wrong" },
            "epoch" => value with { Epoch = 1 },
            _ => value with { Provenance = EvidenceProvenance.Live }
        };
        using var stream = new ProviderSequenceValidator(request);
        Assert.Equal(expected, stream.AcceptJson(ContractJson.Write(value)).Snapshot.Issue);
    }

    [Theory]
    [InlineData(CapabilitySupport.Unknown, SequenceIssue.UnknownCapability)]
    [InlineData(CapabilitySupport.Unsupported, SequenceIssue.UnsupportedCapability)]
    public void UnknownAndUnsupportedDeltasAreDistinctButNeitherPasses(CapabilitySupport support, SequenceIssue expected)
    {
        var request = Request();
        request = request with { Capabilities = request.Capabilities with { LlmTextDeltas = support } };
        using var stream = new ProviderSequenceValidator(request);
        stream.Accept(Event(request, ProviderEventKind.Started, 0));
        var result = stream.Accept(Event(request, ProviderEventKind.TextDelta, 1, "Synthetic.")).Snapshot;
        Assert.Equal(expected, result.Issue);
        Assert.Equal(ErrorCode.ProviderCapability, result.Result!.Error!.Code);
        using var complete = new ProviderSequenceValidator(request);
        complete.Accept(Event(request, ProviderEventKind.Started, 0));
        Assert.Equal(TurnOutcome.Completed, complete.Accept(Event(request, ProviderEventKind.Completed, 1, "Synthetic.")).Snapshot.Result!.Outcome);
    }

    [Fact]
    public void BackpressureDoesNotConsumeSequenceOrTextAndRetryAfterDrainWorks()
    {
        var request = Request();
        using var stream = new ProviderSequenceValidator(request, new() { MaxQueuedChunks = 1, MaxTextCharacters = 2 });
        stream.Accept(Event(request, ProviderEventKind.Started, 0));
        stream.Accept(Event(request, ProviderEventKind.TextDelta, 1, "A"));
        var next = Event(request, ProviderEventKind.TextDelta, 2, "B");
        Assert.Equal(SequenceDecision.Backpressured, stream.Accept(next).Decision);
        Assert.Equal(2, stream.Snapshot.AcceptedEvents);
        Assert.Equal(1, stream.Snapshot.TextCharacters);
        Assert.True(stream.TryReadText(out _));
        Assert.Equal(SequenceDecision.Accepted, stream.Accept(next).Decision);
        Assert.Equal(2, stream.Snapshot.TextCharacters);
        Assert.Equal(1, stream.Snapshot.PeakQueuedChunks);
        stream.Accept(Event(request, ProviderEventKind.Completed, 3));
        Assert.True(stream.TryReadText(out var chunk));
        Assert.Equal("B", chunk!.Text);
    }

    [Theory]
    [InlineData("queue", SequenceIssue.QueueOverflow)]
    [InlineData("text", SequenceIssue.TextLimit)]
    [InlineData("duplicate-flood", SequenceIssue.EventLimit)]
    [InlineData("backpressure-flood", SequenceIssue.EventLimit)]
    public void ResourceLimitsCancelWithoutUnboundedRetention(string fault, SequenceIssue expected)
    {
        var request = Request();
        using var stream = new ProviderSequenceValidator(request, new()
        {
            MaxQueuedChunks = 1, MaxTextCharacters = fault == "text" ? 1 : 100, MaxIngressEvents = 4
        });
        stream.Accept(Event(request, ProviderEventKind.Started, 0));
        var first = Event(request, ProviderEventKind.TextDelta, 1, "A");
        stream.Accept(first);
        var second = Event(request, ProviderEventKind.TextDelta, 2, "B");
        var update = stream.Accept(fault == "duplicate-flood" ? first : second,
            fault == "queue" ? TextOverflowPolicy.Fail : TextOverflowPolicy.Backpressure);
        if (fault.EndsWith("flood", StringComparison.Ordinal))
        {
            stream.Accept(fault == "duplicate-flood" ? first : second);
            update = stream.Accept(fault == "duplicate-flood" ? first : second);
        }
        Assert.Equal(expected, update.Snapshot.Issue);
        Assert.Equal(ErrorCode.PayloadTooLarge, update.Snapshot.Result!.Error!.Code);
        Assert.Equal(0, update.Snapshot.QueuedChunks);
        Assert.InRange(update.Snapshot.AcceptedEvents, 1, 2);
        Assert.Equal(CancellationRequest.LocalDiscardOnly, update.Snapshot.CancellationRequested);
    }

    [Theory]
    [InlineData("first", SequenceIssue.FirstEventDeadline)]
    [InlineData("idle", SequenceIssue.IdleDeadline)]
    [InlineData("total", SequenceIssue.TotalDeadline)]
    public void DeadlinesUseExactMonotonicBoundariesAndDuplicatesDoNotResetIdle(string deadline, SequenceIssue expected)
    {
        var request = Request();
        var clock = new ManualClock();
        var limits = new SequenceLimits
        {
            FirstEventTimeout = TimeSpan.FromSeconds(2), IdleTimeout = TimeSpan.FromSeconds(2),
            TotalTimeout = TimeSpan.FromSeconds(deadline == "total" ? 2 : 10)
        };
        using var stream = new ProviderSequenceValidator(request, limits, clock);
        if (deadline != "first")
            stream.Accept(Event(request, ProviderEventKind.Started, 0));
        clock.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Null(stream.Poll().Snapshot.Result);
        if (deadline == "idle")
            stream.Accept(Event(request, ProviderEventKind.Started, 0));
        clock.Advance(TimeSpan.FromTicks(1));
        var result = stream.Poll().Snapshot;
        Assert.Equal(expected, result.Issue);
        Assert.Equal(ErrorCode.ProviderFailed, result.Result!.Error!.Code);
        Assert.Equal(TurnOutcome.Failed, result.Result.Outcome);
    }

    [Theory]
    [InlineData(CancellationCapability.Unknown, CancellationRequest.LocalDiscardOnly)]
    [InlineData(CancellationCapability.DiscardOnly, CancellationRequest.LocalDiscardOnly)]
    [InlineData(CancellationCapability.RequestAbort, CancellationRequest.AbortRequest)]
    [InlineData(CancellationCapability.CooperativeComputeCancel, CancellationRequest.CooperativeComputeCancel)]
    public void LocalStopFlushesAndOnlyRequestsAvailableUpstreamCancellation(CancellationCapability capability, CancellationRequest expected)
    {
        var request = Request(cancellation: capability);
        using var stream = new ProviderSequenceValidator(request);
        stream.Accept(Event(request, ProviderEventKind.Started, 0));
        stream.Accept(Event(request, ProviderEventKind.TextDelta, 1, "Synthetic."));
        var result = stream.Stop().Snapshot;
        Assert.Equal(TurnOutcome.Canceled, result.Result!.Outcome);
        Assert.Equal(expected, result.CancellationRequested);
        Assert.Equal(capability, result.CancellationCapability);
        Assert.Equal(1, result.CurrentEpoch);
        Assert.Equal(1, stream.Stop().Snapshot.CurrentEpoch);
        Assert.False(stream.TryReadText(out _));
        Assert.Equal(SequenceDecision.ClosedDiscarded, stream.Accept(Event(request, ProviderEventKind.Completed, 2)).Decision);
    }

    [Fact]
    public void CancellationWinsAtDeadlineAndStopsCompletedButUndeliveredText()
    {
        var clock = new ManualClock();
        using var cts = new CancellationTokenSource();
        using var stream = new ProviderSequenceValidator(Request(), timeProvider: clock, cancellationToken: cts.Token);
        clock.Advance(TimeSpan.FromSeconds(60));
        cts.Cancel();
        Assert.Equal(TurnOutcome.Canceled, stream.Poll().Snapshot.Result!.Outcome);
        using var finalCts = new CancellationTokenSource();
        using var done = new ProviderSequenceValidator(Request(), cancellationToken: finalCts.Token);
        done.Accept(Event(Request(), ProviderEventKind.Started, 0));
        done.Accept(Event(Request(), ProviderEventKind.Completed, 1, "Synthetic."));
        finalCts.Cancel();
        Assert.False(done.TryReadText(out _));
        Assert.Equal(TurnOutcome.Completed, done.Snapshot.Result!.Outcome);
        Assert.True(done.Snapshot.LocallyStopped);
    }

    [Fact]
    public void EofWithoutTerminalNeverSucceedsEvenAfterPartialDelivery()
    {
        using var stream = new ProviderSequenceValidator(Request());
        stream.Accept(Event(Request(), ProviderEventKind.Started, 0));
        stream.Accept(Event(Request(), ProviderEventKind.TextDelta, 1, "Partial."));
        stream.TryReadText(out _);
        var failed = stream.EndOfInput().Snapshot;
        Assert.Equal(SequenceIssue.MissingTerminal, failed.Issue);
        Assert.Equal(ErrorCode.StreamTruncated, failed.Result!.Error!.Code);
        Assert.Equal(1, failed.DeliveredChunks);
        Assert.Null(failed.ProviderTerminal);
    }

    [Fact]
    public void ReplacementFlushesOldTextAndOnlyKnownRetiredCorrelationsAreStale()
    {
        var old = Request();
        var next = Request(epoch: 1, turn: 4, attempt: 5);
        using var stream = new ProviderSequenceValidator(old);
        stream.Accept(Event(old, ProviderEventKind.Started, 0));
        stream.Accept(Event(old, ProviderEventKind.TextDelta, 1, "Old."));
        var previous = stream.Transition(next, AttemptTransition.Replace);
        Assert.Equal(TurnOutcome.Canceled, previous.Result!.Outcome);
        Assert.Equal(1, previous.CurrentEpoch);
        Assert.Equal(0, previous.QueuedChunks);
        Assert.Equal(SequenceDecision.StaleDiscarded, stream.Accept(Event(old, ProviderEventKind.TextDelta, 2, "Late.")).Decision);
        Assert.False(stream.TryReadText(out _));
        stream.Accept(Event(next, ProviderEventKind.Started, 0));
        stream.Accept(Event(next, ProviderEventKind.Completed, 1, "New."));
        Assert.True(stream.TryReadText(out var current));
        Assert.Equal(next.Ids, current!.Ids);
        Assert.Equal(next.Epoch, current.Epoch);
    }

    [Fact]
    public void RetryIsExplicitBoundedAndCannotReuseAttemptsOrRetryPartialOutput()
    {
        var request = Request();
        using var stream = new ProviderSequenceValidator(request, new() { MaxAttempts = 2 });
        stream.EndOfInput();
        var next = Request(epoch: 1, attempt: 4);
        Assert.Equal(TurnOutcome.Failed, stream.Transition(next, AttemptTransition.Retry).Result!.Outcome);
        stream.EndOfInput();
        Assert.Equal(ErrorCode.PayloadTooLarge, Assert.Throws<ContractException>(() =>
            stream.Transition(Request(epoch: 2, attempt: 5), AttemptTransition.Retry)).Code);
        using var partial = new ProviderSequenceValidator(request);
        partial.Accept(Event(request, ProviderEventKind.Started, 0));
        partial.Accept(Event(request, ProviderEventKind.TextDelta, 1, "Partial."));
        partial.EndOfInput();
        Assert.Throws<ContractException>(() => partial.Transition(next, AttemptTransition.Retry));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("same-request")]
    [InlineData("session")]
    [InlineData("replace-same-turn")]
    [InlineData("retry-new-turn")]
    [InlineData("retry-active")]
    public void InvalidTransitionsLeaveCurrentAttemptIntact(string fault)
    {
        using var stream = new ProviderSequenceValidator(Request());
        if (fault != "retry-active")
            stream.EndOfInput();
        var next = Request(epoch: 1, attempt: 4);
        var transition = AttemptTransition.Retry;
        next = fault switch
        {
            "epoch" => next with { Epoch = 2 },
            "same-request" => next with { Ids = next.Ids with { RequestId = Id(3) } },
            "session" => next with { Ids = next.Ids with { SessionId = Id(20) } },
            "retry-new-turn" => next with { Ids = next.Ids with { TurnId = Id(20) } },
            _ => next
        };
        if (fault == "replace-same-turn")
            transition = AttemptTransition.Replace;
        var before = stream.Snapshot;
        Assert.Throws<ContractException>(() => stream.Transition(next, transition));
        Assert.Equal(before, stream.Snapshot);
    }

    [Fact]
    public void StopConsumesOneEpochAndReplacementRequiresTheFollowingEpoch()
    {
        using var stream = new ProviderSequenceValidator(Request());
        stream.Stop();
        Assert.Throws<ContractException>(() => stream.Transition(Request(epoch: 1, turn: 4, attempt: 5), AttemptTransition.Replace));
        stream.Transition(Request(epoch: 2, turn: 4, attempt: 5), AttemptTransition.Replace);
        Assert.Equal(2, stream.Snapshot.CurrentEpoch);
        using var edge = new ProviderSequenceValidator(Request(epoch: int.MaxValue - 1));
        Assert.Equal(int.MaxValue, edge.Stop().Snapshot.CurrentEpoch);
        Assert.Throws<ContractException>(() => edge.Transition(Request(epoch: int.MaxValue, turn: 4, attempt: 5), AttemptTransition.Replace));
    }

    [Fact]
    public void DisposalIsIdempotentFlushesContentAndPreventsUse()
    {
        var stream = new ProviderSequenceValidator(Request());
        stream.Accept(Event(Request(), ProviderEventKind.Started, 0));
        stream.Accept(Event(Request(), ProviderEventKind.TextDelta, 1, "Synthetic."));
        stream.Dispose();
        stream.Dispose();
        Assert.Equal(0, stream.Snapshot.QueuedChunks);
        Assert.Equal(2, stream.Snapshot.AcceptedEvents);
        Assert.Null(stream.RefusalText);
        Assert.Throws<ObjectDisposedException>(() => stream.Accept(Event(Request(), ProviderEventKind.Completed, 2)));
    }
}
