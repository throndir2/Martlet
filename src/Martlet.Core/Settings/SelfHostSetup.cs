using System.Text;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public static class SelfHostSetup
{
    public const string GatewayOllamaAlias = "gateway-ollama";
    public const string GatewayF5Alias = "gateway-f5";
    public const string GatewaySttAlias = "gateway-stt";
    public const string LocalWhisperAlias = "local-whisper-cpp";
    public const string LocalOrigin = "local://windows";
    public const string GatewayRole = "voice";
    public const string RegistryId = "martlet.gateway.inference-routes";
    public const string RegistryVersion = "1.0";
    public const string OllamaRouteId = "martlet.gateway.ollama-chat.v1";
    public const string OllamaPath = "/martlet/v1/inference/ollama-chat";
    /// <summary>The request bound of a host's Ollama routes: the bounded persona, history and words with JSON escaping, one base64
    /// screen image and one base64 recording (30 seconds of mono PCM16 WAV at up to 48 kHz) for a model that hears. A host older
    /// than recordings advertises less (room for the image only), so this PC sends it no recording.</summary>
    public const int OllamaRequestBytes = 5_400_000;
    /// <summary>The most text a host's Ollama routes take in one request (UTF-8): the instructions, every earlier message and the
    /// current message with its notes. About 350,000 tokens, above the largest context a host loads
    /// (<see cref="GenerationSettings.MaximumHostContextTokens"/>), so the context size, not this bound, decides how much of the
    /// conversation goes. A host route that advertises less is older (<see cref="LegacyOllamaInputBytes"/>).</summary>
    public const int OllamaInputBytes = 1_048_576;
    /// <summary>What a host older than <see cref="OllamaInputBytes"/> takes: 16 KiB of text in all, and each text at most that.
    /// This PC sends such a host the notes with the instructions and leaves out the oldest messages that don't fit.</summary>
    public const int LegacyOllamaInputBytes = 16_384;
    /// <summary>The largest context window a host older than <see cref="OllamaInputBytes"/> loads; its gateway refuses more.</summary>
    public const int LegacyOllamaContextTokens = 32_768;
    /// <summary>The deep-thinking host role's route: a second Ollama of its own on the host (Deep thinking's model), with the same
    /// native-chat contract as <see cref="OllamaRouteId"/>, so a host can think things over beside the conversation's model.</summary>
    public const string DeepThinkingRouteId = "martlet.gateway.deep-thinking-chat.v1";
    public const string DeepThinkingPath = "/martlet/v1/inference/deep-thinking-chat";
    /// <summary>The most thinks a host's Deep thinking role runs at once (its Ollama's <c>OLLAMA_NUM_PARALLEL</c>, advertised as
    /// the route's maximum concurrency); each one beyond the first takes another context's worth of graphics memory.</summary>
    public const int DeepThinkingMaximumSlots = 4;
    /// <summary>The most graphics cards of one host that each run a Thinking pool model of their own: card 1 is the
    /// deep-thinking role (<see cref="DeepThinkingRouteId"/>), cards 2 to 4 are the deep-thinking-2 to deep-thinking-4 roles,
    /// each a separate Ollama pinned to its own card (CUDA_VISIBLE_DEVICES) on a route of its own.</summary>
    public const int DeepThinkingMaximumCards = 4;
    private const string DeepThinkingRoutePrefix = "martlet.gateway.deep-thinking-", DeepThinkingRouteSuffix = "-chat.v1";

    /// <summary>The route of the Thinking pool model on a host's card <paramref name="card"/> (1 to
    /// <see cref="DeepThinkingMaximumCards"/>): <see cref="DeepThinkingRouteId"/> for card 1.</summary>
    public static string DeepThinkingRouteIdFor(int card) => card == 1 ? DeepThinkingRouteId
        : card is > 1 and <= DeepThinkingMaximumCards ? $"{DeepThinkingRoutePrefix}{card}{DeepThinkingRouteSuffix}"
        : throw new ArgumentOutOfRangeException(nameof(card));

    /// <summary>The gateway path of card <paramref name="card"/>'s Thinking pool route.</summary>
    public static string DeepThinkingPathFor(int card) => card == 1 ? DeepThinkingPath
        : card is > 1 and <= DeepThinkingMaximumCards ? $"/martlet/v1/inference/deep-thinking-{card}-chat"
        : throw new ArgumentOutOfRangeException(nameof(card));

    /// <summary>The host role kind of card <paramref name="card"/>'s Thinking pool model: deep-thinking, deep-thinking-2...</summary>
    public static string DeepThinkingRoleKind(int card) => card == 1 ? "deep-thinking"
        : card is > 1 and <= DeepThinkingMaximumCards ? $"deep-thinking-{card}" : throw new ArgumentOutOfRangeException(nameof(card));

    /// <summary>The card of a Thinking pool route (1 for <see cref="DeepThinkingRouteId"/>), or null for any other route.</summary>
    public static int? DeepThinkingCard(string? routeId) =>
        routeId is null ? null : Enumerable.Range(1, DeepThinkingMaximumCards).Select(card => (int?)card)
            .FirstOrDefault(card => DeepThinkingRouteIdFor(card!.Value) == routeId);

    /// <summary>The card of a Thinking pool host role kind (1 for deep-thinking), or null for any other kind.</summary>
    public static int? DeepThinkingCardOfRole(string? kind) =>
        kind is null ? null : Enumerable.Range(1, DeepThinkingMaximumCards).Select(card => (int?)card)
            .FirstOrDefault(card => DeepThinkingRoleKind(card!.Value) == kind);

    /// <summary>Whether <paramref name="routeId"/> is one of a host's Thinking pool routes (any card).</summary>
    public static bool IsDeepThinkingRoute(string? routeId) => DeepThinkingCard(routeId) is not null;
    public const string F5RouteId = "martlet.gateway.f5-synthesis.v1";
    public const string SttRouteId = "martlet.gateway.transcription.v1";
    public const string OllamaContractId = "ollama-native-chat-v034-text";
    public const string F5ContractId = "martlet.f5.worker";
    public const string SttContractId = "martlet.transcription-relay";
    public const string F5RightsStatementVersion = "voice-rights-v1";
    /// <summary>The longest one request on a gateway route may run (a background think on the host's Ollama). The gateway's
    /// routes and the saved route snapshot share it, so every route a host advertises can be saved.</summary>
    public const int MaximumGatewayRouteDurationSeconds = 15 * 60;

    /// <summary>A paired Martlet host's gateway route (its credential is that pairing's device credential).</summary>
    public static bool IsGateway(SetupRouteType? routeType) =>
        routeType is SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.GatewayStt;

    /// <summary>The role, provider alias, route ID, path and contract of each gateway route type.</summary>
    public static (SetupRole Role, string Alias, string RouteId, string Path, string ContractId) Gateway(SetupRouteType routeType) =>
        routeType switch
        {
            SetupRouteType.GatewayOllama => (SetupRole.Llm, GatewayOllamaAlias, OllamaRouteId, OllamaPath, OllamaContractId),
            SetupRouteType.GatewayF5 => (SetupRole.Tts, GatewayF5Alias, F5RouteId, "/martlet/v1/inference/f5-synthesis", F5ContractId),
            SetupRouteType.GatewayStt => (SetupRole.Stt, GatewaySttAlias, SttRouteId, "/martlet/v1/inference/transcription", SttContractId),
            _ => throw new ContractException(ErrorCode.InvalidContract, "Choose a named gateway route.")
        };

    public const string Disclosure =
        "Self-hosted routes stay off until you pair and enable them; saving settings starts no hosts, downloads or local processes.";

    internal static void Identifier(string? value, int maximum = 128)
    {
        ContractRules.Require(value is { Length: > 0 } && value.Length <= maximum &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'),
            "Use a bounded ASCII identifier.");
    }

    internal static void Token(string? value, int maximum = 128)
    {
        ContractRules.Require(value is { Length: > 0 } && value.Length <= maximum &&
            !value.Any(char.IsControl), "Use a bounded version or model token.");
        try
        {
            ContractRules.Require(Encoding.UTF8.GetByteCount(value!) <= maximum,
                "Use a bounded version or model token.");
        }
        catch (EncoderFallbackException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "Use valid Unicode.");
        }
    }

    internal static void Sha256(string? value, bool prefixed = false)
    {
        var expected = prefixed ? 71 : 64;
        ContractRules.Require(value is { Length: var length } && length == expected &&
            (!prefixed || value.StartsWith("sha256:", StringComparison.Ordinal)) &&
            value[(prefixed ? 7 : 0)..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "Use an exact lowercase SHA-256 identity.");
    }

    internal static void Utc(DateTimeOffset value) =>
        ContractRules.Require(value.Offset == TimeSpan.Zero, "Use a UTC timestamp.");

    internal static void LocalDirectory(string? value)
    {
        try
        {
            ContractRules.Require(value is { Length: >= 4 and <= 1024 } &&
                Path.IsPathFullyQualified(value) &&
                !value.StartsWith(@"\\", StringComparison.Ordinal) &&
                value == Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)) &&
                value != Path.TrimEndingDirectorySeparator(Path.GetPathRoot(value)!),
                "Select one canonical absolute local package directory.");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ContractException(ErrorCode.InvalidContract,
                "Select one canonical absolute local package directory.");
        }
    }
}
