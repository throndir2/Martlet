using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Providers;

// Also built into Martlet's MCP server (model_ability_check and model_lab), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>Test vision's picture (<see cref="ModelVisionTest"/>): one word in large dark capitals on white, drawn on this PC
/// (never anyone's screen), as a PNG. WPF drawing: call it on a thread with a dispatcher (the desktop's UI thread, or the MCP
/// server's STA thread).</summary>
internal static class VisionTestPicture
{
    internal const int Width = 640, Height = 240;
    private const double Size = 88, Margin = 48;

    internal static BoundedImage Render(string word)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        // WPF can hand back an empty (transparent black) bitmap without an error, for example when the PC is short of memory
        // (issue #1060). A black picture would make a model that sees look blind, so check the drawing and draw it again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (image, pixels) = Draw(word);
            if (IsDrawn(pixels)) return image;
        }
        throw new InvalidOperationException("Windows didn't draw the test picture (it came out blank). Try again in a moment.");
    }

    /// <summary>Whether <paramref name="bgra"/> (32-bit pixels) is the test picture: an opaque white corner, and dark letters on
    /// part of it, never none or most.</summary>
    internal static bool IsDrawn(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length < 4 || bgra[0] != 255 || bgra[1] != 255 || bgra[2] != 255 || bgra[3] != 255) return false;
        var dark = 0;
        for (var i = 0; i < bgra.Length; i += 4)
            if (bgra[i + 3] == 255 && bgra[i] < 128) dark++;
        var share = dark / (bgra.Length / 4.0);
        return share is > 0.005 and < 0.5;
    }

    private static (BoundedImage Image, byte[] Pixels) Draw(string word)
    {
        var text = new FormattedText(word.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Size, Brushes.Black, 1.0);
        // A long word is drawn smaller, so it fits.
        if (text.Width > Width - Margin) text.SetFontSize(Size * (Width - Margin) / text.Width);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            context.DrawText(text, new Point((Width - text.Width) / 2, (Height - text.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return (new(stream.ToArray(), ImageMediaType.Png, Width, Height), pixels);
    }
}
