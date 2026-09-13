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

    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        Credentials = null,
        DefaultProxyCredentials = null,
        MaxResponseHeadersLength = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    // Not a public production override: fixtures cannot waive production redirect/TLS policy.
    internal static OpenAiTranscriptionAdapter CreateForFixture(HttpMessageHandler handler,
        IProviderCredentialSource credentials, TimeProvider? clock = null) =>
        new(handler, credentials, clock ?? TimeProvider.System, EvidenceProvenance.Fixture);

    public async Task<TranscriptionResult> TranscribeAsync(ProviderRequestContext context,
        string upstreamModelId, BoundedWaveAudio audio, TranscriptionLimits limits,
        AudioUploadAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        if (cancellationToken.IsCancellationRequested)
            return Canceled(context);
        var remaining = context.Deadline - clock.GetUtcNow();
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
        var timeout = Min(remaining, limits.MaxRequestTime, authorization.ExpiresAt - clock.GetUtcNow());
        if (timeout <= TimeSpan.Zero)
            return Failed(context, ProviderFailureCode.ConsentExpired);
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        try
        {
            using var credential = await credentials.ResolveAsync(binding, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
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
            request.Content = new SingleSendContent(multipart);
            token.ThrowIfCancellationRequested();

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if ((int)response.StatusCode is >= 300 and <= 399)
                return Failed(context, ProviderFailureCode.RedirectRejected);

            bool success = response.StatusCode == HttpStatusCode.OK;
            var (bytes, readFailure) = await ReadBoundedAsync(response.Content, limits.MaxResponseBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!success)
                return Failed(context, OpenAiResponseParser.Classify(response.StatusCode, bytes), RetryAdvice(response));
            if (readFailure is { } bodyFailure)
                return Failed(context, bodyFailure);
            if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentEncoding.Count != 0)
                return Failed(context, ProviderFailureCode.ResponseSchema);
            try
            {
                var result = OpenAiResponseParser.Parse(bytes, context, provenance, limits);
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch (ContractException)
            {
                return Failed(context, ProviderFailureCode.ResponseSchema);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled(context);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Failed(context, ProviderFailureCode.DeadlineExceeded);
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

    private static async Task<(ReadOnlyMemory<byte> Bytes, ProviderFailureCode? Failure)> ReadBoundedAsync(
        HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        long? expected = content.Headers.ContentLength;
        if (expected > maximum)
            return (ReadOnlyMemory<byte>.Empty, ProviderFailureCode.ResponseTooLarge);
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[maximum + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
        }
        if (length > maximum)
            return (ReadOnlyMemory<byte>.Empty, ProviderFailureCode.ResponseTooLarge);
        if (expected is not null && expected != length)
            return (ReadOnlyMemory<byte>.Empty, ProviderFailureCode.ResponseTruncated);
        return (buffer.AsMemory(0, length), null);
    }

    private TimeSpan? RetryAdvice(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date - clock.GetUtcNow());
        return delay is null ? null : TimeSpan.FromSeconds(Math.Clamp(delay.Value.TotalSeconds, 0, 300));
    }

    private TranscriptionResult Failed(ProviderRequestContext context, ProviderFailureCode code, TimeSpan? retryAfter = null) =>
        new(context, provenance, code == ProviderFailureCode.DeadlineExceeded
            ? TranscriptionOutcome.DeadlineExceeded : TranscriptionOutcome.Failed, failure: new(code, retryAfter));

    private TranscriptionResult Canceled(ProviderRequestContext context) => new(context, provenance, TranscriptionOutcome.Canceled);
    private static TimeSpan Min(TimeSpan first, TimeSpan second, TimeSpan third) => new(Math.Min(first.Ticks, Math.Min(second.Ticks, third.Ticks)));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        client.Dispose();
    }

    // Prevent handler-level resubmission from serializing an upload a second time.
    private sealed class SingleSendContent : HttpContent
    {
        private readonly HttpContent inner;
        private int sent;
        public SingleSendContent(HttpContent inner)
        {
            this.inner = inner;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = inner.Headers.ContentLength ?? 0;
            return inner.Headers.ContentLength is not null;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref sent, 1) != 0)
                throw new HttpRequestException("A transcription upload cannot be replayed.");
            return inner.CopyToAsync(stream, context, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
