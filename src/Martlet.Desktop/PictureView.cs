using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Martlet.Desktop;

/// <summary>Shows Martlet's pictures (PNG, JPEG or WebP bytes) in WPF: decoded once, at most <paramref name="width"/> pixels wide,
/// frozen so any thread can hand it to the dispatcher.</summary>
internal static class PictureView
{
    internal static ImageSource? Decode(byte[] bytes, int width = 1024)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes, writable: false);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (Martlet.Core.Pictures.PictureImages.Probe(bytes) is { Width: var actual } && actual > width) image.DecodePixelWidth = width;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException)
        {
            ErrorLog.Info($"Pictures: couldn't show a picture ({error.Message}).");
            return null;
        }
    }

    /// <summary>A picture as a JPEG for the camera view's background: at most 1280x720 and <paramref name="maximumBytes"/> (it
    /// travels in one renderer message), lowering the quality and then the size until it fits. Null when it can't be read.</summary>
    internal static byte[]? CameraJpeg(byte[] bytes, int maximumBytes = 180_000)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
            foreach (var (width, height) in new[] { (1280, 720), (960, 540), (640, 360) })
            {
                var scale = Math.Min(1, Math.Min((double)width / frame.PixelWidth, (double)height / frame.PixelHeight));
                BitmapSource sized = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
                foreach (var quality in new[] { 85, 70, 55 })
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                    encoder.Frames.Add(BitmapFrame.Create(sized));
                    using var output = new MemoryStream();
                    encoder.Save(output);
                    if (output.Length <= maximumBytes) return output.ToArray();
                }
            }
            return null;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException)
        {
            ErrorLog.Info($"Pictures: couldn't use a picture as the camera background ({error.Message}).");
            return null;
        }
    }
}
