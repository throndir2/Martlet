using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>How much looking up a question needs (the research tool's depth). <see cref="Quick"/>: one fact the user waits for,
/// a few searches and pages, and it stops as soon as the pages answer it. <see cref="Full"/>: research with a report, as a careful
/// person does it (the default). <see cref="Deep"/>: an in-depth report with about twice the budget, only when the user asks for
/// one.</summary>
public enum ResearchDepth { Quick, Full, Deep }

/// <summary>What research was asked for: what to look up (as a web search would want it), what the user wants to find out and
/// how deep to go.</summary>
public sealed record ResearchArguments(string Topic, string Find, ResearchDepth Depth = ResearchDepth.Full);

/// <summary>A finished research report: its title, a one- or two-sentence summary, the report in Markdown (sources listed at the
/// end by Martlet itself) and the pages it read.</summary>
public sealed record ResearchReport(string Title, string Summary, string Markdown, IReadOnlyList<WebPage> Sources);

/// <summary>What the research watcher saw a step find: the answer to what the user wants to find out (with the step's page
/// numbers like [2]), the pages it cites, the step that found it and whether the pages settle it (<paramref name="Settled"/>
/// false: a quick look-up's last step gave its best guess without a page that backs it).</summary>
public sealed record ResearchFinding(string Answer, IReadOnlyList<WebPage> Sources, int Step, bool Settled = true);

/// <summary>What a research job found: the report, the answer the watcher saw (a quick look-up ends with it), or the problem in a
/// few plain words.</summary>
public sealed record ResearchOutcome(ResearchReport? Report, string? Problem, ResearchFinding? Answer = null);

/// <summary>What one research step answered: its notes (all that the pages read so far say, rewritten each step), what it found
/// (FOUND: the answer once the pages give it), and more searching, pages to read, or the report.</summary>
public sealed record ResearchStep(string? Search, IReadOnlyList<string> Read, string? Title, string? Summary, string? Report,
    string? Notes = null, string? Found = null);

/// <summary>The budget of one research job, about what a careful person reads and explores to research a subject thoroughly:
/// model steps, searches, pages, bytes downloaded, the pages read after the first search and after each step, and how much of a
/// step's task the notes, the new pages and one new page may take (characters).</summary>
public sealed record ResearchLimits(int Steps = 40, int Searches = 30, int Pages = 60, long Bytes = 25_000_000, int PagesFirstRead = 3,
    int PagesPerStep = 3, int NotesCharacters = 15_000, int FreshCharacters = 15_000, int ExcerptCharacters = 10_000);

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
    /// <summary>The most of a topic (and of a search the model asks for) and of what to find out that is kept (characters).</summary>
    public const int MaxTopicCharacters = 3_000, MaxFindCharacters = 10_000;
    /// <summary>The most a step's task may take (UTF-8): about 13,000 tokens, which fits the Deep thinking window of a paired
    /// computer (32,768 tokens, less a High think's 16,384 for its answer). A place with less room gets less
    /// (<see cref="TaskBytes"/>).</summary>
    public const int MaxTaskBytes = 40_000;
    /// <summary>The least a step's task gets, whatever the place: it fits the smallest input any route keeps (6,144 tokens).</summary>
    public const int MinTaskBytes = 13_000;

    /// <summary>The most a step's task may take on a place whose request holds <paramref name="inputTokens"/> of text: about a
    /// token per three bytes, with 1,024 tokens kept for the step's wrapper, within <see cref="MinTaskBytes"/> and
    /// <see cref="MaxTaskBytes"/>.</summary>
    public static int TaskBytes(int inputTokens) => (int)Math.Clamp((inputTokens - 1_024L) * 3, MinTaskBytes, MaxTaskBytes);
    /// <summary>The most of a report kept (characters, before its sources): more than one step's answer can hold, so a report is
    /// kept whole; a longer one says where it was cut.</summary>
    public const int MaxReportCharacters = 400_000;
    /// <summary>What ends a report cut at <see cref="MaxReportCharacters"/>.</summary>
    public const string ReportCut = "\n\n*The report was cut here: it was longer than Martlet keeps.*";
    /// <summary>What follows a page's excerpt in a step's task when only its start is shown.</summary>
    public const string ExcerptCut = "\n[Only the start of this page is shown; it has more.]";
    /// <summary>What follows a source in the report when only the start of its page could be read.</summary>
    public const string SourceCut = " (only the start of the page was read: it was too long)";

    /// <summary>A job's budget: about what a careful person reads and explores to research a subject thoroughly
    /// (<see cref="ResearchDepth.Full"/>).</summary>
    public static ResearchLimits Budget { get; } = new();

    /// <summary>A quick look-up's budget: enough for one fact (a few searches, at most 9 pages and 4 steps); it ends at once when
    /// the watcher sees the answer.</summary>
    public static ResearchLimits QuickBudget { get; } = new(Steps: 4, Searches: 3, Pages: 9, Bytes: 8_000_000);

    /// <summary>Deep research's budget: about twice a full job's.</summary>
    public static ResearchLimits DeepBudget { get; } = new(Steps: 80, Searches: 60, Pages: 120, Bytes: 50_000_000);

    public static ResearchLimits BudgetFor(ResearchDepth depth) => depth switch
    {
        ResearchDepth.Quick => QuickBudget,
        ResearchDepth.Deep => DeepBudget,
        _ => Budget
    };

    /// <summary>Full and deep research: one at a time, with no hourly limit and no time limit: a job runs until it has what it
    /// needs or has spent its budget (or is canceled); offered when done (the report is shown on a yes).</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 1, null, null, Offer: true, Doing: "Researching")
    {
        PoolKind = ThinkingJobKind.Research, Yields = true
    };

    /// <summary>The quick look-up job kind: lookup-1, lookup-2...</summary>
    public const string LookupKindName = "lookup";

    /// <summary>Quick look-ups: two at a time beside full research, at most 10 minutes each, shared at once (not offered). The user
    /// waits for the answer, so a look-up goes before long work in the Thinking pool's line (the Thinking longer priority) on the
    /// places that do research.</summary>
    public static BackgroundJobKind LookupKind { get; } = new(LookupKindName, 2, null, TimeSpan.FromMinutes(10), Doing: "Looking up")
    {
        PoolKind = ThinkingJobKind.Research, Yields = true, PoolPriority = ThinkingPriority.ThinkLonger
    };

    /// <summary>The kind of job for <paramref name="depth"/>.</summary>
    public static BackgroundJobKind KindFor(ResearchDepth depth) => depth == ResearchDepth.Quick ? LookupKind : Kind;

    /// <summary>The notice kind of an early finding: finding-1...</summary>
    public const string FindingKindName = "finding";

    /// <summary>The notice that full or deep research already found what the user wanted (the watcher saw it): Martlet tells them
    /// as soon as it is free, or with what they say next, while the research goes on for the report.</summary>
    public static BackgroundJobKind FindingKind { get; } = new(FindingKindName, 4, 30, TimeSpan.FromMinutes(1), Doing: "Found out",
        Notice: true)
    {
        Wording = new(PromptCatalog.ResearchFound, PromptCatalog.ResearchFoundNotes, "findings")
    };

    public const string ParametersJson =
        """{"type":"object","properties":{"topic":{"type":"string","description":"What to look up, as you'd type it into a web search."},"what_to_find":{"type":"string","description":"Exactly what the user wants to find out."},"depth":{"type":"string","enum":["quick","full","deep"],"description":"quick: one fact, back in a minute or two. full (the default): research with a report and sources. deep: an in-depth report, only when the user asks for one."}},"required":["topic","what_to_find"],"additionalProperties":false}""";

    public const string Description =
        "Look something up on the web in the background: a quick look-up of one fact (depth quick), or research with a report " +
        "and sources (depth full, or deep when the user asks for an in-depth report). Tell the user first in a few words. A note " +
        "brings what it found.";

    public static TextToolDefinition Definition { get; } = new(Name, Description, ParametersJson);

    /// <summary>The prompt added to a reply's instructions while research is offered: when to answer from what Martlet knows,
    /// when to look something up quickly and when to research it.</summary>
    public static string? Instructions(PromptSettings? prompts) => SkillSettings.Guidance(prompts, SkillCatalog.WebResearch);

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
                "with their length and how to get there\", \"depth\": \"full\"}.");
        if (topic.Length > MaxTopicCharacters) topic = topic[..MaxTopicCharacters];
        if (string.IsNullOrWhiteSpace(find)) find = topic;
        else if (find.Length > MaxFindCharacters) find = find[..MaxFindCharacters];
        return (new(topic, find, Depth(Read("depth"))), null);
    }

    /// <summary>The depth a tool call asked for: quick, deep (also "in-depth") or full (anything else, or none).</summary>
    public static ResearchDepth Depth(string? depth) => depth?.Trim().ToLowerInvariant() switch
    {
        "quick" or "fast" or "lookup" or "look-up" => ResearchDepth.Quick,
        "deep" or "in-depth" or "indepth" => ResearchDepth.Deep,
        _ => ResearchDepth.Full
    };

    private static string Clean(string text) =>
        new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();

    /// <summary>A few words for the talk window and the conversation: the topic, at most 60 characters (never logged).</summary>
    public static string Label(string topic)
    {
        var line = string.Join(' ', topic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "…";
    }

    public static string Started(BackgroundJob job, bool toldUser, ResearchDepth depth = ResearchDepth.Full) =>
        JsonSerializer.Serialize(new
        {
            status = "started", id = job.Id, depth = depth.ToString().ToLowerInvariant(),
            time_limit = job.Kind.TimeLimit is { } limit ? BackgroundJobs.Duration(limit) : "none"
        }) + "\n" +
        (depth == ResearchDepth.Quick
            ? (toldUser
                ? "You're checking it in the background now. You already told the user, so add nothing more, or at most a few words."
                : "You're checking it in the background now. Tell the user now, in a few words in character, that you'll check.") +
              " Don't guess the answer meanwhile; a note brings it in a minute or two."
            : (toldUser
                ? "You're looking it up in the background now. You already told the user, so add nothing more, or at most a few words."
                : "You're looking it up in the background now. Tell the user now, in one short sentence in character, that you'll look " +
                  "into it and get back to them, and that it may take a while.") +
              " Don't make up what you'll find; a note brings what you found as soon as you find it, and the report when it's ready.");

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
    /// (perform_creation with its ID) once the user says yes; or the summary alone when it couldn't be kept.
    /// <paramref name="toldEarly"/>: the watcher's finding was already brought up, so the user knows the short answer.</summary>
    public static string Ready(ResearchReport report, string? key, bool toldEarly = false) =>
        JsonSerializer.Serialize(new { status = "ready", report_id = key, sources = report.Sources.Count }) + "\n" +
        "Title: " + report.Title + "\nSummary: " + report.Summary + "\n" +
        (key is null
            ? "The full report couldn't be kept on this PC, so share the summary."
            : $"The full report with its sources is kept in Creations. Offer to show it; on a yes, call perform_creation with id \"{key}\".") +
        (toldEarly ? " You already told the user the short answer when you found it: don't repeat it; if the report changes it, say so." : "");

    /// <summary>A quick look-up's result for the conversation: what it found (page numbers left out) and its sources, to tell
    /// the user now. A finding the pages didn't settle says so.</summary>
    public static string Answered(ResearchFinding finding)
    {
        var text = new StringBuilder(JsonSerializer.Serialize(new { status = finding.Settled ? "found" : "unsure", sources = finding.Sources.Count }))
            .Append('\n').Append(finding.Settled ? "What you found: " : "The pages didn't clearly settle it. The best they say: ")
            .Append(Spoken(finding.Answer)).Append('\n');
        if (finding.Sources.Count > 0)
            text.Append("Sources: ").Append(string.Join("; ", finding.Sources.Select(p => $"{Cut(p.Title, 80)} <{p.Url}>"))).Append('\n');
        return text.Append(finding.Settled
            ? "Tell the user now, briefly and in character, like \"Oh, so I found out that ...\". Don't read out links unless they ask. " +
              "If they want more, offer to research it fully."
            : "Tell the user plainly what you found and that you couldn't confirm it, and offer to research it fully.").ToString();
    }

    /// <summary>What a quick look-up that ended with a report or notes instead of an answer tells the conversation: the report's
    /// summary, which the pages didn't settle.</summary>
    public static ResearchFinding Unsettled(ResearchReport report) => new(report.Summary, [], 0, Settled: false);

    /// <summary>What the early-finding notice tells the conversation: which research (still going), what the user wanted, what
    /// was found (page numbers left out) and its sources.</summary>
    public static string FindingNote(string jobId, ResearchArguments arguments, ResearchFinding finding) =>
        $"While researching \"{Label(arguments.Topic)}\" ({jobId}, still going for the full report), you found what the user wanted " +
        $"to know ({Cut(arguments.Find, 300)}): {Spoken(finding.Answer)}" +
        (finding.Sources.Count > 0 ? " (from " + string.Join("; ", finding.Sources.Select(p => Cut(p.Title, 80))) + ")" : "");

    private static readonly System.Text.RegularExpressions.Regex PageNumbers = new(@"\s*\[\d+(?:\s*[,;–-]\s*\d+)*\]",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary><paramref name="answer"/> without its page numbers ([2], [1, 3]), on one line, for the conversation.</summary>
    public static string Spoken(string answer)
    {
        try { answer = PageNumbers.Replace(answer, ""); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
        return string.Join(' ', answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------- one step ----------

    /// <summary>The task of research step <paramref name="step"/> of at most <paramref name="steps"/> (Companion › Prompts › Web
    /// research: each step): what to research and how deep, the notes so far and what is new since the last step.</summary>
    public static string StepTask(PromptSettings? prompts, ResearchArguments arguments, string notes, string sources, int step, int steps) =>
        PromptSettings.Fill(prompts, PromptCatalog.ResearchStep, ("topic", arguments.Topic), ("find", arguments.Find),
            ("depth", DepthGuidance(arguments.Depth)),
            ("notes", notes.Length > 0 ? notes : "None yet."), ("sources", sources), ("step", step.ToString(CultureInfo.InvariantCulture)),
            ("steps", steps.ToString(CultureInfo.InvariantCulture)),
            ("last", step < steps ? "" : arguments.Depth == ResearchDepth.Quick
                ? "\nThis is the last step: give FOUND with the best answer the pages give, saying what they didn't settle."
                : "\nThis is the last step: write the report now."))!;

    /// <summary>How hard a step works for <paramref name="depth"/>: the {depth} of Web research: each step.</summary>
    public static string DepthGuidance(ResearchDepth depth) => depth switch
    {
        ResearchDepth.Quick => "This is a quick look-up: the user is waiting for one answer. Read only what you need, and give FOUND as " +
            "soon as the pages clearly answer it; that ends the look-up.",
        ResearchDepth.Deep => "Research it in depth, the way an expert would for a detailed report: cover every part of the question " +
            "from many angles, read primary sources, check that independent sources agree, and look for what is still missing, " +
            "disputed or uncertain.",
        _ => "Research it thoroughly, the way a careful person would: search from several angles, read the most useful pages, check " +
            "that independent sources agree, and look for what is still missing or uncertain."
    };

    /// <summary>Reads a step's answer: NOTES (its lines up to the next field written in capitals), FOUND (the answer, up to the
    /// next field), then SEARCH, READ lines, or the report (TITLE, SUMMARY, REPORT). An answer with none of them is taken as the
    /// report itself.</summary>
    public static ResearchStep ParseStep(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? search = null, title = null, summary = null;
        var reads = new List<string>();
        StringBuilder? notes = null, found = null;
        bool noting = false, finding = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimStart('*', '#', '-', ' ').Replace("**", "", StringComparison.Ordinal);
            // Inside the notes or the answer only a field in capitals ends them, so a note such as "- Title: ..." stays a note.
            var match = noting || finding ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (Field(line, "REPORT", match) is { } first)
            {
                var report = string.Join('\n', (first.Length > 0 ? [first] : Array.Empty<string>()).Concat(lines.Skip(i + 1))).Trim();
                return new(null, [], title, summary, report.Length > 0 ? report : null, Written(notes), Written(found));
            }
            if (Field(line, "NOTES", match) is { } note)
            {
                notes = new StringBuilder(note).Append('\n');
                noting = true;
                finding = false;
                continue;
            }
            if (Field(line, "FOUND", match) is { } answer)
            {
                found = new StringBuilder(answer).Append('\n');
                finding = true;
                noting = false;
                continue;
            }
            var field = true;
            if (Field(line, "TITLE", match) is { } t) title = t.Length > 0 ? t : title;
            else if (Field(line, "SUMMARY", match) is { } s) summary = s.Length > 0 ? s : summary;
            else if (Field(line, "SEARCH", match) is { } q) search ??= q.Length > 0 ? q : null;
            else if (Field(line, "READ", match) is { } r) { if (r.Length > 0 && reads.Count < 3) reads.Add(r.Trim('<', '>', ' ')); }
            else field = false;
            if (field) noting = finding = false;
            else if (noting) notes!.Append(lines[i].TrimEnd()).Append('\n');
            else if (finding) found!.Append(lines[i].Trim()).Append('\n');
        }
        if (search is not null || reads.Count > 0 || notes is not null || found is not null)
            return new(search, reads, title, summary, null, Written(notes), Written(found));
        var whole = text.Trim();
        return new(null, [], title, summary, whole.Length > 0 ? whole : null);

        static string? Field(string line, string name, StringComparison match) =>
            line.StartsWith(name + ":", match) ? line[(name.Length + 1)..].Trim() : null;
        static string? Written(StringBuilder? notes) => notes?.ToString().Trim() is { Length: > 0 } written ? written : null;
    }

    /// <summary>What is new for the model since its last step, numbered for it to cite: the searches so far, how many pages were
    /// read (and when no more can be), the latest search's results not read yet, and the pages not in its notes yet (from
    /// <paramref name="noted"/> on), each an excerpt of at most <see cref="ResearchLimits.ExcerptCharacters"/>, together about
    /// <paramref name="fresh"/>. <paramref name="canRead"/>: the byte budget leaves room for another page. With
    /// <paramref name="terms"/> (what the user wants, <see cref="Terms"/>) a long page shows its start and the parts that match
    /// them best (<see cref="Excerpt"/>); without, only its start.</summary>
    public static string Sources(IReadOnlyList<string> queries, IReadOnlyList<WebSearchResult> unread, IReadOnlyList<WebPage> pages, int noted,
        IReadOnlyList<string> failed, ResearchLimits limits, int fresh, bool canRead = true, IReadOnlyCollection<string>? terms = null)
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
        {
            var (excerpt, parts) = terms is { Count: > 0 } ? Excerpt(pages[i].Text, share, terms) : (Cut(pages[i].Text, share), false);
            text.Append("\n[").Append(i + 1).Append("] ").Append(pages[i].Title).Append(" <").Append(pages[i].Url).Append(">\n")
                .Append(excerpt).Append(parts ? PartsCut : pages[i].Text.Length > share || pages[i].Cut ? ExcerptCut : "").Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>What follows a page's excerpt in a step's task when it shows the start and the parts that match best.</summary>
    public const string PartsCut = "\n[Only the start of this page and the parts that match best are shown; it has more.]";

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "how", "what", "with", "that", "this", "are", "you", "your", "from", "which", "about", "does", "can",
        "into", "they", "them", "their", "there", "where", "when", "who", "why", "want", "wants", "know", "find", "out", "best", "most",
        "some", "any", "all", "get", "got", "use", "used", "has", "have", "had", "was", "were", "will", "would", "should", "could",
        "its", "it's", "not", "but", "one", "like", "more", "much", "many", "user", "users", "than", "then", "also", "just"
    };

    /// <summary>The words of what the user wants (the topic and what to find out) that pick a long page's parts: lower case, at
    /// least three letters or a number, without common words, at most 24.</summary>
    public static IReadOnlyCollection<string> Terms(ResearchArguments arguments) => Terms(arguments.Topic + " " + arguments.Find);

    /// <summary>The words of <paramref name="text"/> that pick a page's parts, as <see cref="Terms(ResearchArguments)"/>.</summary>
    public static IReadOnlyCollection<string> Terms(string text) =>
        [.. text.ToLowerInvariant()
            .Split([.. " \t\n.,;:!?()[]{}\"'/\\|<>*#+=&"], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('-', '\''))
            .Where(word => (word.Length >= 3 || word.Any(char.IsDigit)) && !Common.Contains(word)).Distinct().Take(24)];

    /// <summary>A long page within <paramref name="share"/> characters: its start (a quarter, at most 1,500) and then the parts with
    /// the most of <paramref name="terms"/> with a line around each, in page order, gaps marked "…". A page that fits, or where
    /// no part matches, is cut at its start as before. Parts: whether matching parts were picked.</summary>
    public static (string Text, bool Parts) Excerpt(string text, int share, IReadOnlyCollection<string> terms)
    {
        if (text.Length <= share) return (text, false);
        var pieces = new List<string>();
        foreach (var line in text.Split('\n'))
            for (var at = 0; at < line.Length; at += 800) pieces.Add(line.Substring(at, Math.Min(800, line.Length - at)));
        var scores = pieces.Select(piece => terms.Count(term => piece.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (scores.All(score => score == 0)) return (Cut(text, share), false);
        var keep = new bool[pieces.Count];
        var used = 0;
        var head = Math.Min(share / 4, 1_500);
        for (var i = 0; i < pieces.Count && used < head; i++)
        {
            keep[i] = true;
            used += pieces[i].Length + 1;
        }
        foreach (var best in Enumerable.Range(0, pieces.Count).Where(i => scores[i] > 0).OrderByDescending(i => scores[i]).ThenBy(i => i))
        {
            for (var j = Math.Max(0, best - 1); j <= Math.Min(pieces.Count - 1, best + 1); j++)
            {
                if (keep[j] || used + pieces[j].Length + 1 > share) continue;
                keep[j] = true;
                used += pieces[j].Length + 1;
            }
            if (used >= share - 100) break;
        }
        var excerpt = new StringBuilder();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (!keep[i]) continue;
            if (excerpt.Length > 0) excerpt.Append(keep[i - 1] ? "\n" : "\n…\n");
            excerpt.Append(pieces[i]);
        }
        return (excerpt.Length > share ? Cut(excerpt.ToString(), share) : excerpt.ToString(), true);
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
        if (body.Length > MaxReportCharacters) body = body[..MaxReportCharacters].TrimEnd() + "…" + ReportCut;
        var markdown = new StringBuilder("# ").Append(title).Append("\n\n").Append(body).Append("\n\n## Sources\n");
        if (pages.Count == 0) markdown.Append("No pages could be read; this comes from the search results only.\n");
        for (var i = 0; i < pages.Count; i++)
            markdown.Append(i + 1).Append(". [").Append(pages[i].Title.Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal))
                .Append("](").Append(pages[i].Url).Append(')').Append(pages[i].Cut ? SourceCut : "").Append('\n');
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

/// <summary>One research job's loop, off the reply path: a first search for the topic and its top pages read (at once), then up
/// to <see cref="ResearchLimits.Steps"/> model steps, each a fresh, bounded request where Deep thinking thinks with the notes so far
/// and what is new since the last step (the latest results not read yet and the pages not in the notes yet, a long page shown by
/// its start and the parts that match what the user wants). The model rewrites its notes with what the new pages say, says what
/// it found once the pages answer what the user wants (FOUND, which the <see cref="Watcher"/> takes), then asks for another
/// search, pages to read or the report; the last step always writes the report. A quick look-up
/// (<see cref="ResearchDepth.Quick"/>) ends as soon as the watcher has the answer. The notes carry what was read from step to
/// step, so a long job stays within one message. Searches, pages and bytes are capped by the depth's budget
/// (<see cref="WebResearch.BudgetFor"/>, unless <paramref name="limits"/> are given) and the job's token ends it; a job whose
/// model stops working keeps its notes as the report.</summary>
public sealed class WebResearchRun(IWebSearch search, IWebFetch fetch,
    Func<string, CancellationToken, Task<BackgroundJobOutcome>> think, PromptSettings? prompts, ResearchLimits? limits = null,
    Func<int>? taskBytes = null, ResearchWatcher? watcher = null)
{
    // The least a step's task keeps of the new pages and of the notes (characters) before its sources are cut.
    private const int LeastFresh = 1_200, LeastNotes = 1_500;
    private ResearchLimits limits = limits ?? WebResearch.Budget;
    private readonly ResearchLimits? given = limits;
    private readonly List<WebPage> pages = [];
    private readonly HashSet<string> tried = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> failed = [];
    private readonly List<string> queries = [];
    private IReadOnlyList<WebSearchResult> results = [];
    private IReadOnlyCollection<string> terms = [];
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
    /// <summary>The budget the job runs with (its depth's, once it started).</summary>
    public ResearchLimits Limits => limits;
    /// <summary>Watches the steps for the answer (<see cref="ResearchWatcher.Found"/> is raised once it is there).</summary>
    public ResearchWatcher Watcher { get; } = watcher ?? new();

    public async Task<ResearchOutcome> RunAsync(BackgroundJob job, ResearchArguments arguments, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(arguments);
        limits = given ?? WebResearch.BudgetFor(arguments.Depth);
        terms = WebResearch.Terms(arguments);
        var quick = arguments.Depth == ResearchDepth.Quick;
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
            job.Report(BackgroundJobState.Running, final ? (quick ? "Checking the answer" : "Writing the report") : "Thinking it over");
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
            Watcher.See(parsed, pages, step);
            if (quick && Watcher.Finding is { } finding) return new(null, null, finding);
            if (quick && final && ResearchWatcher.Answer(parsed.Found) is { } guess)
                return new(null, null, new(guess, [], step, Settled: false));
            if (parsed.Report is not null && (final || parsed.Search is null && parsed.Read.Count == 0))
                return new(WebResearch.Report(parsed, arguments, pages), null, Watcher.Finding);
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
        if (notes.Length > 0) return new(WebResearch.FromNotes(notes, arguments, pages, answered), null, Watcher.Finding);
        return new(null, lastProblem ?? "the model never wrote the report", Watcher.Finding);
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

    // The step's task within what the place it thinks on takes (taskBytes, else MaxTaskBytes): the new pages' excerpts shrink
    // first, then the notes it shows, and the sources are cut as a last resort.
    private string TaskFor(ResearchArguments arguments, int step)
    {
        var most = Math.Clamp(taskBytes?.Invoke() ?? WebResearch.MaxTaskBytes, WebResearch.MinTaskBytes, WebResearch.MaxTaskBytes);
        var unread = Unread();
        var fresh = Math.Min(limits.FreshCharacters, Math.Max(1, pages.Count - noted) * limits.ExcerptCharacters);
        var shown = notes;
        while (true)
        {
            var sources = WebResearch.Sources(queries, unread, pages, noted, failed, limits, fresh, Bytes < limits.Bytes, terms);
            var task = WebResearch.StepTask(prompts, arguments, shown, sources, step, limits.Steps);
            var over = Encoding.UTF8.GetByteCount(task) - most;
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

    // Reads up to `most` pages not tried yet within the caps, all at once, and keeps them in the order they were asked for (so
    // their numbers don't depend on which answered first); returns how many were read.
    private async Task<int> ReadAsync(BackgroundJob job, IEnumerable<string> urls, CancellationToken token, int most)
    {
        var batch = new List<Uri>();
        foreach (var link in urls)
        {
            if (pages.Count + batch.Count >= limits.Pages || Bytes >= limits.Bytes || batch.Count >= most) break;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || !WebAccess.IsWeb(url) || !tried.Add(url.AbsoluteUri)) continue;
            batch.Add(url);
        }
        if (batch.Count == 0) return 0;
        job.Report(BackgroundJobState.Running, $"Reading pages ({pages.Count + 1} of at most {limits.Pages})");
        var fetched = await Task.WhenAll(batch.Select(async url =>
        {
            try { return (Url: url, Page: (WebPage?)await fetch.FetchAsync(url, token).ConfigureAwait(false)); }
            catch (WebResearchException) { return (Url: url, Page: (WebPage?)null); }
        })).ConfigureAwait(false);
        var read = 0;
        foreach (var (url, page) in fetched)
        {
            if (page is null) { failed.Add(url.Host); continue; }
            Bytes += page.Bytes;
            if (page.Text.Length < 40 || pages.Count >= limits.Pages) { failed.Add(url.Host); continue; }
            pages.Add(page);
            read++;
        }
        return read;
    }

    public override string ToString() => $"{nameof(WebResearchRun)} ({Searches} searches, {Pages} pages, {Steps} steps)";
}
