using System.IO;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// App guides in the conversation (docs/APP_GUIDES.md, docs/CONVERSATION.md#app-guides): read_up_on, search_guide and skip_guide
// while Companion › App guides is on, the reading-up job (network only, no model), the offer Martlet makes on its own when a game
// without a guide comes to the front, and the guide's notes on the user's message (read from memory only, never waited for).
internal sealed partial class LiveConversationController
{
    /// <summary>The guide library (Companion › App guides); null where guides can't be kept (no data folder).</summary>
    internal AppGuideService? Guides { get; set; }

    /// <summary>Whether replies get the guide tools: App guides on and a Thinking route that does function calling. The same
    /// three tools every time while that holds, so the start of every request stays the same.</summary>
    internal bool OffersGuides(LiveConversationConfiguration configured) => configured.SupportsTools && Guides is { On: true };

    /// <summary>Adds read_up_on, search_guide and skip_guide (and the App guides prompt) while they are offered.</summary>
    private void AddGuideTools(List<(TextToolDefinition, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>)> own,
        ref string? guidance, LiveConversationOperation operation, LiveConversationConfiguration configured)
    {
        if (!OffersGuides(configured)) return;
        own.Add((AppGuideTools.ReadUpDefinition, (call, token) => ValueTask.FromResult(ReadUpOn(operation, call))));
        own.Add((AppGuideTools.SearchDefinition, SearchGuideAsync));
        own.Add((AppGuideTools.SkipDefinition, SkipGuideAsync));
        guidance = Join(guidance, AppGuideTools.Instructions(configured.Prompts));
    }

    /// <summary>read_up_on: starts reading up in the background and returns at once.</summary>
    private ConversationToolResult ReadUpOn(LiveConversationOperation operation, TextToolCall call)
    {
        const string server = "Martlet";
        var (arguments, problem) = AppGuideTools.ParseReadUp(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record(server, AppGuideTools.ReadUpName, "invalid arguments", "", true);
            return new(problem!, true);
        }
        if (Guides is not { On: true } guides) return new(AppGuideTools.TurnedOff, true);
        var start = StartReadingUp(guides, arguments.App, arguments.Sites);
        if (start.Job is not { } job)
        {
            tools?.Record(server, AppGuideTools.ReadUpName, "not started: " + start.Refusal, "", false);
            return new(AppGuideTools.Refused(start), true);
        }
        tools?.Record(server, AppGuideTools.ReadUpName, "started " + job.Id, "", false);
        return new(AppGuideTools.Started(job, AppGuideTools.Label(arguments.App), !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text)));
    }

    /// <summary>Starts a reading-up job for <paramref name="app"/> in this conversation (its talk window shows "Reading up on").</summary>
    internal BackgroundJobStart StartReadingUp(AppGuideService guides, string app, IReadOnlyList<string> sites)
    {
        var start = jobs.Start(AppGuideTools.Kind, AppGuideTools.Label(app), async (job, token) =>
        {
            job.Report(BackgroundJobState.Running, "Looking for its wiki");
            var built = await guides.BuildAsync(app, sites, new GuideProgress(text => job.Report(BackgroundJobState.Running, text)), token)
                .ConfigureAwait(false);
            LogBuild(job.Id, built);
            return built.Built ? BackgroundJobOutcome.Done(AppGuideTools.Ready(built)) : BackgroundJobOutcome.Failed(built.Problem!);
        });
        if (start.Job is { } started)
            ErrorLog.Info($"App guides: started {started.Id} ({BackgroundJobs.Duration(AppGuideTools.TimeLimit)} limit, " +
                $"{jobs.StartedWithinHour(AppGuideTools.KindName)} of {AppGuideTools.Kind.MaxPerHour} this hour).");
        else ErrorLog.Info($"App guides: reading up wasn't started ({start.Refusal}).");
        return start;
    }

    /// <summary>The desktop log's line for one try to read up: counts and time only, never the app, a link or what was read.</summary>
    internal static void LogBuild(string by, AppGuideBuild built) =>
        ErrorLog.Info(built.Built
            ? $"App guides: {by} read {built.Pages} pages from {built.Sites.Count} site{(built.Sites.Count == 1 ? "" : "s")} " +
              $"({built.Failed} unreadable, {built.Downloaded / 1024} KiB downloaded) into {built.Chunks} sections, " +
              $"{built.Bytes / 1024} KiB kept, in {BackgroundJobs.Duration(built.Took)}."
            : $"App guides: {by} couldn't make a guide after {BackgroundJobs.Duration(built.Took)} ({(built.Busy ? "busy" : "failed")}; " +
              $"{built.Failed} pages unreadable).");

    /// <summary>search_guide: the best sections of a guide, at once (an index still loading is waited for briefly).</summary>
    private async ValueTask<ConversationToolResult> SearchGuideAsync(TextToolCall call, CancellationToken token)
    {
        const string server = "Martlet";
        var (arguments, problem) = AppGuideTools.ParseSearch(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record(server, AppGuideTools.SearchName, "invalid arguments", "", true);
            return new(problem!, true);
        }
        if (Guides is not { On: true } guides) return new(AppGuideTools.TurnedOff, true);
        var library = guides.Library;
        var built = library.Apps.Where(a => a.BuiltAt is not null).ToArray();
        var entry = arguments.App is { } named ? AppGuideService.Match(library, [named]) ?? AppGuideService.Named(library, named)
            : guides.Front?.Entry is { } inFront ? built.FirstOrDefault(a => a.Key == inFront.Key)
            : built.Length == 1 ? built[0] : null;
        if (entry is not { BuiltAt: not null })
        {
            tools?.Record(server, AppGuideTools.SearchName, "no guide", "", false);
            return new(AppGuideTools.NoGuide(arguments.App, built), false);
        }
        bool ready;
        try { ready = await guides.ReadyAsync(entry.Key, token).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch (TimeoutException) { ready = false; }
        var hits = ready ? guides.Search(entry.Key, arguments.Question, AppGuideTools.SearchResults) : null;
        if (hits is null)
        {
            tools?.Record(server, AppGuideTools.SearchName, "not ready", "", false);
            return new(AppGuideTools.NotReady(entry.Name), false);
        }
        tools?.Record(server, AppGuideTools.SearchName, $"found {hits.Count}", "", false);
        ErrorLog.Info($"App guides: the Thinking model searched a guide and found {hits.Count} section{(hits.Count == 1 ? "" : "s")}.");
        return new(AppGuideTools.Found(entry.Name, hits));
    }

    /// <summary>skip_guide: remembers the user's no to the offer, so Martlet never offers that app again.</summary>
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
        try
        {
            var declined = await guides.DeclineAsync(app, token).ConfigureAwait(false);
            tools?.Record(server, AppGuideTools.SkipName, "declined", "", false);
            ErrorLog.Info("App guides: you said no to reading up on an app; Martlet won't offer it again.");
            return new(AppGuideTools.Skipped(declined.Name));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            tools?.Record(server, AppGuideTools.SkipName, "failed", "", true);
            return new("Couldn't note that on this PC right now. Say okay briefly anyway.", true);
        }
    }

    /// <summary>The offer Martlet makes on its own: a notice it brings up as soon as it is free, or with what the user says next.
    /// Null when the conversation is closing or too many wait.</summary>
    internal BackgroundJob? OfferGuide(AppGuideOfferCandidate offer) =>
        jobs.Start(AppGuideTools.OfferKind, AppGuideTools.Label(offer.Name),
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done(AppGuideTools.OfferNote(offer)))).Job;

    /// <summary>The guide notes for the user's own words (the app they name, else the app in front), from memory only: the
    /// sections the conversation's notes don't carry yet. Null when there are none. Counts go to the desktop log.</summary>
    private string? GuideNotes(string words, IReadOnlyList<TextHistoryMessage> sent, PromptSettings? prompts, out int used)
    {
        used = 0;
        if (Guides is not { On: true } guides || !guides.Library.Apps.Any(a => a.BuiltAt is not null)) return null;
        var earlier = string.Join("\n", sent.Where(message => message.Role == TextHistoryRole.User &&
            message.Text.Contains(GuideRecall.Label, StringComparison.Ordinal)).Select(message => message.Text));
        if (guides.Recall(words, earlier.Length == 0 ? null : earlier, prompts) is not { } found) return null;
        // Off the reply's path: the line is written after the request has gone.
        ErrorLog.InfoLater(!found.Ready
            ? "App guides: the app's guide is still loading, so your message went without it."
            : found.Notes is null
                ? $"App guides: nothing in the guide matched well enough ({found.Matched} weak matches, best relevance {found.Best:0.00}, " +
                  $"{found.Took.TotalMilliseconds:0.##} ms)."
                : $"App guides: {found.Used} section{(found.Used == 1 ? "" : "s")} went with your message ({found.Matched} matched, best " +
                  $"relevance {found.Best:0.00}, {found.Took.TotalMilliseconds:0.##} ms).");
        used = found.Used;
        return found.Notes;
    }

    private sealed class GuideProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
