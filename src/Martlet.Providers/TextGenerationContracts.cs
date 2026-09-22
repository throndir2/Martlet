using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

public sealed record TextModelSelection(string ModelAlias, string UpstreamModelId)
{
    public override string ToString() => nameof(TextModelSelection);
}

public enum TextHistoryRole { User, Assistant }

public sealed class TextHistoryMessage(TextHistoryRole role, string text)
{
    public TextHistoryRole Role { get; } = role;
    [JsonIgnore]
    public string Text { get; } = text;
    public override string ToString() => nameof(TextHistoryMessage);
}

public sealed class BoundedTextInput
{
    public const int HardMaxUtf8Bytes = 24_064;
    public const int HardMaxHistoryMessages = 16;
    [JsonIgnore]
    public string UserText { get; }
    [JsonIgnore]
    public string? Personality { get; }
    [JsonIgnore]
    public IReadOnlyList<TextHistoryMessage> History { get; }
    public int Utf8Bytes { get; }
    // Local admission budget, NOT measured token usage or a price estimate.
    public int InputTokenReservation { get; }

    public BoundedTextInput(string userText, string? personality = null, IEnumerable<TextHistoryMessage>? history = null)
    {
        var messages = new List<TextHistoryMessage>();
        int bytes = Count(userText);
        ContractRules.Require(!string.IsNullOrWhiteSpace(userText), "A nonempty user message is required.");
        if (personality is not null)
            bytes = checked(bytes + Count(personality));
        if (history is not null)
            foreach (var message in history)
            {
                ContractRules.Require(message is not null && messages.Count < HardMaxHistoryMessages,
                    "Text history exceeds its bound.");
                ContractRules.Defined(message!.Role);
                bytes = checked(bytes + Count(message.Text));
                ContractRules.Require(bytes <= HardMaxUtf8Bytes, "Text input exceeds its byte bound.");
                messages.Add(message);
            }
        ContractRules.Require(bytes <= HardMaxUtf8Bytes, "Text input exceeds its byte bound.");
        UserText = userText;
        Personality = personality;
        History = messages.AsReadOnly();
        Utf8Bytes = bytes;
        InputTokenReservation = bytes + 256 * (messages.Count + (personality is null ? 1 : 2));
    }

    private static int Count(string value)
    {
        ContractRules.Text(value, HardMaxUtf8Bytes);
        try { return new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ContractException(ErrorCode.InvalidContract, "Text contains invalid Unicode."); }
    }

    public override string ToString() => nameof(BoundedTextInput);
}

public sealed record TextGenerationLimits : IContract
{
    public int MaxInputBytes { get; init; } = BoundedTextInput.HardMaxUtf8Bytes;
    public int MaxInputTokens { get; init; } = 24_576;
    public int MaxOutputTokens { get; init; } = 256;
    public int MaxContextTokens { get; init; } = 32_768;
    public int MaxEventBytes { get; init; } = 131_072;
    public int MaxStreamBytes { get; init; } = 2_097_152;
    public int MaxEvents { get; init; } = 1024;
    public int MaxTextCharacters { get; init; } = 16_384;
    public TimeSpan FirstDeltaTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxRequestTime { get; init; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        ContractRules.Require(MaxInputBytes is > 0 and <= BoundedTextInput.HardMaxUtf8Bytes &&
            MaxInputTokens is > 0 and <= 24_576 && MaxOutputTokens is >= 16 and <= 4096 &&
            MaxContextTokens is > 0 and <= 32_768 && MaxInputTokens + MaxOutputTokens <= MaxContextTokens &&
            MaxEventBytes is >= 128 and <= ContractRules.MaxJsonBytes &&
            MaxStreamBytes >= MaxEventBytes && MaxStreamBytes <= 4_194_304 &&
            MaxEvents is >= 2 and <= 4094 &&
            MaxTextCharacters is > 0 and <= ContractRules.MaxTextCharacters,
            "Text generation limits are out of range.");
        foreach (var timeout in new[] { FirstDeltaTimeout, IdleTimeout, MaxRequestTime })
            ContractRules.Require(timeout > TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(2),
                "Text generation deadlines are out of range.");
    }
}

// Created only by a caller that obtained permission; never interchangeable with STT consent.
public sealed class TextDisclosureAuthorization(
    ProviderCredentialBinding binding, TextModelSelection model, CorrelationIds ids, long epoch,
    TextGenerationLimits limits, DateTimeOffset expiresAt, bool allowTextDisclosure, bool allowPotentialCharges)
{
    public ProviderCredentialBinding Binding { get; } = binding;
    public TextModelSelection Model { get; } = model;
    public CorrelationIds Ids { get; } = ids;
    public long Epoch { get; } = epoch;
    public TextGenerationLimits Limits { get; } = limits;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public bool AllowTextDisclosure { get; } = allowTextDisclosure;
    public bool AllowPotentialCharges { get; } = allowPotentialCharges;
    private int consumed;
    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    public override string ToString() => nameof(TextDisclosureAuthorization);
}

public enum TextGenerationOutcome { Completed, Refused, Incomplete, OutputTokenLimit, Failed, Canceled, DeadlineExceeded }

public sealed record TextGenerationUsage(long? InputTokens = null, long? OutputTokens = null, long? TotalTokens = null)
{
    public static TextGenerationUsage Unknown { get; } = new();
    public decimal? EstimatedCost => null;
}

public sealed class TextGenerationResult
{
    public ProviderRequestContext Context { get; }
    public EvidenceProvenance Provenance { get; }
    public TextGenerationOutcome Outcome { get; }
    [JsonIgnore]
    public string? RefusalText { get; }
    public TextGenerationUsage Usage { get; }
    public ProviderFailure? Failure { get; }

    internal TextGenerationResult(ProviderRequestContext context, EvidenceProvenance provenance,
        TextGenerationOutcome outcome, TextGenerationUsage? usage = null, ProviderFailure? failure = null, string? refusal = null)
    {
        Context = context;
        Provenance = provenance;
        Outcome = outcome;
        Usage = usage ?? TextGenerationUsage.Unknown;
        Failure = failure;
        RefusalText = refusal;
    }

    public TurnResult ToTurnResult() => new()
    {
        Version = ContractVersion.Current, Ids = Context.Ids, Provenance = Provenance,
        Outcome = Outcome switch
        {
            TextGenerationOutcome.Completed => TurnOutcome.Completed,
            TextGenerationOutcome.Refused => TurnOutcome.Refused,
            TextGenerationOutcome.Canceled => TurnOutcome.Canceled,
            _ => TurnOutcome.Failed
        },
        Error = Failure?.Error
    };

    public override string ToString() => $"{nameof(TextGenerationResult)}: {Outcome}";
}
