using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Providers;

namespace Martlet.Presentation;

/// <summary>Reads a character model's pictures for its theme (linked into Martlet.Mcp.Protocol for character_theme): decodes
/// its textures small with WIC to read its colors. Pixels stay in memory.</summary>
internal static class CharacterThemeImages
{
    private const int AnalysisEdge = 1024;

    /// <summary>A model's main colors from its textures (a VRM's thumbnail isn't the model, so it is left out).</summary>
    internal static IReadOnlyList<CharacterSwatch> Analyze(IReadOnlyList<CharacterImage> images) =>
        CharacterColors.Analyze(images.Where(i => !i.Thumbnail).Select(i => Decode(i.Bytes, AnalysisEdge)).OfType<CharacterPixels>());

    /// <summary>The model's files read as the renderer reads them: its ID and its pictures.</summary>
    internal static async Task<(string ModelId, IReadOnlyList<CharacterImage> Images)> ReadModelAsync(
        AvatarRenderer renderer, string modelPath, CancellationToken token)
    {
        var path = BundledLive2D.IsBuiltIn(modelPath) ? BundledLive2D.ModelPath(modelPath) : modelPath;
        var assets = await LocalAvatarFiles.ReadModelAsync(renderer, path, token);
        var entry = renderer == AvatarRenderer.Vrm ? assets[0].Name : Path.GetFileName(path);
        var files = assets.Select(a => Martlet.Core.Characters.CharacterModelLibrary.File(a.Name, a.Bytes)).ToArray();
        var id = Martlet.Core.Characters.CharacterModelLibrary.ModelId(SharedCharacterModels.RendererName(renderer), entry, files);
        return (id, CharacterColors.Images(renderer, entry, assets));
    }

    /// <summary>The picture decoded to BGRA, at most <paramref name="edge"/> pixels on its longer side, or null when it isn't
    /// a picture WIC reads.</summary>
    internal static CharacterPixels? Decode(byte[] bytes, int edge, double weight = 1)
    {
        try
        {
            var source = Load(bytes, edge);
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            return new(pixels, converted.PixelWidth, converted.PixelHeight, weight);
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or
            IOException or OverflowException or System.Runtime.InteropServices.COMException) { return null; }
    }

    // WIC scales while it decodes, so an 8192-pixel texture never needs its full size in memory.
    private static BitmapSource Load(byte[] bytes, int edge)
    {
        int width, height;
        using (var probe = new MemoryStream(bytes, writable: false))
        {
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            (width, height) = (frame.PixelWidth, frame.PixelHeight);
        }
        var image = new BitmapImage();
        image.BeginInit();
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes, writable: false);
        if (Math.Max(width, height) > edge)
        {
            if (width >= height) image.DecodePixelWidth = edge;
            else image.DecodePixelHeight = edge;
        }
        image.EndInit();
        image.Freeze();
        return image;
    }
}
