using Martlet.Core.Contracts;
using System.Text.Json.Serialization;

namespace Martlet.Providers;

public sealed record TranscriptionLimits : IContract
{
    public const int HardMaxAudioBytes = 8_640_044;
    public static TimeSpan HardMaxDuration => TimeSpan.FromSeconds(90);
    public int MaxAudioBytes { get; init; } = HardMaxAudioBytes;
    public TimeSpan MaxAudioDuration { get; init; } = HardMaxDuration;
    public int MaxResponseBytes { get; init; } = ContractRules.MaxJsonBytes;
    public int MaxTextCharacters { get; init; } = ContractRules.MaxTextCharacters;
    public TimeSpan MaxRequestTime { get; init; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        ContractRules.Require(MaxAudioBytes is >= 46 and <= HardMaxAudioBytes &&
            MaxAudioDuration > TimeSpan.Zero && MaxAudioDuration <= HardMaxDuration &&
            MaxResponseBytes is > 0 and <= ContractRules.MaxJsonBytes &&
            MaxTextCharacters is > 0 and <= ContractRules.MaxTextCharacters &&
            MaxRequestTime > TimeSpan.Zero && MaxRequestTime <= TimeSpan.FromMinutes(2),
            "Transcription limits are out of range.");
    }
}

public sealed record ProviderRequestContext : IContract
{
    public required CorrelationIds Ids { get; init; }
    public required long Epoch { get; init; }
    public required DateTimeOffset Deadline { get; init; }

    public void Validate()
    {
        ContractRules.Require(Ids is not null, "Request correlation is required.");
        Ids!.Validate();
        ContractRules.Require(Epoch is >= 0 and < int.MaxValue, "The request epoch must leave room for local stop.");
    }
}

public sealed record ProviderCredentialBinding(Uri Origin, ProviderRole Role, string UpstreamModelId);

// Constructed ONLY by a calling path that has obtained data/cost permission.
// This library checks the scope, not the identity or authority of that caller.
public sealed class AudioUploadAuthorization(
    ProviderCredentialBinding binding,
    CorrelationIds ids,
    long epoch,
    TranscriptionLimits limits,
    DateTimeOffset expiresAt,
    bool allowAudioUpload,
    bool allowPotentialCharges)
{
    public ProviderCredentialBinding Binding { get; } = binding;
    public CorrelationIds Ids { get; } = ids;
    public long Epoch { get; } = epoch;
    public TranscriptionLimits Limits { get; } = limits;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public bool AllowAudioUpload { get; } = allowAudioUpload;
    public bool AllowPotentialCharges { get; } = allowPotentialCharges;
    private int consumed;

    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    public override string ToString() => nameof(AudioUploadAuthorization);
}

public enum TranscriptionOutcome { Completed, NoSpeech, Failed, Canceled, DeadlineExceeded }
public enum UsageKind { Unknown, Tokens, Duration }

public sealed record TranscriptionUsage(
    UsageKind Kind,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    double? Seconds = null)
{
    public static TranscriptionUsage Unknown { get; } = new(UsageKind.Unknown);
    public decimal? EstimatedCost => null;
}

public sealed class TranscriptionResult
{
    public ProviderRequestContext Context { get; }
    public EvidenceProvenance Provenance { get; }
    public TranscriptionOutcome Outcome { get; }
    [JsonIgnore]
    public string? Text { get; }
    [JsonIgnore]
    public IReadOnlyList<string> Languages { get; }
    public double? Confidence => null;
    public TranscriptionUsage Usage { get; }
    public ProviderFailure? Failure { get; }

    internal TranscriptionResult(ProviderRequestContext context, EvidenceProvenance provenance,
        TranscriptionOutcome outcome, string? text = null, IReadOnlyList<string>? languages = null,
        TranscriptionUsage? usage = null, ProviderFailure? failure = null)
    {
        Context = context;
        Provenance = provenance;
        Outcome = outcome;
        Text = text;
        Languages = languages ?? Array.Empty<string>();
        Usage = usage ?? TranscriptionUsage.Unknown;
        Failure = failure;
    }

    public ProviderEvent ToTerminalEvent() => new()
    {
        Version = ContractVersion.Current,
        Ids = Context.Ids,
        Epoch = Context.Epoch,
        Sequence = 1,
        ProviderId = OpenAiTranscriptionCatalog.ProviderId,
        Provenance = Provenance,
        Kind = Outcome switch
        {
            TranscriptionOutcome.Completed => ProviderEventKind.Completed,
            TranscriptionOutcome.NoSpeech => ProviderEventKind.NoSpeech,
            TranscriptionOutcome.Canceled => ProviderEventKind.Canceled,
            _ => ProviderEventKind.Failed
        },
        Text = Text,
        Error = Failure?.Error
    };

    public override string ToString() => $"{nameof(TranscriptionResult)}: {Outcome}";
}
