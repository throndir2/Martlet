using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>settings.json for MCP clients (settings_get, settings_schema and settings_set): read any part, see what each part
/// may hold, and change the parts that are the owner's own choices (personalities, replies, prompts and memory). Every change
/// goes through the production settings contract and store (validation, revision checks, the write lock and the atomic
/// write), and a running Martlet follows it by itself. Where Martlet thinks, listens and speaks, the Thinking fallback, the
/// audio devices and the profile identity hold destinations, consent and credential references, so they change only in
/// Martlet itself.</summary>
internal static class SettingsConfiguration
{
    /// <summary>The top-level parts and what they are, for settings_get and martlet_guide.</summary>
    internal static readonly IReadOnlyList<(string Name, bool Changeable, string About)> Sections =
    [
        ("companion", true, "Personalities (personas: name and instructions) and character profiles (a personality with a look and a " +
            "voice). The character_* tools are the easy way to change them."),
        ("generation", true, "How replies are made (Companion › Replies): temperature, top_p, top_k, min_p, penalties, " +
            "max_reply_tokens, context_tokens, reasoning, reasoning_effort, think_longer, short_first_sentence, adult_content. Absent " +
            "while every value is the model's default."),
        ("prompts", true, "Edits to Martlet's internal prompts (Companion › Prompts). Absent while every prompt is built in. Use " +
            "martlet_call with prompts_status to see each prompt's id and text."),
        ("memory", true, "Memory on or off and where memories are kept (Companion › Memory)."),
        ("setup", false, "Where Martlet thinks, listens and speaks (routes, models, destinations and your consent to them). Change it " +
            "in Martlet: Companion › Thinking, Listening and Voice."),
        ("thinking_fallback", false, "The second Thinking destination used when Thinking fails. Change it in Martlet: Companion › " +
            "Thinking › If Thinking fails."),
        ("audio", false, "The microphone and speakers. Change them in Martlet: Companion › Listening and Voice."),
        ("profile", false, "This settings file's identity and credential references. Never changed."),
        ("schema_version", false, "The settings file's version. Never changed.")
    ];

    private static readonly JsonSerializerOptions SchemaOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    internal static async Task<object> GetAsync(string directory, string? path, CancellationToken token)
    {
        var loaded = await new SettingsStore(directory).LoadAsync(token);
        if (loaded.Error is not null) return new { state = "unreadable", problem = loaded.Error.Summary };
        var firstRun = loaded.State == SettingsLoadState.FirstRun;
        var segments = Segments(path);
        // Before the first save, the defaults Martlet starts with (not saved yet).
        var root = Document(loaded.Settings ?? CompanionSettings.Begin(null));
        return new
        {
            state = firstRun ? "first-run" : "loaded",
            note = firstRun ? "Nothing is saved yet; these are the defaults Martlet starts with. The first change saves them." : null,
            dataDirectory = directory,
            revision = loaded.Revision,
            path = Join(segments),
            value = Navigate(root, segments, create: false)?.DeepClone(),
            sections = segments.Count == 0 ? Sections.Select(s => new { name = s.Name, changeable = s.Changeable, about = s.About }).ToArray() : null
        };
    }

    internal static object Schema(string? path, int? depth)
    {
        var segments = Segments(path);
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions, typeof(AppSettings));
        var node = schema;
        foreach (var segment in segments)
            node = Child(node, segment) ?? throw new ArgumentException($"settings.json has no '{Join(segments)}'. Top-level parts: " +
                string.Join(", ", Sections.Select(s => s.Name)) + ".");
        var limit = Math.Clamp(depth ?? (segments.Count == 0 ? 2 : 4), 1, 12);
        return new
        {
            path = Join(segments),
            changeable = segments.Count == 0 || Sections.FirstOrDefault(s => s.Name == segments[0]).Changeable,
            schema = Trim(node.DeepClone(), limit),
            note = "Property names are snake_case, as in settings.json. Deeper parts are cut short to {\"type\": ...}; pass a longer " +
                "path or a larger depth to see them."
        };
    }

    internal static async Task<object> SetAsync(string directory, string path, JsonElement value, string? expectedRevision,
        CancellationToken token)
    {
        var segments = Segments(path);
        if (segments.Count == 0) throw new ArgumentException("Give the path of the part to change, such as generation.temperature.");
        var section = Sections.FirstOrDefault(s => s.Name == segments[0]);
        if (section.Name is null)
            throw new ArgumentException($"settings.json has no '{segments[0]}'. Parts: {string.Join(", ", Sections.Select(s => s.Name))}.");
        if (!section.Changeable) throw new ArgumentException($"'{segments[0]}' isn't changed through MCP. {section.About}");
        var store = new SettingsStore(directory);
        var loaded = await store.LoadAsync(token);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        if (expectedRevision is not null && !string.Equals(expectedRevision, loaded.Revision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Settings changed since you read them. Read them again with settings_get, then change them.");
        var prior = CompanionSettings.Begin(loaded.Settings);
        var root = Document(prior);
        var parent = Navigate(root, segments.Take(segments.Count - 1).ToArray(), create: true)
            ?? throw new ArgumentException($"settings.json has no '{Join(segments.Take(segments.Count - 1).ToArray())}'.");
        var last = segments[^1];
        var replacement = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : JsonNode.Parse(value.GetRawText());
        switch (parent)
        {
            case JsonObject parentObject when replacement is null: parentObject.Remove(last); break;
            case JsonObject parentObject: parentObject[last] = replacement; break;
            case JsonArray array when int.TryParse(last, out var index) && index >= 0 && index < array.Count:
                if (replacement is null) array.RemoveAt(index);
                else array[index] = replacement;
                break;
            case JsonArray array when int.TryParse(last, out var index) && index == array.Count && replacement is not null:
                array.Add(replacement);
                break;
            default: throw new ArgumentException($"'{Join(segments)}' isn't a property or list item in settings.json.");
        }
        PruneAlong(root, segments);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        AppSettings candidate;
        // The serializer's own message names the part that doesn't fit (for example "$.generation.temperature").
        try
        {
            candidate = JsonSerializer.Deserialize<AppSettings>(bytes, SchemaOptions) ?? throw new ArgumentException("settings.json can't be empty.");
        }
        catch (JsonException error) { throw new ArgumentException("Martlet can't use that value: " + error.Message); }
        // Optional parts are saved as absent while everything in them is the default, as Martlet's pages save them.
        candidate = candidate with
        {
            Generation = candidate.Generation is { IsDefault: true } ? null : candidate.Generation,
            Prompts = candidate.Prompts is { IsDefault: true } ? null : candidate.Prompts
        };
        var next = FreshRevisions(prior, candidate);
        var save = await store.SaveAsync(next, loaded.Revision, token);
        if (!save.Saved) throw new InvalidOperationException(save.Error?.Summary ?? "Martlet couldn't save its settings.");
        return new
        {
            saved = true, path = Join(segments), value = Navigate(Document(next), segments, create: false)?.DeepClone(), revision = save.Revision,
            note = "A running Martlet follows this by itself; an open conversation switches before its next reply."
        };
    }

    /// <summary>A persona whose name, text or speech breaks changed, and memory whose choice changed, get a fresh configuration
    /// revision, as the desktop's pages give them.</summary>
    private static AppSettings FreshRevisions(AppSettings prior, AppSettings next)
    {
        if (next.Companion is { } companion && prior.Companion is { } before)
            next = next with
            {
                Companion = companion with
                {
                    Personas = companion.Personas.Select(persona =>
                        before.Personas.SingleOrDefault(p => p.Id == persona.Id) is { } old &&
                        old.ConfigurationRevision == persona.ConfigurationRevision &&
                        (old.Name != persona.Name || old.Text != persona.Text || old.Breaks != persona.Breaks)
                            ? persona with { ConfigurationRevision = Guid.NewGuid() } : persona).ToArray()
                }
            };
        if (next.Memory is { } memory && prior.Memory is { } was && was.ConfigurationRevision == memory.ConfigurationRevision &&
            (was.Enabled != memory.Enabled || was.StoragePolicy != memory.StoragePolicy ||
             !string.Equals(was.CustomDirectory, memory.CustomDirectory, StringComparison.Ordinal)))
            next = next with { Memory = memory with { ConfigurationRevision = Guid.NewGuid() } };
        next.Validate();
        return next;
    }

    private static JsonObject Document(AppSettings settings) =>
        JsonNode.Parse(ContractJson.Write(settings, AppSettings.MaxFileBytes))!.AsObject();

    /// <summary>"generation.temperature", "companion.personas[0].text" or "companion.personas.0.text"; camelCase names are taken
    /// as their snake_case names.</summary>
    private static IReadOnlyList<string> Segments(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() is "." or "$") return [];
        var text = path.Trim();
        if (text.StartsWith("$.", StringComparison.Ordinal)) text = text[2..];
        if (text.Length > 256) throw new ArgumentException("path is too long.");
        var segments = new List<string>();
        foreach (var part in text.Replace("[", ".", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)
                     .Split('.', StringSplitOptions.RemoveEmptyEntries))
            segments.Add(part.Any(char.IsUpper) ? JsonNamingPolicy.SnakeCaseLower.ConvertName(part) : part);
        return segments;
    }

    private static string Join(IReadOnlyList<string> segments) => string.Join('.', segments);

    private static JsonNode? Navigate(JsonNode root, IReadOnlyList<string> segments, bool create)
    {
        JsonNode? node = root;
        foreach (var segment in segments)
        {
            switch (node)
            {
                case JsonObject item:
                    if (item[segment] is null && create) item[segment] = new JsonObject();
                    node = item[segment];
                    break;
                case JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count:
                    node = array[index];
                    break;
                default: return null;
            }
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Drops the objects along the path that were left empty (an optional part with nothing set in it), deepest
    /// first.</summary>
    private static void PruneAlong(JsonObject root, IReadOnlyList<string> segments)
    {
        for (var length = segments.Count - 1; length >= 1; length--)
        {
            var parent = Navigate(root, segments.Take(length - 1).ToArray(), create: false);
            if (parent is JsonObject owner && owner[segments[length - 1]] is JsonObject { Count: 0 }) owner.Remove(segments[length - 1]);
        }
    }

    private static JsonNode? Child(JsonNode node, string segment)
    {
        if (node["properties"]?[segment] is { } property) return property;
        if (int.TryParse(segment, out _) && node["items"] is { } items) return items;
        if (node["additionalProperties"] is JsonObject values) return values;
        return null;
    }

    private static JsonNode? Trim(JsonNode? node, int depth)
    {
        if (node is not JsonObject item) return node;
        if (depth <= 0 && (item["properties"] is not null || item["items"] is JsonObject))
            return new JsonObject { ["type"] = item["type"]?.DeepClone(), ["more"] = "pass a longer path or a larger depth" };
        if (item["properties"] is JsonObject properties)
            foreach (var (key, value) in properties.ToArray())
                properties[key] = Trim(value?.DeepClone(), depth - 1);
        if (item["items"] is JsonObject list) item["items"] = Trim(list.DeepClone(), depth - 1);
        if (item["additionalProperties"] is JsonObject map) item["additionalProperties"] = Trim(map.DeepClone(), depth - 1);
        return item;
    }
}
