using System.Text.Json.Serialization;
using Martlet.Core.Settings;

namespace Martlet.Avatar.Hosting;

/// <summary>What the character's eyes do when nothing else draws them (a glance, a look the Thinking model chose, a touch):
/// follow the mouse pointer (<see cref="Mouse"/>), follow it only while it is near the character and else look straight ahead
/// (<see cref="Near"/>), look straight ahead (<see cref="Ahead"/>) or watch the window the user is using (<see cref="Window"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GazeMode>))]
public enum GazeMode
{
    [JsonStringEnumMemberName("mouse")] Mouse,
    [JsonStringEnumMemberName("near")] Near,
    [JsonStringEnumMemberName("ahead")] Ahead,
    [JsonStringEnumMemberName("window")] Window
}

/// <summary>Where the character's eyes go when nothing else draws them, and why: the owner's choice (<see cref="Owner"/>; null
/// leaves it to the personality), the active persona's temperament (<see cref="Personality"/>; null before it is decided), else
/// the mouse. While the character may change it (<see cref="Free"/>), the Thinking model's choice in a reply
/// (<see cref="Chosen"/>) wins until it changes it again.</summary>
public sealed record GazeSettings(GazeMode? Owner = null, GazeMode? Personality = null, bool Free = true, GazeMode? Chosen = null)
{
    public const string FromOwner = "owner", FromPersonality = "personality", FromDefault = "default";

    /// <summary>What the eyes usually do: the owner's choice, else the personality's, else follow the mouse.</summary>
    public GazeMode Usual => Owner ?? Personality ?? GazeMode.Mouse;

    /// <summary>What the eyes do now: the Thinking model's choice while it may make one, else the usual.</summary>
    public GazeMode Mode => Free && Chosen is { } chosen ? chosen : Usual;

    /// <summary>Who set the usual: <see cref="FromOwner"/>, <see cref="FromPersonality"/> or <see cref="FromDefault"/>.</summary>
    public string UsualFrom => Owner is not null ? FromOwner : Personality is not null ? FromPersonality : FromDefault;

    /// <summary>Whether the Thinking model's choice holds the eyes now (it differs from the usual).</summary>
    public bool Changed => Free && Chosen is { } chosen && chosen != Usual;
}

/// <summary>A rectangle on the desktop in physical screen pixels: the coordinates a screen capture, the mouse and window
/// bounds have for per-monitor DPI-aware code.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Intersects(double left, double top, double right, double bottom) =>
        !IsEmpty && left < Right && right > Left && top < Bottom && bottom > Top;

    public bool Contains(ScreenPoint point) => !IsEmpty && point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    public ScreenPoint Middle => new(Left + Width / 2.0, Top + Height / 2.0);
}

/// <summary>A point on the desktop in physical screen pixels.</summary>
public readonly record struct ScreenPoint(double X, double Y);

/// <summary>Why the character looks somewhere other than the mouse: something just changed there on screen, or the
/// Thinking model chose it while taking a look at the screen.</summary>
public enum GazeReason { Change, Thinking }

/// <summary>Somewhere on the desktop for the character to look at instead of the mouse: the point (physical screen pixels),
/// which ninth of the picture it is in (<c>top right</c>), why, and how long before its eyes go back to the mouse.</summary>
public sealed record GazeSpot(double X, double Y, string Place, GazeReason Reason, TimeSpan Hold);

/// <summary>What a new screenshot did to the character's eyes: a <see cref="Glance"/> at one change, or why they stay on
/// the mouse.</summary>
public enum GazeVerdict
{
    /// <summary>Something changed in one place: the character glances at it.</summary>
    Glance,
    /// <summary>Nothing changed since the screenshot before.</summary>
    Still,
    /// <summary>Only the character itself (or its speech bubble) moved.</summary>
    OnlyCharacter,
    /// <summary>Much of the picture changed: a new scene, scrolling, another window in front.</summary>
    Everywhere,
    /// <summary>The change was right by the mouse, where the eyes already are.</summary>
    ByMouse,
    /// <summary>Things changed in several places and none stood out.</summary>
    Scattered,
    /// <summary>The character glanced away only a moment ago.</summary>
    TooSoon,
    /// <summary>It already looked at that spot lately (a video or animation that keeps changing).</summary>
    Seen,
    /// <summary>No earlier screenshot of the same picture to compare with (the first one, or another window).</summary>
    NoPicture
}

public sealed record GazeDecision(GazeVerdict Verdict, GazeSpot? Spot = null);

/// <summary>
/// Where the desktop character looks. Its usual gaze (<see cref="GazeMode"/>: Companion › Eyes › Where the character looks,
/// the overlay's Eyes menu, or the persona's temperament) is what the eyes do when nothing else draws them; a reply may change
/// it with a mode tag (<c>{look ahead}</c>) while the character may change where it looks. While Martlet watches the screen and
/// decides (Companion › Vision › Glances at your screen), the eyes also glance at something that just changed in one place on
/// screen (<see cref="GazeDirector"/>) or at the ninth of the picture a screen glance's Thinking model names with a look tag
/// (<c>{look top right}</c>). Screenshots are compared as coarse grey grids (<see cref="Grid"/>: <see cref="Columns"/> ×
/// <see cref="Rows"/> averages, far too small to carry content) on this PC; only where to look reaches the character.
/// </summary>
public static class CharacterGaze
{
    public const int Columns = 32, Rows = 18;
    /// <summary>A grid cell whose average grey moved at least this much (of 255) since the screenshot before changed.</summary>
    public const int ChangeThreshold = 16;
    /// <summary>Under the character's overlay a cell must change this much: the character's own breathing, blinking and head
    /// turns stay below it, while something appearing behind it (a notification where it stands) doesn't.</summary>
    public const int CharacterChangeThreshold = 56;
    /// <summary>How far around the character's frame the mouse counts as near it (<see cref="GazeMode.Near"/>), as a fraction of
    /// the frame's width.</summary>
    public const double NearMargin = 0.5;
    /// <summary>The longest a touch turns the character's eyes to the mouse pointer.</summary>
    public const double MaximumAttention = 15;
    /// <summary>How long a glance at a change holds the eyes before they go back to the mouse.</summary>
    public static TimeSpan GlanceHold { get; } = TimeSpan.FromSeconds(2.5);
    /// <summary>How long a look the Thinking model chose holds the eyes.</summary>
    public static TimeSpan ChosenHold { get; } = TimeSpan.FromSeconds(6);
    /// <summary>The least time between two glances at changes.</summary>
    public static TimeSpan GlanceGap { get; } = TimeSpan.FromSeconds(6);

    /// <summary>Each gaze: its word (in its tag, <c>{look near}</c>, and in a temperament), what the eyes do (for the Thinking
    /// model, after "your eyes") and its label (for the owner).</summary>
    public static IReadOnlyList<(GazeMode Mode, string Word, string Does, string Label)> Modes { get; } =
    [
        (GazeMode.Mouse, "mouse", "follow the user's mouse pointer wherever it goes", "Follow your mouse"),
        (GazeMode.Near, "near", "look at the user's mouse pointer only while it is near you, and otherwise straight ahead",
            "Follow your mouse when it's near"),
        (GazeMode.Ahead, "ahead", "look straight ahead and ignore the pointer", "Look straight ahead"),
        (GazeMode.Window, "window", "watch the window the user is working in", "Watch the window you're using")
    ];

    /// <summary>The tag that takes the eyes back to their usual gaze.</summary>
    public const string UsualTag = "{look usual}";

    /// <summary>The tags a reply may write to change where the character looks, in order: <c>{look mouse}</c>,
    /// <c>{look near}</c>, <c>{look ahead}</c>, <c>{look window}</c> and <see cref="UsualTag"/>.</summary>
    public static IReadOnlyList<string> ModeTags { get; } = [.. Modes.Select(m => "{look " + m.Word + "}"), UsualTag];

    public static string Word(GazeMode mode) => Modes.First(m => m.Mode == mode).Word;
    public static string Does(GazeMode mode) => Modes.First(m => m.Mode == mode).Does;
    public static string Label(GazeMode mode) => Modes.First(m => m.Mode == mode).Label;

    /// <summary>The gaze a word names (its own word, its label or a common other word for it, any case), or null.</summary>
    public static GazeMode? ModeOf(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        var slug = string.Join("_", word.Trim().Trim('{', '}').ToLowerInvariant().Replace("'", "").Replace("\u2019", "")
            .Split([' ', '-', '/', '.', ','], StringSplitOptions.RemoveEmptyEntries));
        foreach (var mode in Modes)
            if (slug == mode.Word || slug == string.Join("_", mode.Label.ToLowerInvariant().Replace("'", "").Split(' '))) return mode.Mode;
        return slug switch
        {
            "cursor" or "pointer" or "mouse_pointer" or "follow" or "follow_mouse" or "follows_mouse" or "user" => GazeMode.Mouse,
            "nearby" or "close" or "near_mouse" or "mouse_near" or "when_near" => GazeMode.Near,
            "straight" or "straight_ahead" or "forward" or "front" or "neutral" or "center" or "none" or "ignore" => GazeMode.Ahead,
            "active_window" or "screen" or "work" or "app" or "program" or "watch_window" => GazeMode.Window,
            _ => null
        };
    }

    public static bool IsModeTag(string tag) => ModeTags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="tag"/> is any look tag: a mode tag (<see cref="ModeTags"/>) or a ninth of the screen
    /// (<see cref="Tags"/>).</summary>
    public static bool IsLookTag(string tag) => IsTag(tag) || IsModeTag(tag);

    /// <summary>The gaze a mode tag sets: true with the mode (null for <see cref="UsualTag"/>), false for any other tag.</summary>
    public static bool TryMode(string tag, out GazeMode? mode)
    {
        mode = null;
        if (string.Equals(tag, UsualTag, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var known in Modes)
            if (string.Equals(tag, "{look " + known.Word + "}", StringComparison.OrdinalIgnoreCase))
            {
                mode = known.Mode;
                return true;
            }
        return false;
    }

    /// <summary>What every reply is told while the character may change where it looks (Companion › Prompts › Where you look):
    /// its usual gaze and the mode tags, or null when the owner emptied that prompt. It changes only with the usual gaze, so the
    /// request still starts the same from message to message.</summary>
    public static CharacterActionPrompt? ReplyPrompt(PromptSettings? prompts, GazeMode usual) =>
        PromptSettings.Fill(prompts, PromptCatalog.CharacterGaze, ("usual", Does(usual)),
            ("tags", string.Join("\n", Modes.Select(m => $"{{look {m.Word}}} - {m.Does}").Append($"{UsualTag} - go back to your usual gaze")))) is { } text
            ? new(text, ModeTags) : null;

    /// <summary>The note for the newest message while the Thinking model's choice holds the eyes (Companion › Prompts › Where you
    /// look now): what they do and since when. Null when they do their usual (nothing to say) or the owner emptied the prompt.</summary>
    public static string? Note(PromptSettings? prompts, GazeSettings settings, TimeSpan? since = null) =>
        settings.Changed ? PromptSettings.Fill(prompts, PromptCatalog.CharacterLooking, ("looking", Does(settings.Mode)),
            ("since", since is { } age ? Age(age) + " ago" : "earlier")) : null;

    private static string Age(TimeSpan age) => age.TotalSeconds < 60 ? "a moment"
        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min" : $"{(int)age.TotalHours} h";

    /// <summary>The character's emote and motion prompt (<paramref name="actions"/>, null when it offers none) with its gaze
    /// joined in: both instructions, the emote tags then the look tags, the emotes' showing note and <paramref name="looking"/>
    /// (<see cref="Note"/>). The gaze stays out when its tags don't fit beside the emote tags (at most
    /// <see cref="CharacterActionCatalog.MaximumTags"/>).</summary>
    public static CharacterActionPrompt? Join(CharacterActionPrompt? actions, CharacterActionPrompt? gaze, string? looking)
    {
        if (gaze is null || (actions?.Tags.Count ?? 0) + gaze.Tags.Count > CharacterActionCatalog.MaximumTags) return actions;
        return actions is null ? gaze with { Looking = looking }
            : new(actions.Instructions + "\n\n" + gaze.Instructions, [.. actions.Tags, .. gaze.Tags], actions.Showing, looking);
    }

    /// <summary>Where the eyes turn this moment and toward what: "point" (a point Martlet asked for, while it holds), "mouse"
    /// (while a touch holds the eyes on it, or by the gaze: always for <see cref="GazeMode.Mouse"/>, only while it is near the
    /// character's <paramref name="frame"/> for <see cref="GazeMode.Near"/>), "window" (for <see cref="GazeMode.Window"/>: where
    /// the user last worked in the window they are using, <paramref name="working"/> from <see cref="WindowWatch"/>, while that
    /// is in the window; else its middle) or "ahead" (straight ahead: no point). A "mouse" target with no point means the mouse
    /// can't be read now (the eyes stay where they are).</summary>
    public static (string Target, ScreenPoint? At) Aim(GazeMode mode, ScreenPoint? point, bool attending, ScreenPoint? mouse,
        ScreenRect frame, ScreenRect? window, ScreenPoint? working = null)
    {
        if (point is { } held) return ("point", held);
        if (attending) return ("mouse", mouse);
        return mode switch
        {
            GazeMode.Mouse => ("mouse", mouse),
            GazeMode.Near when mouse is { } at && IsNear(frame, at) => ("mouse", at),
            GazeMode.Window when Watched(window, working) is { } spot => ("window", spot),
            _ => ("ahead", null)
        };
    }

    /// <summary>Where the eyes go in the window the user is using: <paramref name="working"/> (where they last pointed or typed
    /// in it) while it is in the window, else the window's middle; null without a window.</summary>
    public static ScreenPoint? Watched(ScreenRect? window, ScreenPoint? working) =>
        window is not { IsEmpty: false } used ? null : working is { } at && used.Contains(at) ? at : used.Middle;

    /// <summary>Whether the mouse is near the character: over its frame or within <see cref="NearMargin"/> of the frame's width
    /// around it.</summary>
    public static bool IsNear(ScreenRect frame, ScreenPoint mouse)
    {
        if (frame.IsEmpty) return false;
        var margin = frame.Width * NearMargin;
        return mouse.X >= frame.Left - margin && mouse.X <= frame.Right + margin && mouse.Y >= frame.Top - margin && mouse.Y <= frame.Bottom + margin;
    }

    private static readonly (string Place, string Where, double X, double Y)[] Ninths =
    [
        ("top left", "the top-left corner of the picture", 1 / 6.0, 1 / 6.0),
        ("top", "the top middle", 0.5, 1 / 6.0),
        ("top right", "the top-right corner", 5 / 6.0, 1 / 6.0),
        ("left", "the left side, halfway down", 1 / 6.0, 0.5),
        ("center", "the middle", 0.5, 0.5),
        ("right", "the right side, halfway down", 5 / 6.0, 0.5),
        ("bottom left", "the bottom-left corner", 1 / 6.0, 5 / 6.0),
        ("bottom", "the bottom middle", 0.5, 5 / 6.0),
        ("bottom right", "the bottom-right corner", 5 / 6.0, 5 / 6.0)
    ];

    /// <summary>The look tags a screen glance may start with, in reading order: <c>{look top left}</c> to
    /// <c>{look bottom right}</c>. Their spaces keep them apart from emote tags, which never have any.</summary>
    public static IReadOnlyList<string> Tags { get; } = Ninths.Select(n => "{look " + n.Place + "}").ToArray();

    public static bool IsTag(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>What a screen glance is told about the look tags (Companion › Prompts › Where the character looks) and the
    /// tags themselves, or null when the owner emptied that prompt.</summary>
    public static CharacterActionPrompt? Prompt(PromptSettings? prompts, string silent) =>
        PromptSettings.Fill(prompts, PromptCatalog.GlanceLook,
            ("tags", string.Join("\n", Ninths.Select(n => $"{{look {n.Place}}} - {n.Where}"))), ("silent", silent)) is { } text
            ? new(text, Tags) : null;

    /// <summary>Where a look tag points on the screen the glance's picture showed (the middle of that ninth of it), or null
    /// for anything else.</summary>
    public static GazeSpot? Chosen(string tag, ScreenRect area)
    {
        if (area.IsEmpty) return null;
        foreach (var ninth in Ninths)
            if (string.Equals(tag, "{look " + ninth.Place + "}", StringComparison.OrdinalIgnoreCase))
                return new(area.Left + area.Width * ninth.X, area.Top + area.Height * ninth.Y, ninth.Place, GazeReason.Thinking, ChosenHold);
        return null;
    }

    /// <summary>Which ninth of <paramref name="area"/> a point is in, in words (<c>top right</c>, <c>center</c>).</summary>
    public static string Place(ScreenRect area, double x, double y)
    {
        var column = Math.Clamp((int)Math.Floor((x - area.Left) * 3 / Math.Max(1, area.Width)), 0, 2);
        var row = Math.Clamp((int)Math.Floor((y - area.Top) * 3 / Math.Max(1, area.Height)), 0, 2);
        return Ninths[row * 3 + column].Place;
    }

    /// <summary>A screenshot (BGRA32, top-down) as <see cref="Columns"/> × <see cref="Rows"/> average greys, row by row.</summary>
    public static byte[] Grid(ReadOnlySpan<byte> pixels, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (pixels.Length < width * height * 4) throw new ArgumentException("The picture is smaller than its size says.", nameof(pixels));
        var result = new byte[Columns * Rows];
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
        {
            int x0 = column * width / Columns, x1 = Math.Max(x0 + 1, (column + 1) * width / Columns);
            int y0 = row * height / Rows, y1 = Math.Max(y0 + 1, (row + 1) * height / Rows);
            long sum = 0, count = 0;
            for (int y = y0; y < y1 && y < height; y += 2)
            for (int x = x0; x < x1 && x < width; x += 2)
            {
                var i = (y * width + x) * 4;
                sum += (pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8;
                count++;
            }
            result[row * Columns + column] = (byte)(count == 0 ? 0 : sum / count);
        }
        return result;
    }

    /// <summary>How much each cell changed between two grids of the same picture, or null without an earlier one.</summary>
    public static byte[]? Changes(byte[]? previous, byte[] current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null || previous.Length != current.Length) return null;
        var changes = new byte[current.Length];
        for (var i = 0; i < current.Length; i++) changes[i] = (byte)Math.Abs(current[i] - previous[i]);
        return changes;
    }
}

/// <summary>
/// What the character watches in the window the user is using (<see cref="GazeMode.Window"/>): where the user works in it, the
/// way someone watching over their shoulder follows what they do. That is where they last pointed (the mouse moving over that
/// window, not over the character or another window) or typed (its text cursor moving, in programs that show one). Just after
/// they switch to another window it is that window's text cursor, or the mouse when it is over the window (the click that
/// switched), and otherwise nothing yet, so the eyes go to the window's middle (<see cref="CharacterGaze.Watched"/>).
/// </summary>
public sealed class WindowWatch
{
    public const string Pointer = "pointer", TextCursor = "text cursor", Middle = "middle";
    private nint window;
    private ScreenPoint? mouse, caret;

    /// <summary>Where the user last worked in the window they use, or null (nothing yet: its middle).</summary>
    public ScreenPoint? Working { get; private set; }

    /// <summary>What <see cref="Working"/> is: <see cref="Pointer"/> or <see cref="TextCursor"/>; null with nothing yet.</summary>
    public string? From { get; private set; }

    /// <summary>One look at the desktop (the overlay's, every 50 ms): the window the user is using (0 for none), the mouse pointer
    /// (null when it can't be read) and whether it is over that window, and that window's text cursor (null when it shows none,
    /// or the window isn't in front). Returns <see cref="Working"/>.</summary>
    public ScreenPoint? Update(nint used, ScreenPoint? pointer, bool over, ScreenPoint? textCursor)
    {
        if (used != window)
        {
            window = used;
            caret = null;
            Working = null;
            From = null;
            if (textCursor is { } typing) (Working, From) = (typing, TextCursor);
            else if (over && pointer is { } at) (Working, From) = (at, Pointer);
        }
        else if (textCursor is { } typing && typing != caret) (Working, From) = (typing, TextCursor);
        else if (over && pointer is { } at && at != mouse) (Working, From) = (at, Pointer);
        // A pointer or text cursor that can't be read for a moment (the character's menu in front) isn't a move when it is back.
        if (pointer is not null) mouse = pointer;
        if (textCursor is not null) caret = textCursor;
        return Working;
    }

    /// <summary>What the eyes watch in <paramref name="window"/> now: <see cref="Pointer"/>, <see cref="TextCursor"/> or
    /// <see cref="Middle"/>; null without a window.</summary>
    public string? Watching(ScreenRect? window) =>
        window is not { IsEmpty: false } used ? null : Working is { } at && used.Contains(at) ? From : Middle;
}

/// <summary>
/// Decides, after each new screenshot, whether something on screen catches the character's eye, the way a person's eyes go
/// to something that suddenly appears or moves: a notification popping up, a new chat line, a window opening. Otherwise they
/// stay on the mouse. It glances at a change only when it is in one place: not much of the picture at once (a new scene,
/// scrolling, another window in front), not changes all over (an animated page), not right by the mouse (the eyes are already
/// there) and not the character itself: under its overlay only a strong change counts
/// (<see cref="CharacterGaze.CharacterChangeThreshold"/>), and its speech bubble and menus never do. It glances at most every
/// <see cref="CharacterGaze.GlanceGap"/>, and tires of a spot that keeps changing (a video): looking there again waits 20
/// seconds, then 40 and so on, up to two minutes.
/// </summary>
public sealed class GazeDirector(TimeProvider? clock = null)
{
    private const double MostOfThePicture = 0.2, OneThing = 0.5, MouseCells = 2.5, SameSpotCells = 2.5;
    private static readonly TimeSpan Boredom = TimeSpan.FromSeconds(20), MostBored = TimeSpan.FromMinutes(2),
        Forget = TimeSpan.FromMinutes(3);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly List<(double X, double Y, long At, int Times)> seen = [];
    private long? lastGlance;

    /// <param name="changes">How much each cell changed (<see cref="CharacterGaze.Changes"/>); null for no comparison.</param>
    /// <param name="area">Where on the desktop the screenshot was taken.</param>
    /// <param name="mouse">The mouse pointer, when known.</param>
    /// <param name="character">The character's overlay: only a strong change under it counts.</param>
    /// <param name="hidden">The character's other windows (its speech bubble and menus, shown now or a moment ago): changes
    /// there never count.</param>
    public GazeDecision Decide(byte[]? changes, ScreenRect area, ScreenPoint? mouse, IReadOnlyList<ScreenRect> character,
        IReadOnlyList<ScreenRect>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        hidden ??= [];
        if (changes is null || changes.Length != CharacterGaze.Columns * CharacterGaze.Rows || area.IsEmpty) return new(GazeVerdict.NoPicture);
        double cellWidth = area.Width / (double)CharacterGaze.Columns, cellHeight = area.Height / (double)CharacterGaze.Rows;
        var cell = Math.Max(cellWidth, cellHeight);
        (double X, double Y) Center(int i) =>
            (area.Left + (i % CharacterGaze.Columns + 0.5) * cellWidth, area.Top + (i / CharacterGaze.Columns + 0.5) * cellHeight);

        var considered = 0;
        var characterMoved = false;
        var changed = new List<int>();
        for (var i = 0; i < changes.Length; i++)
        {
            double left = area.Left + i % CharacterGaze.Columns * cellWidth, top = area.Top + i / CharacterGaze.Columns * cellHeight;
            // A cell a window touches, with half a cell around it for the downscaled edge.
            bool Touches(ScreenRect window) =>
                window.Intersects(left - cellWidth / 2, top - cellHeight / 2, left + cellWidth * 1.5, top + cellHeight * 1.5);
            if (hidden.Any(Touches))
            {
                characterMoved |= changes[i] >= CharacterGaze.ChangeThreshold;
                continue;
            }
            considered++;
            var under = character.Any(Touches);
            if (changes[i] >= (under ? CharacterGaze.CharacterChangeThreshold : CharacterGaze.ChangeThreshold)) changed.Add(i);
            else if (under && changes[i] >= CharacterGaze.ChangeThreshold) characterMoved = true;
        }
        if (changed.Count == 0) return new(characterMoved ? GazeVerdict.OnlyCharacter : GazeVerdict.Still);
        if (changed.Count > considered * MostOfThePicture) return new(GazeVerdict.Everywhere);
        var candidates = changed.Where(i => mouse is not { } at || Distance(Center(i), (at.X, at.Y)) > cell * MouseCells).ToHashSet();
        if (candidates.Count == 0) return new(GazeVerdict.ByMouse);

        // The biggest change and the changed cells touching it.
        var start = candidates.MaxBy(i => changes[i]);
        var cluster = new HashSet<int> { start };
        var queue = new Queue<int>([start]);
        while (queue.TryDequeue(out var at))
        {
            int x = at % CharacterGaze.Columns, y = at / CharacterGaze.Columns;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= CharacterGaze.Columns || ny >= CharacterGaze.Rows) continue;
                var next = ny * CharacterGaze.Columns + nx;
                if (candidates.Contains(next) && cluster.Add(next)) queue.Enqueue(next);
            }
        }
        double total = candidates.Sum(i => (double)changes[i]), weight = cluster.Sum(i => (double)changes[i]);
        if (weight < total * OneThing) return new(GazeVerdict.Scattered);
        double spotX = cluster.Sum(i => Center(i).X * changes[i]) / weight, spotY = cluster.Sum(i => Center(i).Y * changes[i]) / weight;

        var now = clock.GetTimestamp();
        if (lastGlance is { } last && clock.GetElapsedTime(last, now) < CharacterGaze.GlanceGap) return new(GazeVerdict.TooSoon);
        seen.RemoveAll(s => clock.GetElapsedTime(s.At, now) >= Forget);
        var known = seen.FindIndex(s => Distance((s.X, s.Y), (spotX, spotY)) <= cell * SameSpotCells);
        if (known >= 0)
        {
            var (_, _, at, times) = seen[known];
            var wait = TimeSpan.FromTicks(Math.Min(Boredom.Ticks * times, MostBored.Ticks));
            if (clock.GetElapsedTime(at, now) < wait) return new(GazeVerdict.Seen);
            seen[known] = (spotX, spotY, now, times + 1);
        }
        else seen.Add((spotX, spotY, now, 1));
        lastGlance = now;
        return new(GazeVerdict.Glance, new(spotX, spotY, CharacterGaze.Place(area, spotX, spotY), GazeReason.Change, CharacterGaze.GlanceHold));
    }

    /// <summary>Forgets what it glanced at and when (vision stopped).</summary>
    public void Reset()
    {
        seen.Clear();
        lastGlance = null;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
