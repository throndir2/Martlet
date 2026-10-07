using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Avatar.Hosting;
using Martlet.Providers;

// Also built into Martlet's MCP server (character_touch_zones), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>Touch zone pictures in and out of image files: the renderer's PNG snapshot as pixels, and a composed picture as the
/// PNG (or, when that is too large, JPEG) the vision model is sent. WPF imaging; any thread.</summary>
internal static class TouchZoneImages
{
    /// <summary>The pixels of an image file (straight BGRA32, rows top-down).</summary>
    internal static ZonePixels Decode(byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return new(source.PixelWidth, source.PixelHeight, pixels);
    }

    /// <summary>An opaque picture as the image the vision model gets: a PNG (sharp grid numbers) when it fits the image bound,
    /// else a JPEG at the best quality that fits.</summary>
    internal static BoundedImage Encode(ZonePixels picture)
    {
        var source = BitmapSource.Create(picture.Width, picture.Height, 96, 96, PixelFormats.Bgr32, null, picture.Bgra, picture.Width * 4);
        BitmapEncoder[] encoders = [new PngBitmapEncoder(), .. new[] { 92, 85, 75, 60 }.Select(quality => new JpegBitmapEncoder { QualityLevel = quality })];
        foreach (var encoder in encoders)
        {
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length <= BoundedImage.HardMaxBytes)
                return new(stream.GetBuffer().AsSpan(0, (int)stream.Length), encoder is PngBitmapEncoder ? ImageMediaType.Png : ImageMediaType.Jpeg,
                    picture.Width, picture.Height);
        }
        throw new InvalidOperationException("The picture could not be made small enough.");
    }
}
