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
    /// <summary>The bound of the user's message and of each earlier message (characters), and the whole input a paired host's
    /// gateway takes (UTF-8 bytes).</summary>
    public const int HardMaxUtf8Bytes = 16_384;
    /// <summary>The bound of a whole input (instructions, earlier messages and the message, UTF-8 bytes): about two million
    /// tokens, the largest context size Martlet uses.</summary>
    public const int HardMaxInputUtf8Bytes = 8_388_608;
    public const int HardMaxHistoryMessages = 4_096;
    /// <summary>Estimated tokens each message adds for its role and separators in a chat template.</summary>
    public const int MessageTokens = 8;
    public const int HardMaxTools = 128;
    public const int HardMaxToolDefinitionBytes = 98_304;
    public const int HardMaxToolRounds = 8;
    public const int HardMaxToolExchangeBytes = 65_536;
    [JsonIgnore]
    public string UserText { get; }
    [JsonIgnore]
    public string? Personality { get; }
    [JsonIgnore]
    public IReadOnlyList<TextHistoryMessage> History { get; }
    /// <summary>Optional image sent with the current user message only; never part of history.</summary>
    [JsonIgnore]
    public BoundedImage? Image { get; }
    /// <summary>Optional recording of the current user message (a WAV of what they said), for a model that hears; sent with
    /// the transcript, never part of history.</summary>
    [JsonIgnore]
    public BoundedWaveAudio? Audio { get; }
    public const double HardMaxAudioSeconds = 30;
    // Local admission reservation per started second of audio (providers count roughly 25-32 tokens a second).
    public const int AudioTokensPerSecond = 32;
    /// <summary>Functions the model may call. Empty means a plain text request.</summary>
    [JsonIgnore]
    public IReadOnlyList<TextToolDefinition> Tools { get; }
    /// <summary>Earlier tool rounds of this same reply: the model's calls and what each returned.</summary>
    [JsonIgnore]
    public IReadOnlyList<TextToolRound> ToolRounds { get; }
    /// <summary>False on the last allowed round: the tools stay described but the model must answer in text.</summary>
    public bool ToolCallsAllowed { get; }
    /// <summary>The first input of this reply when this one continues it (after tool calls or without tools).</summary>
    [JsonIgnore]
    public BoundedTextInput? Origin { get; }
    public int Utf8Bytes { get; }
    public int ToolUtf8Bytes { get; }
    // Local admission budget, NOT measured token usage or a price estimate.
    public int InputTokenReservation { get; }
    public int ToolTokenReservation { get; }

    public BoundedTextInput(string userText, string? personality = null, IEnumerable<TextHistoryMessage>? history = null,
        BoundedImage? image = null, IEnumerable<TextToolDefinition>? tools = null, BoundedWaveAudio? audio = null)
    {
        ContractRules.Require(audio is null || audio.Duration.TotalSeconds <= HardMaxAudioSeconds,
            "The recording exceeds its duration bound.");
        var messages = new List<TextHistoryMessage>();
        int bytes = Count(userText);
        ContractRules.Require(!string.IsNullOrWhiteSpace(userText), "A nonempty user message is required.");
        if (personality is not null)
            bytes = checked(bytes + Count(personality, HardMaxInputUtf8Bytes));
        if (history is not null)
            foreach (var message in history)
            {
                ContractRules.Require(message is not null && messages.Count < HardMaxHistoryMessages,
                    "Text history exceeds its bound.");
                ContractRules.Defined(message!.Role);
                bytes = checked(bytes + Count(message.Text));
                ContractRules.Require(bytes <= HardMaxInputUtf8Bytes, "Text input exceeds its byte bound.");
                messages.Add(message);
            }
        ContractRules.Require(bytes <= HardMaxInputUtf8Bytes, "Text input exceeds its byte bound.");
        var definitions = tools?.ToArray() ?? [];
        ContractRules.Require(definitions.Length <= HardMaxTools && definitions.All(t => t is not null) &&
            definitions.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == definitions.Length,
            "Tools must have unique names and stay within their bound.");
        var toolBytes = definitions.Sum(t => t.Utf8Bytes);
        ContractRules.Require(toolBytes <= HardMaxToolDefinitionBytes, "Tool descriptions exceed their byte bound.");
        UserText = userText;
        Personality = personality;
        History = messages.AsReadOnly();
        Image = image;
        Audio = audio;
        Tools = Array.AsReadOnly(definitions);
        ToolRounds = [];
        ToolCallsAllowed = definitions.Length > 0;
        Utf8Bytes = bytes;
        ToolUtf8Bytes = toolBytes;
        ToolTokenReservation = ToolReservation(toolBytes, definitions.Length, 0);
        InputTokenReservation = TextReservation(bytes, messages.Count + (personality is null ? 1 : 2)) +
            (image is null ? 0 : BoundedImage.TokenReservation) + AudioReservation(audio) + ToolTokenReservation;
    }

    /// <summary>The estimated tokens of <paramref name="utf8Bytes"/> of text in <paramref name="messages"/> chat messages: a
    /// token per three bytes (English runs about four bytes a token; Chinese, Japanese and Korean about three), plus
    /// <see cref="MessageTokens"/> for each message's role and separators. A local estimate, not a provider's count.</summary>
    public static int TextReservation(long utf8Bytes, int messages) =>
        checked((int)((utf8Bytes + 2) / 3) + MessageTokens * messages);

    /// <summary>Where the earlier messages a request carries start: the first even index of <paramref name="history"/> from which
    /// they fit beside <paramref name="prompt"/> (the same request without earlier messages) within <paramref name="maxBytes"/>,
    /// <paramref name="maxTextTokens"/> (tools excluded), <paramref name="maxTokens"/> and <paramref name="maxMessages"/>, so the
    /// oldest exchanges are left out first. A message that can't be sent ends the history before it. Null when even the request
    /// without earlier messages doesn't fit. One pass over the history, so a long conversation fits as fast as a short one.</summary>
    public static int? HistoryStart(BoundedTextInput prompt, IReadOnlyList<TextHistoryMessage> history, int maxBytes,
        int maxTextTokens, int maxTokens, int maxMessages)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(history);
        ContractRules.Require(prompt.History.Count == 0 && prompt.ToolRounds.Count == 0, "Fit history to a request without any.");
        var promptMessages = prompt.Personality is null ? 1 : 2;
        // The image, recording and tool reservations stay the same whatever history is sent.
        var others = prompt.InputTokenReservation - TextReservation(prompt.Utf8Bytes, promptMessages);
        var count = history.Count;
        var suffix = new long[count + 1];
        var lowest = 0;
        var strict = new UTF8Encoding(false, true);
        for (var index = count - 1; index >= 0; index--)
        {
            var message = history[index];
            var bytes = 0;
            if (message is null || !Enum.IsDefined(message.Role) || !Sendable(message.Text))
                lowest = Math.Max(lowest, index + 1);
            else
            {
                try { bytes = strict.GetByteCount(message.Text); }
                catch (EncoderFallbackException) { lowest = Math.Max(lowest, index + 1); }
            }
            suffix[index] = suffix[index + 1] + bytes;
        }
        for (var start = lowest + (lowest & 1); start <= count; start += 2)
        {
            var sent = count - start;
            if (sent > maxMessages) continue;
            var bytes = prompt.Utf8Bytes + suffix[start];
            if (bytes > maxBytes || bytes > HardMaxInputUtf8Bytes) continue;
            var tokens = (long)TextReservation(bytes, promptMessages + sent) + others;
            if (tokens - prompt.ToolTokenReservation > maxTextTokens || tokens > maxTokens) continue;
            return start;
        }
        return null;
    }

    private static bool Sendable(string? text) =>
        text is { Length: <= HardMaxUtf8Bytes } && !text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');

    private BoundedTextInput(BoundedTextInput origin, IReadOnlyList<TextToolDefinition> tools, IReadOnlyList<TextToolRound> rounds,
        bool callsAllowed, bool keepAudio, bool keepImage = true)
    {
        UserText = origin.UserText;
        Personality = origin.Personality;
        History = origin.History;
        Image = keepImage ? origin.Image : null;
        Audio = keepAudio ? origin.Audio : null;
        Origin = origin;
        Tools = tools;
        ToolRounds = rounds;
        ToolCallsAllowed = callsAllowed && tools.Count > 0;
        Utf8Bytes = origin.Utf8Bytes;
        var exchange = rounds.Sum(r => r.Utf8Bytes);
        ContractRules.Require(rounds.Count <= HardMaxToolRounds && exchange <= HardMaxToolExchangeBytes,
            "Tool calls and results exceed their bound.");
        ToolUtf8Bytes = tools.Sum(t => t.Utf8Bytes) + exchange;
        ToolTokenReservation = ToolReservation(ToolUtf8Bytes, tools.Count, rounds.Sum(r => r.Calls.Count));
        InputTokenReservation = origin.InputTokenReservation - origin.ToolTokenReservation - AudioReservation(origin.Audio) +
            AudioReservation(Audio) - ImageReservation(origin.Image) + ImageReservation(Image) + ToolTokenReservation;
    }

    /// <summary>This reply's input plus the finished tool rounds; <paramref name="callsAllowed"/> false asks for a text answer.</summary>
    public BoundedTextInput WithToolRounds(IReadOnlyList<TextToolRound> rounds, bool callsAllowed)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        var origin = Origin ?? this;
        return new(origin, origin.Tools, rounds.ToArray(), callsAllowed, Audio is not null, Image is not null);
    }

    /// <summary>This reply's input with no tools at all, for a model that rejected them.</summary>
    public BoundedTextInput WithoutTools() => new(Origin ?? this, [], [], false, Audio is not null, Image is not null);

    /// <summary>This input with only the transcript, for a model that rejected the recording or doesn't hear (a fallback).</summary>
    public BoundedTextInput WithoutAudio() =>
        Audio is null ? this : new(Origin ?? this, Tools, ToolRounds, ToolCallsAllowed, false, Image is not null);

    /// <summary>This input without its picture, for a model that rejected the screen picture sent along with the user's words.</summary>
    public BoundedTextInput WithoutImage() =>
        Image is null ? this : new(Origin ?? this, Tools, ToolRounds, ToolCallsAllowed, Audio is not null, false);

    private static int ImageReservation(BoundedImage? image) => image is null ? 0 : BoundedImage.TokenReservation;

    private static int AudioReservation(BoundedWaveAudio? audio) =>
        audio is null ? 0 : (int)Math.Ceiling(audio.Duration.TotalSeconds) * AudioTokensPerSecond + 64;

    /// <summary>Exchange bytes still available to tool rounds after <paramref name="rounds"/>.</summary>
    public static int RemainingToolExchangeBytes(IEnumerable<TextToolRound> rounds) =>
        HardMaxToolExchangeBytes - rounds.Sum(r => r.Utf8Bytes);

    private static int ToolReservation(int bytes, int tools, int calls) =>
        bytes == 0 ? 0 : (bytes + 2) / 3 + 32 * tools + 64 * calls + 64;

    private static int Count(string value, int maximum = HardMaxUtf8Bytes)
    {
        ContractRules.Text(value, maximum);
        try { return new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ContractException(ErrorCode.InvalidContract, "Text contains invalid Unicode."); }
    }

    public override string ToString() => nameof(BoundedTextInput);
}

public sealed record TextGenerationLimits : IContract
{
    /// <summary>The bound of a request's estimated input and context tokens: above the largest context size Martlet uses,
    /// with room for tool descriptions and results.</summary>
    public const int HardMaxContextTokens = 4_194_304;
    /// <summary>The earlier messages a paired host's gateway takes (and the default bound).</summary>
    public const int DefaultMaxHistoryMessages = 16;

    public int MaxInputBytes { get; init; } = BoundedTextInput.HardMaxUtf8Bytes;
    public int MaxInputTokens { get; init; } = 24_576;
    public int MaxOutputTokens { get; init; } = 256;
    public int MaxContextTokens { get; init; } = 32_768;
    public int MaxHistoryMessages { get; init; } = DefaultMaxHistoryMessages;
    public int MaxEventBytes { get; init; } = 131_072;
    public int MaxStreamBytes { get; init; } = 2_097_152;
    public int MaxEvents { get; init; } = 1024;
    public int MaxTextCharacters { get; init; } = 16_384;
    public TimeSpan FirstDeltaTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxRequestTime { get; init; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        ContractRules.Require(MaxInputBytes is > 0 and <= BoundedTextInput.HardMaxInputUtf8Bytes &&
            MaxInputTokens is > 0 and <= HardMaxContextTokens && MaxOutputTokens is >= 16 and <= 4096 &&
            MaxContextTokens is > 0 and <= HardMaxContextTokens && (long)MaxInputTokens + MaxOutputTokens <= MaxContextTokens &&
            MaxHistoryMessages is >= 0 and <= BoundedTextInput.HardMaxHistoryMessages &&
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
    bool allowImageDisclosure = false, bool allowAudioDisclosure = false)
{
    /// <summary>Separate permission to send an attached screen image; text permission alone never covers it.</summary>
    public bool AllowImageDisclosure { get; } = allowImageDisclosure;
    /// <summary>Separate permission to send the user's attached recording; text permission alone never covers it.</summary>
    public bool AllowAudioDisclosure { get; } = allowAudioDisclosure;
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
    /// <summary>Functions the model asked to call before answering (only with a completed outcome).</summary>
    [JsonIgnore]
    public IReadOnlyList<TextToolCall> ToolCalls { get; }

    internal TextGenerationResult(ProviderRequestContext context, EvidenceProvenance provenance,
        TextGenerationOutcome outcome, TextGenerationUsage? usage = null, ProviderFailure? failure = null, string? refusal = null,
        IReadOnlyList<TextToolCall>? toolCalls = null)
    {
        Context = context;
        Provenance = provenance;
        Outcome = outcome;
        Usage = usage ?? TextGenerationUsage.Unknown;
        Failure = failure;
        RefusalText = refusal;
        ToolCalls = outcome == TextGenerationOutcome.Completed ? toolCalls ?? [] : [];
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
