using Martlet.Core.Text;

namespace Martlet.Conversation.Guides;

/// <summary>A question made ready for a guide's index (docs/APP_GUIDES.md, "Searching a guide"). Its words become
/// <em>concepts</em>: each is one word of the question with the guide's terms that satisfy it (its stem, and a compound such as
/// "fireball" for "fire ball"; a word the guide writes in two, such as "greatsword" for "great sword", becomes both).
/// <list type="bullet">
/// <item>Conversational filler ("hey", "martlet", "wtf", "tf", "um", "please") and the app's full name are left out.</item>
/// <item>Question scaffolding ("find", "use", "best way", "spawn") and a part of the app's name ("in Stardew") are <em>soft</em>:
/// they help ordering when a chunk has them and never lower relevance when it hasn't. When a question has only soft words,
/// they are what it is about.</item>
/// <item>"Where" adds soft "location", "find" and "spawn", so a wiki's Locations section comes first; "who" adds "sell".</item>
/// <item>A few spellings and near-synonyms satisfy each other ("colour" and "color", "picture" and "image").</item>
/// <item>A word the guide doesn't have weighs as much as a fairly rare word (<see cref="GuideRanking.UnknownWeight"/>), so a
/// question about something else stays unsure.</item>
/// <item>An everyday chat word ("bed", "morning", "love") is <see cref="Concept.Common"/>: matched only in a chunk's text, it is
/// weak evidence that the question is about the app.</item>
/// </list></summary>
internal sealed class GuideQuery
{
    /// <summary>At most this many concepts are used (a long message is cut), so a chunk's matched concepts fit in a bit mask.</summary>
    public const int MaximumConcepts = 24;
    private const int MaximumWords = 96;

    // Said to an assistant, never about the app.
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "hey", "hi", "hello", "yo", "ok", "okay", "oh", "ah", "uh", "um", "umm", "uhh", "uhm", "erm", "er", "hmm", "hm", "hmmm",
        "huh", "eh", "well", "please", "pls", "plz", "thanks", "thank", "thx", "ty", "martlet", "wtf", "tf", "wth", "ffs", "omg",
        "lol", "lmao", "rofl", "idk", "ngl", "tbh", "rn", "bro", "dude", "guys", "buddy", "mate", "damn", "dammit", "darn",
        "dang", "heck", "hell", "shit", "fuck", "fucking", "fuckin", "frickin", "freaking", "effing", "bloody", "goddamn", "crap",
        "seriously", "literally", "basically", "actually", "really", "kinda", "sorta", "gonna", "wanna", "gotta", "yeah", "yep",
        "yup", "nah", "nope", "now", "right", "even", "still", "exactly", "quickly", "maybe", "probably", "anyway", "though",
        "tho", "again", "already", "currently", "today", "tonight", "supposed", "ever", "whats", "wheres", "hows", "whos", "u",
        "ur", "ya", "wat", "wut", "ugh", "ooh", "welp", "lemme", "gimme", "real", "deal", "stuck", "anyways"
    };

    // How a question is asked rather than what it is about (stems).
    private static readonly HashSet<string> Scaffolding = Stems(
        "find", "get", "use", "make", "need", "want", "know", "mean", "work", "go", "see", "look", "show", "help", "tell", "explain",
        "thing", "stuff", "way", "best", "good", "better", "easy", "easiest", "fast", "fastest", "quick", "quickest", "early",
        "earliest", "try", "figure", "able", "possible", "anyone", "someone", "something", "anything", "everything", "somewhere",
        "anywhere", "guide", "tip", "info", "information", "game", "app", "program", "software", "place", "spot", "location",
        "locate", "happen", "start", "like", "think", "say", "called", "named", "kind", "type", "lot", "much", "many", "one",
        "obtain", "acquire", "learn", "unlock", "set", "spawn", "drop", "catch", "beat", "kill", "defeat", "rid", "stop", "up",
        "down", "out", "off", "more", "most", "less", "least", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "exist", "available", "supposed", "able", "keep", "come", "put", "take", "give", "have", "past", "worth", "tips",
        "above", "across", "after", "against", "along", "among", "around", "before", "behind", "below", "beneath", "beside",
        "between", "beyond", "during", "except", "inside", "near", "outside", "over", "since", "through", "toward", "towards",
        "under", "until", "upon", "within", "without", "per", "else", "other", "another", "each", "every", "same", "own");

    // Everyday chat words: matched only in a chunk's text, a lone one is weak evidence that the user asks about the app (stems).
    private static readonly HashSet<string> Everyday = Stems(
        "time", "day", "night", "morning", "evening", "afternoon", "week", "weekend", "month", "year", "tomorrow", "yesterday",
        "old", "new", "young", "age", "bad", "great", "nice", "fun", "funny", "cool", "awesome", "love", "hate", "feel", "feeling",
        "happy", "sad", "tired", "bored", "boring", "sick", "hurt", "pain", "bed", "sleep", "wake", "eat", "food", "dinner",
        "lunch", "breakfast", "drink", "coffee", "tea", "beer", "pizza", "home", "house", "room", "work", "job", "school", "class",
        "people", "person", "friend", "family", "mom", "dad", "mother", "father", "brother", "sister", "wife", "husband", "kid",
        "child", "baby", "man", "woman", "boy", "girl", "life", "world", "head", "hand", "eye", "face", "body", "heart", "music",
        "song", "sing", "dance", "movie", "film", "show", "tv", "phone", "call", "message", "email", "weather", "sun", "hot",
        "cold", "warm", "money", "pay", "car", "drive", "walk", "run", "play", "watch", "read", "talk", "ask", "answer",
        "question", "problem", "idea", "story", "news", "book", "joke", "laugh", "cat", "dog", "pet", "big", "small", "long",
        "short", "high", "low", "left", "first", "last", "next", "little", "few", "name", "word", "number", "real", "true",
        "sure", "wrong", "kind", "nothing", "everyone", "everybody", "someone", "today", "minute", "hour", "second", "moment",
        "start", "end", "open", "close", "win", "lose", "fight", "chat", "stream", "video", "tired", "up", "down", "back", "around");

    // Spellings and near-synonyms of one thing ("colour" and "color", "picture" and "image"): each satisfies the others (stems).
    private static readonly Dictionary<string, string[]> Variants = Groups(
        ["picture", "image", "photo", "pic", "photograph"], ["color", "colour"], ["gray", "grey"], ["center", "centre"],
        ["armor", "armour"], ["favorite", "favourite"], ["remove", "delete", "erase"], ["big", "large", "bigger", "larger"],
        ["small", "smaller", "tiny"], ["crooked", "tilted", "skewed"], ["shortcut", "hotkey", "keybind", "keybinding"],
        ["buy", "purchase", "sell", "shop"], ["get", "obtain", "acquire"], ["reset", "respec", "refund"], ["transparent", "transparency"],
        ["setting", "option", "preference"]);

    private GuideQuery(Concept[] concepts, (int Term, double Weight)[] terms, (int A, int B, int Compound)[] pairs, double contentWeight,
        int contentMask, int softMask)
    {
        Concepts = concepts;
        Terms = terms;
        Pairs = pairs;
        ContentWeight = contentWeight;
        ContentMask = contentMask;
        SoftMask = softMask;
    }

    public static GuideQuery Empty { get; } = new([], [], [], 0, 0, 0);

    /// <summary>The concepts in the question's order (added soft ones last).</summary>
    public Concept[] Concepts { get; }
    /// <summary>The guide terms to score, each once, with its query weight (soft ones count less).</summary>
    public (int Term, double Weight)[] Terms { get; }
    /// <summary>Neighbouring content concepts in the question, for phrase matches; Compound is a term joining them or -1.</summary>
    public (int A, int B, int Compound)[] Pairs { get; }
    /// <summary>The summed weight of the content concepts (the share a chunk covers is out of this).</summary>
    public double ContentWeight { get; }
    /// <summary>The bits of the content concepts.</summary>
    public int ContentMask { get; }
    /// <summary>The bits of the soft concepts that the guide has.</summary>
    public int SoftMask { get; }
    public bool IsEmpty => ContentMask == 0;

    /// <summary>One word of the question: the guide terms that satisfy it, its weight (the term's IDF, or
    /// <see cref="GuideRanking.UnknownWeight"/> × the reference IDF when the guide doesn't have it), how informative a match in a
    /// chunk's text is (from 0 to 1), whether it is soft, whether it is an everyday chat word, and whether it is question
    /// scaffolding standing in for a question with nothing else in it (weak even in a heading).</summary>
    public sealed record Concept(string Stem, int[] Terms, double Weight, double Informative, bool Soft, bool Common, bool Weak);

    /// <summary>The question's concepts for an index with <paramref name="vocabulary"/>.</summary>
    /// <param name="idf">A term's IDF in the guide.</param>
    /// <param name="referenceIdf">The IDF of a term in about one chunk in twenty: a matched word at least this rare is fully
    /// informative.</param>
    /// <param name="appWords">The app's names as <see cref="SearchTerms.Words"/> (each name a sequence).</param>
    public static GuideQuery Parse(string? question, IReadOnlyDictionary<string, int> vocabulary, Func<int, double> idf,
        double referenceIdf, IReadOnlyList<string[]> appWords, GuideRanking ranking)
    {
        var words = SearchTerms.Words(question);
        if (words.Count > MaximumWords) words = [.. words.Take(MaximumWords)];
        var nameWord = new bool[words.Count];
        var nameTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in appWords)
        {
            foreach (var w in name)
                if (SearchTerms.Term(w) is { } t) nameTerms.Add(t);
            var joined = string.Concat(name);
            for (var i = 0; i < words.Count; i++)
            {
                if (words[i] == joined) nameWord[i] = true;
                if (name.Length < 2 || i + name.Length > words.Count) continue;
                var all = true;
                for (var j = 0; j < name.Length && all; j++) all = words[i + j] == name[j];
                if (all)
                    for (var j = 0; j < name.Length; j++) nameWord[i + j] = true;
            }
        }

        // The words that remain, each with its term; null marks a break (a grammar word, filler or the app's name), so that only
        // words written next to each other make a phrase or a compound.
        var kept = new List<(string Word, string Term, bool Soft)?>();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (nameWord[i] || Filler.Contains(word) || SearchTerms.Term(word) is not { } term)
            {
                if (kept.Count > 0 && kept[^1] is not null) kept.Add(null);
                continue;
            }
            if (!vocabulary.ContainsKey(term) && Split(word, vocabulary) is { } split)
            {
                kept.Add((split.Left, SearchTerms.Term(split.Left)!, false));
                kept.Add((split.Right, SearchTerms.Term(split.Right)!, false));
                continue;
            }
            kept.Add((word, term, Scaffolding.Contains(term) || nameTerms.Contains(term)));
        }

        var stems = new List<string>();
        var softs = new List<bool>();
        var alternatives = new List<List<int>>();
        var order = new List<int>(kept.Count); // the concept of each kept word, -1 for a break
        foreach (var item in kept)
        {
            if (item is not { } k) { order.Add(-1); continue; }
            var index = stems.IndexOf(k.Term);
            if (index < 0)
            {
                if (stems.Count >= MaximumConcepts) { order.Add(-1); continue; }
                index = stems.Count;
                stems.Add(k.Term);
                softs.Add(k.Soft);
                var ids = new List<int>();
                if (vocabulary.TryGetValue(k.Term, out var id)) ids.Add(id);
                foreach (var variant in Variants.GetValueOrDefault(k.Term) ?? [])
                    if (vocabulary.TryGetValue(variant, out var vid) && !ids.Contains(vid)) ids.Add(vid);
                alternatives.Add(ids);
            }
            else softs[index] &= k.Soft;
            order.Add(index);
        }
        if (stems.Count == 0) return Empty;

        // Two neighbouring words the guide writes as one ("fire ball" → "fireball") satisfy each other's concept.
        var pairs = new List<(int A, int B, int Compound)>();
        for (var i = 0; i + 1 < kept.Count; i++)
        {
            if (kept[i] is not { } a || kept[i + 1] is not { } b) continue;
            int ca = order[i], cb = order[i + 1];
            if (ca < 0 || cb < 0 || ca == cb) continue;
            var compound = SearchTerms.Term(a.Word + b.Word) is { } joined && vocabulary.TryGetValue(joined, out var cid) ? cid : -1;
            if (compound >= 0)
            {
                if (!alternatives[ca].Contains(compound)) alternatives[ca].Add(compound);
                if (!alternatives[cb].Contains(compound)) alternatives[cb].Add(compound);
            }
            pairs.Add((ca, cb, compound));
        }

        // Only soft words ("how do I use it", or only a part of the app's name): they are what the question is about, but
        // scaffolding alone ("you're the best") is weak evidence even in a heading.
        var promoted = !softs.Contains(false);
        if (promoted)
            for (var i = 0; i < softs.Count; i++) softs[i] = false;

        // "Where is X?" asks for its Locations section; "who sells X?" for whoever sells it.
        foreach (var hint in Hints(words))
        {
            if (stems.Count >= MaximumConcepts) break;
            if (stems.Contains(hint) || !vocabulary.TryGetValue(hint, out var id)) continue;
            stems.Add(hint);
            softs.Add(true);
            alternatives.Add([id]);
        }

        var concepts = new Concept[stems.Count];
        var terms = new Dictionary<int, double>();
        double contentWeight = 0;
        int contentMask = 0, softMask = 0;
        for (var i = 0; i < stems.Count; i++)
        {
            var ids = alternatives[i].ToArray();
            // Any of a concept's terms satisfies it, so it weighs as much as its most common one.
            var weight = ids.Length == 0 ? referenceIdf * ranking.UnknownWeight : ids.Min(idf);
            var informative = ids.Length == 0 ? 0 : Math.Min(1, ids.Max(idf) / referenceIdf);
            var weak = promoted && !softs[i] && Scaffolding.Contains(stems[i]);
            concepts[i] = new(stems[i], ids, weight, informative, softs[i], weak || Everyday.Contains(stems[i]), weak);
            if (softs[i])
            {
                if (ids.Length > 0) softMask |= 1 << i;
            }
            else
            {
                contentWeight += weight;
                contentMask |= 1 << i;
            }
            foreach (var id in ids)
                terms[id] = Math.Max(terms.GetValueOrDefault(id), softs[i] ? ranking.SoftWeight : 1);
        }
        if (contentMask == 0) return Empty;
        var contentPairs = pairs.Where(p => !softs[p.A] && !softs[p.B]).Distinct().ToArray();
        return new(concepts, [.. terms.Select(t => (t.Key, t.Value))], contentPairs, contentWeight, contentMask, softMask);
    }

    /// <summary>The concept bits a guide term satisfies.</summary>
    public int MaskOf(int term)
    {
        var mask = 0;
        for (var i = 0; i < Concepts.Length; i++)
            if (Array.IndexOf(Concepts[i].Terms, term) >= 0) mask |= 1 << i;
        return mask;
    }

    private static IEnumerable<string> Hints(IReadOnlyList<string> words)
    {
        if (words.Contains("where") || words.Contains("wheres"))
        {
            yield return "location";
            yield return "find";
            yield return "spawn";
        }
        if (words.Contains("who") || words.Contains("whos")) yield return "sell";
    }

    // "greatsword" when the guide writes "great sword": the split into two words the guide has, the longer first part first.
    private static (string Left, string Right)? Split(string word, IReadOnlyDictionary<string, int> vocabulary)
    {
        if (word.Length < 6) return null;
        foreach (var c in word)
            if (c is < 'a' or > 'z') return null;
        for (var cut = word.Length - 3; cut >= 3; cut--)
        {
            string left = word[..cut], right = word[cut..];
            if (SearchTerms.Term(left) is { } l && SearchTerms.Term(right) is { } r && vocabulary.ContainsKey(l) && vocabulary.ContainsKey(r))
                return (left, right);
        }
        return null;
    }

    private static HashSet<string> Stems(params string[] words)
    {
        var stems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
            if (SearchTerms.Term(word) is { } term) stems.Add(term);
        return stems;
    }

    private static Dictionary<string, string[]> Groups(params string[][] groups)
    {
        var variants = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var stems = Stems(group);
            foreach (var stem in stems) variants[stem] = [.. stems.Where(s => s != stem)];
        }
        return variants;
    }
}
