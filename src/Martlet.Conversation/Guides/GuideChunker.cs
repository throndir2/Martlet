using System.Text;

namespace Martlet.Conversation.Guides;

/// <summary>Cuts a guide's pages into chunks a search can return whole (docs/APP_GUIDES.md, "Searching a guide"):
/// <list type="bullet">
/// <item>a new chunk at each heading, so a section stays together, with the headings above it as the chunk's section;</item>
/// <item>blocks (lines: paragraphs, list items, table rows) gathered until a chunk would pass <see cref="MaximumCharacters"/>; a
/// block is cut only when it alone is longer, at a sentence end or else a space;</item>
/// <item>a section too short to be a chunk ("## Price" with "7 gold") joins the chunk before it (or else the next one) as
/// "Price: 7 gold" instead of being lost;</item>
/// <item>when a long section goes on in a next chunk, that chunk starts with the last sentence of the one before (at most
/// <see cref="OverlapCharacters"/>), or with the table's header row when it goes on in a table, so words that belong together
/// stay findable together.</item>
/// </list></summary>
public static class GuideChunker
{
    public const int MaximumCharacters = 1_200;
    /// <summary>Text shorter than this (a heading alone, a lone link) isn't a chunk of its own.</summary>
    public const int MinimumCharacters = 40;
    /// <summary>The longest end of a chunk that the next chunk of the same section repeats.</summary>
    public const int OverlapCharacters = 160;

    public static IReadOnlyList<GuideChunk> Chunk(GuidePage page, int maximumCharacters = MaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(page);
        var builder = new Builder(page, Math.Max(2 * MinimumCharacters, maximumCharacters));
        foreach (var raw in (page.Text ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (Heading(line) is { } heading) builder.Heading(heading);
            else builder.Line(line);
        }
        return builder.Finish();
    }

    /// <summary>A guide made from what reading up found: every page's chunks, its sources and the sites read.</summary>
    public static AppGuide Guide(string key, string name, GuideBuildOutcome outcome, DateTimeOffset builtAt)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new(key, name, outcome.Sites, builtAt, outcome.Pages.Select(p => new GuideSource(p.Url, p.Title, p.Bytes)).ToArray(),
            outcome.Pages.SelectMany(p => Chunk(p)).ToArray());
    }

    private sealed class Builder(GuidePage page, int maximum)
    {
        private readonly List<GuideChunk> chunks = [];
        private readonly List<(int Level, string Text)> headings = [];
        private readonly StringBuilder text = new();
        private int contextLength; // the start of text that repeats earlier text (overlap or a table header)
        private bool sectionHasChunk;
        private string? tail; // the last sentence of the chunk before, in this section
        private string? tableHeader;
        private bool inTable;
        private string? pending; // a short section waiting for the next chunk

        private string Section => string.Join(" › ", headings.Select(h => h.Text));

        public void Heading((int Level, string Text) heading)
        {
            Flush();
            while (headings.Count > 0 && headings[^1].Level >= heading.Level) headings.RemoveAt(headings.Count - 1);
            headings.Add(heading);
            sectionHasChunk = false;
            tail = null;
            tableHeader = null;
            inTable = false;
        }

        public void Line(string line)
        {
            var row = line.StartsWith('|');
            if (row && IsTableRule(line)) return;
            if (row && !inTable) tableHeader = line;
            else if (!row) tableHeader = null;
            inTable = row;
            foreach (var piece in Pieces(line, maximum))
            {
                if (text.Length > contextLength && text.Length + 1 + piece.Length > maximum)
                {
                    Flush(continuing: true);
                    var context = row && tableHeader is not null && piece != tableHeader ? tableHeader : row ? null : tail;
                    if (context is not null && context.Length + 1 + piece.Length <= maximum)
                    {
                        text.Append(context).Append('\n');
                        contextLength = text.Length;
                    }
                }
                if (text.Length == 0 && pending is not null)
                {
                    if (pending.Length + 1 + piece.Length <= maximum) text.Append(pending).Append('\n');
                    else if (pending.Length >= MinimumCharacters) Add(pending);
                    pending = null;
                }
                text.Append(piece).Append('\n');
            }
        }

        public IReadOnlyList<GuideChunk> Finish()
        {
            Flush();
            if (pending is not null && pending.Length >= MinimumCharacters) Add(pending);
            return chunks;
        }

        private void Flush(bool continuing = false)
        {
            var body = text.ToString().Trim();
            var own = text.Length > contextLength ? text.ToString(contextLength, text.Length - contextLength).Trim() : "";
            text.Clear();
            contextLength = 0;
            if (own.Length == 0) return;
            if (body.Length >= MinimumCharacters)
            {
                Add(body);
                sectionHasChunk = true;
                tail = continuing ? LastSentence(own) : null;
                return;
            }
            // Too short alone: it joins the chunk before, or else waits for the next one, with its heading when it is a section
            // of its own ("Price: 7 gold").
            var labelled = !sectionHasChunk && headings.Count > 0 && (chunks.Count == 0 || chunks[^1].Section != Section)
                ? headings[^1].Text + ": " + own
                : own;
            if (chunks.Count > 0 && chunks[^1].Text.Length + 1 + labelled.Length <= maximum)
                chunks[^1] = chunks[^1] with { Text = chunks[^1].Text + "\n" + labelled };
            else if (pending is null || pending.Length + 1 + labelled.Length > maximum) pending = labelled;
            else pending += "\n" + labelled;
        }

        private void Add(string body) => chunks.Add(new(page.Url, page.Title, Section, body));

        private static string? LastSentence(string own)
        {
            var line = own[(own.LastIndexOf('\n') + 1)..];
            var end = line.Length - 2;
            var start = 0;
            for (var i = end; i > 0; i--)
                if (line[i] == ' ' && line[i - 1] is '.' or '!' or '?')
                {
                    start = i + 1;
                    break;
                }
            var sentence = line[start..].Trim();
            return sentence.Length is >= 20 and <= OverlapCharacters ? sentence : null;
        }
    }

    // "|---|:---:|" between a table's header and its rows: no words, so it isn't kept.
    private static bool IsTableRule(string line)
    {
        foreach (var c in line)
            if (c is not ('|' or '-' or ':' or ' ' or '+' or '=')) return false;
        return true;
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
