using System.Text;

namespace Martlet.Core.Reading;

/// <summary>One line of text read from a screenshot and where it is, in that screenshot's pixels.</summary>
public sealed record ReadLine(string Text, int X, int Y, int Width, int Height);

/// <summary>What Martlet does with the text it reads on the screen: puts the lines in reading order, measures how much the text
/// changed since the last read (a new trigger for a look) and writes the text for a look's message.</summary>
public static class ScreenText
{
    /// <summary>At most this many lines are kept from one read.</summary>
    public const int MaximumLines = 60;
    /// <summary>At most this many characters of a line are kept.</summary>
    public const int MaximumLineCharacters = 200;
    /// <summary>At most this many characters of text go with one look.</summary>
    public const int MaximumPromptCharacters = 1500;

    /// <summary>The lines in reading order: top to bottom (lines whose middles are within half a line's height are one row),
    /// then left to right. Control characters, blank lines and repeated lines are dropped, and each line is cut to
    /// <see cref="MaximumLineCharacters"/>.</summary>
    public static IReadOnlyList<ReadLine> Order(IEnumerable<ReadLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var clean = lines
            .Select(line => line with { Text = Clean(line.Text) })
            .Where(line => line.Text.Length > 0)
            .OrderBy(line => line.Y + line.Height / 2.0)
            .ToList();
        var rows = new List<List<ReadLine>>();
        foreach (var line in clean)
        {
            var middle = line.Y + line.Height / 2.0;
            var row = rows.Count == 0 ? null : rows[^1];
            if (row is not null && Math.Abs(middle - row.Average(l => l.Y + l.Height / 2.0)) <= Math.Max(row.Max(l => l.Height), line.Height) / 2.0)
                row.Add(line);
            else rows.Add([line]);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return rows.SelectMany(row => row.OrderBy(l => l.X)).Where(line => seen.Add(line.Text)).Take(MaximumLines).ToList();
    }

    /// <summary>The text of <paramref name="lines"/>, one line each, at most <paramref name="maximum"/> characters (whole lines;
    /// "…" ends text that was cut).</summary>
    public static string Join(IReadOnlyList<ReadLine> lines, int maximum = MaximumPromptCharacters)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (text.Length + line.Text.Length + 1 > maximum)
            {
                text.Append(text.Length == 0 ? "…" : "\n…");
                break;
            }
            if (text.Length > 0) text.Append('\n');
            text.Append(line.Text);
        }
        return text.ToString();
    }

    /// <summary>The words of <paramref name="text"/>, lower case: runs of letters or digits.</summary>
    public static IReadOnlySet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return words;
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) word.Append(char.ToLowerInvariant(c));
            else Flush();
        }
        Flush();
        return words;

        void Flush()
        {
            if (word.Length > 0) words.Add(word.ToString());
            word.Clear();
        }
    }

    /// <summary>How much the text changed from <paramref name="before"/> to <paramref name="after"/>: 0 when they have the same
    /// words (or both have none), 1 when they have none in common. A first read (<paramref name="before"/> null) of some text is 1.</summary>
    public static double Change(string? before, string? after)
    {
        var old = Words(before);
        var now = Words(after);
        if (old.Count == 0 && now.Count == 0) return 0;
        var shared = old.Count(now.Contains);
        return 1 - (double)shared / (old.Count + now.Count - shared);
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length <= MaximumLineCharacters ? clean : clean[..MaximumLineCharacters].TrimEnd();
    }
}
