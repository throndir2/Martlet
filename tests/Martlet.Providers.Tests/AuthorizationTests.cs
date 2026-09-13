using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class AuthorizationTests
{
    [Theory]
    [InlineData("missing", ProviderFailureCode.ConsentMissing)]
    [InlineData("audio", ProviderFailureCode.ConsentMissing)]
    [InlineData("charges", ProviderFailureCode.ConsentMissing)]
    [InlineData("model", ProviderFailureCode.ConsentMismatch)]
    [InlineData("role", ProviderFailureCode.ConsentMismatch)]
    [InlineData("request", ProviderFailureCode.ConsentMismatch)]
    [InlineData("turn", ProviderFailureCode.ConsentMismatch)]
    [InlineData("session", ProviderFailureCode.ConsentMismatch)]
    [InlineData("epoch", ProviderFailureCode.ConsentMismatch)]
    [InlineData("limits", ProviderFailureCode.ConsentMismatch)]
    [InlineData("expired", ProviderFailureCode.ConsentExpired)]
    public async Task Missing_or_mismatched_consent_blocks_secret_resolution_and_serialization(string fault, ProviderFailureCode expected)
    {
        var context = ProviderFixtures.Context();
        var consentContext = fault switch
        {
            "request" => context with { Ids = context.Ids with { RequestId = Guid.NewGuid() } },
            "turn" => context with { Ids = context.Ids with { TurnId = Guid.NewGuid() } },
            "session" => context with { Ids = context.Ids with { SessionId = Guid.NewGuid() } },
            "epoch" => context with { Epoch = context.Epoch + 1 },
            _ => context
        };
        var limits = new TranscriptionLimits();
        var consentLimits = fault == "limits" ? limits with { MaxResponseBytes = 100 } : limits;
        var binding = fault switch
        {
            "model" => ProviderFixtures.Binding("whisper-1"),
            "role" => ProviderFixtures.Binding() with { Role = ProviderRole.Llm },
            _ => ProviderFixtures.Binding()
        };
        var consent = fault == "missing" ? null : ProviderFixtures.Authorize(consentContext, consentLimits, binding,
            fault == "expired" ? ProviderFixtures.Now : null, fault != "audio", fault != "charges");
        var handler = new RecordingHandler();
        var credentials = new FixtureCredentials();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe",
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), limits, consent);
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(handler.Body);
        Assert.Equal(0, credentials.Calls);
    }

    [Theory]
    [InlineData("http://api.openai.com")]
    [InlineData("https://api.openai.com.attacker.invalid")]
    [InlineData("https://api.openai.com:444")]
    [InlineData("https://api.openai.com@attacker.invalid")]
    [InlineData("https://name:secret@api.openai.com")]
    [InlineData("https://api.openai.com/?key=secret")]
    [InlineData("https://api.openai.com/#secret")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com.")]
    [InlineData("/v1/audio/transcriptions")]
    public async Task Origin_not_exactly_approved_never_receives_key_or_audio(string origin)
    {
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var handler = new RecordingHandler();
        var credentials = new FixtureCredentials();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
            limits, ProviderFixtures.Authorize(context, limits, ProviderFixtures.Binding() with { Origin = new(origin, UriKind.RelativeOrAbsolute) }));
        Assert.Equal(ProviderFailureCode.OriginRejected, result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, credentials.Calls);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("model")]
    [InlineData("role")]
    [InlineData("missing")]
    [InlineData("unavailable")]
    public async Task Secret_store_must_return_matching_bound_credential(string fault)
    {
        var credentials = new FixtureCredentials
        {
            Resolve = (binding, _) => fault switch
            {
                "missing" => ValueTask.FromResult<BoundProviderCredential?>(null),
                "unavailable" => throw new CredentialUnavailableException(),
                _ => ValueTask.FromResult<BoundProviderCredential?>(new(binding with
                {
                    Origin = fault == "origin" ? new("https://attacker.invalid") : binding.Origin,
                    UpstreamModelId = fault == "model" ? "whisper-1" : binding.UpstreamModelId,
                    Role = fault == "role" ? ProviderRole.Tts : binding.Role
                }, ProviderFixtures.Secret))
            }
        };
        var handler = new RecordingHandler();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
            limits, ProviderFixtures.Authorize(context, limits));
        Assert.Equal(fault is "missing" or "unavailable" ? ProviderFailureCode.CredentialUnavailable :
            ProviderFailureCode.CredentialBindingMismatch, result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("GPT-TRANSCRIBE")]
    [InlineData("gpt-4o-transcribe-diarize")]
    [InlineData("../models/whisper-1")]
    [InlineData("https://attacker.invalid/model")]
    public async Task No_default_arbitrary_or_unsupported_model(string model)
    {
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var handler = new RecordingHandler();
        var credentials = new FixtureCredentials();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var result = await adapter.TranscribeAsync(context, model, BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
            limits, ProviderFixtures.Authorize(context, limits, ProviderFixtures.Binding(model)));
        Assert.Equal(ProviderFailureCode.ModelUnsupported, result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, credentials.Calls);
        Assert.Throws<ContractException>(() => OpenAiTranscriptionCatalog.Describe("stt", model));
    }

    [Fact]
    public async Task Authorization_is_single_use_even_after_failed_request()
    {
        var handler = new RecordingHandler { Respond = (_, _) => throw new HttpRequestException("private exception") };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var consent = ProviderFixtures.Authorize(context, limits);
        var audio = BoundedWaveAudio.FromWave(ProviderFixtures.Wave());
        Assert.Equal(ProviderFailureCode.Network, (await adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits, consent)).Failure!.Code);
        Assert.Equal(ProviderFailureCode.ConsentConsumed, (await adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits, consent)).Failure!.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Same_authorization_cannot_start_two_overlapping_uploads()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler
        {
            Respond = async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return ProviderFixtures.Json();
            }
        };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        var authorization = ProviderFixtures.Authorize(context, limits);
        var audio = BoundedWaveAudio.FromWave(ProviderFixtures.Wave());
        var first = adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits, authorization);
        try
        {
            var second = await adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits, authorization);
            Assert.Equal(ProviderFailureCode.ConsentConsumed, second.Failure!.Code);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(TranscriptionOutcome.Completed, (await first).Outcome);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("correlation")]
    [InlineData("audio-bytes")]
    [InlineData("duration")]
    [InlineData("response-bytes")]
    [InlineData("text-characters")]
    [InlineData("time")]
    public async Task Invalid_context_or_limit_is_a_sanitized_programming_error_before_io(string fault)
    {
        var handler = new RecordingHandler();
        var credentials = new FixtureCredentials();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var context = ProviderFixtures.Context();
        context = fault switch
        {
            "epoch" => context with { Epoch = int.MaxValue },
            "correlation" => context with { Ids = context.Ids with { RequestId = Guid.Empty } },
            _ => context
        };
        var limits = fault switch
        {
            "audio-bytes" => new() { MaxAudioBytes = int.MaxValue },
            "duration" => new() { MaxAudioDuration = TimeSpan.MaxValue },
            "response-bytes" => new() { MaxResponseBytes = int.MaxValue },
            "text-characters" => new() { MaxTextCharacters = int.MaxValue },
            "time" => new() { MaxRequestTime = TimeSpan.MaxValue },
            _ => new TranscriptionLimits()
        };
        await Assert.ThrowsAsync<ContractException>(() => adapter.TranscribeAsync(context, "gpt-transcribe",
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), limits, ProviderFixtures.Authorize(context, limits)));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, credentials.Calls);
    }

    [Fact]
    public async Task Placeholder_credential_is_not_authorization()
    {
        var handler = new RecordingHandler();
        var credentials = new FixtureCredentials { Resolve = (binding, _) => ValueTask.FromResult<BoundProviderCredential?>(new(binding, "placeholder")) };
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var result = await adapter.TranscribeAsync(ProviderFixtures.Context(), "gpt-transcribe",
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), new(), null);
        Assert.Equal(ProviderFailureCode.ConsentMissing, result.Failure!.Code);
        Assert.Equal(0, credentials.Calls);
        Assert.Equal(0, handler.Calls);
    }
}
