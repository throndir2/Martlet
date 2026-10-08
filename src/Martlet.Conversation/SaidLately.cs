using System.Globalization;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>One thing Martlet said (a reply, a remark on the screen or on what this PC plays, a reaction to a touch, a reminder
/// it brought up) and when.</summary>
public sealed record Saying(DateTimeOffset At, string Text);

/// <summary>
/// What Martlet said lately, each with when (docs/CONVERSATION.md#what-you-said-lately), so it can tell whether something is worth
/// saying again. The live conversation notes everything it says (never a [pass]) and forgets it with the conversation (Refresh
/// context, pause, lock, closing the talk window). What Martlet says on its own while nobody waits for its first words
/// (<see cref="Carries"/>) gets the newest of it in its notes (Companion › Prompts › What you said lately, <see cref="Note"/>), and
/// the Saying the same things check-in reads it too (<see cref="CheckIns.Repeats"/>). In memory only: never saved or logged.
/// </summary>
public sealed partial class SaidLately
{
    /// <summary>How many things it keeps and lists: the newest.</summary>
    public const int MaximumSayings = 10;
    /// <summary>How long one thing may be in the list; a longer one is cut.</summary>
    public const int MaximumCharacters = 160;
    /// <summary>How far back "lately" goes.</summary>
    public static TimeSpan Window => TimeSpan.FromHours(1);

    private readonly object gate = new();
    private readonly Queue<Saying> said = new();

    /// <summary>How many things it keeps now (some may be older than <see cref="Window"/>).</summary>
    public int Count { get { lock (gate) return said.Count; } }

    /// <summary>Notes that Martlet said <paramref name="text"/> at <paramref name="at"/>, on one line and cut to
    /// <see cref="MaximumCharacters"/>. A [pass] or nothing isn't noted: false.</summary>
    public bool Add(DateTimeOffset at, string? text)
    {
        if (Line(text) is not { } line) return false;
        lock (gate)
        {
            said.Enqueue(new(at, line));
            while (said.Count > MaximumSayings) said.Dequeue();
        }
        return true;
    }

    /// <summary>Forgets everything it said, with the conversation.</summary>
    public void Clear()
    {
        lock (gate) said.Clear();
    }

    /// <summary>What Martlet said within <see cref="Window"/> before <paramref name="now"/>, oldest first.</summary>
    public IReadOnlyList<Saying> Recent(DateTimeOffset now)
    {
        lock (gate) return Within(said, now);
    }

    /// <summary>The newest of <paramref name="said"/> within <see cref="Window"/> before <paramref name="now"/> (at most
    /// <see cref="MaximumSayings"/>), oldest first.</summary>
    public static IReadOnlyList<Saying> Within(IEnumerable<Saying> said, DateTimeOffset now) =>
        [.. said.Where(s => now - s.At <= Window).OrderBy(s => s.At).TakeLast(MaximumSayings)];

    /// <summary>One line for each, oldest first, with the time of day and how long ago:
    /// - 10:05 PM (12 min ago): "Ooh, that boss is almost down!"</summary>
    public static string Lines(IReadOnlyList<Saying> said, DateTimeOffset now) =>
        string.Join("\n", said.Select(s => $"- {Clock(s.At.ToOffset(now.Offset))} ({Reminders.Span(now - s.At)} ago): \"{s.Text}\""));

    /// <summary>The time of day as the lines say it: "10:05 PM".</summary>
    public static string Clock(DateTimeOffset at) => at.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>What a request Martlet makes on its own says in its notes (Companion › Prompts › What you said lately): what it
    /// said lately with when, and to check against it whether something is worth saying again. Null when it said nothing
    /// lately or the owner emptied the prompt. <paramref name="silent"/> is the word the model answers to stay quiet.</summary>
    public static string? Note(PromptSettings? prompts, IReadOnlyList<Saying> said, DateTimeOffset now, string silent) =>
        said.Count == 0 ? null
            : PromptSettings.Fill(prompts, PromptCatalog.SaidLately, ("said", Lines(said, now)), ("time", Clock(now)), ("silent", silent));

    /// <summary>Whether a request started by <paramref name="trigger"/> carries <see cref="Note"/>: what Martlet says on its own
    /// while nobody waits for its first words does (a look, a report of finished work, a due reminder or a check-in, a remark
    /// on what this PC plays); a reply to the user's words or touches never does, so its request, and the time to its first
    /// words, stay as they were.</summary>
    public static bool Carries(MomentTrigger trigger) => trigger is MomentTrigger.Look or MomentTrigger.Report or MomentTrigger.PcAudio;

    private static string? Line(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || StayQuiet.IsQuiet(text)) return null;
        var line = Spaces().Replace(text, " ").Trim();
        return line.Length <= MaximumCharacters ? line : line[..MaximumCharacters].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
