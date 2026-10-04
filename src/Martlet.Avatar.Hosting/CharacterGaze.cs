using Martlet.Core.Settings;

namespace Martlet.Avatar.Hosting;

/// <summary>A rectangle on the desktop in physical screen pixels: the coordinates a screen capture, the mouse and window
/// bounds have for per-monitor DPI-aware code.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Intersects(double left, double top, double right, double bottom) =>
        !IsEmpty && left < Right && right > Left && top < Bottom && bottom > Top;
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
/// Where the desktop character looks while Martlet watches the screen and decides (Companion › Vision › Where the character
/// looks): at the mouse by default, at something that just changed in one place on screen (<see cref="GazeDirector"/>), or at
/// the ninth of the picture a screen glance's Thinking model names with a look tag (<c>{look top right}</c>). Screenshots are
/// compared as coarse grey grids (<see cref="Grid"/>: <see cref="Columns"/> × <see cref="Rows"/> averages, far too small to
/// carry content) on this PC; only where to look reaches the character.
/// </summary>
public static class CharacterGaze
{
    public const int Columns = 32, Rows = 18;
    /// <summary>A grid cell whose average grey moved at least this much (of 255) since the screenshot before changed.</summary>
    public const int ChangeThreshold = 16;
    /// <summary>Under the character's overlay a cell must change this much: the character's own breathing, blinking and head
    /// turns stay below it, while something appearing behind it (a notification where it stands) doesn't.</summary>
    public const int CharacterChangeThreshold = 56;
    /// <summary>How long a glance at a change holds the eyes before they go back to the mouse.</summary>
    public static TimeSpan GlanceHold { get; } = TimeSpan.FromSeconds(2.5);
    /// <summary>How long a look the Thinking model chose holds the eyes.</summary>
    public static TimeSpan ChosenHold { get; } = TimeSpan.FromSeconds(6);
    /// <summary>The least time between two glances at changes.</summary>
    public static TimeSpan GlanceGap { get; } = TimeSpan.FromSeconds(6);

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
