using System.Runtime.InteropServices;
using Martlet.Companion.Platform;
using Martlet.Platform.Linux.Native;
using SkiaSharp;

namespace Martlet.Platform.Linux.Capture;

/// <summary>Turns raw 32-bit screen pixels into one look: opaque, scaled so the long edge fits, JPEG-encoded in memory.
/// Pixels never touch the disk; the working buffers are cleared afterwards.</summary>
internal static unsafe class FrameEncoder
{
    /// <summary>The size a <paramref name="width"/> x <paramref name="height"/> picture gets when its long edge is at most
    /// <paramref name="maxLongEdge"/> (never enlarged).</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxLongEdge)
    {
        var longEdge = Math.Max(width, height);
        if (maxLongEdge <= 0 || longEdge <= maxLongEdge) return (width, height);
        var scale = (double)maxLongEdge / longEdge;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Byte positions of red, green and blue inside one little-endian 32-bit pixel of <paramref name="format"/>.</summary>
    internal static (int R, int G, int B) Channels(SpaVideoFormat format) => format switch
    {
        SpaVideoFormat.BGRx or SpaVideoFormat.BGRA => (2, 1, 0),
        SpaVideoFormat.RGBx or SpaVideoFormat.RGBA => (0, 1, 2),
        SpaVideoFormat.xRGB or SpaVideoFormat.ARGB => (1, 2, 3),
        SpaVideoFormat.xBGR or SpaVideoFormat.ABGR => (3, 2, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static ScreenShot Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, SpaVideoFormat format, ScreenCaptureRequest request)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels.Length < (long)stride * (height - 1) + width * 4)
            throw new ArgumentException("The screen frame is smaller than its size says.");
        var (r, g, b) = Channels(format);
        using var source = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var target = new Span<byte>((void*)source.GetPixels(), source.RowBytes * height);
        try
        {
            for (var y = 0; y < height; y++)
            {
                var from = pixels.Slice(y * stride, width * 4);
                var to = target.Slice(y * source.RowBytes, width * 4);
                for (var x = 0; x < width * 4; x += 4)
                {
                    to[x] = from[x + b];
                    to[x + 1] = from[x + g];
                    to[x + 2] = from[x + r];
                    to[x + 3] = 255;
                }
            }
            var (outWidth, outHeight) = Fit(width, height, request.MaxLongEdge);
            using var scaled = outWidth == width && outHeight == height
                ? null
                : source.Resize(new SKImageInfo(outWidth, outHeight, SKColorType.Bgra8888, SKAlphaType.Opaque),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            var picture = scaled ?? source;
            using var image = SKImage.FromBitmap(picture);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(request.JpegQuality, 1, 100))
                ?? throw new InvalidOperationException("The screen image could not be encoded.");
            if (scaled is not null) Clear(scaled);
            return new ScreenShot(data.ToArray(), outWidth, outHeight, DateTimeOffset.UtcNow);
        }
        finally { target.Clear(); }
    }

    /// <summary>Re-encodes a picture file the system made (Screenshot portal PNG) as one look.</summary>
    public static ScreenShot? EncodeFile(byte[] encoded, ScreenCaptureRequest request)
    {
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null) return null;
        using var bgra = decoded.ColorType == SKColorType.Bgra8888 ? null : decoded.Copy(SKColorType.Bgra8888);
        var picture = bgra ?? decoded;
        var span = new ReadOnlySpan<byte>((void*)picture.GetPixels(), picture.RowBytes * picture.Height);
        try { return Encode(span, picture.Width, picture.Height, picture.RowBytes, SpaVideoFormat.BGRx, request); }
        finally { Clear(decoded); if (bgra is not null) Clear(bgra); }
    }

    private static void Clear(SKBitmap bitmap)
    {
        var pointer = bitmap.GetPixels();
        if (pointer != IntPtr.Zero) new Span<byte>((void*)pointer, bitmap.RowBytes * bitmap.Height).Clear();
    }

    internal static byte[] Copy(byte* data, int length)
    {
        var copy = GC.AllocateUninitializedArray<byte>(length);
        Marshal.Copy((IntPtr)data, copy, 0, length);
        return copy;
    }
}
