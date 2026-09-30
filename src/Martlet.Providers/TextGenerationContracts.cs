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

public enum ImageMediaType { Jpeg, Png }

/// <summary>One still image attached to the current user message (a screen glance). Bytes are copied, bounded and
/// checked for a JPEG/PNG signature; they are private content and never appear in <see cref="ToString"/>.</summary>
public sealed class BoundedImage
{
    public const int HardMaxBytes = 1_048_576;
    public const int HardMaxEdge = 2_048;
    // Local admission reservation for one image; providers count images differently (tiles, patches).
    public const int TokenReservation = 1_536;
    private readonly byte[] bytes;

    public BoundedImage(ReadOnlySpan<byte> content, ImageMediaType mediaType, int width, int height)
    {
        ContractRules.Require(content.Length is > 16 and <= HardMaxBytes, "The image exceeds its byte bound.");
        ContractRules.Require(width is > 0 and <= HardMaxEdge && height is > 0 and <= HardMaxEdge, "The image dimensions are out of range.");
        ContractRules.Require(mediaType switch
        {
            ImageMediaType.Jpeg => content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
            ImageMediaType.Png => content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            _ => false
        }, "The image content does not match its media type.");
        bytes = content.ToArray();
        MediaType = mediaType;
        Width = width;
        Height = height;
    }

    public ImageMediaType MediaType { get; }
    public int Width { get; }
    public int Height { get; }
    public int ByteCount => bytes.Length;
    [JsonIgnore]
    public ReadOnlyMemory<byte> Content => bytes;
    public string MimeType => MediaType == ImageMediaType.Jpeg ? "image/jpeg" : "image/png";
    public string ToBase64() => Convert.ToBase64String(bytes);
    public string ToDataUrl() => $"data:{MimeType};base64,{ToBase64()}";
    public override string ToString() => $"{nameof(BoundedImage)} {Width}x{Height} (content omitted)";
}

public sealed class BoundedTextInput
{
    public const int HardMaxUtf8Bytes = 16_384;
    public const int HardMaxHistoryMessages = 16;
    [JsonIgnore]
    public string UserText { get; }
    [JsonIgnore]
    public string? Personality { get; }
    [JsonIgnore]
    public IReadOnlyList<TextHistoryMessage> History { get; }
    /// <summary>Optional image sent with the current user message only; never part of history.</summary>
    [JsonIgnore]
    public BoundedImage? Image { get; }
    public int Utf8Bytes { get; }
    // Local admission budget, NOT measured token usage or a price estimate.
    public int InputTokenReservation { get; }

    public BoundedTextInput(string userText, string? personality = null, IEnumerable<TextHistoryMessage>? history = null,
        BoundedImage? image = null)
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
        Image = image;
        Utf8Bytes = bytes;
        InputTokenReservation = bytes + 256 * (messages.Count + (personality is null ? 1 : 2)) +
            (image is null ? 0 : BoundedImage.TokenReservation);
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
    TextGenerationLimits limits, DateTimeOffset expiresAt, bool allowTextDisclosure, bool allowPotentialCharges,
    bool allowImageDisclosure = false)
{
    /// <summary>Separate permission to send an attached screen image; text permission alone never covers it.</summary>
    public bool AllowImageDisclosure { get; } = allowImageDisclosure;
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
