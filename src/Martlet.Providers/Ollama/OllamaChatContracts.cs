using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Ollama;

public sealed class OllamaLoopbackOrigin
{
    [JsonIgnore] public string CanonicalOrigin { get; }
    [JsonIgnore] public Uri Endpoint { get; }

    public OllamaLoopbackOrigin(string canonicalOrigin)
    {
        const string prefix = "http://127.0.0.1:";
        ContractRules.Require(canonicalOrigin is { Length: > 0 and <= 26 } &&
            canonicalOrigin.StartsWith(prefix, StringComparison.Ordinal),
            "Select an explicit canonical HTTP IPv4 loopback origin.");
        var portText = canonicalOrigin![prefix.Length..];
        ContractRules.Require(portText.All(char.IsAsciiDigit) &&
            int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            port is >= 1 and <= 65535 && portText == port.ToString(CultureInfo.InvariantCulture),
            "Select an explicit canonical loopback port.");
        CanonicalOrigin = canonicalOrigin;
        Endpoint = new(canonicalOrigin + "/api/chat", UriKind.Absolute);
    }

    public override string ToString() => nameof(OllamaLoopbackOrigin);
}

public sealed partial class OllamaChatModelSelection
{
    public string ModelAlias { get; }
    [JsonIgnore] public string LocalModelName { get; }
    [JsonIgnore] public string RequestModel { get; }

    public OllamaChatModelSelection(string modelAlias, string localModelName)
    {
        ContractRules.Identifier(modelAlias);
        ContractRules.Require(localModelName is { Length: > 0 and <= 242 } && NamePattern().IsMatch(localModelName),
            "Select one explicit local model name and tag without a source suffix.");
        var tag = localModelName![(localModelName.LastIndexOf(':') + 1)..];
        ContractRules.Require(tag is not "local" and not "cloud" && !tag.EndsWith("-cloud", StringComparison.Ordinal),
            "A model tag cannot contain an Ollama source selector.");
        ModelAlias = modelAlias;
        LocalModelName = localModelName;
        RequestModel = localModelName + ":local";
    }

    [GeneratedRegex(@"\A(?:[a-z0-9][a-z0-9_-]{0,79}/)?[a-z0-9][a-z0-9._-]{0,79}:[a-z0-9][a-z0-9._-]{0,79}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
    public override string ToString() => nameof(OllamaChatModelSelection);
}

public sealed class OllamaChatOptions
{
    public double Temperature { get; }
    public OllamaChatOptions(double temperature)
    {
        ContractRules.Require(double.IsFinite(temperature) && temperature is >= 0 and <= 2,
            "Temperature must be a finite number from zero through two.");
        Temperature = temperature;
    }
}

public sealed class OllamaChatAction
{
    public Guid AttemptId { get; } = Guid.NewGuid();
    public string Protocol => OllamaChatAdapter.Protocol;
    public ProviderRequestContext Context { get; }
    [JsonIgnore] public OllamaLoopbackOrigin Origin { get; }
    [JsonIgnore] public OllamaChatModelSelection Selection { get; }
    [JsonIgnore] public BoundedTextInput Input { get; }
    public OllamaChatOptions Options { get; }
    public TextGenerationLimits Limits { get; }
    public DateTimeOffset EffectiveDeadline { get; }
    internal long StartedAt { get; }
    internal DateTimeOffset StartedUtc { get; }

    internal OllamaChatAction(ProviderRequestContext context, OllamaLoopbackOrigin origin,
        OllamaChatModelSelection selection, BoundedTextInput input, OllamaChatOptions options,
        TextGenerationLimits limits, TimeProvider clock)
    {
        Context = context;
        Origin = origin;
        Selection = selection;
        Input = input;
        Options = options;
        Limits = limits;
        StartedAt = clock.GetTimestamp();
        StartedUtc = clock.GetUtcNow();
        var maximum = StartedUtc + limits.MaxRequestTime;
        EffectiveDeadline = context.Deadline < maximum ? context.Deadline : maximum;
    }

    public override string ToString() => nameof(OllamaChatAction);
}

public interface IOllamaChatAuthorizationSource
{
    // Implementations authenticate permission and reserve their own resources; this interface grants neither.
    ValueTask<OllamaChatAuthorization?> AuthorizeAsync(OllamaChatAction action, CancellationToken cancellationToken);
}

public sealed class OllamaChatAuthorization
{
    [JsonIgnore] public OllamaChatAction Action { get; }
    public DateTimeOffset ExpiresAt { get; }
    private readonly IAsyncDisposable dispatchLease;
    private int consumed;

    public OllamaChatAuthorization(OllamaChatAction action, DateTimeOffset expiresAt, IAsyncDisposable dispatchLease)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(dispatchLease);
        Action = action;
        ExpiresAt = expiresAt;
        this.dispatchLease = dispatchLease;
    }

    internal bool TryConsume(out IAsyncDisposable? lease)
    {
        lease = Interlocked.CompareExchange(ref consumed, 1, 0) == 0 ? dispatchLease : null;
        return lease is not null;
    }

    public override string ToString() => nameof(OllamaChatAuthorization);
}

public sealed class OllamaChatAuthorizationUnavailableException()
    : Exception("The trusted Ollama authorization source is unavailable.");

public sealed class OllamaChatCleanupException()
    : Exception("Ollama ownership could not be retired safely. Keep this owner quarantined.");

public sealed record OllamaChatUsage(
    long? PromptEvalCount = null, long? PromptEvalCachedCount = null, long? EvalCount = null,
    long? TotalDurationNanoseconds = null, long? LoadDurationNanoseconds = null,
    long? PromptEvalDurationNanoseconds = null, long? EvalDurationNanoseconds = null);

public sealed class OllamaChatResult
{
    public ProviderRequestContext Context { get; }
    public EvidenceProvenance Provenance { get; }
    public TextGenerationOutcome Outcome { get; }
    public int EmittedTextCharacters { get; }
    public bool HasPartialOutput => EmittedTextCharacters > 0 && Outcome != TextGenerationOutcome.Completed;
    public ProviderFailureCode? FailureCode { get; }
    public MartletError? Error { get; }
    public TimeSpan? RetryAfter { get; }
    public OllamaChatUsage Usage { get; }

    internal OllamaChatResult(OllamaChatAction action, EvidenceProvenance provenance,
        OllamaChatStep step, int emitted, MartletError? cleanupError = null)
    {
        Context = action.Context;
        Provenance = provenance;
        Outcome = step.Outcome ?? TextGenerationOutcome.Failed;
        EmittedTextCharacters = emitted;
        FailureCode = step.Failure;
        Error = cleanupError ?? (step.Failure is { } code ? OllamaChatFailures.Error(code) : null);
        RetryAfter = step.RetryAfter;
        Usage = step.Usage ?? new();
    }

    public TurnResult ToTurnResult() => new()
    {
        Version = ContractVersion.Current, Ids = Context.Ids, Provenance = Provenance,
        Outcome = Outcome == TextGenerationOutcome.Completed ? TurnOutcome.Completed :
            Outcome == TextGenerationOutcome.Canceled ? TurnOutcome.Canceled : TurnOutcome.Failed,
        Error = Error
    };

    public override string ToString() => $"{nameof(OllamaChatResult)}: {Outcome}";
}

internal sealed record OllamaChatStep(string? Text = null, TextGenerationOutcome? Outcome = null,
    ProviderFailureCode? Failure = null, OllamaChatUsage? Usage = null, TimeSpan? RetryAfter = null)
{
    public override string ToString() => nameof(OllamaChatStep);
}

internal sealed class OllamaChatProtocolException(ProviderFailureCode code)
    : Exception("The Ollama response violated the supported wire contract.")
{
    internal ProviderFailureCode Code { get; } = code;
}
