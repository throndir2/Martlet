using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

public sealed class OpenAiSpeechSynthesisAdapter : IDisposable
{
    private readonly HttpClient client;
    private readonly IProviderCredentialSource credentials;
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;

    private OpenAiSpeechSynthesisAdapter(HttpMessageHandler handler, IProviderCredentialSource credentials,
        TimeProvider clock, EvidenceProvenance provenance)
    {
        this.credentials = credentials;
        this.clock = clock;
        this.provenance = provenance;
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static OpenAiSpeechSynthesisAdapter Create(IProviderCredentialSource credentials, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new(OpenAiTransport.CreateProductionHandler(), credentials, timeProvider ?? TimeProvider.System, EvidenceProvenance.Live);
    }

    internal static OpenAiSpeechSynthesisAdapter CreateForFixture(HttpMessageHandler handler,
        IProviderCredentialSource credentials, TimeProvider? clock = null) =>
        new(handler, credentials, clock ?? TimeProvider.System, EvidenceProvenance.Fixture);

    public SpeechSynthesisStream Stream(ProviderRequestContext context, SpeechSynthesisSelection selection,
        BoundedSpeechInput input, SpeechSynthesisLimits limits, SpeechDisclosureAuthorization? authorization,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        ContractRules.Identifier(selection.ModelAlias);
        return new(client, credentials, clock, provenance, shutdown.Token, context, selection, input, limits,
            authorization, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        shutdown.Cancel();
        client.Dispose();
        shutdown.Dispose();
    }
}

public sealed class SpeechSynthesisStream : IAsyncEnumerable<PcmFrame>
{
    private readonly HttpClient client;
    private readonly IProviderCredentialSource credentials;
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    private readonly CancellationToken shutdown;
    private readonly ProviderRequestContext context;
    private readonly SpeechSynthesisSelection selection;
    private readonly BoundedSpeechInput input;
    private readonly SpeechSynthesisLimits limits;
    private readonly SpeechDisclosureAuthorization? authorization;
    private readonly CancellationToken callerToken;
    private readonly long startedAt;
    private readonly DateTimeOffset startedUtc;
    private int enumerated;
    public SpeechSynthesisResult? Result { get; private set; }
    public ProviderCapabilities Capabilities { get; }
    public PcmFormat Format => OpenAiSpeechSynthesisCatalog.PcmFormat;

    internal SpeechSynthesisStream(HttpClient client, IProviderCredentialSource credentials, TimeProvider clock,
        EvidenceProvenance provenance, CancellationToken shutdown, ProviderRequestContext context,
        SpeechSynthesisSelection selection, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        SpeechDisclosureAuthorization? authorization, CancellationToken callerToken)
    {
        this.client = client;
        this.credentials = credentials;
        this.clock = clock;
        this.provenance = provenance;
        this.shutdown = shutdown;
        this.context = context;
        this.selection = selection;
        this.input = input;
        this.limits = limits;
        this.authorization = authorization;
        this.callerToken = callerToken;
        startedAt = clock.GetTimestamp();
        startedUtc = clock.GetUtcNow();
        Capabilities = OpenAiSpeechSynthesisCatalog.AttemptCapabilities(selection.ModelAlias, provenance);
    }

    public ProviderEvent StartedEvent => new()
    {
        Version = ContractVersion.Current, Ids = context.Ids, Epoch = context.Epoch, Sequence = 0,
        ProviderId = OpenAiSpeechSynthesisCatalog.ProviderId, Provenance = provenance, Kind = ProviderEventKind.Started
    };

    public IAsyncEnumerator<PcmFrame> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
            throw new InvalidOperationException("A speech synthesis stream can only be enumerated once.");
        return new SpeechEnumerator(this, Enumerate(cancellationToken).GetAsyncEnumerator());
    }

    private sealed class SpeechEnumerator(SpeechSynthesisStream owner, IAsyncEnumerator<PcmFrame> inner) : IAsyncEnumerator<PcmFrame>
    {
        public PcmFrame Current => inner.Current;
        public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            owner.Result ??= new(owner.context, owner.provenance, SpeechSynthesisOutcome.Canceled, 0, null);
        }
    }

    private async IAsyncEnumerable<PcmFrame> Enumerate([EnumeratorCancellation] CancellationToken enumerationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(callerToken, enumerationToken, shutdown);
        using var operation = new SpeechSynthesisOperation(client, credentials, clock, context, selection, input,
            limits, authorization, startedAt, startedUtc, stop.Token, callerToken, enumerationToken, shutdown);
        long samples = 0;
        long sequence = 0;
        try
        {
            while (true)
            {
                var next = await operation.NextAsync().ConfigureAwait(false);
                next = operation.BeforeDelivery(next);
                if (next.Outcome is { } outcome)
                {
                    Result = new(context, provenance, outcome, samples, operation.HttpStatusCode, next.Failure);
                    operation.Dispose();
                    yield break;
                }
                var frame = new PcmFrame(context.Ids, context.Epoch, sequence++, samples, Format, next.Audio.Span);
                samples += frame.SamplesPerChannel;
                yield return frame;
            }
        }
        finally
        {
            stop.Cancel();
            Result ??= new(context, provenance, SpeechSynthesisOutcome.Canceled, samples, operation.HttpStatusCode);
        }
    }

    public override string ToString() => nameof(SpeechSynthesisStream);
}

internal sealed record SpeechStreamStep(ReadOnlyMemory<byte> Audio = default,
    SpeechSynthesisOutcome? Outcome = null, ProviderFailure? Failure = null)
{
    public override string ToString() => nameof(SpeechStreamStep);
}

internal sealed class SpeechSynthesisOperation(
    HttpClient client, IProviderCredentialSource credentials, TimeProvider clock,
    ProviderRequestContext context, SpeechSynthesisSelection selection, BoundedSpeechInput input, SpeechSynthesisLimits limits,
    SpeechDisclosureAuthorization? authorization, long startedAt, DateTimeOffset startedUtc, CancellationToken stop,
    CancellationToken caller, CancellationToken enumerator, CancellationToken shutdown) : IDisposable
{
    private ProviderRequestWindow? window;
    private CancellationTokenSource? progress;
    private CancellationTokenSource? linked;
    private HttpRequestMessage? request;
    private HttpResponseMessage? response;
    private Stream? body;
    private SpeechPcmReader? reader;
    private long? lastFrameAt;
    private bool initialized;
    private bool disposed;
    private CancellationTokenRegistration abortBody;
    private CancellationToken Token => linked?.Token ?? stop;
    private bool IsStopped => stop.IsCancellationRequested || caller.IsCancellationRequested ||
        enumerator.IsCancellationRequested || shutdown.IsCancellationRequested;
    public int? HttpStatusCode { get; private set; }

    public async Task<SpeechStreamStep> NextAsync()
    {
        try
        {
            EnsureActive();
            if (!initialized)
            {
                initialized = true;
                var blocked = await InitializeAsync().ConfigureAwait(false);
                if (blocked is not null) return blocked;
            }
            var bytes = await reader!.ReadFrameAsync(Token).ConfigureAwait(false);
            EnsureActive();
            if (bytes.IsEmpty)
                return new(Outcome: SpeechSynthesisOutcome.Completed);
            lastFrameAt = clock.GetTimestamp();
            ArmProgress();
            EnsureActive();
            return new(Audio: bytes);
        }
        catch (Exception error) when (error is OperationCanceledException or RequestCutoffException or
            SpeechProtocolException or CredentialUnavailableException or HttpRequestException or IOException ||
            (error is ObjectDisposedException && (IsStopped || Token.IsCancellationRequested)))
        {
            return FailureStep(error);
        }
    }

    public SpeechStreamStep BeforeDelivery(SpeechStreamStep step)
    {
        try { EnsureActive(); return step; }
        catch (RequestCutoffException error) { return FailureStep(error); }
        catch (OperationCanceledException error) { return FailureStep(error); }
    }

    private SpeechStreamStep FailureStep(Exception error)
    {
        if (IsStopped)
            return new(Outcome: SpeechSynthesisOutcome.Canceled);
        // Noncooperative awaits may throw transport errors after synchronous cutoffs.
        try { EnsureActive(); }
        catch (RequestCutoffException cutoff) { return Fail(cutoff.Code); }
        catch (OperationCanceledException) { return IsStopped ? new(Outcome: SpeechSynthesisOutcome.Canceled) : Fail(DeadlineCode()); }
        return Fail(error switch
        {
            RequestCutoffException cutoff => cutoff.Code,
            SpeechProtocolException protocol => protocol.Code,
            CredentialUnavailableException => ProviderFailureCode.CredentialUnavailable,
            HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } or
                HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => ProviderFailureCode.ResponseTruncated,
            _ => ProviderFailureCode.Network
        });
    }

    private async Task<SpeechStreamStep?> InitializeAsync()
    {
        if (!OpenAiSpeechSynthesisCatalog.SupportsModel(selection.UpstreamModelId))
            return Fail(ProviderFailureCode.ModelUnsupported);
        if (!OpenAiSpeechSynthesisCatalog.SupportsVoice(selection.Voice))
            return Fail(ProviderFailureCode.VoiceUnsupported);
        if (!OpenAiSpeechSynthesisCatalog.SupportsFormat(selection.OutputFormat))
            return Fail(ProviderFailureCode.SpeechMediaUnsupported);
        var binding = new ProviderCredentialBinding(OpenAiTransport.Origin, ProviderRole.Tts, selection.UpstreamModelId);
        if (authorization is null || !authorization.AllowTextDisclosure || !authorization.AllowPotentialCharges ||
            !authorization.AiGeneratedVoiceDisclosureConfirmed)
            return Fail(ProviderFailureCode.ConsentMissing);
        if (authorization.Binding is null || !OpenAiTransport.IsApprovedOrigin(authorization.Binding.Origin))
            return Fail(ProviderFailureCode.OriginRejected);
        if (authorization.Binding != binding || authorization.Selection != selection || !authorization.Matches(input) ||
            authorization.Ids != context.Ids || authorization.Epoch != context.Epoch || authorization.Limits != limits)
            return Fail(ProviderFailureCode.ConsentMismatch);
        if (authorization.ExpiresAt <= clock.GetUtcNow())
            return Fail(ProviderFailureCode.ConsentExpired);
        if (input.Utf8Bytes > limits.MaxInputBytes)
            return Fail(ProviderFailureCode.InputLimit);
        if (!authorization.TryConsume())
            return Fail(ProviderFailureCode.ConsentConsumed);
        window = new(clock, startedAt, startedUtc, context.Deadline, limits.MaxRequestTime, authorization.ExpiresAt, stop);
        progress = new(Timeout.InfiniteTimeSpan, clock);
        linked = CancellationTokenSource.CreateLinkedTokenSource(window.Token, progress.Token);
        ArmProgress();
        EnsureActive();
        using var credential = await credentials.ResolveAsync(binding, Token).ConfigureAwait(false);
        EnsureActive();
        if (credential is null)
            return Fail(ProviderFailureCode.CredentialUnavailable);
        if (credential.Binding != binding || !OpenAiTransport.IsApprovedOrigin(credential.Binding.Origin))
            return Fail(ProviderFailureCode.CredentialBindingMismatch);

        request = new(HttpMethod.Post, OpenAiSpeechSynthesisCatalog.Endpoint)
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.Authorization = credential.CreateAuthorization();
        EnsureActive();
        request.Content = new SingleSendContent(CreateJson(), EnsureActive);
        EnsureActive();
        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token).ConfigureAwait(false);
        HttpStatusCode = (int)response.StatusCode;
        EnsureActive();
        if (HttpStatusCode is >= 300 and <= 399)
            return Fail(ProviderFailureCode.RedirectRejected);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            ReadOnlyMemory<byte> bytes = ReadOnlyMemory<byte>.Empty;
            // Only the optional body is best-effort; HTTP status/advice and cutoffs remain authoritative.
            try { (bytes, _) = await OpenAiTransport.ReadBoundedAsync(response.Content, limits.MaxErrorBytes, Token).ConfigureAwait(false); }
            catch (HttpRequestException) { }
            catch (IOException) { }
            EnsureActive();
            return Fail(OpenAiResponseParser.Classify(response.StatusCode, bytes), OpenAiTransport.RetryAdvice(response, clock));
        }
        var declared = SpeechResponseHeaders.Validate(response.Content);
        if (declared > limits.MaxSamples * 2)
            return Fail(ProviderFailureCode.OutputAudioLimit);
        body = await response.Content.ReadAsStreamAsync(Token).ConfigureAwait(false);
        abortBody = Token.Register(body.Dispose);
        EnsureActive();
        reader = new(body, limits.MaxSamples * 2, declared, EnsureActive);
        return null;
    }

    private HttpContent CreateJson()
    {
        EnsureActive();
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("model", selection.UpstreamModelId);
            writer.WriteString("voice", selection.Voice);
            writer.WriteString("input", input.Text);
            writer.WriteString("response_format", "pcm");
            writer.WriteString("stream_format", "audio");
            writer.WriteEndObject();
        }
        var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private void EnsureActive()
    {
        // Check original source flags even when their linked cancellation callbacks have not run.
        caller.ThrowIfCancellationRequested();
        enumerator.ThrowIfCancellationRequested();
        shutdown.ThrowIfCancellationRequested();
        stop.ThrowIfCancellationRequested();
        window?.EnsureActive();
        if (clock.GetElapsedTime(startedAt) >= limits.MaxRequestTime ||
            clock.GetElapsedTime(startedAt) >= context.Deadline - startedUtc)
            throw new RequestCutoffException(ProviderFailureCode.DeadlineExceeded);
        if (lastFrameAt is null && clock.GetElapsedTime(startedAt) >= limits.FirstAudioTimeout)
            throw new RequestCutoffException(ProviderFailureCode.FirstAudioTimeout);
        if (lastFrameAt is { } last && clock.GetElapsedTime(last) >= limits.IdleTimeout)
            throw new RequestCutoffException(ProviderFailureCode.IdleTimeout);
        Token.ThrowIfCancellationRequested();
    }

    private ProviderFailureCode DeadlineCode() => window?.DeadlineCanceled == true
        ? ProviderFailureCode.DeadlineExceeded
        : lastFrameAt is null ? ProviderFailureCode.FirstAudioTimeout : ProviderFailureCode.IdleTimeout;

    private void ArmProgress()
    {
        var due = lastFrameAt is { } last ? limits.IdleTimeout - clock.GetElapsedTime(last)
            : limits.FirstAudioTimeout - clock.GetElapsedTime(startedAt);
        progress!.CancelAfter(due > TimeSpan.Zero ? due : TimeSpan.Zero);
    }

    private static SpeechStreamStep Fail(ProviderFailureCode code, TimeSpan? retryAfter = null) => new(
        Outcome: code is ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstAudioTimeout or ProviderFailureCode.IdleTimeout
            ? SpeechSynthesisOutcome.DeadlineExceeded : SpeechSynthesisOutcome.Failed,
        Failure: new(code, retryAfter, Stage.Synthesis));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        abortBody.Dispose();
        body?.Dispose();
        response?.Dispose();
        request?.Dispose();
        linked?.Dispose();
        progress?.Dispose();
        window?.Dispose();
    }
}
