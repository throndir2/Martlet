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

/// <summary>The character's eyes hint and its pictures: snapshots, page captures and probe pictures for the touch zones.</summary>
internal sealed partial class RendererWindow
{
    /// <summary>Gives the page the face hint (<see cref="RendererEyes"/>: the eyes, and the cheeks and mouth when measured;
    /// without both eyes it clears it) and says what the eyes use now. The page takes it beside the touch and face readings, so
    /// a hint it can't use never fails the character; a hint that isn't valid clears it.</summary>
    private async Task<RendererEyesFrom> EyesAsync(RendererMessage message)
    {
        var hint = message.Data.ValueKind == JsonValueKind.Object ? RendererProtocol.Data<RendererEyes>(message) : new RendererEyes();
        if (!hint.IsValid)
        {
            ErrorLog.Warn("Martlet sent an eye hint that isn't valid; the character's eyes use the model's own data or an estimate.");
            hint = new();
        }
        var answer = await BrowserAsync<object?>("eyes", hint.Clears ? null
            : new { left = hint.Left, right = hint.Right, cheekLeft = hint.CheekLeft, cheekRight = hint.CheekRight, mouth = hint.Mouth });
        return new(answer.TryGetProperty("eyesFrom", out var from) && from.ValueKind == JsonValueKind.String ? RendererEyesFrom.Read(from.GetString()) : null);
    }

    /// <summary>A picture of the character, cropped to its opaque pixels (a head-and-shoulders square for a portrait), scaled
    /// down and encoded as a PNG small enough for one message. A portrait or an ordinary picture is WebView2's capture of the page
    /// as it shows now. A whole picture (touch zones) is drawn by the page itself (<see cref="PictureAsync"/>), never on screen:
    /// framed whole (no zoom, no pan) on a page of the overlay's shape, and drawn again zoomed out when the model draws past its
    /// own canvas and the page cuts it off (<see cref="WholeFraming"/>). Its crop and probe are given with the character framed
    /// whole.</summary>
    private async Task<RendererPicture> SnapshotAsync(RendererSnapshot request)
    {
        failure.ThrowIfFailed();
        var edge = Math.Clamp(request.Edge, RendererSnapshot.MinimumEdge, RendererSnapshot.MaximumEdge);
        RendererZoneProbe? probe = null;
        // The framing the picture is drawn in, when it is a whole picture.
        var (zoom, panX, panY) = (1d, 0d, 0d);
        PageCapture shot;
        if (request.Whole)
        {
            (shot, probe) = await PictureAsync(zoom, panX, panY, chains: true);
            // Parts drawn past the model's canvas (legs below it, say) are cut off at the page's edge: zoom out until all shows.
            // The swinging parts were measured at the first framing, the whole one; they come along.
            if (probe?.Drawables is { Length: > 0 } drawables &&
                WholeFraming.Fit(shot.Seen, shot.Cut, drawables, FrameFraction) is var fit && fit != (1, 0, 0))
            {
                (zoom, panX, panY) = fit;
                (shot, var framed) = await PictureAsync(zoom, panX, panY, chains: false);
                probe = framed is null ? probe : WholeFraming.Unframed(framed, zoom, panX, panY, FrameFraction) with { Chains = probe.Chains };
            }
        }
        else shot = await CaptureAsync();
        var (source, width, height) = (shot.Source, shot.Width, shot.Height);
        var (left, top, right, bottom) = (shot.Left, shot.Top, shot.Right, shot.Bottom);
        int boxWidth = right - left + 1, boxHeight = bottom - top + 1;
        Int32Rect crop;
        if (request.Portrait)
        {
            // A square as wide as the character's shoulders, from just above the head.
            var side = Math.Min(Math.Max(1, Math.Min(boxWidth, boxHeight)), Math.Min(width, height));
            var x0 = Math.Clamp(left + boxWidth / 2 - side / 2, 0, width - side);
            var y0 = Math.Clamp(top - side / 20, 0, height - side);
            crop = new(x0, y0, side, side);
        }
        else
        {
            var pad = Math.Max(boxWidth, boxHeight) / 20;
            var x0 = Math.Max(0, left - pad);
            var y0 = Math.Max(0, top - pad);
            crop = new(x0, y0, Math.Min(width, right + pad + 1) - x0, Math.Min(height, bottom + pad + 1) - y0);
        }
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(source, crop);
        var at = new TouchZoneBox((double)crop.X / width, (double)crop.Y / height, (double)crop.Width / width, (double)crop.Height / height);
        if (zoom != 1 || panX != 0 || panY != 0) at = WholeFraming.Unframed(at, zoom, panX, panY, FrameFraction);
        // The probe travels in the same message as the picture.
        var reserve = probe is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(probe, RendererProtocol.Json).Length;
        foreach (var size in new[] { edge, 1536, 1024, 768, 512, 384, 256, 160 }.Where(size => size <= edge).Distinct())
        {
            var scale = Math.Min(1, (double)size / Math.Max(crop.Width, crop.Height));
            var scaled = new System.Windows.Media.Imaging.TransformedBitmap(cropped, new ScaleTransform(scale, scale));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
            using var png = new MemoryStream();
            encoder.Save(png);
            // Base64 grows by a third; the reply must stay well inside one renderer message.
            if (png.Length * 4 / 3 < RendererProtocol.MaximumMessageBytes - 4096 - reserve)
                return new(Convert.ToBase64String(png.GetBuffer(), 0, (int)png.Length), scaled.PixelWidth, scaled.PixelHeight,
                    at.X, at.Y, at.Width, at.Height, probe, Math.Round(zoom, 4));
        }
        throw new InvalidDataException("The character's picture is too large.");
    }

    // A capture of the page: its pixels, and the box of the character's opaque pixels in them (the whole page when there are none).
    private sealed record PageCapture(System.Windows.Media.Imaging.BitmapSource Source, int Width, int Height, int Left, int Top, int Right,
        int Bottom, bool Empty)
    {
        /// <summary>The character's opaque pixels as a box in fractions of the page.</summary>
        internal TouchZoneBox Seen => new((double)Left / Width, (double)Top / Height, (double)(Right - Left + 1) / Width, (double)(Bottom - Top + 1) / Height);

        /// <summary>The page edges the character's opaque pixels reach, where the page may cut it off.</summary>
        internal PageEdges Cut => Empty ? PageEdges.None
            : (Left <= 1 ? PageEdges.Left : 0) | (Top <= 1 ? PageEdges.Top : 0) | (Right >= Width - 2 ? PageEdges.Right : 0) |
              (Bottom >= Height - 2 ? PageEdges.Bottom : 0);
    }

    private async Task<PageCapture> CaptureAsync()
    {
        using var captured = new MemoryStream();
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, captured);
        captured.Position = 0;
        return Decode(captured);
    }

    // The overlay's shape (its 3:4 frame with the room beside it), as large as the Live2D canvas allows (2048 pixels).
    private const int PictureHeight = 1364, PictureWidth = (int)(PictureHeight / FrameFraction * 3 / 4);

    /// <summary>The page draws the character on its canvas, PictureWidth by PictureHeight pixels in the framing
    /// <paramref name="zoom"/>, <paramref name="x"/>, <paramref name="y"/>, and reads it back in the same step, so it never shows
    /// on screen (in a still renderer, in the model's rest pose). Also where its drawables or bones and its face are in that
    /// picture, as fractions of it (null when the page can't say), and with <paramref name="chains"/> the parts of it that swing
    /// on their own (measured by moving each physics setting, so only once per picture of the character).</summary>
    private async Task<(PageCapture Shot, RendererZoneProbe? Probe)> PictureAsync(double zoom, double x, double y, bool chains)
    {
        const string Prefix = "data:image/png;base64,";
        var drawn = await BrowserAsync("picture", new { width = PictureWidth, height = PictureHeight, zoom, x, y, frame = FrameFraction, chains });
        if (!drawn.TryGetProperty("png", out var png) || png.ValueKind != JsonValueKind.String || png.GetString() is not { } url ||
            !url.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The page couldn't draw the character's picture.");
        using var bytes = new MemoryStream(Convert.FromBase64String(url[Prefix.Length..]), writable: false);
        RendererZoneProbe? probe = null;
        try
        {
            probe = new(drawn.TryGetProperty("drawables", out var drawables) ? drawables.Deserialize<RendererDrawableBox[]>(RendererProtocol.Json) : null,
                drawn.TryGetProperty("bones", out var bones) ? bones.Deserialize<RendererBonePoint[]>(RendererProtocol.Json) : null,
                drawn.TryGetProperty("face", out var face) ? Face(face) : null,
                drawn.TryGetProperty("parts", out var parts) ? parts.Deserialize<RendererModelPart[]>(RendererProtocol.Json) : null);
        }
        catch (JsonException error) { ErrorLog.Warn($"Couldn't read where the character's parts are: {error.Message}"); }
        // The parts that swing on their own come apart, so a chain the page can't describe never loses the rest.
        try
        {
            if (probe is not null && drawn.TryGetProperty("chains", out var swinging) && swinging.ValueKind == JsonValueKind.Array)
                probe = probe with
                {
                    Chains = [.. (swinging.Deserialize<RendererChain[]>(RendererProtocol.Json) ?? []).Where(c => c is { IsValid: true }).Take(RendererChain.MaximumChains)]
                };
            if (probe is not null && drawn.TryGetProperty("springs", out var springs) && springs.ValueKind == JsonValueKind.Array)
                probe = probe with
                {
                    Springs = [.. (springs.Deserialize<RendererSpring[]>(RendererProtocol.Json) ?? []).Where(s => s is { IsValid: true }).Take(RendererSpring.MaximumSprings)]
                };
        }
        catch (JsonException error) { ErrorLog.Warn($"Couldn't read which parts of the character swing on their own: {error.Message}"); }
        return (Decode(bytes), probe);
    }

    // The face anchor the page read while drawing a picture (fractions of it), or null when it isn't one.
    private static RendererFace? Face(JsonElement face)
    {
        double? Number(string name) => face.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        if (face.ValueKind != JsonValueKind.Object || Number("x") is not { } x || Number("y") is not { } y || Number("width") is not { } width) return null;
        var read = new RendererFace(x, y, width, Number("angle") ?? 0,
            face.TryGetProperty("tracking", out var tracking) && tracking.ValueKind == JsonValueKind.String ? tracking.GetString() : null);
        return read.IsValid ? read : read with { Tracking = null } is { IsValid: true } plain ? plain : null;
    }

    // A picture of the page as Bgra32 pixels, with the box of the character's opaque pixels in it.
    private static PageCapture Decode(Stream png)
    {
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(png, System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var source = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        var (left, top, right, bottom) = (width, height, -1, -1);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] > 24)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
        return right < 0 ? new(source, width, height, 0, 0, width - 1, height - 1, Empty: true)
            : new(source, width, height, left, top, right, bottom, Empty: false);
    }

    /// <summary>Where a point of the page (fractions) sits with the character framed whole (no zoom, no pan).</summary>
    private (double X, double Y) Unframed(double x, double y) => CharacterTouch.Unframed(x, y, viewZoom, viewX, viewY, FrameFraction);
}
