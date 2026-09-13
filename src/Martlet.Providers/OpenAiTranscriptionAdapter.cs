using System.Net;
using System.Net.Http.Headers;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

public sealed class OpenAiTranscriptionAdapter : IDisposable
{
    private readonly HttpClient client;
    private readonly IProviderCredentialSource credentials;
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    private bool disposed;

    private OpenAiTranscriptionAdapter(HttpMessageHandler handler, IProviderCredentialSource credentials,
        TimeProvider clock, EvidenceProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(clock);
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        this.credentials = credentials;
        this.clock = clock;
        this.provenance = provenance;
    }

    public static OpenAiTranscriptionAdapter Create(IProviderCredentialSource credentials, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new(CreateProductionHandler(), credentials, timeProvider ?? TimeProvider.System, EvidenceProvenance.Live);
    }

    internal static SocketsHttpHandler CreateProductionHandler() => OpenAiTransport.CreateProductionHandler();

    // Not a public production override: fixtures cannot waive production redirect/TLS policy.
    internal static OpenAiTranscriptionAdapter CreateForFixture(HttpMessageHandler handler,
        IProviderCredentialSource credentials, TimeProvider? clock = null) =>
        new(handler, credentials, clock ?? TimeProvider.System, EvidenceProvenance.Fixture);

    public Task<TranscriptionResult> TranscribeAsync(ProviderRequestContext context,
        string upstreamModelId, BoundedWaveAudio audio, TranscriptionLimits limits,
        AudioUploadAuthorization? authorization, CancellationToken cancellationToken = default) =>
        TranscribeAsync(context, upstreamModelId, audio, limits, authorization, cancellationToken, CancellationToken.None);

    public async Task<TranscriptionResult> TranscribeAsync(ProviderRequestContext context,
        string upstreamModelId, BoundedWaveAudio audio, TranscriptionLimits limits,
        AudioUploadAuthorization? authorization, CancellationToken cancellationToken,
        CancellationToken operationCancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        if (cancellationToken.IsCancellationRequested || operationCancellationToken.IsCancellationRequested)
            return Canceled(context);
        long startedAt = clock.GetTimestamp();
        var startedUtc = clock.GetUtcNow();
        var remaining = context.Deadline - startedUtc;
        if (remaining <= TimeSpan.Zero)
            return Failed(context, ProviderFailureCode.DeadlineExceeded);
        if (!OpenAiTranscriptionCatalog.SupportsModel(upstreamModelId))
            return Failed(context, ProviderFailureCode.ModelUnsupported);

        var binding = new ProviderCredentialBinding(OpenAiTranscriptionCatalog.Origin, ProviderRole.Stt, upstreamModelId);
        var consentFailure = ValidateAuthorization(authorization, context, binding, limits, audio);
        if (consentFailure is { } failure)
            return Failed(context, failure);
        if (!authorization!.TryConsume())
            return Failed(context, ProviderFailureCode.ConsentConsumed);

        // Bound the entire operation, including credential retrieval, upload and response body.
        // A shorter authorization expiry also aborts this local request.
        var requestWindow = remaining < limits.MaxRequestTime ? remaining : limits.MaxRequestTime;
        var consentWindow = authorization.ExpiresAt - startedUtc;
        var timeout = Min(requestWindow, consentWindow) - clock.GetElapsedTime(startedAt);
        if (timeout <= TimeSpan.Zero)
            return Failed(context, requestWindow <= consentWindow
                ? ProviderFailureCode.DeadlineExceeded : ProviderFailureCode.ConsentExpired);
        ProviderRequestWindow? window = null;
        void EnsureActive() => window!.EnsureActive();

        try
        {
            window = new ProviderRequestWindow(clock, startedAt, startedUtc,
                context.Deadline, limits.MaxRequestTime, authorization.ExpiresAt, cancellationToken, operationCancellationToken);
            var token = window.Token;
            EnsureActive();
            using var credential = await credentials.ResolveAsync(binding, token).ConfigureAwait(false);
            EnsureActive();
            if (credential is null)
                return Failed(context, ProviderFailureCode.CredentialUnavailable);
            if (credential.Binding != binding || !OpenAiTranscriptionCatalog.IsApprovedOrigin(credential.Binding.Origin))
                return Failed(context, ProviderFailureCode.CredentialBindingMismatch);

            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiTranscriptionCatalog.Endpoint)
            {
                // One bounded file, never streaming inference or protocol-upgrade retry.
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = credential.CreateAuthorization();
            using var multipart = new MultipartFormDataContent();
            using var file = audio.CreateContent();
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            multipart.Add(file, "file", "utterance.wav");
            multipart.Add(new StringContent(upstreamModelId), "model");
            multipart.Add(new StringContent("json"), "response_format");
            multipart.Add(new StringContent("false"), "stream");
            request.Content = new SingleSendContent(multipart, EnsureActive);
            EnsureActive();

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            EnsureActive();
            if ((int)response.StatusCode is >= 300 and <= 399)
                return Failed(context, ProviderFailureCode.RedirectRejected);

            bool success = response.StatusCode == HttpStatusCode.OK;
            ReadOnlyMemory<byte> bytes = ReadOnlyMemory<byte>.Empty;
            ProviderFailureCode? readFailure;
            try
            {
                (bytes, readFailure) = await OpenAiTransport.ReadBoundedAsync(response.Content, limits.MaxResponseBytes, token).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                readFailure = error.HttpRequestError == HttpRequestError.ResponseEnded
                    ? ProviderFailureCode.ResponseTruncated : ProviderFailureCode.Network;
            }
            catch (IOException error)
            {
                readFailure = error is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded }
                    ? ProviderFailureCode.ResponseTruncated : ProviderFailureCode.Network;
            }
            EnsureActive();
            // An unreadable optional error body must not replace the known HTTP failure or advice.
            if (!success)
                return Failed(context, OpenAiResponseParser.Classify(response.StatusCode, bytes), OpenAiTransport.RetryAdvice(response, clock));
            if (readFailure is { } bodyFailure)
                return Failed(context, bodyFailure);
            if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentEncoding.Count != 0)
                return Failed(context, ProviderFailureCode.ResponseSchema);
            try
            {
                var result = OpenAiResponseParser.Parse(bytes, context, provenance, limits);
                EnsureActive();
                return result;
            }
            catch (ContractException)
            {
                return cancellationToken.IsCancellationRequested || operationCancellationToken.IsCancellationRequested
                    ? Canceled(context) : Failed(context, ProviderFailureCode.ResponseSchema);
            }
        }
        catch (Exception error) when (
            (cancellationToken.IsCancellationRequested || operationCancellationToken.IsCancellationRequested) &&
            error is OperationCanceledException or RequestCutoffException or CredentialUnavailableException or
                HttpRequestException or IOException)
        {
            return Canceled(context);
        }
        catch (OperationCanceledException) when (window?.DeadlineCanceled == true)
        {
            return Failed(context, ProviderFailureCode.DeadlineExceeded);
        }
        catch (RequestCutoffException error)
        {
            return Failed(context, error.Code);
        }
        catch (CredentialUnavailableException)
        {
            return Failed(context, ProviderFailureCode.CredentialUnavailable);
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ResponseEnded)
        {
            return Failed(context, ProviderFailureCode.ResponseTruncated);
        }
        catch (HttpIOException error) when (error.HttpRequestError == HttpRequestError.ResponseEnded)
        {
            return Failed(context, ProviderFailureCode.ResponseTruncated);
        }
        catch (HttpRequestException)
        {
            return Failed(context, ProviderFailureCode.Network);
        }
        catch (IOException)
        {
            return Failed(context, ProviderFailureCode.Network);
        }
        finally
        {
            window?.Dispose();
        }
    }

    private ProviderFailureCode? ValidateAuthorization(AudioUploadAuthorization? authorization,
        ProviderRequestContext context, ProviderCredentialBinding binding, TranscriptionLimits limits, BoundedWaveAudio audio)
    {
        if (authorization is null || !authorization.AllowAudioUpload || !authorization.AllowPotentialCharges)
            return ProviderFailureCode.ConsentMissing;
        if (authorization.Binding is null || !OpenAiTranscriptionCatalog.IsApprovedOrigin(authorization.Binding.Origin))
            return ProviderFailureCode.OriginRejected;
        if (authorization.Binding != binding || authorization.Ids != context.Ids || authorization.Epoch != context.Epoch ||
            authorization.Limits != limits)
            return ProviderFailureCode.ConsentMismatch;
        if (authorization.ExpiresAt <= clock.GetUtcNow())
            return ProviderFailureCode.ConsentExpired;
        if (audio.ByteLength > limits.MaxAudioBytes ||
            (long)audio.SamplesPerChannel * TimeSpan.TicksPerSecond > limits.MaxAudioDuration.Ticks * audio.Format.SampleRate)
            return ProviderFailureCode.AudioLimit;
        return null;
    }

    private TranscriptionResult Failed(ProviderRequestContext context, ProviderFailureCode code, TimeSpan? retryAfter = null) =>
        new(context, provenance, code == ProviderFailureCode.DeadlineExceeded
            ? TranscriptionOutcome.DeadlineExceeded : TranscriptionOutcome.Failed, failure: new(code, retryAfter));

    private TranscriptionResult Canceled(ProviderRequestContext context) => new(context, provenance, TranscriptionOutcome.Canceled);
    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        client.Dispose();
    }
}
