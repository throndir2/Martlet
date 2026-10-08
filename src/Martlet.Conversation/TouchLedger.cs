namespace Martlet.Conversation;

/// <summary>What the user did to the desktop character. <see cref="Tap"/> is a poke; <see cref="Pat"/> a tap on the top of the
/// head or the hair; <see cref="Hold"/> a long press (about 600 ms or more) that didn't move; <see cref="Stroke"/> a drag across
/// the character while its place is locked. The rest are what the user did to the character's window: moved it (to another
/// monitor, say), zoomed, panned, resized, locked or unlocked its place, hid or showed it.</summary>
public enum PhysicalKind { Tap, Pat, Hold, Stroke, Moved, Zoomed, Panned, Resized, Locked, Unlocked, Hidden, Shown }

/// <summary>One thing the user did to the character, at <paramref name="At"/> (a monotonic time). <paramref name="Zone"/> is
/// where, as the character hears it ("the top of your head", "your left cheek"), and <paramref name="Zones"/> several places
/// (a stroke across them); <paramref name="Label"/> a short name for the conversation's history ("top of head");
/// <paramref name="Detail"/> more about it ("to another monitor", "in"); <paramref name="Hint"/> the owner's own words for a
/// zone's touch (Companion › Touch › Touch zones).</summary>
public sealed record PhysicalEvent(PhysicalKind Kind, TimeSpan At, string? Zone = null, string? Label = null, string? Detail = null,
    string? Hint = null, IReadOnlyList<string>? Zones = null);

/// <summary>Things of one kind, in one place, in a row: how many and from when to when.</summary>
public sealed record TouchEntry(PhysicalKind Kind, string? Zone, string? Label, string? Detail, string? Hint, int Count, TimeSpan First,
    TimeSpan Last);

/// <summary>What the ledger held when it was taken: its <see cref="Line"/> for the Thinking model and its
/// <see cref="HistoryLine"/> for the conversation's history.</summary>
public sealed record TouchBurst(IReadOnlyList<TouchEntry> Entries)
{
    public string Line => TouchWording.Line(Entries);
    public string HistoryLine => TouchWording.HistoryLine(Entries);
    /// <summary>The talk window's note for a reply to these touches alone ("You touched Ivy (touch: top of head pat x3)").</summary>
    public string Note(string? character) => TouchWording.Note(character, Entries);
    /// <summary>How many things the user did (each tap counts).</summary>
    public int Count => Entries.Sum(e => e.Count);
    /// <summary>How many of them may start a reply on their own (<see cref="PhysicalKinds.StartsTurn"/>: touches and moves of the
    /// character), not other things done to its window.</summary>
    public int Touches => Entries.Where(e => PhysicalKinds.StartsTurn(e.Kind)).Sum(e => e.Count);
    /// <summary>Whether any of it may start a touch-only reply on its own (<see cref="PhysicalKinds.StartsTurn"/>).</summary>
    public bool StartsTurn => Entries.Any(e => PhysicalKinds.StartsTurn(e.Kind));
}

/// <summary>What each kind of thing does in the conversation and how it is said.</summary>
public static class PhysicalKinds
{
    /// <summary>Whether this kind is a touch on the character itself: a poke, a pat, a hold or a stroke.</summary>
    public static bool IsTouch(PhysicalKind kind) => kind is PhysicalKind.Tap or PhysicalKind.Pat or PhysicalKind.Hold or PhysicalKind.Stroke;

    /// <summary>Whether this kind may start a touch-only reply on its own. Touches do, and so does moving the character around
    /// (a drag, or to another monitor); the rest of what the user does to the window (zooming, panning, resizing, locking,
    /// hiding or showing it) only goes with the next reply.</summary>
    public static bool StartsTurn(PhysicalKind kind) => IsTouch(kind) || kind == PhysicalKind.Moved;

    /// <summary>What they did, past tense, as the character hears it ("patted the top of your head").</summary>
    public static string Phrase(PhysicalKind kind, string? zone, string? detail)
    {
        var where = zone is { Length: > 0 } ? zone : "you";
        var more = detail is { Length: > 0 } ? " " + detail : "";
        return kind switch
        {
            PhysicalKind.Tap => $"poked {where}{more}",
            PhysicalKind.Pat => $"patted {where}{more}",
            PhysicalKind.Hold => $"pressed and held {where}{more}",
            PhysicalKind.Stroke => detail is "slowly" or "quickly" or "gently" ? $"{detail} stroked {where}" : $"stroked {where}{more}",
            PhysicalKind.Moved => $"moved you{(more.Length > 0 ? more : " around their screen")}",
            PhysicalKind.Zoomed => $"zoomed{(more.Length > 0 ? more : " in on you")}",
            PhysicalKind.Panned => $"panned the view of you{more}",
            PhysicalKind.Resized => $"resized you{more}",
            PhysicalKind.Locked => $"locked you in place{more}",
            PhysicalKind.Unlocked => $"unlocked your place so you can be moved{more}",
            PhysicalKind.Hidden => $"hid you{more}",
            PhysicalKind.Shown => $"showed you again{more}",
            _ => $"touched {where}{more}"
        };
    }

    /// <summary>The short word for the history line ("pat", "poke", "moved").</summary>
    public static string Short(PhysicalKind kind) => kind switch
    {
        PhysicalKind.Tap => "poke",
        PhysicalKind.Pat => "pat",
        PhysicalKind.Hold => "hold",
        PhysicalKind.Stroke => "stroke",
        _ => kind.ToString().ToLowerInvariant()
    };
}

/// <summary>How a run of things the user did is said: one plain line for the Thinking model ("They patted the top of your head
/// 3 times over 2 seconds, then poked your left cheek once.") and a short, stable one for the history
/// ("(touch: top of head pat x3, left cheek poke)").</summary>
public static class TouchWording
{
    public const string HistoryMarker = "(touch:";

    public static string Line(IReadOnlyList<TouchEntry> entries)
    {
        if (entries.Count == 0) return "";
        var parts = entries.Select(Part).ToArray();
        return "They " + string.Join(", then ", parts) + ".";
    }

    private static string Part(TouchEntry entry)
    {
        var zone = entry.Zone ?? (entry.Label is { Length: > 0 } label ? "your " + label : null);
        var text = PhysicalKinds.Phrase(entry.Kind, zone, entry.Detail);
        var touch = PhysicalKinds.IsTouch(entry.Kind);
        if (entry.Count == 1) text += touch ? " once" : "";
        else text += entry.Count == 2 ? " twice" : $" {entry.Count} times";
        var span = entry.Last - entry.First;
        if (entry.Count > 1 && span >= TimeSpan.FromSeconds(1))
        {
            var seconds = (int)Math.Round(span.TotalSeconds);
            text += seconds < 90 ? $" over {seconds} second{(seconds == 1 ? "" : "s")}" : $" over {(int)Math.Round(span.TotalMinutes)} minutes";
        }
        if (entry.Hint is { Length: > 0 } hint) text += $" (\"{hint}\")";
        return text;
    }

    public static string HistoryLine(IReadOnlyList<TouchEntry> entries) =>
        entries.Count == 0 ? "" : HistoryMarker + " " + string.Join(", ", entries.Select(e =>
            (e.Label is { Length: > 0 } label ? label + " " : "") + PhysicalKinds.Short(e.Kind) + (e.Count > 1 ? $" x{e.Count}" : ""))) + ")";

    /// <summary>The talk window's note for a reply to touches alone: who was touched, by <paramref name="character"/>'s name (the
    /// persona's; "Martlet" without one), then the history line ("You touched Ivy (touch: top of head pat x3)").</summary>
    public static string Note(string? character, IReadOnlyList<TouchEntry> entries) =>
        $"You touched {Martlet.Core.Speakers.CompanionNames.Character(character)} {HistoryLine(entries)}";
}

/// <summary>What the user did to the desktop character since Martlet last heard about it (touches on zones Martlet notices,
/// and what the strokes and window log add): runs of the same thing in the same place add up, so a burst of pokes becomes one
/// line. A reply to the user takes it all in its notes; touches on their own may start a short reply
/// (<see cref="TouchDebounce"/>). Bounded: at most <see cref="MaximumEntries"/> runs (the oldest go), and what is older than
/// <see cref="MaximumAge"/> is let go. Thread-safe.</summary>
public sealed class TouchLedger
{
    public const int MaximumEntries = 8;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);

    private readonly object gate = new();
    private readonly List<TouchEntry> entries = [];

    /// <summary>Raised (on the caller's thread) when something was recorded, taken or put back.</summary>
    public event Action? Changed;

    public void Record(PhysicalEvent physical)
    {
        var zone = physical.Zone ?? (physical.Zones is { Count: > 0 } several ? JoinZones(several) : null);
        lock (gate)
        {
            var same = entries.Count > 0 && entries[^1] is var last && last.Kind == physical.Kind && last.Zone == zone &&
                last.Label == physical.Label && last.Detail == physical.Detail;
            if (same)
                entries[^1] = entries[^1] with { Count = Math.Min(entries[^1].Count + 1, 999), Last = physical.At, Hint = physical.Hint ?? entries[^1].Hint };
            else
            {
                entries.Add(new(physical.Kind, zone, physical.Label, physical.Detail, physical.Hint, 1, physical.At, physical.At));
                if (entries.Count > MaximumEntries) entries.RemoveAt(0);
            }
        }
        Changed?.Invoke();
    }

    private static string JoinZones(IReadOnlyList<string> zones) =>
        zones.Count == 1 ? zones[0] : string.Join(", ", zones.Take(zones.Count - 1)) + " and " + zones[^1];

    /// <summary>What waits now (nothing older than <see cref="MaximumAge"/>), or null.</summary>
    public TouchBurst? Peek(TimeSpan now)
    {
        lock (gate)
        {
            entries.RemoveAll(e => now - e.Last > MaximumAge);
            return entries.Count == 0 ? null : new([.. entries]);
        }
    }

    /// <summary>Takes what waits (for a reply's request), or null.</summary>
    public TouchBurst? Drain(TimeSpan now)
    {
        TouchBurst? taken;
        lock (gate)
        {
            entries.RemoveAll(e => now - e.Last > MaximumAge);
            taken = entries.Count == 0 ? null : new([.. entries]);
            entries.Clear();
        }
        if (taken is not null) Changed?.Invoke();
        return taken;
    }

    /// <summary>Puts back what a reply took but never got to answer (it was stopped), before anything newer.</summary>
    public void Restore(TouchBurst burst)
    {
        lock (gate)
        {
            entries.InsertRange(0, burst.Entries);
            while (entries.Count > MaximumEntries) entries.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
        Changed?.Invoke();
    }
}

/// <summary>When touches on their own start a short reply: <see cref="Quiet"/> after the last touch (each touch moves it on), but
/// at most <see cref="Longest"/> after the first, and never sooner than <see cref="Cooldown"/> after the last touch-only reply
/// started (later touches keep adding up meanwhile). The user speaking or typing <see cref="Cancel"/>s it: their reply takes
/// the touches. Times are monotonic.</summary>
public sealed class TouchDebounce
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1200), Longest = TimeSpan.FromSeconds(3), Cooldown = TimeSpan.FromSeconds(4);

    private TimeSpan? first;
    private TimeSpan last;
    private TimeSpan? lastTurn;

    /// <summary>A touch-only reply is waiting to start.</summary>
    public bool Waiting => first is not null;

    /// <summary>When it starts, or null when none waits.</summary>
    public TimeSpan? DueAt
    {
        get
        {
            if (first is not { } started) return null;
            var at = last + Quiet < started + Longest ? last + Quiet : started + Longest;
            return lastTurn is { } turn && turn + Cooldown > at ? turn + Cooldown : at;
        }
    }

    /// <summary>A touch that may start a reply happened at <paramref name="now"/>.</summary>
    public void Touched(TimeSpan now)
    {
        first ??= now;
        last = now;
    }

    public bool Due(TimeSpan now) => DueAt is { } at && now >= at;

    /// <summary>The user started speaking or typing: no touch-only reply; theirs takes the touches.</summary>
    public void Cancel() => first = null;

    /// <summary>A touch-only reply started at <paramref name="now"/>.</summary>
    public void Started(TimeSpan now)
    {
        first = null;
        lastTurn = now;
    }
}
