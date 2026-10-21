using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>research_check: rehearses web research end to end with Martlet's own parts: the research tool's texts and job kind
/// (<see cref="WebResearch"/>), the background-job scheduler (<see cref="BackgroundJobs"/>), the web client
/// (<see cref="WebAccess"/>: the DuckDuckGo results parser, the page reader and its public-address guard), the research loop
/// (<see cref="WebResearchRun"/>, its notes and its budget) with each step a background think (<see cref="BackgroundThink"/>)
/// through the conversation runtime and Chat Completions adapter, the report creation and its page (<see cref="ResearchReports"/>), against
/// fixtures on 127.0.0.1: a search page, web pages and a model with canned answers (NOT AI), and an in-memory web for the
/// budget. Nothing leaves loopback; no credentials are read; the report is kept in a temporary data folder that is deleted
/// afterwards.</summary>
internal static class ResearchCheck
{
    private const string Model = "fixture-model";
    private const string Persona = "You are Martlet, a cheerful companion who talks with the user.";
    private const string Asked = "Can you look up what toys cats like best?";
    private const string Acknowledged = "Ooh, good question! Let me look into that for you.";
    private const string Topic = "best toys for cats";
    private const string Find = "which toys most cats like and why";
    private const string Summary = "Most cats love wand toys and crinkle balls because they imitate prey (FIXTURE - NOT AI).";
    private const string FixtureNote = "Wand toys let cats stalk and pounce like prey; most play daily [1] (FIXTURE NOTE - NOT AI)";
    private const string Found = "Most cats like wand toys best, because they move like prey [1] (FIXTURE - NOT AI).";

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-research-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            await using var fixture = new Fixture();
            var settings = Settings();
            var guard = Guard();
            var flow = await FlowAsync(fixture, directory, cancellation);
            var limits = await LimitsAsync(cancellation);
            var budget = await BudgetAsync(cancellation);
            var levels = await LevelsAsync(directory, cancellation);
            return new
            {
                ok = settings.Ok && guard.Ok && flow.Ok && limits.Ok && budget.Ok && levels.Ok,
                endpoint = fixture.BaseUrl,
                note = "Fixture search page, web pages and model on 127.0.0.1 with canned answers (NOT AI); the tool texts, job kind, " +
                    "scheduler, web client, research loop, watcher, source check, runtime, adapter and report creation are Martlet's own. " +
                    "No real web search or model was used.",
                settings = settings.Report, guard = guard.Report, flow = flow.Report, limits = limits.Report, budget = budget.Report,
                levels = levels.Report
            };
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // On by default; off when turned off or with Thinking longer off; saved lean (only off is saved).
    private static (bool Ok, object Report) Settings()
    {
        var byDefault = new ThinkLongerSettings();
        var off = new ThinkLongerSettings { WebResearch = false };
        var deepOff = new ThinkLongerSettings { Enabled = false };
        var saved = JsonSerializer.Serialize(ThinkLongerSettings.Normalize(off));
        var ok = byDefault.Researches && byDefault.WebResearchOn && !off.Researches && !deepOff.Researches &&
            ThinkLongerSettings.Normalize(off with { WebResearch = true }) is null && saved.Contains("false", StringComparison.Ordinal);
        return (ok, new { ok, byDefault = byDefault.Researches, turnedOff = off.Researches, deepThinkingOff = deepOff.Researches, saved });
    }

    // Only public internet addresses are reachable (redirects included).
    private static (bool Ok, object Report) Guard()
    {
        var cases = new (string Address, bool Public)[]
        {
            ("93.184.215.14", true), ("2606:4700::1111", true), ("127.0.0.1", false), ("10.1.2.3", false), ("192.168.1.1", false),
            ("172.20.0.5", false), ("169.254.169.254", false), ("100.100.1.1", false), ("::1", false), ("fd00::1", false), ("fe80::1", false),
            ("::ffff:192.168.0.1", false), ("0.0.0.0", false), ("224.0.0.251", false)
        };
        var results = cases.Select(c => (c.Address, c.Public, Got: WebAccess.IsPublic(IPAddress.Parse(c.Address)))).ToArray();
        var ok = results.All(r => r.Public == r.Got);
        return (ok, new { ok, addresses = results.Select(r => new { address = r.Address, @public = r.Got, expected = r.Public }) });
    }

    private static async Task<(bool Ok, object Report)> FlowAsync(Fixture fixture, string directory, CancellationToken cancellation)
    {
        var registry = new CreationRegistry();
        registry.Register(ResearchReports.Kind);
        string? opened = null;
        using var attached = registry.Handle(ResearchReports.KindName, ResearchReports.Handler(directory, path => opened = path));
        var thinkSettings = new ThinkLongerSettings { WebResearch = true };
        using var jobs = new BackgroundJobs();
        await using var replies = ConversationRuntime.Create(new NoCredentials());
        await using var thinking = ConversationRuntime.Create(new NoCredentials());
        var permissions = new Permissions(ChatCompletionsSetup.BaseUri(fixture.BaseUrl + "/v1"));
        var instructions = string.Join("\n\n", Persona, PromptSettings.Fill(null, PromptCatalog.Tools), ThinkLonger.Instructions(thinkSettings, null),
            WebResearch.Instructions(null));
        var input = new BoundedTextInput(Asked, instructions, [new(TextHistoryRole.User, "Hi!"), new(TextHistoryRole.Assistant, "Hey!")],
            tools: [.. ThinkLonger.Definitions(thinkSettings), WebResearch.Definition]);
        var clock = Stopwatch.StartNew();
        long toolReturnedMs = -1;
        ConversationTurn? reply = null;
        BackgroundJobStart? started = null;
        WebResearchRun? run = null;
        string? key = null;
        var host = new Tools(call =>
        {
            if (call.Name != WebResearch.Name) return new("Not in this check.", true);
            var (arguments, problem) = WebResearch.Parse(call.ArgumentsJson);
            if (arguments is null) return new(problem!, true);
            started = jobs.Start(WebResearch.Kind, WebResearch.Label(arguments.Topic), async (job, token) =>
            {
                using var web = new WebAccess(fixture.BaseUrl + "/search", allowLoopback: true);
                run = new WebResearchRun(web, web, (task, stepToken) => new BackgroundThink(thinking, left =>
                    (StepRequest(fixture, input, reply!.Content.Text, task, thinkSettings, left), permissions)) { Doing = job.Progress }
                    .RunAsync(job, stepToken), null);
                var found = await run.RunAsync(job, arguments, token);
                if (found.Report is not { } report) return BackgroundJobOutcome.Failed(found.Problem ?? "nothing");
                var creation = await CreationStore.AddAsync(directory, ResearchReports.Draft(report, new CreationAuthor { Device = "fixture-device" }),
                    registry, DateTimeOffset.UtcNow, token);
                key = creation.Key;
                return BackgroundJobOutcome.Done(WebResearch.Ready(report, key));
            });
            toolReturnedMs = clock.ElapsedMilliseconds;
            return started.Job is { } job ? new(WebResearch.Started(job, !string.IsNullOrWhiteSpace(reply!.Content.Text))) : new(WebResearch.Refused(started), true);
        });
        reply = replies.Start(new ConversationRequest(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model), ReplyLimits,
            new ConversationLimits { TurnTimeout = TimeSpan.FromSeconds(60), MaxToolRounds = 4 }, chat: new ChatCompletionsTarget(fixture.BaseUrl + "/v1", true),
            generation: new GenerationSettings { Reasoning = false }, tools: host), permissions, cancellation);
        var replied = await reply.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        await reply.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        var replyMs = clock.ElapsedMilliseconds;
        var job = started?.Job;
        var runningWhenReplyEnded = job is { Finished: false };
        var waited = Stopwatch.StartNew();
        while (job is { Finished: false } && waited.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(20, cancellation);

        // The steps the model got, the note the conversation gets and the report shown on a yes.
        var steps = fixture.Bodies("step").Select(Last).ToArray();
        var firstStep = steps.FirstOrDefault() ?? "";
        var secondStep = steps.ElementAtOrDefault(1) ?? "";
        var results = job is null ? "" : BackgroundJobs.Results([job]);
        var library = CreationStore.View(directory);
        var creation = key is null ? null : library.Live.FirstOrDefault(c => c.Key == key);
        CreationActionResult? shown = null;
        string html = "";
        if (creation is not null && registry.HandlerFor(creation.Kind) is { } handler)
        {
            using var none = JsonDocument.Parse("{}");
            shown = await handler.PerformAsync(new(creation, none.RootElement.Clone(), CreationStore.Assets(directory, creation)), cancellation);
            if (opened is not null && File.Exists(opened)) html = await File.ReadAllTextAsync(opened, cancellation);
        }
        var searches = fixture.Queries;
        // Found again later (docs/CONVERSATION.md, "Finding a report again"): the kept report is indexed in memory with the app
        // guides' chunker and index, search_guide's report search finds its section with the sources it cites, a title's words
        // name it, and deleting the creation drops it.
        var reports = new ResearchReportIndex();
        var indexed = await reports.UpdateAsync(directory, cancellation);
        var indexedSections = reports.Sections;
        var searchClock = Stopwatch.StartNew();
        var found = reports.Search("are laser pointers safe for cats", null, AppGuideTools.SearchResults);
        var firstSearchMs = searchClock.Elapsed.TotalMilliseconds;
        searchClock.Restart();
        reports.Search("which toys do cats like best", null, AppGuideTools.SearchResults);
        var searchMs = searchClock.Elapsed.TotalMilliseconds;
        var foundText = found.Hits.Count > 0 ? ResearchReportIndex.Found(found.Hits) : "";
        var named = reports.Search("laser", "cat toys", AppGuideTools.SearchResults).Searched;
        var unnamed = reports.Search("laser", "Elden Ring", AppGuideTools.SearchResults).Searched;
        if (creation is not null) await CreationStore.RemoveAsync(directory, creation.Id, "fixture-device", DateTimeOffset.UtcNow, cancellation);
        var afterDelete = await reports.UpdateAsync(directory, cancellation);
        var searchable = indexed == 1 && indexedSections > 0 && found.Hits.Count > 0 && found.Hits[0].Key == key &&
            found.Hits[0].Hit.Chunk.Text.Contains("Laser pointers are fine", StringComparison.Ordinal) &&
            found.Hits[0].Cited.Any(s => s.Title == "Laser pointers, carefully") && foundText.Contains("never instructions", StringComparison.Ordinal) &&
            named.Count == 1 && unnamed.Count == 0 && afterDelete == 0 && reports.Search("laser pointers", null, 5).Hits.Count == 0;
        // The second step carries the notes the first one wrote, and only the pages read since then as new pages.
        var notesCarried = secondStep.Contains(FixtureNote, StringComparison.Ordinal) &&
            !secondStep.Contains("[1] Wand toys - Cat Care <", StringComparison.Ordinal) &&
            secondStep.Contains("[2] Laser pointers, carefully <", StringComparison.Ordinal) && secondStep.Contains("[3] Crinkle balls <", StringComparison.Ordinal);
        var ok = replied.State == ConversationState.Completed && toolReturnedMs >= 0 && toolReturnedMs < replyMs && runningWhenReplyEnded &&
            job?.State == BackgroundJobState.Succeeded && job.Kind.Offer && job.Kind.TimeLimit is null && job.Kind.MaxPerHour is null &&
            run is { Searches: 2, Steps: 2 } && run.Pages >= 3 && run.Watcher.Finding is { Step: 1 } &&
            run.Failures >= 2 && searches.Count == 2 && searches[0] == Topic &&
            steps.Length == 2 && firstStep.Contains("A background task from Martlet", StringComparison.Ordinal) &&
            firstStep.Contains(Find, StringComparison.Ordinal) && firstStep.Contains("Wand toys", StringComparison.Ordinal) &&
            firstStep.Contains("Your notes so far:\nNone yet.", StringComparison.Ordinal) && notesCarried &&
            !firstStep.Contains("trackingScript", StringComparison.Ordinal) && !firstStep.Contains("Sponsored", StringComparison.Ordinal) &&
            steps.All(s => Encoding.UTF8.GetByteCount(s) < BoundedTextInput.HardMaxUtf8Bytes) &&
            results.Contains("go-ahead", StringComparison.Ordinal) && results.Contains("perform_creation", StringComparison.Ordinal) &&
            results.Contains(Summary, StringComparison.Ordinal) &&
            creation is { Kind: ResearchReports.KindName } && creation.Text?.Contains("## Sources", StringComparison.Ordinal) == true &&
            shown is { IsError: false } && html.Contains($"<a href=\"{fixture.BaseUrl}/page/1\"", StringComparison.Ordinal) &&
            !html.Contains("<script", StringComparison.OrdinalIgnoreCase) && fixture.Fetched("/page/5") == 0 && searchable;
        return (ok, new
        {
            ok,
            reply = new { state = replied.State.ToString(), toolReturnedMs, replyMs, researchStillRunning = runningWhenReplyEnded },
            job = job is null ? null : new
            {
                id = job.Id, kind = job.Kind.Name, state = job.State.ToString(), offer = job.Kind.Offer, doing = job.Kind.Doing,
                timeLimit = job.Kind.TimeLimit is { } limit ? BackgroundJobs.Duration(limit) : "none",
                perHour = job.Kind.MaxPerHour?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", atOnce = job.Kind.MaxActive,
                elapsedMs = (long)job.Elapsed.TotalMilliseconds, problem = job.Problem
            },
            run = run is null ? null : new
            {
                searches = run.Searches, pagesRead = run.Pages, unreadable = run.Failures, bytes = run.Bytes, modelSteps = run.Steps,
                notesCharacters = run.NotesCharacters,
                foundEarly = run.Watcher.Finding is { } early ? new { step = early.Step, pages = early.Sources.Count, answer = WebResearch.Spoken(early.Answer) } : null
            },
            searchQueries = searches,
            pagesFetched = fixture.Paths.Where(p => p.StartsWith("/page/", StringComparison.Ordinal)).ToArray(),
            privateRedirectFollowed = fixture.Fetched("/page/5") > 0,
            steps = steps.Select(s => new { bytes = Encoding.UTF8.GetByteCount(s), hasSources = s.Contains("[1]", StringComparison.Ordinal),
                carriesNotes = s.Contains(FixtureNote, StringComparison.Ordinal), last = s.Contains("This is the last step", StringComparison.Ordinal) }),
            notesCarried,
            note = results,
            creation = creation is null ? null : new { key = creation.Key, kind = creation.Kind, title = creation.Title, textCharacters = creation.Text?.Length, bytes = creation.Bytes },
            shown = shown?.Text, page = new { written = opened is not null, characters = html.Length, links = CountOf(html, "<a href=") },
            searchable = new
            {
                ok = searchable, reportsIndexed = indexed, sections = indexedSections, hits = found.Hits.Count,
                best = found.Hits.Count == 0 ? 0 : Math.Round(found.Hits[0].Hit.Relevance, 3),
                cited = found.Hits.Count == 0 ? [] : found.Hits[0].Cited.Select(s => s.Number).ToArray(),
                firstSearchMs = Math.Round(firstSearchMs, 3), searchMs = Math.Round(searchMs, 3),
                answerCharacters = foundText.Length, namedByTitleWords = named.Count, namedByOtherWords = unnamed.Count, reportsAfterDelete = afterDelete,
                searchGuide = new
                {
                    description = AppGuideTools.SearchDefinition.Description, parameters = JsonNode.Parse(AppGuideTools.SearchParametersJson),
                    bytes = Encoding.UTF8.GetByteCount(AppGuideTools.SearchDefinition.Name + AppGuideTools.SearchDefinition.Description +
                        AppGuideTools.SearchParametersJson)
                }
            },
            tool = new { name = WebResearch.Definition.Name, description = WebResearch.Definition.Description, parameters = JsonNode.Parse(WebResearch.ParametersJson) },
            prompt = WebResearch.Instructions(null)
        });
    }

    // One research at a time (a think may run beside it), no hourly limit, Cancel and a failed first search.
    private static async Task<(bool Ok, object Report)> LimitsAsync(CancellationToken cancellation)
    {
        using var jobs = new BackgroundJobs();
        var gate = new TaskCompletionSource<BackgroundJobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = jobs.Start(WebResearch.Kind, "first", (_, token) => gate.Task.WaitAsync(token));
        var second = jobs.Start(WebResearch.Kind, "second", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x")));
        var think = jobs.Start(ThinkLonger.Kind(new()), "beside", (_, token) => gate.Task.WaitAsync(token));
        var canceled = jobs.Cancel(first.Job?.Id, BackgroundJob.CanceledByYou);
        var waited = Stopwatch.StartNew();
        while (first.Job is { Finished: false } && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        gate.TrySetResult(BackgroundJobOutcome.Done("done"));
        waited.Restart();
        while (jobs.Active.Count > 0 && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        // No hourly limit: ten in a row each start once the one before is done.
        using var hourly = new BackgroundJobs();
        var refusals = new List<string?>();
        for (var i = 0; i < 10; i++)
        {
            refusals.Add(hourly.Start(WebResearch.Kind, "x", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x"))).Refusal);
            waited.Restart();
            while (hourly.Active.Count > 0 && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5, cancellation);
        }
        // Placed on Deep thinking's places (ThinkLonger.Places): a think holding the only place keeps research from starting
        // (the message names it). Research is a long job, so it never takes the pool's last free slot while the pool has two
        // or more slots: with one other place free it is refused too (the message says why) and a quick job (a screen summary)
        // takes that place at once; with two other places free, research runs on the one sharing least with the conversation.
        using var placed = new BackgroundJobs();
        var hold = new TaskCompletionSource<BackgroundJobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        BackgroundPlace[] one = [new("diva", "diva")], two = [.. one, new("imouto", "imouto", 1)], three = [.. two, new("ripley", "ripley", 2)];
        var holder = placed.Start(ThinkLonger.Kind(new(), 2), "holds diva", (_, token) => hold.Task.WaitAsync(token), one);
        var blocked = placed.Start(WebResearch.Kind, "x", (_, token) => hold.Task.WaitAsync(token), one);
        var lastSlot = placed.Start(WebResearch.Kind, "x", (_, token) => hold.Task.WaitAsync(token), two);
        string? quickOn;
        using (var quick = placed.Places.TryAcquire(two, "digest-1", demand: ThinkingDemand.For(ThinkingJobKind.Digest, two)))
            quickOn = quick?.Place.Name;
        var elsewhere = placed.Start(WebResearch.Kind, "x", (_, token) => hold.Task.WaitAsync(token), three);
        var researchPlace = elsewhere.Job?.Place?.Name;
        hold.TrySetResult(BackgroundJobOutcome.Done("done"));
        using var broken = new BackgroundJobs();
        var failing = broken.Start(WebResearch.Kind, "x", async (job, token) =>
        {
            var found = await new WebResearchRun(new BrokenSearch(), new BrokenSearch(), (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x")), null)
                .RunAsync(job, new(Topic, Find), token);
            return found.Report is null ? BackgroundJobOutcome.Failed(found.Problem!) : BackgroundJobOutcome.Done("x");
        }).Job!;
        waited.Restart();
        while (!failing.Finished && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        var ok = first.Started && second.Refusal == "busy" && think.Started && canceled is not null && first.Job!.State == BackgroundJobState.Canceled &&
            refusals.All(r => r is null) &&
            failing.State == BackgroundJobState.Failed && failing.Problem?.Contains("web search", StringComparison.Ordinal) == true &&
            holder.Job?.Place?.Name == "diva" && blocked.Refusal == "busy" && blocked.Message?.Contains("diva", StringComparison.Ordinal) == true &&
            lastSlot.Refusal == "busy" && lastSlot.Message?.Contains("The last free slot stays free for quick jobs.", StringComparison.Ordinal) == true &&
            quickOn == "imouto" && researchPlace == "imouto";
        return (ok, new
        {
            ok, secondRefused = second.Refusal, secondTold = second.Started ? null : WebResearch.Refused(second), thinkBeside = think.Started,
            canceled = first.Job?.State.ToString(), noHourlyLimit = new { started = refusals.Count(r => r is null), of = refusals.Count },
            failedSearch = failing.Problem,
            placement = new
            {
                thinkOn = holder.Job?.Place?.Name, researchRefused = blocked.Refusal, refusedBecause = blocked.Message,
                lastFreeSlot = new { researchRefused = lastSlot.Refusal, refusedBecause = lastSlot.Message, quickJobOn = quickOn },
                researchOn = researchPlace
            }
        });
    }

    // The production budget, about what a careful person reads to research something thoroughly: an in-memory web with endless
    // results and a fixture model (NOT AI) that adds a note for each new page and searches again until no more pages can be
    // read, then writes the report. Every step's task stays within one message, the notes within their bound, and the report
    // lists every page read.
    private static async Task<(bool Ok, object Report)> BudgetAsync(CancellationToken cancellation)
    {
        var budget = WebResearch.Budget;
        var web = new EndlessWeb();
        var tasks = new List<string>();
        var notes = new StringBuilder();
        WebResearchRun? run = null;
        ResearchOutcome? outcome = null;
        using var jobs = new BackgroundJobs();
        var job = jobs.Start(WebResearch.Kind, "budget", async (job, token) =>
        {
            run = new WebResearchRun(web, web, (task, _) =>
            {
                lock (tasks) tasks.Add(task);
                if (task.Contains("This is the last step", StringComparison.Ordinal) || task.Contains("no more can be read", StringComparison.Ordinal))
                    return Task.FromResult(BackgroundJobOutcome.Done(
                        "TITLE: Budget check\nSUMMARY: It read every page it could (FIXTURE - NOT AI).\nREPORT:\nEverything it read [1]."));
                foreach (Match page in NewPage.Matches(task))
                    notes.Append("- Page ").Append(page.Groups[1].Value).Append(" says what the fixture pages say about the topic, in a line ")
                        .Append("long enough to fill the notes [").Append(page.Groups[1].Value).Append("] (FIXTURE NOTE - NOT AI)\n");
                return Task.FromResult(BackgroundJobOutcome.Done($"NOTES:\n{notes}SEARCH: fixture query {tasks.Count}"));
            }, null);
            outcome = await run.RunAsync(job, new("fixture topic", "everything about it"), token);
            return outcome.Report is null ? BackgroundJobOutcome.Failed(outcome.Problem ?? "no report") : BackgroundJobOutcome.Done("report");
        }).Job!;
        var waited = Stopwatch.StartNew();
        while (!job.Finished && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(10, cancellation);
        var largest = tasks.Count == 0 ? 0 : tasks.Max(task => Encoding.UTF8.GetByteCount(task));
        var ok = job.State == BackgroundJobState.Succeeded && outcome?.Report is { } report && report.Sources.Count == budget.Pages &&
            run is { } done && done.Pages == budget.Pages && done.Searches <= budget.Searches && done.Steps <= budget.Steps &&
            done.NotesCharacters <= budget.NotesCharacters && largest <= WebResearch.MaxTaskBytes &&
            WebResearch.Kind is { MaxActive: 1, MaxPerHour: null, TimeLimit: null };
        return (ok, new
        {
            ok,
            budget = new
            {
                searches = budget.Searches, pages = budget.Pages, modelSteps = budget.Steps, megabytes = budget.Bytes / 1_000_000,
                pagesFirstRead = budget.PagesFirstRead, pagesPerStep = budget.PagesPerStep, notesCharacters = budget.NotesCharacters,
                timeLimit = "none", perHour = "none", atOnce = WebResearch.Kind.MaxActive
            },
            run = run is null ? null : new
            {
                searches = run.Searches, pagesRead = run.Pages, modelSteps = run.Steps, bytes = run.Bytes, notesCharacters = run.NotesCharacters
            },
            reportSources = outcome?.Report?.Sources.Count, largestTaskBytes = largest, maxTaskBytes = WebResearch.MaxTaskBytes,
            problem = outcome?.Problem
        });
    }

    // The levels (docs/CONVERSATION.md, Web research): the tool's depth and the reply's prompt that says when to answer from what
    // Martlet knows, search a guide or report, look up quickly or research; each depth's budget and job kind (a quick look-up runs
    // beside research, before it in the Thinking pool's line, on a pool member that may be a paired computer); a quick look-up
    // that ends on the watcher's answer, found deep in a long page; full research whose early finding is a notice while it goes
    // on; and the source check, which starts only after the report's result is ready and adds its section to the kept report.
    private static async Task<(bool Ok, object Report)> LevelsAsync(string directory, CancellationToken cancellation)
    {
        using var parameters = JsonDocument.Parse(WebResearch.ParametersJson);
        var depths = parameters.RootElement.GetProperty("properties").GetProperty("depth").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        var prompt = WebResearch.Instructions(null) ?? "";
        string[] rungs = ["search_guide", "depth quick", "depth full"];
        var ladder = rungs.All(rung => prompt.Contains(rung, StringComparison.Ordinal));
        var parsed = new[] { "quick", "full", "deep", null }.Select(depth =>
            WebResearch.Parse(JsonSerializer.Serialize(new { topic = "t", what_to_find = "f", depth })).Arguments!.Depth.ToString()).ToArray();
        var budgets = new[] { ResearchDepth.Quick, ResearchDepth.Full, ResearchDepth.Deep }.Select(depth => (Depth: depth, Limits: WebResearch.BudgetFor(depth)))
            .ToArray();
        var lookupPriority = ThinkingDemand.For(ThinkingJobKind.Research, null, WebResearch.LookupKind.PoolPriority).Priority;
        var researchPriority = ThinkingDemand.For(ThinkingJobKind.Research).Priority;

        // Placed on the Thinking pool's places (here diva, a paired computer, and imouto): a quick look-up starts beside full
        // research instead of being refused.
        using var placed = new BackgroundJobs();
        var hold = new TaskCompletionSource<BackgroundJobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        BackgroundPlace[] places = [new("diva", "diva"), new("imouto", "imouto", 1), new("ripley", "ripley", 2)];
        var research = placed.Start(WebResearch.Kind, "x", (_, token) => hold.Task.WaitAsync(token), places);
        var lookup = placed.Start(WebResearch.LookupKind, "x", (_, token) => hold.Task.WaitAsync(token), places);
        var placement = new { research = research.Job?.Place?.Name, lookup = lookup.Job?.Place?.Name, lookupRefused = lookup.Refusal };
        hold.TrySetResult(BackgroundJobOutcome.Done("done"));

        // A quick look-up: the answer is deep in a long page, so only a step shown the page's matching parts can find it.
        using var jobs = new BackgroundJobs();
        var quickTasks = new List<string>();
        ResearchOutcome? quickOutcome = null;
        WebResearchRun? quickRun = null;
        var quick = jobs.Start(WebResearch.LookupKind, "iron ore", async (job, token) =>
        {
            quickRun = new WebResearchRun(new GameWeb(), new GameWeb(), (task, _) =>
            {
                lock (quickTasks) quickTasks.Add(task);
                return Task.FromResult(BackgroundJobOutcome.Done(task.Contains("stone pickaxe", StringComparison.Ordinal)
                    ? "NOTES:\n- Iron ore needs a stone pickaxe [1] (FIXTURE NOTE - NOT AI)\nFOUND: Mine iron ore with a stone pickaxe [1] (FIXTURE - NOT AI).\nSEARCH: more"
                    : "NOTES:\n- Nothing yet (FIXTURE NOTE - NOT AI)\nSEARCH: iron ore pickaxe"));
            }, null);
            quickOutcome = await quickRun.RunAsync(job, new("minecraft iron ore", "how to get iron ore", ResearchDepth.Quick), token);
            return quickOutcome.Answer is { } answer ? BackgroundJobOutcome.Done(WebResearch.Answered(answer)) : BackgroundJobOutcome.Failed("no answer");
        }).Job!;
        await WaitAsync(quick, cancellation);

        // Full research: the first step finds the answer; the watcher's notice is out while the second step still runs.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = 0;
        BackgroundJob? notice = null;
        string? key = null;
        long addedRevision = 0;
        ResearchReport? report = null;
        var registry = new CreationRegistry();
        registry.Register(ResearchReports.Kind);
        var watcher = new ResearchWatcher();
        var arguments = new ResearchArguments("minecraft iron ore", "how to get iron ore");
        var full = jobs.Start(WebResearch.Kind, "iron ore", async (job, token) =>
        {
            watcher.Found += finding => notice = jobs.Start(WebResearch.FindingKind, WebResearch.Label(arguments.Topic),
                (_, _) => Task.FromResult(BackgroundJobOutcome.Done(WebResearch.FindingNote(job.Id, arguments, finding)))).Job;
            var found = await new WebResearchRun(new GameWeb(), new GameWeb(), async (task, stepToken) =>
            {
                if (Interlocked.Increment(ref steps) == 1)
                    return BackgroundJobOutcome.Done("NOTES:\n- Stone pickaxe [1] (FIXTURE NOTE - NOT AI)\nFOUND: Mine iron ore with a stone pickaxe [1] " +
                        "(FIXTURE - NOT AI).\nSEARCH: iron ore depth");
                await gate.Task.WaitAsync(stepToken);
                return BackgroundJobOutcome.Done("TITLE: Iron ore (FIXTURE - NOT AI)\nSUMMARY: Mine it with a stone pickaxe.\nREPORT:\n" +
                    "Mine iron ore with a stone pickaxe [1]. Iron ore glows in the dark [1].");
            }, null, watcher: watcher).RunAsync(job, arguments, token);
            report = found.Report;
            if (report is null) return BackgroundJobOutcome.Failed(found.Problem ?? "no report");
            var added = await CreationStore.AddAsync(directory, ResearchReports.Draft(report, new CreationAuthor { Device = "fixture-device" }), registry,
                DateTimeOffset.UtcNow, token);
            (key, addedRevision) = (added.Key, added.Revision);
            return BackgroundJobOutcome.Done(WebResearch.Ready(report, key));
        }).Job!;
        var waited = Stopwatch.StartNew();
        while (notice is not { Finished: true } && !full.Finished && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, cancellation);
        var noticeBeforeReport = notice is { Finished: true } && !full.Finished;
        var noticeNotes = notice is null ? "" : BackgroundJobs.ReportNotes(null, [notice]) ?? "";
        var noticeWaits = notice?.Delivery == BackgroundDeliveryState.Pending;
        gate.TrySetResult();
        await WaitAsync(full, cancellation);

        // The source check starts after the report's result is ready (on a pool place, quiet) and never holds it back.
        var verdicts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SourceCheckResult? result = null;
        var checking = report is null || key is null ? null : jobs.Start(SourceCheck.Kind, WebResearch.Label(report.Title), async (job, token) =>
        {
            result = await SourceCheck.RunAsync(report, async (task, stepToken) =>
            {
                await verdicts.Task.WaitAsync(stepToken);
                return BackgroundJobOutcome.Done("1: SUPPORTED\n2: NOT SUPPORTED: the page doesn't say it glows (FIXTURE - NOT AI)");
            }, null, token, job);
            var creation = CreationStore.View(directory).Resolve(key)!;
            var (text, assets) = ResearchReports.Content(SourceCheck.Revised(report.Markdown, result));
            await CreationStore.ReviseAsync(directory, creation.Id, creation.Summary, text, assets, registry, "fixture-device", DateTimeOffset.UtcNow, token);
            return BackgroundJobOutcome.Done(SourceCheck.Note(result, report.Title, key));
        }, [places[0]], wait: true).Job;
        await Task.Delay(50, cancellation);
        var resultReadyFirst = full.State == BackgroundJobState.Succeeded && full.Result?.Contains("\"status\":\"ready\"", StringComparison.Ordinal) == true &&
            checking is { Finished: false };
        verdicts.TrySetResult();
        if (checking is not null) await WaitAsync(checking, cancellation);
        var kept = key is null ? null : CreationStore.View(directory).Resolve(key);
        var keptText = kept?.Text ?? "";
        var reportBytes = kept is null ? null : await CreationStore.ReadAssetAsync(directory, kept, "report", cancellation);
        var section = reportBytes is null ? "" : Encoding.UTF8.GetString(reportBytes);

        var ok = depths.SequenceEqual(["quick", "full", "deep"]) && ladder && parsed.SequenceEqual(["Quick", "Full", "Deep", "Full"]) &&
            budgets[0].Limits.Pages < budgets[1].Limits.Pages && budgets[2].Limits.Pages > budgets[1].Limits.Pages &&
            lookupPriority > researchPriority && WebResearch.LookupKind.MaxActive == 2 && !WebResearch.LookupKind.Offer &&
            placement.research is not null && placement.lookup is not null && placement.lookup != placement.research &&
            quick.State == BackgroundJobState.Succeeded && quickOutcome?.Answer is { Step: 1, Settled: true } && quickRun is { Steps: 1 } &&
            quickTasks.Count == 1 && quickTasks[0].Contains(WebResearch.PartsCut.Trim(), StringComparison.Ordinal) &&
            quick.Result?.Contains("What you found: Mine iron ore with a stone pickaxe", StringComparison.Ordinal) == true &&
            noticeBeforeReport && noticeWaits && notice?.Kind.Notice == true &&
            noticeNotes.Contains("already found what the user wanted", StringComparison.Ordinal) &&
            noticeNotes.Contains("Mine iron ore with a stone pickaxe", StringComparison.Ordinal) && !noticeNotes.Contains("[1]", StringComparison.Ordinal) &&
            full.State == BackgroundJobState.Succeeded && resultReadyFirst && checking is { State: BackgroundJobState.Succeeded, Quiet: true } &&
            result is { Supported: 1, NotSupported: 1, Requests: 1 } && kept?.Revision > addedRevision &&
            section.Contains(SourceCheck.SectionHeading, StringComparison.Ordinal) && keptText.Contains(SourceCheck.SectionHeading, StringComparison.Ordinal) &&
            section.IndexOf(SourceCheck.SectionHeading, StringComparison.Ordinal) < section.IndexOf("## Sources", StringComparison.Ordinal) &&
            checking.Result?.Contains("not supported", StringComparison.Ordinal) == true;
        return (ok, new
        {
            ok,
            tool = new { depths, ladder, parsed },
            budgets = budgets.Select(b => new { depth = b.Depth.ToString().ToLowerInvariant(), searches = b.Limits.Searches, pages = b.Limits.Pages, steps = b.Limits.Steps }),
            kinds = new
            {
                lookup = new { name = WebResearch.LookupKind.Name, atOnce = WebResearch.LookupKind.MaxActive, offer = WebResearch.LookupKind.Offer,
                    timeLimit = BackgroundJobs.Duration(WebResearch.LookupKind.TimeLimit!.Value), priority = lookupPriority, researchPriority,
                    pool = WebResearch.LookupKind.PoolKind?.ToString() },
                finding = new { name = WebResearch.FindingKind.Name, notice = WebResearch.FindingKind.Notice,
                    spokenAs = UnpromptedSpeech.Of(WebResearch.FindingKind).ToString() },
                sourceCheck = new { name = SourceCheck.Kind.Name, quiet = SourceCheck.Kind.Quiet, pool = SourceCheck.Kind.PoolKind?.ToString() }
            },
            placement,
            quick = new
            {
                state = quick.State.ToString(), steps = quickRun?.Steps, searches = quickRun?.Searches, pages = quickRun?.Pages,
                answerStep = quickOutcome?.Answer?.Step, settled = quickOutcome?.Answer?.Settled,
                pageShownByParts = quickTasks.Count > 0 && quickTasks[0].Contains(WebResearch.PartsCut.Trim(), StringComparison.Ordinal),
                result = quick.Result
            },
            watcher = new
            {
                noticeBeforeReport, noticeId = notice?.Id, noticeWaitsForMartlet = noticeWaits, noticeNotes,
                researchState = full.State.ToString(), steps
            },
            sourceCheck = new
            {
                resultReadyFirst, id = checking?.Id, state = checking?.State.ToString(), quiet = checking?.Quiet, placedOn = places[0].Name,
                claims = result?.Claims.Count, supported = result?.Supported, notSupported = result?.NotSupported, requests = result?.Requests,
                revised = kept?.Revision > addedRevision, sectionInReport = section.Contains(SourceCheck.SectionHeading, StringComparison.Ordinal), note = checking?.Result
            }
        });
    }

    private static async Task WaitAsync(BackgroundJob job, CancellationToken cancellation)
    {
        var waited = Stopwatch.StartNew();
        while (!job.Finished && waited.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10, cancellation);
    }

    /// <summary>An in-memory web about a game (NOT the internet): every search finds the same three pages, each long, with the
    /// answer only near the end of the first one.</summary>
    private sealed class GameWeb : IWebSearch, IWebFetch
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            IReadOnlyList<WebSearchResult> found = [.. Enumerable.Range(1, 3).Select(i =>
                new WebSearchResult($"Game wiki {i} (FIXTURE)", $"https://game.example/{i}", "A fixture snippet (NOT AI)."))];
            return Task.FromResult(found);
        }

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            var filler = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"Line {i} of the fixture wiki about farming and boats (NOT AI)."));
            var text = url.AbsolutePath == "/1" ? "Fixture wiki: ores.\n" + filler + "\nTo get iron ore, mine it with a stone pickaxe (FIXTURE - NOT AI)." : filler;
            return Task.FromResult(new WebPage(url.AbsoluteUri, "Game wiki " + url.AbsolutePath.Trim('/'), text, text.Length));
        }
    }

    private static readonly Regex NewPage = new(@"^\[(\d+)\] ", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>An in-memory web (NOT the internet): every search finds 8 new results, and every page has about 5,000 characters
    /// of text.</summary>
    private sealed class EndlessWeb : IWebSearch, IWebFetch
    {
        private int searches;

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            var round = Interlocked.Increment(ref searches);
            IReadOnlyList<WebSearchResult> found = [.. Enumerable.Range(0, 8).Select(i =>
                new WebSearchResult($"Fixture result {round}-{i}", $"https://fixture.example/{round}/{i}", "A fixture snippet (NOT AI)."))];
            return Task.FromResult(found);
        }

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            var text = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"Line {i} of the fixture page {url.AbsolutePath}, about the topic (NOT AI)."));
            return Task.FromResult(new WebPage(url.AbsoluteUri, "Fixture page " + url.AbsolutePath, text, text.Length));
        }
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Last(string body) =>
        JsonNode.Parse(body)?["messages"]?.AsArray().LastOrDefault()?["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static readonly TextGenerationLimits ReplyLimits = new()
    {
        MaxInputBytes = BoundedTextInput.HardMaxInputUtf8Bytes, MaxInputTokens = 181_968, MaxOutputTokens = GenerationSettings.ChatReplyTokens,
        MaxContextTokens = 181_968 + GenerationSettings.ChatReplyTokens, MaxHistoryMessages = BoundedTextInput.HardMaxHistoryMessages,
        MaxEvents = 4094, MaxStreamBytes = 4_194_304, MaxRequestTime = TimeSpan.FromSeconds(45)
    };

    // A step's request as the desktop builds it with the Thinking model (LiveConversationConfiguration.ThinkRequest): the reply's
    // request continued, then the step's task.
    private static ConversationRequest StepRequest(Fixture fixture, BoundedTextInput conversation, string reply, string task,
        ThinkLongerSettings settings, TimeSpan left)
    {
        var input = ThinkLonger.Input(conversation, reply, task, null, null);
        return new(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model), ThinkLonger.Limits(ReplyLimits, settings.HowHard, left),
            ThinkLonger.TurnLimits(left), chat: new ChatCompletionsTarget(fixture.BaseUrl + "/v1", true),
            generation: ThinkLonger.Generation(new GenerationSettings { Reasoning = false }, settings.HowHard, false),
            tools: input.Tools.Count > 0 ? ThinkLonger.NoTools : null);
    }

    private sealed class BrokenSearch : IWebSearch, IWebFetch
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new WebResearchException("it answered 503");

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken) => throw new WebResearchException("it answered 503");
    }

    private sealed class Tools(Func<TextToolCall, ConversationToolResult> call) : IConversationToolHost
    {
        public ValueTask<ConversationToolResult> CallAsync(TextToolCall textCall, CancellationToken cancellationToken) =>
            ValueTask.FromResult(call(textCall));
    }

    private sealed class NoCredentials : IProviderCredentialSource
    {
        public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken) =>
            ValueTask.FromResult<BoundProviderCredential?>(null);
    }

    private sealed class Permissions(Uri baseUri) : IConversationAuthorizationSource
    {
        public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(
                new(baseUri, ProviderRole.Llm, action.Model.UpstreamModelId), action.Model, action.Context.Ids, action.Context.Epoch,
                action.Limits, action.Context.Deadline, true, true), new(action.Budget, action.Context.Deadline)));

        public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedSpeechOperation?>(null);
    }

    /// <summary>One HTTP server on 127.0.0.1 (NOT AI, canned): /search answers like DuckDuckGo's HTML search (an ad, results
    /// behind its redirect links), /page/N are web pages (one with scripts and navigation, one a PDF, one redirecting to a
    /// private address, one never linked), and /v1/chat/completions is the model: the reply says it'll look into it and calls
    /// research; a research step asks for another search and a page first, then writes the report.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LoopbackHttpServer server;
        private readonly List<(string Kind, string Body)> bodies = [];
        private readonly List<string> paths = [];
        private readonly List<string> queries = [];

        internal Fixture() => server = new LoopbackHttpServer(HandleAsync, concurrent: true);

        internal string BaseUrl => server.Origin;
        internal IReadOnlyList<string> Queries { get { lock (paths) return [.. queries]; } }
        internal IReadOnlyList<string> Paths { get { lock (paths) return [.. paths]; } }
        internal int Fetched(string path) { lock (paths) return paths.Count(p => p == path); }
        internal IReadOnlyList<string> Bodies(string kind) { lock (paths) return [.. bodies.Where(b => b.Kind == kind).Select(b => b.Body)]; }

        private async Task HandleAsync(LoopbackExchange exchange)
        {
            var (method, target, body) = (exchange.Request.Method, exchange.Request.Target, exchange.Request.Text);
            var path = exchange.Request.Path;
            lock (paths)
            {
                paths.Add(path);
                if (path == "/search")
                    queries.Add(Uri.UnescapeDataString(target.Split("q=", 2).ElementAtOrDefault(1)?.Split('&')[0] ?? "").Replace('+', ' '));
            }
            if (method == "POST" && path == "/v1/chat/completions") await ChatAsync(exchange, body);
            else if (path == "/search") await PageAsync(exchange, "text/html", SearchPage());
            else if (path == "/page/1") await PageAsync(exchange, "text/html", Page("Wand toys - Cat Care", "Wand toys let cats stalk and pounce like they hunt prey. Most cats play with them daily.", script: true));
            else if (path == "/page/2") await PageAsync(exchange, "application/pdf", "%PDF-1.4");
            else if (path == "/page/3") await exchange.RespondAsync(302, "", "", ("Location", "http://192.168.1.1/page/5"));
            else if (path == "/page/4") await PageAsync(exchange, "text/html", Page("Crinkle balls", "Crinkle balls make a sound cats find exciting; they bat them around for hours."));
            else if (path == "/page/6") await PageAsync(exchange, "text/html", Page("Laser pointers, carefully", "Laser pointers should end on a real toy so the cat can catch something."));
            else await exchange.RespondAsync(404, "", "");
        }

        private string Link(string path) => "//duckduckgo.com/l/?uddg=" + Uri.EscapeDataString(BaseUrl + path) + "&amp;rut=fixture";

        private string SearchPage() =>
            "<html><body><div class=\"results\">" +
            "<div class=\"result result--ad\"><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/y.js?ad=1\">Sponsored cat stuff</a></div>" +
            Result("/page/1", "Wand toys - Cat Care", "Wand <b>toys</b> imitate prey.") + Result("/page/2", "Cat toy guide (PDF)", "A PDF guide.") +
            Result("/page/3", "Toy reviews", "Reviews of cat toys.") + Result("/page/4", "Crinkle balls", "Cats love the sound.") +
            "</div></body></html>";

        private string Result(string path, string title, string snippet) =>
            $"<div class=\"result\"><h2 class=\"result__title\"><a rel=\"nofollow\" class=\"result__a\" href=\"{Link(path)}\">{title}</a></h2>" +
            $"<a class=\"result__snippet\" href=\"{Link(path)}\">{snippet}</a></div>";

        private static string Page(string title, string text, bool script = false) =>
            $"<html><head><title>{title}</title><script>var trackingScript = 1;</script></head><body><nav>Home | About</nav>" +
            $"<article><h1>{title}</h1><p>{text}</p><p>FIXTURE - NOT AI web page for research_check.</p></article>" +
            (script ? "<script>trackingScript()</script>" : "") + "<footer>Copyright</footer></body></html>";

        private async Task ChatAsync(LoopbackExchange exchange, string body)
        {
            var last = Last(body);
            var role = (string?)JsonNode.Parse(body)?["messages"]?.AsArray().LastOrDefault()?["role"];
            var kind = role == "tool" ? "after-tool" : last.Contains("A background task from Martlet", StringComparison.Ordinal) ? "step" : "reply";
            lock (paths) bodies.Add((kind, body));
            await exchange.StartAsync();
            switch (kind)
            {
                case "reply":
                    await ChunkAsync(exchange, "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(Acknowledged) + "}");
                    var arguments = JsonSerializer.Serialize(new { topic = Topic, what_to_find = Find });
                    await ChunkAsync(exchange, "{\"tool_calls\":[{\"index\":0,\"id\":\"call_research\",\"type\":\"function\",\"function\":" +
                        "{\"name\":\"research\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}");
                    await FinishAsync(exchange, "tool_calls");
                    break;
                case "after-tool":
                    await FinishAsync(exchange, "stop");
                    break;
                default:
                    var answer = last.Contains("step 1 of", StringComparison.Ordinal)
                        ? $"NOTES:\n- {FixtureNote}\nFOUND: {Found}\nSEARCH: laser pointer cats safe\nREAD: {BaseUrl}/page/6"
                        : "TITLE: Toys cats like best\nSUMMARY: " + Summary + "\nREPORT:\nMost cats prefer toys that move like prey: wand toys [1] " +
                          "and crinkle balls [3]. Laser pointers are fine if the game ends on a toy they can catch [2].";
                    await ChunkAsync(exchange, "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(answer) + "}");
                    await FinishAsync(exchange, "stop");
                    break;
            }
        }

        private static Task PageAsync(LoopbackExchange exchange, string type, string content) =>
            exchange.RespondAsync(200, content, type + "; charset=utf-8");

        private static Task ChunkAsync(LoopbackExchange exchange, string delta) => exchange.WriteAsync(FixtureChat.Delta(delta));

        private static Task FinishAsync(LoopbackExchange exchange, string reason) => exchange.WriteAsync(FixtureChat.Finish(reason));

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
