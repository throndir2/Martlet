using Martlet.Core.Contracts;

namespace Martlet.Providers.Ollama;

public sealed class OllamaChatAdapter : IAsyncDisposable
{
    public const string Protocol = "ollama-native-chat-v034-text";
    public const string SourceRevision = "d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f";
    public const string ProviderId = "ollama-chat";
    private readonly object gate = new();
    private readonly HashSet<OllamaChatStream> streams = [];
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    internal HttpClient Client { get; }
    internal IOllamaChatAuthorizationSource Source { get; }
    private bool disposed, quarantined;
    private Task? disposal;
    public bool IsQuarantined { get { lock (gate) return quarantined; } }
    internal bool IsStopping { get { lock (gate) return disposed; } }

    private OllamaChatAdapter(HttpMessageHandler handler, IOllamaChatAuthorizationSource source,
        TimeProvider clock, EvidenceProvenance provenance)
    {
        Source = source;
        this.clock = clock;
        this.provenance = provenance;
        Client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static OllamaChatAdapter Create(IOllamaChatAuthorizationSource source, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(OllamaChatTransport.CreateHandler(), source, clock ?? TimeProvider.System, EvidenceProvenance.Live);
    }

    internal static OllamaChatAdapter CreateForFixture(HttpMessageHandler handler,
        IOllamaChatAuthorizationSource source, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(source);
        return new(handler, source, clock ?? TimeProvider.System, EvidenceProvenance.Fixture);
    }

    public OllamaChatStream Stream(ProviderRequestContext context, OllamaLoopbackOrigin origin,
        OllamaChatModelSelection selection, BoundedTextInput input, OllamaChatOptions options,
        TextGenerationLimits limits, CancellationToken callerToken, CancellationToken operationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        ContractRules.Require(input.Personality is null && input.History.Count == 0,
            "This Ollama adapter accepts only one current user message.", ErrorCode.ProviderCapability);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (quarantined) throw new OllamaChatCleanupException();
            var action = new OllamaChatAction(context, origin, selection, input, options, limits, clock);
            var stream = new OllamaChatStream(this, action, clock, provenance, callerToken, operationToken);
            streams.Add(stream);
            return stream;
        }
    }

    public static ProviderCapabilities Describe(OllamaChatModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Capabilities(selection, EvidenceProvenance.NotRun);
    }

    internal static ProviderCapabilities Capabilities(OllamaChatModelSelection selection, EvidenceProvenance provenance) => new()
    {
        Version = ContractVersion.Current, ProviderId = ProviderId, AdapterVersion = "0.1.0",
        ModelId = selection.ModelAlias, Role = ProviderRole.Llm, Provenance = provenance,
        SttPartials = CapabilitySupport.Unsupported, TtsAudioTransport = CapabilitySupport.Unsupported,
        TtsIncrementalSynthesis = CapabilitySupport.Unsupported,
        LlmTextDeltas = provenance == EvidenceProvenance.NotRun ? CapabilitySupport.Unknown : CapabilitySupport.Supported,
        Cancellation = provenance == EvidenceProvenance.NotRun ? CancellationCapability.Unknown : CancellationCapability.RequestAbort,
        MaxInputBytes = BoundedTextInput.HardMaxUtf8Bytes
    };

    internal void Retire(OllamaChatStream stream, bool failed)
    {
        lock (gate)
        {
            if (failed) quarantined = true;
            else streams.Remove(stream);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        OllamaChatStream[] captured;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            captured = streams.ToArray();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }
        _ = DisposeOwnedAsync(captured, completion);
        return new(disposal);
    }

    private async Task DisposeOwnedAsync(OllamaChatStream[] captured, TaskCompletionSource completion)
    {
        try
        {
            await Task.WhenAll(captured.Select(stream => stream.DisposeAsync().AsTask())).ConfigureAwait(false);
            Client.Dispose();
            completion.SetResult();
        }
        catch (Exception)
        {
            lock (gate) quarantined = true;
            completion.SetException(new OllamaChatCleanupException());
        }
    }
}
