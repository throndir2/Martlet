using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;

namespace Martlet.Conversation;

/// <summary>One voice Martlet heard lately (Voice ID): the name it goes by (null: a voice Martlet doesn't know, or one without a
/// name yet), whether the owner marked it as their own, and when it spoke.</summary>
public sealed record CheckInVoice(string? Name, bool Owner, DateTimeOffset At)
{
    /// <summary>Which voice of Martlet's voice list it was, to tell voices without a name apart.</summary>
    public string? Id { get; init; }
}

public sealed partial record CheckIn
{
    /// <summary>With <see cref="CheckInConditions.Between"/>: the first hour it may run (0-23, local time).</summary>
    public int FromHour { get; init; } = CheckIns.DefaultFromHour;
    /// <summary>With <see cref="CheckInConditions.Between"/>: the hour it stops (0-23, local time; before
    /// <see cref="FromHour"/> means past midnight).</summary>
    public int UntilHour { get; init; } = CheckIns.DefaultUntilHour;
    /// <summary>At most this many runs in an hour (one of <see cref="CheckIns.MostPerHourChoices"/>; 0: no limit).</summary>
    public int MostPerHour { get; init; }
}

/// <summary>The signals check-ins can wait for and know about, gathered by the desktop on its UI thread. Like the rest of the
/// state, they go only to a Thinking pool member with the check, never to logs or status files.</summary>
public sealed partial record CheckInState
{
    /// <summary>When someone came back to this PC after <see cref="CheckIns.Idle"/> or more away, or null.</summary>
    public DateTimeOffset? CameBackAt { get; init; }
    /// <summary>How long they were away before <see cref="CameBackAt"/>.</summary>
    public TimeSpan? WasAway { get; init; }
    /// <summary>What the user seems to be doing on this PC now ("playing a game (Elden Ring), full screen"); empty while nothing
    /// plays, null while Martlet doesn't know (it guesses only while it hears what this PC plays).</summary>
    public string? Activity { get; init; }
    /// <summary>The user is in a call or a voice chat now.</summary>
    public bool OnCall { get; init; }
    /// <summary>When a game, a call or a full-screen app last started or ended, or null.</summary>
    public DateTimeOffset? ActivityChangedAt { get; init; }
    /// <summary>What the user did before that change (empty: nothing with sound), or null.</summary>
    public string? ActivityBefore { get; init; }
    /// <summary>When a taskbar button last flashed or a notification last showed, while a check-in that is on waits for it.</summary>
    public DateTimeOffset? AttentionAt { get; init; }
    /// <summary>When a song Martlet sang last played to its end, or null.</summary>
    public DateTimeOffset? SongEndedAt { get; init; }
    /// <summary>The voices Martlet heard lately, oldest first.</summary>
    public IReadOnlyList<CheckInVoice> Voices { get; init; } = [];
    /// <summary>The owner marked one of the voices Martlet knows as their own.</summary>
    public bool OwnerVoiceKnown { get; init; }
    /// <summary>What happened while the user was away (before <see cref="CameBackAt"/>), one short line each.</summary>
    public IReadOnlyList<string> WhileAway { get; init; } = [];
}

public sealed partial record CheckInRun
{
    /// <summary>A short hash of what the check-in's script printed in this run (never the output), for
    /// <see cref="CheckInConditions.ScriptChanged"/>.</summary>
    public string? ScriptHash { get; init; }
    /// <summary>When it ran within the last hour, this run included, for <see cref="CheckIn.MostPerHour"/>.</summary>
    public IReadOnlyList<DateTimeOffset> Recent { get; init; } = [];
}

public static partial class CheckIns
{
    public const string Welcome = "welcome", Unanswered = "unanswered", Call = "call", Others = "others";
    /// <summary>Every fact Martlet offers.</summary>
    public const CheckInFacts KnownFacts = (CheckInFacts)((1 << 13) - 1);
    /// <summary>Every condition Martlet offers.</summary>
    public const CheckInConditions KnownConditions = (CheckInConditions)((1 << 19) - 1);
    public const int DefaultFromHour = 8, DefaultUntilHour = 22;
    /// <summary>How many runs an hour a check-in may be held to: no limit, or 1 to 12.</summary>
    public static IReadOnlyList<int> MostPerHourChoices { get; } = [0, 1, 2, 3, 4, 6, 12];
    /// <summary>How recent a signal must be for a check-in's first run (later runs: since the last one).</summary>
    public static TimeSpan SignalWindow => TimeSpan.FromMinutes(5);
    /// <summary>How long Martlet's question waits for an answer before Unanswered question may follow up.</summary>
    public static TimeSpan UnansweredAfter => TimeSpan.FromMinutes(2);
    /// <summary>A question older than this is let go.</summary>
    public static TimeSpan UnansweredWithin => TimeSpan.FromMinutes(30);
    /// <summary>How long a voice Martlet heard counts as here.</summary>
    public static TimeSpan PeopleWindow => TimeSpan.FromMinutes(10);
    private static TimeSpan Hour => TimeSpan.FromHours(1);

    // Waits for the signal conditions and the per-hour cap, after the other conditions. With Check now, only for what there must
    // be to check: no script to compare, a call, nobody else heard, no question asked.
    private static string? WaitForSignals(CheckIn checkIn, CheckInState state, CheckInRun? last, bool now)
    {
        var when = checkIn.Conditions;
        if (when.HasFlag(CheckInConditions.ScriptChanged) && !checkIn.RunsScript) return "it has no script whose output could change";
        if (when.HasFlag(CheckInConditions.NotOnCall) && state.OnCall) return "you're on a call";
        if (when.HasFlag(CheckInConditions.SomeoneElse) && OthersHere(state).Count == 0) return "Martlet heard nobody else lately";
        var question = when.HasFlag(CheckInConditions.Unanswered) ? LastQuestion(state) : null;
        if (when.HasFlag(CheckInConditions.Unanswered) && question is null) return "Martlet's last remark didn't ask anything";
        if (now) return null;
        if (when.HasFlag(CheckInConditions.Between) && !InHours(checkIn.FromHour, checkIn.UntilHour, state.Now.Hour))
            return $"it runs only between {Clock(checkIn.FromHour)} and {Clock(checkIn.UntilHour)}";
        if (checkIn.MostPerHour > 0 && last is not null && last.Recent.Count(at => state.Now - at < Hour) >= checkIn.MostPerHour)
            return $"it ran {checkIn.MostPerHour} {(checkIn.MostPerHour == 1 ? "time" : "times")} in the last hour, its most";
        var since = last?.At ?? state.Now - SignalWindow;
        if (when.HasFlag(CheckInConditions.CameBack) &&
            !(state.CameBackAt is { } back && state.Now - back <= SignalWindow && back > since))
            return $"it waits for you to come back after {Reminders.Span(Idle)} or more away";
        if (when.HasFlag(CheckInConditions.ActivityChanged) && !(state.ActivityChangedAt is { } changed && changed > since))
            return state.Activity is null && state.ActivityChangedAt is null
                ? "Martlet doesn't know what you're doing (it tells only while it hears what this PC plays)"
                : "what you do hasn't changed since the last check";
        if (when.HasFlag(CheckInConditions.Attention) && !(state.AttentionAt is { } flashed && flashed > since))
            return "no taskbar button flashed and no notification showed since the last check";
        if (when.HasFlag(CheckInConditions.SongEnded) && !(state.SongEndedAt is { } ended && ended > since))
            return "no song played to its end since the last check";
        if (when.HasFlag(CheckInConditions.SomeoneElse) && !OthersHere(state).Any(v => v.At > since))
            return "nobody else spoke since the last check";
        if (question is not null)
        {
            var asked = state.Now - question.At;
            if (last is not null && question.At <= last.At) return "it already followed up on Martlet's last question";
            if (asked < UnansweredAfter) return $"it gives you {Reminders.Span(UnansweredAfter)} to answer";
            if (state.Quiet is { } quiet && quiet + TimeSpan.FromSeconds(30) < asked) return "the conversation went on after the question";
            if (state.Away is { } away && away > UnansweredAfter) return "you aren't at the PC";
        }
        return null;
    }

    /// <summary>Whether <paramref name="hour"/> (0-23) is from <paramref name="from"/> up to, not including,
    /// <paramref name="until"/>, past midnight when <paramref name="until"/> comes first.</summary>
    public static bool InHours(int from, int until, int hour) => from < until ? hour >= from && hour < until : hour >= from || hour < until;

    /// <summary>An hour of the day in words: "8 AM", "12 PM", "12 AM".</summary>
    public static string Clock(int hour) => DateTime.MinValue.AddHours(hour).ToString("h tt", CultureInfo.InvariantCulture);

    /// <summary>Martlet's newest saying when it asks the user something (it ends with a question mark, emotes and quotes left
    /// out), within <see cref="UnansweredWithin"/>; otherwise null.</summary>
    public static Saying? LastQuestion(CheckInState state)
    {
        if (state.Said.Count == 0 || state.Said[^1] is not { } newest || state.Now - newest.At > UnansweredWithin) return null;
        var plain = Tags().Replace(newest.Text, " ").Trim().TrimEnd('"', '\'', '\u201D', '\u2019', ')', ' ', '~', '!');
        return plain.EndsWith('?') ? newest : null;
    }

    /// <summary>The voices heard within <see cref="PeopleWindow"/> that aren't the user's own: those not marked as the owner's,
    /// or, while the owner marked none, every recent voice once two different ones spoke (one voice alone is taken to be the
    /// user's).</summary>
    public static IReadOnlyList<CheckInVoice> OthersHere(CheckInState state)
    {
        var recent = state.Voices.Where(v => state.Now - v.At <= PeopleWindow).ToList();
        if (state.OwnerVoiceKnown) return [.. recent.Where(v => !v.Owner)];
        return recent.Select(v => v.Id ?? v.Name ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2 ? recent : [];
    }

    /// <summary>What the user seems to be doing on this PC (<see cref="CheckInFacts.Activity"/>), as the check reads it.</summary>
    public static string Activity(CheckInState state)
    {
        var text = state.Activity switch
        {
            null => "Martlet doesn't know what the user is doing on this PC now (it guesses only while it hears what the PC plays).",
            "" => "The user doesn't seem to be doing anything with sound on this PC now.",
            var doing => "What the user seems to be doing on this PC now (a guess from which apps play sound and which window " +
                $"fills the screen): {Clip(OneLine(doing), 400)}."
        };
        if (state.OnCall) text += " They are in a call or a voice chat.";
        if (state.ActivityChangedAt is { } changed)
            text += $" That changed {Reminders.Span(state.Now - changed)} ago; before, " + (string.IsNullOrWhiteSpace(state.ActivityBefore)
                ? "nothing with sound played." : $"they were {Clip(OneLine(state.ActivityBefore), 300)}.");
        return text;
    }

    /// <summary>Who Martlet heard lately (<see cref="CheckInFacts.People"/>), as the check reads it: each voice once, newest first,
    /// with when.</summary>
    public static string People(CheckInState state)
    {
        var minutes = (int)PeopleWindow.TotalMinutes;
        var recent = state.Voices.Where(v => state.Now - v.At <= PeopleWindow)
            .GroupBy(v => (v.Id ?? v.Name ?? "", v.Owner)).Select(g => g.MaxBy(v => v.At)!).OrderByDescending(v => v.At).Take(8).ToList();
        if (recent.Count == 0) return $"Martlet heard no voices in the last {minutes} minutes (or doesn't recognize voices here).";
        var others = OthersHere(state).Count > 0;
        return $"Voices Martlet heard in the last {minutes} minutes, newest first:\n" + string.Join("\n", recent.Select(v =>
            $"- {(v.Name is { Length: > 0 } name ? Clip(OneLine(name), 60) : "a voice Martlet doesn't know")}" +
            $"{(v.Owner ? " (the user's own voice)" : "")}, {Reminders.Span(state.Now - v.At)} ago")) +
            (others ? "\nSomeone other than the user seems to be here." : "\nOnly the user seems to be here.");
    }

    /// <summary>What happened while the user was away (<see cref="CheckInFacts.WhileAway"/>), as the check reads it.</summary>
    public static string Away(CheckInState state)
    {
        if (state.CameBackAt is not { } back || state.WasAway is not { } away)
            return state.Away is { } gone && gone > Idle ? $"The user has been away from this PC for {Reminders.Span(gone)}."
                : "The user hasn't been away from this PC lately.";
        var text = new StringBuilder($"The user came back to this PC {Reminders.Span(state.Now - back)} ago, after " +
            $"{Reminders.Span(away)} away (from {SaidLately.Clock(back - away)} to {SaidLately.Clock(back)}).");
        text.Append(state.WhileAway.Count == 0 ? "\nNothing Martlet knows of happened while they were away."
            : "\nWhile they were away:\n" + string.Join("\n", state.WhileAway.Take(12).Select(line => "- " + Clip(OneLine(line), 200))));
        return text.ToString();
    }

    /// <summary>A short hash of what a script printed, to tell whether it changed (never the output itself).</summary>
    public static string ScriptHash(string? output) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output ?? "")))[..16];

    /// <summary>Whether <paramref name="checkIn"/> waits for its script's output to change and it didn't: its run ends without
    /// asking the Thinking pool. Never for Check now.</summary>
    public static bool ScriptUnchanged(CheckIn checkIn, string? hash, CheckInRun? last, bool now) =>
        !now && checkIn.Conditions.HasFlag(CheckInConditions.ScriptChanged) && hash is not null && last?.ScriptHash == hash;

    /// <summary>The times a check-in ran within the last hour, with a run at <paramref name="at"/>.</summary>
    public static IReadOnlyList<DateTimeOffset> Recent(CheckInRun? last, DateTimeOffset at) =>
        [.. (last?.Recent ?? []).Where(t => at - t < Hour), at];

    internal static void CheckSignals(int fromHour, int untilHour, int mostPerHour)
    {
        ContractRules.Require(fromHour is >= 0 and <= 23 && untilHour is >= 0 and <= 23 && fromHour != untilHour,
            "A check-in's hours start and end at different hours of the day.");
        ContractRules.Require(MostPerHourChoices.Contains(mostPerHour),
            $"A check-in runs at most {string.Join(", ", MostPerHourChoices.Skip(1).SkipLast(1))} or {MostPerHourChoices[^1]} times an hour, or without a limit.");
    }

    [GeneratedRegex(@"\{[^{}]*\}|\[[^\[\]]*\]")]
    private static partial Regex Tags();
}
