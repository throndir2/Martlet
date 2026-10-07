using System.IO;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

internal sealed partial class AvatarController
{

    /// <summary>A snapshot of the showing character for finding its touch zones: the whole character (framed whole for a moment:
    /// no zoom, no pan) at the size it shows, up to <see cref="RendererSnapshot.MaximumEdge"/> pixels, where it sat on the renderer
    /// page, and where its drawables or bones were in that framing; null while it is hidden or the picture couldn't be taken.</summary>
    internal async Task<(byte[] Png, RendererPicture Picture, RendererZoneProbe? Probe)?> ZoneSnapshotAsync(CancellationToken token)
    {
        if (renderer is not { HasExited: false } current || profile is null) return null;
        try
        {
            var reply = await current.SendAsync("snapshot", new RendererSnapshot(false, RendererSnapshot.MaximumEdge, Whole: true), token,
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (reply.Kind != "picture") return null;
            var picture = RendererProtocol.Data<RendererPicture>(reply);
            if (picture.Png.Length == 0) return null;
            return (Convert.FromBase64String(picture.Png), picture with { Png = "", Probe = null }, picture.Probe);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or TimeoutException or
            ObjectDisposedException or OperationCanceledException or FormatException or System.Text.Json.JsonException)
        {
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            ErrorLog.Warn($"Couldn't take a picture of the character for its touch zones: {error.Message}");
            return null;
        }
    }
}
