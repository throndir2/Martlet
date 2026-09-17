using System.Runtime.InteropServices;
using Martlet.Audio;
using static Martlet.VoiceActivity.Tests.AnalysisFixtures;

namespace Martlet.VoiceActivity.Tests;

public sealed class CapturedUtteranceAnalysisTests
{
    [Fact]
    public async Task PublicEligibilityIsInertAndWinsBeforeAnyDataAdmission()
    {
        using var source = await Capture();
        var original = Copy(source);
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var authorization = Authorize(source, clock, options);
        await using var analyzer = new CpuVoiceActivityAnalyzer(Session, Profile, Revision, "model-must-not-be-opened", options, clock);
        Assert.False(analyzer.Availability.Eligible);
        Assert.Equal(VoiceActivityFailureCode.NativePrivacyUnqualified, analyzer.Availability.Failure.Code);
        var failure = Assert.Throws<VoiceActivityException>(() => analyzer.Start(source, authorization));
        Assert.Equal(VoiceActivityFailureCode.NativePrivacyUnqualified, failure.Failure.Code);
        Assert.False(authorization.IsConsumed);
        Assert.Equal(original, Copy(source));
        source.Dispose();
        Assert.Equal(VoiceActivityFailureCode.NativePrivacyUnqualified,
            Assert.Throws<VoiceActivityException>(() => analyzer.Start(source, authorization)).Failure.Code);
        Assert.False(authorization.IsConsumed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCaptureNormalizationTransferFeedsManagedAnalysisWithoutChangingPcm(bool resample)
    {
        using var source = await Capture(resample: resample);
        var original = Copy(source);
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions { MinimumSpeechSamples = 512 };
        var backend = new ControlledInference();
        var factory = new ControlledInferenceFactory { NewSession = () => backend };
        await using var analyzer = Analyzer(factory, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options));
        var terminal = await operation.Completion.WaitAsync(Wait);
        Assert.Equal(VoiceActivityOutcome.ActivityDetected, terminal.Outcome);
        Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        var result = operation.TakeResult();
        Assert.Equal(Binding(source), result.Binding);
        Assert.Equal(VoiceActivityEvidence.InternalFakeInference, result.Evidence);
        Assert.Equal(new[] { 0, 512, 1024 }, result.Scores.Select(s => s.SampleOffset));
        Assert.Equal(new[] { 512, 512, 176 }, result.Scores.Select(s => s.ValidSamples));
        Assert.Equal(1200, result.Segments.Single().SpeechEndSampleExclusive);
        Assert.Equal(original, Copy(source));
        Assert.Equal(1, backend.Loads);
        Assert.Equal(1, backend.Disposals);
        Assert.Single(backend.OwnerThreads.Distinct());
        Assert.Equal(0, operation.Snapshot.RetainedPcmBytes);
        Assert.Throws<VoiceActivityException>(() => operation.TakeResult());
    }

    [Fact]
    public async Task CallerDisposalAndExternalFrameMutationAfterCopyDoNotChangeAnalysis()
    {
        using var source = await Capture();
        var original = Copy(source);
        using var blocked = new ManualResetEventSlim();
        var backend = new ControlledInference { LoadBlock = blocked };
        var factory = new ControlledInferenceFactory { NewSession = () => backend };
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        await using var analyzer = Analyzer(factory, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options));
        try
        {
            await Until(() => backend.LoadEntered.IsSet);
            var external = source.GetFrame(0);
            Assert.True(MemoryMarshal.TryGetArray(external.Data, out var array));
            Array.Fill(array.Array!, (byte)0);
            Assert.Equal(original, Copy(source));
            source.Dispose();
            blocked.Set();
            await operation.Completion.WaitAsync(Wait);
            Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
            Assert.Equal(1234 / 32768f, backend.Windows.First()[0]);
        }
        finally { blocked.Set(); }
    }

    [Fact]
    public async Task DisposedSourceIsAnExplicitErrorBeforeFactoryEntry()
    {
        using var source = await Capture();
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var factory = new ControlledInferenceFactory();
        var authorization = Authorize(source, clock, options);
        source.Dispose();
        await using var analyzer = Analyzer(factory, clock, options);
        var operation = analyzer.Start(source, authorization);
        var result = await operation.Completion.WaitAsync(Wait);
        Assert.Equal(VoiceActivityFailureCode.InputUnavailable, result.Failure!.Code);
        Assert.Equal(0, factory.Creates);
        Assert.True(authorization.IsConsumed);
        Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
    }

    [Fact]
    public async Task WholeInputAndPermissionBindingsRejectMismatchWithoutConsumption()
    {
        using var first = await Capture();
        using var second = await Capture(epoch: 1);
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var factory = new ControlledInferenceFactory();
        await using var analyzer = Analyzer(factory, clock, options);
        var authorization = Authorize(first, clock, options);
        Assert.Equal(VoiceActivityFailureCode.InvalidBinding,
            Assert.Throws<VoiceActivityException>(() => analyzer.Start(second, authorization)).Failure.Code);
        Assert.False(authorization.IsConsumed);
        var wrong = new LocalVoiceActivityAuthorization(first, Binding(first) with { ConfigurationRevision = Guid.NewGuid() },
            options, clock.GetUtcNow().AddSeconds(30), true, clock);
        Assert.Equal(VoiceActivityFailureCode.InvalidBinding,
            Assert.Throws<VoiceActivityException>(() => analyzer.Start(first, wrong)).Failure.Code);
        Assert.False(wrong.IsConsumed);
        Assert.Equal(0, factory.Creates);
    }

    [Fact]
    public async Task NoActivityIsDistinctFromErrorsAndNeverDiscardsPttIntent()
    {
        using var source = await Capture();
        var original = Copy(source);
        var backend = new ControlledInference { Result = 0 };
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options));
        Assert.Equal(VoiceActivityOutcome.NoActivityDetected, (await operation.Completion.WaitAsync(Wait)).Outcome);
        Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Empty(operation.TakeResult().Segments);
        Assert.Equal(original, Copy(source));
    }

    [Fact]
    public async Task MaximumCanonicalInputProducesExactly938BoundedWindows()
    {
        using var source = await Capture(VoiceActivityOptions.HardMaximumSamples);
        var backend = new ControlledInference { Result = 0 };
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options));
        await operation.Completion.WaitAsync(Wait);
        Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        var result = operation.TakeResult();
        Assert.Equal(938, result.Scores.Count);
        Assert.Equal(256, result.Scores[^1].ValidSamples);
        Assert.Equal(480000, result.Scores.Sum(s => s.ValidSamples));
    }
}
