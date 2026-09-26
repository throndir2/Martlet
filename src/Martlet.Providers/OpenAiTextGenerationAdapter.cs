using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

public sealed class OpenAiTextGenerationAdapter : IDisposable
{
    private readonly HttpClient client;
    private readonly IProviderCredentialSource credentials;
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;

    private OpenAiTextGenerationAdapter(HttpMessageHandler handler, IProviderCredentialSource credentials,
        TimeProvider clock, EvidenceProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        this.credentials = credentials;
        this.clock = clock;
        this.provenance = provenance;
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static OpenAiTextGenerationAdapter Create(IProviderCredentialSource credentials, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new(OpenAiTransport.CreateProductionHandler(), credentials, timeProvider ?? TimeProvider.System, EvidenceProvenance.Live);
    }

    internal static OpenAiTextGenerationAdapter CreateForFixture(HttpMessageHandler handler,
        IProviderCredentialSource credentials, TimeProvider? clock = null) =>
        new(handler, credentials, clock ?? TimeProvider.System, EvidenceProvenance.Fixture);

    public TextGenerationStream Stream(ProviderRequestContext context, TextModelSelection model,
        BoundedTextInput input, TextGenerationLimits limits, TextDisclosureAuthorization? authorization,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        ContractRules.Identifier(model.ModelAlias);
        return new(client, credentials, clock, provenance, shutdown.Token, context, model, input, limits,
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

// One enumeration owns one attempted disclosure; user-visible content lives in events, not metadata.
public sealed class TextGenerationStream : IAsyncEnumerable<ProviderEvent>
{
    private readonly HttpClient client;
    private readonly IProviderCredentialSource? credentials;
    private readonly Uri? chatBaseUri;
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    private readonly CancellationToken shutdown;
    private readonly ProviderRequestContext context;
    private readonly TextModelSelection model;
    private readonly BoundedTextInput input;
    private readonly TextGenerationLimits limits;
    private readonly TextDisclosureAuthorization? authorization;
    private readonly CancellationToken callerToken;
    private readonly long startedAt;
    private readonly DateTimeOffset startedUtc;
    private int enumerated;
    public TextGenerationResult? Result { get; private set; }
    public ProviderCapabilities Capabilities { get; }

    internal TextGenerationStream(HttpClient client, IProviderCredentialSource? credentials, TimeProvider clock,
        EvidenceProvenance provenance, CancellationToken shutdown, ProviderRequestContext context, TextModelSelection model,
        BoundedTextInput input, TextGenerationLimits limits, TextDisclosureAuthorization? authorization, CancellationToken callerToken,
        Uri? chatBaseUri = null)
    {
        this.client = client;
        this.credentials = credentials;
        this.chatBaseUri = chatBaseUri;
        this.clock = clock;
        this.provenance = provenance;
        this.shutdown = shutdown;
        this.context = context;
        this.model = model;
        this.input = input;
        this.limits = limits;
        this.authorization = authorization;
        this.callerToken = callerToken;
        startedAt = clock.GetTimestamp();
        startedUtc = clock.GetUtcNow();
        Capabilities = OpenAiTextGenerationCatalog.AttemptCapabilities(model.ModelAlias, provenance);
        if (chatBaseUri is not null)
            Capabilities = Capabilities with { ProviderId = ChatCompletionsSetup.Alias };
    }

    public IAsyncEnumerator<ProviderEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
            throw new InvalidOperationException("A text generation stream can only be enumerated once.");
        return Enumerate(cancellationToken).GetAsyncEnumerator();
    }

    private async IAsyncEnumerable<ProviderEvent> Enumerate([EnumeratorCancellation] CancellationToken enumerationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(callerToken, enumerationToken, shutdown);
        using var operation = new TextGenerationOperation(client, credentials, clock, context, model, input,
            limits, authorization, startedAt, startedUtc, stop.Token, callerToken, enumerationToken, shutdown, chatBaseUri);
        long sequence = 0;
        try
        {
            yield return Event(ProviderEventKind.Started, sequence++);
            while (true)
            {
                var next = await operation.NextAsync().ConfigureAwait(false);
                if (next.Outcome is { } outcome)
                {
                    Result = new(context, provenance, outcome, next.Usage, next.Failure, next.Refusal);
                    operation.Dispose();
                    yield return Event(outcome switch
                    {
                        TextGenerationOutcome.Completed => ProviderEventKind.Completed,
                        TextGenerationOutcome.Refused => ProviderEventKind.Refused,
                        TextGenerationOutcome.Canceled => ProviderEventKind.Canceled,
                        _ => ProviderEventKind.Failed
                    }, sequence, next.Refusal, next.Failure?.Error);
                    yield break;
                }
                yield return Event(ProviderEventKind.TextDelta, sequence++, next.Text);
            }
        }
        finally
        {
            stop.Cancel();
            Result ??= new(context, provenance, TextGenerationOutcome.Canceled);
        }
    }

    private ProviderEvent Event(ProviderEventKind kind, long sequence, string? text = null, MartletError? error = null) => new()
    {
        Version = ContractVersion.Current, Ids = context.Ids, Epoch = context.Epoch, Sequence = sequence,
        ProviderId = Capabilities.ProviderId, Provenance = provenance, Kind = kind, Text = text, Error = error
    };

    public override string ToString() => nameof(TextGenerationStream);
}

internal sealed record TextStreamStep(string? Text = null, TextGenerationOutcome? Outcome = null,
    TextGenerationUsage? Usage = null, ProviderFailure? Failure = null, string? Refusal = null)
{
    public override string ToString() => nameof(TextStreamStep);
}

internal sealed class TextGenerationOperation(
    HttpClient client, IProviderCredentialSource? credentials, TimeProvider clock,
    ProviderRequestContext context, TextModelSelection model, BoundedTextInput input, TextGenerationLimits limits,
    TextDisclosureAuthorization? authorization, long startedAt, DateTimeOffset startedUtc, CancellationToken stop,
    CancellationToken caller, CancellationToken enumerator, CancellationToken shutdown, Uri? chatBaseUri = null) : IDisposable
{
    private ProviderRequestWindow? window;
    private CancellationTokenSource? progress;
    private CancellationTokenSource? linked;
    private HttpRequestMessage? request;
    private HttpResponseMessage? response;
    private Stream? body;
    private ResponsesSseReader? reader;
    private ResponsesTextNormalizer? normalizer;
    private ChatCompletionsTextNormalizer? chatNormalizer;
    private long? lastEventAt;
    private bool firstDelta;
    private bool initialized;
    private bool disposed;
    private CancellationTokenRegistration abortBody;
    private CancellationToken Token => linked?.Token ?? stop;
    private bool IsStopped => stop.IsCancellationRequested || caller.IsCancellationRequested ||
        enumerator.IsCancellationRequested || shutdown.IsCancellationRequested;

    public async Task<TextStreamStep> NextAsync()
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
            while (true)
            {
                EnsureActive();
                var item = await reader!.ReadAsync(Token).ConfigureAwait(false);
                EnsureActive();
                if (item is null)
                    throw new ResponseProtocolException(ProviderFailureCode.ResponseTruncated);
                var step = chatNormalizer is null ? normalizer!.Accept(item) : chatNormalizer.Accept(item);
                if (step?.Outcome is not null)
                {
                    if (chatNormalizer is null)
                        await reader.VerifyDeclaredEndAsync(Token).ConfigureAwait(false);
                    else if (await reader.ReadAsync(Token).ConfigureAwait(false) is not null)
                        throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema);
                }
                // Comments/unknown events/duplicates cannot extend progress deadlines.
                if (chatNormalizer is null || chatNormalizer.MadeProgress)
                    lastEventAt = clock.GetTimestamp();
                if (chatNormalizer?.HasContentDelta ?? normalizer!.HasContentDelta)
                    firstDelta = true;
                ArmProgress();
                EnsureActive();
                if (step is not null)
                    return step;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or RequestCutoffException or
            ResponseProtocolException or CredentialUnavailableException or HttpRequestException or IOException or ContractException ||
            (error is ObjectDisposedException && IsStopped))
        {
            // Re-evaluate synchronous windows even when a noncooperating await threw something else.
            if (IsStopped)
                return new(Outcome: TextGenerationOutcome.Canceled);
            try { EnsureActive(); }
            catch (RequestCutoffException cutoff) { return Fail(cutoff.Code); }
            catch (OperationCanceledException) { return IsStopped ? new(Outcome: TextGenerationOutcome.Canceled) : Fail(DeadlineCode()); }
            return Fail(error switch
            {
                RequestCutoffException cutoff => cutoff.Code,
                ResponseProtocolException protocol => protocol.Code,
                CredentialUnavailableException => ProviderFailureCode.CredentialUnavailable,
                HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } or
                    HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => ProviderFailureCode.ResponseTruncated,
                ContractException => ProviderFailureCode.ResponseSchema,
                _ => ProviderFailureCode.Network
            });
        }
    }

    private async Task<TextStreamStep?> InitializeAsync()
    {
        if (context.Deadline <= startedUtc)
            return Fail(ProviderFailureCode.DeadlineExceeded);
        if (chatBaseUri is null && !OpenAiTextGenerationCatalog.SupportsModel(model.UpstreamModelId))
            return Fail(ProviderFailureCode.ModelUnsupported);
        var binding = new ProviderCredentialBinding(chatBaseUri ?? OpenAiTransport.Origin, ProviderRole.Llm, model.UpstreamModelId);
        if (authorization is null || !authorization.AllowTextDisclosure || !authorization.AllowPotentialCharges)
            return Fail(ProviderFailureCode.ConsentMissing);
        if (authorization.Binding is null || (chatBaseUri is null
            ? !OpenAiTransport.IsApprovedOrigin(authorization.Binding.Origin)
            : !MatchesChatBase(authorization.Binding.Origin)))
            return Fail(ProviderFailureCode.OriginRejected);
        if (authorization.Binding != binding || authorization.Model != model ||
            authorization.Ids != context.Ids || authorization.Epoch != context.Epoch || authorization.Limits != limits)
            return Fail(ProviderFailureCode.ConsentMismatch);
        if (authorization.ExpiresAt <= clock.GetUtcNow())
            return Fail(ProviderFailureCode.ConsentExpired);
        if (input.Utf8Bytes > limits.MaxInputBytes || input.InputTokenReservation > limits.MaxInputTokens)
            return Fail(ProviderFailureCode.InputLimit);
        if (!authorization.TryConsume())
            return Fail(ProviderFailureCode.ConsentConsumed);
        window = new(clock, startedAt, startedUtc, context.Deadline, limits.MaxRequestTime, authorization.ExpiresAt, stop);
        progress = new(Timeout.InfiniteTimeSpan, clock);
        linked = CancellationTokenSource.CreateLinkedTokenSource(window.Token, progress.Token);
        ArmProgress();
        EnsureActive();
        using var credential = credentials is null ? null : await credentials.ResolveAsync(binding, Token).ConfigureAwait(false);
        EnsureActive();
        if (credential is null && (chatBaseUri is null || credentials is not null))
            return Fail(ProviderFailureCode.CredentialUnavailable);
        if (credential is not null && (credential.Binding != binding ||
            (chatBaseUri is null ? !OpenAiTransport.IsApprovedOrigin(credential.Binding.Origin) :
                !MatchesChatBase(credential.Binding.Origin))))
            return Fail(ProviderFailureCode.CredentialBindingMismatch);

        request = new(HttpMethod.Post, chatBaseUri is null ? OpenAiTextGenerationCatalog.Endpoint :
            new Uri(chatBaseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions"))
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = credential?.CreateAuthorization();
        EnsureActive();
        request.Content = new SingleSendContent(CreateJson(), EnsureActive);
        EnsureActive();
        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token).ConfigureAwait(false);
        EnsureActive();
        if ((int)response.StatusCode is >= 300 and <= 399)
            return Fail(ProviderFailureCode.RedirectRejected);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            ReadOnlyMemory<byte> bytes = ReadOnlyMemory<byte>.Empty;
            // Optional diagnostics cannot erase an authoritative HTTP failure.
            try { (bytes, _) = await OpenAiTransport.ReadBoundedAsync(response.Content, limits.MaxEventBytes, Token).ConfigureAwait(false); }
            catch (HttpRequestException) { }
            catch (IOException) { }
            EnsureActive();
            var code = OpenAiResponseParser.Classify(response.StatusCode, bytes);
            return Fail(code == ProviderFailureCode.FormatRejected ? ProviderFailureCode.RequestRejected : code,
                OpenAiTransport.RetryAdvice(response, clock));
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream" ||
            response.Content.Headers.ContentEncoding.Count != 0)
            return Fail(ProviderFailureCode.ResponseSchema);
        if (response.Content.Headers.ContentLength > limits.MaxStreamBytes)
            return Fail(ProviderFailureCode.ResponseTooLarge);
        body = await response.Content.ReadAsStreamAsync(Token).ConfigureAwait(false);
        abortBody = Token.Register(body.Dispose);
        EnsureActive();
        reader = new(body, limits, EnsureActive, response.Content.Headers.ContentLength);
        if (chatBaseUri is null) normalizer = new(limits, model.UpstreamModelId);
        else chatNormalizer = new(limits);
        return null;
    }

    private bool MatchesChatBase(Uri? origin) => origin is { IsAbsoluteUri: true } &&
        string.Equals(origin.AbsoluteUri, chatBaseUri!.AbsoluteUri, StringComparison.Ordinal);

    private HttpContent CreateJson()
    {
        EnsureActive();
        if (chatBaseUri is not null)
            return CreateChatJson();
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model.UpstreamModelId);
            writer.WriteBoolean("stream", true);
            writer.WriteBoolean("store", false);
            writer.WriteBoolean("background", false);
            writer.WriteStartArray("tools");
            writer.WriteEndArray();
            writer.WriteString("tool_choice", "none");
            writer.WriteBoolean("parallel_tool_calls", false);
            writer.WriteString("truncation", "disabled");
            writer.WriteNumber("max_output_tokens", limits.MaxOutputTokens);
            writer.WriteStartObject("text");
            writer.WriteStartObject("format");
            writer.WriteString("type", "text");
            writer.WriteEndObject();
            writer.WriteEndObject();
            if (input.Personality is not null)
                writer.WriteString("instructions", input.Personality);
            writer.WriteStartArray("input");
            foreach (var message in input.History)
                WriteMessage(writer, message.Role == TextHistoryRole.User ? "user" : "assistant", message.Text);
            WriteMessage(writer, "user", input.UserText);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private HttpContent CreateChatJson()
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model.UpstreamModelId);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("max_tokens", limits.MaxOutputTokens);
            writer.WriteStartArray("messages");
            if (input.Personality is not null) WriteMessage(writer, "system", input.Personality);
            foreach (var message in input.History)
                WriteMessage(writer, message.Role == TextHistoryRole.User ? "user" : "assistant", message.Text);
            WriteMessage(writer, "user", input.UserText);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", text);
        writer.WriteEndObject();
    }

    private void EnsureActive()
    {
        // A later blocking callback can delay propagation to linked tokens, not these source flags.
        caller.ThrowIfCancellationRequested();
        enumerator.ThrowIfCancellationRequested();
        shutdown.ThrowIfCancellationRequested();
        stop.ThrowIfCancellationRequested();
        window?.EnsureActive();
        if (clock.GetElapsedTime(startedAt) >= limits.MaxRequestTime ||
            clock.GetElapsedTime(startedAt) >= context.Deadline - startedUtc)
            throw new RequestCutoffException(ProviderFailureCode.DeadlineExceeded);
        if (!firstDelta && clock.GetElapsedTime(startedAt) >= limits.FirstDeltaTimeout)
            throw new RequestCutoffException(ProviderFailureCode.FirstDeltaTimeout);
        if (clock.GetElapsedTime(lastEventAt ?? startedAt) >= limits.IdleTimeout)
            throw new RequestCutoffException(ProviderFailureCode.IdleTimeout);
        Token.ThrowIfCancellationRequested();
    }

    private ProviderFailureCode DeadlineCode() => window?.DeadlineCanceled == true
        ? ProviderFailureCode.DeadlineExceeded
        : !firstDelta && clock.GetElapsedTime(startedAt) >= limits.FirstDeltaTimeout
            ? ProviderFailureCode.FirstDeltaTimeout : ProviderFailureCode.IdleTimeout;

    private void ArmProgress()
    {
        var idle = limits.IdleTimeout - clock.GetElapsedTime(lastEventAt ?? startedAt);
        var first = limits.FirstDeltaTimeout - clock.GetElapsedTime(startedAt);
        var due = !firstDelta && first < idle ? first : idle;
        progress!.CancelAfter(due > TimeSpan.Zero ? due : TimeSpan.Zero);
    }

    private static TextStreamStep Fail(ProviderFailureCode code, TimeSpan? retryAfter = null) => new(
        Outcome: code is ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout
            ? TextGenerationOutcome.DeadlineExceeded : TextGenerationOutcome.Failed,
        Failure: new(code, retryAfter, Stage.Generation));

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
