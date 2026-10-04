using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Providers;

namespace Martlet.Presentation;

/// <summary>Reads a character model's pictures for its theme (linked into Martlet.Mcp.Protocol for character_theme): decodes
/// its textures small with WIC to read its colors, and lays the character's picture and texture sheet side by side in one
/// JPEG for the Thinking model. Pixels stay in memory.</summary>
internal static class CharacterThemeImages
{
    private const int AnalysisEdge = 1024;
    private const int SheetHeight = 768;

    /// <summary>A model's main colors from its textures (a VRM's thumbnail isn't the model, so it is left out).</summary>
    internal static IReadOnlyList<CharacterSwatch> Analyze(IReadOnlyList<CharacterImage> images) =>
        CharacterColors.Analyze(images.Where(i => !i.Thumbnail).Select(i => Decode(i.Bytes, AnalysisEdge)).OfType<CharacterPixels>());

    /// <summary>The model's files read as the renderer reads them: its ID, its pictures and who its files say it is.</summary>
    internal static async Task<(string ModelId, IReadOnlyList<CharacterImage> Images, CharacterIdentity Identity)> ReadModelAsync(
        AvatarRenderer renderer, string modelPath, CancellationToken token)
    {
        var path = BundledLive2D.IsBuiltIn(modelPath) ? BundledLive2D.ModelPath(modelPath) : modelPath;
        var assets = await LocalAvatarFiles.ReadModelAsync(renderer, path, token);
        var entry = renderer == AvatarRenderer.Vrm ? assets[0].Name : Path.GetFileName(path);
        var files = assets.Select(a => Martlet.Core.Characters.CharacterModelLibrary.File(a.Name, a.Bytes)).ToArray();
        var id = Martlet.Core.Characters.CharacterModelLibrary.ModelId(SharedCharacterModels.RendererName(renderer), entry, files);
        var identity = BundledLive2D.IsBuiltIn(modelPath)
            ? new CharacterIdentity(modelPath[BundledLive2D.Prefix.Length..], "Live2D Inc.'s official Cubism sample model")
            : CharacterIdentity.Read(renderer, entry, assets);
        return (id, CharacterColors.Images(renderer, entry, assets), identity);
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

    /// <summary>One JPEG for the Thinking model: the character's picture (a snapshot of it showing, or the model's thumbnail)
    /// on the left and its textures on the right, on a mid-gray backdrop so light and dark parts both show, at most 2048 by
    /// <see cref="SheetHeight"/> pixels. Null when there is nothing to show.</summary>
    internal static BoundedImage? Sheet(byte[]? picture, IReadOnlyList<CharacterImage> images)
    {
        var parts = new List<BitmapSource>();
        if (picture is not null && TryLoad(picture, SheetHeight) is { } shown) parts.Add(shown);
        var textures = images.Where(i => !i.Thumbnail).Select(i => TryLoad(i.Bytes, SheetHeight)).OfType<BitmapSource>().ToArray();
        if (parts.Count == 0 && images.FirstOrDefault(i => i.Thumbnail) is { } thumbnail && TryLoad(thumbnail.Bytes, SheetHeight) is { } own)
            parts.Add(own);
        if (parts.Count == 0 && textures.Length == 0) return null;
        // The textures share the right side: up to two columns of tiles, all the same height.
        var rows = textures.Length <= 1 ? 1 : (int)Math.Ceiling(textures.Length / 2.0);
        var tile = textures.Length == 0 ? 0 : SheetHeight / rows;
        var pictureWidth = parts.Sum(p => (int)Math.Round(p.PixelWidth * (double)SheetHeight / p.PixelHeight));
        var columns = textures.Length <= 1 ? textures.Length : 2;
        var width = Math.Min(BoundedImage.HardMaxEdge, pictureWidth + columns * tile + (parts.Count > 0 && textures.Length > 0 ? 16 : 0));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), null, new Rect(0, 0, width, SheetHeight));
            var x = 0.0;
            foreach (var part in parts)
            {
                var w = part.PixelWidth * (double)SheetHeight / part.PixelHeight;
                context.DrawImage(part, new Rect(x, 0, w, SheetHeight));
                x += w + 16;
            }
            for (var i = 0; i < textures.Length; i++)
            {
                var texture = textures[i];
                var scale = Math.Min((double)tile / texture.PixelWidth, (double)tile / texture.PixelHeight);
                context.DrawImage(texture, new Rect(x + i % 2 * tile, i / 2 * tile, texture.PixelWidth * scale, texture.PixelHeight * scale));
            }
        }
        var bitmap = new RenderTargetBitmap(width, SheetHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        foreach (var quality in new[] { 85, 70, 55, 40 })
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length <= BoundedImage.HardMaxBytes)
                return new(stream.GetBuffer().AsSpan(0, (int)stream.Length), ImageMediaType.Jpeg, width, SheetHeight);
        }
        return null;
    }

    private static BitmapSource? TryLoad(byte[] bytes, int edge)
    {
        try { return Load(bytes, edge); }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or
            IOException or OverflowException or System.Runtime.InteropServices.COMException) { return null; }
    }
}
