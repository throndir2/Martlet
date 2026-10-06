using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What research was asked for: what to look up (as a web search would want it) and what the user wants to find out.</summary>
public sealed record ResearchArguments(string Topic, string Find);

/// <summary>A finished research report: its title, a one- or two-sentence summary, the report in Markdown (sources listed at the
/// end by Martlet itself) and the pages it read.</summary>
public sealed record ResearchReport(string Title, string Summary, string Markdown, IReadOnlyList<WebPage> Sources);

/// <summary>What a research job found: the report, or the problem in a few plain words.</summary>
public sealed record ResearchOutcome(ResearchReport? Report, string? Problem);

/// <summary>What one research step asked for: more searching, pages to read, or the report.</summary>
public sealed record ResearchStep(string? Search, IReadOnlyList<string> Read, string? Title, string? Summary, string? Report);

/// <summary>The caps of one research job: model steps, searches, pages, bytes downloaded and the page text a step carries.</summary>
public sealed record ResearchLimits(int Steps = 4, int Searches = 3, int Pages = 8, long Bytes = 3_000_000, int PagesFirstRead = 3,
    int ExcerptCharacters = 2_400, int SourceCharacters = 9_000);

/// <summary>Web research as background work (Companion › Deep thinking › Web research, off by default): research(topic,
/// what_to_find) starts a <see cref="KindName"/> job and returns at once; the job searches the web and reads pages with
/// <see cref="WebAccess"/> (or another <see cref="IWebSearch"/>/<see cref="IWebFetch"/>), and asks the model where Deep
/// thinking thinks, one bounded step at a time, whether to search again, read more or write the report
/// (<see cref="WebResearchRun"/>). The report is kept as a creation (<see cref="ResearchReports"/>) and offered when done.</summary>
public static class WebResearch
{
    public const string Name = "research";
    /// <summary>The background job kind: research-1, research-2...</summary>
    public const string KindName = "research";
    public const int MaxTopicCharacters = 300, MaxFindCharacters = 1_000;
    /// <summary>The most a step's task may take (UTF-8), leaving room in a background message's 16 KiB for its wrapper.</summary>
    public const int MaxTaskBytes = 13_000;

    /// <summary>One at a time, 4 an hour, 12 minutes each; offered when done (the report is shown on a yes).</summary>
    public static TimeSpan TimeLimit { get; } = TimeSpan.FromMinutes(12);

    public static BackgroundJobKind Kind { get; } = new(KindName, 1, 4, TimeLimit, Offer: true, Doing: "Researching");

    public const string ParametersJson =
        """{"type":"object","properties":{"topic":{"type":"string","description":"What to look up, as you'd type it into a web search."},"what_to_find":{"type":"string","description":"Exactly what the user wants to find out."}},"required":["topic","what_to_find"],"additionalProperties":false}""";

    public const string Description =
        "Look something up on the web in the background (a few minutes) and write a short report with sources. Only when the user " +
        "asks you to look something up, search for it or research it. Tell them first. One at a time.";

    public static TextToolDefinition Definition { get; } = new(Name, Description, ParametersJson);

    /// <summary>The prompt added to a reply's instructions while research is offered.</summary>
    public static string? Instructions(PromptSettings? prompts) => PromptSettings.Fill(prompts, PromptCatalog.WebResearch);

    public static (ResearchArguments? Arguments, string? Problem) Parse(string argumentsJson)
    {
        JsonObject? arguments = null;
        try { arguments = JsonNode.Parse(argumentsJson) as JsonObject; }
        catch (JsonException) { }
        string? Read(string name) => arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? Clean(text) : null;
        var topic = Read("topic");
        var find = Read("what_to_find");
        if (arguments is null || string.IsNullOrWhiteSpace(topic))
            return (null, "Pass one JSON object like {\"topic\": \"best hiking trails near Seattle\", \"what_to_find\": \"three easy trails " +
                "with their length and how to get there\"}.");
        if (topic.Length > MaxTopicCharacters) topic = topic[..MaxTopicCharacters];
        if (string.IsNullOrWhiteSpace(find)) find = topic;
        else if (find.Length > MaxFindCharacters) find = find[..MaxFindCharacters];
        return (new(topic, find), null);
    }

    private static string Clean(string text) =>
        new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();

    /// <summary>A few words for the talk window and the conversation: the topic, at most 60 characters (never logged).</summary>
    public static string Label(string topic)
    {
        var line = string.Join(' ', topic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "…";
    }

    public static string Started(BackgroundJob job, bool toldUser) =>
        JsonSerializer.Serialize(new { status = "started", id = job.Id, time_limit = BackgroundJobs.Duration(TimeLimit) }) + "\n" +
        (toldUser
            ? "You're looking it up in the background now. You already told the user, so add nothing more, or at most a few words."
            : "You're looking it up in the background now. Tell the user now, in one short sentence in character, that you'll look " +
              "into it and get back to them.") +
        " Don't make up what you'll find; a note brings the report when it's ready.";

    public static string Refused(BackgroundJobStart start) => start.Refusal switch
    {
        "busy" => $"Not started: {start.Message} Tell the user you're still looking into the other thing; try again after it's done.",
        "hourly_limit" => $"Not started: {start.Message} Tell the user you can't look anything else up for a while; answer from what you know, saying so.",
        _ => $"Not started: {start.Message ?? "it isn't available right now."} Answer from what you know instead, saying you couldn't look it up."
    };

    public const string TurnedOff =
        "Not started: web research is off (Companion › Deep thinking › Web research). Answer from what you know, and say you can't look things up.";

    public static string Unavailable(string why) =>
        $"Not started: Deep thinking can't run right now, so there's nothing to research with. {why} Answer from what you know instead.";

    /// <summary>The job's result for the conversation: the report's title and summary, how many sources, and how to show it
    /// (perform_creation with its ID) once the user says yes; or the summary alone when it couldn't be kept.</summary>
    public static string Ready(ResearchReport report, string? key) =>
        JsonSerializer.Serialize(new { status = "ready", report_id = key, sources = report.Sources.Count }) + "\n" +
        "Title: " + report.Title + "\nSummary: " + report.Summary + "\n" +
        (key is null
            ? "The full report couldn't be kept on this PC, so share the summary."
            : $"The full report with its sources is kept in Creations. Offer to show it; on a yes, call perform_creation with id \"{key}\".");

    // ---------- one step ----------

    /// <summary>The task of research step <paramref name="step"/> of <paramref name="steps"/> (Companion › Prompts › Web research:
    /// each step): what to research and what was found so far.</summary>
    public static string StepTask(PromptSettings? prompts, ResearchArguments arguments, string sources, int step, int steps) =>
        PromptSettings.Fill(prompts, PromptCatalog.ResearchStep, ("topic", arguments.Topic), ("find", arguments.Find),
            ("sources", sources), ("step", step.ToString(CultureInfo.InvariantCulture)), ("steps", steps.ToString(CultureInfo.InvariantCulture)),
            ("last", step >= steps ? "\nThis is the last step: write the report now." : ""))!;

    /// <summary>Reads a step's answer: SEARCH, READ lines, or the report (TITLE, SUMMARY, REPORT). An answer with none of them
    /// is taken as the report itself.</summary>
    public static ResearchStep ParseStep(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? search = null, title = null, summary = null;
        var reads = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimStart('*', '#', '-', ' ').Replace("**", "", StringComparison.Ordinal);
            if (Field(line, "REPORT") is { } first)
            {
                var report = string.Join('\n', (first.Length > 0 ? [first] : Array.Empty<string>()).Concat(lines.Skip(i + 1))).Trim();
                return new(null, [], title, summary, report.Length > 0 ? report : null);
            }
            if (Field(line, "TITLE") is { Length: > 0 } t) title = t;
            else if (Field(line, "SUMMARY") is { Length: > 0 } s) summary = s;
            else if (Field(line, "SEARCH") is { Length: > 0 } q) search ??= q;
            else if (Field(line, "READ") is { Length: > 0 } r && reads.Count < 3) reads.Add(r.Trim('<', '>', ' '));
        }
        if (search is not null || reads.Count > 0) return new(search, reads, null, null, null);
        var whole = text.Trim();
        return new(null, [], title, summary, whole.Length > 0 ? whole : null);

        static string? Field(string line, string name) =>
            line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) ? line[(name.Length + 1)..].Trim() : null;
    }

    /// <summary>What was found so far, numbered for the model to cite: the latest search's results, then the pages read (each
    /// an excerpt, together at most <see cref="ResearchLimits.SourceCharacters"/>).</summary>
    public static string Sources(IReadOnlyList<WebSearchResult> results, IReadOnlyList<WebPage> pages, IReadOnlyCollection<string> failed,
        ResearchLimits limits)
    {
        var text = new StringBuilder();
        if (results.Count > 0)
        {
            text.Append("Search results:\n");
            foreach (var result in results)
                text.Append("- ").Append(result.Title).Append(" <").Append(result.Url).Append(">\n  ").Append(Cut(result.Snippet, 240)).Append('\n');
        }
        if (pages.Count == 0) text.Append(results.Count == 0 ? "Nothing yet.\n" : "No pages read yet.\n");
        else
        {
            var share = Math.Max(150, Math.Min(limits.ExcerptCharacters, (limits.SourceCharacters - text.Length) / pages.Count));
            for (var i = 0; i < pages.Count; i++)
                text.Append("\n[").Append(i + 1).Append("] ").Append(pages[i].Title).Append(" <").Append(pages[i].Url).Append(">\n")
                    .Append(Cut(pages[i].Text, share)).Append('\n');
        }
        if (failed.Count > 0) text.Append("\nCouldn't read: ").Append(string.Join(", ", failed.Take(6))).Append('\n');
        return text.ToString().TrimEnd();
    }

    private static string Cut(string text, int characters) => text.Length <= characters ? text : text[..characters].TrimEnd() + "…";

    /// <summary>The report as kept: the model's report, then the pages read as numbered links (added by Martlet, so the sources
    /// are always there).</summary>
    public static ResearchReport Report(ResearchStep step, ResearchArguments arguments, IReadOnlyList<WebPage> pages)
    {
        var body = step.Report!.Trim();
        var title = step.Title is { Length: > 0 } t ? t : FirstHeading(body) ?? arguments.Topic;
        title = string.Join(' ', title.Trim('#', ' ', '"').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length > 100) title = title[..97].TrimEnd() + "…";
        if (title.Length == 0) title = "Research";
        var summary = step.Summary is { Length: > 0 } s ? s : FirstSentence(body);
        if (summary.Length > 400) summary = summary[..397].TrimEnd() + "…";
        if (body.Length > 12_000) body = body[..12_000].TrimEnd() + "…";
        var markdown = new StringBuilder("# ").Append(title).Append("\n\n").Append(body).Append("\n\n## Sources\n");
        if (pages.Count == 0) markdown.Append("No pages could be read; this comes from the search results only.\n");
        for (var i = 0; i < pages.Count; i++)
            markdown.Append(i + 1).Append(". [").Append(pages[i].Title.Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal))
                .Append("](").Append(pages[i].Url).Append(")\n");
        return new(title, summary, markdown.ToString().TrimEnd() + "\n", pages);
    }

    private static string? FirstHeading(string body) =>
        body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith('#'))?.TrimStart('#').Trim();

    private static string FirstSentence(string body)
    {
        var plain = string.Join(' ', body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        var end = plain.IndexOfAny(['.', '!', '?']);
        return end > 0 && end < 400 ? plain[..(end + 1)] : plain.Length > 400 ? plain[..397] + "…" : plain;
    }
}

/// <summary>One research job's loop, off the reply path: a first search for the topic and its top pages read, then up to
/// <see cref="ResearchLimits.Steps"/> model steps (each a fresh, bounded request where Deep thinking thinks, with what was found
/// so far), each asking for another search, pages to read or the report; the last step always writes the report. Searches,
/// pages and bytes are capped; the job's token ends it.</summary>
public sealed class WebResearchRun(IWebSearch search, IWebFetch fetch,
    Func<string, CancellationToken, Task<BackgroundJobOutcome>> think, PromptSettings? prompts, ResearchLimits? limits = null)
{
    private readonly ResearchLimits limits = limits ?? new();
    private readonly List<WebPage> pages = [];
    private readonly HashSet<string> tried = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> failed = [];
    private IReadOnlyList<WebSearchResult> results = [];

    public int Searches { get; private set; }
    public int Steps { get; private set; }
    public int Pages => pages.Count;
    public long Bytes { get; private set; }
    /// <summary>Pages that couldn't be read (blocked, too slow, not a page...).</summary>
    public int Failures => failed.Count;

    public async Task<ResearchOutcome> RunAsync(BackgroundJob job, ResearchArguments arguments, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(arguments);
        var first = await SearchAsync(job, arguments.Topic, token).ConfigureAwait(false);
        if (first is not null) return new(null, first);
        if (results.Count == 0) return new(null, "the web search found nothing for it");
        await ReadAsync(job, results.Select(r => r.Url), token, limits.PagesFirstRead).ConfigureAwait(false);
        string? lastProblem = null;
        var failures = 0;
        for (var step = 1; step <= limits.Steps; step++)
        {
            token.ThrowIfCancellationRequested();
            var final = step == limits.Steps;
            job.Report(BackgroundJobState.Running, final ? "Writing the report" : "Thinking it over");
            var task = TaskFor(arguments, step);
            Steps++;
            var outcome = await think(task, token).ConfigureAwait(false);
            if (outcome.Result is not { } answer)
            {
                lastProblem = outcome.Problem;
                // A model that failed twice won't do better a third time.
                if (++failures >= 2) break;
                continue;
            }
            var parsed = WebResearch.ParseStep(answer);
            if (parsed.Report is not null && (final || parsed.Search is null && parsed.Read.Count == 0))
                return new(WebResearch.Report(parsed, arguments, pages), null);
            if (final) break;
            var acted = false;
            if (parsed.Search is { } query && Searches < limits.Searches)
            {
                acted = await SearchAsync(job, query, token).ConfigureAwait(false) is null;
                if (acted) await ReadAsync(job, results.Select(r => r.Url), token, 2).ConfigureAwait(false);
            }
            if (parsed.Read.Count > 0) acted |= await ReadAsync(job, parsed.Read, token).ConfigureAwait(false) > 0;
            // Nothing new to look at: the next step is the last.
            if (!acted && step < limits.Steps - 1) step = limits.Steps - 1;
        }
        return new(null, lastProblem ?? "the model never wrote the report");
    }

    // The step's task within MaxTaskBytes (a background message must stay under 16 KiB): excerpts shrink until it fits, and
    // the sources are cut as a last resort.
    private string TaskFor(ResearchArguments arguments, int step)
    {
        var budget = limits;
        while (true)
        {
            var sources = WebResearch.Sources(results, pages, failed, budget);
            var task = WebResearch.StepTask(prompts, arguments, sources, step, limits.Steps);
            if (Encoding.UTF8.GetByteCount(task) <= WebResearch.MaxTaskBytes) return task;
            if (budget.SourceCharacters > 1_000)
            {
                budget = budget with { SourceCharacters = budget.SourceCharacters * 2 / 3, ExcerptCharacters = budget.ExcerptCharacters * 2 / 3 };
                continue;
            }
            var over = Encoding.UTF8.GetByteCount(task) - WebResearch.MaxTaskBytes;
            var keep = sources.Length;
            while (keep > 0 && Encoding.UTF8.GetByteCount(sources.AsSpan(0, keep)) > Encoding.UTF8.GetByteCount(sources) - over - 16) keep -= 64;
            if (keep > 0 && char.IsHighSurrogate(sources[keep - 1])) keep--;
            return WebResearch.StepTask(prompts, arguments, sources[..Math.Max(0, keep)] + "…", step, limits.Steps);
        }
    }

    // Searches the web (the first one's failure ends the job); null when it worked.
    private async Task<string?> SearchAsync(BackgroundJob job, string query, CancellationToken token)
    {
        job.Report(BackgroundJobState.Running, Searches == 0 ? "Searching the web" : "Searching again");
        Searches++;
        try
        {
            var found = await search.SearchAsync(query, token).ConfigureAwait(false);
            if (found.Count > 0 || Searches == 1) results = found;
            return null;
        }
        catch (WebResearchException error) { return "the web search didn't work (" + error.Message + ")"; }
    }

    // Reads pages not tried yet within the caps; returns how many were read.
    private async Task<int> ReadAsync(BackgroundJob job, IEnumerable<string> urls, CancellationToken token, int most = 3)
    {
        int read = 0, attempted = 0;
        foreach (var link in urls)
        {
            if (pages.Count >= limits.Pages || Bytes >= limits.Bytes || attempted >= most) break;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || !WebAccess.IsWeb(url) || !tried.Add(url.AbsoluteUri)) continue;
            attempted++;
            job.Report(BackgroundJobState.Running, $"Reading pages ({pages.Count + 1} of at most {limits.Pages})");
            try
            {
                var page = await fetch.FetchAsync(url, token).ConfigureAwait(false);
                Bytes += page.Bytes;
                if (page.Text.Length < 40) { failed.Add(url.Host); continue; }
                pages.Add(page);
                read++;
            }
            catch (WebResearchException) { failed.Add(url.Host); }
        }
        return read;
    }

    public override string ToString() => $"{nameof(WebResearchRun)} ({Searches} searches, {Pages} pages, {Steps} steps)";
}
