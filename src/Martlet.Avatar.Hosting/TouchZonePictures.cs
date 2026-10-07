using System.Globalization;

namespace Martlet.Avatar.Hosting;

/// <summary>A picture as straight (not premultiplied) BGRA32 pixels, rows top-down: a snapshot of the character, or what the
/// vision model is sent.</summary>
public sealed class ZonePixels
{
    public ZonePixels(int width, int height, byte[] bgra)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (bgra.Length != (long)width * height * 4) throw new ArgumentException("The pixels don't match the picture's size.", nameof(bgra));
        (Width, Height, Bgra) = (width, height, bgra);
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public (byte R, byte G, byte B, byte A) Pixel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i], Bgra[i + 3]);
    }
}

/// <summary>A rectangle of whole pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>The pixels <paramref name="box"/> (fractions of a <paramref name="width"/> by <paramref name="height"/> picture)
    /// covers, rounded outward and kept inside the picture; at least one pixel.</summary>
    public static PixelRect Of(TouchZoneBox box, int width, int height)
    {
        // A fraction a whole pixel makes (62 / 200) can come back a hair under it (61.99999...).
        const double Slack = 1e-6;
        int x0 = Math.Clamp((int)Math.Floor(box.X * width + Slack), 0, width - 1), y0 = Math.Clamp((int)Math.Floor(box.Y * height + Slack), 0, height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((box.X + box.Width) * width - Slack), x0 + 1, width);
        int y1 = Math.Clamp((int)Math.Ceiling((box.Y + box.Height) * height - Slack), y0 + 1, height);
        return new(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>This rectangle as fractions of a <paramref name="width"/> by <paramref name="height"/> picture.</summary>
    public TouchZoneBox Fraction(int width, int height) => new((double)X / width, (double)Y / height, (double)Width / width, (double)Height / height);
}

public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>A numbered box drawn on a picture for the vision model, as fractions of the picture.</summary>
public sealed record MarkedBox(int Number, TouchZoneBox Box, Rgb Color);

/// <summary>The CPU side of finding touch zones: where the character's opaque pixels are, and the pictures the vision model
/// gets: the character flattened onto a plain backdrop that contrasts with it (servers paint a transparent background black
/// or white as they like), scaled up or down, with a grid of tenths numbered 0.1 to 0.9 along the top and left edges and,
/// for a check, the current boxes drawn and numbered.</summary>
public static class TouchZonePictures
{
    /// <summary>A pixel with more alpha than this is part of the character (as the renderer's snapshot crop counts it).</summary>
    public const byte OpaqueAlpha = 24;
    public static Rgb LightBackdrop { get; } = new(236, 236, 236);
    public static Rgb DarkBackdrop { get; } = new(40, 40, 46);

    /// <summary>The colors of the numbered boxes: saturated, and far from both backdrops and the grid's grey.</summary>
    public static IReadOnlyList<Rgb> MarkColors { get; } =
    [
        new(230, 30, 50), new(20, 110, 245), new(0, 165, 70), new(245, 130, 0), new(155, 60, 225), new(225, 0, 160),
        new(0, 165, 200), new(140, 140, 0), new(165, 80, 30), new(0, 120, 120)
    ];

    /// <summary>The bounds of the opaque pixels inside <paramref name="within"/>, or null when none are.</summary>
    public static PixelRect? OpaqueBounds(ZonePixels picture, PixelRect within)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        var bgra = picture.Bgra;
        for (var y = Math.Max(0, within.Y); y < Math.Min(picture.Height, within.Bottom); y++)
        {
            var row = y * picture.Width;
            for (var x = Math.Max(0, within.X); x < Math.Min(picture.Width, within.Right); x++)
            {
                if (bgra[(row + x) * 4 + 3] <= OpaqueAlpha) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                bottom = y;
            }
        }
        return right < 0 ? null : new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>The share (0 to 1) of the pixels inside <paramref name="within"/> that are opaque, and the bounds of the rows and
    /// columns that are at least <paramref name="lineShare"/> opaque (stray strands left out), or null when none are.</summary>
    public static (double Share, PixelRect? Bounds) Opaque(ZonePixels picture, PixelRect within, double lineShare = 0.03)
    {
        int x0 = Math.Max(0, within.X), y0 = Math.Max(0, within.Y);
        int x1 = Math.Min(picture.Width, within.Right), y1 = Math.Min(picture.Height, within.Bottom);
        if (x1 <= x0 || y1 <= y0) return (0, null);
        var rows = new int[y1 - y0];
        var columns = new int[x1 - x0];
        var total = 0;
        var bgra = picture.Bgra;
        for (var y = y0; y < y1; y++)
        {
            var row = y * picture.Width;
            for (var x = x0; x < x1; x++)
            {
                if (bgra[(row + x) * 4 + 3] <= OpaqueAlpha) continue;
                rows[y - y0]++;
                columns[x - x0]++;
                total++;
            }
        }
        var share = (double)total / ((x1 - x0) * (y1 - y0));
        int rowNeed = Math.Max(1, (int)Math.Ceiling(lineShare * (x1 - x0))), columnNeed = Math.Max(1, (int)Math.Ceiling(lineShare * (y1 - y0)));
        int top = Array.FindIndex(rows, c => c >= rowNeed), bottom = Array.FindLastIndex(rows, c => c >= rowNeed);
        int left = Array.FindIndex(columns, c => c >= columnNeed), right = Array.FindLastIndex(columns, c => c >= columnNeed);
        return top < 0 || left < 0 ? (share, null) : (share, new PixelRect(x0 + left, y0 + top, right - left + 1, bottom - top + 1));
    }

    /// <summary>The plain color the character is shown on: light for a mostly dark character, dark for a mostly light one.</summary>
    public static Rgb Backdrop(ZonePixels picture)
    {
        double sum = 0, weight = 0;
        var bgra = picture.Bgra;
        // Every few pixels is plenty for an average.
        var step = Math.Max(1, (int)Math.Sqrt((double)picture.Width * picture.Height / 250_000));
        for (var y = 0; y < picture.Height; y += step)
            for (var x = 0; x < picture.Width; x += step)
            {
                var i = (y * picture.Width + x) * 4;
                var a = bgra[i + 3];
                if (a <= OpaqueAlpha) continue;
                sum += a * (0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2]);
                weight += a;
            }
        return weight == 0 || sum / weight < 140 ? LightBackdrop : DarkBackdrop;
    }

    /// <summary>What the vision model sees of <paramref name="region"/> (fractions of <paramref name="source"/>): scaled so its
    /// longer side is <paramref name="edge"/> pixels (but never more than <paramref name="maximumZoom"/> times larger), flattened
    /// onto <paramref name="backdrop"/>, with the grid of tenths when <paramref name="grid"/> and the <paramref name="marks"/>
    /// (fractions of the region) drawn and numbered. Opaque.</summary>
    public static ZonePixels Compose(ZonePixels source, TouchZoneBox region, int edge, double maximumZoom, Rgb backdrop, bool grid,
        IReadOnlyList<MarkedBox>? marks = null)
    {
        var area = PixelRect.Of(region, source.Width, source.Height);
        var scale = Math.Min((double)edge / Math.Max(area.Width, area.Height), maximumZoom);
        int w = Math.Max(1, (int)Math.Round(area.Width * scale)), h = Math.Max(1, (int)Math.Round(area.Height * scale));
        var output = new byte[w * h * 4];
        double stepX = (double)area.Width / w, stepY = (double)area.Height / h;
        int samplesX = Math.Max(1, (int)Math.Ceiling(stepX)), samplesY = Math.Max(1, (int)Math.Ceiling(stepY));
        var count = samplesX * samplesY;
        // Each output pixel averages premultiplied samples over its footprint (a box filter when shrinking, bilinear when
        // enlarging), then lies on the backdrop.
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (var j = 0; j < samplesY; j++)
                    for (var i = 0; i < samplesX; i++)
                        Bilinear(source, area.X + (x + (i + 0.5) / samplesX) * stepX - 0.5, area.Y + (y + (j + 0.5) / samplesY) * stepY - 0.5,
                            ref b, ref g, ref r, ref a);
                var o = (y * w + x) * 4;
                var rest = 1 - a / count / 255;
                output[o] = ToByte(b / count + backdrop.B * rest);
                output[o + 1] = ToByte(g / count + backdrop.G * rest);
                output[o + 2] = ToByte(r / count + backdrop.R * rest);
                output[o + 3] = 255;
            }
        });
        var picture = new ZonePixels(w, h, output);
        var canvas = new Canvas(picture);
        if (grid) canvas.Grid(backdrop);
        if (marks is { Count: > 0 }) canvas.Marks(marks);
        return picture;
    }

    private static void Bilinear(ZonePixels source, double x, double y, ref double b, ref double g, ref double r, ref double a)
    {
        x = Math.Clamp(x, 0, source.Width - 1);
        y = Math.Clamp(y, 0, source.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, source.Width - 1), y1 = Math.Min(y0 + 1, source.Height - 1);
        double fx = x - x0, fy = y - y0;
        Tap(source, x0, y0, (1 - fx) * (1 - fy), ref b, ref g, ref r, ref a);
        Tap(source, x1, y0, fx * (1 - fy), ref b, ref g, ref r, ref a);
        Tap(source, x0, y1, (1 - fx) * fy, ref b, ref g, ref r, ref a);
        Tap(source, x1, y1, fx * fy, ref b, ref g, ref r, ref a);
    }

    private static void Tap(ZonePixels source, int x, int y, double weight, ref double b, ref double g, ref double r, ref double a)
    {
        if (weight <= 0) return;
        var i = (y * source.Width + x) * 4;
        var alpha = source.Bgra[i + 3];
        if (alpha == 0) return;
        var w = weight * alpha / 255;
        b += source.Bgra[i] * w;
        g += source.Bgra[i + 1] * w;
        r += source.Bgra[i + 2] * w;
        a += weight * alpha;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    // ---------- drawing on an opaque picture ----------

    private sealed class Canvas(ZonePixels picture)
    {
        private readonly List<PixelRect> tags = [];
        private int Size => Math.Max(picture.Width, picture.Height);
        // The digits' scale: 21 pixels tall on a 1024-pixel picture.
        private int TextScale => Math.Clamp((int)Math.Round(Size / 340.0), 1, 5);

        internal void Grid(Rgb backdrop)
        {
            var thickness = Math.Max(1, (int)Math.Round(Size / 900.0));
            var line = new Rgb(128, 128, 136);
            var dark = backdrop.R < 128;
            Rgb tag = dark ? new(20, 20, 24) : new(255, 255, 255), text = dark ? new(235, 235, 235) : new(25, 25, 30);
            for (var i = 1; i < 10; i++)
            {
                Fill((int)Math.Round(picture.Width * i / 10.0) - thickness / 2, 0, thickness, picture.Height, line, 0.5);
                Fill(0, (int)Math.Round(picture.Height * i / 10.0) - thickness / 2, picture.Width, thickness, line, 0.5);
            }
            var scale = TextScale;
            // Each label fits between its grid lines; when even small numbers don't fit (a narrow picture), every other line
            // is numbered, larger.
            int Fits(double spacing, int units) => Math.Max(1, Math.Min(scale, (int)Math.Floor(spacing * 0.75 / units)));
            int stepAcross = Fits(picture.Width / 10.0, 15) < 2 ? 2 : 1, stepDown = Fits(picture.Height / 10.0, 9) < 2 ? 2 : 1;
            int across = Fits(picture.Width * stepAcross / 10.0, 15), down = Fits(picture.Height * stepDown / 10.0, 9);
            for (var i = 1; i < 10; i++)
            {
                var label = "0." + i.ToString(CultureInfo.InvariantCulture);
                if (i % stepAcross == 0)
                {
                    int width = TextWidth(label, across), height = 7 * across, pad = across;
                    var x = (int)Math.Round(picture.Width * i / 10.0) - width / 2;
                    Fill(x - pad, 0, width + 2 * pad, height + 2 * pad, tag, 0.85);
                    Text(label, x, pad, across, text);
                }
                if (i % stepDown == 0)
                {
                    int width = TextWidth(label, down), height = 7 * down, pad = down;
                    var y = (int)Math.Round(picture.Height * i / 10.0) - height / 2;
                    Fill(0, y - pad, width + 2 * pad, height + 2 * pad, tag, 0.85);
                    Text(label, pad, y, down, text);
                }
            }
        }

        internal void Marks(IReadOnlyList<MarkedBox> marks)
        {
            var thickness = Math.Max(2, (int)Math.Round(Size / 400.0));
            foreach (var mark in marks)
            {
                var r = PixelRect.Of(mark.Box.Clamped(), picture.Width, picture.Height);
                Fill(r.X, r.Y, r.Width, thickness, mark.Color, 1);
                Fill(r.X, r.Bottom - thickness, r.Width, thickness, mark.Color, 1);
                Fill(r.X, r.Y, thickness, r.Height, mark.Color, 1);
                Fill(r.Right - thickness, r.Y, thickness, r.Height, mark.Color, 1);
            }
            // The numbers go on top of every outline, each at a corner of its box where it hides no other number.
            var scale = TextScale;
            var pad = scale;
            foreach (var mark in marks)
            {
                var r = PixelRect.Of(mark.Box.Clamped(), picture.Width, picture.Height);
                var label = mark.Number.ToString(CultureInfo.InvariantCulture);
                int width = TextWidth(label, scale) + 2 * pad, height = 7 * scale + 2 * pad;
                PixelRect[] candidates =
                [
                    new(r.X, r.Y, width, height), new(r.X, r.Y - height, width, height), new(r.Right - width, r.Y, width, height),
                    new(r.Right - width, r.Y - height, width, height), new(r.X, r.Bottom - height, width, height),
                    new(r.Right - width, r.Bottom - height, width, height), new(r.X, r.Bottom, width, height),
                    new(r.X - width, r.Y, width, height), new(r.Right, r.Y, width, height)
                ];
                var places = candidates.Select(Inside).ToArray();
                var place = places.FirstOrDefault(c => !tags.Any(t => Overlap(t, c)), places[0]);
                tags.Add(place);
                Fill(place.X, place.Y, place.Width, place.Height, mark.Color, 1);
                Text(label, place.X + pad, place.Y + pad, scale, new(255, 255, 255));
            }
        }

        private PixelRect Inside(PixelRect r) =>
            new(Math.Clamp(r.X, 0, Math.Max(0, picture.Width - r.Width)), Math.Clamp(r.Y, 0, Math.Max(0, picture.Height - r.Height)), r.Width, r.Height);

        private static bool Overlap(PixelRect a, PixelRect b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

        private void Fill(int x, int y, int width, int height, Rgb color, double alpha)
        {
            var bgra = picture.Bgra;
            for (var py = Math.Max(0, y); py < Math.Min(picture.Height, y + height); py++)
                for (var px = Math.Max(0, x); px < Math.Min(picture.Width, x + width); px++)
                {
                    var i = (py * picture.Width + px) * 4;
                    bgra[i] = ToByte(bgra[i] * (1 - alpha) + color.B * alpha);
                    bgra[i + 1] = ToByte(bgra[i + 1] * (1 - alpha) + color.G * alpha);
                    bgra[i + 2] = ToByte(bgra[i + 2] * (1 - alpha) + color.R * alpha);
                }
        }

        private void Text(string text, int x, int y, int scale, Rgb color)
        {
            foreach (var c in text)
            {
                if (!Font.TryGetValue(c, out var glyph)) continue;
                for (var row = 0; row < glyph.Length; row++)
                    for (var column = 0; column < glyph[row].Length; column++)
                        if (glyph[row][column] == '#') Fill(x + column * scale, y + row * scale, scale, scale, color, 1);
                x += (glyph[0].Length + 1) * scale;
            }
        }

        private static int TextWidth(string text, int scale) =>
            Math.Max(0, text.Sum(c => Font.TryGetValue(c, out var glyph) ? glyph[0].Length + 1 : 0) - 1) * scale;
    }

    // A 5 by 7 pixel font for the grid's and the boxes' numbers.
    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['0'] = [" ### ", "#   #", "#  ##", "# # #", "##  #", "#   #", " ### "],
        ['1'] = ["  #  ", " ##  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### "],
        ['2'] = [" ### ", "#   #", "    #", "   # ", "  #  ", " #   ", "#####"],
        ['3'] = ["#####", "   # ", "  #  ", "   # ", "    #", "#   #", " ### "],
        ['4'] = ["   # ", "  ## ", " # # ", "#  # ", "#####", "   # ", "   # "],
        ['5'] = ["#####", "#    ", "#### ", "    #", "    #", "#   #", " ### "],
        ['6'] = ["  ## ", " #   ", "#    ", "#### ", "#   #", "#   #", " ### "],
        ['7'] = ["#####", "    #", "   # ", "  #  ", " #   ", " #   ", " #   "],
        ['8'] = [" ### ", "#   #", "#   #", " ### ", "#   #", "#   #", " ### "],
        ['9'] = [" ### ", "#   #", "#   #", " ####", "    #", "   # ", " ##  "],
        ['.'] = ["  ", "  ", "  ", "  ", "  ", "##", "##"]
    };
}
