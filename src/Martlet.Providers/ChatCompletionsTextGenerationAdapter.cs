using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

public sealed class ChatCompletionsTextGenerationAdapter : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri baseUri;
    private readonly IProviderCredentialSource? credentials;
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;

    private ChatCompletionsTextGenerationAdapter(string baseUrl, HttpMessageHandler handler,
        IProviderCredentialSource? credentials, TimeProvider clock, EvidenceProvenance provenance)
    {
        baseUri = ChatCompletionsSetup.BaseUri(baseUrl);
        this.credentials = credentials;
        this.clock = clock;
        this.provenance = provenance;
        client = new(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static ChatCompletionsTextGenerationAdapter Create(string baseUrl,
        IProviderCredentialSource? credentials = null, TimeProvider? timeProvider = null)
    {
        _ = ChatCompletionsSetup.BaseUri(baseUrl);
        var handler = OpenAiTransport.CreateProductionHandler();
        // Explicit loopback traffic must not disclose text or credentials to a configured proxy.
        handler.UseProxy = false;
        return new(baseUrl, handler, credentials, timeProvider ?? TimeProvider.System, EvidenceProvenance.Live);
    }

    internal static ChatCompletionsTextGenerationAdapter CreateForFixture(string baseUrl,
        HttpMessageHandler handler, IProviderCredentialSource? credentials = null, TimeProvider? clock = null) =>
        new(baseUrl, handler, credentials, clock ?? TimeProvider.System, EvidenceProvenance.Fixture);

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
        ChatCompletionsSetup.ModelId(model.UpstreamModelId);
        return new(client, credentials, clock, provenance, shutdown.Token, context, model, input, limits,
            authorization, cancellationToken, baseUri);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        client.Dispose();
        shutdown.Dispose();
    }
}
