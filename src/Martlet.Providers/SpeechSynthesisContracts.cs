using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

public enum SpeechOutputFormat { Pcm24KhzMono16Le }

public sealed record SpeechSynthesisSelection(
    [property: JsonIgnore] string ModelAlias, [property: JsonIgnore] string UpstreamModelId,
    [property: JsonIgnore] string Voice, SpeechOutputFormat OutputFormat)
{
    public override string ToString() => nameof(SpeechSynthesisSelection);
}

public sealed class BoundedSpeechInput
{
    public const int HardMaxUtf8Bytes = 1536;
    [JsonIgnore]
    public string Text { get; }
    public int Utf8Bytes { get; }
    // Opaque instance identity, not a text hash or proof of human consent.
    internal Guid Identity { get; } = Guid.NewGuid();

    public BoundedSpeechInput(string text)
    {
        ContractRules.Text(text, HardMaxUtf8Bytes);
        ContractRules.Require(!string.IsNullOrWhiteSpace(text), "A nonempty speech segment is required.");
        try { Utf8Bytes = new UTF8Encoding(false, true).GetByteCount(text); }
        catch (EncoderFallbackException) { throw new ContractException(ErrorCode.InvalidContract, "Speech text contains invalid Unicode."); }
        ContractRules.Require(Utf8Bytes <= HardMaxUtf8Bytes, "Speech input exceeds its byte bound.");
        Text = text;
    }

    public override string ToString() => nameof(BoundedSpeechInput);
}

public sealed record SpeechSynthesisLimits : IContract
{
    public const int HardMaxAudioBytes = 4_320_000;
    public int MaxInputBytes { get; init; } = BoundedSpeechInput.HardMaxUtf8Bytes;
    public int MaxAudioBytes { get; init; } = HardMaxAudioBytes;
    public TimeSpan MaxAudioDuration { get; init; } = TimeSpan.FromSeconds(90);
    public int MaxErrorBytes { get; init; } = 16_384;
    public TimeSpan FirstAudioTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxRequestTime { get; init; } = TimeSpan.FromSeconds(90);

    public long MaxSamples => Math.Min(MaxAudioBytes / 2,
        MaxAudioDuration.Ticks * 24_000 / TimeSpan.TicksPerSecond);

    public void Validate()
    {
        ContractRules.Require(MaxInputBytes is > 0 and <= BoundedSpeechInput.HardMaxUtf8Bytes &&
            MaxAudioBytes is >= 2 and <= HardMaxAudioBytes && MaxAudioBytes % 2 == 0 &&
            MaxAudioDuration > TimeSpan.Zero && MaxAudioDuration <= TimeSpan.FromSeconds(90) &&
            MaxSamples > 0 && MaxErrorBytes is >= 128 and <= 65_536,
            "Speech synthesis limits are out of range.");
        foreach (var timeout in new[] { FirstAudioTimeout, IdleTimeout, MaxRequestTime })
            ContractRules.Require(timeout > TimeSpan.Zero && timeout <= TimeSpan.FromSeconds(90),
                "Speech synthesis deadlines are out of range.");
    }
}

// Construct only after the caller has obtained permission for this exact segment and settings.
public sealed class SpeechDisclosureAuthorization
{
    [JsonIgnore]
    public ProviderCredentialBinding Binding { get; }
    [JsonIgnore]
    public SpeechSynthesisSelection Selection { get; }
    public CorrelationIds Ids { get; }
    public long Epoch { get; }
    public SpeechSynthesisLimits Limits { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool AllowTextDisclosure { get; }
    public bool AllowPotentialCharges { get; }
    public bool AiGeneratedVoiceDisclosureConfirmed { get; }
    private readonly Guid inputIdentity;
    private int consumed;

    public SpeechDisclosureAuthorization(ProviderCredentialBinding binding, SpeechSynthesisSelection selection,
        BoundedSpeechInput input, CorrelationIds ids, long epoch, SpeechSynthesisLimits limits,
        DateTimeOffset expiresAt, bool allowTextDisclosure, bool allowPotentialCharges,
        bool aiGeneratedVoiceDisclosureConfirmed)
    {
        ArgumentNullException.ThrowIfNull(input);
        Binding = binding;
        Selection = selection;
        inputIdentity = input.Identity;
        Ids = ids;
        Epoch = epoch;
        Limits = limits;
        ExpiresAt = expiresAt;
        AllowTextDisclosure = allowTextDisclosure;
        AllowPotentialCharges = allowPotentialCharges;
        AiGeneratedVoiceDisclosureConfirmed = aiGeneratedVoiceDisclosureConfirmed;
    }

    internal bool Matches(BoundedSpeechInput input) => inputIdentity == input.Identity;
    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    public override string ToString() => nameof(SpeechDisclosureAuthorization);
}

public enum SpeechSynthesisOutcome { Completed, Failed, Canceled, DeadlineExceeded }
public enum SpeechProviderTerminal { HttpBodyCompleted }

public sealed class SpeechSynthesisResult
{
    public ProviderRequestContext Context { get; }
    public EvidenceProvenance Provenance { get; }
    public SpeechSynthesisOutcome Outcome { get; }
    public long DeliveredSampleCount { get; }
    public bool IsPartial => DeliveredSampleCount > 0 && Outcome != SpeechSynthesisOutcome.Completed;
    public long? FinalSampleCount => Outcome == SpeechSynthesisOutcome.Completed ? DeliveredSampleCount : null;
    public SpeechProviderTerminal? ProviderTerminal => Outcome == SpeechSynthesisOutcome.Completed
        ? SpeechProviderTerminal.HttpBodyCompleted : null;
    public int? HttpStatusCode { get; }
    public ProviderFailure? Failure { get; }
    public decimal? EstimatedCost => null;
    public long? BilledInputTokens => null;
    public long? BilledOutputTokens => null;

    internal SpeechSynthesisResult(ProviderRequestContext context, EvidenceProvenance provenance,
        SpeechSynthesisOutcome outcome, long deliveredSampleCount, int? httpStatusCode, ProviderFailure? failure = null)
    {
        Context = context;
        Provenance = provenance;
        Outcome = outcome;
        DeliveredSampleCount = deliveredSampleCount;
        HttpStatusCode = httpStatusCode;
        Failure = failure;
    }

    // PCM sequences are independent of the optional Started(0)/terminal(1) control events.
    public ProviderEvent ToTerminalEvent() => new()
    {
        Version = ContractVersion.Current, Ids = Context.Ids, Epoch = Context.Epoch, Sequence = 1,
        ProviderId = OpenAiSpeechSynthesisCatalog.ProviderId, Provenance = Provenance,
        Kind = Outcome switch
        {
            SpeechSynthesisOutcome.Completed => ProviderEventKind.Completed,
            SpeechSynthesisOutcome.Canceled => ProviderEventKind.Canceled,
            _ => ProviderEventKind.Failed
        },
        FinalSampleCount = FinalSampleCount, Error = Failure?.Error
    };

    public override string ToString() => $"{nameof(SpeechSynthesisResult)}: {Outcome}";
}
