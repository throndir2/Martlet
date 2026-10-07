using System.IO;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

internal sealed partial class AvatarController
{

    /// <summary>A snapshot of the showing character for finding its touch zones (the whole character, where it sat on the
    /// renderer page) and where its drawables or bones are now; null while it is hidden or the picture couldn't be taken.</summary>
    internal async Task<(byte[] Png, RendererPicture Picture, RendererZoneProbe? Probe)?> ZoneSnapshotAsync(CancellationToken token)
    {
        if (renderer is not { HasExited: false } current || profile is null) return null;
        try
        {
            var reply = await current.SendAsync("snapshot", new RendererSnapshot(false), token, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (reply.Kind != "picture") return null;
            var picture = RendererProtocol.Data<RendererPicture>(reply);
            if (picture.Png.Length == 0) return null;
            RendererZoneProbe? probe = null;
            try
            {
                var answer = await current.SendAsync("zones", new { }, token, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                probe = answer.Data.ValueKind == System.Text.Json.JsonValueKind.Object ? RendererProtocol.Data<RendererZoneProbe>(answer) : null;
            }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or TimeoutException)
            {
                ErrorLog.Warn($"Couldn't read where the character's parts are: {error.Message}");
            }
            return (Convert.FromBase64String(picture.Png), picture with { Png = "" }, probe);
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
