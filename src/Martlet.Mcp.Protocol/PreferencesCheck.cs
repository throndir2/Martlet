using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Activities;
using Martlet.Core.Preferences;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>preferences_list, preferences_add, preferences_update, preferences_remove and preferences_check (docs/CONVERSATION.md,
/// "Your preferences"). The first four read and change user-preferences.json of a data directory only through its owner,
/// <see cref="UserPreferences"/>, as Companion › Memory › Your preferences does; a running Martlet follows the file and its
/// settings sync carries the change to the owner's other computers. The check rehearses the reply's manage_preferences tool
/// (<see cref="PreferenceTools"/>), the note that goes at the end of each request, the request contract that carries it and
/// the sharing between two simulated computers, all with the production code on a disposable folder.</summary>
internal static class PreferencesCheck
{
    private static object Limits => new
    {
        preferences = UserPreferences.MaximumPreferences, textCharacters = UserPreferences.MaximumTextCharacters,
        noteCharacters = UserPreferences.MaximumNoteCharacters
    };

    /// <summary>The list, its status line and, for <paramref name="where"/> (everywhere, games, a game's name), the preferences
    /// that apply there and the note a request then carries.</summary>
    internal static object List(string dataDirectory, string? where)
    {
        var path = UserPreferences.PathOf(dataDirectory);
        var state = !File.Exists(path) ? "none" : UserPreferences.TryLoad(dataDirectory) is null ? "unreadable" : "loaded";
        var owner = new UserPreferences(dataDirectory);
        string? now = null;
        if (where is not null && !ActivityScope.TryParse(where, null, out now, out var problem)) throw new ArgumentException(problem);
        var applying = owner.ApplyingNow(now);
        return new
        {
            state, status = owner.Status(), file = UserPreferences.FileName, sharedKey = UserPreferences.SharedKey,
            scope = SettingScopes.Of(UserPreferences.SharedKey),
            preferences = owner.All.Select(Shaped).ToArray(),
            appliesTo = ActivityScope.Describe(now),
            applying = applying.Select(p => p.Id).ToArray(),
            note = UserPreferences.Note(applying, null),
            limits = Limits
        };
    }

    internal static object Add(string dataDirectory, string? text, string? where)
    {
        if (!ActivityScope.TryParse(where, null, out var activity, out var problem)) throw new ArgumentException(problem);
        return Changed(new UserPreferences(dataDirectory), owner => owner.Add(text, activity, UserPreferences.Assistant), "added");
    }

    internal static object Update(string dataDirectory, string? id, string? text, string? where)
    {
        if (text is null && where is null) throw new ArgumentException("Give text, where or both to change.");
        string? activity = null;
        if (where is not null && !ActivityScope.TryParse(where, null, out activity, out var problem)) throw new ArgumentException(problem);
        return Changed(new UserPreferences(dataDirectory), owner => owner.Update(id, text, where is not null, activity, UserPreferences.Assistant),
            "updated");
    }

    internal static object Remove(string dataDirectory, string? id) =>
        Changed(new UserPreferences(dataDirectory), owner => owner.Remove(id), "removed");

    private static object Changed(UserPreferences owner, Func<UserPreferences, UserPreferenceChange> change, string done)
    {
        var result = change(owner);
        if (result.Problem is { } problem) throw new ArgumentException(problem);
        return new
        {
            outcome = result.Changed ? done : "unchanged", preference = Shaped(result.Preference!), status = owner.Status(),
            followUp = result.Changed
                ? "A running Martlet uses it from its next reply; its settings sync takes it to your other computers."
                : "Nothing needed a change."
        };
    }

    private static object Shaped(UserPreference preference) => new
    {
        id = preference.Id, text = preference.Text, where = preference.Where, activity = preference.Activity,
        source = preference.Source, createdAt = preference.CreatedAt, changedAt = preference.ChangedAt
    };

    /// <summary>Rehearses preferences end to end on a disposable folder with the production code.</summary>
    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-preferences-check-" + Guid.NewGuid().ToString("N"));
        var steps = new List<object>();
        void Step(string name, bool passed, object? detail = null) => steps.Add(new { step = name, passed, detail });
        var dataA = Path.Combine(root, "computer-a");
        var dataB = Path.Combine(root, "computer-b");
        try
        {
            var owner = new UserPreferences(dataA);
            var changes = 0;
            owner.Changed += () => changes++;
            const string Elden = "gaming/ELDEN RING";

            Step("the tool is offered the same way every time", PreferenceTools.Definition.Name == "manage_preferences" &&
                ReferenceEquals(PreferenceTools.Definition, PreferenceTools.Definition) &&
                PreferenceTools.Definition.Description == PreferenceTools.Description,
                new { tool = PreferenceTools.Name, bytes = PreferenceTools.Definition.Utf8Bytes });

            Step("no preferences: nothing goes with a request", owner.Note(Elden, null) is null && owner.Status() == "No preferences yet.");

            var add = PreferenceTools.Run(owner, """{"action":"add","text":"Stop giving hints unless I ask","where":"this game"}""", Elden);
            var hint = owner.All.SingleOrDefault();
            Step("\"stop giving hints\" while playing: the reply's tool saves it for this game",
                !add.Result.IsError && add.Changed && hint is { Activity: Elden, Source: UserPreferences.Said } && changes == 1,
                new { outcome = add.Logged, where = hint?.Where });

            var swear = PreferenceTools.Run(owner, """{"action":"add","text":"Don't swear"}""", Elden);
            var note = owner.Note(Elden, null);
            var elsewhere = owner.Note(null, null);
            var otherGame = owner.Note("gaming/Palworld", null);
            Step("the note carries what applies now, everywhere first and the game's last",
                !swear.Result.IsError && note is not null && note.Contains("- Don't swear", StringComparison.Ordinal) &&
                note.IndexOf("Don't swear", StringComparison.Ordinal) < note.IndexOf("Stop giving hints", StringComparison.Ordinal) &&
                elsewhere is not null && !elsewhere.Contains("hints", StringComparison.Ordinal) &&
                otherGame is not null && !otherGame.Contains("hints", StringComparison.Ordinal),
                new { inGame = owner.ApplyingNow(Elden).Count, elsewhere = owner.ApplyingNow(null).Count, otherGame = owner.ApplyingNow("gaming/Palworld").Count });

            var again = PreferenceTools.Run(owner, """{"action":"add","text":"stop giving hints unless I ask","where":"Elden Ring"}""", null);
            Step("the same rule for the same game is kept once", !again.Changed && owner.All.Count == 2, new { outcome = again.Logged });

            var refused = new[]
            {
                PreferenceTools.Run(owner, "not json", Elden), PreferenceTools.Run(owner, """{"action":"add"}""", Elden),
                PreferenceTools.Run(owner, """{"action":"add","text":"x","where":"this game"}""", null),
                PreferenceTools.Run(owner, """{"action":"remove","id":"p000000"}""", Elden),
                PreferenceTools.Run(owner, """{"action":"forget"}""", Elden)
            };
            Step("bad calls are refused with a reason and change nothing", refused.All(r => r.Result.IsError && !r.Changed) && owner.All.Count == 2,
                refused.Select(r => r.Result.Output).ToArray());

            var update = PreferenceTools.Run(owner, $$"""{"action":"update","id":"{{hint!.Id}}","text":"Only give hints when I ask for one"}""", Elden);
            Step("update changes the rule and keeps where it applies",
                update.Changed && owner.Find(hint.Id) is { Text: "Only give hints when I ask for one", Activity: Elden });

            var list = PreferenceTools.Run(owner, """{"action":"list"}""", Elden);
            Step("list gives every preference with its id", !list.Result.IsError && list.Result.Output.Contains(hint.Id, StringComparison.Ordinal) &&
                list.Result.Output.Contains("\"applies_now\":true", StringComparison.Ordinal));

            // The note goes at the end of the request, after the user's words, and the request's start stays the same.
            var before = new BoundedTextInput("Where do I go next?", "You are Martlet (fixture).", [], context: "[MARTLET_NOTES]\n" + note + "\n[/MARTLET_NOTES]");
            var empty = new BoundedTextInput("Where do I go next?", "You are Martlet (fixture).", []);
            Step("a request carries the note at its end and keeps its instructions the same",
                before.Personality == empty.Personality && before.Context is { } context && context.Contains("hints", StringComparison.Ordinal) &&
                before.KeptUserText == empty.KeptUserText,
                new { contextCharacters = before.Context?.Length ?? 0 });

            var remove = PreferenceTools.Run(owner, $$"""{"action":"remove","id":"{{hint.Id}}"}""", Elden);
            Step("\"you can give hints again\": remove takes it away", remove.Changed && owner.Find(hint.Id) is null &&
                owner.Note(Elden, null) is { } after && !after.Contains("hints", StringComparison.Ordinal));

            for (var i = owner.All.Count; i < UserPreferences.MaximumPreferences; i++) owner.Add($"Rule number {i} " + new string('x', 250), null, UserPreferences.Typed);
            var full = owner.Add("One too many", null, UserPreferences.Typed);
            var bounded = owner.Note(null, null)!;
            Step("the list and the note are bounded", full.Problem is not null && owner.All.Count == UserPreferences.MaximumPreferences &&
                bounded.Contains("more not shown", StringComparison.Ordinal),
                new { preferences = owner.All.Count, noteCharacters = bounded.Length });
            foreach (var extra in owner.All.Where(p => p.Text.StartsWith("Rule number", StringComparison.Ordinal)).ToArray()) owner.Remove(extra.Id);
            owner.Add("No spoilers for the story", ActivityScope.Gaming, UserPreferences.Typed);

            // Two computers: A's list reaches B through the shared settings (an account setting), and B's owner follows it.
            var onB = new UserPreferences(dataB);
            var followed = 0;
            var nodeA = new SharedSettingsNode(dataA, "desktop-a", [UserPreferences.Section(dataA)]);
            var nodeB = new SharedSettingsNode(dataB, "desktop-b", [UserPreferences.Section(dataB, () => { followed++; onB.Followed(); })]);
            var now = DateTimeOffset.UtcNow;
            await nodeB.SyncAsync([], true, now, cancellation);
            await nodeA.SyncAsync([nodeB.Document], true, now.AddSeconds(15), cancellation);
            var taken = await nodeB.SyncAsync([nodeA.Document], true, now.AddSeconds(30), cancellation);
            Step("another computer takes the same preferences and obeys them",
                taken.Applied.Any(c => c.Key == UserPreferences.SharedKey) && followed == 1 &&
                UserPreferences.Write(onB.All) == UserPreferences.Write(owner.All) && onB.Note(Elden, null) == owner.Note(Elden, null) &&
                SettingScopes.IsAccountKey(UserPreferences.SharedKey) && SettingsFiles.Find(UserPreferences.FileName) is { Scope: SettingsFileScope.Account },
                new { applied = taken.Applied.Count, scope = SettingScopes.Of(UserPreferences.SharedKey), onB = onB.All.Count });

            var removedOnB = onB.Remove(onB.All.First(p => p.Text == "Don't swear").Id);
            await nodeB.SyncAsync([nodeA.Document], true, now.AddSeconds(45), cancellation);
            await nodeA.SyncAsync([nodeB.Document], true, now.AddSeconds(60), cancellation);
            Step("a preference removed on the other computer goes here too", removedOnB.Changed && owner.All.All(p => p.Text != "Don't swear"),
                new { here = owner.All.Count });

            return new
            {
                passed = steps.All(step => (bool)step.GetType().GetProperty("passed")!.GetValue(step)!),
                steps,
                prompt = PromptCatalog.UserPreferences,
                limits = Limits,
                note = "Synthetic preferences in a disposable folder with the production owner, reply tool, request contract and " +
                    "shared-settings section. No model, network or credentials; it never opens your data folder."
            };
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
