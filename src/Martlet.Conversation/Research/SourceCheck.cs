using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>What a source check said about one claim.</summary>
public enum SourceVerdict { Supported, Partly, NotSupported, Unchecked }

/// <summary>One sentence of a report that cites pages (<paramref name="Pages"/>, numbered as in the report) with the parts of
/// those pages that match it best (<paramref name="Passages"/>).</summary>
public sealed record CitedClaim(int Number, string Sentence, IReadOnlyList<int> Pages, string Passages);

/// <summary>A claim and what the check said about it (<paramref name="Why"/>: a few words after PARTLY or NOT SUPPORTED).</summary>
public sealed record CheckedClaim(CitedClaim Claim, SourceVerdict Verdict, string? Why = null);

/// <summary>What a source check found: every claim it looked at, the model requests it made and the last problem, if any.</summary>
public sealed record SourceCheckResult(IReadOnlyList<CheckedClaim> Claims, int Requests, string? Problem = null)
{
    public int Supported => Claims.Count(c => c.Verdict == SourceVerdict.Supported);
    public int Partly => Claims.Count(c => c.Verdict == SourceVerdict.Partly);
    public int NotSupported => Claims.Count(c => c.Verdict == SourceVerdict.NotSupported);
    public int Unchecked => Claims.Count(c => c.Verdict == SourceVerdict.Unchecked);
    public IReadOnlyList<CheckedClaim> Problems => [.. Claims.Where(c => c.Verdict is SourceVerdict.Partly or SourceVerdict.NotSupported)];
}

/// <summary>The source check of a research report, after the report is ready, so it never holds the result back: the report is
/// kept and brought up first, then a quiet <see cref="KindName"/> job on the Thinking pool (a research job, so any member that
/// does research, also a paired computer, can take it) checks each cited sentence against the parts of its pages that match it
/// best, a few claims a request, and adds a <see cref="SectionHeading"/> section to the kept report. Its note only comes with
/// the user's next message.</summary>
public static class SourceCheck
{
    /// <summary>The job kind: sourcecheck-1...</summary>
    public const string KindName = "sourcecheck";
    /// <summary>The most claims a report's check looks at, how many go in one request, and the characters of a claim and of
    /// the page parts shown with it.</summary>
    public const int MaxClaims = 24, BatchClaims = 6, SentenceCharacters = 400, PassageCharacters = 1_000;
    public const string SectionHeading = "## Source check";
    private const string SourcesHeading = "\n## Sources\n";

    /// <summary>One at a time, at most 30 minutes once it has a place (it waits in line for one), quiet: what it found comes with
    /// the user's next message, never as a reply of its own.</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 1, null, TimeSpan.FromMinutes(30), Doing: "Checking the sources of")
    {
        PoolKind = ThinkingJobKind.Research, Yields = true, Quiet = true
    };

    private static readonly Regex Sentence = new(@"(?<=[.!?])\s+(?=[\p{Lu}\d""“(\[])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Verdict = new(
        @"^\W*(\d+)\W*\s*[:.)\-]\s*\W*(NOT\s+SUPPORTED|UNSUPPORTED|PARTLY|PARTIALLY|SUPPORTED)\b\W*?(?:[:\-–—]\s*(.*))?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The report's sentences that cite its pages, in order, at most <paramref name="most"/>: each with the pages it
    /// cites and the parts of them that match it best. The source list and an earlier check are left out.</summary>
    public static IReadOnlyList<CitedClaim> Claims(ResearchReport report, int most = MaxClaims)
    {
        ArgumentNullException.ThrowIfNull(report);
        var claims = new List<CitedClaim>();
        foreach (var line in Body(report.Markdown).Split('\n'))
        {
            var text = line.Trim().TrimStart('#', '-', '*', '>', ' ');
            if (text.Length == 0) continue;
            string[] sentences;
            try { sentences = Sentence.Split(text); }
            catch (RegexMatchTimeoutException) { sentences = [text]; }
            foreach (var sentence in sentences)
            {
                var pages = ResearchWatcher.Cited(sentence, report.Sources.Count);
                if (pages.Count == 0) continue;
                var clean = WebResearch.Cut(sentence.Replace("**", "", StringComparison.Ordinal).Trim(), SentenceCharacters);
                var terms = WebResearch.Terms(WebResearch.Spoken(clean));
                var share = PassageCharacters / pages.Count;
                var passages = string.Join("\n", pages.Select(p => $"[{p}] " + Best(report.Sources[p - 1].Text, terms, share)));
                claims.Add(new(claims.Count + 1, clean, pages, passages));
                if (claims.Count >= most) return claims;
            }
        }
        return claims;
    }

    // The report's text before its source list and before an earlier check.
    private static string Body(string markdown)
    {
        var end = markdown.LastIndexOf(SourcesHeading, StringComparison.Ordinal);
        var body = end >= 0 ? markdown[..end] : markdown;
        var check = body.LastIndexOf("\n" + SectionHeading + "\n", StringComparison.Ordinal);
        return check >= 0 ? body[..check] : body;
    }

    /// <summary>The parts of <paramref name="text"/> with the most of <paramref name="terms"/>, in page order, within
    /// <paramref name="characters"/>; its start when nothing matches.</summary>
    public static string Best(string text, IReadOnlyCollection<string> terms, int characters)
    {
        if (text.Length <= characters) return WebResearch.Spoken(text);
        var pieces = new List<string>();
        foreach (var line in text.Split('\n'))
            for (var at = 0; at < line.Length; at += 300) pieces.Add(line.Substring(at, Math.Min(300, line.Length - at)));
        var scores = pieces.Select(piece => terms.Count(term => piece.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (terms.Count == 0 || scores.All(score => score == 0)) return WebResearch.Cut(text.Replace('\n', ' '), characters);
        var keep = new bool[pieces.Count];
        var used = 0;
        foreach (var best in Enumerable.Range(0, pieces.Count).Where(i => scores[i] > 0).OrderByDescending(i => scores[i]).ThenBy(i => i))
        {
            if (used + pieces[best].Length + 3 > characters) continue;
            keep[best] = true;
            used += pieces[best].Length + 3;
        }
        var parts = Enumerable.Range(0, pieces.Count).Where(i => keep[i]).Select(i => pieces[i].Trim());
        return string.Join(" … ", parts);
    }

    /// <summary>One request's task (Companion › Prompts › Web research: checking the sources): the claims, each with the pages
    /// it cites (titles and links from <paramref name="report"/>) and what they say.</summary>
    public static string Task(PromptSettings? prompts, ResearchReport report, IReadOnlyList<CitedClaim> batch)
    {
        var claims = new StringBuilder();
        foreach (var claim in batch)
            claims.Append(claim.Number).Append(". Claim: ").Append(claim.Sentence).Append('\n')
                .Append("Cites: ").Append(string.Join("; ", claim.Pages.Select(p => $"[{p}] {WebResearch.Cut(report.Sources[p - 1].Title, 100)}")))
                .Append('\n').Append("What the pages say: ").Append(claim.Passages).Append("\n\n");
        return PromptSettings.Fill(prompts, PromptCatalog.ResearchSourceCheck, ("claims", claims.ToString().TrimEnd()))!;
    }

    /// <summary>Reads a request's answer: one line a claim, "number: SUPPORTED", "number: PARTLY: why" or "number: NOT SUPPORTED:
    /// why". Claims it doesn't name stay unchecked.</summary>
    public static IReadOnlyDictionary<int, (SourceVerdict Verdict, string? Why)> Parse(string answer)
    {
        var verdicts = new Dictionary<int, (SourceVerdict, string?)>();
        try
        {
            foreach (Match match in Verdict.Matches(answer.Replace("\r\n", "\n", StringComparison.Ordinal)))
            {
                if (!int.TryParse(match.Groups[1].Value, out var number)) continue;
                var word = string.Join(' ', match.Groups[2].Value.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var verdict = word switch
                {
                    "SUPPORTED" => SourceVerdict.Supported,
                    "PARTLY" or "PARTIALLY" => SourceVerdict.Partly,
                    _ => SourceVerdict.NotSupported
                };
                var why = match.Groups[3].Success && match.Groups[3].Value.Trim().Trim('*').Trim() is { Length: > 0 } said
                    ? WebResearch.Cut(said, 200) : null;
                verdicts.TryAdd(number, (verdict, verdict == SourceVerdict.Supported ? null : why));
            }
        }
        catch (RegexMatchTimeoutException) { }
        return verdicts;
    }

    /// <summary>Checks <paramref name="report"/>'s cited sentences, <see cref="BatchClaims"/> a request through
    /// <paramref name="think"/>. A request that fails leaves its claims unchecked; two failures in a row end the check.</summary>
    public static async Task<SourceCheckResult> RunAsync(ResearchReport report, Func<string, CancellationToken, Task<BackgroundJobOutcome>> think,
        PromptSettings? prompts, CancellationToken token, BackgroundJob? job = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(think);
        var claims = Claims(report);
        var results = new List<CheckedClaim>();
        string? problem = null;
        int requests = 0, failures = 0;
        foreach (var batch in claims.Chunk(BatchClaims))
        {
            token.ThrowIfCancellationRequested();
            if (failures >= 2)
            {
                results.AddRange(batch.Select(claim => new CheckedClaim(claim, SourceVerdict.Unchecked)));
                continue;
            }
            job?.Report(BackgroundJobState.Running, $"Checking claims {batch[0].Number}-{batch[^1].Number} of {claims.Count}");
            requests++;
            var outcome = await think(Task(prompts, report, batch), token).ConfigureAwait(false);
            if (outcome.Result is not { } answer)
            {
                problem = outcome.Problem;
                failures++;
                results.AddRange(batch.Select(claim => new CheckedClaim(claim, SourceVerdict.Unchecked)));
                continue;
            }
            failures = 0;
            var verdicts = Parse(answer);
            results.AddRange(batch.Select(claim => verdicts.TryGetValue(claim.Number, out var said)
                ? new CheckedClaim(claim, said.Verdict, said.Why) : new CheckedClaim(claim, SourceVerdict.Unchecked)));
        }
        return new(results, requests, problem);
    }

    /// <summary>The report's Source check section: how many claims were checked and how each came out, then every claim its
    /// pages don't (fully) support, with why.</summary>
    public static string Section(SourceCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder(SectionHeading).Append('\n');
        if (result.Claims.Count == 0) return text.Append("No sentence of this report cites a page Martlet could check.").ToString();
        text.Append("Martlet checked ").Append(result.Claims.Count).Append(result.Claims.Count == 1 ? " claim" : " claims")
            .Append(" against the pages they cite after the report was ready: ").Append(Counts(result)).Append('.');
        foreach (var claim in result.Problems)
            text.Append("\n- ").Append(claim.Verdict == SourceVerdict.Partly ? "Only partly supported: " : "Not supported: ")
                .Append('"').Append(WebResearch.Spoken(claim.Claim.Sentence)).Append("\" (")
                .Append(string.Join(", ", claim.Claim.Pages.Select(p => $"[{p}]"))).Append(')')
                .Append(claim.Why is { } why ? ": " + why : "");
        return text.ToString();
    }

    private static string Counts(SourceCheckResult result)
    {
        if (result.Supported == result.Claims.Count) return "all supported";
        var parts = new List<string>();
        if (result.Supported > 0) parts.Add($"{result.Supported} supported");
        if (result.Partly > 0) parts.Add($"{result.Partly} only partly");
        if (result.NotSupported > 0) parts.Add($"{result.NotSupported} not supported");
        if (result.Unchecked > 0) parts.Add($"{result.Unchecked} not checked");
        return string.Join(", ", parts);
    }

    /// <summary>The report's Markdown with <paramref name="result"/>'s section before its source list (an earlier check is
    /// replaced).</summary>
    public static string Revised(string markdown, SourceCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var end = markdown.LastIndexOf(SourcesHeading, StringComparison.Ordinal);
        var (body, sources) = end >= 0 ? (markdown[..end], markdown[end..]) : (markdown, "");
        var earlier = body.LastIndexOf("\n" + SectionHeading + "\n", StringComparison.Ordinal);
        if (earlier >= 0) body = body[..earlier];
        return body.TrimEnd() + "\n\n" + Section(result) + "\n" + (sources.Length > 0 ? sources : "");
    }

    /// <summary>The job's note for the conversation (it comes with the user's next message): what the check found about the
    /// report <paramref name="title"/> (<paramref name="key"/>), and the claims that weren't supported, to mention only when they
    /// matter.</summary>
    public static string Note(SourceCheckResult result, string title, string? key)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder("Martlet checked the sources of the report \"").Append(title).Append('"')
            .Append(key is null ? "" : $" ({key})").Append(" after it was ready: ");
        if (result.Claims.Count == 0) return text.Append("it cites no page in a way Martlet could check. Nothing to say about it.").ToString();
        text.Append(result.Claims.Count).Append(" claims, ").Append(Counts(result)).Append('.')
            .Append(key is null ? "" : " The report now lists them under Source check.");
        if (result.Problems.Count == 0) return text.Append(" Nothing to say about it unless the user asks.").ToString();
        text.Append(" If the user asks about the report, or you told them something one of these changes, say so briefly:");
        foreach (var claim in result.Problems.Take(3))
            text.Append("\n- \"").Append(WebResearch.Cut(WebResearch.Spoken(claim.Claim.Sentence), 160)).Append("\": ")
                .Append(claim.Verdict == SourceVerdict.Partly ? "only partly supported" : "not supported")
                .Append(claim.Why is { } why ? $" ({why})" : "");
        return text.ToString();
    }
}
