using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class SpeechAuthorizationTests
{
    [Theory]
    [InlineData("alloy")]
    [InlineData("coral")]
    public async Task Exact_speech_request_is_explicit_and_credential_scoped(string voice)
    {
        var handler = SpeechFixtures.Handler();
        handler.Inspect = request =>
        {
            Assert.Equal(new Uri("https://api.openai.com/v1/audio/speech"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("application/octet-stream", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("Bearer " + ProviderFixtures.Secret, request.Headers.Authorization!.ToString());
            Assert.Equal("application/json; charset=utf-8", request.Content!.Headers.ContentType!.ToString());
            Assert.Empty(request.RequestUri!.Query);
        };
        BoundProviderCredential? credential = null;
        var source = new FixtureCredentials
        {
            Resolve = (binding, _) =>
            {
                Assert.Equal(SpeechFixtures.Binding, binding);
                return ValueTask.FromResult<BoundProviderCredential?>(credential = new(binding, ProviderFixtures.Secret));
            }
        };
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput(ProviderFixtures.ContentCanary + " \"\\\n\u00e9");
        var selection = SpeechFixtures.Selection with { Voice = voice };
        var limits = new SpeechSynthesisLimits();
        var authorization = SpeechFixtures.Authorize(context, input, limits, selection: selection);
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, new FixtureClock());
        var stream = adapter.Stream(context, selection, input, limits, authorization);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
        var result = await SpeechFixtures.Collect(stream);
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Result.Outcome);
        using var json = JsonDocument.Parse(handler.Body);
        var root = json.RootElement;
        Assert.Equal(new[] { "input", "model", "response_format", "stream_format", "voice" },
            root.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(input.Text, root.GetProperty("input").GetString());
        Assert.Equal(SpeechFixtures.Model, root.GetProperty("model").GetString());
        Assert.Equal(voice, root.GetProperty("voice").GetString());
        Assert.Equal("pcm", root.GetProperty("response_format").GetString());
        Assert.Equal("audio", root.GetProperty("stream_format").GetString());
        Assert.DoesNotContain(ProviderFixtures.Secret, Encoding.UTF8.GetString(handler.Body));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, source.Calls);
        Assert.Throws<CredentialUnavailableException>(() => credential!.CreateAuthorization());
        var metadata = input + authorization.ToString() + stream + result.Result.ToString() +
            JsonSerializer.Serialize(input) + JsonSerializer.Serialize(authorization) + JsonSerializer.Serialize(result.Result);
        Assert.DoesNotContain(ProviderFixtures.Secret, metadata);
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, metadata);
    }

    [Theory]
    [InlineData("missing", ProviderFailureCode.ConsentMissing)]
    [InlineData("no-text", ProviderFailureCode.ConsentMissing)]
    [InlineData("no-charge", ProviderFailureCode.ConsentMissing)]
    [InlineData("no-disclosure", ProviderFailureCode.ConsentMissing)]
    [InlineData("stt", ProviderFailureCode.ConsentMismatch)]
    [InlineData("llm", ProviderFailureCode.ConsentMismatch)]
    [InlineData("bound-model", ProviderFailureCode.ConsentMismatch)]
    [InlineData("alias", ProviderFailureCode.ConsentMismatch)]
    [InlineData("voice", ProviderFailureCode.ConsentMismatch)]
    [InlineData("input", ProviderFailureCode.ConsentMismatch)]
    [InlineData("identical-new-input", ProviderFailureCode.ConsentMismatch)]
    [InlineData("format-scope", ProviderFailureCode.ConsentMismatch)]
    [InlineData("origin", ProviderFailureCode.OriginRejected)]
    [InlineData("http", ProviderFailureCode.OriginRejected)]
    [InlineData("port", ProviderFailureCode.OriginRejected)]
    [InlineData("path", ProviderFailureCode.OriginRejected)]
    [InlineData("query", ProviderFailureCode.OriginRejected)]
    [InlineData("fragment", ProviderFailureCode.OriginRejected)]
    [InlineData("userinfo", ProviderFailureCode.OriginRejected)]
    [InlineData("relative", ProviderFailureCode.OriginRejected)]
    [InlineData("session", ProviderFailureCode.ConsentMismatch)]
    [InlineData("turn", ProviderFailureCode.ConsentMismatch)]
    [InlineData("request", ProviderFailureCode.ConsentMismatch)]
    [InlineData("epoch", ProviderFailureCode.ConsentMismatch)]
    [InlineData("expired", ProviderFailureCode.ConsentExpired)]
    [InlineData("input-limit", ProviderFailureCode.InputLimit)]
    [InlineData("unsupported-model", ProviderFailureCode.ModelUnsupported)]
    [InlineData("floating-model", ProviderFailureCode.ModelUnsupported)]
    [InlineData("unsupported-voice", ProviderFailureCode.VoiceUnsupported)]
    [InlineData("custom-voice", ProviderFailureCode.VoiceUnsupported)]
    [InlineData("unsupported-format", ProviderFailureCode.SpeechMediaUnsupported)]
    public async Task Rejected_scope_performs_zero_credential_lookups_or_sends(string scenario, ProviderFailureCode expected)
    {
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput(ProviderFixtures.ContentCanary);
        var limits = new SpeechSynthesisLimits { MaxInputBytes = scenario == "input-limit" ? 1 : 1536 };
        var selection = SpeechFixtures.Selection;
        var binding = scenario switch
        {
            "stt" => SpeechFixtures.Binding with { Role = ProviderRole.Stt },
            "llm" => SpeechFixtures.Binding with { Role = ProviderRole.Llm },
            "bound-model" => SpeechFixtures.Binding with { UpstreamModelId = "different" },
            "origin" => SpeechFixtures.Binding with { Origin = new("https://example.com") },
            "http" => SpeechFixtures.Binding with { Origin = new("http://api.openai.com") },
            "port" => SpeechFixtures.Binding with { Origin = new("https://api.openai.com:444") },
            "path" => SpeechFixtures.Binding with { Origin = new("https://api.openai.com/v1/responses") },
            "query" => SpeechFixtures.Binding with { Origin = new("https://api.openai.com/?secret=fixture") },
            "fragment" => SpeechFixtures.Binding with { Origin = new("https://api.openai.com/#fixture") },
            "userinfo" => SpeechFixtures.Binding with { Origin = new("https://fixture:secret@api.openai.com") },
            "relative" => SpeechFixtures.Binding with { Origin = new("relative", UriKind.Relative) },
            _ => SpeechFixtures.Binding
        };
        var ids = scenario switch
        {
            "session" => context.Ids with { SessionId = Guid.NewGuid() },
            "turn" => context.Ids with { TurnId = Guid.NewGuid() },
            "request" => context.Ids with { RequestId = Guid.NewGuid() },
            _ => context.Ids
        };
        var authorizedSelection = scenario switch
        {
            "alias" => selection with { ModelAlias = "another" },
            "voice" => selection with { Voice = "coral" },
            "format-scope" => selection with { OutputFormat = (SpeechOutputFormat)99 },
            _ => selection
        };
        var authorizedInput = scenario switch
        {
            "input" => new BoundedSpeechInput("Different segment."),
            "identical-new-input" => new BoundedSpeechInput(input.Text),
            _ => input
        };
        var authorization = scenario == "missing" ? null : SpeechFixtures.Authorize(context, authorizedInput, limits,
            expiresAt: scenario == "expired" ? ProviderFixtures.Now : null, binding: binding,
            selection: authorizedSelection, ids: ids, epoch: scenario == "epoch" ? context.Epoch + 1 : null,
            text: scenario != "no-text", charges: scenario != "no-charge", disclosure: scenario != "no-disclosure");
        selection = scenario switch
        {
            "unsupported-model" => selection with { UpstreamModelId = "tts-1" },
            "floating-model" => selection with { UpstreamModelId = "gpt-4o-mini-tts" },
            "unsupported-voice" => selection with { Voice = "cedar" },
            "custom-voice" => selection with { Voice = "voice_1234" },
            "unsupported-format" => selection with { OutputFormat = (SpeechOutputFormat)99 },
            _ => selection
        };
        var source = new FixtureCredentials();
        var handler = SpeechFixtures.Handler();
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, new FixtureClock());
        var result = await SpeechFixtures.Collect(adapter.Stream(context, selection, input, limits, authorization));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.Empty(result.Frames);
        Assert.Null(result.Result.HttpStatusCode);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(handler.Body);
        Assert.DoesNotContain("fixture:secret", JsonSerializer.Serialize(authorization));
    }

    public static TheoryData<SpeechSynthesisLimits> DifferentLimits => new()
    {
        new() { MaxInputBytes = 1 }, new() { MaxAudioBytes = 2 }, new() { MaxAudioDuration = TimeSpan.FromSeconds(1) },
        new() { MaxErrorBytes = 128 }, new() { FirstAudioTimeout = TimeSpan.FromSeconds(1) },
        new() { IdleTimeout = TimeSpan.FromSeconds(1) }, new() { MaxRequestTime = TimeSpan.FromSeconds(1) }
    };

    [Theory]
    [MemberData(nameof(DifferentLimits))]
    public async Task Every_limit_is_bound_in_authorization(SpeechSynthesisLimits authorizedLimits)
    {
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var source = new FixtureCredentials();
        var handler = SpeechFixtures.Handler();
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, new FixtureClock());
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, new(),
            SpeechFixtures.Authorize(context, input, authorizedLimits)));
        Assert.Equal(ProviderFailureCode.ConsentMismatch, result.Result.Failure!.Code);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Simultaneous_consumption_and_later_reuse_allow_exactly_one_attempt()
    {
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var authorization = SpeechFixtures.Authorize(context, input, limits);
        var handler = SpeechFixtures.Handler();
        var source = new FixtureCredentials
        {
            Resolve = async (binding, _) => { await Task.Yield(); return new(binding, ProviderFixtures.Secret); }
        };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, new FixtureClock());
        var streams = Enumerable.Range(0, 8).Select(_ => adapter.Stream(context, SpeechFixtures.Selection, input, limits, authorization)).ToArray();
        var results = await Task.WhenAll(streams.Select(s => SpeechFixtures.Collect(s)));
        Assert.Single(results, r => r.Result.Outcome == SpeechSynthesisOutcome.Completed);
        Assert.Equal(7, results.Count(r => r.Result.Failure?.Code == ProviderFailureCode.ConsentConsumed));
        var reuse = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits, authorization));
        Assert.Equal(ProviderFailureCode.ConsentConsumed, reuse.Result.Failure!.Code);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, handler.Calls);
        Assert.All(streams, s => Assert.Throws<InvalidOperationException>(() => s.GetAsyncEnumerator()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unavailable")]
    [InlineData("stt")]
    [InlineData("origin")]
    [InlineData("model")]
    public async Task Returned_credential_must_match_and_is_disposed(string cause)
    {
        BoundProviderCredential? credential = null;
        var source = new FixtureCredentials
        {
            Resolve = (binding, _) =>
            {
                if (cause == "unavailable") throw new CredentialUnavailableException();
                var wrong = cause switch
                {
                    "stt" => binding with { Role = ProviderRole.Stt },
                    "origin" => binding with { Origin = new("https://example.com") },
                    _ => binding with { UpstreamModelId = "other" }
                };
                return ValueTask.FromResult<BoundProviderCredential?>(credential = cause == "null" ? null : new(wrong, ProviderFixtures.Secret));
            }
        };
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var handler = SpeechFixtures.Handler();
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, new FixtureClock());
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits)));
        Assert.Equal(cause is "null" or "unavailable" ? ProviderFailureCode.CredentialUnavailable : ProviderFailureCode.CredentialBindingMismatch,
            result.Result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        if (credential is not null) Assert.Throws<CredentialUnavailableException>(() => credential.CreateAuthorization());
    }

    [Fact]
    public void Catalog_factory_and_immutable_input_do_not_claim_live_readiness_or_do_work()
    {
        var source = new FixtureCredentials();
        using var adapter = OpenAiSpeechSynthesisAdapter.Create(source);
        var capabilities = OpenAiSpeechSynthesisCatalog.Describe(SpeechFixtures.Selection);
        Assert.Equal(0, source.Calls);
        Assert.Equal(EvidenceProvenance.NotRun, capabilities.Provenance);
        Assert.Equal(CapabilitySupport.Unknown, capabilities.TtsAudioTransport);
        Assert.Equal(CapabilitySupport.Unknown, capabilities.TtsIncrementalSynthesis);
        Assert.Equal(CancellationCapability.Unknown, capabilities.Cancellation);
        Assert.Equal(ProviderRole.Tts, capabilities.Role);
        Assert.False(OpenAiSpeechSynthesisCatalog.SupportsModel(null));
        Assert.False(OpenAiSpeechSynthesisCatalog.SupportsVoice(null));
        Assert.Single(OpenAiSpeechSynthesisCatalog.SupportedModelIds);
        Assert.Throws<ContractException>(() => OpenAiSpeechSynthesisCatalog.Describe(SpeechFixtures.Selection with { Voice = "unreviewed" }));
        Assert.Throws<ContractException>(() => OpenAiSpeechSynthesisCatalog.Describe(SpeechFixtures.Selection with { UpstreamModelId = "gpt-4o-mini-tts" }));
        Assert.Equal(1536, new BoundedSpeechInput(new string('a', 1536)).Utf8Bytes);
        Assert.Equal(1536, new BoundedSpeechInput(new string('\u00e9', 768)).Utf8Bytes);
        Assert.Throws<ContractException>(() => new BoundedSpeechInput(new string('a', 1537)));
        Assert.Throws<ContractException>(() => new BoundedSpeechInput(new string('\u00e9', 769)));
        Assert.Throws<ContractException>(() => new BoundedSpeechInput("\ud800"));
        Assert.Throws<ContractException>(() => new BoundedSpeechInput(" \n"));
        Assert.Throws<ContractException>(() => new BoundedSpeechInput("no\u0000"));
        Assert.DoesNotContain(typeof(OpenAiSpeechSynthesisAdapter).GetMethods(),
            m => m.IsPublic && m.GetParameters().Any(p => p.ParameterType == typeof(HttpMessageHandler) || p.ParameterType == typeof(HttpClient)));
        using var safe = OpenAiTransport.CreateProductionHandler();
        Assert.False(safe.AllowAutoRedirect);
        Assert.False(safe.UseCookies);
        Assert.Equal(DecompressionMethods.None, safe.AutomaticDecompression);
        Assert.Null(safe.Credentials);
        Assert.Null(safe.DefaultProxyCredentials);
        Assert.Null(safe.SslOptions.RemoteCertificateValidationCallback);
    }

    public static TheoryData<SpeechSynthesisLimits> InvalidLimits => new()
    {
        new() { MaxInputBytes = 0 }, new() { MaxInputBytes = 1537 }, new() { MaxAudioBytes = 0 },
        new() { MaxAudioBytes = 3 }, new() { MaxAudioBytes = 4_320_002 }, new() { MaxAudioDuration = TimeSpan.FromTicks(1) },
        new() { MaxAudioDuration = TimeSpan.FromSeconds(91) }, new() { MaxErrorBytes = 127 },
        new() { MaxErrorBytes = 65_537 }, new() { FirstAudioTimeout = TimeSpan.Zero },
        new() { IdleTimeout = TimeSpan.FromSeconds(91) }, new() { MaxRequestTime = TimeSpan.FromSeconds(91) }
    };

    [Theory]
    [MemberData(nameof(InvalidLimits))]
    public void Invalid_limits_fail_the_actual_contract(SpeechSynthesisLimits limits) =>
        Assert.Throws<ContractException>(limits.Validate);
}
