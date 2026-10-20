using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Text;

namespace Martlet.Core.Lorebooks;

/// <summary>What one reply is scanned against: the message being answered and the recent conversation before it.</summary>
/// <param name="Current">The message being answered (typed, transcribed or a screen-glance prompt).</param>
/// <param name="Earlier">Recent messages before it, oldest first.</param>
/// <param name="PersonaId">The active persona; SelectedPersonas lorebooks apply only to their personas.</param>
/// <param name="PersonaName">Replaces {{char}} in keys and content.</param>
/// <param name="Activity">What the user does now, such as the name of the game played: scanned on its own after the messages
/// (whatever the scan depth), so an entry keyed by a game's name joins in while that game is played. Null: nothing.</param>
public sealed record LorebookScanRequest(string Current, IReadOnlyList<string> Earlier, Guid? PersonaId = null, string? PersonaName = null,
    string? Activity = null);

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
/// logic, always-on entries, probability, recursion through triggered content and a byte budget. With word forms on
/// (<see cref="LorebookLibrary.MatchWordForms"/>, or an entry's own choice), a keyword also matches the other forms of its words
/// by their <see cref="SearchTerms"/> stems ("dragon" finds "dragons", "Drache" finds "Drachen" in German text); a /regex/ key
/// never changes.</summary>
public static class LorebookScanner
{
    public const int MaximumIncluded = 64;
    public const int MaximumRecursionPasses = 5;
    internal const string DefaultCharacterName = "the companion";
    internal const string UserName = "the user";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex?> Regexes = new();
    private static readonly ConcurrentDictionary<(string Key, SearchLanguage Language), KeyWord[]?> KeyWords = new();

    // Content, its size and the filter keywords are worked out only for entries whose keywords matched, so a large lorebook
    // costs little more per reply than the keyword search itself.
    private sealed class Candidate(Lorebook book, int bookIndex, int entryIndex, LorebookEntry entry, string name,
        bool caseSensitive, bool wholeWords, bool wordForms, int depth)
    {
        private string? content;
        private string[]? secondaryKeys;
        private int utf8Bytes = -1;
        internal int BookIndex { get; } = bookIndex;
        internal int EntryIndex { get; } = entryIndex;
        internal LorebookEntry Entry { get; } = entry;
        internal string Name { get; } = name;
        internal string[] SecondaryKeys => secondaryKeys ??= Entry.SecondaryKeys.Select(key => Expand(key, Name)).ToArray();
        internal string Content => content ??= Expand(Entry.Content, Name).Trim();
        internal int Utf8Bytes => utf8Bytes >= 0 ? utf8Bytes : utf8Bytes = Encoding.UTF8.GetByteCount(Content);
        internal bool CaseSensitive { get; } = caseSensitive;
        internal bool WholeWords { get; } = wholeWords;
        internal bool WordForms { get; } = wordForms;
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
                // Expanding {{char}} and {{user}} never empties content, so blank content is blank before expanding too.
                if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Content) || !entry.Constant && entry.Keys.Count == 0) continue;
                candidates.Add(new(book, b, e, entry, name, entry.CaseSensitive ?? library.CaseSensitive,
                    entry.MatchWholeWords ?? library.MatchWholeWords, entry.MatchWordForms ?? library.MatchWordForms,
                    entry.ScanDepth ?? library.ScanDepth));
            }
        }
        if (candidates.Count == 0) return LorebookScanResult.Empty with { ActiveBooks = activeBooks };

        // Only the messages there are: a scan depth beyond them reads the same text as the whole conversation.
        var messages = request.Earlier.Append(request.Current).ToArray();
        var windows = new Dictionary<int, string>();
        string Window(int depth)
        {
            depth = Math.Clamp(depth, 0, messages.Length);
            if (!windows.TryGetValue(depth, out var text))
                windows[depth] = text = depth == 0 ? "" : string.Join('\n', messages, messages.Length - depth, depth);
            return text;
        }
        var recursed = "";
        var withRecursion = new Dictionary<int, string>();
        string WithRecursion(int depth)
        {
            depth = Math.Clamp(depth, 0, messages.Length);
            if (!withRecursion.TryGetValue(depth, out var text))
                withRecursion[depth] = text = Window(depth) + "\n" + recursed;
            return text;
        }

        // The words of each text scanned, read only for entries that match word forms.
        var read = new Dictionary<string, WordForms>(ReferenceEqualityComparer.Instance);
        WordForms? Forms(Candidate candidate, string text)
        {
            if (!candidate.WordForms) return null;
            if (!read.TryGetValue(text, out var forms)) read[text] = forms = new(text);
            return forms;
        }

        var included = new List<LorebookHit>();
        var overBudget = new List<LorebookHit>();
        var skipped = new List<LorebookHit>();
        var recursion = new StringBuilder();
        var used = 0;
        for (var pass = 0; pass <= MaximumRecursionPasses; pass++)
        {
            var matched = new List<(Candidate Candidate, string Trigger)>();
            recursed = recursion.ToString();
            withRecursion.Clear();
            foreach (var candidate in candidates)
            {
                if (candidate.Decided) continue;
                if (candidate.Entry.Constant)
                {
                    if (pass == 0) matched.Add((candidate, "always on"));
                    continue;
                }
                if (pass > 0 && candidate.Entry.ExcludeRecursion) continue;
                var text = pass == 0 ? Window(candidate.Depth) : WithRecursion(candidate.Depth);
                var forms = Forms(candidate, text);
                var key = FirstMatch(candidate.Entry.Keys, name, text, candidate.CaseSensitive, candidate.WholeWords, forms);
                // The game played now: an entry keyed by its name joins in while it is played.
                if (key is null && pass == 0 && !string.IsNullOrWhiteSpace(request.Activity))
                {
                    text = request.Activity;
                    forms = Forms(candidate, text);
                    key = FirstMatch(candidate.Entry.Keys, name, text, candidate.CaseSensitive, candidate.WholeWords, forms) is { } now
                        ? now + " (played now)" : null;
                }
                if (key is null) continue;
                if (candidate.Entry.SecondaryKeys.Count > 0 && !SecondaryPasses(candidate, text, forms)) continue;
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

    private static bool SecondaryPasses(Candidate candidate, string text, WordForms? forms)
    {
        var hits = candidate.SecondaryKeys.Count(key =>
            Matches(key, text, candidate.CaseSensitive, candidate.WholeWords) || forms?.Matches(key, candidate.CaseSensitive) == true);
        return candidate.Entry.SecondaryLogic switch
        {
            LorebookSecondaryLogic.AndAny => hits > 0,
            LorebookSecondaryLogic.AndAll => hits == candidate.SecondaryKeys.Length,
            LorebookSecondaryLogic.NotAny => hits == 0,
            LorebookSecondaryLogic.NotAll => hits < candidate.SecondaryKeys.Length,
            _ => false
        };
    }

    private static string? FirstMatch(IReadOnlyList<string> keys, string name, string text, bool caseSensitive, bool wholeWords,
        WordForms? forms)
    {
        foreach (var raw in keys)
        {
            var key = Expand(raw, name);
            if (Matches(key, text, caseSensitive, wholeWords) || forms?.Matches(key, caseSensitive) == true) return key;
        }
        return null;
    }

    /// <summary>Whether one key, or another form of its words, occurs in <paramref name="text"/>: the key's words in order, each
    /// with the same <see cref="SearchTerms"/> stem as a word of the text (by English rules, and by the text's own language's
    /// when it is in another one), so "dragon" matches "dragons" and "red dragon" matches "Red Dragons". A case-sensitive key
    /// needs the same capital letters as well ("Rose" matches "Roses", never "rose"). A /regex/ key and a key in a script
    /// written without spaces match only as <see cref="Matches(string, string, bool, bool)"/> does.</summary>
    public static bool MatchesWordForms(string key, string text, bool caseSensitive) =>
        !string.IsNullOrEmpty(text) && new WordForms(text).Matches(key, caseSensitive);

    // One word of a key: its English stem, its stem by the text's language, and where it is in the key.
    private readonly record struct KeyWord(string Stem, string Form, int Start, int Length);

    // The words of a scanned text and their stems, read once for all the keys that match word forms.
    private sealed class WordForms
    {
        private readonly string text;
        private readonly SearchLanguage language;
        private readonly IReadOnlyList<SearchWord> words;
        private readonly string[] stems, forms;
        private readonly Dictionary<string, List<int>> places = new(StringComparer.Ordinal);

        internal WordForms(string text)
        {
            this.text = text;
            words = SearchTerms.Locate(text);
            var plain = new string[words.Count];
            for (var i = 0; i < plain.Length; i++) plain[i] = words[i].Word;
            language = SearchTerms.LanguageOf(plain);
            stems = new string[plain.Length];
            forms = new string[plain.Length];
            for (var i = 0; i < plain.Length; i++)
            {
                stems[i] = SearchTerms.Stem(plain[i]);
                forms[i] = language == SearchLanguage.English ? stems[i] : SearchTerms.Stem(plain[i], language);
                Place(stems[i], i);
                if (forms[i] != stems[i]) Place(forms[i], i);
            }
        }

        private void Place(string stem, int at)
        {
            if (!places.TryGetValue(stem, out var list)) places[stem] = list = [];
            if (list.Count == 0 || list[^1] != at) list.Add(at);
        }

        internal bool Matches(string key, bool caseSensitive)
        {
            if (Words(key, language) is not { Length: > 0 } keyWords) return false;
            return Starting(keyWords[0].Stem, keyWords, key, caseSensitive) ||
                keyWords[0].Form != keyWords[0].Stem && Starting(keyWords[0].Form, keyWords, key, caseSensitive);
        }

        private bool Starting(string stem, KeyWord[] keyWords, string key, bool caseSensitive)
        {
            if (!places.TryGetValue(stem, out var starts)) return false;
            foreach (var start in starts)
            {
                if (start + keyWords.Length > words.Count) break;
                var all = true;
                for (var j = 0; j < keyWords.Length && all; j++) all = Same(start + j, keyWords[j], key, caseSensitive);
                if (all) return true;
            }
            return false;
        }

        private bool Same(int at, KeyWord keyWord, string key, bool caseSensitive)
        {
            if (stems[at] != keyWord.Stem && stems[at] != keyWord.Form && forms[at] != keyWord.Stem && forms[at] != keyWord.Form) return false;
            if (!caseSensitive) return true;
            var word = words[at];
            for (var i = 0; i < Math.Min(word.Length, keyWord.Length); i++)
                if (char.IsUpper(text[word.Start + i]) != char.IsUpper(key[keyWord.Start + i])) return false;
            return true;
        }
    }

    // The words of a literal key in a script written with spaces, with their stems for a text in language; null for a /regex/
    // key, a key of Chinese, Japanese, Korean or Thai-like script (no word forms to find) or a key without words.
    private static KeyWord[]? Words(string key, SearchLanguage language)
    {
        if (KeyWords.TryGetValue((key, language), out var known)) return known;
        KeyWord[]? keyWords = null;
        if (!IsRegexKey(key) && !key.Any(Unspaced) && SearchTerms.Locate(key) is { Count: > 0 } words)
        {
            keyWords = new KeyWord[words.Count];
            for (var i = 0; i < keyWords.Length; i++)
            {
                var stem = SearchTerms.Stem(words[i].Word);
                keyWords[i] = new(stem, language == SearchLanguage.English ? stem : SearchTerms.Stem(words[i].Word, language),
                    words[i].Start, words[i].Length);
            }
        }
        // Every key of a large lorebook stays known between replies; a count kept here, since counting the dictionary locks it.
        if (Interlocked.Increment(ref knownKeys) > MaximumKnownKeys)
        {
            KeyWords.Clear();
            Interlocked.Exchange(ref knownKeys, 0);
        }
        KeyWords.TryAdd((key, language), keyWords);
        return keyWords;
    }

    private const int MaximumKnownKeys = 65_536;
    private static int knownKeys;

    private static bool Unspaced(char c) =>
        c is >= '\u0E00' and <= '\u0EFF' or >= '\u1000' and <= '\u11FF' or >= '\u1780' and <= '\u17FF' or >= '\u2E80' and <= '\uD7FF' ||
        char.IsSurrogate(c) || c is >= '\uF900' and <= '\uFAFF' or >= '\uFF66' and <= '\uFF9F';

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
