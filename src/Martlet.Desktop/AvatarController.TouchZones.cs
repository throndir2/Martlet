using System.IO;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

internal sealed partial class AvatarController
{

    /// <summary>A picture of <paramref name="selected"/>'s character for finding its touch zones, drawn by a renderer of its own
    /// that never shows on screen and never animates: the whole character in its rest pose (no idle motion, eyes open, looking
    /// ahead), framed whole on a page of the overlay's shape (zoomed out when the model draws past its own canvas), up to
    /// <see cref="RendererSnapshot.MaximumEdge"/> pixels, with where it sat on that page and where its drawables or bones were
    /// (both with the character framed whole). It works whether or not the character shows and never moves the one on the
    /// desktop. Null when the picture couldn't be taken (the desktop log says why).</summary>
    internal async Task<(byte[] Png, RendererPicture Picture, RendererZoneProbe? Probe)?> ZoneSnapshotAsync(AvatarProfile selected,
        CancellationToken token)
    {
        IAvatarRenderer? still = null;
        try
        {
            var snapshot = await LocalAvatarFiles.SnapshotAsync(selected, token);
            still = createStillRenderer();
            await still.StartAsync(selected with { ResourceRevision = snapshot.Revision }, snapshot.Revision, null, true, token);
            var reply = await still.SendAsync("snapshot", new RendererSnapshot(false, RendererSnapshot.MaximumEdge, Whole: true), token,
                TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            if (reply.Kind != "picture") return null;
            var picture = RendererProtocol.Data<RendererPicture>(reply);
            if (picture.Png.Length == 0) return null;
            return (Convert.FromBase64String(picture.Png), picture with { Png = "", Probe = null }, picture.Probe);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or TimeoutException or
            ObjectDisposedException or OperationCanceledException or FormatException or System.Text.Json.JsonException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or Martlet.Core.Contracts.ContractException)
        {
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            ErrorLog.Warn($"Couldn't take a picture of the character for its touch zones: {error.Message}");
            return null;
        }
        finally
        {
            if (still is not null)
                try { await still.DisposeAsync(); }
                catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or
                    System.ComponentModel.Win32Exception)
                { ErrorLog.Warn($"The renderer of the touch zones picture didn't close cleanly: {error.Message}"); }
        }
    }
}
