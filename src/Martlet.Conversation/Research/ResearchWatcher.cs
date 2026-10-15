using System.Text.RegularExpressions;

namespace Martlet.Conversation;

/// <summary>Watches a research job's steps for the answer to what the user wants to find out, so Martlet can tell them as soon
/// as it is found instead of after the whole report. Every step that reads pages also says whether they answer it (FOUND), so
/// the watcher needs no model request of its own and adds no wait: it takes the first FOUND that is a real answer (not "none" or
/// "not yet") and cites a page that was read, keeps it and raises <see cref="Found"/> once. Steps of any thread of a job may feed
/// it; it is thread-safe.</summary>
public sealed class ResearchWatcher
{
    private static readonly Regex PageNumber = new(@"\[(\d+)(?:\s*[,;]\s*(\d+))*\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Number = new(@"\d+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly object gate = new();
    private ResearchFinding? finding;

    /// <summary>Raised once, on the thread of the step that found it, with the first answer that cites a page.</summary>
    public event Action<ResearchFinding>? Found;

    /// <summary>The first answer found, or null.</summary>
    public ResearchFinding? Finding { get { lock (gate) return finding; } }

    /// <summary>Looks at step <paramref name="step"/>'s answer with the <paramref name="pages"/> read so far (numbered from 1).
    /// Returns the finding when this step found it first, else null.</summary>
    public ResearchFinding? See(ResearchStep answer, IReadOnlyList<WebPage> pages, int step)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(pages);
        if (Answer(answer.Found) is not { } text) return null;
        var cited = Cited(text, pages.Count);
        if (cited.Count == 0) return null;
        ResearchFinding found;
        lock (gate)
        {
            if (finding is not null) return null;
            finding = found = new(text, [.. cited.Select(n => pages[n - 1])], step);
        }
        Found?.Invoke(found);
        return found;
    }

    /// <summary>The answer in a FOUND field, on one line, or null when it is empty or only says nothing is found yet.</summary>
    public static string? Answer(string? found)
    {
        if (found is null) return null;
        var text = string.Join(' ', found.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        var bare = text.Trim('.', '!', ' ', '<', '>', '-', '"', '(', ')').ToLowerInvariant();
        if (bare.Length < 3 || bare is "none" or "no" or "n/a" or "na" or "nothing" or "unknown" or "not found" or "not yet" ||
            bare.StartsWith("not yet", StringComparison.Ordinal) || bare.StartsWith("none yet", StringComparison.Ordinal) ||
            bare.StartsWith("nothing yet", StringComparison.Ordinal) || bare.StartsWith("no answer", StringComparison.Ordinal) ||
            bare.StartsWith("not found", StringComparison.Ordinal) || bare.StartsWith("unknown", StringComparison.Ordinal) ||
            bare.StartsWith("the pages don't", StringComparison.Ordinal) || bare.StartsWith("the pages do not", StringComparison.Ordinal))
            return null;
        return text;
    }

    /// <summary>The page numbers <paramref name="answer"/> cites ([2], [1, 3]) that are pages read (1 to <paramref name="pages"/>),
    /// in order, each once.</summary>
    public static IReadOnlyList<int> Cited(string answer, int pages)
    {
        var cited = new List<int>();
        try
        {
            foreach (Match match in PageNumber.Matches(answer))
                foreach (Match number in Number.Matches(match.Value))
                    if (int.TryParse(number.Value, out var n) && n >= 1 && n <= pages && !cited.Contains(n)) cited.Add(n);
        }
        catch (RegexMatchTimeoutException) { }
        return cited;
    }

    public override string ToString() => $"{nameof(ResearchWatcher)} (found: {Finding is not null})";
}
