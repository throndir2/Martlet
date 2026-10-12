using System.Diagnostics;

namespace Martlet.Conversation.Guides;

/// <summary>What one research job did: the app, the area (null for a brief only), whether it read the web, the sections it added,
/// whether it wrote a new brief, why it stopped (null when it did what it set out to do) and how long it took.</summary>
public sealed record GuideResearchResult(string Key, string? Area, bool Web, int Sections, bool Brief, string? Problem, TimeSpan Took);

/// <summary>Deeper research for App guides (docs/APP_GUIDES.md): the brief after a guide is read, update_guide_brief, and web
/// research on the area of the game or app the screen shows. One job at a time, off every reply's path, with hourly caps. The
/// model work goes only to <see cref="IGuideThinker"/> (a Thinking pool member), never to the conversation's Thinking route.
/// The log gets counts and times only, never the screen, the area, questions, links or text.</summary>
public sealed class AppGuideResearcher : IDisposable
{
    public const int BriefsPerHour = 6;
    public const int AreasPerHour = 4;
    /// <summary>How often the screen is looked at for a new area, at most.</summary>
    public static TimeSpan DefaultScreenEvery { get; } = TimeSpan.FromMinutes(5);
    /// <summary>An area researched more recently than this isn't researched again.</summary>
    public static TimeSpan AreaAgain { get; } = TimeSpan.FromHours(24);

    private readonly AppGuideService guides;
    private readonly IGuideThinker thinker;
    private readonly IWebSearch search;
    private readonly IWebDocuments documents;
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan, CancellationToken, Task> wait;
    private readonly SemaphoreSlim job = new(1, 1);
    private readonly CancellationTokenSource closing = new();
    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> briefTimes = new(), areaTimes = new();
    private DateTimeOffset lastScreen = DateTimeOffset.MinValue;
    private string? lastScreenText;
    private string? doing, lastProblem;
    private int briefs, areas, webAreas, sections, screens, mores;
    private DateTimeOffset? lastAt;
    private bool disposed;

    public AppGuideResearcher(AppGuideService guides, IGuideThinker thinker, IWebSearch search, IWebDocuments documents,
        TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        this.guides = guides ?? throw new ArgumentNullException(nameof(guides));
        this.thinker = thinker ?? throw new ArgumentNullException(nameof(thinker));
        this.search = search ?? throw new ArgumentNullException(nameof(search));
        this.documents = documents ?? throw new ArgumentNullException(nameof(documents));
        this.clock = clock ?? TimeProvider.System;
        this.wait = wait ?? ((time, token) => Task.Delay(time, this.clock, token));
        guides.Built += OnBuilt;
    }

    /// <summary>How often the screen is looked at for a new area, at most (checks pass a shorter time).</summary>
    public TimeSpan ScreenEvery { get; init; } = DefaultScreenEvery;

    /// <summary>Gets one line per job: counts and times only.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Raised on any thread when a job starts or ends.</summary>
    public event Action? Changed;

    /// <summary>Whether a Thinking pool member that answers now can take research.</summary>
    public bool CanThink => thinker.CanThink;

    /// <summary>What the running job does ("brief", "area"), or null.</summary>
    public string? Doing { get { lock (gate) return doing; } }
    public int Briefs { get { lock (gate) return briefs; } }
    /// <summary>Areas found on the screen or asked for and looked into (in the guide or on the web).</summary>
    public int Areas { get { lock (gate) return areas; } }
    /// <summary>Areas researched on the web.</summary>
    public int WebAreas { get { lock (gate) return webAreas; } }
    /// <summary>Sections research added to guides.</summary>
    public int Sections { get { lock (gate) return sections; } }
    /// <summary>Screen notes looked at for an area.</summary>
    public int Screens { get { lock (gate) return screens; } }
    /// <summary>Times Martlet read more of a wiki after a read.</summary>
    public int Mores { get { lock (gate) return mores; } }
    public string? LastProblem { get { lock (gate) return lastProblem; } }
    public DateTimeOffset? LastAt { get { lock (gate) return lastAt; } }

    /// <summary>Why update_guide_brief can't start for <paramref name="key"/> now, or null when it can.</summary>
    public string? CantStart(string key)
    {
        if (!guides.On) return "app guides are off";
        if (guides.Library.Apps.FirstOrDefault(a => a.Key == key) is not { BuiltAt: not null }) return "there's no guide about it yet";
        if (!thinker.CanThink) return "no Thinking pool member can take research now (Companion › Deep thinking › Thinking pool)";
        return null;
    }

    /// <summary>Starts update_guide_brief's research in the background: the focus (when given) in the guide and on the web, then
    /// the brief. "busy" when another research job runs; else why not, or null when it started.</summary>
    public string? Start(string key, string? focus)
    {
        if (CantStart(key) is { } why) return why;
        if (!job.Wait(0)) return "busy";
        _ = Task.Run(async () =>
        {
            try { await RefreshCoreAsync(key, focus, closing.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally { Release(); }
        });
        return null;
    }

    /// <summary>update_guide_brief's research, waited for (checks): the focus (when given), then the brief.</summary>
    public async Task<GuideResearchResult> RefreshAsync(string key, string? focus, CancellationToken cancellationToken = default)
    {
        if (CantStart(key) is { } why) return new(key, focus, false, 0, false, why, TimeSpan.Zero);
        await job.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RefreshCoreAsync(key, focus, cancellationToken).ConfigureAwait(false); }
        finally { Release(); }
    }

    /// <summary>Writes the brief of <paramref name="key"/>'s app, waited for (checks and after a read).</summary>
    public async Task<GuideResearchResult> WriteBriefAsync(string key, string? focus = null, CancellationToken cancellationToken = default)
    {
        await job.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await BriefJobAsync(key, focus, cancellationToken).ConfigureAwait(false); }
        finally { Release(); }
    }

    /// <summary>Looks at a screen note: when it shows an area of the app in front (a Thinking pool member names it), and the guide
    /// has little on it, researches it on the web. Waited for (checks); <see cref="Screen"/> is the throttled call.</summary>
    public async Task<GuideResearchResult> ResearchScreenAsync(string key, string screen, CancellationToken cancellationToken = default)
    {
        await job.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ScreenJobAsync(key, screen, cancellationToken).ConfigureAwait(false); }
        finally { Release(); }
    }

    /// <summary>A new screen note (the context board's screen summary): looked at in the background at most once every
    /// <see cref="ScreenEvery"/>, only while App guides and Read up when I start a game or app are on, the app in front has a
    /// guide the user didn't say no to and a pool member can think, and never while another research job runs. Returns at once.</summary>
    public void Screen(string? text)
    {
        if (disposed || string.IsNullOrWhiteSpace(text)) return;
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (now - lastScreen < ScreenEvery || string.Equals(text, lastScreenText, StringComparison.Ordinal)) return;
            lastScreen = now;
            lastScreenText = text;
        }
        // The caller may be on a reply's path (the board's Changed): everything else happens on the thread pool.
        _ = Task.Run(async () =>
        {
            var library = guides.Library;
            if (!library.On || !library.ReadUpWhenStarted || guides.Building is not null ||
                guides.Front?.Entry is not { BuiltAt: not null, Declined: false } entry || !thinker.CanThink || !job.Wait(0)) return;
            try { await ScreenJobAsync(entry.Key, text, closing.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally { Release(); }
        });
    }

    /// <summary>Whether <see cref="Screen"/> would look at a new note now (cheap: the caller reads the board only then).</summary>
    public bool WantsScreen
    {
        get
        {
            if (disposed) return false;
            lock (gate) return clock.GetUtcNow() - lastScreen >= ScreenEvery;
        }
    }

    // After a read (or Read again), the brief is written again in the background, when a pool member can think; then a pool member
    // chooses more pages to read from the links of what was read, with what the user does now, and the brief is written again
    // when that added pages.
    private void OnBuilt(AppGuideBuild built)
    {
        if (disposed || !built.Built) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Waits for a running job, but not longer than a few minutes.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
                limit.CancelAfter(TimeSpan.FromMinutes(10));
                await job.WaitAsync(limit.Token).ConfigureAwait(false);
                try
                {
                    if (!thinker.CanThink)
                    {
                        Done("brief", new(built.Key, null, false, 0, false, "no Thinking pool member can take research now", TimeSpan.Zero));
                        return;
                    }
                    await BriefJobAsync(built.Key, null, closing.Token).ConfigureAwait(false);
                    if (built.More is { Count: > 0 } more && (await MoreJobAsync(built.Key, more, closing.Token).ConfigureAwait(false)).Sections > 0 &&
                        thinker.CanThink)
                        await BriefJobAsync(built.Key, null, closing.Token).ConfigureAwait(false);
                }
                finally { Release(); }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    /// <summary>Reads more of <paramref name="key"/>'s wiki, waited for (checks): a pool member chooses among the titles the first
    /// read's pages link to (<see cref="GuidePageChooser"/>, with <see cref="Context"/>), and the chosen pages join the guide.</summary>
    public async Task<GuideResearchResult> ReadMoreAsync(string key, GuideMore more, CancellationToken cancellationToken = default)
    {
        await job.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await MoreJobAsync(key, more, cancellationToken).ConfigureAwait(false); }
        finally { Release(); }
    }

    /// <summary>Notes on what the user does now for choosing more pages (the desktop: the screen, sounds and activity notes and
    /// the last exchanges), read again for each ask; null for none. Never logged.</summary>
    public Func<string?>? Context { get; set; }

    private async Task<GuideResearchResult> MoreJobAsync(string key, GuideMore more, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        Begin("more");
        GuideResearchResult result;
        var chooser = new GuidePageChooser(thinker, guides.Library.Apps.FirstOrDefault(a => a.Key == key)?.Name ?? key, Context);
        AppGuideBuild? read = null;
        try
        {
            read = await guides.ReadMoreAsync(key, more, chooser.ChooseAsync, null, token).ConfigureAwait(false);
            result = new(key, null, true, read.Built ? read.Chunks : 0, false, read.Problem, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
        {
            result = new(key, null, true, 0, false, "it failed on this PC (" + error.GetType().Name + ")", Stopwatch.GetElapsedTime(started));
        }
        lock (gate) mores++;
        Log?.Invoke($"App guides: read more of a wiki in {BackgroundJobs.Duration(result.Took)}: {more.Count} linked titles, " +
            $"{chooser.Answers} of {chooser.Asks} asks answered{(chooser.HadContext ? " with what you do now" : "")}, " +
            $"{(read?.Built == true ? read.Pages : 0)} pages added ({result.Sections} sections, {(read?.Downloaded ?? 0) / 1024} KiB downloaded)" +
            (result.Problem is null ? "." : $"; {result.Problem}."));
        Done("more", result);
        return result;
    }

    private async Task<GuideResearchResult> RefreshCoreAsync(string key, string? focus, CancellationToken token)
    {
        focus = GuideBrief.CleanFocus(focus);
        GuideResearchResult? area = null;
        if (focus is not null) area = await AreaJobAsync(key, focus, force: true, token).ConfigureAwait(false);
        var brief = await BriefJobAsync(key, focus, token).ConfigureAwait(false);
        return area is null ? brief : area with { Brief = brief.Brief, Problem = brief.Problem ?? area.Problem, Took = area.Took + brief.Took };
    }

    private async Task<GuideResearchResult> BriefJobAsync(string key, string? focus, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        Begin("brief");
        string? problem;
        try { problem = await BriefCoreAsync(key, focus, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
        {
            problem = "it failed on this PC (" + error.GetType().Name + ")";
        }
        var result = new GuideResearchResult(key, focus, false, 0, problem is null, problem, Stopwatch.GetElapsedTime(started));
        Done("brief", result);
        return result;
    }

    private async Task<string?> BriefCoreAsync(string key, string? focus, CancellationToken token)
    {
        if (guides.Library.Apps.FirstOrDefault(a => a.Key == key) is not { BuiltAt: not null } entry) return "there's no guide about it yet";
        if (!thinker.CanThink) return "no Thinking pool member can take research now";
        if (!Hourly(briefTimes, BriefsPerHour)) return "the hourly limit of briefs is reached";
        if (!await guides.ReadyAsync(key, token).ConfigureAwait(false)) return "its guide can't be read";
        IReadOnlyList<GuideHit> Find(string question, int max) => guides.Search(key, question, max) ?? [];
        var sections = GuideBrief.Sections(Find, focus, guides.First(key, 3));
        if (sections.Count == 0) return "its guide has no sections";
        var input = GuideBrief.Input(entry.Name, focus, entry.Brief, sections);
        var (answer, why) = await thinker.ThinkAsync(new("App guide brief", GuideBrief.Instructions, input, GuideBrief.MaxOutputTokens,
            GuideBrief.Timeout), token).ConfigureAwait(false);
        if (GuideBrief.Clean(answer) is not { } brief) return why ?? "the answer wasn't a brief";
        return await guides.KeepBriefAsync(key, brief, token).ConfigureAwait(false) ? null : "the brief couldn't be kept";
    }

    private async Task<GuideResearchResult> ScreenJobAsync(string key, string screen, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        lock (gate) screens++;
        if (guides.Library.Apps.FirstOrDefault(a => a.Key == key) is not { BuiltAt: not null } entry)
            return new(key, null, false, 0, false, "there's no guide about it yet", TimeSpan.Zero);
        var text = screen.Length <= 4_000 ? screen : screen[..4_000];
        var (answer, why) = await thinker.ThinkAsync(new("App guide area", GuideAreaResearch.ClueInstructions,
            GuideAreaResearch.ClueInput(entry.Name, text), 24, GuideAreaResearch.ClueTimeout), token).ConfigureAwait(false);
        var area = GuideAreaResearch.ParseArea(answer, entry.Name);
        if (area is null)
        {
            var none = new GuideResearchResult(key, null, false, 0, false, answer is null ? why ?? "no answer" : "no area on the screen",
                Stopwatch.GetElapsedTime(started));
            Log?.Invoke($"App guides: looked at the screen for an area in {none.Took.TotalSeconds:0.0} s; none found.");
            return none;
        }
        return await AreaJobAsync(key, area, force: false, token).ConfigureAwait(false);
    }

    private async Task<GuideResearchResult> AreaJobAsync(string key, string area, bool force, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        Begin("area");
        GuideResearchResult result;
        try { result = await AreaCoreAsync(key, area, force, started, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
        {
            result = new(key, area, false, 0, false, "it failed on this PC (" + error.GetType().Name + ")", Stopwatch.GetElapsedTime(started));
        }
        Done("area", result);
        return result;
    }

    private async Task<GuideResearchResult> AreaCoreAsync(string key, string area, bool force, long started, CancellationToken token)
    {
        GuideResearchResult Result(bool web, int added, string? problem) =>
            new(key, area, web, added, false, problem, Stopwatch.GetElapsedTime(started));
        if (guides.Library.Apps.FirstOrDefault(a => a.Key == key) is not { BuiltAt: not null } entry) return Result(false, 0, "there's no guide about it yet");
        var now = clock.GetUtcNow();
        var earlier = entry.Areas.LastOrDefault(a => a.Name.Equals(area, StringComparison.OrdinalIgnoreCase));
        // An area looked into lately isn't looked into again (on the web), unless the user asked (and then not within the hour).
        if (earlier is not null && now - earlier.At < (force ? TimeSpan.FromHours(1) : AreaAgain)) return Result(false, 0, "it was researched lately");
        if (!await guides.ReadyAsync(key, token).ConfigureAwait(false)) return Result(false, 0, "its guide can't be read");
        // The guide first: when its sections already match the area well, there's nothing to look for on the web.
        var known = (guides.Search(key, area, 3) ?? []).Where(h => h.Relevance >= GuideRecall.MinimumRelevance && !GuideAreaResearch.IsResearched(h.Chunk))
            .ToArray();
        if (known.Length > 0 && !force)
        {
            await guides.KeepAreaAsync(key, area, 0, token).ConfigureAwait(false);
            return Result(false, 0, null);
        }
        if (!Hourly(areaTimes, AreasPerHour)) return Result(false, 0, "the hourly limit of area research is reached");
        var findings = await GuideAreaResearch.ReadAsync(search, documents, entry.Name, area, wait, token).ConfigureAwait(false);
        if (findings.Excerpts.Count + findings.Snippets.Count == 0)
        {
            await guides.KeepAreaAsync(key, area, 0, token).ConfigureAwait(false);
            return Result(true, 0, findings.Problem ?? "nothing about it was found on the web");
        }
        string? summary = null;
        if (thinker.CanThink)
        {
            var (answer, _) = await thinker.ThinkAsync(new("App guide area notes", GuideAreaResearch.SummaryInstructions,
                GuideAreaResearch.SummaryInput(entry.Name, area, findings), 500, GuideAreaResearch.SummaryTimeout), token).ConfigureAwait(false);
            summary = GuideAreaResearch.CleanSummary(answer);
        }
        var added = GuideAreaResearch.Sections(area, summary, findings);
        var refused = await guides.AddResearchAsync(key, area, added, token).ConfigureAwait(false);
        if (refused is not null) return Result(true, 0, refused);
        Log?.Invoke($"App guides: researched an area on the web in {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s: {findings.Searches} " +
            $"search(es), {findings.Pages} page(s), {findings.Failed} not read, {findings.Bytes / 1024} KB, {added.Count} section(s) added" +
            (summary is null ? ", no summary." : ", with a summary."));
        return Result(true, added.Count, null);
    }

    private bool Hourly(Queue<DateTimeOffset> times, int most)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromHours(1)) times.Dequeue();
            if (times.Count >= most) return false;
            times.Enqueue(now);
            return true;
        }
    }

    private void Begin(string what)
    {
        lock (gate) doing = what;
        Changed?.Invoke();
    }

    private void Done(string what, GuideResearchResult result)
    {
        lock (gate)
        {
            doing = null;
            lastAt = clock.GetUtcNow();
            lastProblem = result.Problem;
            if (what == "brief" && result.Brief) briefs++;
            if (what == "area" && result.Area is not null && result.Problem is null) areas++;
            if (what == "area" && result.Web && result.Sections > 0) webAreas++;
            sections += result.Sections;
        }
        if (what == "brief")
            Log?.Invoke($"App guides: {(result.Brief ? "wrote a brief" : "wrote no brief (" + result.Problem + ")")} in {result.Took.TotalSeconds:0.0} s.");
        else if (what == "area" && result.Sections == 0)
            Log?.Invoke($"App guides: looked into an area in {result.Took.TotalSeconds:0.0} s: " +
                (result.Problem is null ? "the guide had enough on it." : result.Problem + "."));
        Changed?.Invoke();
    }

    private void Release()
    {
        try { job.Release(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        guides.Built -= OnBuilt;
        closing.Cancel();
        closing.Dispose();
    }
}
