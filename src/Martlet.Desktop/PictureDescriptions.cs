using System.Text;
using System.Text.RegularExpressions;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

// Also built into Martlet's MCP server (image_model_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>What asks the image model to describe the newest picture ahead of time (docs/SENSE_MODELS.md, Pictures: the image
/// model): you talk or type (or press the talk button), a look is due (or a look needs its own picture), or the picture changed
/// while you talk with Martlet.</summary>
internal enum PictureTrigger { Talking, Look, Changed }

/// <summary>One picture of what Martlet watches as the image model gets it, without its pixels: its picture
/// <paramref name="Version"/> (the same while nothing changes on it), when it was taken, its <paramref name="Source"/> (a key for
/// the screen, window or camera it shows), the window's title and the program in front (a screen only), what it shows
/// (<paramref name="What"/>, for the image model) and the same in a few words (<paramref name="Short"/>, for the note).</summary>
internal sealed record PictureShot(long Version, DateTimeOffset TakenAt, string Source, bool Camera, string Title, string App,
    bool FullScreen, string What, string Short)
{
    /// <summary>The same window of the same program (and source) as <paramref name="other"/>.</summary>
    internal bool SameWindow(PictureShot other) => Source == other.Source && Title == other.Title && App == other.App && FullScreen == other.FullScreen;

    public override string ToString() => $"{nameof(PictureShot)} {Version}";
}

/// <summary>What the image model said about <paramref name="Shot"/>: its first line (<paramref name="Summary"/>, which the
/// conversation keeps as what Martlet saw) and the rest (<paramref name="Details"/>). In memory only; never logged or saved.</summary>
internal sealed record PictureDescription(PictureShot Shot, string Summary, string Details, string Model, DateTimeOffset At, TimeSpan Took,
    PictureTrigger Why)
{
    /// <summary>The whole description, as the note gives it to Thinking.</summary>
    internal string Text => Details.Length == 0 ? Summary : Summary + "\n" + Details;

    public override string ToString() => $"{nameof(PictureDescription)} of picture {Shot.Version} (content omitted)";
}

/// <summary>How one description job ended: the description, or why there is none (<see cref="Result"/>: the lane's outcome,
/// model, problem and time; never its words in logs). <paramref name="Reused"/>: a look took a description already made.</summary>
internal sealed record PictureDescribed(PictureShot Shot, PictureTrigger Why, PictureDescription? Description, SenseJobResult Result,
    bool Reused = false)
{
    public override string ToString() => $"{nameof(PictureDescribed)} {Result.Outcome}";
}

/// <summary>How long descriptions count and how often new ones are asked for.</summary>
internal sealed record PictureTiming
{
    /// <summary>A description of the same window counts while its screenshot is at most this old (the freshness rule for a
    /// picture that goes with a message).</summary>
    internal TimeSpan Fresh { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>No description older than this counts, even of a picture that didn't change.</summary>
    internal TimeSpan MaximumAge { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>The least time between two descriptions asked for while you talk or type.</summary>
    internal TimeSpan TalkEvery { get; init; } = TimeSpan.FromSeconds(4);
    /// <summary>The least time between two descriptions asked for because the picture changed.</summary>
    internal TimeSpan ChangeEvery { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>A changed picture is described only while you talked with Martlet this recently.</summary>
    internal TimeSpan ActiveFor { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>No new description this long after one failed.</summary>
    internal TimeSpan FailureWait { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>The picture a description failed for isn't asked for again this long.</summary>
    internal TimeSpan SamePictureWait { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long one job may wait for the image model and run (SenseJob.Timeout).</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>The image model's descriptions of what Martlet watches (docs/SENSE_MODELS.md, Pictures: the image model): made ahead
/// of time on the image model's lane (one key, so only the newest picture waits), kept in memory (the newest three), and taken by
/// a reply only when one is ready for the picture the reply would have sent (<see cref="For"/>): a reply never waits. A look
/// reuses a description of its exact picture, waits for the one being made, or asks for one (<see cref="ForLookAsync"/>).</summary>
internal sealed partial class PictureDescriptions
{
    /// <summary>The lane key of every picture job: a newer picture takes the place of an older one that waits.</summary>
    internal const string Key = "picture";
    /// <summary>A picture job goes before a screen summary waiting on the same model.</summary>
    internal const int Priority = 10;
    internal const int MaximumOutputTokens = 300;
    /// <summary>The longest description a note carries, in characters.</summary>
    internal const int MaximumText = 1200;
    private const int KeptCount = 3, LatelyMessages = 6, LatelyLine = 200, LatelyAll = 1200, DetailLines = 8;
    private static long versions;

    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly List<PictureDescription> kept = [];
    private readonly Dictionary<long, Task<PictureDescribed>> running = [];
    private CancellationTokenSource stop = new();
    private DateTimeOffset? lastStarted, lastFailed;
    private long failedVersion;

    internal PictureDescriptions(TimeProvider clock, PictureTiming? timing = null)
    {
        this.clock = clock;
        Timing = timing ?? new();
    }

    internal PictureTiming Timing { get; }

    /// <summary>Raised (off the caller's thread) when a description job ends: for the desktop log and the status file.</summary>
    internal event Action<PictureDescribed>? Ended;

    /// <summary>A picture version no other picture in this process has: each talk window numbers its pictures from here, so a
    /// new window never matches an old description.</summary>
    internal static long NewVersion() => Interlocked.Increment(ref versions);

    /// <summary>Whether a new screenshot (<paramref name="next"/>, keyed with version 0) is a new picture after
    /// <paramref name="before"/>: it changed enough (the glancer's change score, <paramref name="change"/>), or it shows another
    /// window, program, full-screen state or source.</summary>
    internal static bool Changed(PictureShot? before, PictureShot next, double change) =>
        before is null || change >= 0.01 || !before.SameWindow(next);

    /// <summary>The description a reply takes for <paramref name="shot"/> (the picture it would have sent) now, or null; it never
    /// waits. It counts when it is of the same source, at most <see cref="PictureTiming.MaximumAge"/> old, and of the same picture
    /// (version) or, unless <paramref name="exact"/>, of the same window with a screenshot at most <see cref="PictureTiming.Fresh"/>
    /// old. The newest screenshot wins.</summary>
    internal PictureDescription? For(PictureShot? shot, bool exact = false)
    {
        if (shot is null || shot.Version <= 0) return null;
        var now = clock.GetUtcNow();
        lock (gate) return Counting(shot, exact, now);
    }

    private PictureDescription? Counting(PictureShot shot, bool exact, DateTimeOffset now) =>
        kept.Where(d => Counts(d, shot, exact, now)).OrderByDescending(d => d.Shot.TakenAt).FirstOrDefault();

    private bool Counts(PictureDescription description, PictureShot shot, bool exact, DateTimeOffset now) =>
        description.Shot.Source == shot.Source && now - description.At <= Timing.MaximumAge &&
        (description.Shot.Version == shot.Version ||
         !exact && description.Shot.SameWindow(shot) && now - description.Shot.TakenAt <= Timing.Fresh);

    /// <summary>Whether a description of <paramref name="shot"/> should start now for <paramref name="why"/>: none counts for it
    /// and none is being made of it, the last one didn't fail just now, and the pace allows it (while you talk or type at most one
    /// every <see cref="PictureTiming.TalkEvery"/>; for a changed picture at most one every <see cref="PictureTiming.ChangeEvery"/>
    /// and only while you talked with Martlet lately, <paramref name="active"/>; a look at once).</summary>
    internal bool Wants(PictureShot? shot, PictureTrigger why, bool active = true)
    {
        if (shot is null || shot.Version <= 0) return false;
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (Counting(shot, why == PictureTrigger.Look, now) is not null || running.ContainsKey(shot.Version)) return false;
            if (lastFailed is { } failed &&
                (now - failed < Timing.FailureWait || failedVersion == shot.Version && now - failed < Timing.SamePictureWait))
                return false;
            return why switch
            {
                PictureTrigger.Look => true,
                PictureTrigger.Talking => lastStarted is not { } talked || now - talked >= Timing.TalkEvery,
                _ => active && (lastStarted is not { } started || now - started >= Timing.ChangeEvery)
            };
        }
    }

    /// <summary>Starts describing <paramref name="shot"/> (its <paramref name="image"/>) on the image model with
    /// <paramref name="run"/>, in the background; the job being made of the same picture when there is one. The description is
    /// kept for the replies and looks that follow.</summary>
    internal Task<PictureDescribed> Describe(PictureShot shot, BoundedImage image, PromptSettings? prompts,
        IReadOnlyList<TextHistoryMessage> lately, PictureTrigger why, Func<SenseJob, CancellationToken, Task<SenseJobResult>> run)
    {
        ArgumentNullException.ThrowIfNull(shot);
        ArgumentNullException.ThrowIfNull(run);
        var job = Job(prompts, shot, image, lately, why, Timing.Timeout);
        lock (gate)
        {
            if (running.TryGetValue(shot.Version, out var already)) return already;
            lastStarted = clock.GetUtcNow();
            var task = RunAsync(shot, why, job, run, stop.Token);
            // A job that ended at once removed itself already; only a running one is kept.
            if (!task.IsCompleted) running[shot.Version] = task;
            return task;
        }
    }

    /// <summary>The description a look takes for its own picture: one of that exact picture, the one being made of it, or a new
    /// one. Canceling <paramref name="token"/> (the look stopped) leaves a job being made running for the replies that follow.</summary>
    internal async Task<PictureDescribed> ForLookAsync(PictureShot shot, BoundedImage image, PromptSettings? prompts,
        IReadOnlyList<TextHistoryMessage> lately, Func<SenseJob, CancellationToken, Task<SenseJobResult>> run, CancellationToken token)
    {
        if (For(shot, exact: true) is { } ready)
            return new(shot, PictureTrigger.Look, ready, new(SenseJobOutcome.Succeeded, ready.Text, ready.Model, null, TimeSpan.Zero), Reused: true);
        Task<PictureDescribed>? pending;
        lock (gate) running.TryGetValue(shot.Version, out pending);
        return await (pending ?? Describe(shot, image, prompts, lately, PictureTrigger.Look, run)).WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>Lets every description go and stops the jobs being made (the conversation was cleared, paused or locked, or
    /// watching stopped). The stop runs in the background, so a caller may hold its own lock.</summary>
    internal void Forget()
    {
        CancellationTokenSource old;
        lock (gate)
        {
            kept.Clear();
            running.Clear();
            lastStarted = lastFailed = null;
            failedVersion = 0;
            old = stop;
            stop = new();
        }
        _ = old.CancelAsync();
    }

    /// <summary>The newest description kept and how many jobs are being made (for the status file; never the words).</summary>
    internal (PictureDescription? Newest, int Running) Now()
    {
        lock (gate) return (kept.OrderByDescending(d => d.At).FirstOrDefault(), running.Count);
    }

    private async Task<PictureDescribed> RunAsync(PictureShot shot, PictureTrigger why, SenseJob job,
        Func<SenseJob, CancellationToken, Task<SenseJobResult>> run, CancellationToken token)
    {
        await Task.Yield();
        SenseJobResult result;
        try { result = await run(job, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { result = new(SenseJobOutcome.Stale, null, null, "the conversation was cleared", TimeSpan.Zero); }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException or ArgumentException)
        {
            result = new(SenseJobOutcome.Failed, null, null, "the job couldn't be sent", TimeSpan.Zero);
        }
        PictureDescription? description = null;
        if (result.Succeeded)
        {
            if (Parse(result.Text) is { } words)
                description = new(shot, words.Summary, words.Details, result.Model ?? "the image model", clock.GetUtcNow(), result.Took, why);
            else result = result with { Outcome = SenseJobOutcome.Failed, Text = null, Problem = "it came back empty" };
        }
        lock (gate)
        {
            // A job the conversation let go of (Forget) changes nothing: its state is gone, and a new job may describe the same picture.
            if (!token.IsCancellationRequested)
            {
                running.Remove(shot.Version);
                if (description is not null)
                {
                    kept.Add(description);
                    while (kept.Count > KeptCount) kept.RemoveAt(0);
                }
                else if (result.Outcome is SenseJobOutcome.Failed or SenseJobOutcome.TimedOut or SenseJobOutcome.Refused)
                {
                    lastFailed = clock.GetUtcNow();
                    failedVersion = shot.Version;
                }
            }
        }
        var ended = new PictureDescribed(shot, why, description, result);
        Ended?.Invoke(ended);
        return ended;
    }

    /// <summary>The image model's job for one picture: Companion › Prompts › Image model: describe the picture as its
    /// instructions, and as its message what the picture shows, the program in front and the window's title (a screen only) and
    /// the last lines of the conversation (<see cref="Lately"/>). Nothing else: no persona, memory, tools or notes.</summary>
    internal static SenseJob Job(PromptSettings? prompts, PictureShot shot, BoundedImage image, IReadOnlyList<TextHistoryMessage> lately,
        PictureTrigger why, TimeSpan? timeout = null) => new()
    {
        Purpose = why == PictureTrigger.Look ? "glance" : "reply picture",
        Key = Key,
        Priority = Priority,
        Instructions = PromptSettings.Fill(prompts, PromptCatalog.DescribePicture) ?? PromptCatalog.DefaultDescribePictureInstructions,
        Text = Message(shot, lately),
        Image = image,
        Timeout = timeout ?? TimeSpan.FromSeconds(15),
        DropWhenStale = true,
        MaxOutputTokens = MaximumOutputTokens,
        Reasoning = false
    };

    /// <summary>The image model's message: what the picture shows, the program in front and the window's title, then what was
    /// said lately.</summary>
    internal static string Message(PictureShot shot, IReadOnlyList<TextHistoryMessage> lately)
    {
        var text = new StringBuilder("The picture shows ").Append(shot.What).Append('.');
        if (!shot.Camera && (shot.App.Length > 0 || shot.FullScreen)) text.Append(" Active app: ").Append(ActiveApp.Describe(shot.App, shot.FullScreen)).Append('.');
        if (!shot.Camera && shot.Title.Length > 0) text.Append(" Active window: \"").Append(shot.Title).Append("\".");
        var said = Lately(lately);
        text.Append("\n\n").Append(said.Length == 0 ? "Nothing was said yet." : "What was said lately, oldest first:\n" + said);
        return text.ToString();
    }

    /// <summary>The last lines of the conversation for the image model, oldest first: at most six messages, each on one line of at
    /// most 200 characters and 1,200 in all, the user's as "User:" and Martlet's as "Martlet:" (without seen tags; a [pass] is
    /// left out).</summary>
    internal static string Lately(IReadOnlyList<TextHistoryMessage> history)
    {
        var picked = new List<string>();
        var total = 0;
        for (var i = history.Count - 1; i >= 0 && picked.Count < LatelyMessages; i--)
        {
            var message = history[i];
            var said = OneLine(message.Role == TextHistoryRole.User ? message.Text : SeenTags.Without(message.Text));
            if (said.Length == 0 || StayQuiet.IsQuiet(said)) continue;
            if (said.Length > LatelyLine) said = said[..(LatelyLine - 1)].TrimEnd() + "…";
            var line = (message.Role == TextHistoryRole.User ? "User: " : "Martlet: ") + said;
            if (total + line.Length > LatelyAll) break;
            picked.Add(line);
            total += line.Length + 1;
        }
        picked.Reverse();
        return string.Join("\n", picked);
    }

    private static string OneLine(string text) => Spaces().Replace(new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), " ").Trim();

    /// <summary>The image model's answer as a description: its first line, tidied, is the summary (at most
    /// <see cref="SeenTags.MaximumDescription"/> characters, as the conversation keeps it); up to eight more lines are the details,
    /// and the whole is at most <see cref="MaximumText"/> characters. Markdown, numbering, labels and control tags are taken out.
    /// One long paragraph gives its first sentence as the summary. Null when nothing is left.</summary>
    internal static (string Summary, string Details)? Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var lines = answer.Replace("\r", "").Split('\n')
            .Select(line => Tags().Replace(line, "").Replace("**", "").Replace("__", "").Trim())
            .Select(line => Numbered().Replace(line.TrimStart('-', '*', '•', '#', '>', ' '), "").Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0) return null;
        var first = lines[0];
        foreach (var label in new[] { "Summary:", "Summary -", "Line 1:" })
            if (first.StartsWith(label, StringComparison.OrdinalIgnoreCase)) first = first[label.Length..].TrimStart();
        var rest = lines.Skip(1).Take(DetailLines).ToList();
        var end = first.IndexOfAny(['.', '!', '?']);
        if (lines.Count == 1 && first.Length > SeenTags.MaximumDescription && end > 10 && end < first.Length - 1)
        {
            rest.Insert(0, first[(end + 1)..].Trim());
            first = first[..(end + 1)];
        }
        if (SeenTags.Clean(first) is not { } summary) return null;
        var details = string.Join("\n", rest.Where(line => line.Length > 0));
        var room = MaximumText - summary.Length - 1;
        if (details.Length > room) details = room > 1 ? details[..(room - 1)].TrimEnd() + "…" : "";
        return (summary, details);
    }

    /// <summary>The note that carries <paramref name="description"/> to Thinking (Companion › Prompts › What the image model saw);
    /// null when the owner emptied it.</summary>
    internal static string? Note(PromptSettings? prompts, PictureDescription description) =>
        PromptSettings.Fill(prompts, PromptCatalog.SeenDescribedNote, ("source", description.Shot.Short), ("description", description.Text));

    /// <summary>What every reply and look is told while the image model describes pictures (Companion › Prompts › Pictures as
    /// words); the same in every request. Null when the owner emptied it.</summary>
    internal static string? Instructions(PromptSettings? prompts) => PromptSettings.Fill(prompts, PromptCatalog.SeenDescribed);

    /// <summary>A look's message with the image model's words: the glance prompt, the description, then the text read on the
    /// screen (last, so the request starts like the one before).</summary>
    internal static string GlanceMessage(string prompt, string description, string? read) =>
        string.Join("\n\n", new[] { prompt, description, read }.Where(part => !string.IsNullOrWhiteSpace(part)));

    [GeneratedRegex(@"\[[a-zA-Z_]+:[^\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"^\d{1,2}[.)]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Numbered();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
