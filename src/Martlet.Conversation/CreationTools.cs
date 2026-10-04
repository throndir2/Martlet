using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Creations;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>list_creations and perform_creation: Martlet finds and performs, shows or activates what it made (docs/CREATIONS.md).
/// They are offered to every tool-capable reply while at least one kind of creation is registered, always the same two in the
/// same order with the same texts (built only from the registered kinds, never from the creations), so the start of every
/// request stays the same for prompt caches. The owner never presses Play: Martlet performs its creations itself.</summary>
public static class CreationTools
{
    public const string ListName = "list_creations";
    public const string PerformName = "perform_creation";
    /// <summary>The most creations one list_creations call returns.</summary>
    public const int MaxListed = 30;
    public const int MaxDescribeCharacters = 240;

    public const string ListParametersJson =
        """{"type":"object","properties":{"kind":{"type":"string","description":"Only this kind."},"query":{"type":"string","description":"Words in the title, summary or text."}},"additionalProperties":false}""";

    public const string PerformParametersJson =
        """{"type":"object","properties":{"id":{"type":"string","description":"The id list_creations gave."},"options":{"type":"object","description":"How to do it; see the kind's options."}},"required":["id"],"additionalProperties":false}""";

    /// <summary>The two tools for <paramref name="kinds"/> (the registered kinds, by name).</summary>
    public static IReadOnlyList<TextToolDefinition> Definitions(IReadOnlyList<CreationKind> kinds) =>
        [new(ListName, ListDescription(kinds), ListParametersJson), new(PerformName, PerformDescription(kinds), PerformParametersJson)];

    public static string ListDescription(IReadOnlyList<CreationKind> kinds) =>
        $"List things you made before ({Kinds(kinds)}), kept on all the user's computers: ids, titles, kinds and short descriptions, newest " +
        "first. Use it when the user asks about or for something you made.";

    public static string PerformDescription(IReadOnlyList<CreationKind> kinds)
    {
        var text = "Perform, show or activate something you made, by its id from list_creations (" +
            string.Join("; ", kinds.Select(k => $"{k.Plural}: you {k.Verb} it")) + "). You do it yourself; the user has no play button. " +
            "It starts at once and returns.";
        foreach (var hint in kinds.Select(k => k.OptionsHint).Where(h => !string.IsNullOrWhiteSpace(h)))
            if (text.Length + hint!.Length + 1 <= TextToolDefinition.MaxDescriptionCharacters) text += " " + hint.Trim();
        return text;
    }

    private static string Kinds(IReadOnlyList<CreationKind> kinds) => kinds.Count == 0 ? "creations" : string.Join(", ", kinds.Select(k => k.Plural));

    /// <summary>list_creations: the live creations matching the call, newest first, as JSON for the model. <paramref name="isHere"/>
    /// says whether a creation's assets are on this computer yet.</summary>
    public static ConversationToolResult List(CreationLibrary library, CreationRegistry registry, string argumentsJson, Func<Creation, bool> isHere)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(registry);
        var arguments = Arguments(argumentsJson);
        if (arguments is null) return new("Pass one JSON object, like {} or {\"kind\": \"song\", \"query\": \"rain\"}.", true);
        var kind = Text(arguments, "kind")?.ToLowerInvariant();
        var query = Text(arguments, "query");
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = library.Live.Where(c => kind is null || string.Equals(c.Kind, kind, StringComparison.Ordinal) ||
                registry.Find(c.Kind) is { } k && (k.Noun == kind || k.Plural == kind))
            .Where(c => words.All(w => Contains(c.Title, w) || Contains(c.Summary, w) || Contains(c.Text, w)))
            .ToArray();
        var listed = matches.Take(MaxListed).Select(c =>
        {
            var known = registry.Find(c.Kind);
            return new
            {
                id = c.Key,
                kind = c.Kind,
                title = c.Title,
                description = Cut(known?.DescribeFor(c) ?? c.Summary ?? c.Kind!, MaxDescribeCharacters),
                length = c.Duration is { } duration ? Duration(duration) : null,
                made = c.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                can_perform = known is not null && registry.HandlerFor(c.Kind) is not null && isHere(c),
                note = known is null ? "This Martlet doesn't know this kind; it may need an update."
                    : registry.HandlerFor(c.Kind) is null ? $"You can't {known.Verb} {known.Plural} here right now."
                    : isHere(c) ? null : "Still copying to this computer."
            };
        }).ToArray();
        var json = JsonSerializer.Serialize(new { count = matches.Length, shown = listed.Length, creations = listed },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        return new(matches.Length == 0
            ? json + "\nNothing matches. Tell the user briefly; don't make one up."
            : json + "\nUse perform_creation with an id to do one. Don't read these lists out unless asked.");
    }

    /// <summary>perform_creation: resolves the id and hands the creation to its kind's handler with the options the model passed.
    /// Every refusal is a clear sentence for the model.</summary>
    public static async ValueTask<ConversationToolResult> PerformAsync(CreationLibrary library, CreationRegistry registry, string argumentsJson,
        Func<Creation, ICreationAssets> assets, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(assets);
        var arguments = Arguments(argumentsJson);
        var id = arguments is null ? null : Text(arguments, "id");
        if (id is null) return new("Pass one JSON object with the id from list_creations, like {\"id\": \"3f2a9c1b7d04\"}.", true);
        var creation = library.Resolve(id);
        if (creation is null)
            return new($"There is no creation with id {Cut(id, 40)}. Call list_creations for the ids; don't guess.", true);
        var kind = registry.Find(creation.Kind);
        if (kind is null)
            return new($"\"{creation.Title}\" is a {creation.Kind}, which this Martlet doesn't know how to perform. Tell the user it may need an update.", true);
        var handler = registry.HandlerFor(kind.Name);
        if (handler is null)
            return new($"You can't {kind.Verb} {kind.Plural} here right now. Tell the user briefly instead of pretending.", true);
        var here = assets(creation);
        if (!here.IsComplete)
            return new($"\"{creation.Title}\" is still copying to this computer from your other ones. Tell the user it'll be ready in a moment.", true);
        JsonElement options;
        if (arguments!["options"] is JsonObject given) options = JsonSerializer.SerializeToElement(given);
        else if (arguments["options"] is null) options = JsonSerializer.SerializeToElement(new { });
        else return new("options must be a JSON object, like {} or the kind's options.", true);
        try
        {
            var result = await handler.PerformAsync(new(creation, options, here), token).ConfigureAwait(false);
            return new(result.Text, result.IsError);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or InvalidOperationException or IOException or
            ArgumentException or JsonException)
        {
            return new($"Couldn't {kind.Verb} \"{creation.Title}\": {error.Message} Tell the user briefly.", true);
        }
    }

    private static JsonObject? Arguments(string argumentsJson)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static bool Contains(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    private static string Cut(string text, int maximum) => text.Length <= maximum ? text : text[..(maximum - 1)].TrimEnd() + "…";

    private static string Duration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}"
        : $"0:{Math.Max(1, (int)Math.Round(duration.TotalSeconds)):00}";
}
