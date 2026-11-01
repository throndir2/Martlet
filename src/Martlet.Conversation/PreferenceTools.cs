using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Activities;
using Martlet.Core.Preferences;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>manage_preferences (docs/CONVERSATION.md, "Your preferences"): the reply's tool to keep the user's own rules for how
/// Martlet behaves, such as "stop giving hints". It adds, updates, removes and lists preferences in the one owner,
/// <see cref="UserPreferences"/>. The tool's name, description and schema never change, so the start of every request stays the
/// same.</summary>
public static class PreferenceTools
{
    public const string Name = "manage_preferences";

    public const string Description =
        "The user's standing preferences: their own rules for how you behave from now on, kept on all their computers and " +
        "shown to you at the end of every request. add: when the user tells you to change how you behave for good (\"stop " +
        "giving hints\", \"no spoilers for Elden Ring\", \"don't call me buddy\"); text is the rule in their words, like \"No " +
        "hints unless I ask\"; where is everywhere (the default), games, this game or a game's name. update: change one by id " +
        "(text, where) when they adjust it. remove: one by id when they take it back (\"you can give hints again\"). list: all " +
        "of them with ids. Not for facts about the user. After a change, confirm briefly in your own words.";

    public const string ParametersJson =
        """{"type":"object","properties":{"action":{"type":"string","enum":["list","add","update","remove"]},"text":{"type":"string","description":"add, update: the rule in plain words, like \"No hints unless I ask\"."},"where":{"type":"string","description":"add, update: everywhere, games, this game, or a game's name."},"id":{"type":"string","description":"update, remove: the id from list."}},"required":["action"],"additionalProperties":false}""";

    public static TextToolDefinition Definition { get; } = new(Name, Description, ParametersJson);

    /// <summary>What one call did: the answer for the model, a short outcome for the tool log (never the rule's text) and whether
    /// the list changed.</summary>
    public sealed record Outcome(ConversationToolResult Result, string Logged, bool Changed);

    /// <summary>Runs one manage_preferences call on <paramref name="preferences"/> while the user does <paramref name="now"/>
    /// (the activity "this game" means), for <paramref name="source"/> (<see cref="UserPreferences.Said"/> for the reply).</summary>
    public static Outcome Run(UserPreferences preferences, string? argumentsJson, string? now, string source = UserPreferences.Said)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        JsonObject? arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (JsonException) { arguments = null; }
        if (arguments is null || Text(arguments, "action")?.Trim().ToLowerInvariant() is not { } action)
            return Refused("Pass one JSON object with an action, like {\"action\": \"add\", \"text\": \"No hints unless I ask\"}.");
        switch (action)
        {
            case "list":
            {
                var all = preferences.All;
                var listed = all.Select(p => new
                {
                    id = p.Id, text = p.Text, where = p.Where, applies_now = ActivityScope.Applies(p.Activity, now)
                }).ToArray();
                return new(new(JsonSerializer.Serialize(new { preferences = listed }) +
                    (listed.Length == 0 ? "\nThe user has no preferences yet." : "")), $"listed {listed.Length}", false);
            }
            case "add":
            {
                if (!Where(arguments, now, out var activity, out var problem)) return Refused(problem!);
                var change = preferences.Add(Text(arguments, "text"), activity, source);
                if (change.Problem is { } refused) return Refused(refused);
                var kept = change.Preference!;
                return change.Changed
                    ? new(new($"Saved preference {kept.Id} ({kept.Where}): {kept.Text}. It applies from now on, on all the user's " +
                        "computers. Confirm briefly."), "added", true)
                    : new(new($"The user already has this preference ({kept.Id}, {kept.Where}): {kept.Text}."), "already kept", false);
            }
            case "update":
            {
                var setWhere = Text(arguments, "where") is not null;
                string? activity = null;
                if (setWhere && !Where(arguments, now, out activity, out var problem)) return Refused(problem!);
                var text = Text(arguments, "text");
                if (text is null && !setWhere) return Refused("Pass text, where or both to change, with the id from list.");
                var change = preferences.Update(Text(arguments, "id"), text, setWhere, activity, source);
                if (change.Problem is { } refused) return Refused(refused);
                var kept = change.Preference!;
                return new(new(change.Changed ? $"Changed preference {kept.Id} ({kept.Where}): {kept.Text}. Confirm briefly."
                    : $"Preference {kept.Id} already says that ({kept.Where}): {kept.Text}."), change.Changed ? "updated" : "unchanged", change.Changed);
            }
            case "remove":
            {
                var change = preferences.Remove(Text(arguments, "id"));
                if (change.Problem is { } refused) return Refused(refused);
                return new(new($"Removed preference {change.Preference!.Id}: {change.Preference.Text}. Confirm briefly."), "removed", true);
            }
            default:
                return Refused("action must be list, add, update or remove.");
        }
    }

    private static bool Where(JsonObject arguments, string? now, out string? activity, out string? problem) =>
        ActivityScope.TryParse(Text(arguments, "where"), now, out activity, out problem);

    private static Outcome Refused(string problem) => new(new(problem, true), "refused", false);

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
