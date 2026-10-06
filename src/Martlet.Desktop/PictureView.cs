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
}
