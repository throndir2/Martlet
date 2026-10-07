using System.Text.Json.Serialization;

namespace Martlet.Avatar.Hosting;

/// <summary>
/// Something the user did to the character overlay, sent unprompted on the renderer's request pipe as a "physical" message once
/// it has settled (a drag is one "moved", a spin of the wheel one "zoomed"). <paramref name="Kind"/>: "moved" (dragged, nudged
/// with the arrow keys or moved through UI Automation), "home" (sent back to its default spot), "zoomed" (in or out, the
/// overlay growing or the view zooming), "zoom_reset" or "panned" (the zoomed view moved over the character).
/// <paramref name="Dx"/> and <paramref name="Dy"/> are how far it moved (device-independent pixels, +y down) and
/// <paramref name="ScreenWidth"/> the width of the screen it ended on; <paramref name="FromScreen"/> and <paramref name="ToScreen"/>
/// the monitors it was on before and after (device names such as DISPLAY2). <paramref name="ZoomFrom"/> and
/// <paramref name="ZoomTo"/> are the character's size on screen before and after (its frame width times the view's zoom).
/// <paramref name="Focus"/> is the page's hit test of what the zoom closed in on or the panned view centers on (null: nothing).
/// </summary>
public sealed record RendererPhysical(string Kind, double Dx = 0, double Dy = 0, double ScreenWidth = 0, string? FromScreen = null,
    string? ToScreen = null, double ZoomFrom = 0, double ZoomTo = 0, CharacterTouch? Focus = null)
{
    public static IReadOnlyList<string> Kinds { get; } = ["moved", "home", "zoomed", "zoom_reset", "panned"];

    [JsonIgnore]
    public bool IsValid => Kinds.Contains(Kind) && new[] { Dx, Dy, ScreenWidth, ZoomFrom, ZoomTo }.All(double.IsFinite) &&
        Math.Abs(Dx) < 1e6 && Math.Abs(Dy) < 1e6 && ScreenWidth is >= 0 and < 1e6 && ZoomFrom is >= 0 and < 1e7 && ZoomTo is >= 0 and < 1e7 &&
        new[] { FromScreen, ToScreen }.All(s => s is null || s.Length is > 0 and <= 64 && !s.Any(char.IsControl)) && (Focus is null || Focus.IsValid);

    /// <summary>Whether it ended on another monitor than it started on.</summary>
    [JsonIgnore]
    public bool OtherScreen => FromScreen is { } from && ToScreen is { } to && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where the overlay is and how it shows the character: its top-left (device-independent pixels), the monitor it is on,
/// the character's size on screen (frame width times the view's zoom), the view's zoom and pan.</summary>
public readonly record struct OverlayState(double Left, double Top, string? Screen, double Scale, double Zoom, double ViewX, double ViewY,
    double ScreenWidth = 0);

/// <summary>A settled change: its kind, the overlay before the first step and after the last, and the point (fractions of the
/// page) the zoom closed in on, if any.</summary>
public sealed record OverlayChange(string Kind, OverlayState Before, OverlayState After, (double X, double Y)? Anchor);

/// <summary>Debounces what the user does to the overlay: each kind of change collects its steps (each move of a drag, each notch
/// of the wheel) and becomes one <see cref="OverlayChange"/> once nothing more of it happened for <paramref name="settleMs"/>.
/// Sending it home or resetting the zoom drops the moves, zooms and pans before it. A change that came back to where it started
/// is dropped. Times are any monotonic milliseconds.</summary>
public sealed class OverlayChangeTracker(long settleMs = 800)
{
    private readonly List<(string Kind, OverlayState Before, OverlayState After, long Last, (double X, double Y)? Anchor)> pending = [];

    public long SettleMs => settleMs;
    public bool Pending => pending.Count > 0;

    public void Note(string kind, OverlayState before, OverlayState after, long now, (double X, double Y)? anchor = null)
    {
        if (!RendererPhysical.Kinds.Contains(kind)) throw new ArgumentException("Unknown overlay change.", nameof(kind));
        if (kind == "home") pending.RemoveAll(p => p.Kind is "moved" or "zoomed" or "zoom_reset" or "panned" or "home");
        if (kind == "zoom_reset") pending.RemoveAll(p => p.Kind is "zoomed" or "panned" or "zoom_reset");
        var at = pending.FindIndex(p => p.Kind == kind);
        if (at < 0) pending.Add((kind, before, after, now, anchor));
        else pending[at] = (kind, pending[at].Before, after, now, anchor ?? pending[at].Anchor);
    }

    /// <summary>The changes that have settled by <paramref name="now"/> and changed something, oldest first.</summary>
    public IReadOnlyList<OverlayChange> Due(long now)
    {
        var due = pending.Where(p => now - p.Last >= settleMs).ToArray();
        pending.RemoveAll(p => now - p.Last >= settleMs);
        return [.. due.OrderBy(p => p.Last).Where(p => Changed(p.Kind, p.Before, p.After)).Select(p => new OverlayChange(p.Kind, p.Before, p.After, p.Anchor))];
    }

    public static bool Changed(string kind, OverlayState before, OverlayState after) => kind switch
    {
        "moved" => Math.Abs(after.Left - before.Left) >= 4 || Math.Abs(after.Top - before.Top) >= 4 ||
            !string.Equals(before.Screen, after.Screen, StringComparison.OrdinalIgnoreCase),
        "zoomed" => before.Scale > 0 && after.Scale > 0 && Math.Abs(Math.Log(after.Scale / before.Scale)) >= 0.02,
        "panned" => Math.Abs(after.ViewX - before.ViewX) >= 0.02 || Math.Abs(after.ViewY - before.ViewY) >= 0.02,
        _ => true
    };
}

/// <summary>Plain words for what the user did to the character, as the character hears it ("to their other monitor", "in on
/// your face"). Each is a detail for the touch ledger's phrase of that kind; null means the ledger's own default.</summary>
public static class CharacterPhysicalWords
{
    /// <summary>Where a move took the character: to another monitor, else how far (by the screen's width) and which way.</summary>
    public static string MoveDetail(RendererPhysical change)
    {
        if (change.OtherScreen) return "to their other monitor";
        var distance = Math.Sqrt(change.Dx * change.Dx + change.Dy * change.Dy);
        var width = change.ScreenWidth > 0 ? change.ScreenWidth : 1920;
        var amount = distance < width * 0.08 ? "a little " : distance > width * 0.4 ? "a long way " : "";
        double ax = Math.Abs(change.Dx), ay = Math.Abs(change.Dy);
        var horizontal = change.Dx < 0 ? "to the left" : "to the right";
        var vertical = change.Dy < 0 ? "up" : "down";
        var way = ax >= ay * 2.5 ? horizontal : ay >= ax * 2.5 ? vertical : $"{vertical} and {horizontal}";
        return amount + way;
    }

    /// <summary>How a zoom went: "in on your face" (what it closed in on), "in" or "out".</summary>
    public static string ZoomDetail(RendererPhysical change, string? focus) =>
        change.ZoomTo >= change.ZoomFrom ? focus is { Length: > 0 } ? "in on " + focus : "in" : "out";

    /// <summary>The touch ledger's kind for a settled change ("Moved", "Zoomed" or "Panned") and its detail; <paramref name="focus"/>
    /// is the phrase for what a zoom closed in on or a pan centers on ("your face").</summary>
    public static (string Kind, string? Detail) Describe(RendererPhysical change, string? focus) => change.Kind switch
    {
        "moved" => ("Moved", MoveDetail(change)),
        "home" => ("Moved", "back to your usual spot"),
        "zoomed" => ("Zoomed", ZoomDetail(change, focus)),
        "zoom_reset" => ("Zoomed", "back out to normal"),
        _ => ("Panned", focus is { Length: > 0 } ? "to " + focus : null)
    };

    /// <summary>A stroke's detail for the touch ledger ("slowly", "quickly" or none) and how many times to record it: once for
    /// each pass, at most 8, so the ledger says "stroked your hair slowly 4 times".</summary>
    public static (string? Detail, int Times) Stroke(StrokeSummary summary) =>
        (summary.Pace switch { "slow" => "slowly", "quick" => "quickly", _ => null }, Math.Clamp(summary.Passes, 1, 8));
}
