using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Providers.Ollama;

namespace Martlet.Providers.Tests.Ollama;

public sealed class OllamaChatAuthorizationTests
{
    [Fact]
    public async Task Construction_description_and_started_are_passive()
    {
        var handler = OllamaFixtures.Handler();
        var authority = new OllamaAuthority();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, authority, new FixtureClock());
        var description = OllamaChatAdapter.Describe(OllamaFixtures.Model);
        description.Validate();
        Assert.Equal(EvidenceProvenance.NotRun, description.Provenance);
        Assert.Equal(CancellationCapability.Unknown, description.Cancellation);
        await using var stream = OllamaFixtures.Stream(adapter);
        await using var iterator = stream.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.Started, iterator.Current.Kind);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, handler.Calls);
        Assert.False(stream.OwnershipRelease.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => stream.GetAsyncEnumerator());
    }

    [Theory]
    [InlineData("null", ProviderFailureCode.ConsentMissing)]
    [InlineData("unavailable", ProviderFailureCode.ConsentMissing)]
    [InlineData("expired", ProviderFailureCode.ConsentExpired)]
    [InlineData("overlong", ProviderFailureCode.ConsentMismatch)]
    public async Task Denied_expired_and_overlong_permissions_never_dispatch(string mode, ProviderFailureCode code)
    {
        var source = new OllamaAuthority();
        OllamaChatAuthorization? offered = null;
        source.Authorize = (a, _) => mode switch
        {
            "null" => ValueTask.FromResult<OllamaChatAuthorization?>(null),
            "unavailable" => throw new OllamaChatAuthorizationUnavailableException(),
            _ => ValueTask.FromResult<OllamaChatAuthorization?>(offered = new(a,
                mode == "expired" ? ProviderFixtures.Now : a.EffectiveDeadline.AddSeconds(1), source.Lease))
        };
        var handler = OllamaFixtures.Handler();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, new FixtureClock());
        var result = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        Assert.Equal(code, result.Result.FailureCode);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(offered is null ? 0 : 1, source.Lease.Calls);
        if (offered is not null) Assert.False(offered.TryConsume(out _));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("model")]
    [InlineData("alias")]
    [InlineData("input")]
    [InlineData("options")]
    [InlineData("context")]
    [InlineData("epoch")]
    [InlineData("deadline")]
    [InlineData("MaxInputBytes")]
    [InlineData("MaxInputTokens")]
    [InlineData("MaxOutputTokens")]
    [InlineData("MaxContextTokens")]
    [InlineData("MaxEventBytes")]
    [InlineData("MaxStreamBytes")]
    [InlineData("MaxEvents")]
    [InlineData("MaxTextCharacters")]
    [InlineData("FirstDeltaTimeout")]
    [InlineData("IdleTimeout")]
    [InlineData("MaxRequestTime")]
    [InlineData("identical values different action")]
    public async Task Different_exact_action_burns_mismatched_permit(string change)
    {
        var clock = new FixtureClock();
        var limits = new TextGenerationLimits();
        var context = ProviderFixtures.Context();
        var input = new BoundedTextInput("Fixture input.");
        var origin = change == "origin" ? new OllamaLoopbackOrigin("http://127.0.0.1:12346") : OllamaFixtures.Origin;
        var model = change == "model" ? new OllamaChatModelSelection("fixture", "different:v1") :
            change == "alias" ? new("different", "fixture-model:v1") : OllamaFixtures.Model;
        var options = change == "options" ? new OllamaChatOptions(1) : OllamaFixtures.Options;
        if (change == "input") input = new("Different.");
        if (change == "context") context = context with { Ids = context.Ids with { RequestId = Guid.NewGuid() } };
        if (change == "epoch") context = context with { Epoch = 8 };
        if (change == "deadline") context = context with { Deadline = context.Deadline.AddSeconds(-1) };
        limits = change switch
        {
            "MaxInputBytes" => limits with { MaxInputBytes = 1024 },
            "MaxInputTokens" => limits with { MaxInputTokens = 1024 },
            "MaxOutputTokens" => limits with { MaxOutputTokens = 128 },
            "MaxContextTokens" => limits with { MaxContextTokens = 30_000 },
            "MaxEventBytes" => limits with { MaxEventBytes = 1024 },
            "MaxStreamBytes" => limits with { MaxStreamBytes = 1_048_576 },
            "MaxEvents" => limits with { MaxEvents = 20 },
            "MaxTextCharacters" => limits with { MaxTextCharacters = 1024 },
            "FirstDeltaTimeout" => limits with { FirstDeltaTimeout = TimeSpan.FromSeconds(9) },
            "IdleTimeout" => limits with { IdleTimeout = TimeSpan.FromSeconds(9) },
            "MaxRequestTime" => limits with { MaxRequestTime = TimeSpan.FromSeconds(20) },
            _ => limits
        };
        var different = new OllamaChatAction(context, origin, model, input, options, limits, clock);
        var lease = new CountingLease();
        var permit = new OllamaChatAuthorization(different, ProviderFixtures.Now.AddSeconds(10), lease);
        var source = new OllamaAuthority { Authorize = (_, _) => ValueTask.FromResult<OllamaChatAuthorization?>(permit) };
        var handler = OllamaFixtures.Handler();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, clock);
        var first = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        var second = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        Assert.Equal(ProviderFailureCode.ConsentMismatch, first.Result.FailureCode);
        Assert.Equal(ProviderFailureCode.ConsentConsumed, second.Result.FailureCode);
        Assert.Equal(1, lease.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Reused_permission_does_not_release_the_first_consumers_active_lease()
    {
        OllamaChatAuthorization? permit = null;
        var source = new OllamaAuthority();
        source.Authorize = (a, _) => ValueTask.FromResult<OllamaChatAuthorization?>(permit ??= new(a, a.EffectiveDeadline, source.Lease));
        var handler = OllamaFixtures.Handler();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, new FixtureClock());
        await using var first = OllamaFixtures.Stream(adapter);
        await using var iterator = first.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.TextDelta, iterator.Current.Kind);
        var rejected = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        Assert.Equal(ProviderFailureCode.ConsentConsumed, rejected.Result.FailureCode);
        Assert.Equal(0, source.Lease.Calls);
        Assert.False(first.OwnershipRelease.IsCompleted);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(1, source.Lease.Calls);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Personality_and_history_are_rejected_not_ignored(bool personality)
    {
        var handler = OllamaFixtures.Handler();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), new FixtureClock());
        var input = personality ? new BoundedTextInput("User", "Style") :
            new("User", history: [new(TextHistoryRole.Assistant, "History")]);
        Assert.Throws<ContractException>(() => OllamaFixtures.Stream(adapter, input: input));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Input_limit_precedes_authorization_and_metadata_contains_no_content()
    {
        var source = new OllamaAuthority();
        var handler = OllamaFixtures.Handler();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, new FixtureClock());
        var denied = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter,
            new() { MaxInputBytes = 1 }, input: new(ProviderFixtures.ContentCanary)));
        Assert.Equal(ProviderFailureCode.InputLimit, denied.Result.FailureCode);
        Assert.Equal(0, source.Calls);
        var accepted = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter, input: new(ProviderFixtures.ContentCanary)));
        var metadata = JsonSerializer.Serialize(source.LastAction) + source.LastAction +
            JsonSerializer.Serialize(accepted.Result) + accepted.Result + JsonSerializer.Serialize(denied.Result);
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, metadata);
        Assert.DoesNotContain("Hello fixture.", metadata);
        Assert.Throws<ArgumentNullException>(() => OllamaChatAdapter.Create(null!));
    }
}
