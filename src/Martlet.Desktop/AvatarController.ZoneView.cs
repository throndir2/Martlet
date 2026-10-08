using System.IO;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

internal sealed partial class AvatarController
{
    private readonly object zoneViewGate = new();
    private Func<string?, RendererZoneView?>? zoneViewFor;
    // The zones last sent (or being sent) to which renderer, as they went on the pipe, so the same zones aren't sent again.
    private (IAvatarRenderer Renderer, string Sent)? zoneViewSent;

    /// <summary>Where the touch zones the showing character gets come from (<see cref="RendererZoneView"/>): the zones saved for
    /// the model at a path, or null.</summary>
    internal void UseZoneView(Func<string?, RendererZoneView?> provider) => Volatile.Write(ref zoneViewFor, provider);

    /// <summary>Gives the showing character its touch zones (Touch zones › Show the zones on the character draws them over it)
    /// unless it already has them; nothing while it is hidden.</summary>
    internal Task SendZoneViewAsync(RendererZoneView view, CancellationToken token) =>
        renderer is { HasExited: false } current && profile is not null ? SendZoneViewAsync(current, view, token) : Task.CompletedTask;

    // After each model load, and whenever the zones or Show the zones on the character change. A renderer that can't take them is
    // noted in the log and never fails the character: it only shows and reads no zones.
    private async Task SendZoneViewAsync(IAvatarRenderer target, RendererZoneView? view, CancellationToken token)
    {
        view ??= new(false, []);
        var sending = System.Text.Json.JsonSerializer.Serialize(view, RendererProtocol.Json);
        lock (zoneViewGate)
        {
            if (zoneViewSent is { } sent && ReferenceEquals(sent.Renderer, target) && sent.Sent == sending) return;
            zoneViewSent = (target, sending);
        }
        try
        {
            await target.SendAsync("zoneview", view, token).ConfigureAwait(false);
            ErrorLog.Info(view.Areas.Length == 0 ? "The character has no touch zones to show."
                : $"The character got its touch zones: {view.Areas.Length} area{(view.Areas.Length == 1 ? "" : "s")} of " +
                  $"{view.Areas.Select(a => a.Zone).Distinct(StringComparer.Ordinal).Count()} zones{(view.Draw ? ", drawn over it" : "")}.");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or TimeoutException or
            ObjectDisposedException or OperationCanceledException or System.Text.Json.JsonException)
        {
            lock (zoneViewGate) if (zoneViewSent is { } sent && ReferenceEquals(sent.Renderer, target)) zoneViewSent = null;
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            ErrorLog.Info($"The character's renderer didn't take its touch zones ({error.Message}); it shows none.");
        }
    }

    // A new renderer has no zones yet.
    private void ForgetZoneView()
    {
        lock (zoneViewGate) zoneViewSent = null;
    }
}
