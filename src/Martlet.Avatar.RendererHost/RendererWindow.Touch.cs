using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost.Logging;
using Martlet.Presentation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Martlet.Avatar.RendererHost;

/// <summary>Tapping the character: a press becomes a tap, the page's hit test becomes a touch, and the touch goes to Martlet.</summary>
internal sealed partial class RendererWindow
{
    // ---------- tapping the character ----------

    // Where (in the viewport) and when the left button went down on the character; cleared once it moves too far to be a tap.
    private (Point At, long Since)? press;

    private int touchId;
    private (int Id, double X, double Y, int Held)? touchPending;

    private static bool Moved(Point from, Point to) =>
        Math.Abs(to.X - from.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(to.Y - from.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>Drags the overlay (Windows' own move loop, which returns on release). A press that moved it less than the system
    /// drag distance is a tap instead: the jitter is undone and the place isn't saved.</summary>
    private void DragWindow()
    {
        double left = Left, top = Top;
        var before = OverlayNow();
        var held = press;
        press = null;
        DragMove();
        if (held is { } h && !Moved(new Point(left, top), new Point(Left, Top)))
        {
            if (Left != left || Top != top) (Left, Top) = (left, top);
            // Windows' move loop can end before the button comes up; then the release (or a move) decides.
            if (PrimaryButtonDown()) press = h;
            else Tap(h.At, Environment.TickCount64 - h.Since);
            return;
        }
        NoteOverlay("moved", before);
        PlacementChanged();
    }

    private static bool PrimaryButtonDown() =>
        (GetAsyncKeyState(GetSystemMetrics(23) != 0 ? 0x02 : 0x01) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private void TrackPress(Point position)
    {
        if (press is { } held && Moved(held.At, position)) press = null;
    }

    /// <summary>The left button came up at <paramref name="at"/>: a short press that didn't move is a tap there.</summary>
    private void EndPress(Point at)
    {
        if (press is not { } held) return;
        press = null;
        if (!Moved(held.At, at)) Tap(held.At, Environment.TickCount64 - held.Since);
    }

    // A press that came up where it went down: a tap, or a hold when it lasted CharacterTouch.HoldMilliseconds or more. A press
    // longer than CharacterTouch.MaximumHeldMilliseconds is let go.
    private void Tap(Point at, long heldMilliseconds)
    {
        if (viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
        if (heldMilliseconds is < 0 or > CharacterTouch.MaximumHeldMilliseconds) return;
        TouchAt(at.X / viewport.ActualWidth, at.Y / viewport.ActualHeight, (int)heldMilliseconds);
    }

    /// <summary>Asks the page what of the character is at <paramref name="x"/>, <paramref name="y"/> (fractions of the page, +y
    /// down); its answer arrives unprompted (see <see cref="Touched"/>). A tap through UI Automation (Martlet's MCP
    /// character_touch) comes here too.</summary>
    private void TouchAt(double x, double y, int held)
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        x = Math.Round(Math.Clamp(x, 0, 1), 4);
        y = Math.Round(Math.Clamp(y, 0, 1), 4);
        var id = ++touchId;
        touchPending = (id, x, y, Math.Clamp(held, 0, CharacterTouch.MaximumHeldMilliseconds));
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "touch", data = new { id, x, y } },
                RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private int faceId;
    private int? facePending;

    /// <summary>The page's hit test for the last tap: remembered for UI Automation and, when it found the character, sent to
    /// Martlet on the request pipe. A malformed or stale answer is ignored; it never fails the renderer.</summary>
    private void Touched(JsonElement answer)
    {
        if (touchPending is not { } pending || answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) ||
            number != pending.Id) return;
        touchPending = null;
        var (hit, touch) = ReadTouch(answer, pending.X, pending.Y);
        touch = touch with { HeldMilliseconds = pending.Held };
        viewport.LastTouch = JsonSerializer.Serialize(new
        {
            n = pending.Id, x = touch.X, y = touch.Y, hit, zone = hit ? touch.CoarseZone : null, touch.HitAreas, touch.Drawables,
            touch.Bone, touch.Node, touch.Hair, touch.Mesh, touch.Material, held = touch.HeldMilliseconds,
            rest = touch is { RestX: { } restX, RestY: { } restY } ? new { x = restX, y = restY } : null
        }, RendererProtocol.Json);
        if (hit) SendTouch(touch);
    }

    /// <summary>One answer of the page's hit test at <paramref name="x"/>, <paramref name="y"/>: whether it found the character
    /// and what is there, where that point sits with the character framed whole, and where the touched point of the character
    /// was in its rest pose (the page's <c>rest</c>, framed now and whole). Malformed names, and a rest point far off the page,
    /// are dropped.</summary>
    private (bool Hit, CharacterTouch Touch) ReadTouch(JsonElement answer, double x, double y)
    {
        static string? Name(JsonElement owner, string property) =>
            owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text && !text.Any(char.IsControl)
                ? text[..Math.Min(text.Length, CharacterTouch.MaximumName)] : null;
        static string[] Names(JsonElement owner, string property, int most) =>
            owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).Where(text => text.Length is > 0 and <= CharacterTouch.MaximumName && !text.Any(char.IsControl))
                    .Take(most).ToArray()
                : [];
        static bool Near(double value) => value is >= CharacterTouch.MinimumFar and <= CharacterTouch.MaximumFar;
        var (wholeX, wholeY) = Unframed(x, y);
        if (answer.ValueKind != JsonValueKind.Object) return (false, new(x, y, [], [], null, null, false, null, null, 0, wholeX, wholeY));
        var hit = answer.TryGetProperty("hit", out var found) && found.ValueKind == JsonValueKind.True;
        double? restX = null, restY = null, restWholeX = null, restWholeY = null;
        if (answer.TryGetProperty("rest", out var rest) && rest.ValueKind == JsonValueKind.Object &&
            rest.TryGetProperty("x", out var rx) && rx.ValueKind == JsonValueKind.Number && rx.TryGetDouble(out var px) &&
            rest.TryGetProperty("y", out var ry) && ry.ValueKind == JsonValueKind.Number && ry.TryGetDouble(out var py) &&
            Near(px) && Near(py) && Unframed(px, py) is var (wx, wy) && Near(wx) && Near(wy))
            (restX, restY, restWholeX, restWholeY) = (px, py, wx, wy);
        return (hit, new CharacterTouch(x, y, Names(answer, "hitAreas", CharacterTouch.MaximumHitAreas),
            Names(answer, "drawables", CharacterTouch.MaximumDrawables), Name(answer, "bone"), Name(answer, "node"),
            answer.TryGetProperty("hair", out var hair) && hair.ValueKind == JsonValueKind.True, Name(answer, "mesh"), Name(answer, "material"),
            0, wholeX, wholeY, restX, restY, restWholeX, restWholeY));
    }

    private async void SendTouch(CharacterTouch touch)
    {
        if (!CanRequest) return;
        await SendRequestAsync("touch", touch, "A tap on the character");
    }
}
