namespace Martlet.Avatar.Hosting;

/// <summary>Which edges of the renderer page the character's opaque pixels reach in a picture.</summary>
[Flags]
public enum PageEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>
/// How a whole-character picture (touch zones) frames the character. Zoom 1 with no pan fits the model's own canvas to the
/// page, but a Live2D model can draw past its canvas (legs below it, say), and the page then cuts those parts off. <see cref="Fit"/>
/// zooms out just enough for all of the character to show, and <see cref="Unframed(TouchZoneBox, double, double, double, double)"/>
/// turns what that picture and its probe say back into the whole framing (zoom 1, no pan), which touches compare with
/// (<see cref="CharacterTouch.WholeX"/>). Boxes in the whole framing can then lie partly outside 0..1.
/// </summary>
public static class WholeFraming
{
    /// <summary>The room kept around the character when it zooms out, as a fraction of the page.</summary>
    public const double Margin = 0.02;
    /// <summary>The smallest zoom, so a part far from the character never shrinks the character to nothing.</summary>
    public const double MinimumZoom = 0.3;
    // Parts this close to the page's edges (fractions) still fit, and parts this close to each other touch.
    private const double Slack = 0.005, Touching = 0.01;

    /// <summary>
    /// The framing (zoom and pan, as the renderers take them: fitted * zoom + (x * frame, y)) that shows the whole character:
    /// (1, 0, 0) when it already fits, else zoomed out around its middle. <paramref name="seen"/> is the box of its opaque pixels
    /// at zoom 1 (fractions of the page) and <paramref name="cut"/> the page edges those pixels reach; past such an edge, the
    /// character reaches as far as its drawables do (<paramref name="drawables"/>, their bounds at zoom 1 as fractions of the
    /// page; only those that touch the visible character, so a part placed far away is left out). <paramref name="frame"/>
    /// is the fraction of the page's width the character's frame spans.
    /// </summary>
    public static (double Zoom, double X, double Y) Fit(TouchZoneBox seen, PageEdges cut, IEnumerable<RendererDrawableBox> drawables, double frame)
    {
        if (cut == PageEdges.None || Reach(drawables) is not { } reach || !(frame > 0)) return (1, 0, 0);
        double left = seen.X, top = seen.Y, right = seen.X + seen.Width, bottom = seen.Y + seen.Height;
        if (cut.HasFlag(PageEdges.Left)) left = Math.Min(left, reach.Left);
        if (cut.HasFlag(PageEdges.Top)) top = Math.Min(top, reach.Top);
        if (cut.HasFlag(PageEdges.Right)) right = Math.Max(right, reach.Right);
        if (cut.HasFlag(PageEdges.Bottom)) bottom = Math.Max(bottom, reach.Bottom);
        if ((left >= -Slack && top >= -Slack && right <= 1 + Slack && bottom <= 1 + Slack) || !(right > left) || !(bottom > top)) return (1, 0, 0);
        var zoom = Math.Clamp(Math.Min((1 - 2 * Margin) / (right - left), (1 - 2 * Margin) / (bottom - top)), MinimumZoom, 1);
        // The character's middle goes to the page's middle, where the renderers draw clip space 0.
        return (zoom, -(left + right - 1) * zoom / frame, (top + bottom - 1) * zoom);
    }

    /// <summary>The bounds of the drawables that make up the visible character: those on the page, and those that touch them
    /// (a calf below a thigh that the page cuts off), and so on; null when none is on the page.</summary>
    internal static (double Left, double Top, double Right, double Bottom)? Reach(IEnumerable<RendererDrawableBox> drawables)
    {
        var boxes = drawables.Where(d => double.IsFinite(d.Left) && double.IsFinite(d.Top) && double.IsFinite(d.Right) &&
            double.IsFinite(d.Bottom) && d.Right > d.Left && d.Bottom > d.Top).ToList();
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        bool Overlaps(RendererDrawableBox d, double l, double t, double r, double b) => d.Left <= r && d.Right >= l && d.Top <= b && d.Bottom >= t;
        void Take(RendererDrawableBox d)
        {
            left = Math.Min(left, d.Left);
            top = Math.Min(top, d.Top);
            right = Math.Max(right, d.Right);
            bottom = Math.Max(bottom, d.Bottom);
        }
        var rest = new List<RendererDrawableBox>();
        foreach (var box in boxes)
            if (Overlaps(box, 0, 0, 1, 1)) Take(box);
            else rest.Add(box);
        if (!double.IsFinite(left)) return null;
        for (var grew = true; grew && rest.Count > 0;)
        {
            grew = false;
            for (var i = rest.Count - 1; i >= 0; i--)
            {
                if (!Overlaps(rest[i], left - Touching, top - Touching, right + Touching, bottom + Touching)) continue;
                Take(rest[i]);
                rest.RemoveAt(i);
                grew = true;
            }
        }
        return (left, top, right, bottom);
    }

    /// <summary>A box on the page in the framing <paramref name="zoom"/>, <paramref name="x"/>, <paramref name="y"/> as the same
    /// part of the character with it framed whole (<see cref="CharacterTouch.Unframed"/>).</summary>
    public static TouchZoneBox Unframed(TouchZoneBox box, double zoom, double x, double y, double frame)
    {
        var (left, top) = CharacterTouch.Unframed(box.X, box.Y, zoom, x, y, frame);
        var (right, bottom) = CharacterTouch.Unframed(box.X + box.Width, box.Y + box.Height, zoom, x, y, frame);
        return new(left, top, right - left, bottom - top);
    }

    /// <summary>A zones probe taken in the framing <paramref name="zoom"/>, <paramref name="x"/>, <paramref name="y"/> as it
    /// would read with the character framed whole (the face's width scales with the zoom; its roll doesn't change; drawables keep
    /// their parts).</summary>
    public static RendererZoneProbe Unframed(RendererZoneProbe probe, double zoom, double x, double y, double frame) => probe with
    {
        Drawables = probe.Drawables?.Select(d => Unframed(new TouchZoneBox(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top), zoom, x, y, frame) is var box
            ? d with { Left = box.X, Top = box.Y, Right = box.X + box.Width, Bottom = box.Y + box.Height } : d).ToArray(),
        Bones = probe.Bones?.Select(b => CharacterTouch.Unframed(b.X, b.Y, zoom, x, y, frame) is var (bx, by) ? new RendererBonePoint(b.Bone, bx, by) : b).ToArray(),
        Face = probe.Face is { } face && CharacterTouch.Unframed(face.X, face.Y, zoom, x, y, frame) is var (fx, fy)
            ? face with { X = fx, Y = fy, Width = face.Width / (zoom > 0 && double.IsFinite(zoom) ? zoom : 1) } : probe.Face
    };
}
