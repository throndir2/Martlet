using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;

namespace Martlet.Providers.Tests;

public sealed class TranscriptionTransportTests
{
    [Theory]
    [InlineData("gpt-transcribe")]
    [InlineData("gpt-4o-transcribe")]
    [InlineData("gpt-4o-mini-transcribe")]
    [InlineData("gpt-4o-mini-transcribe-2025-12-15")]
    [InlineData("whisper-1")]
    public async Task Actual_multipart_has_only_documented_fields_exact_wave_and_header_only_key(string model)
    {
        var handler = new RecordingHandler();
        var credentials = new FixtureCredentials();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        byte[] original = ProviderFixtures.Wave();
        var audio = BoundedWaveAudio.FromWave(original);
        var result = await adapter.TranscribeAsync(context, model, audio, limits,
            ProviderFixtures.Authorize(context, limits, ProviderFixtures.Binding(model)));

        Assert.Equal(TranscriptionOutcome.Completed, result.Outcome);
        Assert.Equal(EvidenceProvenance.Fixture, result.Provenance);
        Assert.Equal(context, result.Context);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", handler.Uri!.AbsoluteUri);
        Assert.Equal($"Bearer {ProviderFixtures.Secret}", handler.Authorization);
        string boundary = MediaTypeHeaderValue.Parse(handler.ContentType!).Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        string multipart = Encoding.Latin1.GetString(handler.Body);
        var parts = multipart.Split($"--{boundary}", StringSplitOptions.None);
        Assert.Equal(6, parts.Length);
        Assert.Equal("", parts[0]);
        Assert.Equal("--\r\n", parts[^1]);
        var bodies = new Dictionary<string, byte[]>();
        foreach (string part in parts[1..^1])
        {
            int endHeaders = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string[] headers = part[..endHeaders].Trim().Split("\r\n");
            var disposition = ContentDispositionHeaderValue.Parse(headers.Single(h => h.StartsWith("Content-Disposition:", StringComparison.Ordinal))[20..].Trim());
            string name = disposition.Name!.Trim('"');
            bodies.Add(name, Encoding.Latin1.GetBytes(part[(endHeaders + 4)..^2]));
            if (name == "file")
            {
                Assert.Equal("utterance.wav", disposition.FileName!.Trim('"'));
                Assert.Contains("Content-Type: audio/wav", headers);
            }
        }
        Assert.Equal(original, bodies["file"]);
        Assert.Equal(model, Encoding.UTF8.GetString(bodies["model"]));
        Assert.Equal("json", Encoding.UTF8.GetString(bodies["response_format"]));
        Assert.Equal("false", Encoding.UTF8.GetString(bodies["stream"]));
        Assert.DoesNotContain(ProviderFixtures.Secret, multipart);
        Assert.DoesNotContain(ProviderFixtures.Secret, handler.Uri.AbsoluteUri);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, credentials.Calls);
    }

    [Fact]
    public void Construction_and_catalog_are_passive_and_production_transport_is_not_overridable()
    {
        var credentials = new FixtureCredentials();
        using var adapter = OpenAiTranscriptionAdapter.Create(credentials);
        var capabilities = OpenAiTranscriptionCatalog.Describe("stt-primary", "gpt-transcribe");
        Assert.Equal(EvidenceProvenance.NotRun, capabilities.Provenance);
        Assert.Equal(CapabilitySupport.Unsupported, capabilities.SttPartials);
        Assert.Equal(CancellationCapability.Unknown, capabilities.Cancellation);
        Assert.Equal("stt-primary", capabilities.ModelId);
        Assert.Equal(0, credentials.Calls);
        Assert.Empty(typeof(OpenAiTranscriptionAdapter).GetConstructors());
        Assert.DoesNotContain(typeof(OpenAiTranscriptionAdapter).GetMethods(),
            method => method.GetParameters().Any(p => p.ParameterType == typeof(HttpClient) || p.ParameterType == typeof(HttpMessageHandler)));
        using var handler = OpenAiTranscriptionAdapter.CreateProductionHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
    }

    [Fact]
    public async Task Credential_is_bound_disposed_and_never_serialized_or_stringified()
    {
        BoundProviderCredential? credential = null;
        var credentials = new FixtureCredentials
        {
            Resolve = (binding, _) => ValueTask.FromResult<BoundProviderCredential?>(credential = new(binding, ProviderFixtures.Secret))
        };
        var handler = new RecordingHandler();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()), limits,
            ProviderFixtures.Authorize(context, limits));
        Assert.NotNull(credential);
        Assert.Throws<CredentialUnavailableException>(() => credential.CreateAuthorization());
        Assert.DoesNotContain(ProviderFixtures.Secret, credential.ToString());
        Assert.DoesNotContain(ProviderFixtures.Secret, JsonSerializer.Serialize(credential));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirect_is_rejected_without_following_location_or_retry(int status)
    {
        var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = ProviderFixtures.Json(status: status);
                response.Headers.Location = new Uri("https://attacker.invalid/collect?secret=echoed");
                return Task.FromResult(response);
            }
        };
        var result = await Run(handler);
        Assert.Equal(ProviderFailureCode.RedirectRejected, result.Failure!.Code);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("attacker", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Handler_cannot_serialize_same_upload_twice()
    {
        var handler = new RecordingHandler
        {
            Respond = async (request, token) =>
            {
                using var second = new MemoryStream();
                await request.Content!.CopyToAsync(second, token);
                return ProviderFixtures.Json();
            }
        };
        var result = await Run(handler);
        Assert.Equal(ProviderFailureCode.Network, result.Failure!.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Adapter_owns_transport_until_disposal_not_one_client_per_request()
    {
        var handler = new RecordingHandler();
        using (var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock()))
        {
            var context = ProviderFixtures.Context();
            var limits = new TranscriptionLimits();
            for (int i = 0; i < 2; i++)
            {
                await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
                    limits, ProviderFixtures.Authorize(context, limits));
                Assert.False(handler.Disposed);
            }
            Assert.Equal(2, handler.Calls);
        }
        Assert.True(handler.Disposed);
    }

    [Theory]
    [InlineData("""{"text":"A fixture."}""", ProviderEventKind.Completed, TurnOutcome.Completed)]
    [InlineData("""{"text":""}""", ProviderEventKind.NoSpeech, TurnOutcome.Suppressed)]
    [InlineData("""{"bad":"schema"}""", ProviderEventKind.Failed, TurnOutcome.Failed)]
    public async Task Terminal_event_runs_through_existing_core_validator(string json, ProviderEventKind kind, TurnOutcome outcome)
    {
        var result = await Run(new RecordingHandler { Respond = (_, _) => Task.FromResult(ProviderFixtures.Json(json)) });
        var capabilities = OpenAiTranscriptionCatalog.Describe("stt-primary", "gpt-transcribe") with
        {
            Provenance = EvidenceProvenance.Fixture, Cancellation = CancellationCapability.RequestAbort
        };
        using var sequence = new ProviderSequenceValidator(new()
        {
            Ids = result.Context.Ids, Epoch = result.Context.Epoch, Capabilities = capabilities
        }, new(), new FixtureClock());
        var terminal = result.ToTerminalEvent();
        terminal.Validate();
        sequence.Accept(terminal with { Sequence = 0, Kind = ProviderEventKind.Started, Text = null, Error = null });
        var update = sequence.AcceptJson(ContractJson.Write(terminal));
        Assert.Equal(kind, update.Snapshot.ProviderTerminal);
        Assert.Equal(outcome, update.Snapshot.Result!.Outcome);
        Assert.Equal(result.Context.Epoch, update.Snapshot.RequestEpoch);
    }

    internal static async Task<TranscriptionResult> Run(RecordingHandler handler, TranscriptionLimits? limits = null)
    {
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        limits ??= new();
        return await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
            limits, ProviderFixtures.Authorize(context, limits));
    }
}
