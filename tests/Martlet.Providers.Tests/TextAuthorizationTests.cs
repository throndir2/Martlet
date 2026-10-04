using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class TextAuthorizationTests
{
    [Fact]
    public async Task Request_is_exact_stateless_text_subset_and_origin_role_bound()
    {
        var handler = new TextRecordingHandler
        {
            Inspect = request =>
            {
                Assert.Equal(new Uri("https://api.openai.com/v1/responses"), request.RequestUri);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(HttpVersion.Version11, request.Version);
                Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
                Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
                Assert.Equal("Bearer " + ProviderFixtures.Secret, request.Headers.Authorization!.ToString());
            }
        };
        BoundProviderCredential? credential = null;
        var source = new FixtureCredentials
        {
            Resolve = (binding, _) =>
            {
                Assert.Equal(TextFixtures.Binding, binding);
                return ValueTask.FromResult<BoundProviderCredential?>(credential = new(binding, ProviderFixtures.Secret));
            }
        };
        var history = new List<TextHistoryMessage> { new(TextHistoryRole.User, "Earlier fixture"), new(TextHistoryRole.Assistant, "Earlier answer") };
        var input = new BoundedTextInput(ProviderFixtures.ContentCanary, "Bounded personality", history);
        history.Clear();
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, source, new FixtureClock());
        var stream = adapter.Stream(context, TextFixtures.Selection, input, limits, TextFixtures.Authorize(context, limits));
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
        var result = await TextFixtures.Collect(stream);
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        using var json = JsonDocument.Parse(handler.Body);
        var root = json.RootElement;
        Assert.Equal(new[] { "background", "input", "instructions", "max_output_tokens", "model", "parallel_tool_calls", "store", "stream", "text", "tool_choice", "tools", "truncation" },
            root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(TextFixtures.Model, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.False(root.GetProperty("background").GetBoolean());
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("none", root.GetProperty("tool_choice").GetString());
        Assert.Empty(root.GetProperty("tools").EnumerateArray());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("disabled", root.GetProperty("truncation").GetString());
        Assert.Equal("text", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(limits.MaxOutputTokens, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("Bounded personality", root.GetProperty("instructions").GetString());
        Assert.Equal(3, root.GetProperty("input").GetArrayLength());
        Assert.Equal(ProviderFixtures.ContentCanary, root.GetProperty("input")[2].GetProperty("content").GetString());
        Assert.DoesNotContain(ProviderFixtures.Secret, Encoding.UTF8.GetString(handler.Body));
        Assert.DoesNotContain("conversation", root.GetProperty("model").GetString()!);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, handler.Calls);
        Assert.Throws<CredentialUnavailableException>(() => credential!.CreateAuthorization());
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, input + JsonSerializer.Serialize(input) + JsonSerializer.Serialize(result.Result));
    }

    [Fact]
    public async Task Responses_notes_are_appended_to_current_user_message()
    {
        var handler = new TextRecordingHandler();
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var input = new BoundedTextInput("Current message", "Stable instructions", notes: "Per-turn notes");

        var result = await TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, input, limits,
            TextFixtures.Authorize(context, limits)));

        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        using var json = JsonDocument.Parse(handler.Body);
        Assert.Equal("Stable instructions", json.RootElement.GetProperty("instructions").GetString());
        var items = json.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal("user", item.GetProperty("role").GetString());
        Assert.Equal("Current message\n\nPer-turn notes", item.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("missing", ProviderFailureCode.ConsentMissing)]
    [InlineData("no-text", ProviderFailureCode.ConsentMissing)]
    [InlineData("no-charge", ProviderFailureCode.ConsentMissing)]
    [InlineData("stt", ProviderFailureCode.ConsentMismatch)]
    [InlineData("model", ProviderFailureCode.ConsentMismatch)]
    [InlineData("alias", ProviderFailureCode.ConsentMismatch)]
    [InlineData("origin", ProviderFailureCode.OriginRejected)]
    [InlineData("port", ProviderFailureCode.OriginRejected)]
    [InlineData("path", ProviderFailureCode.OriginRejected)]
    [InlineData("userinfo", ProviderFailureCode.OriginRejected)]
    [InlineData("session", ProviderFailureCode.ConsentMismatch)]
    [InlineData("turn", ProviderFailureCode.ConsentMismatch)]
    [InlineData("request", ProviderFailureCode.ConsentMismatch)]
    [InlineData("epoch", ProviderFailureCode.ConsentMismatch)]
    [InlineData("limits", ProviderFailureCode.ConsentMismatch)]
    [InlineData("expired", ProviderFailureCode.ConsentExpired)]
    [InlineData("input-bytes", ProviderFailureCode.InputLimit)]
    [InlineData("input-tokens", ProviderFailureCode.InputLimit)]
    [InlineData("unsupported-model", ProviderFailureCode.ModelUnsupported)]
    public async Task Unauthorized_requests_never_resolve_or_send(string scenario, ProviderFailureCode expected)
    {
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        if (scenario == "input-bytes") limits = limits with { MaxInputBytes = 1 };
        if (scenario == "input-tokens") limits = limits with { MaxInputTokens = 1 };
        var binding = scenario switch
        {
            "stt" => TextFixtures.Binding with { Role = ProviderRole.Stt },
            "model" => TextFixtures.Binding with { UpstreamModelId = "gpt-4.1" },
            "origin" => TextFixtures.Binding with { Origin = new("https://example.com") },
            "port" => TextFixtures.Binding with { Origin = new("https://api.openai.com:444") },
            "path" => TextFixtures.Binding with { Origin = new("https://api.openai.com/path") },
            "userinfo" => TextFixtures.Binding with { Origin = new("https://fixture@api.openai.com") },
            _ => TextFixtures.Binding
        };
        var ids = scenario switch
        {
            "session" => context.Ids with { SessionId = Guid.NewGuid() },
            "turn" => context.Ids with { TurnId = Guid.NewGuid() },
            "request" => context.Ids with { RequestId = Guid.NewGuid() },
            _ => context.Ids
        };
        var authorization = scenario == "missing" ? null : TextFixtures.Authorize(context,
            scenario == "limits" ? limits with { MaxOutputTokens = 32 } : limits,
            scenario == "expired" ? ProviderFixtures.Now : null, binding,
            scenario == "alias" ? TextFixtures.Selection with { ModelAlias = "another" } : null, ids,
            scenario == "epoch" ? context.Epoch + 1 : null, scenario != "no-text", scenario != "no-charge");
        var source = new FixtureCredentials();
        var handler = new TextRecordingHandler();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, source, new FixtureClock());
        var result = await TextFixtures.Collect(adapter.Stream(context,
            scenario == "unsupported-model" ? TextFixtures.Selection with { UpstreamModelId = "unreviewed" } : TextFixtures.Selection,
            new("Fixture text"), limits, authorization));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Authorization_consumption_is_atomic_and_stream_enumeration_is_single_use()
    {
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var authorization = TextFixtures.Authorize(context, limits);
        var handler = new TextRecordingHandler();
        var source = new FixtureCredentials
        {
            Resolve = async (binding, _) =>
            {
                await Task.Yield();
                return new(binding, ProviderFixtures.Secret);
            }
        };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, source, new FixtureClock());
        var first = adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, authorization);
        var second = adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, authorization);
        var results = await Task.WhenAll(TextFixtures.Collect(first), TextFixtures.Collect(second));
        Assert.Single(results, x => x.Result.Outcome == TextGenerationOutcome.Completed);
        Assert.Single(results, x => x.Result.Failure?.Code == ProviderFailureCode.ConsentConsumed);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, source.Calls);
        Assert.Throws<InvalidOperationException>(() => first.GetAsyncEnumerator());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unavailable")]
    [InlineData("binding")]
    public async Task Credential_boundary_fails_without_sending(string scenario)
    {
        var source = new FixtureCredentials
        {
            Resolve = (binding, _) => scenario switch
            {
                "null" => ValueTask.FromResult<BoundProviderCredential?>(null),
                "unavailable" => throw new CredentialUnavailableException(),
                _ => ValueTask.FromResult<BoundProviderCredential?>(new(binding with { Role = ProviderRole.Stt }, ProviderFixtures.Secret))
            }
        };
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var handler = new TextRecordingHandler();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, source, new FixtureClock());
        var result = await TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, TextFixtures.Authorize(context, limits)));
        Assert.Equal(scenario == "binding" ? ProviderFailureCode.CredentialBindingMismatch : ProviderFailureCode.CredentialUnavailable,
            result.Result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("expiry", ProviderFailureCode.ConsentExpired)]
    [InlineData("rollback", ProviderFailureCode.ConsentExpired)]
    [InlineData("utc-forward", ProviderFailureCode.ConsentExpired)]
    [InlineData("deadline", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("maximum", ProviderFailureCode.DeadlineExceeded)]
    public async Task Synchronous_cutoff_after_slow_credentials_defeats_delayed_timers(string scenario, ProviderFailureCode expected)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        BoundProviderCredential? credential = null;
        var source = new FixtureCredentials
        {
            Resolve = async (binding, _) =>
            {
                await Task.Yield();
                clock.Advance(TimeSpan.FromSeconds(scenario == "utc-forward" ? 1 : 5));
                if (scenario == "rollback") clock.ShiftUtc(TimeSpan.FromHours(-1));
                if (scenario == "utc-forward") clock.ShiftUtc(TimeSpan.FromHours(1));
                return credential = new(binding, ProviderFixtures.Secret);
            }
        };
        var context = ProviderFixtures.Context();
        if (scenario == "deadline") context = context with { Deadline = ProviderFixtures.Now.AddSeconds(5) };
        var limits = new TextGenerationLimits { MaxRequestTime = TimeSpan.FromSeconds(scenario == "maximum" ? 5 : 60) };
        var handler = new TextRecordingHandler();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, source, clock);
        var result = await TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits,
            TextFixtures.Authorize(context, limits, expiresAt: scenario is "deadline" or "maximum" ? null : ProviderFixtures.Now.AddSeconds(5))));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Throws<CredentialUnavailableException>(() => credential!.CreateAuthorization());
    }

    [Theory]
    [InlineData("consent", ProviderFailureCode.ConsentExpired)]
    [InlineData("request", ProviderFailureCode.DeadlineExceeded)]
    public async Task Serializer_entry_rechecks_original_window(string scenario, ProviderFailureCode expected)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var handler = new TextRecordingHandler { BeforeSerialization = () => clock.Advance(TimeSpan.FromSeconds(5)) };
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits { MaxRequestTime = TimeSpan.FromSeconds(scenario == "request" ? 5 : 60) };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var result = await TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits,
            TextFixtures.Authorize(context, limits, expiresAt: scenario == "consent" ? ProviderFixtures.Now.AddSeconds(5) : null)));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.Empty(handler.Body);
    }

    [Fact]
    public void Public_catalog_is_explicit_and_live_unverified_and_input_is_bounded()
    {
        var capabilities = OpenAiTextGenerationCatalog.Describe(TextFixtures.Selection);
        Assert.Equal(EvidenceProvenance.NotRun, capabilities.Provenance);
        Assert.Equal(CapabilitySupport.Unknown, capabilities.LlmTextDeltas);
        Assert.Equal(CancellationCapability.Unknown, capabilities.Cancellation);
        Assert.Equal("conversation", capabilities.ModelId);
        Assert.Equal(new[] { "gpt-4.1-mini-2025-04-14", "gpt-4.1-2025-04-14" },
            OpenAiTextGenerationCatalog.SupportedModelIds);
        Assert.True(OpenAiTextGenerationCatalog.SupportsModel("gpt-4.1-2025-04-14"));
        Assert.False(OpenAiTextGenerationCatalog.SupportsModel(null));
        Assert.Throws<ContractException>(() => OpenAiTextGenerationCatalog.Describe(new("conversation", "gpt-4.1-mini")));
        Assert.Throws<ContractException>(() => new BoundedTextInput(new string('x', 16_385)));
        Assert.Throws<ContractException>(() => new BoundedTextInput("Fixture", history: Enumerable.Repeat(new TextHistoryMessage(TextHistoryRole.User, "a"),
            BoundedTextInput.HardMaxHistoryMessages + 1)));
        Assert.Throws<ContractException>(() => new BoundedTextInput("Fixture", history: Enumerable.Repeat(new TextHistoryMessage(TextHistoryRole.User,
            new string('a', 16_000)), BoundedTextInput.HardMaxInputUtf8Bytes / 16_000 + 1)));
        Assert.Throws<ContractException>(() => new BoundedTextInput("\ud800"));
        Assert.Throws<ContractException>(() => new BoundedTextInput(" "));
        Assert.Throws<ContractException>(() => (new TextGenerationLimits { MaxContextTokens = 64 }).Validate());
        Assert.Throws<ContractException>(() => (new TextGenerationLimits { MaxOutputTokens = TextGenerationLimits.HardMaxOutputTokens + 1 }).Validate());
        Assert.Throws<ContractException>(() => (new TextGenerationLimits { MaxEvents = TextGenerationLimits.HardMaxEvents + 1 }).Validate());
        using var safe = OpenAiTransport.CreateProductionHandler();
        Assert.False(safe.AllowAutoRedirect);
        Assert.False(safe.UseCookies);
        Assert.Null(safe.SslOptions.RemoteCertificateValidationCallback);
        Assert.DoesNotContain(typeof(OpenAiTextGenerationAdapter).GetMethods(),
            method => method.IsPublic && method.GetParameters().Any(p => p.ParameterType == typeof(HttpMessageHandler)));
    }
}
