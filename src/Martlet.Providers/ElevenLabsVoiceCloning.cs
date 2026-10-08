using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>The voice ElevenLabs made from a recording: its <c>voice_id</c>, and whether ElevenLabs wants the voice verified
/// before it speaks with it.</summary>
public sealed record ElevenLabsClonedVoice(string VoiceId, bool RequiresVerification);

/// <summary>Instant Voice Cloning (<c>POST /v1/voices/add</c>, multipart form with <c>name</c>, <c>files</c>,
/// <c>remove_background_noise</c> and <c>description</c>), as ElevenLabs documents it (read 2026-10-07). It uploads the owner's
/// recording to the owner's ElevenLabs account, so call it only after the owner explicitly chose to (Companion › Voice). The
/// voice stays in that account (My Voices) until the owner deletes it there. Never run against the live service.</summary>
public sealed class ElevenLabsVoiceCloner : IDisposable
{
    public const string Api = "ElevenLabs Instant Voice Cloning";
    /// <summary>The largest recording Martlet uploads.</summary>
    public const int MaximumRecordingBytes = 32 * 1024 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient http;
    private readonly Uri origin;

    /// <param name="origin">ElevenLabs' own origin (the default), or a numeric loopback origin for a local fixture that follows
    /// the documented protocol.</param>
    public ElevenLabsVoiceCloner(Uri? origin = null, HttpMessageHandler? handler = null)
    {
        origin ??= ElevenLabsSpeechCatalog.Origin;
        if (!ElevenLabsSpeechCatalog.IsAllowedOrigin(origin))
            throw new ArgumentException("Voice cloning goes only to api.elevenlabs.io or a local fixture on a loopback address.", nameof(origin));
        this.origin = origin;
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, Credentials = null,
            DefaultProxyCredentials = null, MaxResponseHeadersLength = 16
        }) { Timeout = TimeSpan.FromSeconds(90) };
    }

    /// <summary>Uploads <paramref name="wave"/> (a WAV recording) as a new voice named <paramref name="name"/> in the account of
    /// <paramref name="apiKey"/>. Throws <see cref="ElevenLabsException"/> with ElevenLabs' reason when it refuses.</summary>
    public async Task<ElevenLabsClonedVoice> CloneAsync(string apiKey, string name, ReadOnlyMemory<byte> wave, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 || apiKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ElevenLabsException(ProviderFailureCode.CredentialUnavailable, "Paste your ElevenLabs API key first.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("A voice name is 1-100 characters on one line.", nameof(name));
        if (wave.Length is < 44 or > MaximumRecordingBytes)
            throw new ElevenLabsException(ProviderFailureCode.AudioLimit, "The recording is empty or too large to upload.");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "name");
        var file = new ReadOnlyMemoryContent(wave);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "files", "voice.wav");
        form.Add(new StringContent("false"), "remove_background_noise");
        form.Add(new StringContent("Cloned by Martlet from a saved speaking voice."), "description");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, ElevenLabsSpeechCatalog.ClonePath)) { Content = form };
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { throw Fail(ProviderFailureCode.Network, "Martlet couldn't reach ElevenLabs."); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Fail(ProviderFailureCode.DeadlineExceeded, "ElevenLabs didn't answer in time.");
        }
        using (response)
        {
            var (body, failure) = await OpenAiTransport.ReadBoundedAsync(response.Content, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            if (failure is { } tooLarge) throw Fail(tooLarge, "ElevenLabs' answer was too large.");
            var said = ProviderDiagnostics.Describe(body);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode switch
                {
                    401 => ProviderFailureCode.Authentication,
                    403 => ProviderFailureCode.PermissionDenied,
                    402 => ProviderFailureCode.QuotaExceeded,
                    429 => ProviderFailureCode.RateLimited,
                    >= 300 and < 400 => ProviderFailureCode.RedirectRejected,
                    >= 500 => ProviderFailureCode.Server,
                    _ => ProviderFailureCode.RequestRejected
                };
                throw Fail(code, $"HTTP {(int)response.StatusCode}{(said is null ? "" : ": " + said)}");
            }
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("voice_id", out var id) && id.ValueKind == JsonValueKind.String &&
                    id.GetString() is { } voiceId && ElevenLabsSetup.IsVoiceId(voiceId))
                    return new(voiceId, root.TryGetProperty("requires_verification", out var verify) && verify.ValueKind == JsonValueKind.True);
            }
            catch (JsonException) { }
            throw Fail(ProviderFailureCode.ResponseSchema, "ElevenLabs' answer had no valid voice_id.");
        }
    }

    private static ElevenLabsException Fail(ProviderFailureCode code, string detail)
    {
        ProviderDiagnostics.Report(Api, code, detail);
        return new(code, detail);
    }

    public void Dispose() => http.Dispose();
}
