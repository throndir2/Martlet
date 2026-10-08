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

/// <summary>What one research step answered: its notes (all that the pages read so far say, rewritten each step), and more
/// searching, pages to read, or the report.</summary>
public sealed record ResearchStep(string? Search, IReadOnlyList<string> Read, string? Title, string? Summary, string? Report,
    string? Notes = null);

/// <summary>The budget of one research job, about what a careful person reads and explores to research a subject thoroughly:
/// model steps, searches, pages, bytes downloaded, the pages read after the first search and after each step, and how much of a
/// step's task the notes, the new pages and one new page may take (characters).</summary>
public sealed record ResearchLimits(int Steps = 40, int Searches = 30, int Pages = 60, long Bytes = 25_000_000, int PagesFirstRead = 3,
    int PagesPerStep = 3, int NotesCharacters = 5_000, int FreshCharacters = 4_500, int ExcerptCharacters = 3_000);

/// <summary>Web research as background work (Companion › Deep thinking › Web research, on by default): research(topic,
/// what_to_find) starts a <see cref="KindName"/> job and returns at once; the job searches the web and reads pages with
/// <see cref="WebAccess"/> (or another <see cref="IWebSearch"/>/<see cref="IWebFetch"/>), and asks the model where Deep
/// thinking thinks, one bounded step at a time, to add what the new pages say to its notes and to search again, read more or
/// write the report (<see cref="WebResearchRun"/>), within <see cref="Budget"/>. The report is kept as a creation
/// (<see cref="ResearchReports"/>) and offered when done.</summary>
public static class WebResearch
{
    public const string Name = "research";
    /// <summary>The background job kind: research-1, research-2...</summary>
    public const string KindName = "research";
    public const int MaxTopicCharacters = 300, MaxFindCharacters = 1_000;
    /// <summary>The most a step's task may take (UTF-8), leaving room in a background message's 16 KiB for its wrapper.</summary>
    public const int MaxTaskBytes = 13_000;
    /// <summary>The most of a report kept (characters, before its sources).</summary>
    public const int MaxReportCharacters = 40_000;

    /// <summary>A job's budget: about what a careful person reads and explores to research a subject thoroughly.</summary>
    public static ResearchLimits Budget { get; } = new();

    /// <summary>One at a time, with no hourly limit and no time limit: a job runs until it has what it needs or has spent its
    /// <see cref="Budget"/> (or is canceled); offered when done (the report is shown on a yes).</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 1, null, null, Offer: true, Doing: "Researching")
    {
        PoolKind = ThinkingJobKind.Research, Yields = true
    };

    public const string ParametersJson =
        """{"type":"object","properties":{"topic":{"type":"string","description":"What to look up, as you'd type it into a web search."},"what_to_find":{"type":"string","description":"Exactly what the user wants to find out."}},"required":["topic","what_to_find"],"additionalProperties":false}""";

    public const string Description =
        "Research something on the web in the background, as thoroughly as a careful person would (it can take a while), and " +
        "write a report with sources. Only when the user asks you to look something up, search for it or research it. Tell them " +
        "first. One at a time.";

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
        JsonSerializer.Serialize(new { status = "started", id = job.Id, time_limit = "none" }) + "\n" +
        (toldUser
            ? "You're looking it up in the background now. You already told the user, so add nothing more, or at most a few words."
            : "You're looking it up in the background now. Tell the user now, in one short sentence in character, that you'll look " +
              "into it and get back to them, and that it may take a while.") +
        " Don't make up what you'll find; a note brings the report when it's ready.";

    public static string Refused(BackgroundJobStart start) => start.Refusal switch
    {
        "busy" => $"Not started: {start.Message} Tell the user you're still looking into the other thing; try again after it's done.",
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

    /// <summary>The task of research step <paramref name="step"/> of at most <paramref name="steps"/> (Companion › Prompts › Web
    /// research: each step): what to research, the notes so far and what is new since the last step.</summary>
    public static string StepTask(PromptSettings? prompts, ResearchArguments arguments, string notes, string sources, int step, int steps) =>
        PromptSettings.Fill(prompts, PromptCatalog.ResearchStep, ("topic", arguments.Topic), ("find", arguments.Find),
            ("notes", notes.Length > 0 ? notes : "None yet."), ("sources", sources), ("step", step.ToString(CultureInfo.InvariantCulture)),
            ("steps", steps.ToString(CultureInfo.InvariantCulture)),
            ("last", step >= steps ? "\nThis is the last step: write the report now." : ""))!;

    /// <summary>Reads a step's answer: NOTES (its lines up to the next field written in capitals), then SEARCH, READ lines, or
    /// the report (TITLE, SUMMARY, REPORT). An answer with none of them is taken as the report itself.</summary>
    public static ResearchStep ParseStep(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? search = null, title = null, summary = null;
        var reads = new List<string>();
        StringBuilder? notes = null;
        var noting = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimStart('*', '#', '-', ' ').Replace("**", "", StringComparison.Ordinal);
            // Inside the notes only a field in capitals ends them, so a note such as "- Title: ..." stays a note.
            var match = noting ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (Field(line, "REPORT", match) is { } first)
            {
                var report = string.Join('\n', (first.Length > 0 ? [first] : Array.Empty<string>()).Concat(lines.Skip(i + 1))).Trim();
                return new(null, [], title, summary, report.Length > 0 ? report : null, Written(notes));
            }
            if (Field(line, "NOTES", match) is { } note)
            {
                notes = new StringBuilder(note).Append('\n');
                noting = true;
                continue;
            }
            var field = true;
            if (Field(line, "TITLE", match) is { } t) title = t.Length > 0 ? t : title;
            else if (Field(line, "SUMMARY", match) is { } s) summary = s.Length > 0 ? s : summary;
            else if (Field(line, "SEARCH", match) is { } q) search ??= q.Length > 0 ? q : null;
            else if (Field(line, "READ", match) is { } r) { if (r.Length > 0 && reads.Count < 3) reads.Add(r.Trim('<', '>', ' ')); }
            else field = false;
            if (field) noting = false;
            else if (noting) notes!.Append(lines[i].TrimEnd()).Append('\n');
        }
        if (search is not null || reads.Count > 0 || notes is not null) return new(search, reads, title, summary, null, Written(notes));
        var whole = text.Trim();
        return new(null, [], title, summary, whole.Length > 0 ? whole : null);

        static string? Field(string line, string name, StringComparison match) =>
            line.StartsWith(name + ":", match) ? line[(name.Length + 1)..].Trim() : null;
        static string? Written(StringBuilder? notes) => notes?.ToString().Trim() is { Length: > 0 } written ? written : null;
    }

    /// <summary>What is new for the model since its last step, numbered for it to cite: the searches so far, how many pages were
    /// read (and when no more can be), the latest search's results not read yet, and the pages not in its notes yet (from
    /// <paramref name="noted"/> on), each an excerpt of at most <see cref="ResearchLimits.ExcerptCharacters"/>, together about
    /// <paramref name="fresh"/>. <paramref name="canRead"/>: the byte budget leaves room for another page.</summary>
    public static string Sources(IReadOnlyList<string> queries, IReadOnlyList<WebSearchResult> unread, IReadOnlyList<WebPage> pages, int noted,
        IReadOnlyList<string> failed, ResearchLimits limits, int fresh, bool canRead = true)
    {
        var full = !canRead || pages.Count >= limits.Pages;
        var text = new StringBuilder("Searches so far (").Append(queries.Count).Append(" of at most ").Append(limits.Searches)
            .Append(queries.Count >= limits.Searches ? ", no more" : "").Append("): ").Append(Cut(string.Join(" | ", queries.TakeLast(12)), 600))
            .Append("\nPages read: ").Append(pages.Count).Append(" of at most ").Append(limits.Pages)
            .Append(full ? ", no more can be read, so write the report." : ".");
        if (failed.Count > 0) text.Append(" Couldn't read ").Append(failed.Count).Append(": ").Append(string.Join(", ", failed.TakeLast(6))).Append('.');
        if (full) unread = [];
        text.Append(unread.Count == 0 ? "\n\nSearch results not read yet: none.\n" : "\n\nSearch results not read yet:\n");
        foreach (var result in unread)
        {
            text.Append("- ").Append(Cut(result.Title, 120)).Append(" <").Append(result.Url).Append(">\n");
            if (result.Snippet.Length > 0) text.Append("  ").Append(Cut(result.Snippet, 200)).Append('\n');
        }
        var count = pages.Count - noted;
        if (count <= 0) return text.Append("\nNew pages: none since your last step.").ToString();
        text.Append("\nNew pages to add to your notes:\n");
        var share = Math.Max(300, Math.Min(limits.ExcerptCharacters, fresh / count));
        for (var i = noted; i < pages.Count; i++)
            text.Append("\n[").Append(i + 1).Append("] ").Append(pages[i].Title).Append(" <").Append(pages[i].Url).Append(">\n")
                .Append(Cut(pages[i].Text, share)).Append('\n');
        return text.ToString().TrimEnd();
    }

    internal static string Cut(string text, int characters) => text.Length <= characters ? text : text[..characters].TrimEnd() + "…";

    /// <summary><paramref name="text"/> within <paramref name="characters"/>: its first lines, cut at the end of a line where it can be.</summary>
    public static string Clip(string text, int characters)
    {
        if (text.Length <= characters) return text;
        var end = text.LastIndexOf('\n', Math.Max(0, characters - 2));
        var cut = end > characters / 2 ? text[..end] : text[..Math.Max(0, characters - 2)];
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut.TrimEnd() + "\n…";
    }

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
        if (body.Length > MaxReportCharacters) body = body[..MaxReportCharacters].TrimEnd() + "…";
        var markdown = new StringBuilder("# ").Append(title).Append("\n\n").Append(body).Append("\n\n## Sources\n");
        if (pages.Count == 0) markdown.Append("No pages could be read; this comes from the search results only.\n");
        for (var i = 0; i < pages.Count; i++)
            markdown.Append(i + 1).Append(". [").Append(pages[i].Title.Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal))
                .Append("](").Append(pages[i].Url).Append(")\n");
        return new(title, summary, markdown.ToString().TrimEnd() + "\n", pages);
    }

    /// <summary>A report made of the notes, for a job whose model never wrote the report (it stopped working, or its last step
    /// gave only notes), so what it read isn't lost. <paramref name="step"/>'s title and summary are used when it gave them.</summary>
    public static ResearchReport FromNotes(string notes, ResearchArguments arguments, IReadOnlyList<WebPage> pages, ResearchStep? step = null) =>
        Report(new(null, [], step?.Title ?? arguments.Topic,
            step?.Summary ?? $"Only the notes from {pages.Count} pages: the full report couldn't be written.",
            "The full report couldn't be written, so these are the notes taken while reading.\n\n" + notes), arguments, pages);

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
/// <see cref="ResearchLimits.Steps"/> model steps, each a fresh, bounded request where Deep thinking thinks with the notes so far
/// and what is new since the last step (the latest results not read yet and the pages not in the notes yet). The model rewrites
/// its notes with what the new pages say, then asks for another search, pages to read or the report; the last step always
/// writes the report. The notes carry what was read from step to step, so a long job stays within one message. Searches, pages
/// and bytes are capped and the job's token ends it; a job whose model stops working keeps its notes as the report.</summary>
public sealed class WebResearchRun(IWebSearch search, IWebFetch fetch,
    Func<string, CancellationToken, Task<BackgroundJobOutcome>> think, PromptSettings? prompts, ResearchLimits? limits = null)
{
    // The least a step's task keeps of the new pages and of the notes (characters) before its sources are cut.
    private const int LeastFresh = 1_200, LeastNotes = 1_500;
    private readonly ResearchLimits limits = limits ?? WebResearch.Budget;
    private readonly List<WebPage> pages = [];
    private readonly HashSet<string> tried = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> failed = [];
    private readonly List<string> queries = [];
    private IReadOnlyList<WebSearchResult> results = [];
    private string notes = "";
    // The pages before this one are in the notes; the model hasn't taken notes on the rest yet.
    private int noted;

    public int Searches => queries.Count;
    public int Steps { get; private set; }
    public int Pages => pages.Count;
    public long Bytes { get; private set; }
    /// <summary>Pages that couldn't be read (blocked, too slow, not a page...).</summary>
    public int Failures => failed.Count;
    /// <summary>How long the notes are now (characters).</summary>
    public int NotesCharacters => notes.Length;

    public async Task<ResearchOutcome> RunAsync(BackgroundJob job, ResearchArguments arguments, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(arguments);
        var first = await SearchAsync(job, arguments.Topic, token).ConfigureAwait(false);
        if (first is not null) return new(null, first);
        if (results.Count == 0) return new(null, "the web search found nothing for it");
        await ReadAsync(job, results.Select(r => r.Url), token, limits.PagesFirstRead).ConfigureAwait(false);
        string? lastProblem = null;
        ResearchStep? answered = null;
        var failures = 0;
        for (var step = 1; step <= limits.Steps; step++)
        {
            token.ThrowIfCancellationRequested();
            var final = step == limits.Steps;
            job.Report(BackgroundJobState.Running, final ? "Writing the report" : "Thinking it over");
            var shown = pages.Count;
            var task = TaskFor(arguments, step);
            Steps++;
            var outcome = await think(task, token).ConfigureAwait(false);
            if (outcome.Result is not { } answer)
            {
                lastProblem = outcome.Problem;
                // A model that failed twice in a row won't do better a third time.
                if (++failures >= 2) break;
                continue;
            }
            failures = 0;
            var parsed = answered = WebResearch.ParseStep(answer);
            if (parsed.Report is not null && (final || parsed.Search is null && parsed.Read.Count == 0))
                return new(WebResearch.Report(parsed, arguments, pages), null);
            Note(parsed.Notes, shown);
            if (final) break;
            var before = pages.Count;
            if (parsed.Read.Count > 0) await ReadAsync(job, parsed.Read, token, limits.PagesPerStep).ConfigureAwait(false);
            var found = false;
            var query = parsed.Search is { } asked ? asked[..Math.Min(asked.Length, WebResearch.MaxTopicCharacters)] : null;
            // A search only helps while there are pages left to read.
            if (query is not null && Searches < limits.Searches && CanRead && !queries.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                var previous = results;
                if (await SearchAsync(job, query, token).ConfigureAwait(false) is null && !ReferenceEquals(results, previous))
                {
                    var room = limits.PagesPerStep - (pages.Count - before);
                    if (room > 0) await ReadAsync(job, results.Select(r => r.Url), token, room).ConfigureAwait(false);
                    found = CanRead && Unread().Count > 0;
                }
            }
            // Nothing new to look at: the next step is the last.
            if (pages.Count == before && !found && step < limits.Steps - 1) step = limits.Steps - 1;
        }
        // No report, but notes: what was read is kept.
        if (notes.Length > 0) return new(WebResearch.FromNotes(notes, arguments, pages, answered), null);
        return new(null, lastProblem ?? "the model never wrote the report");
    }

    // The model's notes replace the old ones; notes much shorter than the old ones are taken as only what the new pages add and
    // go after them. When it wrote none, each new page it saw goes in as its title and first words, so nothing read is lost.
    private void Note(string? written, int shown)
    {
        if (written is not null)
            notes = notes.Length >= 1_000 && written.Length < notes.Length / 3
                ? WebResearch.Clip(WebResearch.Clip(notes, Math.Max(LeastNotes, limits.NotesCharacters - written.Length - 2)) + "\n" + written,
                    limits.NotesCharacters)
                : WebResearch.Clip(written, limits.NotesCharacters);
        else if (shown > noted)
        {
            var text = new StringBuilder(notes);
            for (var i = noted; i < shown; i++)
                text.Append(text.Length > 0 ? "\n" : "").Append("- [").Append(i + 1).Append("] ").Append(pages[i].Title).Append(": ")
                    .Append(WebResearch.Cut(pages[i].Text, 300).Replace('\n', ' '));
            notes = WebResearch.Clip(text.ToString(), limits.NotesCharacters);
        }
        noted = Math.Max(noted, shown);
    }

    // The step's task within MaxTaskBytes (a background message must stay under 16 KiB): the new pages' excerpts shrink first,
    // then the notes it shows, and the sources are cut as a last resort.
    private string TaskFor(ResearchArguments arguments, int step)
    {
        var unread = Unread();
        var fresh = Math.Min(limits.FreshCharacters, Math.Max(1, pages.Count - noted) * limits.ExcerptCharacters);
        var shown = notes;
        while (true)
        {
            var sources = WebResearch.Sources(queries, unread, pages, noted, failed, limits, fresh, Bytes < limits.Bytes);
            var task = WebResearch.StepTask(prompts, arguments, shown, sources, step, limits.Steps);
            var over = Encoding.UTF8.GetByteCount(task) - WebResearch.MaxTaskBytes;
            if (over <= 0) return task;
            if (fresh > LeastFresh)
            {
                fresh = Math.Max(LeastFresh, fresh - over);
                continue;
            }
            if (shown.Length > LeastNotes)
            {
                shown = WebResearch.Clip(shown, Math.Max(LeastNotes, shown.Length - over));
                continue;
            }
            var keep = sources.Length;
            var target = Encoding.UTF8.GetByteCount(sources) - over - 16;
            while (keep > 0 && Encoding.UTF8.GetByteCount(sources.AsSpan(0, keep)) > target) keep = Math.Max(0, keep - 64);
            if (keep > 0 && char.IsHighSurrogate(sources[keep - 1])) keep--;
            return WebResearch.StepTask(prompts, arguments, shown, sources[..keep] + "…", step, limits.Steps);
        }
    }

    // The latest search's results not tried yet.
    private IReadOnlyList<WebSearchResult> Unread() =>
        [.. results.Where(r => Uri.TryCreate(r.Url, UriKind.Absolute, out var url) && !tried.Contains(url.AbsoluteUri))];

    // Whether the page and byte budgets leave room for another page.
    private bool CanRead => pages.Count < limits.Pages && Bytes < limits.Bytes;

    // Searches the web (the first one's failure ends the job); null when it worked.
    private async Task<string?> SearchAsync(BackgroundJob job, string query, CancellationToken token)
    {
        job.Report(BackgroundJobState.Running, queries.Count == 0 ? "Searching the web" : "Searching again");
        queries.Add(query);
        try
        {
            var found = await search.SearchAsync(query, token).ConfigureAwait(false);
            if (found.Count > 0 || queries.Count == 1) results = found;
            return null;
        }
        catch (WebResearchException error) { return "the web search didn't work (" + error.Message + ")"; }
    }

    // Reads up to `most` pages not tried yet within the caps; returns how many were read.
    private async Task<int> ReadAsync(BackgroundJob job, IEnumerable<string> urls, CancellationToken token, int most)
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
