using System.Text;

namespace Martlet.Conversation.Guides;

/// <summary>Cuts a guide's pages into chunks a search can return whole: a new chunk at each heading, and blocks gathered until a
/// chunk would pass <see cref="MaximumCharacters"/> (a longer block is cut at a sentence or word).</summary>
public static class GuideChunker
{
    public const int MaximumCharacters = 1_200;
    /// <summary>Text shorter than this (a heading alone, a lone link) isn't a chunk.</summary>
    public const int MinimumCharacters = 40;

    public static IReadOnlyList<GuideChunk> Chunk(GuidePage page, int maximumCharacters = MaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(page);
        var chunks = new List<GuideChunk>();
        var headings = new List<(int Level, string Text)>();
        var text = new StringBuilder();
        foreach (var raw in page.Text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (Heading(line) is { } heading)
            {
                Flush();
                while (headings.Count > 0 && headings[^1].Level >= heading.Level) headings.RemoveAt(headings.Count - 1);
                headings.Add(heading);
                continue;
            }
            foreach (var piece in Pieces(line, maximumCharacters))
            {
                if (text.Length > 0 && text.Length + 1 + piece.Length > maximumCharacters) Flush();
                if (text.Length > 0) text.Append('\n');
                text.Append(piece);
            }
        }
        Flush();
        return chunks;

        void Flush()
        {
            var body = text.ToString().Trim();
            text.Clear();
            if (body.Length < MinimumCharacters) return;
            chunks.Add(new(page.Url, page.Title, string.Join(" › ", headings.Select(h => h.Text)), body));
        }
    }

    /// <summary>A guide made from what reading up found: every page's chunks, its sources and the sites read.</summary>
    public static AppGuide Guide(string key, string name, GuideBuildOutcome outcome, DateTimeOffset builtAt)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new(key, name, outcome.Sites, builtAt, outcome.Pages.Select(p => new GuideSource(p.Url, p.Title, p.Bytes)).ToArray(),
            outcome.Pages.SelectMany(p => Chunk(p)).ToArray());
    }

    private static (int Level, string Text)? Heading(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        if (level is 0 or > 6 || level >= line.Length || line[level] != ' ') return null;
        var text = line[(level + 1)..].Trim();
        return text.Length == 0 ? null : (level, text.Length > 120 ? text[..120] : text);
    }

    // A block longer than a chunk, cut at sentence ends (else at a space).
    private static IEnumerable<string> Pieces(string line, int maximum)
    {
        while (line.Length > maximum)
        {
            var cut = line.LastIndexOfAny(['.', '!', '?'], maximum - 1, maximum / 2);
            if (cut < 0) cut = line.LastIndexOf(' ', maximum - 1, maximum / 2);
            if (cut < 0) cut = maximum - 1;
            yield return line[..(cut + 1)].Trim();
            line = line[(cut + 1)..].Trim();
        }
        if (line.Length > 0) yield return line;
    }
}
