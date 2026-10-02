using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Core.Lorebooks;

/// <summary>What one reply is scanned against: the message being answered and the recent conversation before it.</summary>
/// <param name="Current">The message being answered (typed, transcribed or a screen-glance prompt).</param>
/// <param name="Earlier">Recent messages before it, oldest first.</param>
/// <param name="PersonaId">The active persona; SelectedPersonas lorebooks apply only to their personas.</param>
/// <param name="PersonaName">Replaces {{char}} in keys and content.</param>
public sealed record LorebookScanRequest(string Current, IReadOnlyList<string> Earlier, Guid? PersonaId = null, string? PersonaName = null);

/// <summary>One triggered entry, with macros already expanded in <see cref="Content"/>.</summary>
public sealed record LorebookHit(Guid BookId, string BookName, int BookIndex, int EntryIndex, LorebookEntry Entry,
    string Content, int Utf8Bytes, string Trigger);

/// <summary>Triggered entries in priority order (always-on first, then higher order first); the first ones are the most important.</summary>
public sealed record LorebookScanResult(IReadOnlyList<LorebookHit> Included, IReadOnlyList<LorebookHit> OverBudget,
    IReadOnlyList<LorebookHit> SkippedByChance, int ActiveBooks, int UsedUtf8Bytes)
{
    public static LorebookScanResult Empty { get; } = new([], [], [], 0, 0);

    /// <summary>Entries for one position, in the order they are written: lower order first, so higher orders sit closer to the reply.</summary>
    public static IReadOnlyList<LorebookHit> Arrange(IEnumerable<LorebookHit> hits, LorebookPosition position) =>
        hits.Where(hit => hit.Entry.Position == position)
            .OrderBy(hit => hit.Entry.Order).ThenBy(hit => hit.BookIndex).ThenBy(hit => hit.EntryIndex).ToArray();
}

/// <summary>SillyTavern-style World Info activation: keyword (or /regex/) matches in the latest messages, optional secondary-key
/// logic, always-on entries, probability, recursion through triggered content and a byte budget.</summary>
public static class LorebookScanner
{
    public const int MaximumIncluded = 64;
    public const int MaximumRecursionPasses = 5;
    internal const string DefaultCharacterName = "the companion";
    internal const string UserName = "the user";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex?> Regexes = new();

    private sealed class Candidate(Lorebook book, int bookIndex, int entryIndex, LorebookEntry entry,
        string[] keys, string[] secondaryKeys, string content, bool caseSensitive, bool wholeWords, int depth)
    {
        internal int BookIndex { get; } = bookIndex;
        internal int EntryIndex { get; } = entryIndex;
        internal LorebookEntry Entry { get; } = entry;
        internal string[] Keys { get; } = keys;
        internal string[] SecondaryKeys { get; } = secondaryKeys;
        internal string Content { get; } = content;
        internal int Utf8Bytes { get; } = Encoding.UTF8.GetByteCount(content);
        internal bool CaseSensitive { get; } = caseSensitive;
        internal bool WholeWords { get; } = wholeWords;
        internal int Depth { get; } = depth;
        internal bool Decided { get; set; }
        internal LorebookHit Hit(string trigger) => new(book.Id, book.Name, BookIndex, EntryIndex, Entry, Content, Utf8Bytes, trigger);
    }

    public static LorebookScanResult Scan(LorebookLibrary library, LorebookScanRequest request, Func<int, int>? next = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(request);
        next ??= Random.Shared.Next;
        var name = string.IsNullOrWhiteSpace(request.PersonaName) ? DefaultCharacterName : request.PersonaName.Trim();
        var candidates = new List<Candidate>();
        var activeBooks = 0;
        for (var b = 0; b < library.Books.Count; b++)
        {
            var book = library.Books[b];
            if (!book.AppliesTo(request.PersonaId)) continue;
            activeBooks++;
            for (var e = 0; e < book.Entries.Count; e++)
            {
                var entry = book.Entries[e];
                var content = Expand(entry.Content, name).Trim();
                if (!entry.Enabled || content.Length == 0 || !entry.Constant && entry.Keys.Count == 0) continue;
                candidates.Add(new(book, b, e, entry,
                    entry.Keys.Select(key => Expand(key, name)).ToArray(),
                    entry.SecondaryKeys.Select(key => Expand(key, name)).ToArray(),
                    content, entry.CaseSensitive ?? library.CaseSensitive, entry.MatchWholeWords ?? library.MatchWholeWords,
                    entry.ScanDepth ?? library.ScanDepth));
            }
        }
        if (candidates.Count == 0) return LorebookScanResult.Empty with { ActiveBooks = activeBooks };

        var messages = request.Earlier.Append(request.Current).ToArray();
        var windows = new Dictionary<int, string>();
        string Window(int depth)
        {
            if (!windows.TryGetValue(depth, out var text))
                windows[depth] = text = depth <= 0 ? "" : string.Join('\n', messages.Skip(Math.Max(0, messages.Length - depth)));
            return text;
        }

        var included = new List<LorebookHit>();
        var overBudget = new List<LorebookHit>();
        var skipped = new List<LorebookHit>();
        var recursion = new StringBuilder();
        var used = 0;
        for (var pass = 0; pass <= MaximumRecursionPasses; pass++)
        {
            var matched = new List<(Candidate Candidate, string Trigger)>();
            var recursed = recursion.ToString();
            foreach (var candidate in candidates)
            {
                if (candidate.Decided) continue;
                if (candidate.Entry.Constant)
                {
                    if (pass == 0) matched.Add((candidate, "always on"));
                    continue;
                }
                if (pass > 0 && candidate.Entry.ExcludeRecursion) continue;
                var text = pass == 0 ? Window(candidate.Depth) : Window(candidate.Depth) + "\n" + recursed;
                if (FirstMatch(candidate.Keys, text, candidate.CaseSensitive, candidate.WholeWords) is not { } key) continue;
                if (candidate.SecondaryKeys.Length > 0 && !SecondaryPasses(candidate, text)) continue;
                matched.Add((candidate, pass == 0 ? key : key + " (from another entry)"));
            }
            if (matched.Count == 0) break;

            var grew = false;
            foreach (var (candidate, trigger) in matched
                .OrderByDescending(item => item.Candidate.Entry.Constant)
                .ThenByDescending(item => item.Candidate.Entry.Order)
                .ThenBy(item => item.Candidate.BookIndex).ThenBy(item => item.Candidate.EntryIndex))
            {
                candidate.Decided = true;
                var hit = candidate.Hit(trigger);
                if (candidate.Entry.Probability < 100 && next(100) >= candidate.Entry.Probability)
                {
                    skipped.Add(hit);
                    continue;
                }
                if (included.Count >= MaximumIncluded || used + candidate.Utf8Bytes > library.BudgetUtf8Bytes)
                {
                    overBudget.Add(hit);
                    continue;
                }
                included.Add(hit);
                used += candidate.Utf8Bytes;
                if (library.Recursive && !candidate.Entry.PreventRecursion)
                {
                    recursion.Append('\n').Append(candidate.Content);
                    grew = true;
                }
            }
            if (!grew) break;
        }
        return new(included, overBudget, skipped, activeBooks, used);
    }

    /// <summary>Replaces SillyTavern's {{char}}/{{user}} (and legacy &lt;BOT&gt;/&lt;USER&gt;) macros.</summary>
    public static string Expand(string text, string? characterName)
    {
        if (text.IndexOf('{') < 0 && text.IndexOf('<') < 0) return text;
        var name = string.IsNullOrWhiteSpace(characterName) ? DefaultCharacterName : characterName.Trim();
        return text.Replace("{{char}}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("<BOT>", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{{user}}", UserName, StringComparison.OrdinalIgnoreCase)
            .Replace("<USER>", UserName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SecondaryPasses(Candidate candidate, string text)
    {
        var hits = candidate.SecondaryKeys.Count(key => Matches(key, text, candidate.CaseSensitive, candidate.WholeWords));
        return candidate.Entry.SecondaryLogic switch
        {
            LorebookSecondaryLogic.AndAny => hits > 0,
            LorebookSecondaryLogic.AndAll => hits == candidate.SecondaryKeys.Length,
            LorebookSecondaryLogic.NotAny => hits == 0,
            LorebookSecondaryLogic.NotAll => hits < candidate.SecondaryKeys.Length,
            _ => false
        };
    }

    private static string? FirstMatch(IEnumerable<string> keys, string text, bool caseSensitive, bool wholeWords) =>
        keys.FirstOrDefault(key => Matches(key, text, caseSensitive, wholeWords));

    /// <summary>Whether one key occurs in <paramref name="text"/>. A key written /pattern/flags is a regular expression
    /// (flags i, m and s are honored); an invalid or too slow expression never matches.</summary>
    public static bool Matches(string key, string text, bool caseSensitive, bool wholeWords)
    {
        if (text.Length == 0) return false;
        if (RegexKey(key) is { } regex)
        {
            try { return regex.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }
        if (IsRegexKey(key)) return false;
        key = key.Trim();
        if (key.Length == 0) return false;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!wholeWords) return text.Contains(key, comparison);
        for (var start = text.IndexOf(key, comparison); start >= 0; start = text.IndexOf(key, start + 1, comparison))
        {
            var end = start + key.Length;
            var leftOk = !IsWord(key[0]) || start == 0 || !IsWord(text[start - 1]);
            var rightOk = !IsWord(key[^1]) || end == text.Length || !IsWord(text[end]);
            if (leftOk && rightOk) return true;
            if (end >= text.Length) break;
        }
        return false;
    }

    /// <summary>Whether <paramref name="key"/> is written as /pattern/flags.</summary>
    public static bool IsRegexKey(string key)
    {
        key = key.Trim();
        var close = key.LastIndexOf('/');
        return key.Length >= 3 && key[0] == '/' && close > 1 && key[(close + 1)..].All(c => "gimsuyd".Contains(c));
    }

    /// <summary>The compiled expression of a /pattern/flags key, or null when the key is literal or the pattern is invalid.</summary>
    public static Regex? RegexKey(string key)
    {
        if (!IsRegexKey(key)) return null;
        key = key.Trim();
        var close = key.LastIndexOf('/');
        var options = RegexOptions.CultureInvariant;
        foreach (var flag in key[(close + 1)..])
            options |= flag switch { 'i' => RegexOptions.IgnoreCase, 'm' => RegexOptions.Multiline, 's' => RegexOptions.Singleline, _ => 0 };
        var pattern = key[1..close];
        if (Regexes.Count > 1024) Regexes.Clear();
        return Regexes.GetOrAdd((pattern, options), static item =>
        {
            try { return new Regex(item.Pattern, item.Options, RegexTimeout); }
            catch (ArgumentException) { return null; }
        });
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}
