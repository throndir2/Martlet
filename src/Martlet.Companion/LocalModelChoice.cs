using Martlet.Providers;

namespace Martlet.Companion;

/// <summary>Settings › Thinking's Find model apps: what looking on this computer found, in words, and which app and model to
/// fill in (the first that lists a model it can use, else the first found).</summary>
internal static class LocalModelChoice
{
    internal static string Describe(IReadOnlyList<LocalModelServer> found) =>
        found.Count == 0
            ? "No model app answers on this computer. Start Ollama, LM Studio or your app's local server, then look again."
            : "Found: " + string.Join("; ", found.Select(s => $"{s.Name} at {s.ChatCompletionsBaseUrl} (" +
                (s.NeedsKey ? "asks for a key" : s.Models.Count == 0 ? "no model loaded" : string.Join(", ", s.Models.Take(4)) +
                    (s.Models.Count > 4 ? $" and {s.Models.Count - 4} more" : "")) + ")")) + ".";

    internal static (string BaseUrl, string? Model)? Pick(IReadOnlyList<LocalModelServer> found, string? typedModel)
    {
        var server = found.FirstOrDefault(s => !s.NeedsKey && s.Models.Count > 0) ?? found.FirstOrDefault();
        if (server is null) return null;
        var typed = typedModel?.Trim();
        return (server.ChatCompletionsBaseUrl,
            typed is { Length: > 0 } && server.Models.Contains(typed, StringComparer.Ordinal) ? typed : server.Models.FirstOrDefault() ?? typed);
    }
}
