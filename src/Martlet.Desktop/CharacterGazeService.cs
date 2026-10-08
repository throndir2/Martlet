using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>
/// Where the showing character looks. Its head and eyes follow its usual gaze on their own (the overlay does that): the mouse,
/// the mouse when it's near, straight ahead or the window you're using, as you chose (Companion › Eyes › Where the
/// character looks, or the character's right-click Eyes menu) or else as its personality decided (the persona's temperament).
/// While the character may change where it looks, a reply's mode tag (<c>{look ahead}</c>) changes that until a reply changes it
/// again, and every reply is told its usual gaze (with a note while its own choice holds the eyes). A touch whose temperament
/// says so turns the eyes to the mouse for a few seconds. With Companion › Vision › Glances at your screen set to Martlet
/// decides, each new screenshot Martlet takes of your screen (every 3 seconds while vision watches your active window or whole
/// screen) is also a chance to look elsewhere: something that just appeared or moved in one place draws a short glance
/// (<see cref="GazeDirector"/>), and a screen glance's Thinking model may start its answer with a look tag
/// (<c>{look top right}</c>) to look at that part of the picture for a few seconds. Screenshots are compared on this PC as
/// coarse grey grids, and only a point on the desktop or a gaze reaches the character.
/// </summary>
internal sealed class CharacterGazeService
{
    // How recent the last screenshot must be for Martlet to count as watching your screen.
    private static readonly TimeSpan Watching = TimeSpan.FromSeconds(10);
    // What a newly shown character starts with: following the mouse, with As the personality decides and Let the character
    // change it checked on its Eyes menu.
    private static readonly (GazeMode Mode, string Choice, bool Free) Fresh = (GazeMode.Mouse, RendererGaze.Personality, true);
    private readonly AvatarController avatar;
    private readonly TimeProvider clock;
    private readonly GazeDirector director;
    private readonly object gate = new();
    private bool decides;
    // The screen the latest screenshot showed: a screen glance's look tags point into it.
    private ScreenRect? lookArea;
    // The character's windows at the screenshot before: a speech bubble that just closed still left a change where it was.
    private ScreenRect[] lastOverlay = [], lastHidden = [];
    private long? observedAt;
    private GazeSpot? holding;
    private long holdUntil;
    private GazeVerdict? verdict;
    private (GazeSpot Spot, DateTime At)? lastSpot;
    private RendererLook? look;
    // The owner's choice (null: as the personality decides), whether the character may change where it looks, the Thinking
    // model's choice and when it made it, until when (and why) a touch keeps the eyes on the mouse, what the overlay was last
    // told and what applied when last checked. Messages to the overlay go one after another.
    private GazeMode? owner;
    private bool free = true;
    private GazeMode? chosen;
    private DateTimeOffset chosenAt;
    private long attendUntil;
    private string attendWhy = "";
    private (GazeMode Mode, string Choice, bool Free) told = Fresh, known = Fresh;
    private Task sending = Task.CompletedTask;

    internal CharacterGazeService(AvatarController avatar, TimeProvider? clock = null)
    {
        this.avatar = avatar;
        this.clock = clock ?? TimeProvider.System;
        director = new(this.clock);
    }

    /// <summary>The usual gaze the active persona's temperament decided, or null (not decided yet).</summary>
    internal Func<GazeMode?> Personality { get; set; } = () => null;

    /// <summary>Where the eyes go when nothing else draws them, and why.</summary>
    internal GazeSettings Settings { get { lock (gate) return SettingsLocked(); } }

    private GazeSettings SettingsLocked() => new(owner, Personality(), free, chosen);

    /// <summary>Raised (on any thread) when the usual gaze, the reply's choice or a touch's look changes.</summary>
    internal event Action? Changed;

    /// <summary>The owner's choice (null: as the personality decides) and whether the character may change where it looks. A new
    /// choice, or taking that away, ends the Thinking model's own choice; a showing character follows at once.</summary>
    internal void Configure(GazeMode? usual, bool mayChange)
    {
        lock (gate)
        {
            if (owner == usual && free == mayChange) return;
            if (owner != usual || !mayChange) chosen = null;
            owner = usual;
            free = mayChange;
        }
        Refresh();
        Changed?.Invoke();
    }

    /// <summary>Tells a showing character its gaze and Eyes menu again when they changed (also after the persona in use or its
    /// temperament changed). It goes back to that gaze at once. Raises <see cref="Changed"/> when what applies changed, also
    /// while the character is hidden.</summary>
    internal void Refresh()
    {
        lock (gate)
        {
            var state = StateLocked();
            var changed = state != known;
            known = state;
            var send = avatar.IsShowing && state != told;
            if (send)
            {
                told = state;
                holding = null;
                holdUntil = attendUntil = 0;
                SendLocked(new RendererGaze(Mode: state.Mode, Choice: state.Choice, Free: state.Free));
            }
            if (!changed && !send) return;
        }
        Changed?.Invoke();
    }

    private (GazeMode Mode, string Choice, bool Free) StateLocked() => (SettingsLocked().Mode, RendererGaze.ChoiceOf(owner), free);

    /// <summary>A newly shown character starts following the mouse with As the personality decides and Let the character change
    /// it checked: it is told otherwise when that isn't so. A failure never stops it showing.</summary>
    internal async Task RestoreAsync(IAvatarRenderer target, CancellationToken token)
    {
        (GazeMode Mode, string Choice, bool Free) state;
        lock (gate)
        {
            state = StateLocked();
            told = known = state;
            holding = null;
            holdUntil = attendUntil = 0;
        }
        if (state == Fresh) return;
        try { await target.SendAsync("gaze", new RendererGaze(Mode: state.Mode, Choice: state.Choice, Free: state.Free), token); }
        catch (Exception error) when (RendererFailures.Is(error, token))
        {
            // The next change sends it again.
            lock (gate) told = default;
            RendererFailures.Log("The character couldn't be told where to look", error);
        }
    }

    /// <summary>What a reply is told while the character shows and may change where it looks: its usual gaze and the look tags
    /// (in the instructions, the same from message to message) and, while its own choice holds the eyes, a note on that
    /// (<see cref="CharacterActionPrompt.Looking"/>, for the newest message only). Null otherwise.</summary>
    internal CharacterActionPrompt? Prompt(Martlet.Core.Settings.PromptSettings? prompts)
    {
        if (!avatar.IsShowing) return null;
        GazeSettings settings;
        DateTimeOffset at;
        lock (gate)
        {
            if (!free) return null;
            settings = SettingsLocked();
            at = chosenAt;
        }
        return CharacterGaze.ReplyPrompt(prompts, settings.Usual) is { } prompt
            ? prompt with { Looking = CharacterGaze.Note(prompts, settings, clock.GetUtcNow() - at) } : null;
    }

    /// <summary>A touch turns the character's eyes to the mouse pointer for <paramref name="seconds"/> (its temperament says so),
    /// whatever its usual gaze; glances at the screen wait meanwhile.</summary>
    internal void Attend(double seconds, string why)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || !avatar.IsShowing) return;
        seconds = Math.Clamp(seconds, RendererGaze.MinimumSeconds, CharacterGaze.MaximumAttention);
        lock (gate)
        {
            attendUntil = clock.GetTimestamp() + (long)(seconds * clock.TimestampFrequency);
            attendWhy = why;
            holding = null;
            holdUntil = 0;
            SendLocked(new RendererGaze(Mouse: true, Seconds: seconds));
        }
        ErrorLog.Info($"The character looks at the mouse for {seconds:0.#} s after {why}.");
        Changed?.Invoke();
        // Say so again when the look ends.
        Task.Delay(TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(100)).ContinueWith(_ => Changed?.Invoke(), TaskScheduler.Default).Forget();
    }

    private bool Attending => attendUntil != 0 && clock.GetTimestamp() < attendUntil;

    /// <summary>What a gaze does, for the owner ("follows your mouse").</summary>
    internal static string Describe(GazeMode mode) => mode switch
    {
        GazeMode.Near => "follows your mouse when it's near, and otherwise looks straight ahead",
        GazeMode.Ahead => "looks straight ahead",
        GazeMode.Window => "watches the window you're using",
        _ => "follows your mouse"
    };

    private static string Doing(GazeMode mode) => mode switch
    {
        GazeMode.Near => "Looking at your mouse when it's near",
        GazeMode.Ahead => "Looking straight ahead",
        GazeMode.Window => "Watching the window you're using",
        _ => "Looking at your mouse"
    };

    /// <summary>What the eyes do now and why, for the owner (Companion › Eyes › Where the character looks).</summary>
    internal string Looking
    {
        get
        {
            lock (gate)
            {
                var settings = SettingsLocked();
                var usual = Describe(settings.Usual) + settings.UsualFrom switch
                {
                    GazeSettings.FromOwner => ", as you chose",
                    GazeSettings.FromPersonality => ", as its personality decided",
                    _ => " (Martlet's default until its personality decides)"
                };
                var now = settings.Changed
                    ? $"The character {Describe(settings.Mode)}: it chose that at {chosenAt.ToLocalTime():t}. Usually it {usual}."
                    : $"The character {usual}.";
                if (Attending) now += $" Right now it looks at your mouse after {attendWhy}.";
                else if (Holding && holding is { } spot) now += $" Right now it looks at the {spot.Place} of your screen.";
                return now + (settings.Free ? " It may change where it looks in its replies." : " It keeps this; it can't change where it looks.");
            }
        }
    }

    /// <summary>Whether Martlet decides where the character looks while it watches your screen (Companion › Vision › Glances at
    /// your screen); off, the character keeps its usual gaze.</summary>
    internal bool Decides
    {
        get { lock (gate) return decides; }
        set
        {
            lock (gate)
            {
                if (decides == value) return;
                decides = value;
                var away = Holding;
                ForgetLocked();
                if (away) SendSpotLocked(null);
            }
        }
    }

    /// <summary>What the character looked at when the overlay last answered ("mouse", "point", "window" or "ahead"), its head
    /// and eye direction then and its usual gaze, or null before Martlet asked it to look anywhere.</summary>
    internal RendererLook? LastLook { get { lock (gate) return look; } }

    /// <summary>The overlay's last answer in words ("The character's overlay last turned toward: ahead (0, 0); usual gaze
    /// ahead."), or null before it answered.</summary>
    internal string? LastLookText => LastLook is { } last
        ? System.FormattableString.Invariant($"The character's overlay last turned toward: {last.Target} ({last.X:0.##}, {last.Y:0.##}); usual gaze {CharacterGaze.Word(last.Mode)}.")
        : null;

    /// <summary>Reads the mouse and the character's windows on the desktop for the renderer's process (replaced in tests).</summary>
    internal Func<int?, (ScreenPoint? Mouse, ScreenRect[] Overlay, ScreenRect[] Hidden)> ReadDesktop { get; set; } = Where;

    /// <summary>What the eyes are on now while Martlet watches your screen, in words (the glances' status).</summary>
    internal string Status
    {
        get
        {
            lock (gate)
            {
                var mode = SettingsLocked().Mode;
                if (!decides) return $"The character {Describe(mode)}.";
                if (!avatar.IsShowing) return "Martlet decides where the character looks once the character shows.";
                if (observedAt is not { } at || clock.GetElapsedTime(at) > Watching)
                    return $"Martlet decides where the character looks while vision watches your screen. Until then it {Describe(mode)}.";
                var now = Attending ? $"Looking at your mouse after {attendWhy}."
                    : Holding && holding is { } spot
                    ? spot.Reason == GazeReason.Thinking
                        ? $"Looking at the {spot.Place} of your screen, where Martlet chose to look."
                        : $"Glancing at something new at the {spot.Place} of your screen."
                    : Doing(mode) + verdict switch
                    {
                        GazeVerdict.Still => ": nothing new on screen.",
                        GazeVerdict.OnlyCharacter => ": only the character itself moved.",
                        GazeVerdict.Everywhere => ": much of the screen changed at once.",
                        GazeVerdict.ByMouse => ": what changed was by your mouse.",
                        GazeVerdict.Scattered => ": things changed all over, nothing stood out.",
                        GazeVerdict.TooSoon => ": it glanced away a moment ago.",
                        GazeVerdict.Seen => ": it looked at that spot lately.",
                        GazeVerdict.Glance => " again.",
                        _ => "."
                    };
                if (lastSpot is not { } last || Holding) return now;
                return now + $" Last looked away at {last.At:T}: " + (last.Spot.Reason == GazeReason.Thinking
                    ? $"the {last.Spot.Place}, where Martlet chose." : $"something new at the {last.Spot.Place}.");
            }
        }
    }

    private bool Holding => holding is not null && clock.GetTimestamp() < holdUntil;

    /// <summary>A screen glance with <paramref name="frame"/> is about to go to the Thinking model: whether it may choose where
    /// the character looks (Martlet decides and the character shows). Its look tags then point into the screen it shows.</summary>
    internal bool Offer(ScreenFrame frame)
    {
        if (frame.Area is not { IsEmpty: false } area || !avatar.IsShowing) return false;
        lock (gate)
        {
            if (!decides) return false;
            lookArea = area;
            return true;
        }
    }

    /// <summary>A new screenshot of your screen (on the UI thread): glance at what just changed in one place, or keep to the
    /// mouse. Null when Martlet doesn't decide, the character is hidden or the frame isn't of the screen.</summary>
    internal GazeDecision? Observe(ScreenFrame frame)
    {
        if (frame.Area is not { IsEmpty: false } area || !avatar.IsShowing) return null;
        lock (gate) if (!decides) return null;
        var (mouse, overlay, hidden) = ReadDesktop(avatar.RendererProcessId);
        GazeDecision decision;
        lock (gate)
        {
            if (!decides) return null;
            observedAt = clock.GetTimestamp();
            lookArea = area;
            ScreenRect[] character = [.. overlay, .. lastOverlay], shut = [.. hidden, .. lastHidden];
            (lastOverlay, lastHidden) = (overlay, hidden);
            // A look the Thinking model chose, or a touch's look at the mouse, holds until it ends.
            if (Holding && holding is { Reason: GazeReason.Thinking } || Attending) return new(GazeVerdict.TooSoon);
            decision = director.Decide(frame.Changes, area, mouse, character, shut);
            verdict = decision.Verdict;
            if (decision.Spot is { } spot)
            {
                HoldLocked(spot);
                SendSpotLocked(spot);
            }
        }
        return decision;
    }

    /// <summary>A reply wrote a look tag. A mode tag (<c>{look ahead}</c>) changes the usual gaze while the character may change
    /// where it looks, until a reply changes it again (<c>{look usual}</c> goes back); returns null. A screen glance's tag for a
    /// ninth of its picture looks at that part of the screen for a while; returns where.</summary>
    internal GazeSpot? Chosen(string tag)
    {
        if (CharacterGaze.TryMode(tag, out var mode))
        {
            Choose(mode);
            return null;
        }
        GazeSpot? spot;
        lock (gate)
        {
            if (!decides || lookArea is not { } area || CharacterGaze.Chosen(tag, area) is not { } chosenSpot) return null;
            spot = chosenSpot;
            HoldLocked(chosenSpot);
            SendSpotLocked(chosenSpot);
        }
        ErrorLog.Info($"The Thinking model turned the character's eyes to the {spot.Place} of the screen.");
        return spot;
    }

    private void Choose(GazeMode? mode, string? why = null)
    {
        GazeSettings before, after;
        lock (gate)
        {
            if (!free) return;
            before = SettingsLocked();
            chosen = mode is { } picked && picked != before.Usual ? picked : null;
            chosenAt = clock.GetUtcNow();
            after = SettingsLocked();
        }
        ErrorLog.Info(after.Changed
            ? $"The Thinking model chose the character's gaze: {CharacterGaze.Word(after.Mode)} (usually {CharacterGaze.Word(after.Usual)})."
            : $"{why ?? "The Thinking model"} took the character's eyes back to their usual gaze ({CharacterGaze.Word(after.Usual)}).");
        if (after.Mode != before.Mode) Refresh();
        Changed?.Invoke();
    }

    /// <summary>When a reply's choice began to hold the eyes, or null while they do their usual.</summary>
    internal DateTimeOffset? ChosenSince
    {
        get
        {
            lock (gate) return SettingsLocked().Changed ? chosenAt : null;
        }
    }

    /// <summary>Takes the eyes back to their usual gaze because of <paramref name="why"/> ("A check-in"), as a reply's
    /// <c>{look usual}</c> does. Returns whether a reply's choice held them.</summary>
    internal bool BackToUsual(string why)
    {
        if (ChosenSince is null) return false;
        Choose(null, why);
        return true;
    }

    /// <summary>Vision stopped: the eyes go back to their usual gaze and what was seen is forgotten.</summary>
    internal void Stop()
    {
        lock (gate)
        {
            var away = Holding;
            ForgetLocked();
            if (away) SendSpotLocked(null);
        }
    }

    private void HoldLocked(GazeSpot spot)
    {
        holding = spot;
        holdUntil = clock.GetTimestamp() + (long)(spot.Hold.TotalSeconds * clock.TimestampFrequency);
        lastSpot = (spot, DateTime.Now);
    }

    private void ForgetLocked()
    {
        director.Reset();
        lookArea = null;
        observedAt = null;
        lastOverlay = lastHidden = [];
        holding = null;
        holdUntil = 0;
        verdict = null;
    }

    private void SendSpotLocked(GazeSpot? spot) =>
        SendLocked(spot is null ? new RendererGaze() : new RendererGaze(spot.X, spot.Y, spot.Hold.TotalSeconds));

    // With the gate held, where the state the gaze carries was decided: gazes reach the overlay in the order they were decided,
    // one after another.
    private void SendLocked(RendererGaze gaze) =>
        sending = sending.ContinueWith(_ => SendAsync(gaze), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    private async Task SendAsync(RendererGaze gaze)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var reply = await avatar.GazeAsync(gaze, timeout.Token).ConfigureAwait(false);
            if (reply is null) return;
            lock (gate) look = reply;
            Changed?.Invoke();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or
            InvalidDataException or TimeoutException or ObjectDisposedException or JsonException)
        {
            // A usual gaze the overlay didn't take goes again with the next change.
            if (gaze.Mode is not null) lock (gate) told = default;
        }
    }

    /// <summary>The mouse and the character's windows on the desktop in physical pixels, like the screenshots: the largest of
    /// the renderer's visible windows is its overlay, the others its speech bubble and menus.</summary>
    private static (ScreenPoint? Mouse, ScreenRect[] Overlay, ScreenRect[] Hidden) Where(int? process)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            ScreenPoint? mouse = GetCursorPos(out var point) ? new(point.X, point.Y) : null;
            if (process is not int id) return (mouse, [], []);
            var windows = new List<ScreenRect>();
            EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window) && GetWindowThreadProcessId(window, out var owner) != 0 && owner == (uint)id &&
                    GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
                    windows.Add(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
                return windows.Count < 32;
            }, 0);
            var overlay = windows.OrderByDescending(w => (long)w.Width * w.Height).Take(1).ToArray();
            return (mouse, overlay, [.. windows.Where(w => !overlay.Contains(w))]);
        }
        finally
        {
            if (old != 0) SetThreadDpiAwarenessContext(old);
        }
    }

    private static readonly nint PerMonitorAwareV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    private delegate bool WindowCallback(nint window, nint data);

    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out WindowRect rect);
}
