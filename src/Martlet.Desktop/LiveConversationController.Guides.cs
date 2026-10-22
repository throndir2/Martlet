using System.IO;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Creations;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// App guides in the conversation (docs/APP_GUIDES.md, docs/CONVERSATION.md#app-guides): read_up_on, search_guide and skip_guide
// while Companion › App guides is on, the reading-up job (network only, no model), reading up on its own on a game without a guide
// that comes to the front (never left to the model's answer), "read up on X" on a route that can't call read_up_on, and the
// guide's notes on the user's message (read from memory only, never waited for). search_guide also searches the research reports
// Martlet wrote (docs/CONVERSATION.md#finding-a-report-again), and is offered alone for them while App guides are off.
internal sealed partial class LiveConversationController
{
    /// <summary>The guide library (Companion › App guides); null where guides can't be kept (no data folder).</summary>
    internal AppGuideService? Guides { get; set; }

    /// <summary>Whether replies get the guide tools: App guides on and a Thinking route that does function calling. The same
    /// three tools every time while that holds, so the start of every request stays the same.</summary>
    internal bool OffersGuides(LiveConversationConfiguration configured) => configured.SupportsTools && Guides is { On: true };

    /// <summary>Whether replies get search_guide alone, for the research reports Martlet wrote: App guides off and Web research
    /// offered (saved choices only, so it doesn't come and go with the reports).</summary>
    internal bool OffersReportSearch(LiveConversationConfiguration configured) =>
        !OffersGuides(configured) && configured.SupportsTools && dataDirectory is not null && OffersResearch(configured);

    /// <summary>The research reports on this computer, searchable (search_guide), in memory only.</summary>
    internal ResearchReportIndex Reports { get; } = new();

    /// <summary>Adds read_up_on, search_guide and skip_guide (and the App guides prompt) while they are offered, or search_guide
    /// alone for the research reports.</summary>
    private void AddGuideTools(List<(TextToolDefinition, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>)> own,
        ref string? guidance, LiveConversationOperation operation, LiveConversationConfiguration configured)
    {
        if (!OffersGuides(configured))
        {
            if (OffersReportSearch(configured)) own.Add((AppGuideTools.SearchDefinition, (call, token) => SearchGuideAsync(operation, call, token)));
            return;
        }
        own.Add((AppGuideTools.ReadUpDefinition, (call, token) => ValueTask.FromResult(ReadUpOn(operation, call))));
        own.Add((AppGuideTools.SearchDefinition, (call, token) => SearchGuideAsync(operation, call, token)));
        own.Add((AppGuideTools.SkipDefinition, SkipGuideAsync));
        own.Add((AppGuideTools.BriefDefinition, (call, token) => ValueTask.FromResult(UpdateGuideBrief(call))));
        guidance = Join(guidance, AppGuideTools.Instructions(configured.Prompts));
    }

    /// <summary>read_up_on: starts reading up in the background and returns at once.</summary>
    private ConversationToolResult ReadUpOn(LiveConversationOperation? operation, TextToolCall call)
    {
        const string server = "Martlet";
        var (arguments, problem) = AppGuideTools.ParseReadUp(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record(server, AppGuideTools.ReadUpName, "invalid arguments", "", true);
            return new(problem!, true);
        }
        if (Guides is not { On: true } guides) return new(AppGuideTools.TurnedOff, true);
        // Martlet may already read up on it: it started on its own when the app came to the front, or from the user's words.
        if ((guides.Building?.Key ?? Volatile.Read(ref startedGuide)) is { } readingKey && readingKey == (AppGuideService.Match(guides.Library, [arguments.App])?.Key ??
                AppGuideService.KeyOf(AppGuideTools.CleanName(arguments.App))))
        {
            tools?.Record(server, AppGuideTools.ReadUpName, "already reading", "", false);
            return new(AppGuideTools.AlreadyReading(guides.Library.Apps.FirstOrDefault(a => a.Key == readingKey)?.Name ?? AppGuideTools.Label(arguments.App)));
        }
        // An app with a guide already: Martlet reads more pages of the same wiki (never the first pages again).
        var more = GuideToReadMore(guides.Library, arguments.App, arguments.Sites);
        var start = more is not null ? StartReadingMore(guides, more) : StartReadingUp(guides, arguments.App, arguments.Sites);
        if (start.Job is not { } job)
        {
            tools?.Record(server, AppGuideTools.ReadUpName, "not started: " + start.Refusal, "", false);
            return new(AppGuideTools.Refused(start), true);
        }
        tools?.Record(server, AppGuideTools.ReadUpName, (more is null ? "started " : "reading more ") + job.Id, "", false);
        return new(AppGuideTools.Started(job, more?.Name ?? AppGuideTools.Label(arguments.App), operation is null || !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text),
            more is not null));
    }

    /// <summary>Whether a reading-more job of this conversation was started and hasn't ended.</summary>
    internal bool ReadingGuide => Volatile.Read(ref startedGuide) is not null;

    // The app whose guide a reading-up job reads now (set when it starts, before the guide service says so), or null.
    private string? startedGuide;

    /// <summary>The app's entry when Martlet has a guide about <paramref name="app"/> already and no other pages were given, so
    /// reading up means reading more of its wiki; null otherwise.</summary>
    internal static AppGuideEntry? GuideToReadMore(AppGuideLibrary library, string app, IReadOnlyList<string> sites) =>
        AppGuideService.Match(library, [app]) is { BuiltAt: not null } entry &&
        sites.All(site => entry.Sites.Contains(site, StringComparer.OrdinalIgnoreCase)) ? entry : null;

    /// <summary>Starts reading more of <paramref name="entry"/>'s wiki as this conversation's job (its talk window shows "Reading
    /// up on"): from what the guide file kept, so it works after Martlet starts again; a Thinking pool member chooses the pages
    /// when one can, else the most linked titles are read with no model. The pages join the guide.</summary>
    internal BackgroundJobStart StartReadingMore(AppGuideService guides, AppGuideEntry entry)
    {
        var previous = Interlocked.Exchange(ref startedGuide, entry.Key);
        var start = jobs.Start(AppGuideTools.Kind, AppGuideTools.Label(entry.Name), async (job, token) =>
        {
            try
            {
                job.Report(BackgroundJobState.Running, "Reading more of its wiki");
                var progress = new GuideProgress(text => job.Report(BackgroundJobState.Running, "Reading more: " + text));
                var read = GuideResearch is { } research
                    ? await research.ReadMoreNowAsync(entry.Key, progress, token).ConfigureAwait(false)
                    : await guides.ReadMoreAsync(entry.Key, null, null, progress, token).ConfigureAwait(false);
                LogMore(job.Id, read);
                var total = guides.Library.Apps.FirstOrDefault(a => a.Key == entry.Key)?.Pages ?? read.Pages;
                return read is { Built: true, Pages: > 0 } ? BackgroundJobOutcome.Done(AppGuideTools.MoreReady(read, total))
                    : BackgroundJobOutcome.Failed(read.Problem ?? "no more pages were worth reading");
            }
            finally { Interlocked.CompareExchange(ref startedGuide, null, entry.Key); }
        });
        if (start.Job is { } started)
            ErrorLog.Info($"App guides: started {started.Id}, reading more of a guide's wiki " +
                $"({jobs.StartedWithinHour(AppGuideTools.KindName)} of {AppGuideTools.Kind.MaxPerHour} this hour).");
        else
        {
            Interlocked.CompareExchange(ref startedGuide, previous, entry.Key);
            ErrorLog.Info($"App guides: reading more wasn't started ({start.Refusal}).");
        }
        return start;
    }

    /// <summary>The desktop log's line for one try to read more: counts and time only, never the app, a link or what was read.</summary>
    internal static void LogMore(string by, AppGuideBuild read) =>
        ErrorLog.Info(read is { Built: true, Pages: > 0 }
            ? $"App guides: {by} read {read.Pages} more pages ({read.Failed} unreadable, {read.Downloaded / 1024} KiB downloaded) into " +
              $"{read.Chunks} sections, in {BackgroundJobs.Duration(read.Took)}."
            : $"App guides: {by} added no pages after {BackgroundJobs.Duration(read.Took)} ({(read.Busy ? "busy" : read.Problem)}).");

    /// <summary>Starts a reading-up job for <paramref name="app"/> in this conversation (its talk window shows "Reading up on");
    /// <paramref name="programs"/> join its entry (the names Windows gives the program in front).</summary>
    internal BackgroundJobStart StartReadingUp(AppGuideService guides, string app, IReadOnlyList<string> sites,
        IReadOnlyList<string>? programs = null)
    {
        var start = jobs.Start(AppGuideTools.Kind, AppGuideTools.Label(app), async (job, token) =>
        {
            job.Report(BackgroundJobState.Running, "Looking for its wiki");
            var built = await guides.BuildAsync(app, sites, new GuideProgress(text => job.Report(BackgroundJobState.Running, text)), token,
                programs).ConfigureAwait(false);
            LogBuild(job.Id, built);
            return built.Built ? BackgroundJobOutcome.Done(AppGuideTools.Ready(built)) : BackgroundJobOutcome.Failed(built.Problem!);
        });
        if (start.Job is { } started)
            ErrorLog.Info($"App guides: started {started.Id} ({(AppGuideTools.TimeLimit is { } limit ? BackgroundJobs.Duration(limit) : "no")} time limit, " +
                $"{jobs.StartedWithinHour(AppGuideTools.KindName)} of {AppGuideTools.Kind.MaxPerHour} this hour).");
        else ErrorLog.Info($"App guides: reading up wasn't started ({start.Refusal}).");
        return start;
    }

    /// <summary>The desktop log's line for one try to read up: counts and time only, never the app, a link or what was read.</summary>
    internal static void LogBuild(string by, AppGuideBuild built) =>
        ErrorLog.Info((built.Built
            ? $"App guides: {by} read {built.Pages} pages from {built.Sites.Count} site{(built.Sites.Count == 1 ? "" : "s")} " +
              $"({built.Failed} unreadable, {built.Downloaded / 1024} KiB downloaded) into {built.Chunks} sections, " +
              $"{built.Bytes / 1024} KiB kept, in {BackgroundJobs.Duration(built.Took)}."
            : $"App guides: {by} couldn't make a guide after {BackgroundJobs.Duration(built.Took)} ({(built.Busy ? "busy" : built.Problem)}; " +
              $"{built.Failed} pages unreadable).") + Choice(built.Choice));

    // How the wiki's pages were chosen, from their titles: counts only.
    internal static string Choice(GuidePageChoice? choice) => choice is not { Titles: > 0 } c ? ""
        : $" Of {c.Titles} wiki page titles it looked at, {c.OtherGames} were about another game of the series and were left out" +
          (c.Asked > 0 ? $"; a Thinking pool helper chose from {c.Asked} batch{(c.Asked == 1 ? "" : "es")}." : ".");

    /// <summary>search_guide: the best sections of a guide, at once (an index still loading is waited for briefly), and of the
    /// research reports Martlet wrote (<see cref="Reports"/>). A guide the call names is searched alone, as before. Without a
    /// name, the guide of the app in front (or the only guide) is searched, and report sections that match well
    /// (<see cref="ResearchReportIndex.MinimumRelevance"/>) go with its best sections. A name that isn't a guide's searches the
    /// reports it names; with no guide to search, every report is searched.</summary>
    private async ValueTask<ConversationToolResult> SearchGuideAsync(LiveConversationOperation? operation, TextToolCall call, CancellationToken token)
    {
        const string server = "Martlet";
        var (arguments, problem) = AppGuideTools.ParseSearch(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record(server, AppGuideTools.SearchName, "invalid arguments", "", true);
            return new(problem!, true);
        }
        var reports = await ReportsAsync(token).ConfigureAwait(false);
        KeepMeaningGuides();
        // Finding by meaning: the question's vector (null when no embedding model can be used now; then words only).
        var vector = (Guides is { On: true } || reports is { Count: > 0 }) && Meaning is { } search
            ? await search.QueryAsync(arguments.Question, token).ConfigureAwait(false) : null;
        var reportsByMeaning = vector is not null && reports is { Count: > 0 } ? Meaning?.ReportSections(reports.Documents, vector) : null;
        if (Guides is not { On: true } guides)
            return reports is null ? new(AppGuideTools.TurnedOff, true) : SearchReports(arguments, reports, null, reportsByMeaning);
        var library = guides.Library;
        var built = library.Apps.Where(a => a.BuiltAt is not null).ToArray();
        var entry = arguments.App is { } named ? AppGuideService.Match(library, [named]) ?? AppGuideService.Named(library, named)
            : guides.Front?.Entry is { } inFront ? built.FirstOrDefault(a => a.Key == inFront.Key)
            : built.Length == 1 ? built[0] : null;
        if (entry is not { BuiltAt: not null }) return SearchReports(arguments, reports, built, reportsByMeaning);
        bool ready;
        try { ready = await guides.ReadyAsync(entry.Key, token).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch (TimeoutException) { ready = false; }
        var byMeaning = ready && vector is not null && guides.Chunks(entry.Key) is { Count: > 0 } chunks
            ? Meaning?.GuideSections(entry.Key, chunks, vector) : null;
        var hits = ready ? guides.Search(entry.Key, arguments.Question, AppGuideTools.SearchResults, byMeaning) : null;
        if (hits is null)
        {
            tools?.Record(server, AppGuideTools.SearchName, "not ready", "", false);
            return new(AppGuideTools.NotReady(entry.Name), false);
        }
        var fromReports = arguments.App is null && reports is not null
            ? reports.Search(arguments.Question, null, AppGuideTools.SearchResults, ResearchReportIndex.MinimumRelevance, reportsByMeaning).Hits : [];
        var kept = hits.Select(h => (h.Relevance, Guide: (GuideHit?)h, Report: (ReportHit?)null))
            .Concat(fromReports.Select(h => (h.Hit.Relevance, Guide: (GuideHit?)null, Report: (ReportHit?)h)))
            .OrderByDescending(h => h.Relevance).Take(AppGuideTools.SearchResults).ToArray();
        var guideHits = kept.Where(k => k.Guide is not null).Select(k => k.Guide!).ToArray();
        var reportHits = kept.Where(k => k.Report is not null).Select(k => k.Report!).ToArray();
        if (guideHits.Length > 0 && operation is not null) UsedGuide(operation, entry.Key);
        tools?.Record(server, AppGuideTools.SearchName, $"found {guideHits.Length}" + (reportHits.Length > 0 ? $" and {reportHits.Length} in reports" : ""),
            "", false);
        ErrorLog.Info($"App guides: the Thinking model searched a guide and found {guideHits.Length} section{(guideHits.Length == 1 ? "" : "s")}" +
            (reportHits.Length > 0 ? $", and {reportHits.Length} in research reports" : "") +
            (byMeaning is null ? "" : $" ({byMeaning.Count} section{(byMeaning.Count == 1 ? "" : "s")} close in meaning fused in)") + ".");
        if (reportHits.Length == 0) return new(AppGuideTools.Found(entry.Name, hits));
        return new((guideHits.Length > 0 ? AppGuideTools.Found(entry.Name, guideHits) + "\n\n" : "") + ResearchReportIndex.Found(reportHits));
    }

    // The research reports, brought in step with this computer's Creations first (only new or changed reports are read); null
    // where creations can't be kept.
    private async Task<ResearchReportIndex?> ReportsAsync(CancellationToken token)
    {
        if (dataDirectory is null || CreationsDirectory is not { } directory) return null;
        try { await Reports.UpdateAsync(directory, token).ConfigureAwait(false); }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            ErrorLog.Warn($"Research reports: couldn't read the reports on this PC ({error.GetType().Name}); searching what was read before.");
        }
        return Reports;
    }

    // search_guide over the research reports: those the call names, or every report. guides: the guides there are (null while App
    // guides are off), for the answer when no report is the one asked for. byMeaning: report sections found by meaning.
    private ConversationToolResult SearchReports(GuideQuestion arguments, ResearchReportIndex? reports, IReadOnlyList<AppGuideEntry>? guides,
        IReadOnlyDictionary<string, IReadOnlyList<(int Chunk, double Meaning)>>? byMeaning = null)
    {
        const string server = "Martlet";
        var titles = reports?.Titles ?? [];
        var found = reports?.Search(arguments.Question, arguments.App, AppGuideTools.SearchResults, similar: byMeaning);
        if (found is not { Searched.Count: > 0 } || found.Hits.Count == 0 && arguments.App is null && guides is { Count: > 0 })
        {
            tools?.Record(server, AppGuideTools.SearchName, "no guide", "", false);
            return new(guides is null ? AppGuideTools.NoReport(arguments.App, titles) : AppGuideTools.NoGuide(arguments.App, guides, titles), false);
        }
        tools?.Record(server, AppGuideTools.SearchName, $"found {found.Hits.Count} in reports", "", false);
        ErrorLog.Info($"Research reports: the Thinking model searched {found.Searched.Count} report{(found.Searched.Count == 1 ? "" : "s")} and " +
            $"found {found.Hits.Count} section{(found.Hits.Count == 1 ? "" : "s")}.");
        return new(found.Hits.Count == 0 ? ResearchReportIndex.NothingFound(found.Searched) : ResearchReportIndex.Found(found.Hits));
    }

    /// <summary>skip_guide: the user doesn't want Martlet to read up on the app: it stops reading up on it (when it does now) and
    /// never reads up on it on its own again.</summary>
    private async ValueTask<ConversationToolResult> SkipGuideAsync(TextToolCall call, CancellationToken token)
    {
        const string server = "Martlet";
        var (app, problem) = AppGuideTools.ParseSkip(call.ArgumentsJson);
        if (app is null)
        {
            tools?.Record(server, AppGuideTools.SkipName, "invalid arguments", "", true);
            return new(problem!, true);
        }
        if (Guides is not { } guides) return new(AppGuideTools.TurnedOff, true);
        // One app is read at a time: when it is this one, its job stops (and isn't brought up later, since Martlet knows).
        if (guides.Building is { } reading && reading.Key == (AppGuideService.Match(guides.Library, [app])?.Key ??
                AppGuideService.KeyOf(AppGuideTools.CleanName(app))) &&
            jobs.Cancel(null, BackgroundJob.CanceledByMartlet, AppGuideTools.KindName) is { } stopped)
            ErrorLog.Info($"App guides: {stopped.Id} stopped; you didn't want Martlet to read up on that app.");
        try
        {
            var declined = await guides.DeclineAsync(app, token).ConfigureAwait(false);
            tools?.Record(server, AppGuideTools.SkipName, "declined", "", false);
            ErrorLog.Info("App guides: you said no to reading up on an app; Martlet won't read up on it on its own again.");
            return new(AppGuideTools.Skipped(declined.Name));
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            ErrorLog.Warn($"App guides: couldn't keep your no to reading up on an app ({why}).");
            tools?.Record(server, AppGuideTools.SkipName, "failed", "", true);
            return new("Couldn't note that on this PC right now. Say okay briefly anyway.", true);
        }
    }

    /// <summary>A game (or an app on the list) without a guide came to the front: Martlet reads up on it on its own, as this
    /// conversation's job (never left to the model's answer, so the app is on the list from the start, with the names Windows
    /// gives its program), and tells the user with a notice (<see cref="OfferGuide"/>) as soon as it is free.</summary>
    internal BackgroundJobStart ReadUpOnItsOwn(AppGuideService guides, AppGuideOfferCandidate offer)
    {
        var start = StartReadingUp(guides, offer.Name, [], offer.Programs);
        if (start.Job is not null && OfferGuide(offer) is { } notice)
            ErrorLog.Info($"App guides: {notice.Id} waits; Martlet tells you it reads up on the app in front as soon as it's free.");
        return start;
    }

    /// <summary>The notice that Martlet started reading up on a game in front on its own: it brings it up as soon as it is free,
    /// or with what the user says next. Null when the conversation is closing or too many wait.</summary>
    internal BackgroundJob? OfferGuide(AppGuideOfferCandidate offer) =>
        jobs.Start(AppGuideTools.OfferKind, AppGuideTools.Label(offer.Name),
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done(AppGuideTools.OfferNote(offer)))).Job;

    /// <summary>The user's own words ask Martlet to read up on an app while this reply can't call read_up_on (a Thinking route
    /// without function calling, or a model that rejected tools), or to read more about an app it has a guide for (on every
    /// route, so a reply never says yes with no job behind it): Martlet starts it itself, and the note tells the model. Null when
    /// the words don't ask that ("read up on it" and "read more about it" mean the app in front, when there is one).</summary>
    private string? ReadUpAsked(AppGuideService guides, string words, DesktopToolset? toolset)
    {
        var offered = toolset?.Offers(AppGuideTools.ReadUpName) == true;
        var moreWords = AppGuideTools.AskedToReadMore(words);
        if ((moreWords ?? (offered ? null : AppGuideTools.AskedToReadUp(words))) is not { } asked) return null;
        var front = guides.Front;
        IReadOnlyList<string>? programs = null;
        if (asked == AppGuideTools.InFront)
        {
            if (front is null) return null;
            asked = front.Entry?.Name ?? AppGuideService.Readable(front.Program.Length > 0 ? front.Program : front.File);
            programs = new[] { front.Program, front.File }.Where(p => p.Length > 0).ToArray();
            if (asked.Length == 0) return null;
        }
        var more = GuideToReadMore(guides.Library, asked, []);
        // Read-more words about an app without a guide read up on it, but only where the model can't call read_up_on itself.
        if (more is null && moreWords is not null && offered) return null;
        if ((guides.Building?.Key ?? Volatile.Read(ref startedGuide)) is { } reading && reading == (more?.Key ??
                AppGuideService.Match(guides.Library, [asked])?.Key ?? AppGuideService.KeyOf(asked)))
            return AppGuideTools.ReadingNote(guides.Library.Apps.FirstOrDefault(a => a.Key == reading)?.Name ?? asked, null, more is not null);
        var start = more is not null ? StartReadingMore(guides, more) : StartReadingUp(guides, asked, [], programs);
        tools?.Record("Martlet", AppGuideTools.ReadUpName,
            start.Job is { } job ? (more is null ? "started " : "reading more ") + job.Id + " (asked in words)" : "not started: " + start.Refusal, "", false);
        return AppGuideTools.ReadingNote(more?.Name ?? AppGuideTools.Label(asked), start, more is not null);
    }

    /// <summary>The app whose guide the last reply used (its notes or search_guide found sections), or null: what the talk
    /// window's Read more button and the speech bubble's offer read more about when no app with a guide is in front.</summary>
    internal string? LastGuide => Volatile.Read(ref lastGuide);
    private string? lastGuide;

    // A reply used the app's guide: the operation and the talk window know which.
    private void UsedGuide(LiveConversationOperation operation, string key)
    {
        operation.GuideKey = key;
        Volatile.Write(ref lastGuide, key);
    }

    /// <summary>The app Read more reads more about now: the app in front when it has a guide, else the app whose guide the last
    /// reply used; null when there is none or App guides are off.</summary>
    internal AppGuideEntry? ReadMoreTarget()
    {
        if (Guides is not { On: true } guides) return null;
        var library = guides.Library;
        return guides.Front?.Entry is { } front && library.Apps.FirstOrDefault(a => a.Key == front.Key) is { BuiltAt: not null } inFront ? inFront
            : LastGuide is { } key && library.Apps.FirstOrDefault(a => a.Key == key) is { BuiltAt: not null } used ? used : null;
    }

    /// <summary>Reads more about <paramref name="entry"/> as this conversation's job (Read more in the talk window, on the speech
    /// bubble or on Companion › App guides), or says why it can't start: App guides off, or it reads up on an app now.</summary>
    internal BackgroundJobStart ReadMore(AppGuideEntry entry)
    {
        if (Guides is not { On: true } guides) return new(null, "off", "App guides are off.");
        if ((guides.Building?.Key ?? Volatile.Read(ref startedGuide)) is not null)
            return new(null, "busy", $"Martlet is already reading up on {guides.Building?.Name ?? "an app"}.");
        return StartReadingMore(guides, entry);
    }

    /// <summary>The guide notes for the user's own words (the app they name, else the app in front), from memory only: the
    /// sections the conversation's notes don't carry yet. Null when there are none. Counts go to the desktop log.</summary>
    private string? GuideNotes(string words, IReadOnlyList<TextHistoryMessage> sent, PromptSettings? prompts, out int used, out string? key)
    {
        used = 0;
        key = null;
        if (Guides is not { On: true } guides || !guides.Library.Apps.Any(a => a.BuiltAt is not null)) return null;
        var earlier = string.Join("\n", sent.Where(message => message.Role == TextHistoryRole.User &&
            (message.Text.Contains(GuideRecall.Label, StringComparison.Ordinal) || message.Text.Contains(GuideBrief.Label, StringComparison.Ordinal)))
            .Select(message => message.Text));
        if (guides.Recall(words, earlier.Length == 0 ? null : earlier, prompts) is not { } found) return null;
        var brief = GuideRecall.BriefNotes(found.App, found.Brief, prompts);
        // Off the reply's path: the line is written after the request has gone.
        ErrorLog.InfoLater((!found.Ready
            ? "App guides: the app's guide is still loading, so your message went without it."
            : found.Notes is null
                ? $"App guides: nothing in the guide matched well enough ({found.Matched} weak matches, best relevance {found.Best:0.00}, " +
                  $"{found.Took.TotalMilliseconds:0.##} ms)."
                : $"App guides: {found.Used} section{(found.Used == 1 ? "" : "s")} went with your message ({found.Matched} matched, best " +
                  $"relevance {found.Best:0.00}, {found.Took.TotalMilliseconds:0.##} ms).") +
            (brief is null ? "" : " The app's brief went with it."));
        used = found.Used;
        if (used > 0) key = AppGuideService.Match(guides.Library, [found.App])?.Key;
        return brief is null ? found.Notes : found.Notes is null ? brief : brief + "\n" + found.Notes;
    }

    private sealed class GuideProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
