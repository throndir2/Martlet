using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Activities;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Preferences;

/// <summary>One of the user's preferences: their own rule for how Martlet behaves, in plain words ("Don't give hints unless I
/// ask", "No spoilers for Elden Ring"), where it applies (<see cref="Activity"/>, the values of <see cref="ActivityScope"/>:
/// null everywhere, "gaming" while gaming, "gaming/&lt;name&gt;" in one game), when it was made and changed, and its source
/// (<see cref="UserPreferences.Said"/>, <see cref="UserPreferences.Typed"/> or <see cref="UserPreferences.Assistant"/>).</summary>
public sealed record UserPreference
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public string? Activity { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }
    public string Source { get; init; } = UserPreferences.Said;

    /// <summary>"everywhere", "while gaming" or "while playing Palworld".</summary>
    [JsonIgnore] public string Where => ActivityScope.Describe(Activity);
}

/// <summary>What one change to the preferences did: the preference it added, changed or removed (null when nothing was found),
/// whether the list changed, and the problem in plain words when it could not be done.</summary>
public sealed record UserPreferenceChange(UserPreference? Preference, bool Changed, string? Problem)
{
    public bool Done => Problem is null;
}

/// <summary>The user's preferences (docs/CONVERSATION.md, "Your preferences"): the one owner of the list of custom rules the user
/// gave Martlet about how it behaves, such as "stop giving hints". It reads and writes user-preferences.json in Martlet's data
/// folder (the account's working copy), which travels to the owner's other computers as the account setting
/// <see cref="SharedKey"/> (<see cref="Section"/>). The reply model changes it through manage_preferences
/// (<c>Martlet.Conversation.PreferenceTools</c>), the owner through Companion › Memory › Your preferences, and MCP through the
/// preferences_* tools. Every reply, screen glance and check-in reads the preferences that apply now through
/// <see cref="ApplyingNow"/> and <see cref="Note"/>. Thread-safe; it reads the file again when another writer (the shared
/// settings sync, MCP) changed it.</summary>
public sealed class UserPreferences
{
    public const string FileName = "user-preferences.json";
    public const string SharedKey = "user-preferences";
    public const int SchemaVersion1 = 1;
    /// <summary>The most preferences kept.</summary>
    public const int MaximumPreferences = 40;
    /// <summary>The longest rule text.</summary>
    public const int MaximumTextCharacters = 300;
    /// <summary>The longest <see cref="Note"/> (its lines without the prompt around them).</summary>
    public const int MaximumNoteCharacters = 1_500;
    /// <summary>Sources: the user said it in a conversation (the reply's tool), typed it in the app, or an assistant set it
    /// through Martlet's MCP server.</summary>
    public const string Said = "said", Typed = "typed", Assistant = "assistant";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    private readonly object gate = new();
    private readonly TimeProvider clock;
    private IReadOnlyList<UserPreference> cached = [];
    private (DateTime Time, long Length)? stamp;

    public UserPreferences(string directory, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The data folder the preferences are kept in.</summary>
    public string Directory { get; }

    /// <summary>Raised after the list changed here or was taken from another computer (on the caller's thread).</summary>
    public event Action? Changed;

    public static string PathOf(string directory) => Path.Combine(directory, FileName);

    /// <summary>Every preference, oldest first.</summary>
    public IReadOnlyList<UserPreference> All
    {
        get { lock (gate) return Current(); }
    }

    /// <summary>The preferences that apply while the user does <paramref name="activity"/> (null: no activity known, so only
    /// those for everywhere): everywhere first, then those for the activity's kind ("gaming"), then those for the activity
    /// itself ("gaming/Elden Ring"), each oldest first. A later one is more specific, so it wins when two disagree.</summary>
    public IReadOnlyList<UserPreference> ApplyingNow(string? activity)
    {
        lock (gate)
            return [.. Current().Where(p => ActivityScope.Applies(p.Activity, activity)).OrderBy(p => Specificity(p.Activity))];
    }

    /// <summary>The preferences that apply while the user does <paramref name="activity"/> as one block for a request (the
    /// Your preferences prompt, Companion › Prompts, with one line for each), or null when none applies or the owner emptied the
    /// prompt. The lines are at most <see cref="MaximumNoteCharacters"/>: when they don't fit, the least specific and oldest go
    /// first and the block says how many more there are. It goes at the end of the request (never its start), so prompt caches
    /// keep working.</summary>
    public string? Note(string? activity, PromptSettings? prompts) => Note(ApplyingNow(activity), prompts);

    /// <summary><see cref="Note(string?, PromptSettings?)"/> for a list already read.</summary>
    public static string? Note(IReadOnlyList<UserPreference> applying, PromptSettings? prompts)
    {
        ArgumentNullException.ThrowIfNull(applying);
        if (applying.Count == 0) return null;
        var lines = applying.Select(Line).ToList();
        var left = 0;
        while (lines.Count > 1 && lines.Sum(line => line.Length + 1) > MaximumNoteCharacters)
        {
            lines.RemoveAt(0);
            left++;
        }
        if (left > 0) lines.Add($"- ({left} more not shown; manage_preferences list shows them all.)");
        return PromptSettings.Fill(prompts, PromptCatalog.UserPreferences, ("preferences", string.Join("\n", lines)));
    }

    /// <summary>"- No hints unless I ask (while playing Elden Ring)": one preference as the note and the tools show it.</summary>
    public static string Line(UserPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        return "- " + preference.Text + (preference.Activity is null ? "" : $" ({preference.Where})");
    }

    /// <summary>"No preferences yet." or "3 preferences: 1 everywhere, 2 while gaming." Counts only, never a preference's text.</summary>
    public string Status()
    {
        var all = All;
        if (all.Count == 0) return "No preferences yet.";
        var everywhere = all.Count(p => p.Activity is null);
        var gaming = all.Count(p => p.Activity is not null && ActivityScope.Name(p.Activity) is null);
        var games = all.Count(p => ActivityScope.Name(p.Activity) is not null);
        var parts = new List<string>();
        if (everywhere > 0) parts.Add($"{everywhere} everywhere");
        if (gaming > 0) parts.Add($"{gaming} while gaming");
        if (games > 0) parts.Add($"{games} for one game");
        return $"{all.Count} preference{(all.Count == 1 ? "" : "s")}: {string.Join(", ", parts)}.";
    }

    /// <summary>Keeps <paramref name="text"/> as a new preference for <paramref name="activity"/>. The same rule for the same place
    /// is kept once (the call changes nothing).</summary>
    public UserPreferenceChange Add(string? text, string? activity, string source)
    {
        if (Clean(text) is not { } clean) return Refused($"Give the preference in words, up to {MaximumTextCharacters} characters.");
        if (!ActivityScope.IsValid(activity)) return Refused("That isn't a place a preference can apply.");
        var by = SourceOf(source);
        lock (gate)
        {
            var current = Current();
            if (current.FirstOrDefault(p => ActivityScope.Same(p.Activity, activity) &&
                string.Equals(p.Text, clean, StringComparison.OrdinalIgnoreCase)) is { } same)
                return new(same, false, null);
            if (current.Count >= MaximumPreferences)
                return Refused($"There are already {MaximumPreferences} preferences. Remove or change one first.");
            var preference = new UserPreference
            {
                Id = NewId(current), Text = clean, Activity = activity, CreatedAt = clock.GetUtcNow(), Source = by
            };
            return Saved([.. current, preference], preference);
        }
    }

    /// <summary>Changes preference <paramref name="id"/>: its text when <paramref name="text"/> is given, and where it applies when
    /// <paramref name="setActivity"/> (to <paramref name="activity"/>).</summary>
    public UserPreferenceChange Update(string? id, string? text, bool setActivity, string? activity, string source)
    {
        string? clean = null;
        if (text is not null && (clean = Clean(text)) is null)
            return Refused($"Give the preference in words, up to {MaximumTextCharacters} characters.");
        if (setActivity && !ActivityScope.IsValid(activity)) return Refused("That isn't a place a preference can apply.");
        var by = SourceOf(source);
        lock (gate)
        {
            var current = Current();
            if (Find(current, id) is not { } found) return Refused(NotFound(id));
            var next = found with
            {
                Text = clean ?? found.Text, Activity = setActivity ? activity : found.Activity, ChangedAt = clock.GetUtcNow(), Source = by
            };
            if (next.Text == found.Text && next.Activity == found.Activity) return new(found, false, null);
            return Saved([.. current.Select(p => p.Id == found.Id ? next : p)], next);
        }
    }

    /// <summary>Removes preference <paramref name="id"/>.</summary>
    public UserPreferenceChange Remove(string? id)
    {
        lock (gate)
        {
            var current = Current();
            if (Find(current, id) is not { } found) return Refused(NotFound(id));
            return Saved([.. current.Where(p => p.Id != found.Id)], found);
        }
    }

    /// <summary>The preference with <paramref name="id"/> (any case), or null.</summary>
    public UserPreference? Find(string? id)
    {
        lock (gate) return Find(Current(), id);
    }

    /// <summary>Another computer's list was taken (the shared settings saved the file): read it again and tell who follows.</summary>
    public void Followed()
    {
        lock (gate) stamp = null;
        Changed?.Invoke();
    }

    // ---------- the file ----------

    /// <summary>The saved preferences, or none when there is no file or it can't be read (or a newer Martlet wrote it).</summary>
    public static IReadOnlyList<UserPreference> Load(string directory) => TryLoad(directory) ?? [];

    /// <summary>The saved preferences, or null when there is no file or it can't be read (or a newer Martlet wrote it).</summary>
    public static IReadOnlyList<UserPreference>? TryLoad(string directory)
    {
        try { return Parse(File.ReadAllText(PathOf(directory))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Saves <paramref name="preferences"/> in one step (a temporary file, then a rename).</summary>
    public static void Save(string directory, IReadOnlyList<UserPreference> preferences)
    {
        var json = Write(preferences);
        System.IO.Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"user-preferences.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, PathOf(directory), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The canonical JSON form, as it is saved and shared.</summary>
    public static string Write(IReadOnlyList<UserPreference> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var document = new Document { SchemaVersion = SchemaVersion1, Preferences = [.. preferences] };
        Validate(document);
        return JsonSerializer.Serialize(document, Json);
    }

    /// <summary>Reads a list, or null when it isn't one this Martlet can use (a newer Martlet's, or broken).</summary>
    public static IReadOnlyList<UserPreference>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(json, Json);
            if (document is null) return null;
            document = document with { Preferences = document.Preferences ?? [] };
            Validate(document);
            return document.Preferences;
        }
        catch (Exception error) when (error is JsonException or ContractException or NotSupportedException) { return null; }
    }

    /// <summary>The account setting <see cref="SharedKey"/> backed by user-preferences.json in <paramref name="directory"/>.
    /// <paramref name="followed"/> runs after this computer took another computer's list (or an account's, on a switch).</summary>
    public static ISharedSection Section(string directory, Action? followed = null) => new DelegateSection(SharedKey, "Your preferences",
        _ =>
        {
            var saved = TryLoad(directory);
            var path = PathOf(directory);
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            return Task.FromResult<SharedLocal?>(new(Write(saved ?? []), null, saved is null, changed));
        },
        (setting, _) =>
        {
            if (Parse(setting.Value) is not { } preferences)
                return Task.FromResult(SharedApply.Waiting("They were saved by a newer Martlet; update this PC to use them."));
            try { Save(directory, preferences); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(SharedApply.Waiting("They couldn't be saved on this PC."));
            }
            followed?.Invoke();
            return Task.FromResult(SharedApply.Done);
        },
        () => Write([]));

    private sealed record Document
    {
        public int SchemaVersion { get; init; } = SchemaVersion1;
        public IReadOnlyList<UserPreference> Preferences { get; init; } = [];
    }

    private static void Validate(Document document)
    {
        ContractRules.Require(document.SchemaVersion == SchemaVersion1, "The preferences were saved by a newer Martlet.");
        ContractRules.Require(document.Preferences.Count <= MaximumPreferences, "There are too many preferences.");
        foreach (var preference in document.Preferences)
        {
            ContractRules.Require(preference is not null && IsId(preference.Id), "A preference has no valid ID.");
            ContractRules.Require(Clean(preference!.Text) == preference.Text, "A preference's text is empty, too long or has control characters.");
            ContractRules.Require(ActivityScope.IsValid(preference.Activity), "A preference applies to an unknown place.");
            ContractRules.Require(preference.Source is Said or Typed or Assistant, "A preference names an unknown source.");
        }
        ContractRules.Require(document.Preferences.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == document.Preferences.Count,
            "Two preferences have the same ID.");
    }

    // ---------- helpers ----------

    // The list as the file has it now, read again only when the file changed (its time or length) since it was read.
    private IReadOnlyList<UserPreference> Current()
    {
        var path = PathOf(Directory);
        var info = new FileInfo(path);
        (DateTime, long)? now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        if (stamp is { } known && now is { } seen && known == seen) return cached;
        if (now is null)
        {
            cached = [];
            stamp = null;
            return cached;
        }
        cached = TryLoad(Directory) ?? [];
        stamp = now;
        return cached;
    }

    private UserPreferenceChange Saved(IReadOnlyList<UserPreference> next, UserPreference preference)
    {
        try { Save(Directory, next); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Refused("The preferences couldn't be saved on this PC.");
        }
        cached = next;
        var info = new FileInfo(PathOf(Directory));
        stamp = info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        Changed?.Invoke();
        return new(preference, true, null);
    }

    private static UserPreferenceChange Refused(string problem) => new(null, false, problem);

    private static string NotFound(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "Give the id of the preference (list shows them)." : $"There is no preference {Clip(id.Trim())}. Call list for the ids; don't guess.";

    private static UserPreference? Find(IReadOnlyList<UserPreference> list, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : list.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string SourceOf(string source) => source is Said or Typed or Assistant
        ? source : throw new ArgumentException("A source is said, typed or assistant.", nameof(source));

    // Everywhere, then a kind ("gaming"), then one activity.
    private static int Specificity(string? activity) => activity is null ? 0 : ActivityScope.Name(activity) is null ? 1 : 2;

    /// <summary>The rule as it is kept: white space made single spaces, trimmed, quotes around it left out; null when it is empty,
    /// longer than <see cref="MaximumTextCharacters"/> or has a control character other than white space.</summary>
    internal static string? Clean(string? text)
    {
        if (text is null) return null;
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
            }
            else if (char.IsControl(character)) return null;
            else builder.Append(character);
        }
        var clean = builder.ToString().Trim().Trim('"', '\u201C', '\u201D').Trim();
        return clean.Length is > 0 and <= MaximumTextCharacters ? clean : null;
    }

    private static bool IsId(string? id) => id is { Length: >= 2 and <= 16 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    // "p" and six hex digits that no preference has.
    private static string NewId(IReadOnlyList<UserPreference> list)
    {
        while (true)
        {
            var id = "p" + Guid.NewGuid().ToString("N")[..6];
            if (list.All(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))) return id;
        }
    }

    private static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
