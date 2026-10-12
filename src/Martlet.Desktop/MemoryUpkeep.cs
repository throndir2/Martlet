using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Memory;

// Also built into Martlet's MCP server (memory_upkeep_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>A fact's hidden search words as Martlet keeps them (<see cref="MemoryFact.SearchWords"/>): up to 16 short words or
/// phrases, comma separated, each with a letter or digit, without repeats.</summary>
internal static class MemorySearchWords
{
    internal const int MaximumWords = 16;
    internal const int MaximumWordCharacters = 40;

    /// <summary>The words of a model's WORDS line, cleaned up, or null when none is usable.</summary>
    internal static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var length = 0;
        foreach (var raw in text.Split([',', ';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Trim('"', '\'', '.', '*', '`', '[', ']', '(', ')', '\u201C', '\u201D').Trim();
            if (word.Length is 0 or > MaximumWordCharacters || !word.Any(char.IsLetterOrDigit) || word.Any(char.IsControl) ||
                !seen.Add(word))
                continue;
            if (length + word.Length + 2 > MemoryLimits.MaximumSearchWordsCharacters) break;
            words.Add(word.ToLowerInvariant());
            length += word.Length + 2;
            if (words.Count == MaximumWords) break;
        }
        return words.Count == 0 ? null : string.Join(", ", words);
    }

    /// <summary>The words of both, the first's first, within the bounds (null when neither has any).</summary>
    internal static string? Union(params string?[] lists) => Normalize(string.Join(", ", lists.OfType<string>()));
}

/// <summary>One upkeep pass's work on one store (<see cref="MemoryUpkeep.Plan"/>): the facts listed (numbered from 1), the groups
/// of numbers that may repeat or contradict each other, the numbers that need search words, and how far the store's facts are
/// checked for duplicates once this pass is done (the newest fact looked at, by its last change and ID).</summary>
internal sealed record MemoryUpkeepPlan(IReadOnlyList<MemoryFact> Facts, IReadOnlyList<IReadOnlyList<int>> Groups,
    IReadOnlySet<int> NeedWords, (DateTimeOffset At, Guid Id)? CheckedThrough, int Examined)
{
    internal bool HasWork => Groups.Count > 0 || NeedWords.Count > 0;

    internal MemoryFact this[int number] => Facts[number - 1];

    public override string ToString() =>
        $"{nameof(MemoryUpkeepPlan)} {{ Facts = {Facts.Count}, Groups = {Groups.Count}, NeedWords = {NeedWords.Count}, Examined = {Examined} }}";
}

/// <summary>What the model answered: duplicates to fold into the first (with the sentence that says what they all say, or
/// null to keep the first's), facts a newer fact made outdated and search words by fact number.</summary>
internal sealed record MemoryUpkeepAnswer(IReadOnlyList<(int Keep, IReadOnlyList<int> Others, string? Content)> Same,
    IReadOnlyList<(int Old, int Newer)> Outdated, IReadOnlyDictionary<int, string> Words)
{
    internal bool IsEmpty => Same.Count == 0 && Outdated.Count == 0 && Words.Count == 0;

    public override string ToString() =>
        $"{nameof(MemoryUpkeepAnswer)} {{ Same = {Same.Count}, Outdated = {Outdated.Count}, Words = {Words.Count} }}";
}

/// <summary>Tidying memory in the background (docs/MEMORY.md, "Search words, history and tidying"): each pass lists groups of a
/// store's facts that may say the same thing or contradict each other (found with the store's own search, the facts' search
/// words included) and facts without search words, asks a free Thinking pool member (Companion › Prompts › Tidying memory),
/// and turns its SAME, OUTDATED and WORDS lines into one <see cref="MemoryStore.TidyAsync"/> request. It never runs on the
/// reply's path or on the conversation's own model.</summary>
internal static partial class MemoryUpkeep
{
    internal const string Nothing = "NOTHING";
    internal const int MaximumGroups = 6;
    internal const int MaximumGroupSize = 5;
    internal const int MaximumWordsFacts = 16;
    internal const int MaximumListed = 40;
    /// <summary>The most facts one pass looks at for duplicates (each one search of the store's index, in microseconds).</summary>
    internal const int MaximumExamined = 400;
    internal const int MaximumFactCharacters = 300;

    /// <summary>The work of one pass over <paramref name="snapshot"/>: facts changed after <paramref name="checkedThrough"/> are
    /// looked at for duplicates (oldest first), and current facts without search words that <paramref name="asked"/> doesn't
    /// rule out get words (newest first). <paramref name="person"/> maps a voice ID to the person it stands for now, so only facts
    /// of one person are grouped. <paramref name="similar"/> (finding by meaning) gives the facts close enough in meaning to a fact
    /// to be grouped with it even when they share few words; they are fused with the lexical matches through the search engine.</summary>
    internal static MemoryUpkeepPlan Plan(MemorySnapshot snapshot, DateTimeOffset now, (DateTimeOffset At, Guid Id)? checkedThrough,
        Func<MemoryFact, bool>? asked = null, Func<string?, string?>? person = null, CancellationToken token = default,
        Func<MemoryFact, IReadOnlyCollection<Guid>>? similar = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        person ??= id => id;
        var current = snapshot.Live(now).OrderBy(f => f.UpdatedAtUtc).ThenBy(f => f.Id).ToArray();
        var listed = new List<MemoryFact>();
        var numbers = new Dictionary<Guid, int>();
        int Number(MemoryFact fact)
        {
            if (numbers.TryGetValue(fact.Id, out var number)) return number;
            listed.Add(fact);
            return numbers[fact.Id] = listed.Count;
        }
        var groups = new List<IReadOnlyList<int>>();
        (DateTimeOffset At, Guid Id)? through = checkedThrough;
        var examined = 0;
        foreach (var fact in current)
        {
            if (checkedThrough is { } mark && (fact.UpdatedAtUtc < mark.At || fact.UpdatedAtUtc == mark.At && fact.Id.CompareTo(mark.Id) <= 0))
                continue;
            if (groups.Count == MaximumGroups || examined == MaximumExamined || listed.Count + MaximumGroupSize > MaximumListed - 8)
                break;
            token.ThrowIfCancellationRequested();
            examined++;
            through = (fact.UpdatedAtUtc, fact.Id);
            if (numbers.ContainsKey(fact.Id)) continue;
            var text = Clip(fact.Content, 1_000) + (fact.SearchWords is { } words ? ", " + words : "");
            var near = similar?.Invoke(fact) ?? [];
            if (MemoryQuery.TryFromBoundedSource(text, 8) is not { } query) continue;
            if (near.Count > 0) query = query with { Similar = [.. near] };
            // Facts that share a good part of this one's words (its search words too), or that are close in meaning, of the same
            // person.
            var needed = Math.Max(2, (int)Math.Ceiling(query.Text.Split(' ').Length * 0.25));
            var who = person(fact.VoiceId);
            var related = snapshot.Search(query, now, token)
                .Where(hit => hit.Fact.Id != fact.Id && !numbers.ContainsKey(hit.Fact.Id) && (hit.MatchedTerms >= needed || near.Contains(hit.Fact.Id)) &&
                    string.Equals(person(hit.Fact.VoiceId), who, StringComparison.Ordinal))
                .Take(MaximumGroupSize - 1).Select(hit => hit.Fact).ToArray();
            if (related.Length == 0) continue;
            groups.Add([Number(fact), .. related.Select(Number)]);
        }
        var needWords = new HashSet<int>();
        foreach (var fact in listed.Where(f => f.SearchWords is null && asked?.Invoke(f) != true).ToArray())
            needWords.Add(Number(fact));
        foreach (var fact in Enumerable.Reverse(current))
        {
            if (needWords.Count >= MaximumWordsFacts || listed.Count >= MaximumListed) break;
            if (fact.SearchWords is not null || numbers.ContainsKey(fact.Id) || asked?.Invoke(fact) == true) continue;
            needWords.Add(Number(fact));
        }
        return new(listed, groups, needWords, through, examined);
    }

    /// <summary>The Tidying memory prompt (Companion › Prompts).</summary>
    internal static string? Instructions(PromptSettings? prompts) =>
        PromptSettings.Fill(prompts, PromptCatalog.MemoryUpkeep, ("nothing", Nothing));

    /// <summary>The numbered facts of <paramref name="plan"/>: each group, then the facts that only need words. Each fact starts
    /// with whose it is (<paramref name="whose"/>, such as "[Sam] ") and ends with when it last changed.</summary>
    internal static string Text(MemoryUpkeepPlan plan, Func<MemoryFact, string>? whose = null)
    {
        var text = new StringBuilder();
        void Line(int number)
        {
            var fact = plan[number];
            text.Append(number).Append(". ").Append(whose?.Invoke(fact) ?? "").Append(Clip(fact.Content, MaximumFactCharacters))
                .Append(" (").Append(fact.UpdatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(plan.NeedWords.Contains(number) ? "; needs words)" : ")").Append('\n');
        }
        if (plan.Groups.Count > 0)
        {
            text.Append("Groups of facts that may say the same thing or contradict each other:\n");
            for (var g = 0; g < plan.Groups.Count; g++)
            {
                text.Append("Group ").Append(g + 1).Append(":\n");
                foreach (var number in plan.Groups[g]) Line(number);
            }
        }
        var alone = plan.NeedWords.Where(number => !plan.Groups.Any(group => group.Contains(number))).Order().ToArray();
        if (alone.Length > 0)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append("Facts that need search words:\n");
            foreach (var number in alone) Line(number);
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The SAME, OUTDATED and WORDS lines of <paramref name="answer"/> that fit <paramref name="plan"/>: numbers of one
    /// group for SAME and OUTDATED (the newer fact last changed no earlier than the old one), each fact in at most one of them,
    /// WORDS only for facts that need words. Anything else is left out.</summary>
    internal static MemoryUpkeepAnswer Parse(string? answer, MemoryUpkeepPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var same = new List<(int, IReadOnlyList<int>, string?)>();
        var outdated = new List<(int, int)>();
        var words = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(answer)) return new(same, outdated, words);
        var used = new HashSet<int>();
        int? GroupOf(int number)
        {
            for (var g = 0; g < plan.Groups.Count; g++)
                if (plan.Groups[g].Contains(number)) return g;
            return null;
        }
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal)
                .Trim().TrimStart('-', '*', '\u2022', '>', ' ', '\t').Trim();
            Match match;
            if ((match = SameLine().Match(line)).Success)
            {
                var listed = NumberList().Matches(match.Groups["ns"].Value)
                    .Select(m => int.Parse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture)).Distinct().ToArray();
                if (listed.Length < 2 || listed.Any(n => n < 1 || n > plan.Facts.Count || used.Contains(n)) ||
                    listed.Select(GroupOf).Distinct().ToArray() is not [{ } _])
                    continue;
                string? content = null;
                if (match.Groups["fact"].Success)
                {
                    content = Fact(match.Groups["fact"].Value);
                    if (content is null) continue;
                }
                same.Add((listed[0], listed[1..], content));
                used.UnionWith(listed);
            }
            else if ((match = OutdatedLine().Match(line)).Success)
            {
                if (!int.TryParse(match.Groups["old"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var old) ||
                    !int.TryParse(match.Groups["new"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var newer) ||
                    old == newer || old < 1 || newer < 1 || old > plan.Facts.Count || newer > plan.Facts.Count ||
                    used.Contains(old) || GroupOf(old) is not { } group || GroupOf(newer) != group ||
                    plan[newer].UpdatedAtUtc < plan[old].UpdatedAtUtc)
                    continue;
                outdated.Add((old, newer));
                used.Add(old);
            }
            else if ((match = WordsLine().Match(line)).Success)
            {
                if (int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                    plan.NeedWords.Contains(number) && !words.ContainsKey(number) &&
                    MemorySearchWords.Normalize(match.Groups["words"].Value) is { } found)
                    words[number] = found;
            }
        }
        // A fact folded into another or made outdated needs no words of its own.
        foreach (var (_, others, _) in same)
            foreach (var other in others) words.Remove(other);
        foreach (var (old, _) in outdated)
            words.Remove(old);
        return new(same, outdated, words);
    }

    /// <summary>The store request for <paramref name="answer"/>: the exact revisions the plan listed, so a fact changed meanwhile
    /// is left alone. A kept fact whose sentence changes gets the words the answer gave it, else the words of every fact folded
    /// into it.</summary>
    internal static MemoryTidyRequest Request(MemoryUpkeepPlan plan, MemoryUpkeepAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(answer);
        var merges = new List<MemoryFactMerge>();
        var keptWords = new HashSet<int>();
        foreach (var (keep, others, content) in answer.Same)
        {
            var kept = plan[keep];
            var changed = content is not null && !string.Equals(content, kept.Content, StringComparison.Ordinal);
            var words = answer.Words.GetValueOrDefault(keep) ??
                (changed ? MemorySearchWords.Union([kept.SearchWords, .. others.Select(n => plan[n].SearchWords)]) : null);
            if (answer.Words.ContainsKey(keep)) keptWords.Add(keep);
            merges.Add(new(kept.Id, kept.Revision, [.. others.Select(n => (plan[n].Id, plan[n].Revision))], changed ? content : null, words));
        }
        return new()
        {
            Retirements = [.. answer.Outdated.Select(o => new MemoryFactRetirement(plan[o.Old].Id, plan[o.Old].Revision, plan[o.Newer].Id))],
            Merges = merges,
            Words = [.. answer.Words.Where(w => !keptWords.Contains(w.Key)).Select(w => new MemoryFactWords(plan[w.Key].Id, plan[w.Key].Revision, w.Value))]
        };
    }

    private static string? Fact(string value)
    {
        var fact = value.Trim().Trim('"', '\'', '\u201C', '\u201D').Trim();
        return fact.Length is 0 or > MaximumFactCharacters || fact.Count(char.IsLetter) < 3 || fact.Any(char.IsControl) ||
            string.Equals(fact, Nothing, StringComparison.OrdinalIgnoreCase) ||
            fact.Contains("MARTLET_LOCAL_MEMORY", StringComparison.OrdinalIgnoreCase) ? null : fact;
    }

    /// <summary>One line of at most <paramref name="maximum"/> characters, never splitting a surrogate pair.</summary>
    private static string Clip(string text, int maximum)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= maximum) return line;
        var end = char.IsHighSurrogate(line[maximum - 1]) ? maximum - 1 : maximum;
        return line[..end].TrimEnd() + "\u2026";
    }

    [GeneratedRegex(@"^(?:SAME|MERGE|DUPLICATES?)\s*#?\s*(?<ns>\d{1,3}(?:\s*(?:,|&|\+|and)?\s*#?\s*\d{1,3})+)\s*(?:[:\-\u2013]\s*(?<fact>.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SameLine();

    [GeneratedRegex(@"\d{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberList();

    [GeneratedRegex(@"^(?:OUTDATED|REPLACED|OLD)\s*#?\s*(?<old>\d{1,3})\s*(?:BY|WITH|->|\u2192|:)\s*#?\s*(?<new>\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutdatedLine();

    [GeneratedRegex(@"^(?:WORDS|SEARCH\s*WORDS|KEYWORDS)\s*#?\s*(?<n>\d{1,3})\s*[:\-\u2013.)]\s*(?<words>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordsLine();
}
