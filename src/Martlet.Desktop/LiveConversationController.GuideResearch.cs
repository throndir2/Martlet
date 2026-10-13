using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Deeper research for App guides (docs/APP_GUIDES.md, "The brief" and "Researching where you are"): the brief after a read,
// update_guide_brief and research on the area the screen summary shows. All of it runs on Thinking pool members only, as
// Research jobs (the lowest priority), never on the conversation's own Thinking route, and never on a reply's path.
internal sealed partial class LiveConversationController
{
    private AppGuideResearcher? guideResearch;

    /// <summary>The research on games and apps with a guide; null before <see cref="StartGuideResearch"/>.</summary>
    internal AppGuideResearcher? GuideResearch => Volatile.Read(ref guideResearch);

    /// <summary>Starts the research for <paramref name="guides"/>: a brief after every read, and a look at each new screen summary
    /// (at most every few minutes) for an area to research.</summary>
    internal void StartGuideResearch(AppGuideService guides, WebAccess web)
    {
        if (guideResearch is not null) return;
        var research = new AppGuideResearcher(guides, new PoolGuideThinker(() => ThinkingPool), web, web)
        {
            Log = ErrorLog.Info, Context = GuideContext
        };
        Volatile.Write(ref guideResearch, research);
        Board.Changed += ScreenForGuides;
    }

    internal void StopGuideResearch()
    {
        Board.Changed -= ScreenForGuides;
        Interlocked.Exchange(ref guideResearch, null)?.Dispose();
    }

    /// <summary>What the user does now, for choosing more wiki pages to read: the screen, sounds and activity notes on the board
    /// and the last few exchanges (what the user said or wrote, and the replies). Off the reply's path; never logged.</summary>
    internal string? GuideContext()
    {
        var text = new System.Text.StringBuilder();
        foreach (var note in Board.Snapshot(clock.GetLocalNow()).Notes)
            if (note.Source is ContextBoard.Screen or ContextBoard.Sound or ContextBoard.Activity)
                text.Append(note.Source switch { ContextBoard.Screen => "Screen: ", ContextBoard.Sound => "Sounds: ", _ => "Doing: " })
                    .Append(note.Text.Replace('\n', ' ')).Append('\n');
        foreach (var exchange in RecentExchanges(GuideContextExchanges).Exchanges)
            text.Append("User: ").Append(Short(exchange.User)).Append("\nCompanion: ").Append(Short(exchange.Martlet)).Append('\n');
        return text.Length == 0 ? null : text.ToString().TrimEnd();

        static string Short(string? words)
        {
            var line = (words ?? "").Replace('\n', ' ').Trim();
            return line.Length <= 300 ? line : line[..300] + "…";
        }
    }

    private const int GuideContextExchanges = 4;

    // The board changes on a reply's path too (a sent request): this only reads the screen note when research wants one, and the
    // research itself runs on the thread pool.
    private void ScreenForGuides()
    {
        if (GuideResearch is not { WantsScreen: true } research) return;
        var screen = Board.Snapshot(clock.GetLocalNow()).Notes.FirstOrDefault(n => n.Source == ContextBoard.Screen)?.Text;
        if (screen is not null) research.Screen(screen);
    }

    /// <summary>update_guide_brief: starts research on the app (and the focus) on a Thinking pool member and returns at once.</summary>
    private ConversationToolResult UpdateGuideBrief(TextToolCall call)
    {
        const string server = "Martlet";
        var (app, focus, problem) = AppGuideTools.ParseBrief(call.ArgumentsJson);
        if (problem is not null)
        {
            tools?.Record(server, AppGuideTools.BriefName, "invalid arguments", "", true);
            return new(problem, true);
        }
        if (Guides is not { On: true } guides) return new(AppGuideTools.TurnedOff, true);
        var library = guides.Library;
        var built = library.Apps.Where(a => a.BuiltAt is not null).ToArray();
        var entry = app is { } named ? AppGuideService.Match(library, [named]) ?? AppGuideService.Named(library, named)
            : guides.Front?.Entry is { } inFront ? built.FirstOrDefault(a => a.Key == inFront.Key)
            : built.Length == 1 ? built[0] : null;
        if (entry is not { BuiltAt: not null })
        {
            tools?.Record(server, AppGuideTools.BriefName, "no guide", "", false);
            return new(AppGuideTools.NoGuide(app, built), false);
        }
        if (GuideResearch is not { } research) return new(AppGuideTools.BriefRefused("research isn't available on this PC"), true);
        var refused = research.Start(entry.Key, focus);
        tools?.Record(server, AppGuideTools.BriefName, refused is null ? "started" : refused == "busy" ? "busy" : "not started", "", false);
        ErrorLog.Info($"App guides: the Thinking model asked to update a brief{(focus is null ? "" : " with a focus")} " +
            $"({(refused is null ? "started" : refused == "busy" ? "busy" : "not started")}).");
        return refused is null or "busy" ? new(AppGuideTools.BriefStarted(entry.Name, focus, refused == "busy"))
            : new(AppGuideTools.BriefRefused(refused), false);
    }
}

/// <summary>Research's model work as Thinking pool Research jobs (the lowest priority): a member that takes long jobs, never the
/// conversation's own route, so it never pushes the conversation out of a local model's cache. A pool without such a member
/// answers "no member" at once.</summary>
internal sealed class PoolGuideThinker(Func<DesktopThinkingPool> pool) : IGuideThinker
{
    public bool CanThink => pool().CanRun(ThinkingJobKind.Research);

    public async Task<(string? Text, string? Problem)> ThinkAsync(GuideThinkingAsk ask, CancellationToken cancellationToken)
    {
        var result = await pool().RunAsync(new ThinkingJob
        {
            Kind = ThinkingJobKind.Research,
            Label = ask.Label,
            Instructions = ask.Instructions,
            Text = ask.Text,
            Timeout = ask.Timeout,
            MaxOutputTokens = ask.MaxOutputTokens,
            Reasoning = false
        }, cancellationToken).ConfigureAwait(false);
        return result.Outcome == ThinkingJobOutcome.Succeeded
            ? (result.Text, null)
            : (null, result.Outcome switch
            {
                ThinkingJobOutcome.NoMember => "no Thinking pool member can take research now",
                ThinkingJobOutcome.TimedOut => "the Thinking pool member took too long",
                ThinkingJobOutcome.Preempted or ThinkingJobOutcome.Stale => "the Thinking pool member was needed for the conversation",
                _ => "the Thinking pool member couldn't answer"
            });
    }
}
