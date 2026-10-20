using Martlet.HostClient;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>A host PC follows its own new address (docs/NETWORK.md, "When a computer gets a new address"). When this PC's host
/// service in Docker Desktop is published on an address this PC no longer has (a restart or a new DHCP lease gave it another
/// one), Martlet publishes it on the address this PC has now (martlet-host home), hidden, in Background tasks. Only the
/// published port moves: the host's name (its paired address), key, approval, pairings and roles stay, and your other
/// computers find it at the new address by themselves. It tries once per new address; Set up again stays as the fallback.</summary>
public partial class MainWindow
{
    internal const string HostMoveRun = "Move this host to its new address";
    private Task? hostMove;
    private string? hostMoveTried;

    /// <summary>Whether Martlet is moving this PC's host service to its new address now.</summary>
    private bool HostMoving => hostMove is { IsCompleted: false };

    private void FollowThisPcAddress(LocalHostServiceState state)
    {
        if (closing || store is null || HostMoving || state.Stage != LocalHostServiceStage.Running || state.AddressOnThisPc != false ||
            state.PublishedEverywhere != false || state.Address is null)
            return;
        var target = ThisPcTarget();
        if (!HostSetupCommands.IsPrivate(target.Address) || hostMoveTried == target.Address) return;
        hostMoveTried = target.Address;
        hostMove = MoveThisPcHostAsync(target, state.Address);
    }

    private async Task MoveThisPcHostAsync(HostSetupTarget target, string published)
    {
        var port = System.Net.IPEndPoint.TryParse(published, out var endpoint) ? endpoint.Port : WindowsFirewall.Port;
        var moved = $"{target.Address}:{port}";
        ErrorLog.Info($"This PC's host service: this PC's address is {target.Address} now, but the host service is published on {published}. " +
            "Moving it to the new address by itself; pairings and roles stay.");
        RenderHost();
        var done = await HostRunWindow.RunAsync(this, HostMoveRun, async run =>
        {
            await HostLocal.EnsureImageAsync(target, run);
            run.Status($"Publishing this host on {moved}...");
            var exit = await HostLocal.EngineAsync(target, ["home"], run.Output, run.Token);
            if (exit != 0)
                throw new InvalidOperationException($"The host service couldn't move to {moved} (exit {exit}). The output says why; Set up again " +
                    "on the host dashboard also moves it (pairings and roles stay).");
            return $"This host is published on {moved} now, and your other computers find it there by themselves (pairings and roles stay).";
        }, hidden: true);
        if (closing) return;
        if (done is not null)
        {
            ErrorLog.Info("This PC's host service: " + done);
            // This PC reaches its own host there too; the move is kept with the pairing (MainWindow.Outside).
            if (ThisPcHost() is { } own) HostRoutes.Move(own.Pairing.Origin, own.Pairing.SpkiFingerprint, moved);
        }
        else ErrorLog.Warn($"This PC's host service: couldn't move it to {moved} by itself. Background tasks shows why ({HostMoveRun}).");
        await CheckThisPcHostAsync();
    }
}
