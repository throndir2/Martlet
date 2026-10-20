using System.IO;
using System.Windows.Media.Imaging;

namespace Martlet.Desktop;

/// <summary>The pictures that Companion › Touch › Touch zones and Companion › Eyes show: each picture file decoded once at about
/// the size it shows, and the folder of the pictures that Thinking saw, opened in Explorer.</summary>
internal static class PictureFiles
{
    // Pictures decoded once at about the size they show, kept while their file stays the same (Detect again and Measure the face
    // write new pictures under the same names), so drawing a page again doesn't decode them again. On the UI thread.
    private static readonly Dictionary<(string Path, int Height, int Width), (DateTime Written, long Length, BitmapImage Picture)> decoded = [];

    /// <summary>A picture file decoded at about the size it shows (the snapshot can be 2048 pixels tall).</summary>
    internal static BitmapImage At(string path, int decodeHeight = (int)(TouchZonesPage.PictureHeight * 2), int decodeWidth = 0)
    {
        var file = new FileInfo(path);
        var (key, written, length) = ((path, decodeHeight, decodeWidth), file.LastWriteTimeUtc, file.Length);
        if (decoded.TryGetValue(key, out var kept) && kept.Written == written && kept.Length == length) return kept.Picture;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // The file's own changes are followed above, not by WPF's image cache.
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (decodeHeight > 0) bitmap.DecodePixelHeight = decodeHeight;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        if (decoded.Count >= 4) decoded.Clear();
        decoded[key] = (written, length, bitmap);
        return bitmap;
    }

    /// <summary>Opens <paramref name="folder"/> in Explorer.</summary>
    internal static void OpenFolder(string folder)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
