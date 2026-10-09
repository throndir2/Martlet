using Martlet.Core.Contracts;

namespace Martlet.Conversation;

/// <summary>What happens that starts a check-in at once (Companion › Check-ins › It starts when; docs/CONVERSATION.md#check-ins).
/// A check-in with a trigger ticked runs only when one of them fires, at most once per its pace (the pace is its cooldown); a
/// check-in with none runs on its pace. The first triggers come from the touches on the desktop character
/// (<see cref="TouchTriggers"/>). To add a trigger that is not about touches: add a flag here, its words in
/// <see cref="CheckIns.TriggerWords"/>, its box on the page, and fire it on the desktop (<c>MainWindow.FireCheckIns</c>).</summary>
[Flags]
public enum CheckInTriggers
{
    None = 0,
    /// <summary>A burst of touches Martlet noticed has ended: <see cref="TouchDebounce.Quiet"/> passed after the last one.</summary>
    TouchesEnded = 1,
    /// <summary>Those touches included an intimate zone (<see cref="PhysicalEvent.Intimate"/>).</summary>
    IntimateTouch = 2,
    /// <summary>A stroke among them crossed <see cref="CheckIns.StrokeZones"/> zones or more.</summary>
    StrokeAcrossZones = 4,
    /// <summary>They touched a place the user keeps coming back to (a <see cref="Martlet.Conversation.TouchHabit"/> of the
    /// touch ledger).</summary>
    KeepsComingBack = 8
}

/// <summary>Triggers that fired (<see cref="Fired"/>, one flag or more), when, and what happened in a few words with counts only
/// (never where, or what was said), for the page, the status and the log.</summary>
public sealed record CheckInTrigger(CheckInTriggers Fired, DateTimeOffset At, string What);

/// <summary>Collects the touches on the desktop character until they settle, then says which check-in triggers they fire
/// (<see cref="Settle"/>). Only touches count (<see cref="PhysicalKinds.IsTouch"/>), never moving or zooming the character. It
/// never takes anything from the touch ledger, so the touch reply and the next reply still get every touch. Not thread-safe: the
/// desktop uses it on its UI thread.</summary>
public sealed class TouchTriggers
{
    private int touches, zones;
    private bool intimate;
    private TouchHabit? habit;

    /// <summary>Touches wait to settle.</summary>
    public bool Waiting => touches > 0;

    /// <summary>A touch Martlet noticed; <paramref name="often"/> are the places the touch ledger says the user keeps coming
    /// back to now (<see cref="TouchBurst.Often"/>).</summary>
    public void Touched(PhysicalEvent physical, IReadOnlyList<TouchHabit>? often = null)
    {
        ArgumentNullException.ThrowIfNull(physical);
        if (!PhysicalKinds.IsTouch(physical.Kind)) return;
        touches++;
        intimate |= physical.Intimate;
        if (physical.Kind == PhysicalKind.Stroke && physical.Zones is { Count: > 0 } crossed)
            zones = Math.Max(zones, crossed.Distinct(StringComparer.Ordinal).Count());
        if (often?.MaxBy(h => h.Count) is { } most && (habit is null || most.Count >= habit.Count)) habit = most;
    }

    /// <summary>The touches settled at <paramref name="now"/>: the triggers they fire (always <see cref="CheckInTriggers.TouchesEnded"/>,
    /// and the others they meet), or null when there were none. It starts again empty.</summary>
    public CheckInTrigger? Settle(DateTimeOffset now)
    {
        if (touches == 0) return null;
        var fired = CheckInTriggers.TouchesEnded;
        var what = new List<string> { touches == 1 ? "1 touch" : $"{touches} touches" };
        if (intimate)
        {
            fired |= CheckInTriggers.IntimateTouch;
            what.Add("an intimate one");
        }
        if (zones >= CheckIns.StrokeZones)
        {
            fired |= CheckInTriggers.StrokeAcrossZones;
            what.Add($"a stroke across {zones} zones");
        }
        if (habit is { } often)
        {
            fired |= CheckInTriggers.KeepsComingBack;
            what.Add($"one place touched {often.Count} times lately");
        }
        touches = zones = 0;
        intimate = false;
        habit = null;
        return new(fired, now, string.Join(", ", what));
    }
}

public static partial class CheckIns
{
    /// <summary>Every trigger Martlet offers.</summary>
    public const CheckInTriggers AllTriggers = CheckInTriggers.TouchesEnded | CheckInTriggers.IntimateTouch |
        CheckInTriggers.StrokeAcrossZones | CheckInTriggers.KeepsComingBack;

    /// <summary>How many zones a stroke crosses to fire <see cref="CheckInTriggers.StrokeAcrossZones"/>.</summary>
    public const int StrokeZones = 3;

    /// <summary>How long a fired trigger waits for its check-in to run (after its pace and its other waits). As long as the touch
    /// ledger keeps touches (<see cref="TouchLedger.MaximumAge"/>), so a check-in never runs on touches the ledger let go.</summary>
    public static TimeSpan TriggerAge => TouchLedger.MaximumAge;

    private static readonly (CheckInTriggers Trigger, string Words)[] TriggerPhrases =
    [
        (CheckInTriggers.TouchesEnded, "your touches to end"),
        (CheckInTriggers.IntimateTouch, "an intimate touch"),
        (CheckInTriggers.StrokeAcrossZones, $"a stroke across {StrokeZones} zones"),
        (CheckInTriggers.KeepsComingBack, "you to keep coming back to one place")
    ];

    /// <summary>The triggers in words, for "it waits for ...": "your touches to end or an intimate touch".</summary>
    public static string TriggerWords(CheckInTriggers triggers)
    {
        var words = TriggerPhrases.Where(p => triggers.HasFlag(p.Trigger)).Select(p => p.Words).ToArray();
        return words.Length switch
        {
            0 => "nothing",
            1 => words[0],
            _ => string.Join(", ", words[..^1]) + " or " + words[^1]
        };
    }

    /// <summary>Whether <paramref name="fired"/> starts <paramref name="checkIn"/> at <paramref name="now"/>: it is one of the
    /// check-in's triggers and isn't older than <see cref="TriggerAge"/>.</summary>
    public static bool Fires(CheckIn checkIn, CheckInTrigger? fired, DateTimeOffset now) =>
        fired is not null && (fired.Fired & checkIn.Triggers) != 0 && now - fired.At <= TriggerAge;

    internal static void CheckTriggers(CheckInTriggers triggers) =>
        ContractRules.Require((triggers & ~AllTriggers) == 0, "A check-in starts only on the triggers Martlet offers.");
}
