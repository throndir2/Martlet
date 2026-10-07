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
    /// and somewhere for Deep thinking to think.</summary>
    private bool OffersResearch(LiveConversationConfiguration configured) =>
        configured.OffersThinkLonger && configured.ThinkLonger.Researches && DeepPlan(configured).Available;

    /// <summary>research: starts the research job on a free Deep thinking place and returns at once, telling the model to tell
    /// the user now unless it did.</summary>
    private ConversationToolResult Research(LiveConversationOperation operation, LiveConversationConfiguration configured, TextToolCall call)
    {
        const string server = "Martlet";
        var (arguments, problem) = WebResearch.Parse(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record(server, WebResearch.Name, "invalid arguments", "", true);
            return new(problem!, true);
        }
        var label = WebResearch.Label(arguments.Topic);
        if (!configured.ThinkLonger.Researches) return new(WebResearch.TurnedOff, true);
        var pool = DeepPool(configured);
        var plan = pool.Plan;
        if (!plan.Available)
        {
            tools?.Record(server, WebResearch.Name, "not started: unavailable", label, false);
            ErrorLog.Info($"Web research: a new job wasn't started ({plan.Why})");
            return new(WebResearch.Unavailable(plan.Why), true);
        }
        var toldUser = !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text);
        var sent = operation.Sent;
        var thinkingModel = configured.Route(SetupRole.Llm).ModelId;
        var start = jobs.Start(WebResearch.Kind, label, (job, token) =>
            ResearchAsync(job, arguments, configured, pool.Find(job.Place!.Id)!, sent, () => operation.Turn?.Content.Text, thinkingModel, token),
            ThinkLonger.Places(pool, BackgroundDuties.Of(dataDirectory), PoolCan));
        if (start.Job is not { } started)
        {
            tools?.Record(server, WebResearch.Name, "not started: " + start.Refusal, label, false);
            ErrorLog.Info($"Web research: a new job wasn't started ({start.Refusal}: {start.Message})");
            return new(WebResearch.Refused(start), true);
        }
        tools?.Record(server, WebResearch.Name, "started " + started.Id, label, false);
        var chosen = pool.Find(started.Place!.Id)!;
        ErrorLog.Info($"Web research: started {started.Id}, thinking on {(chosen.Settings.Separate ? chosen.Settings.Describe() : thinkingModel)} " +
            $"(placed on {chosen.Computer}; {BackgroundJobs.Duration(WebResearch.TimeLimit)} limit, " +
            $"{jobs.StartedWithinHour(WebResearch.KindName)} of {WebResearch.Kind.MaxPerHour} this hour)" +
            (toldUser ? "." : " The reply hadn't told you yet, so it was asked to."));
        return new(WebResearch.Started(started, toldUser));
    }

    // The research job: checks a second model in Ollama on this PC fits beside Thinking's (as a think does), runs the loop with
    // each step a background think continuing the reply's request on the job's place, then keeps the report as a creation.
    private async Task<BackgroundJobOutcome> ResearchAsync(BackgroundJob job, ResearchArguments arguments, LiveConversationConfiguration configured,
        DeepThinkingSpot spot, BoundedTextInput? sent, Func<string?> reply, string thinkingModel, CancellationToken token)
    {
        var place = spot.Settings;
        var where = place.Separate ? place.Describe() : thinkingModel;
        var slot = ThinkSlotFor(spot.Key);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch = Task.CompletedTask;
        string? pushed = null;
        using var web = ResearchWeb();
        var run = new WebResearchRun(web, web, (task, stepToken) =>
        {
            var think = new BackgroundThink(ThinkRuntime(slot),
                left => PrepareThink(configured, place, sent, reply, task, null, left, own => Volatile.Write(ref slot.Authorization, own)), clock)
            {
                Doing = job.Progress,
                AttemptFinished = terminal =>
                {
                    NoteFallback("Web research", configured, terminal);
                    NoteInput("Web research", terminal, reply: false);
                    if (IsFailure(terminal) && terminal.State != ConversationState.Canceled)
                        ErrorLog.Warn($"Web research: a step on {where} failed ({Describe(terminal)}).");
                }
            };
            return think.RunAsync(job, stepToken);
        }, configured.Prompts);
        try
        {
            if (spot.Plan.ChecksFit)
            {
                job.Report(BackgroundJobState.Waiting, "checking it fits beside Thinking");
                var fit = await LocalDeepThinking.CheckAsync(thinkingModel, place.ModelId!, loadThinking: true, guard.Token).ConfigureAwait(false);
                ErrorLog.Info($"Web research: {job.Id} {(fit.Fits ? "can" : "can't")} think in Ollama on this PC beside Thinking. {fit.Why}");
                if (!fit.Fits) return BackgroundJobOutcome.Failed(fit.Why.TrimEnd('.'));
                watch = LocalDeepThinking.WatchAsync(thinkingModel, place.ModelId!, why =>
                {
                    Volatile.Write(ref pushed, why);
                    guard.Cancel();
                }, guard.Token);
            }
            var found = await run.RunAsync(job, arguments, guard.Token).ConfigureAwait(false);
            if (found.Report is not { } report) return BackgroundJobOutcome.Failed(found.Problem ?? "it found nothing usable");
            job.Report(BackgroundJobState.Running, "Saving the report");
            return BackgroundJobOutcome.Done(WebResearch.Ready(report, await KeepReportAsync(job, report, configured, token).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref pushed) is { } pushedOut)
        {
            ErrorLog.Warn($"Web research: {job.Id} stopped. {pushedOut}");
            LocalDeepThinking.RecoverAsync(thinkingModel, place.ModelId!).Forget();
            return BackgroundJobOutcome.Failed(pushedOut.TrimEnd('.'));
        }
        finally
        {
            await guard.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            // Counts only: never the topic, a query, a link or what was read.
            ErrorLog.Info($"Web research: {job.Id} ended on {spot.Computer} after {BackgroundJobs.Duration(job.Elapsed)} ({run.Searches} searches, " +
                $"{run.Pages} pages read, {run.Failures} unreadable, {run.Bytes / 1024} KiB downloaded, {run.Steps} model steps on {where}).");
        }
    }

    // The report as a creation (shared with every paired Martlet computer); its key, or null when it couldn't be kept.
    private async Task<string?> KeepReportAsync(BackgroundJob job, ResearchReport report, LiveConversationConfiguration configured,
        CancellationToken token)
    {
        if (dataDirectory is null || Creations.Find(ResearchReports.KindName) is null) return null;
        var author = new CreationAuthor
        {
            Device = HostSetupCommands.SuggestedDeviceId(), Computer = Environment.MachineName,
            Persona = configured.Persona?.Name is { Length: > 0 } persona ? persona : null
        };
        try
        {
            var creation = await CreationStore.AddAsync(dataDirectory, ResearchReports.Draft(report, author), Creations, clock.GetUtcNow(), token)
                .ConfigureAwait(false);
            ErrorLog.Info($"Web research: {job.Id} kept its report as creation {creation.Key} ({creation.Bytes / 1024} KiB, {report.Sources.Count} sources).");
            return creation.Key;
        }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            ErrorLog.Warn($"Web research: {job.Id} couldn't keep its report ({error.GetType().Name}).");
            return null;
        }
    }
}
