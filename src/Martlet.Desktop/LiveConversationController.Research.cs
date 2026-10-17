using Martlet.Conversation;
using Martlet.Core.Creations;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Web research (research): a background job that searches the web and reads pages off the reply path, asks the model one
// bounded step at a time on the Deep thinking place the job list placed it on (held for the whole job, with that place's own
// runtime and authorization), keeps the report as a creation and offers it (docs/CONVERSATION.md, Web research).
internal sealed partial class LiveConversationController
{
    /// <summary>Makes the web client a research job uses (Martlet's own DuckDuckGo search and page reader; checks replace it).</summary>
    internal Func<WebAccess> ResearchWeb { get; init; } = () => new WebAccess();

    /// <summary>Whether replies get research: Web research and Thinking longer on, a Thinking route that does function calling
    /// and somewhere for Deep thinking to think, with every computer counted as online (the tools stay the same while computers
    /// come and go).</summary>
    internal bool OffersResearch(LiveConversationConfiguration configured) =>
        configured.OffersThinkLonger && configured.ThinkLonger.Researches && DeepPlan(configured).Available;

    /// <summary>research: starts the research job on a free Deep thinking place and returns at once, telling the model to tell
    /// the user now unless it did.</summary>
    private ConversationToolResult Research(LiveConversationOperation operation, LiveConversationConfiguration configured, TextToolCall call) =>
        StartResearch(configured, operation.Sent, () => operation.Turn?.Content.Text, !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text),
            call, checkIn: null);

    /// <summary>Starts a research job whose steps continue <paramref name="sent"/> and what the reply said (<paramref name="reply"/>).
    /// <paramref name="checkIn"/>: the check-in that started it after the exchange (null: the reply's research), which gets its
    /// own words back.</summary>
    private ConversationToolResult StartResearch(LiveConversationConfiguration configured, BoundedTextInput? sent, Func<string?> reply,
        bool toldUser, TextToolCall call, string? checkIn)
    {
        const string server = "Martlet";
        var (arguments, problem) = WebResearch.Parse(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record(server, WebResearch.Name, "invalid arguments", "", true);
            return new(checkIn is null ? problem! : BackgroundWorkTools.NotStarted("invalid arguments") + "\n" + problem, true);
        }
        var label = WebResearch.Label(arguments.Topic);
        if (!configured.ThinkLonger.Researches)
            return new(checkIn is null ? WebResearch.TurnedOff : BackgroundWorkTools.NotStarted("Web research is off"), true);
        // Members whose computers are offline now can't start it; they stay in the list, so the broker places it on one once it
        // answers again.
        var live = LivePool(configured);
        var plan = live.Plan;
        if (!plan.Available)
        {
            tools?.Record(server, WebResearch.Name, "not started: unavailable", label, false);
            ErrorLog.Info($"Web research: a new job wasn't started ({plan.Why})");
            return new(checkIn is null ? WebResearch.Unavailable(plan.Why)
                : BackgroundWorkTools.NotStarted("Deep thinking can't run right now") + "\n" + plan.Why, true);
        }
        var pool = Placing(DeepPool(configured), live);
        var thinkingModel = configured.Route(SetupRole.Llm).ModelId;
        var runsOn = LongRunsOn(ThinkLonger.Places(pool, BackgroundDuties.Of(dataDirectory), PoolCan, HostRouteGpus.For, Volatile.Read(ref thinkingPool)),
            ThinkingJobKind.Research);
        if (runsOn.Why is { } none)
        {
            tools?.Record(server, WebResearch.Name, "not started: unavailable", label, false);
            ErrorLog.Info($"Web research: a new job wasn't started ({none})");
            return new(checkIn is null ? WebResearch.Unavailable(none)
                : BackgroundWorkTools.NotStarted("Deep thinking can't run right now") + "\n" + none, true);
        }
        var depth = arguments.Depth;
        var budget = WebResearch.BudgetFor(depth);
        var kind = WebResearch.KindFor(depth);
        var limit = kind.TimeLimit is { } most ? $"at most {BackgroundJobs.Duration(most)}" : "no time limit";
        var start = jobs.Start(kind with { SmartFirst = runsOn.SmartFirst }, label, (job, token) =>
            ResearchAsync(job, arguments, configured, pool, runsOn.Places, sent, reply, thinkingModel, token),
            runsOn.Places);
        if (start.Job is not { } started)
        {
            tools?.Record(server, WebResearch.Name, "not started: " + start.Refusal, label, false);
            ErrorLog.Info($"Web research: a new {Depth(depth)} job wasn't started ({start.Refusal}: {start.Message})");
            return new(checkIn is null ? WebResearch.Refused(start) : BackgroundWorkTools.NotStarted(start.Refusal ?? "unavailable") +
                (start.Message is { } refusal ? "\n" + refusal : ""), true);
        }
        var by = checkIn is null ? (toldUser ? "." : " The reply hadn't told you yet, so it was asked to.") : $" (started by the check-in {checkIn}).";
        tools?.Record(server, WebResearch.Name, "started " + started.Id, label, false);
        if (started.Place is not { } seat)
        {
            // Every place it can run on is kept free for the conversation: it starts once the conversation pauses.
            ErrorLog.Info($"Web research: {started.Id} ({Depth(depth)}) waits for the conversation (the live floor keeps its places free " +
                $"while you talk; {limit} once it starts)" +
                by);
            return new(checkIn is null ? WebResearch.Started(started, toldUser, depth) : BackgroundWorkTools.Started(started, "", forConversation: true));
        }
        var chosen = pool.Find(seat.Id)!;
        ErrorLog.Info($"Web research: started {started.Id} ({Depth(depth)}), thinking on {(chosen.Settings.Separate ? chosen.Settings.Describe() : thinkingModel)} " +
            $"(placed on {chosen.Computer}; {limit}, at most {budget.Searches} searches, {budget.Pages} pages " +
            $"and {budget.Steps} model steps)" +
            by);
        return new(checkIn is null ? WebResearch.Started(started, toldUser, depth) : BackgroundWorkTools.Started(started));
    }

    private static string Depth(ResearchDepth depth) => depth switch
    {
        ResearchDepth.Quick => "quick look-up",
        ResearchDepth.Deep => "deep research",
        _ => "full research"
    };

    // The research job: runs the loop on the job's place (OnPlaceAsync), each step a background think continuing the reply's
    // request (PoolThink). Full and deep research tell the user early once the watcher sees the answer (TellFinding), keep the
    // report as a creation, and only then check its sources in a job of their own (CheckSources), so the result never waits for
    // the check. A quick look-up's result is the answer itself.
    private async Task<BackgroundJobOutcome> ResearchAsync(BackgroundJob job, ResearchArguments arguments, LiveConversationConfiguration configured,
        DeepThinkingPool pool, IReadOnlyList<BackgroundPlace> places, BoundedTextInput? sent, Func<string?> reply, string thinkingModel,
        CancellationToken token)
    {
        var spot = pool.Find(job.Place!.Id)!;
        var thinkingRoute = configured.Routes.ThinkingRoute();
        using var web = ResearchWeb();
        var watcher = new ResearchWatcher();
        BackgroundJob? notice = null;
        if (arguments.Depth != ResearchDepth.Quick)
            watcher.Found += finding => Volatile.Write(ref notice, TellFinding(job, arguments, finding));
        // How much a step's task may take on the place the job thinks on now: Deep thinking's own destination, or the Thinking
        // model's context (Same as Thinking).
        int TaskBytes()
        {
            var here = job.Place is { } at ? pool.Find(at.Id) ?? spot : spot;
            return WebResearch.TaskBytes(here.Settings.Separate
                ? DeepThinkTarget.For(here.Settings.Single, configured.ThinkLonger.HowHard, thinkingRoute, ModelLimits.Load(dataDirectory))
                    .Bounds.MaxInputTokens
                : configured.TextInputTokens);
        }
        var run = new WebResearchRun(web, web, PoolThink("Web research", job, configured, pool, spot, sent, reply, thinkingModel),
            configured.Prompts, taskBytes: TaskBytes, watcher: watcher);
        // Counts only: never the topic, a query, a link or what was read.
        return await OnPlaceAsync("Web research", job, configured, pool, thinkingModel, async guard =>
        {
            var found = await run.RunAsync(job, arguments, guard).ConfigureAwait(false);
            if (arguments.Depth == ResearchDepth.Quick)
                return (found.Answer ?? (found.Report is { } notes ? WebResearch.Unsettled(notes) : null)) is { } answer
                    ? BackgroundJobOutcome.Done(WebResearch.Answered(answer))
                    : BackgroundJobOutcome.Failed(found.Problem ?? "it found nothing usable");
            if (found.Report is not { } report) return BackgroundJobOutcome.Failed(found.Problem ?? "it found nothing usable");
            job.Report(BackgroundJobState.Running, "Saving the report");
            var key = await KeepReportAsync(job, report, configured, token).ConfigureAwait(false);
            if (key is not null) CheckSources(job, report, key, configured, pool, places, sent, reply, thinkingModel);
            return BackgroundJobOutcome.Done(WebResearch.Ready(report, key, ToldEarly(Volatile.Read(ref notice))));
        }, () => $"{Depth(arguments.Depth)}, {run.Searches} searches, {run.Pages} pages read, {run.Failures} unreadable, " +
            $"{run.Bytes / 1024} KiB downloaded, {run.Steps} model steps, {run.NotesCharacters} characters of notes" +
            (watcher.Finding is { } early ? $", answer found at step {early.Step}" : ""), token).ConfigureAwait(false);
    }

    // The watcher saw full or deep research find what the user wanted: a notice tells them as soon as Martlet is free (or with
    // what they say next) while the research goes on for the report. Null when the notice couldn't start.
    private BackgroundJob? TellFinding(BackgroundJob research, ResearchArguments arguments, ResearchFinding finding)
    {
        var notice = jobs.Start(WebResearch.FindingKind, WebResearch.Label(arguments.Topic),
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done(WebResearch.FindingNote(research.Id, arguments, finding))));
        ErrorLog.Info(notice.Job is { } told
            ? $"Web research: {research.Id} found what you asked for at step {finding.Step} ({finding.Sources.Count} pages back it); " +
              $"{told.Id} tells you as soon as Martlet is free, and the research goes on for the report."
            : $"Web research: {research.Id} found what you asked for at step {finding.Step}, but couldn't tell you early ({notice.Refusal}).");
        return notice.Job;
    }

    // Whether the user heard the early finding (a reply carries or carried it). One still waiting when the report is ready is
    // dropped: the report's note brings the answer instead, so it isn't said twice.
    private bool ToldEarly(BackgroundJob? notice)
    {
        if (notice is null) return false;
        if (jobs.Drop(waiting => ReferenceEquals(waiting, notice)).Count > 0) return false;
        return notice.Delivery is BackgroundDeliveryState.Reserved or BackgroundDeliveryState.Delivered;
    }

    // After the report was kept (and its result is on its way to the conversation): a quiet job checks its sources on one of the
    // same places once one is free, and adds what it found to the kept report.
    private void CheckSources(BackgroundJob research, ResearchReport report, string key, LiveConversationConfiguration configured,
        DeepThinkingPool pool, IReadOnlyList<BackgroundPlace> places, BoundedTextInput? sent, Func<string?> reply, string thinkingModel)
    {
        var start = jobs.Start(SourceCheck.Kind with { SmartFirst = research.Kind.SmartFirst }, WebResearch.Label(report.Title), (job, token) =>
            CheckSourcesAsync(job, report, key, configured, pool, sent, reply, thinkingModel, token), places, wait: true);
        ErrorLog.Info(start.Job is { } check
            ? $"Web research: {research.Id}'s report is ready; {check.Id} checks its sources after it ({SourceCheck.Claims(report).Count} cited claims)."
            : $"Web research: {research.Id}'s sources aren't checked ({start.Refusal}: {start.Message}).");
    }

    private async Task<BackgroundJobOutcome> CheckSourcesAsync(BackgroundJob job, ResearchReport report, string key,
        LiveConversationConfiguration configured, DeepThinkingPool pool, BoundedTextInput? sent, Func<string?> reply, string thinkingModel,
        CancellationToken token)
    {
        var spot = pool.Find(job.Place!.Id)!;
        SourceCheckResult? result = null;
        return await OnPlaceAsync("Source check", job, configured, pool, thinkingModel, async guard =>
        {
            var checking = await SourceCheck.RunAsync(report, PoolThink("Source check", job, configured, pool, spot, sent, reply, thinkingModel),
                configured.Prompts, guard, job).ConfigureAwait(false);
            result = checking;
            if (checking.Claims.Count > 0 && checking.Unchecked == checking.Claims.Count)
                return BackgroundJobOutcome.Failed(checking.Problem ?? "the model couldn't check the claims");
            var revised = checking.Claims.Count > 0 && await ReviseReportAsync(job, key, report, checking, token).ConfigureAwait(false);
            return BackgroundJobOutcome.Done(SourceCheck.Note(checking, report.Title, revised ? key : null));
        }, () => result is not { } done ? "nothing checked" : $"{done.Claims.Count} claims, {done.Requests} requests, {done.Supported} supported, " +
            $"{done.Partly} only partly, {done.NotSupported} not supported, {done.Unchecked} not checked", token).ConfigureAwait(false);
    }

    // The kept report with its Source check section, on every computer; false when it is gone or couldn't be written.
    private async Task<bool> ReviseReportAsync(BackgroundJob job, string key, ResearchReport report, SourceCheckResult result, CancellationToken token)
    {
        if (CreationsDirectory is null) return false;
        try
        {
            if (CreationStore.View(CreationsDirectory).Resolve(key) is not { } creation) return false;
            var (text, assets) = ResearchReports.Content(SourceCheck.Revised(report.Markdown, result));
            var revised = await CreationStore.ReviseAsync(CreationsDirectory, creation.Id, creation.Summary, text, assets, Creations,
                HostSetupCommands.SuggestedDeviceId(), clock.GetUtcNow(), token).ConfigureAwait(false);
            if (revised is null) return false;
            ErrorLog.Info($"Web research: {job.Id} added its source check to report {key}.");
            try { await Reports.UpdateAsync(CreationsDirectory, token).ConfigureAwait(false); }
            catch (Exception error) when (CreationStore.IsFailure(error)) { }
            return true;
        }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            ErrorLog.Warn($"Web research: {job.Id} couldn't add its source check to report {key} ({error.GetType().Name}).");
            return false;
        }
    }

    // A job's model work on its Thinking pool place (this PC, a cloud provider or a paired computer's Thinking pool role): each
    // task is a background think continuing the reply's request with that place's own runtime and authorization; a think the
    // live floor stops goes on later on the place the job gets next. `what` names the work in the log.
    private Func<string, CancellationToken, Task<BackgroundJobOutcome>> PoolThink(string what, BackgroundJob job, LiveConversationConfiguration configured,
        DeepThinkingPool pool, DeepThinkingSpot spot, BoundedTextInput? sent, Func<string?> reply, string thinkingModel)
    {
        var thinkingRoute = configured.Routes.ThinkingRoute();
        return (task, stepToken) => YieldingThink.RunAsync(jobs, job, (at, resume) =>
        {
            var here = pool.Find(at.Id) ?? spot;
            var there = here.Settings.Separate ? here.Settings.Describe() : thinkingModel;
            var slot = ThinkSlotFor(here.Key);
            return new BackgroundThink(ThinkRuntime(slot),
                left => PrepareThink(configured, here.Settings, sent, reply, task, null, left, own => Volatile.Write(ref slot.Authorization, own), resume), clock)
            {
                Doing = job.Progress,
                AttemptFinished = terminal =>
                {
                    routing.NoteFallback(what, configured, terminal);
                    NoteInput(what, terminal, reply: false);
                    if (ConversationRouting.IsFailure(terminal) && terminal.State != ConversationState.Canceled)
                    {
                        ErrorLog.Warn($"{what}: a step on {there} failed ({ConversationRouting.Describe(terminal)}).");
                        // Its computer didn't answer: offline at once, so the step (and the next job) goes elsewhere.
                        if (terminal.ProviderFailure == ProviderFailureCode.Network) NoteUnreachable(here.Settings, job.Id);
                    }
                }
            };
        }, at => pool.Find(at.Id) is { } next && ThinkLonger.ContinuesInPlace(next.Settings, thinkingRoute), stepToken,
            (at, _) => ErrorLog.Info($"{what}: {job.Id} stopped a step on {at.Name} for the conversation; it goes on later."),
            (at, began) => HeldOnHost(pool, at, began), GoesOnElsewhere(what, job.Id));
    }

    // Runs a job's work on its place: where Deep thinking uses a second model in Ollama on this PC, it first checks it fits beside
    // Thinking's (as a think does) and stops the work if Thinking's gets pushed off the card. The log's end line has `counts`.
    private async Task<BackgroundJobOutcome> OnPlaceAsync(string what, BackgroundJob job, LiveConversationConfiguration configured,
        DeepThinkingPool pool, string thinkingModel, Func<CancellationToken, Task<BackgroundJobOutcome>> work, Func<string> counts,
        CancellationToken token)
    {
        var spot = pool.Find(job.Place!.Id)!;
        var place = spot.Settings;
        var where = place.Separate ? place.Describe() : thinkingModel;
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch = Task.CompletedTask;
        string? pushed = null;
        try
        {
            if (spot.Plan.ChecksFit)
            {
                job.Report(BackgroundJobState.Waiting, "checking it fits beside Thinking");
                var fit = await LocalDeepThinking.CheckAsync(thinkingModel, place.ModelId!, loadThinking: true, guard.Token).ConfigureAwait(false);
                ErrorLog.Info($"{what}: {job.Id} {(fit.Fits ? "can" : "can't")} think in Ollama on this PC beside Thinking. {fit.Why}");
                if (!fit.Fits) return BackgroundJobOutcome.Failed(fit.Why.TrimEnd('.'));
                watch = LocalDeepThinking.WatchAsync(thinkingModel, place.ModelId!, why =>
                {
                    Volatile.Write(ref pushed, why);
                    guard.Cancel();
                }, guard.Token);
            }
            return await work(guard.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref pushed) is { } pushedOut)
        {
            ErrorLog.Warn($"{what}: {job.Id} stopped. {pushedOut}");
            LocalDeepThinking.RecoverAsync(thinkingModel, place.ModelId!).Forget();
            return BackgroundJobOutcome.Failed(pushedOut.TrimEnd('.'));
        }
        finally
        {
            await guard.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ErrorLog.Info($"{what}: {job.Id} ended on {spot.Computer} after {BackgroundJobs.Duration(job.Elapsed)} ({counts()}, thinking on {where}).");
        }
    }
    // The report as a creation (shared with every paired Martlet computer); its key, or null when it couldn't be kept.
    private async Task<string?> KeepReportAsync(BackgroundJob job, ResearchReport report, LiveConversationConfiguration configured,
        CancellationToken token)
    {
        if (dataDirectory is null || Creations.Find(ResearchReports.KindName) is null) return null;
        var author = new CreationAuthor
        {
            Device = HostSetupCommands.SuggestedDeviceId(), Computer = Martlet.Core.Network.ThisComputer.Name,
            Persona = configured.Persona?.Name is { Length: > 0 } persona ? persona : null
        };
        try
        {
            var creation = await CreationStore.AddAsync(CreationsDirectory!, ResearchReports.Draft(report, author), Creations, clock.GetUtcNow(), token)
                .ConfigureAwait(false);
            ErrorLog.Info($"Web research: {job.Id} kept its report as creation {creation.Key} ({creation.Bytes / 1024} KiB, {report.Sources.Count} sources).");
            // Searchable at once (search_guide), indexed here off the reply's path.
            try { await Reports.UpdateAsync(CreationsDirectory!, token).ConfigureAwait(false); }
            catch (Exception error) when (CreationStore.IsFailure(error)) { }
            return creation.Key;
        }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            ErrorLog.Warn($"Web research: {job.Id} couldn't keep its report ({error.GetType().Name}).");
            return null;
        }
    }
}
