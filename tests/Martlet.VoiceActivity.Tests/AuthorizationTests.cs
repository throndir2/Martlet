using static Martlet.VoiceActivity.Tests.AnalysisFixtures;

namespace Martlet.VoiceActivity.Tests;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task MissingLocalConsentIsNotConsumedAndDoesNotEnterFactory()
    {
        using var source = await Capture();
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var factory = new ControlledInferenceFactory();
        await using var analyzer = Analyzer(factory, clock, options);
        var permission = new LocalVoiceActivityAuthorization(source, Binding(source), options,
            clock.GetUtcNow().AddSeconds(30), timeProvider: clock);
        Assert.Equal(VoiceActivityFailureCode.AuthorizationRequired,
            Assert.Throws<VoiceActivityException>(() => analyzer.Start(source, permission)).Failure.Code);
        Assert.False(permission.IsConsumed);
        Assert.Equal(0, factory.Creates);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("session")]
    [InlineData("count")]
    [InlineData("epoch")]
    [InlineData("options")]
    [InlineData("clock")]
    public async Task ExactScopeIsCheckedBeforeConsumption(string changed)
    {
        using var source = await Capture();
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var binding = Binding(source);
        if (changed == "profile") binding = binding with { ProfileId = Guid.NewGuid() };
        if (changed == "session") binding = binding with { Ids = source.Ids with { SessionId = Guid.NewGuid() } };
        if (changed == "count") binding = binding with { SampleCount = source.SampleCount - 1 };
        if (changed == "epoch") binding = binding with { Epoch = 1 };
        var grantedOptions = changed == "options" ? options with { MinimumSpeechSamples = 1 } : options;
        var permission = new LocalVoiceActivityAuthorization(source, binding, grantedOptions, clock.GetUtcNow().AddSeconds(30),
            true, changed == "clock" ? new AnalysisClock() : clock);
        var factory = new ControlledInferenceFactory();
        await using var analyzer = Analyzer(factory, clock, options);
        Assert.Equal(VoiceActivityFailureCode.InvalidBinding,
            Assert.Throws<VoiceActivityException>(() => analyzer.Start(source, permission)).Failure.Code);
        Assert.False(permission.IsConsumed);
        Assert.Equal(0, factory.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrCancellationBeforeAdmissionLeavesPermissionUnused(bool callerCancellation)
    {
        using var source = await Capture();
        using var caller = new CancellationTokenSource();
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var factory = new ControlledInferenceFactory();
        var permission = Authorize(source, clock, options);
        if (callerCancellation) caller.Cancel(); else permission.Revoke();
        await using var analyzer = Analyzer(factory, clock, options);
        Assert.Equal(VoiceActivityFailureCode.Canceled,
            Assert.Throws<VoiceActivityException>(() => analyzer.Start(source, permission, caller.Token)).Failure.Code);
        Assert.False(permission.IsConsumed);
        Assert.Equal(0, factory.Creates);
    }

    [Fact]
    public async Task LowerInputCapRejectsWholeSourceWithoutCopyOrConsumption()
    {
        using var source = await Capture();
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions { MaximumInputSamples = 100 };
        var permission = Authorize(source, clock, options);
        var factory = new ControlledInferenceFactory();
        await using var analyzer = Analyzer(factory, clock, options);
        Assert.Equal(VoiceActivityFailureCode.PayloadTooLarge,
            Assert.Throws<VoiceActivityException>(() => analyzer.Start(source, permission)).Failure.Code);
        Assert.False(permission.IsConsumed);
        Assert.Equal(0, factory.Creates);
    }

    [Fact]
    public async Task PermissionExpiryDuringLoadCannotBeRenewedByLaterNativeReturn()
    {
        using var source = await Capture();
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var backend = new ControlledInference { OnLoad = () => clock.Advance(TimeSpan.FromSeconds(2), false) };
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, Authorize(source, clock, options, TimeSpan.FromSeconds(1)));
        Assert.Equal(VoiceActivityFailureCode.DeadlineExceeded, (await operation.Completion.WaitAsync(Wait)).Failure!.Code);
        Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Equal(0, backend.Scores);
    }

    [Fact]
    public async Task RevocationInsideScoreReturnPreventsAcceptingThatWindow()
    {
        using var source = await Capture();
        var clock = new AnalysisClock();
        var options = new VoiceActivityOptions();
        var permission = Authorize(source, clock, options);
        var backend = new ControlledInference { OnScore = permission.Revoke };
        await using var analyzer = Analyzer(new() { NewSession = () => backend }, clock, options);
        var operation = analyzer.Start(source, permission);
        Assert.Equal(VoiceActivityOutcome.Canceled, (await operation.Completion.WaitAsync(Wait)).Outcome);
        Assert.True((await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Equal(1, backend.Scores);
        Assert.Throws<VoiceActivityException>(() => operation.TakeResult());
    }
}
