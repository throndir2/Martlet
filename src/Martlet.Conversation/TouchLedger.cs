using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>What the user did to the desktop character. <see cref="Tap"/> is a poke; <see cref="Pat"/> a tap on the top of the
/// head or the hair; <see cref="Hold"/> a long press (about 600 ms or more) that didn't move; <see cref="Stroke"/> a drag across
/// the character while its place is locked. The rest are what the user did to the character's window: moved it (to another
/// monitor, say), zoomed, panned, resized, locked or unlocked its place, hid or showed it.</summary>
public enum PhysicalKind { Tap, Pat, Hold, Stroke, Moved, Zoomed, Panned, Resized, Locked, Unlocked, Hidden, Shown }

/// <summary>Which touches stop Martlet while it says a reply or a remark aloud (Companion › Touch › Touch zones › When you touch
/// Martlet while it talks): <see cref="Any"/> touch it notices (the default), only <see cref="Intimate"/> ones, or
/// <see cref="Never"/> (it finishes first, and the touches wait for its next reply).</summary>
public enum TouchInterrupts { Any, Intimate, Never }

/// <summary>One thing the user did to the character, at <paramref name="At"/> (a monotonic time). <paramref name="Zone"/> is
/// where, as the character hears it ("the top of your head", "your left cheek", or a stroke's whole path), and
/// <paramref name="Zones"/> the places it touched one by one (each zone a stroke crossed, or each zone a touch landed in where
/// zones overlap; joined into the place when <paramref name="Zone"/> is null), which count toward how often each place is
/// touched; <paramref name="Label"/> a short name
/// for the conversation's history ("top of head"); <paramref name="Detail"/> more about it ("to another monitor", "in");
/// <paramref name="Hint"/> the owner's own words for a zone's touch (Companion › Touch › Touch zones);
/// <paramref name="Intimate"/> whether it touched an intimate zone; <paramref name="Feeling"/> how the persona feels about being
/// touched there, from its touch temperament ("you love being touched there").</summary>
public sealed record PhysicalEvent(PhysicalKind Kind, TimeSpan At, string? Zone = null, string? Label = null, string? Detail = null,
    string? Hint = null, IReadOnlyList<string>? Zones = null, bool Intimate = false, string? Feeling = null);

/// <summary>Things of one kind, in one place, in a row: how many and from when to when, the places they touched
/// (<see cref="PhysicalEvent.Zones"/>), how the persona feels about it and whether it was intimate.</summary>
public sealed record TouchEntry(PhysicalKind Kind, string? Zone, string? Label, string? Detail, string? Hint, int Count, TimeSpan First,
    TimeSpan Last, IReadOnlyList<string>? Places = null, string? Feeling = null, bool Intimate = false);

/// <summary>A place the user keeps coming back to: <see cref="Count"/> touches there over the last <see cref="Over"/>.</summary>
public sealed record TouchHabit(string Place, int Count, TimeSpan Over);

/// <summary>A touch stopped Martlet while it was talking, at <paramref name="At"/>: what it had said aloud by then
/// (<paramref name="Said"/>, its last words) and the user's own words it was answering (<paramref name="Answering"/>; null for
/// its own remarks).</summary>
public sealed record TouchCut(string? Said, string? Answering, TimeSpan At)
{
    /// <summary>At most this many characters of what Martlet said or was answering are kept (the end of it).</summary>
    public const int MaximumCharacters = 300;

    /// <summary>The end of <paramref name="text"/>, at most <see cref="MaximumCharacters"/> long, from a word's start; null when
    /// there is nothing.</summary>
    public static string? Tail(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length <= MaximumCharacters) return trimmed;
        var tail = trimmed[^MaximumCharacters..];
        var space = tail.IndexOf(' ');
        return "…" + (space is > 0 and < 40 ? tail[(space + 1)..] : tail);
    }
}

/// <summary>What the ledger held when it was taken: its <see cref="Line"/> for the Thinking model and its
/// <see cref="HistoryLine"/> for the conversation's history; the places the user keeps coming back to (<see cref="Often"/>) and,
/// when a touch stopped Martlet talking, what it was saying (<see cref="Cut"/>).</summary>
public sealed record TouchBurst(IReadOnlyList<TouchEntry> Entries, IReadOnlyList<TouchHabit>? Often = null, TouchCut? Cut = null)
{
    public string Line => TouchWording.Line(Entries) + TouchWording.Often(Often);
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

    /// <summary>Whether <paramref name="physical"/> stops Martlet while it talks, with <paramref name="choice"/>: only touches
    /// ever do (moving or zooming the character never does), any of them or only intimate ones.</summary>
    public static bool Interrupts(TouchInterrupts choice, PhysicalEvent physical) => IsTouch(physical.Kind) &&
        (choice == TouchInterrupts.Any || choice == TouchInterrupts.Intimate && physical.Intimate);

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
        string?[] more = [entry.Hint is { Length: > 0 } hint ? $"\"{hint}\"" : null, entry.Feeling is { Length: > 0 } feeling ? feeling : null];
        if (more.Any(m => m is not null)) text += " (" + string.Join("; ", more.OfType<string>()) + ")";
        return text;
    }

    /// <summary>The places the user keeps coming back to, as a sentence after the line (with a space before it): "They keep
    /// coming back to your groin: 9 times in the last 4 minutes." Empty when there are none.</summary>
    public static string Often(IReadOnlyList<TouchHabit>? often)
    {
        if (often is not { Count: > 0 }) return "";
        var minutes = Math.Max(1, (int)Math.Ceiling(often.Max(h => h.Over).TotalMinutes));
        var over = minutes == 1 ? "the last minute" : $"the last {minutes} minutes";
        return often.Count == 1 ? $" They keep coming back to {often[0].Place}: {often[0].Count} times in {over}."
            : $" They keep coming back to {string.Join(" and ", often.Select(h => $"{h.Place} ({h.Count} times)"))} in {over}.";
    }

    public static string HistoryLine(IReadOnlyList<TouchEntry> entries) =>
        entries.Count == 0 ? "" : HistoryMarker + " " + string.Join(", ", entries.Select(e =>
            (e.Label is { Length: > 0 } label ? label + " " : "") + PhysicalKinds.Short(e.Kind) + (e.Count > 1 ? $" x{e.Count}" : ""))) + ")";

    /// <summary>The talk window's note for a reply to touches alone: who was touched, by <paramref name="character"/>'s name (the
    /// persona's; "Martlet" without one), then the history line ("You touched Ivy (touch: top of head pat x3)").</summary>
    public static string Note(string? character, IReadOnlyList<TouchEntry> entries) =>
        $"You touched {Martlet.Core.Speakers.CompanionNames.Character(character)} {HistoryLine(entries)}";

    /// <summary>What the Thinking model hears of <paramref name="burst"/>, the {touches} of Companion › Prompts › Touched and
    /// Touched, with your message: its <see cref="TouchBurst.Line"/>, and when a touch stopped Martlet talking
    /// (<see cref="TouchBurst.Cut"/>), the Touched, cutting you off prompt with what Martlet had said aloud and the user's
    /// message it was answering (the end of each, <see cref="TouchCut.Tail"/>).</summary>
    public static string Told(PromptSettings? prompts, TouchBurst burst)
    {
        if (burst.Cut is not { } cut) return burst.Line;
        var said = TouchCut.Tail(cut.Said) is { } aloud ? $" You had said, out loud: \"{aloud}\"" : "";
        var answering = TouchCut.Tail(cut.Answering) is { } asked ? $" You were answering their message: \"{asked}\"" : "";
        return PromptSettings.Fill(prompts, PromptCatalog.TouchedCutIn, ("said", said), ("answering", answering)) is { } note
            ? burst.Line + " " + note : burst.Line;
    }
}

/// <summary>What the user did to the desktop character since Martlet last heard about it (touches on zones Martlet notices,
/// and what the strokes and window log add): runs of the same thing in the same place add up, so a burst of pokes becomes one
/// line. A reply to the user takes it all in its notes; touches on their own may start a short reply
/// (<see cref="TouchDebounce"/>). Bounded: at most <see cref="MaximumEntries"/> runs (the oldest go), and what is older than
/// <see cref="MaximumAge"/> is let go. Each place a touch reached also counts for <see cref="OftenWindow"/>, across replies, so
/// a burst names the places the user keeps coming back to (<see cref="OftenTouches"/> or more touches there, more than this
/// burst alone has); and when a touch stopped Martlet talking (<see cref="CutIn"/>) the next burst carries what it was saying.
/// Thread-safe.</summary>
public sealed class TouchLedger
{
    public const int MaximumEntries = 8, OftenTouches = 5, MaximumRecent = 400;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2), OftenWindow = TimeSpan.FromMinutes(10);

    private readonly object gate = new();
    private readonly List<TouchEntry> entries = [];
    // Every place a touch reached over the last OftenWindow, kept across replies (Drain leaves it).
    private readonly List<(string Place, TimeSpan At)> recent = [];
    private TouchCut? cut;

    /// <summary>Raised (on the caller's thread) when something was recorded, taken or put back.</summary>
    public event Action? Changed;

    public void Record(PhysicalEvent physical)
    {
        var zone = physical.Zone ?? (physical.Zones is { Count: > 0 } several ? JoinZones(several) : null);
        IReadOnlyList<string>? places = !PhysicalKinds.IsTouch(physical.Kind) ? null
            : physical.Zones is { Count: > 0 } each ? each : zone is not null ? [zone] : null;
        lock (gate)
        {
            var same = entries.Count > 0 && entries[^1] is var last && last.Kind == physical.Kind && last.Zone == zone &&
                last.Label == physical.Label && last.Detail == physical.Detail;
            if (same)
                entries[^1] = entries[^1] with
                {
                    Count = Math.Min(entries[^1].Count + 1, 999), Last = physical.At, Hint = physical.Hint ?? entries[^1].Hint,
                    Feeling = physical.Feeling ?? entries[^1].Feeling, Intimate = physical.Intimate || entries[^1].Intimate
                };
            else
            {
                entries.Add(new(physical.Kind, zone, physical.Label, physical.Detail, physical.Hint, 1, physical.At, physical.At, places,
                    physical.Feeling, physical.Intimate));
                if (entries.Count > MaximumEntries) entries.RemoveAt(0);
            }
            foreach (var place in places ?? []) recent.Add((place, physical.At));
            recent.RemoveAll(r => physical.At - r.At > OftenWindow);
            if (recent.Count > MaximumRecent) recent.RemoveRange(0, recent.Count - MaximumRecent);
        }
        Changed?.Invoke();
    }

    /// <summary>A touch stopped Martlet while it talked (<see cref="TouchCut"/>): the next burst carries it, so its reply knows
    /// what Martlet was saying. A newer one replaces it.</summary>
    public void CutIn(TouchCut stopped)
    {
        lock (gate) cut = stopped;
        Changed?.Invoke();
    }

    private static string JoinZones(IReadOnlyList<string> zones) =>
        zones.Count == 1 ? zones[0] : string.Join(", ", zones.Take(zones.Count - 1)) + " and " + zones[^1];

    // What waits now as a burst (under the gate), after letting go of what is too old.
    private TouchBurst? Burst(TimeSpan now)
    {
        entries.RemoveAll(e => now - e.Last > MaximumAge);
        if (cut is { } stopped && now - stopped.At > MaximumAge) cut = null;
        return entries.Count == 0 ? null : new([.. entries], Often(now), cut);
    }

    // The places this burst touched that the user keeps coming back to: more touches there over OftenWindow than in this
    // burst, and at least OftenTouches; the two most touched.
    private IReadOnlyList<TouchHabit>? Often(TimeSpan now)
    {
        var mine = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
            foreach (var place in entry.Places ?? [])
                mine[place] = mine.GetValueOrDefault(place) + entry.Count;
        var habits = mine.Select(m => (Place: m.Key, Mine: m.Value, All: recent.Where(r => r.Place == m.Key && now - r.At <= OftenWindow).ToArray()))
            .Where(m => m.All.Length >= OftenTouches && m.All.Length > m.Mine)
            .OrderByDescending(m => m.All.Length).Take(2)
            .Select(m => new TouchHabit(m.Place, m.All.Length, now - m.All.Min(r => r.At))).ToArray();
        return habits.Length == 0 ? null : habits;
    }

    /// <summary>What waits now (nothing older than <see cref="MaximumAge"/>), or null.</summary>
    public TouchBurst? Peek(TimeSpan now)
    {
        lock (gate) return Burst(now);
    }

    /// <summary>Takes what waits (for a reply's request), or null.</summary>
    public TouchBurst? Drain(TimeSpan now)
    {
        TouchBurst? taken;
        lock (gate)
        {
            taken = Burst(now);
            entries.Clear();
            if (taken is not null) cut = null;
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
            cut ??= burst.Cut;
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            recent.Clear();
            cut = null;
        }
        Changed?.Invoke();
    }
}

/// <summary>When touches on their own start a short reply: <see cref="Quiet"/> after the last touch (each touch moves it on), but
/// at most <see cref="Longest"/> after the first, and never sooner than <see cref="Cooldown"/> after the last touch-only reply
/// started (later touches keep adding up meanwhile). A touch that stopped Martlet talking (<see cref="CutIn"/>) is answered
/// sooner: <see cref="CutInQuiet"/> after the last touch, with no cooldown. The user speaking or typing <see cref="Cancel"/>s
/// it: their reply takes the touches. Times are monotonic.</summary>
public sealed class TouchDebounce
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1200), Longest = TimeSpan.FromSeconds(3), Cooldown = TimeSpan.FromSeconds(4),
        CutInQuiet = TimeSpan.FromMilliseconds(500);

    private TimeSpan? first;
    private TimeSpan last;
    private TimeSpan? lastTurn;
    private bool cutIn;

    /// <summary>A touch-only reply is waiting to start.</summary>
    public bool Waiting => first is not null;

    /// <summary>The waiting reply answers a touch that stopped Martlet talking.</summary>
    public bool CutInWaiting => first is not null && cutIn;

    /// <summary>When it starts, or null when none waits.</summary>
    public TimeSpan? DueAt
    {
        get
        {
            if (first is not { } started) return null;
            var quiet = cutIn ? CutInQuiet : Quiet;
            var at = last + quiet < started + Longest ? last + quiet : started + Longest;
            return !cutIn && lastTurn is { } turn && turn + Cooldown > at ? turn + Cooldown : at;
        }
    }

    /// <summary>A touch that may start a reply happened at <paramref name="now"/>.</summary>
    public void Touched(TimeSpan now)
    {
        first ??= now;
        last = now;
    }

    /// <summary>The touch just noted (<see cref="Touched"/>) stopped Martlet talking: its reply comes sooner.</summary>
    public void CutIn()
    {
        if (first is not null) cutIn = true;
    }

    public bool Due(TimeSpan now) => DueAt is { } at && now >= at;

    /// <summary>The user started speaking or typing: no touch-only reply; theirs takes the touches.</summary>
    public void Cancel()
    {
        first = null;
        cutIn = false;
    }

    /// <summary>A touch-only reply started at <paramref name="now"/>.</summary>
    public void Started(TimeSpan now)
    {
        first = null;
        cutIn = false;
        lastTurn = now;
    }
}
