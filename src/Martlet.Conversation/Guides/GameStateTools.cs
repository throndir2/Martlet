using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Guides;

/// <summary>game_state, the reply's tool for the game state (docs/CONVERSATION.md, "Where you are in a game"): read gives the full
/// state of the game in front (the board line is short), correct sets or clears one field with the user's word, playthrough
/// starts or switches to a named playthrough. Offered while App guides are on, after the App guides tools, always worded the same
/// so the start of every request stays the same. <see cref="GameProgress"/> does the work.</summary>
public static class GameStateTools
{
    public const string Name = "game_state";
    public const string Read = "read", Correct = "correct", Playthrough = "playthrough";
    public const int MaxGameCharacters = 80, MaxValueCharacters = 300;

    public static readonly string ParametersJson = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Read, Correct, Playthrough) },
            ["game"] = new JsonObject { ["type"] = "string", ["description"] = "The game. Leave it out for the one in front." },
            ["field"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. GameFields.All.Select(f => (JsonNode)f)]) },
            ["value"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "correct: the field's new value in the user's words; empty clears it. playthrough: its name."
            }
        },
        ["required"] = new JsonArray("action"),
        ["additionalProperties"] = false
    }.ToJsonString();

    public static TextToolDefinition Definition { get; } = new(Name,
        "What you track about the user's game: the place, goal, what they do, their character (name, class, level), build, " +
        "equipment, recent events and progress, kept for each playthrough. read returns all of it (your notes show only a short " +
        "line). correct sets one field to what the user tells you, or clears it with an empty value; the user's word wins over " +
        "what the screen showed. playthrough starts or switches to a named playthrough when the user starts the game again or " +
        "plays another save.", ParametersJson);

    /// <summary>The arguments: the action, the game (null: the one in front), the field and the value.</summary>
    public static (string? Action, string? Game, string? Field, string? Value, string? Problem) Parse(string argumentsJson)
    {
        JsonObject? arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { arguments = null; }
        var action = Text(arguments, "action", 20)?.ToLowerInvariant();
        if (arguments is null || action is not (Read or Correct or Playthrough))
            return (null, null, null, null, "Pass one JSON object like {\"action\": \"read\"}, {\"action\": \"correct\", \"field\": " +
                "\"place\", \"value\": \"Whiterun\"} or {\"action\": \"playthrough\", \"value\": \"mage run\"}.");
        var game = Text(arguments, "game", MaxGameCharacters);
        var field = Text(arguments, "field", 40);
        var value = arguments["value"] is JsonValue raw && raw.TryGetValue<string>(out var given) ? given.Trim() : null;
        if (value is { Length: > MaxValueCharacters }) value = value[..MaxValueCharacters];
        if (action == Correct && field is null) return (null, null, null, null, "Say which field to correct: " + string.Join(", ", GameFields.All) + ".");
        if (action == Playthrough && string.IsNullOrWhiteSpace(value)) return (null, null, null, null, "Say the playthrough's name in value.");
        return (action, game, field, value, null);
    }

    /// <summary>The full state as the model reads it: the game, the playthrough, then one line for each known field with who said
    /// it and how old it is.</summary>
    public static string Format(GameState? state, DateTimeOffset now)
    {
        if (state is null) return "Nothing is known about a game yet. When the user tells you, use correct to note it.";
        var text = new StringBuilder("Game: ").Append(state.Game).Append("\nPlaythrough: ").Append(state.Playthrough);
        if (state.Playthroughs.Count > 1) text.Append(" (others: ").Append(string.Join(", ", state.Playthroughs.Where(p => p != state.Playthrough))).Append(')');
        if (state.Fields.Count == 0) text.Append("\nNothing noted yet.");
        foreach (var field in state.Fields)
            text.Append('\n').Append(field.Label).Append(": ").Append(field.Value).Append(" (")
                .Append(field.ByUser ? "the user said" : "seen on screen").Append(field.SeenThisSession ? "" : ", in an earlier session")
                .Append(", ").Append(Ago(now - field.At)).Append(')');
        return text.ToString();
    }

    private static string Ago(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "just now" : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago" :
        age < TimeSpan.FromDays(2) ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} days ago";

    private static string? Text(JsonObject? arguments, string name, int maximum) =>
        arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim() is { Length: > 0 } trimmed
            ? trimmed.Length <= maximum ? trimmed : trimmed[..maximum] : null;
}
