using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Settings;

/// <summary>Where a reply's context size came from.</summary>
public enum ContextSource
{
    /// <summary>The context size saved on Companion › Replies.</summary>
    Saved,
    /// <summary>Martlet's default for a cloud model (<see cref="GenerationSettings.DefaultContextTokens"/>).</summary>
    Default,
    /// <summary>The model's own limit, smaller than the saved size or the default.</summary>
    ModelLimit,
    /// <summary>The window a paired host's Ollama loads when none is saved (<see cref="GenerationSettings.DefaultHostContextTokens"/>).</summary>
    HostDefault,
    /// <summary>The context Ollama on this PC gives the model (its own context length setting).</summary>
    Ollama,
    /// <summary>Ollama on this PC, before Martlet has seen what it gives the model; its smallest default is assumed.</summary>
    OllamaAssumed
}

/// <summary>How much a reply request may hold: the persona, lore, memory, the conversation so far and the message, plus room
/// for the reply. Martlet keeps every request within it, leaving the oldest exchanges out first. The size is the one saved on
/// Companion › Replies, or <see cref="GenerationSettings.DefaultContextTokens"/>, never more than the model's own limit when
/// Martlet knows it (see <see cref="ModelLimits"/>). A paired host's Ollama loads the size it is sent, up to its gateway's
/// <see cref="GenerationSettings.MaximumHostContextTokens"/>. Ollama on this PC can't be sent a size through its OpenAI-compatible
/// endpoint, so its own context length setting is the limit there.</summary>
public sealed record ContextBudget(int Tokens, int ReplyTokens, ContextSource Source, int? ModelTokens)
{
    /// <summary>Ollama's smallest default context (graphics cards under 24 GiB), assumed until Martlet sees what it gives a model.</summary>
    public const int AssumedLocalOllamaTokens = 4_096;

    /// <summary>The least room a request's text always gets, so a persona and message that fit before still fit on a small
    /// context. A server with less context trims the oldest conversation itself.</summary>
    public const int MinimumInputTokens = 6_144;

    /// <summary>The estimated tokens left for the request's text (persona, lore, memory, conversation, message) after the reply's
    /// room; tool descriptions and results have their own room on top.</summary>
    public int InputTokens => Math.Max(Tokens - ReplyTokens, MinimumInputTokens);

    /// <summary>The context of a Thinking route of <paramref name="routeType"/> at <paramref name="chatBaseUrl"/>, from the saved
    /// <paramref name="settings"/> and the model's known limit (<paramref name="modelTokens"/>, null when unknown).</summary>
    public static ContextBudget For(SetupRouteType? routeType, string? chatBaseUrl, GenerationSettings? settings, int? modelTokens)
    {
        var saved = settings?.ContextTokens;
        int tokens;
        ContextSource source;
        int reply;
        if (routeType == SetupRouteType.GatewayOllama)
        {
            tokens = Math.Min(saved ?? GenerationSettings.DefaultHostContextTokens, GenerationSettings.MaximumHostContextTokens);
            source = saved is null ? ContextSource.HostDefault : ContextSource.Saved;
            reply = GenerationSupport.ReplyTokens(routeType, settings);
        }
        else if (IsLocalOllama(routeType, chatBaseUrl))
        {
            var window = modelTokens ?? AssumedLocalOllamaTokens;
            tokens = saved is { } size && size < window ? size : window;
            source = saved is { } smaller && smaller < window ? ContextSource.Saved
                : modelTokens is null ? ContextSource.OllamaAssumed : ContextSource.Ollama;
            // No reply budget is sent unless a max reply length is set; the room kept is the local route's reply reservation.
            reply = settings?.MaxReplyTokens ?? LocalOllamaReplyTokens;
        }
        else
        {
            var wanted = saved ?? GenerationSettings.DefaultContextTokens;
            if (modelTokens is { } limit && limit < wanted)
            {
                tokens = limit;
                source = ContextSource.ModelLimit;
            }
            else
            {
                tokens = wanted;
                source = saved is null ? ContextSource.Default : ContextSource.Saved;
            }
            reply = GenerationSupport.ReplyTokens(routeType, settings);
        }
        return new(tokens, Math.Min(reply, tokens / 2), source, modelTokens);
    }

    /// <summary>The reply reservation of Ollama on this PC when no max reply length is set.</summary>
    public const int LocalOllamaReplyTokens = 4_096;

    public static bool IsLocalOllama(SetupRouteType? routeType, string? chatBaseUrl) =>
        routeType == SetupRouteType.ChatCompletions &&
        string.Equals(chatBaseUrl, GenerationSupport.LocalOllamaChatBaseUrl, StringComparison.Ordinal);

    private static readonly string[] NetworkSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".ts.net"];

    /// <summary>Thinking runs on this PC or another computer on the home network: a paired Martlet host, or a Chat Completions
    /// server at a loopback, private, link-local or Tailscale address or a local host name. It gets local timing, not a cloud's.</summary>
    public static bool IsInNetwork(SetupRouteType? routeType, string? chatBaseUrl)
    {
        if (routeType == SetupRouteType.GatewayOllama) return true;
        if (routeType != SetupRouteType.ChatCompletions || !Uri.TryCreate(chatBaseUrl, UriKind.Absolute, out var uri)) return false;
        var host = uri.IdnHost.Trim('[', ']');
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 ||
                    b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127;
            }
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        }
        return !host.Contains('.') || NetworkSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The context of the saved Thinking route (a legacy route without a type is OpenAI), with what this PC knows about
    /// its model.</summary>
    public static ContextBudget For(SetupRoute? thinking, GenerationSettings? settings, ModelLimits? limits) =>
        For(thinking?.RouteType, thinking?.Origin, settings, thinking is null ? null : ModelContextCatalog.Known(thinking, limits));

    /// <summary>The size and where it came from, in words: "100,000 tokens (Martlet's default)".</summary>
    public string Describe() => $"{Tokens:N0} tokens ({Source switch
    {
        ContextSource.Saved => "your setting",
        ContextSource.Default => "Martlet's default",
        ContextSource.ModelLimit => "the model's limit",
        ContextSource.HostDefault => "the host's default",
        ContextSource.Ollama => "Ollama's context length setting",
        _ => "Ollama's smallest default, until Martlet checks the model"
    }})";
}

/// <summary>Context windows Martlet knows without asking: the models of the named OpenAI route (OpenAI documents them; its model
/// list doesn't say), then whatever was found on this PC (<see cref="ModelLimits"/>).</summary>
public static class ModelContextCatalog
{
    /// <summary>gpt-4.1 and gpt-4.1-mini hold 1,047,576 tokens (OpenAI model pages, checked 2026-10-02).</summary>
    public const int Gpt41ContextTokens = 1_047_576;

    public static int? Catalog(SetupRouteType? routeType, string? modelId) =>
        (routeType is null or SetupRouteType.OpenAi) && modelId is not null &&
        modelId.StartsWith("gpt-4.1", StringComparison.Ordinal) ? Gpt41ContextTokens : null;

    /// <summary>The context the route's model takes per request: what Martlet found on this PC, otherwise the catalog.</summary>
    public static int? Known(SetupRoute route, ModelLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(route);
        return limits?.Find(route.Origin, route.ModelId)?.ContextTokens ?? Catalog(route.RouteType, route.ModelId);
    }
}

/// <summary>One model's context as Martlet found it: what the server takes per request (<see cref="ContextTokens"/>; for Ollama
/// on this PC, the context it gives the model) and the model's own maximum when that differs. Never a key or a message.</summary>
public sealed record ModelLimit
{
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ModelMaximum { get; init; }
    /// <summary>Where it came from, in words: "OpenRouter's model list", "Ollama on this PC".</summary>
    public required string Source { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
}

/// <summary>The context windows Martlet found for Thinking models on this PC (model-limits.json in the data directory): kept per
/// PC rather than in settings, because Ollama's context depends on this PC, and so that a check never changes the settings a
/// conversation runs on. A conversation uses it from its next start (or reload).</summary>
public sealed record ModelLimits
{
    public const string FileName = "model-limits.json";
    public const int MaximumModels = 32;
    public const int MaximumContextTokens = 100_000_000;

    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<ModelLimit> Models { get; init; } = [];

    public ModelLimit? Find(string? origin, string? modelId) =>
        Models.FirstOrDefault(m => string.Equals(m.Origin, origin, StringComparison.Ordinal) &&
            string.Equals(m.ModelId, modelId, StringComparison.Ordinal));

    /// <summary>This list with <paramref name="limit"/> first, replacing any earlier entry for the same model.</summary>
    public ModelLimits With(ModelLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return this with
        {
            Models = [limit, .. Models.Where(m => m.Origin != limit.Origin || m.ModelId != limit.ModelId).Take(MaximumModels - 1)]
        };
    }

    private static bool Valid(ModelLimit? m) => m is { Origin.Length: > 0 and <= 2048, ModelId.Length: > 0 and <= 256, Source.Length: <= 200 } &&
        m.ContextTokens is null or > 0 and <= MaximumContextTokens && m.ModelMaximum is null or > 0 and <= MaximumContextTokens;

    public static ModelLimits Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<ModelLimits>(File.ReadAllText(path));
            return loaded is { SchemaVersion: 1 } ? new() { Models = [.. (loaded.Models ?? []).Where(Valid).Take(MaximumModels)] } : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"model-limits.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this with { Models = [.. Models.Where(Valid).Take(MaximumModels)] },
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
