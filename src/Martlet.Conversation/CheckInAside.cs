using System.Text;
using Martlet.Core.Settings;
using static Martlet.Conversation.CheckInAnswers;
using static Martlet.Conversation.CheckInPrompts;
using static Martlet.Conversation.CheckInSchedule;
using static Martlet.Conversation.CheckInText;

namespace Martlet.Conversation;

/// <summary>One thing always listening heard the user say lately (<see cref="CheckInFacts.Heard"/>): when, its words and whether
/// Martlet answered it or stayed quiet ([pass]); who spoke most (voice recognition), how many other voices were heard in it,
/// whether people talked over each other, and whether lines this PC played went with it. In memory only, never logged.</summary>
public sealed record CheckInHeard(DateTimeOffset At, string Words, bool Answered)
{
    /// <summary>The name of the voice that spoke most, or null (a voice without a name, or no voice recognized).</summary>
    public string? Speaker { get; init; }
    /// <summary>Voice recognition told apart the voice that spoke most (with a name or not).</summary>
    public bool Recognized { get; init; }
    /// <summary>That voice is the signed-in user's own.</summary>
    public bool Owner { get; init; }
    /// <summary>How many other voices were heard clearly in it.</summary>
    public int OtherVoices { get; init; }
    /// <summary>People talked over each other in it.</summary>
    public bool Overlap { get; init; }
    /// <summary>Lines this PC played (a video, the other side of a call on this PC) went with it.</summary>
    public bool PcPlayed { get; init; }
}

/// <summary>Martlet holds back while the user talks to someone else (<see cref="CheckInOutcome.HoldBack"/>): since when, who the
/// user talks to and how in a few words (<see cref="About"/>, from the check-in's answer; never logged or saved), when the user
/// last spoke (<see cref="Heard"/>) and the check-in that started it (<see cref="By"/>). It ends <see cref="CheckIns.AsideQuiet"/>
/// after the user last spoke, or when a check-in answers BACK.</summary>
public sealed record CheckInAside(DateTimeOffset Since, string About, DateTimeOffset Heard, string By)
{
    /// <summary>When it ends unless the user speaks again.</summary>
    public DateTimeOffset Until => Heard + CheckIns.AsideQuiet;

    public bool HoldsAt(DateTimeOffset now) => now <= Until;

    public override string ToString() => $"{nameof(CheckInAside)} (since {Since:O}, until {Until:O}, by {By})";
}

public sealed partial record CheckInState
{
    /// <summary>What always listening heard the user say lately, oldest first.</summary>
    public IReadOnlyList<CheckInHeard> Heard { get; init; } = [];
    /// <summary>Martlet holds back now while the user talks to someone else, or null.</summary>
    public CheckInAside? Aside { get; init; }
}

public static partial class CheckIns
{
    /// <summary>Talking to someone else: right after always listening hears the user, a pool member decides whether they talk to
    /// someone else (in the room, in a call on this PC, or on a phone Martlet can't see), and Martlet holds back while that lasts
    /// (<see cref="CheckInOutcome.HoldBack"/>).</summary>
    public const string Aside = "aside";
    /// <summary>How long an ASIDE: line (who the user talks to and how) may be.</summary>
    public const int MaximumAsideCharacters = 120;
    /// <summary>How many things the user said <see cref="CheckInFacts.Heard"/> shows at most.</summary>
    public const int MaximumHeard = 12;
    /// <summary>How long Martlet holds back after the user last spoke, when no check-in says BACK first: a phone call has quiet
    /// stretches while the user listens.</summary>
    public static TimeSpan AsideQuiet => TimeSpan.FromMinutes(3);
    /// <summary>How long what the user said counts for <see cref="CheckInFacts.Heard"/>.</summary>
    public static TimeSpan HeardWindow => PeopleWindow;

    /// <summary>Built-in check-ins an older Martlet had, whose saved choices are dropped: On a call (<c>call</c>), whose job
    /// (keep quiet during a call on this PC) Talking to someone else does, also for calls Martlet can't see.</summary>
    public static bool Retired(string id) => id is "call";

    /// <summary>What always listening heard lately (<see cref="CheckInFacts.Heard"/>), as the check reads it: each thing the user
    /// said in the last <see cref="HeardWindow"/>, oldest first, with when, who spoke, the other voices and this PC's sound with
    /// it and whether Martlet answered; then whether Martlet holds back now.</summary>
    public static string Heard(CheckInState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var minutes = (int)HeardWindow.TotalMinutes;
        var recent = state.Heard.Where(h => state.Now - h.At <= HeardWindow).TakeLast(MaximumHeard).ToList();
        var text = new StringBuilder();
        if (recent.Count == 0) text.Append($"Always listening heard nothing from the user in the last {minutes} minutes.");
        else
        {
            text.Append($"What always listening heard in the last {minutes} minutes, oldest first (on a phone Martlet can't see, it " +
                "hears only the user's side):");
            foreach (var heard in recent)
            {
                text.Append("\n- ").Append(SaidLately.Clock(heard.At)).Append(" (").Append(Reminders.Span(state.Now - heard.At)).Append(" ago)");
                if (heard.Recognized)
                    text.Append(", ").Append(heard.Speaker is { Length: > 0 } name ? Clip(OneLine(name), 60) : "a voice Martlet doesn't know")
                        .Append(heard.Owner ? " (the user's own voice)" : "");
                if (heard.OtherVoices > 0) text.Append(heard.OtherVoices == 1 ? ", 1 other voice too" : $", {heard.OtherVoices} other voices too");
                if (heard.Overlap) text.Append(", people talked over each other");
                if (heard.PcPlayed) text.Append(", with lines this PC played");
                text.Append(": \"").Append(Clip(OneLine(heard.Words), 300)).Append("\" (")
                    .Append(heard.Answered ? $"{state.Who} answered" : $"{state.Who} stayed quiet").Append(')');
            }
        }
        text.Append('\n').Append(state.Aside is { } aside && aside.HoldsAt(state.Now)
            ? $"{state.Who} holds back now (since {SaidLately.Clock(aside.Since)}, {Reminders.Span(state.Now - aside.Since)} ago), because " +
                $"the user seems to be talking to someone else: {Clip(OneLine(aside.About), MaximumAsideCharacters)}."
            : $"{state.Who} doesn't hold back now.");
        return text.ToString();
    }

    /// <summary>The hold after a check-in's ASIDE: line (<paramref name="about"/>) at <paramref name="now"/>: a new one, or
    /// <paramref name="holding"/> with the new words and its time renewed (it keeps its start).</summary>
    public static CheckInAside HoldBack(CheckInAside? holding, string about, string by, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(about);
        var still = holding is not null && holding.HoldsAt(now) ? holding : null;
        return new(still?.Since ?? now, Clip(OneLine(about), MaximumAsideCharacters), now, still?.By ?? by);
    }

    /// <summary>The user spoke at <paramref name="at"/>: a hold that still holds lasts <see cref="AsideQuiet"/> from then; one
    /// that ended is gone (null).</summary>
    public static CheckInAside? Spoke(CheckInAside? holding, DateTimeOffset at) =>
        holding is not null && holding.HoldsAt(at) ? holding with { Heard = at > holding.Heard ? at : holding.Heard } : null;

    /// <summary>What every request says while Martlet holds back (Companion › Prompts › Check-in: holding back while you talk to
    /// someone else), in the notes at the end of the message; null when the owner emptied that prompt.</summary>
    public static string? AsideNote(PromptSettings? prompts, CheckInAside aside)
    {
        ArgumentNullException.ThrowIfNull(aside);
        return PromptSettings.Fill(prompts, PromptCatalog.CheckInAsideNote, ("about", aside.About), ("silent", StayQuiet.Marker));
    }
}
