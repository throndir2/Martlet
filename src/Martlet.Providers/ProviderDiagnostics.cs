using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Providers;

/// <summary>Local diagnostics for failed provider requests: the endpoint, model, HTTP status and the provider's own short
/// explanation (for example "The model ... has reached its end of life"). Failure results and user-facing messages never
/// carry provider text; these lines only go to the sink the app installs (Martlet's local error log). Request content,
/// headers and credentials are never included, and key-like strings in the provider's text are redacted.</summary>
public static partial class ProviderDiagnostics
{
    public const int MaximumDetailCharacters = 400;
    private static Action<string>? sink;

    /// <summary>Routes diagnostic lines to <paramref name="value"/> (null turns them off). A throwing sink is ignored.</summary>
    public static void SetSink(Action<string>? value) => Volatile.Write(ref sink, value);

    internal static bool Enabled => Volatile.Read(ref sink) is not null;

    /// <summary>Records a failure on a route these adapters don't own (for example a paired Martlet host's gateway).</summary>
    public static void Report(string api, ProviderFailureCode code, string? detail) =>
        Report(api, null, null, code, detail: string.IsNullOrWhiteSpace(detail) ? null : Clean(detail, MaximumDetailCharacters));

    internal static void Report(string api, Uri? endpoint, string? model, ProviderFailureCode code,
        HttpResponseMessage? response = null, string? detail = null)
    {
        if (Volatile.Read(ref sink) is not { } write) return;
        try
        {
            var line = new StringBuilder(api).Append(" request failed: ").Append(code);
            if (endpoint is not null) line.Append(". POST ").Append(Endpoint(endpoint));
            if (!string.IsNullOrEmpty(model)) line.Append(" (model ").Append(Clean(model, 256)).Append(')');
            if (response is not null)
            {
                line.Append(" -> HTTP ").Append((int)response.StatusCode);
                if (response.ReasonPhrase is { Length: > 0 } reason) line.Append(' ').Append(Clean(reason, 64));
                if (response.Content.Headers.ContentType?.MediaType is { } media) line.Append(" (").Append(Clean(media, 64)).Append(')');
            }
            if (!string.IsNullOrWhiteSpace(detail)) line.Append(". Provider said: ").Append(detail);
            write(line.ToString());
        }
        // Diagnostics must never change a request's outcome.
        catch (Exception) { }
    }

    /// <summary>The provider's own short explanation from an error body: OpenAI-style <c>error</c> objects, RFC 9457
    /// problem details (NVIDIA), FastAPI validation lists, or plain text. Bounded, single-line and redacted.</summary>
    internal static string? Describe(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty) return null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            if (Describe(document.RootElement) is { } described) return described;
        }
        catch (JsonException) { }
        catch (ArgumentException) { }
        var text = Encoding.UTF8.GetString(body.Span[..Math.Min(body.Length, MaximumDetailCharacters * 4)]);
        return string.IsNullOrWhiteSpace(text) ? null : Clean(text, MaximumDetailCharacters);
    }

    internal static string? Describe(JsonElement root)
    {
        var parts = new List<string>();
        Collect(root, parts);
        return parts.Count == 0 ? null : Clean(string.Join("; ", parts.Distinct(StringComparer.Ordinal)), MaximumDetailCharacters);
    }

    private static void Collect(JsonElement root, List<string> parts)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        // Responses API error events carry their code at the top level.
        if (root.TryGetProperty("code", out var rootCode) && rootCode.ValueKind == JsonValueKind.String)
            Add(parts, "[" + rootCode.GetString() + "]");
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String) Add(parts, error.GetString());
            else if (error.ValueKind == JsonValueKind.Object)
            {
                var labels = new[] { "code", "type", "status" }
                    .Select(name => error.TryGetProperty(name, out var value) ? Scalar(value) : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                if (labels.Length > 0) Add(parts, "[" + string.Join(", ", labels) + "]");
                Add(parts, error.TryGetProperty("message", out var message) ? Scalar(message) : null);
                // OpenRouter puts the upstream provider's own error under error.metadata.raw.
                if (error.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                    metadata.TryGetProperty("raw", out var raw))
                    Add(parts, Scalar(raw));
            }
        }
        foreach (var name in new[] { "title", "message" })
            if (root.TryGetProperty(name, out var value)) Add(parts, Scalar(value));
        if (root.TryGetProperty("detail", out var detail))
        {
            if (detail.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in detail.EnumerateArray().Take(4))
                {
                    if (item.ValueKind != JsonValueKind.Object) { Add(parts, Scalar(item)); continue; }
                    var location = item.TryGetProperty("loc", out var loc) && loc.ValueKind == JsonValueKind.Array
                        ? string.Join(".", loc.EnumerateArray().Select(Scalar)) + ": " : "";
                    Add(parts, location + (item.TryGetProperty("msg", out var msg) ? Scalar(msg) : null));
                }
            }
            else Add(parts, Scalar(detail));
        }
    }

    private static void Add(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim());
    }

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => null
    };

    private static string Endpoint(Uri endpoint) => endpoint.IsAbsoluteUri
        ? endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
        : "(relative endpoint)";

    /// <summary>One bounded line without control characters or key-like strings.</summary>
    internal static string Clean(string text, int maximum)
    {
        var single = Whitespace().Replace(new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), " ").Trim();
        single = Bearer().Replace(single, "$1[redacted]");
        single = KeyLike().Replace(single, "[redacted]");
        single = LongToken().Replace(single, "[redacted]");
        return single.Length <= maximum ? single : single[..maximum] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?i)\b(bearer\s+)\S+")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)\b(?:sk|nvapi|rk|pk|key|api[-_]?key)[-_][A-Za-z0-9_\-*.]{6,}")]
    private static partial Regex KeyLike();

    // Long opaque tokens (hex or base64 secrets); hyphens split them, so hyphenated model names stay readable.
    [GeneratedRegex(@"[A-Za-z0-9_+/=]{40,}")]
    private static partial Regex LongToken();
}
