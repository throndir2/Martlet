using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>
/// Where the showing character looks. Its head and eyes follow the mouse on their own (the overlay does that). With Companion ›
/// Vision › Where the character looks set to Martlet decides, each new screenshot Martlet takes of your screen (every 3
/// seconds while vision watches your active window or whole screen) decides it again: something that just appeared or moved in
/// one place draws a short glance (<see cref="GazeDirector"/>), and a screen glance's Thinking model may start its answer with
/// a look tag (<c>{look top right}</c>) to look at that part of the picture for a few seconds; otherwise the mouse. Screenshots
/// are compared on this PC as coarse grey grids, and only a point on the desktop reaches the character.
/// </summary>
internal sealed class CharacterGazeService
{
    // How recent the last screenshot must be for Martlet to count as watching your screen.
    private static readonly TimeSpan Watching = TimeSpan.FromSeconds(10);
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

    internal CharacterGazeService(AvatarController avatar, TimeProvider? clock = null)
    {
        this.avatar = avatar;
        this.clock = clock ?? TimeProvider.System;
        director = new(this.clock);
    }

    /// <summary>Whether Martlet decides where the character looks (Companion › Vision › Where the character looks); off, the
    /// character follows the mouse.</summary>
    internal bool Decides
    {
        get { lock (gate) return decides; }
        set
        {
            bool away;
            lock (gate)
            {
                if (decides == value) return;
                decides = value;
                away = Holding;
                ForgetLocked();
            }
            if (away) Send(null);
        }
    }

    /// <summary>What the character looked at when the overlay last answered ("mouse" or "point") and its head and eye
    /// direction then, or null before Martlet asked it to look anywhere.</summary>
    internal RendererLook? LastLook { get { lock (gate) return look; } }

    /// <summary>Reads the mouse and the character's windows on the desktop for the renderer's process (replaced in tests).</summary>
    internal Func<int?, (ScreenPoint? Mouse, ScreenRect[] Overlay, ScreenRect[] Hidden)> ReadDesktop { get; set; } = Where;

    /// <summary>What the eyes are on now, in words.</summary>
    internal string Status
    {
        get
        {
            lock (gate)
            {
                if (!decides) return "The character follows your mouse.";
                if (!avatar.IsShowing) return "Martlet decides where the character looks once the character shows.";
                if (observedAt is not { } at || clock.GetElapsedTime(at) > Watching)
                    return "Martlet decides where the character looks while vision watches your screen. Until then it follows your mouse.";
                var now = Holding && holding is { } spot
                    ? spot.Reason == GazeReason.Thinking
                        ? $"Looking at the {spot.Place} of your screen, where Martlet chose to look."
                        : $"Glancing at something new at the {spot.Place} of your screen."
                    : "Looking at your mouse" + verdict switch
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
            // A look the Thinking model chose holds until it ends.
            if (Holding && holding is { Reason: GazeReason.Thinking }) return new(GazeVerdict.TooSoon);
            decision = director.Decide(frame.Changes, area, mouse, character, shut);
            verdict = decision.Verdict;
            if (decision.Spot is { } spot) HoldLocked(spot);
        }
        if (decision.Spot is { } glance) Send(glance);
        return decision;
    }

    /// <summary>A screen glance's Thinking model wrote a look tag: look at that part of the screen its picture showed.</summary>
    internal GazeSpot? Chosen(string tag)
    {
        GazeSpot? spot;
        lock (gate)
        {
            if (!decides || lookArea is not { } area || CharacterGaze.Chosen(tag, area) is not { } chosen) return null;
            spot = chosen;
            HoldLocked(chosen);
        }
        ErrorLog.Info($"The Thinking model turned the character's eyes to the {spot.Place} of the screen.");
        Send(spot);
        return spot;
    }

    /// <summary>Vision stopped: the eyes go back to the mouse and what was seen is forgotten.</summary>
    internal void Stop()
    {
        bool away;
        lock (gate)
        {
            away = Holding;
            ForgetLocked();
        }
        if (away) Send(null);
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

    private void Send(GazeSpot? spot) => SendAsync(spot is null ? new RendererGaze()
        : new RendererGaze(spot.X, spot.Y, spot.Hold.TotalSeconds)).Forget();

    private async Task SendAsync(RendererGaze gaze)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var reply = await avatar.GazeAsync(gaze, timeout.Token).ConfigureAwait(false);
            if (reply is null) return;
            lock (gate) look = reply;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or
            InvalidDataException or TimeoutException or ObjectDisposedException or JsonException) { }
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
