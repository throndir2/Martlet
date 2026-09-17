using System.Reflection;
using static Martlet.VoiceActivity.Tests.AnalysisFixtures;

namespace Martlet.VoiceActivity.Tests;

public sealed class VoiceActivityLifetimeTests
{
    [Theory]
    [InlineData("load")]
    [InlineData("score")]
    [InlineData("dispose")]
    public async Task StopObservationNeverReleasesBlockedOwnedWork(string boundary)
    {
        using var source = await Capture();
        using var next = await Capture(epoch: 1);
        using var blocked = new ManualResetEventSlim();
        var backend = new ControlledInference();
        if (boundary == "load") backend.LoadBlock = blocked;
        if (boundary == "score") backend.ScoreBlock = blocked;
        if (boundary == "dispose") backend.DisposeBlock = blocked;
        var factory = new ControlledInferenceFactory { NewSession = () => backend };
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        await using var analyzer = Analyzer(factory, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options));
        try
        {
            await Until(() => boundary == "load" ? backend.LoadEntered.IsSet : boundary == "score" ? backend.ScoreEntered.IsSet : backend.DisposeEntered.IsSet);
            var pcm = (byte[])typeof(VoiceActivityOperation).GetField("pcm", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(operation)!;
            Assert.Contains(pcm, b => b != 0);
            Assert.Equal(VoiceActivityOutcome.Canceled, (await operation.Cancel().WaitAsync(Wait)).Outcome);
            Assert.False(operation.OwnershipRelease.IsCompleted);
            Assert.Equal(VoiceActivityOwnershipState.Pending, operation.Snapshot.Ownership);
            Assert.Contains(pcm, b => b != 0);
            var fresh = Authorize(next, clock, options);
            Assert.Equal(VoiceActivityFailureCode.Busy, Assert.Throws<VoiceActivityException>(() => analyzer.Start(next, fresh)).Failure.Code);
            Assert.False(fresh.IsConsumed);
            Assert.Throws<VoiceActivityException>(() => operation.TakeResult());
            blocked.Set();
            Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
            Assert.All(pcm, b => Assert.Equal(0, b));
        }
        finally { blocked.Set(); }
    }

    [Fact]
    public async Task OwnedCancellationCallbackMustRetireBeforeDisposeAndSlotReuse()
    {
        using var source = await Capture();
        using var native = new ManualResetEventSlim();
        using var cancel = new ManualResetEventSlim();
        var backend = new ControlledInference { ScoreBlock = native, CancelBlock = cancel };
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options));
        try
        {
            await Until(() => backend.ScoreEntered.IsSet);
            await operation.Cancel().WaitAsync(Wait);
            await Until(() => backend.CancelEntered.IsSet);
            native.Set();
            Assert.False(operation.OwnershipRelease.IsCompleted);
            Assert.False(backend.DisposeEntered.IsSet);
            cancel.Set();
            Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
            Assert.Equal(1, backend.Cancels);
            Assert.Equal(1, backend.Disposals);
        }
        finally { native.Set(); cancel.Set(); }
    }

    [Fact]
    public async Task OriginalCallerFlagWinsEvenWhenItsNewerCallbackBlocks()
    {
        using var source = await Capture();
        using var native = new ManualResetEventSlim();
        using var callback = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        var backend = new ControlledInference { ScoreBlock = native };
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options), caller.Token);
        using var registration = caller.Token.Register(() => { entered.Set(); callback.Wait(); });
        await Until(() => backend.ScoreEntered.IsSet);
        var cancellation = caller.CancelAsync();
        try
        {
            await Until(() => entered.IsSet);
            Assert.Equal(VoiceActivityFailureCode.Canceled, operation.Snapshot.Failure!.Code);
            native.Set();
            Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
            Assert.False(cancellation.IsCompleted);
            Assert.Throws<VoiceActivityException>(() => operation.TakeResult());
        }
        finally { callback.Set(); native.Set(); await cancellation.WaitAsync(Wait); }
    }

    [Theory]
    [InlineData("utc")]
    [InlineData("permission")]
    [InlineData("processing")]
    public async Task OriginalClocksRejectLateNativeResultsWithoutTimerDelivery(string cause)
    {
        using var source = await Capture();
        using var native = new ManualResetEventSlim();
        var backend = new ControlledInference { LoadBlock = native };
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var permission = cause == "permission" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options, permission));
        try
        {
            await Until(() => backend.LoadEntered.IsSet);
            if (cause == "utc") clock.ShiftUtc(TimeSpan.FromSeconds(31));
            else
            {
                clock.ShiftUtc(TimeSpan.FromSeconds(-60));
                clock.Advance(TimeSpan.FromSeconds(cause == "permission" ? 2 : 11), deliver: false);
            }
            native.Set();
            var terminal = await operation.Completion.WaitAsync(Wait);
            Assert.Equal(VoiceActivityFailureCode.DeadlineExceeded, terminal.Failure!.Code);
            Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
            Assert.Equal(0, backend.Scores);
            Assert.Throws<VoiceActivityException>(() => operation.TakeResult());
        }
        finally { native.Set(); }
    }

    [Theory]
    [InlineData("revoke", false)]
    [InlineData("caller", false)]
    [InlineData("cancel", false)]
    [InlineData("utc", false)]
    [InlineData("rollback", false)]
    [InlineData("revoke", true)]
    [InlineData("caller", true)]
    [InlineData("cancel", true)]
    [InlineData("utc", true)]
    [InlineData("rollback", true)]
    public async Task CompletedMetadataDoesNotRenewPermissionAtResultTransfer(string cause, bool activity)
    {
        using var source = await Capture();
        using var caller = new CancellationTokenSource();
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions { MinimumSpeechSamples = activity ? 512 : 4096 };
        var authorization = Authorize(source, clock, options);
        await using var analyzer = Analyzer(new(), clock, options);
        var operation = analyzer.Start(source, authorization, caller.Token);
        var historical = await operation.Completion.WaitAsync(Wait);
        Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Equal(activity ? VoiceActivityOutcome.ActivityDetected : VoiceActivityOutcome.NoActivityDetected, historical.Outcome);
        if (cause == "revoke") authorization.Revoke();
        if (cause == "caller") caller.Cancel();
        if (cause == "cancel") await operation.Cancel().WaitAsync(Wait);
        if (cause == "utc") clock.ShiftUtc(TimeSpan.FromSeconds(31));
        if (cause == "rollback") { clock.ShiftUtc(TimeSpan.FromSeconds(-60)); clock.Advance(TimeSpan.FromSeconds(31), false); }
        Assert.Throws<VoiceActivityException>(() => operation.TakeResult());
        Assert.Same(historical, await operation.Completion);
        var current = operation.Snapshot;
        Assert.Equal(VoiceActivityOwnershipState.Released, current.Ownership);
        Assert.Equal(cause is "utc" or "rollback" ? VoiceActivityOutcome.Failed : VoiceActivityOutcome.Canceled, current.Outcome);
        Assert.Equal(cause is "utc" or "rollback" ? VoiceActivityFailureCode.DeadlineExceeded : VoiceActivityFailureCode.Canceled,
            current.Failure!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupOrOwnedCancellationFailureQuarantinesWithoutDiscardingCallerAudio(bool cancellationFailure)
    {
        using var source = await Capture();
        using var next = await Capture(epoch: 1);
        var original = Copy(source);
        using var native = new ManualResetEventSlim(!cancellationFailure);
        var backend = new ControlledInference { ScoreBlock = native };
        if (cancellationFailure) backend.CancelFailure = new InvalidOperationException(PrivateCanary);
        else backend.DisposeFailure = new InvalidOperationException(PrivateCanary);
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options));
        try
        {
            if (cancellationFailure)
            {
                await Until(() => backend.ScoreEntered.IsSet);
                await operation.Cancel().WaitAsync(Wait);
                await Until(() => backend.CancelEntered.IsSet);
                native.Set();
            }
            var release = await operation.OwnershipRelease.WaitAsync(Wait);
            Assert.False(release.Released);
            Assert.Equal(cancellationFailure ? VoiceActivityFailureCode.CancellationFailed : VoiceActivityFailureCode.CleanupFailed,
                release.Failure!.Code);
            Assert.DoesNotContain(PrivateCanary, release.ToString());
            Assert.Equal(VoiceActivityOwnershipState.Quarantined, operation.Snapshot.Ownership);
            Assert.Equal(VoiceActivityOutcome.Failed, operation.Snapshot.Outcome);
            Assert.True(operation.Snapshot.RetainedPcmBytes > 0);
            Assert.Equal(original, Copy(source));
            Assert.Throws<VoiceActivityException>(() => operation.TakeResult());
            Assert.Equal(VoiceActivityFailureCode.Busy,
                Assert.Throws<VoiceActivityException>(() => analyzer.Start(next, Authorize(next, clock, options))).Failure.Code);
        }
        finally { native.Set(); }
    }

    [Fact]
    public async Task FreshEpochIsRequiredAndOldControlsCannotCancelTheNextRun()
    {
        using var first = await Capture();
        using var second = await Capture(epoch: 1);
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var factory = new ControlledInferenceFactory();
        await using var analyzer = Analyzer(factory, clock, options);
        var firstAuthorization = Authorize(first, clock, options);
        var previous = analyzer.Start(first, firstAuthorization);
        await previous.OwnershipRelease.WaitAsync(Wait);
        Assert.Throws<VoiceActivityException>(() => analyzer.Start(first, firstAuthorization));
        var next = analyzer.Start(second, Authorize(second, clock, options));
        Assert.Throws<VoiceActivityException>(() => previous.TakeResult());
        await previous.Cancel().WaitAsync(Wait);
        await next.Completion.WaitAsync(Wait);
        Assert.True((await next.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Equal(second.Ids, next.TakeResult().Binding.Ids);
        Assert.Equal(2, factory.Creates);
    }

    [Theory]
    [InlineData("load")]
    [InlineData("score")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("range")]
    public async Task InferenceFaultsAreExplicitSanitizedFailuresNeverSilence(string fault)
    {
        using var source = await Capture();
        var original = Copy(source);
        var backend = new ControlledInference();
        if (fault == "load") backend.LoadFailure = new IOException(PrivateCanary);
        if (fault == "score") backend.ScoreFailure = new IOException(PrivateCanary);
        if (fault == "nan") backend.Result = float.NaN;
        if (fault == "infinity") backend.Result = float.PositiveInfinity;
        if (fault == "range") backend.Result = 1.01f;
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options));
        var terminal = await operation.Completion.WaitAsync(Wait);
        Assert.Equal(VoiceActivityOutcome.Failed, terminal.Outcome);
        Assert.NotNull(terminal.Failure);
        Assert.DoesNotContain(PrivateCanary, terminal.ToString());
        Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Equal(original, Copy(source));
        Assert.Throws<VoiceActivityException>(() => operation.TakeResult());
    }
}
