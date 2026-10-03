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
        CancellationToken cancellationToken = default, GenerationSettings? generation = null)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        generation?.Validate();
        ContractRules.Identifier(model.ModelAlias);
        return new(client, credentials, clock, provenance, shutdown.Token, context, model, input, limits,
            authorization, cancellationToken, generation: generation);
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

// A text reply stream the conversation runtime consumes: a cloud adapter's or a paired Martlet host's.
public interface ITextGenerationStream : IAsyncEnumerable<ProviderEvent>
{
    TextGenerationResult? Result { get; }
    ProviderCapabilities Capabilities { get; }
    /// <summary>How long after the stream was created the provider's response headers arrived; null until then or when the
    /// route doesn't report it. Diagnostics only.</summary>
    TimeSpan? ResponseAfter => null;
    /// <summary>How long after the stream was created the first hidden reasoning (thinking) arrived; null when none did or the
    /// route doesn't report it. Diagnostics only.</summary>
    TimeSpan? FirstReasoningAfter => null;
}

// When a text stream's response and first reasoning arrived, in ticks of elapsed time from the stream's creation (0 = not yet).
internal sealed class TextStreamTimings
{
    private long response, reasoning;
    internal TimeSpan? Response => Read(ref response);
    internal TimeSpan? Reasoning => Read(ref reasoning);
    internal void MarkResponse(TimeSpan elapsed) => Mark(ref response, elapsed);
    internal void MarkReasoning(TimeSpan elapsed) => Mark(ref reasoning, elapsed);
    private static TimeSpan? Read(ref long field) => Interlocked.Read(ref field) is var ticks and > 0 ? TimeSpan.FromTicks(ticks) : null;
    private static void Mark(ref long field, TimeSpan elapsed) => Interlocked.CompareExchange(ref field, Math.Max(1, elapsed.Ticks), 0);
}

// One enumeration owns one attempted disclosure; user-visible content lives in events, not metadata.
public sealed class TextGenerationStream : ITextGenerationStream
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
    private readonly GenerationSettings? generation;
    private readonly CancellationToken callerToken;
    private readonly long startedAt;
    private readonly DateTimeOffset startedUtc;
    private int enumerated;
    private readonly TextStreamTimings timings = new();
    public TextGenerationResult? Result { get; private set; }
    public ProviderCapabilities Capabilities { get; }
    public TimeSpan? ResponseAfter => timings.Response;
    public TimeSpan? FirstReasoningAfter => timings.Reasoning;

    internal TextGenerationStream(HttpClient client, IProviderCredentialSource? credentials, TimeProvider clock,
        EvidenceProvenance provenance, CancellationToken shutdown, ProviderRequestContext context, TextModelSelection model,
        BoundedTextInput input, TextGenerationLimits limits, TextDisclosureAuthorization? authorization, CancellationToken callerToken,
        Uri? chatBaseUri = null, GenerationSettings? generation = null)
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
        this.generation = generation;
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
            limits, authorization, startedAt, startedUtc, stop.Token, callerToken, enumerationToken, shutdown, chatBaseUri, generation,
            timings);
        long sequence = 0;
        try
        {
            yield return Event(ProviderEventKind.Started, sequence++);
            while (true)
            {
                var next = await operation.NextAsync().ConfigureAwait(false);
                if (next.Outcome is { } outcome)
                {
                    Result = new(context, provenance, outcome, next.Usage, next.Failure, next.Refusal, next.ToolCalls);
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
    TextGenerationUsage? Usage = null, ProviderFailure? Failure = null, string? Refusal = null,
    IReadOnlyList<TextToolCall>? ToolCalls = null)
{
    public override string ToString() => nameof(TextStreamStep);
}

internal sealed class TextGenerationOperation(
    HttpClient client, IProviderCredentialSource? credentials, TimeProvider clock,
    ProviderRequestContext context, TextModelSelection model, BoundedTextInput input, TextGenerationLimits limits,
    TextDisclosureAuthorization? authorization, long startedAt, DateTimeOffset startedUtc, CancellationToken stop,
    CancellationToken caller, CancellationToken enumerator, CancellationToken shutdown, Uri? chatBaseUri = null,
    GenerationSettings? generation = null, TextStreamTimings? timings = null) : IDisposable
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
                    if (step.Failure is { } failed) Report(failed.Code);
                }
                // Comments/unknown events/duplicates cannot extend progress deadlines.
                if (chatNormalizer is null || chatNormalizer.MadeProgress)
                    lastEventAt = clock.GetTimestamp();
                if (chatNormalizer is null ? normalizer!.HasContentDelta :
                    chatNormalizer.HasContentDelta || chatNormalizer.HasReasoningDelta)
                    firstDelta = true;
                if (chatNormalizer is { HasReasoningDelta: true }) timings?.MarkReasoning(clock.GetElapsedTime(startedAt));
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
        if (authorization is null || !authorization.AllowTextDisclosure || !authorization.AllowPotentialCharges ||
            input.Image is not null && !authorization.AllowImageDisclosure ||
            input.Audio is not null && !authorization.AllowAudioDisclosure)
            return Fail(ProviderFailureCode.ConsentMissing);
        // The Responses route's models take no audio; the caller asks again with the transcript only.
        if (chatBaseUri is null && input.Audio is not null)
            return Fail(ProviderFailureCode.RequestRejected);
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
        if (chatBaseUri is not null && string.Equals(chatBaseUri.AbsoluteUri,
            ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, StringComparison.Ordinal))
        {
            // OpenRouter's documented optional app attribution headers.
            request.Headers.TryAddWithoutValidation("HTTP-Referer", ChatCompletionsEndpointCatalog.OpenRouterAppUrl);
            request.Headers.TryAddWithoutValidation("X-Title", ChatCompletionsEndpointCatalog.OpenRouterAppTitle);
        }
        EnsureActive();
        request.Content = new SingleSendContent(CreateJson(), EnsureActive);
        EnsureActive();
        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token).ConfigureAwait(false);
        timings?.MarkResponse(clock.GetElapsedTime(startedAt));
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
            var code = chatBaseUri is not null && response.StatusCode == HttpStatusCode.PaymentRequired
                ? ProviderFailureCode.QuotaExceeded
                : OpenAiResponseParser.Classify(response.StatusCode, bytes);
            return Fail(code == ProviderFailureCode.FormatRejected ? ProviderFailureCode.RequestRejected : code,
                OpenAiTransport.RetryAdvice(response, clock), ProviderDiagnostics.Enabled ? ProviderDiagnostics.Describe(bytes) : null);
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream" ||
            response.Content.Headers.ContentEncoding.Count != 0)
            return Fail(ProviderFailureCode.ResponseSchema, detail: "expected an uncompressed text/event-stream reply");
        if (response.Content.Headers.ContentLength > limits.MaxStreamBytes)
            return Fail(ProviderFailureCode.ResponseTooLarge);
        body = await response.Content.ReadAsStreamAsync(Token).ConfigureAwait(false);
        abortBody = Token.Register(body.Dispose);
        EnsureActive();
        reader = new(body, limits, EnsureActive, response.Content.Headers.ContentLength);
        if (chatBaseUri is null) normalizer = new(limits, model.UpstreamModelId, input.Tools.Count > 0);
        else chatNormalizer = new(limits, input.Tools.Count > 0, GenerationSupport.SendsReplyBudget(chatBaseUri.AbsoluteUri, generation));
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
            if (input.Tools.Count == 0)
            {
                writer.WriteStartArray("tools");
                writer.WriteEndArray();
                writer.WriteString("tool_choice", "none");
                writer.WriteBoolean("parallel_tool_calls", false);
            }
            else
            {
                writer.WriteStartArray("tools");
                foreach (var tool in input.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    writer.WriteRawValue(tool.ParametersJson);
                    // MCP schemas are not strict-mode schemas; the Responses API defaults to strict.
                    writer.WriteBoolean("strict", false);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteString("tool_choice", input.ToolCallsAllowed ? "auto" : "none");
                writer.WriteBoolean("parallel_tool_calls", true);
            }
            writer.WriteString("truncation", "disabled");
            writer.WriteNumber("max_output_tokens", limits.MaxOutputTokens);
            if (generation?.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
            if (generation?.TopP is { } topP) writer.WriteNumber("top_p", topP);
            writer.WriteStartObject("text");
            writer.WriteStartObject("format");
            writer.WriteString("type", "text");
            writer.WriteEndObject();
            writer.WriteEndObject();
            if (input.Personality is not null)
                writer.WriteString("instructions", input.Personality);
            // Martlet's notes for this message close it, after the user's words, as in Chat Completions: the instructions and
            // the conversation before stay the same from request to request, so OpenAI reuses its prompt cache for them.
            writer.WriteStartArray("input");
            foreach (var message in input.History)
                WriteMessage(writer, message.Role == TextHistoryRole.User ? "user" : "assistant", message.Text);
            if (input.Image is { } image)
            {
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "input_text");
                writer.WriteString("text", input.SentUserText);
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WriteString("type", "input_image");
                writer.WriteString("image_url", image.ToDataUrl());
                writer.WriteString("detail", "auto");
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else WriteMessage(writer, "user", input.SentUserText);
            foreach (var round in input.ToolRounds)
            {
                if (round.Text.Trim().Length > 0) WriteMessage(writer, "assistant", round.Text);
                foreach (var call in round.Calls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call");
                    writer.WriteString("call_id", call.CallId);
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                }
                foreach (var result in round.Results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call_output");
                    writer.WriteString("call_id", result.CallId);
                    writer.WriteString("output", result.Output);
                    writer.WriteEndObject();
                }
            }
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
            if (GenerationSupport.SendsReplyBudget(chatBaseUri!.AbsoluteUri, generation))
                writer.WriteNumber("max_tokens", limits.MaxOutputTokens);
            if (generation is { } sampling)
            {
                if (sampling.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
                if (sampling.TopP is { } topP) writer.WriteNumber("top_p", topP);
                if (sampling.FrequencyPenalty is { } frequency) writer.WriteNumber("frequency_penalty", frequency);
                if (sampling.PresencePenalty is { } presence) writer.WriteNumber("presence_penalty", presence);
                // Not part of the OpenAI schema: only for servers that understand them (OpenRouter, vLLM, LM Studio...).
                if (GenerationSupport.SendsExtendedSamplers(chatBaseUri!.AbsoluteUri))
                {
                    if (sampling.TopK is { } topK) writer.WriteNumber("top_k", topK);
                    if (sampling.MinP is { } minP) writer.WriteNumber("min_p", minP);
                    if (sampling.RepeatPenalty is { } repeat) writer.WriteNumber("repetition_penalty", repeat);
                }
                // Thinking steps: each server family reads its own control (GenerationSupport.ChatReasoning).
                if (sampling.Reasoning is { } reasoning)
                    GenerationSupport.WriteReasoning(writer, GenerationSupport.ChatReasoning(chatBaseUri!.AbsoluteUri), reasoning);
            }
            if (string.Equals(chatBaseUri!.AbsoluteUri, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl,
                StringComparison.Ordinal))
            {
                writer.WriteStartObject("provider");
                writer.WriteBoolean("allow_fallbacks", false);
                writer.WriteEndObject();
            }
            // Ask for the closing usage chunk where the server is known to take it, so the reply can say how much of the input
            // came from the prompt cache.
            if (GenerationSupport.AsksStreamUsage(chatBaseUri!.AbsoluteUri))
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }
            // Martlet's notes for this message close it, after the user's words: the instructions and the conversation
            // before stay the same from request to request, so the server's prompt cache (or Ollama's) can reuse them.
            var userText = input.SentUserText;
            writer.WriteStartArray("messages");
            if (input.Personality is not null) WriteMessage(writer, "system", input.Personality);
            foreach (var message in input.History)
                WriteMessage(writer, message.Role == TextHistoryRole.User ? "user" : "assistant", message.Text);
            if (input.Image is not null || input.Audio is not null)
            {
                // OpenAI-compatible multimodal message; servers without vision or hearing reject it (surfaced as RequestRejected).
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", userText);
                writer.WriteEndObject();
                if (input.Image is { } image)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    writer.WriteString("url", image.ToDataUrl());
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                if (input.Audio is { } audio)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_audio");
                    writer.WriteStartObject("input_audio");
                    writer.WriteString("data", audio.ToBase64());
                    writer.WriteString("format", "wav");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else WriteMessage(writer, "user", userText);
            foreach (var round in input.ToolRounds)
            {
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");
                if (round.Text.Trim().Length > 0) writer.WriteString("content", round.Text);
                else writer.WriteNull("content");
                writer.WriteStartArray("tool_calls");
                foreach (var call in round.Calls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", call.CallId);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                foreach (var result in round.Results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", result.CallId);
                    writer.WriteString("content", result.Output);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
            if (input.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in input.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    writer.WriteRawValue(tool.ParametersJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteString("tool_choice", input.ToolCallsAllowed ? "auto" : "none");
            }
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

    private TextStreamStep Fail(ProviderFailureCode code, TimeSpan? retryAfter = null, string? detail = null)
    {
        Report(code, detail);
        return new(
            Outcome: code is ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout
                ? TextGenerationOutcome.DeadlineExceeded : TextGenerationOutcome.Failed,
            Failure: new(code, retryAfter, Stage.Generation));
    }

    private void Report(ProviderFailureCode code, string? detail = null) =>
        ProviderDiagnostics.Report(chatBaseUri is null ? "OpenAI Responses" : "Chat Completions",
            request?.RequestUri ?? chatBaseUri ?? OpenAiTextGenerationCatalog.Endpoint, model.UpstreamModelId, code, response,
            detail ?? chatNormalizer?.ProviderDetail ?? normalizer?.ProviderDetail);

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
