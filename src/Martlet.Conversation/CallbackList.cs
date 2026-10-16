using System.Text.RegularExpressions;

namespace Martlet.Conversation;

/// <summary>One thing worth bringing back later in the conversation ("quote - they said \"one more try\" about the boss") and
/// when it first came up.</summary>
public sealed record Callback(DateTimeOffset Since, string Text);

/// <summary>
/// The callback list (docs/CONVERSATION.md#callback-list): the few things from this conversation worth bringing back later
/// (open topics, running jokes, the user's own words, facts that fit a tease), each with when it first came up. The Callback
/// list check-in writes it after each reply (<see cref="CheckIns.Callbacks"/>); Think ahead and Quiet moments read it, so a
/// small model can call back to something said long before the end of the conversation it sees. The live conversation forgets
/// it with the conversation (Refresh context, pause, lock, closing the talk window). In memory only: never saved or logged.
/// </summary>
public sealed partial class CallbackList
{
    /// <summary>How many things it keeps.</summary>
    public const int MaximumItems = 8;
    /// <summary>How long one thing may be; a longer one is cut.</summary>
    public const int MaximumCharacters = 160;
    /// <summary>A list that wasn't written for this long is stale: nothing was said for a long time.</summary>
    public static TimeSpan Window => TimeSpan.FromHours(2);

    private readonly object gate = new();
    private IReadOnlyList<Callback> items = [];
    private DateTimeOffset written;

    /// <summary>Goes up each time the list is forgotten, so an answer about a forgotten conversation can't write it again.</summary>
    public int Version { get { lock (gate) return version; } }
    private int version;

    /// <summary>How many things it keeps now (it may be stale).</summary>
    public int Count { get { lock (gate) return items.Count; } }

    /// <summary>The list at <paramref name="now"/>, oldest first; empty when it wasn't written within <see cref="Window"/>.</summary>
    public IReadOnlyList<Callback> Recent(DateTimeOffset now)
    {
        lock (gate) return now - written <= Window ? items : [];
    }

    /// <summary>Writes the list again with <paramref name="texts"/> at <paramref name="now"/> (<see cref="Merge"/>), unless it
    /// was forgotten since <paramref name="expected"/> (its <see cref="Version"/> when the check-in started): then nothing
    /// changes and it returns null.</summary>
    public IReadOnlyList<Callback>? Keep(IReadOnlyList<string> texts, DateTimeOffset now, int? expected = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        lock (gate)
        {
            if (expected is { } wanted && wanted != version) return null;
            items = Merge(now - written <= Window ? items : [], texts, now);
            written = now;
            return items;
        }
    }

    /// <summary>Forgets the list, with the conversation.</summary>
    public void Clear()
    {
        lock (gate)
        {
            items = [];
            written = default;
            version++;
        }
    }

    /// <summary>The new list: each of <paramref name="texts"/> on one line, cut to <see cref="MaximumCharacters"/>, at most
    /// <see cref="MaximumItems"/> and each once. A thing that was on <paramref name="old"/> (the same words, whatever the case,
    /// spaces and closing punctuation) keeps when it first came up; a new one came up at <paramref name="now"/>.</summary>
    public static IReadOnlyList<Callback> Merge(IReadOnlyList<Callback> old, IReadOnlyList<string> texts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(texts);
        var kept = new List<Callback>();
        foreach (var raw in texts)
        {
            if (Line(raw) is not { } text) continue;
            var key = Key(text);
            if (kept.Any(k => Key(k.Text) == key)) continue;
            kept.Add(new(old.FirstOrDefault(o => Key(o.Text) == key)?.Since ?? now, text));
            if (kept.Count == MaximumItems) break;
        }
        return [.. kept.OrderBy(k => k.Since)];
    }

    /// <summary>One line for each, oldest first, with when it came up and how long ago:
    /// - 10:05 PM (40 min ago): quote - they said "one more try" about the boss</summary>
    public static string Lines(IReadOnlyList<Callback> items, DateTimeOffset now) =>
        string.Join("\n", items.Select(c => $"- {SaidLately.Clock(c.Since.ToOffset(now.Offset))} ({Reminders.Span(now - c.Since)} ago): {c.Text}"));

    private static string? Line(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = Spaces().Replace(text, " ").Trim();
        return line.Length == 0 ? null : line.Length <= MaximumCharacters ? line : line[..MaximumCharacters].TrimEnd() + "…";
    }

    private static string Key(string text) => Spaces().Replace(text, " ").Trim().TrimEnd('.', '!', '…').Trim().ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
