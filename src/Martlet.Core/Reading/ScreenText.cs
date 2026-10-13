using System.Text;

namespace Martlet.Core.Reading;

/// <summary>One line of text read from a screenshot and where it is, in that screenshot's pixels.</summary>
public sealed record ReadLine(string Text, int X, int Y, int Width, int Height);

/// <summary>The lines of a read that count after <see cref="ScreenText.Settle"/> (in reading order), and how many lines wait for
/// the next read to come or go.</summary>
public sealed record SettledText(IReadOnlyList<ReadLine> Lines, int Pending);

/// <summary>What Martlet does with the text it reads on the screen: puts the lines in reading order, takes out OCR's flicker
/// (<see cref="Settle"/>), measures how much the text changed since the last read (a new trigger for a look) and writes the
/// text for a look's message.</summary>
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
    public static double Change(string? before, string? after) => Change(Words(before), Words(after));

    private static double Change(IReadOnlySet<string> old, IReadOnlySet<string> now)
    {
        if (old.Count == 0 && now.Count == 0) return 0;
        var shared = old.Count(now.Contains);
        return 1 - (double)shared / (old.Count + now.Count - shared);
    }

    /// <summary>When the words change this much (0 to 1) from one read to the next, the screen shows something else (another
    /// window): a line that counted and is gone from its place goes at once.</summary>
    public const double SettleKeepBelow = 0.5;

    /// <summary>
    /// The lines of the newest read that count, without OCR's flicker (hysteresis, docs/READING.md). OCR can misread a
    /// character for one read ("App1e" for "Apple"); that must not count as new text. A line of <paramref name="newest"/>
    /// counts when the read before (<paramref name="before"/>) had it too, or it counted already (<paramref name="settled"/>,
    /// the lines that counted at the read before). A line has it when the text is the same, or when all its words are there
    /// (OCR can split or join lines). A line whose place showed a different text in each of the last three reads
    /// (<paramref name="earlier"/> is the read before <paramref name="before"/>) counts as it is: a live value such as a
    /// timer. A line that counted and is missing from only the newest read still counts for one read: when its place now
    /// shows a text that waits and is almost the same (a misread or a small change), or shows nothing while the words changed
    /// less than <see cref="SettleKeepBelow"/>. A first read (<paramref name="before"/> null) has no line that counts yet.
    /// </summary>
    public static SettledText Settle(IReadOnlyList<ReadLine> newest, IReadOnlyList<ReadLine>? before,
        IReadOnlyList<ReadLine>? settled, IReadOnlyList<ReadLine>? earlier = null)
    {
        ArgumentNullException.ThrowIfNull(newest);
        if (before is null) return new([], newest.Count);
        var counted = settled ?? [];
        var (inBefore, inSettled, inNewest) = (new LineSet(before), new LineSet(counted), new LineSet(newest));
        var lines = new List<ReadLine>();
        var waiting = new List<ReadLine>();
        foreach (var line in newest)
            (inBefore.Has(line) || inSettled.Has(line) || Live(line, before, earlier) ? lines : waiting).Add(line);
        var same = Change(inBefore.AllWords, inNewest.AllWords) < SettleKeepBelow;
        var kept = counted.Where(line => !inNewest.Has(line) && inBefore.Has(line) && At(newest, line) switch
        {
            null => same,
            var there => waiting.Contains(there) && Near(there.Text, line.Text)
        }).ToList();
        return new(Order([.. lines, .. kept]), waiting.Count + kept.Count);
    }

    // Two texts are almost the same: at most a few characters differ (two, or a quarter of the shorter).
    private static bool Near(string a, string b)
    {
        var most = Math.Max(2, Math.Min(a.Length, b.Length) / 4);
        if (Math.Abs(a.Length - b.Length) > most) return false;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1];
            next[0] = i;
            for (var j = 1; j <= b.Length; j++)
                next[j] = Math.Min(Math.Min(next[j - 1], row[j]) + 1, row[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            row = next;
        }
        return row[b.Length] <= most;
    }

    // The place of the line showed a different text in each of the last three reads.
    private static bool Live(ReadLine line, IReadOnlyList<ReadLine> before, IReadOnlyList<ReadLine>? earlier) =>
        earlier is not null && At(before, line) is { } last && last.Text != line.Text &&
        At(earlier, line) is { } first && first.Text != line.Text && first.Text != last.Text;

    // The line of a read at the same place as the given line: their boxes overlap by at least half a line's height.
    private static ReadLine? At(IReadOnlyList<ReadLine> lines, ReadLine line) => lines.FirstOrDefault(other =>
        Math.Min(other.X + other.Width, line.X + line.Width) > Math.Max(other.X, line.X) &&
        Math.Min(other.Y + other.Height, line.Y + line.Height) - Math.Max(other.Y, line.Y) >= Math.Min(other.Height, line.Height) / 2.0);

    // The texts and words of a read, to find whether it has a line.
    private sealed class LineSet
    {
        private readonly HashSet<string> texts;

        internal LineSet(IReadOnlyList<ReadLine> lines)
        {
            texts = new(lines.Select(line => line.Text), StringComparer.Ordinal);
            AllWords = ScreenText.Words(string.Join('\n', texts));
        }

        internal IReadOnlySet<string> AllWords { get; }

        internal bool Has(ReadLine line) =>
            texts.Contains(line.Text) || ScreenText.Words(line.Text) is { Count: > 0 } words && words.All(AllWords.Contains);
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length <= MaximumLineCharacters ? clean : clean[..MaximumLineCharacters].TrimEnd();
    }
}
