using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Motion;

namespace Martlet.Desktop;

/// <summary>
/// The work Companion › Motion (an optional extra, docs/MOTION_HOST.md#the-motion-pool) shows: making the Motion list once from the
/// older choice, a computer joining the list when the motion role is added there, and setting the <c>motion</c> host role
/// (Tencent's HY-Motion 1.0) up and waiting for it. The page itself is <see cref="MotionPage"/> in Pages\Motion
/// (MainWindow.MotionPage.cs gives it its services).
/// </summary>
public partial class MainWindow
{
    internal const string MotionThisPc = "this-pc";

    /// <summary>The computer Motion is being set up on now (a host ID, or <see cref="MotionThisPc"/>), or null.</summary>
    private string? motionPendingHost;
    /// <summary>Why the last setup failed, by computer.</summary>
    private readonly Dictionary<string, string> motionFailures = new(StringComparer.Ordinal);

    /// <summary>Makes the motion list once from the older choice (motion.json: one computer, automatic or off) and the computers
    /// that run Motion (every one in the shared plan, and those this PC's checks saw), so the choice isn't lost. The list is
    /// shared, so it takes every companion PC's motion computers, not only this PC's pairings, and each PC then uses the ones it is
    /// paired with. Hosts a friend shares with this PC are left out. Automatic while no computer runs Motion makes no list, so
    /// adding the role somewhere still turns motion on.</summary>
    private void EnsureMotionPool()
    {
        if (store is null || WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Motion) is not null) return;
        var paired = MotionClient.Paired(store.DataDirectory).Select(h => h.HostId).ToArray();
        var runners = MotionPool.Runners(ClusterSync.LoadPlan(store.DataDirectory), null, ThisPcHost()?.HostId)
            .Concat(paired.Where(id => hostChecks.GetValueOrDefault(id)?.Offers?.ContainsKey(HostRoles.Motion) == true))
            .Where(id => FindSharedHost(id) is null)
            .Distinct(StringComparer.Ordinal).ToArray();
        var older = MotionClient.Settings(store.DataDirectory);
        if (MotionPool.FirstList(older, runners) is not { } list || !PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Motion, list)) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: the Motion list was made from the motion choice ({older.Describe()}): " +
            (list.Members.Count == 0 ? "empty (off)." : string.Join(", ", list.Members.Select(m => m.Name)) + (list.NoneOn ? " (all off: motion was off)." : ".")));
    }

    /// <summary>After the motion role was added on <paramref name="hostId"/> from anywhere (Devices, a fix card), that computer
    /// joins the Motion list at its end when the list exists and doesn't have it, so adding the role turns motion on there.</summary>
    private void JoinMotionPool(string hostId)
    {
        if (store is null || WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Motion) is not { } list) return;
        var own = ThisPcHost()?.HostId;
        if (list.Members.Any(m => m.HostId == hostId || m.Kind == PoolMemberKind.ThisPc && hostId == own)) return;
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Motion, list.With(PoolMember.Computer(hostId)))) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: {hostId} runs Motion now, so it joined the Motion list.");
        if (!closing && openTab == CompanionTab.Motion) Dispatcher.InvokeAsync(RenderTab);
    }

    /// <summary>Why this PC can't make motions: no NVIDIA graphics card, or one with less than 24 GB; null when it can or Martlet
    /// hasn't read this PC's hardware yet.</summary>
    private string? ThisPcCannotMove()
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) return null;
        if (machine.ArmRefusal("motion") is { } arm) return arm;
        var nvidia = machine.Gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();
        if (nvidia is null)
            return $"Needs an NVIDIA graphics card with 24 GB+; this PC has {(machine.Gpus.Count == 0 ? "none" : string.Join(", ", machine.Gpus.Select(g => g.Describe())))}.";
        return nvidia.MemoryGb is { } gb && gb < 22.5 ? $"Needs an NVIDIA graphics card with 24 GB+; this PC has {nvidia.Describe()}." : null;
    }

    /// <summary>Sets the motion role up on <paramref name="host"/> (null: this PC) through the same martlet-host add as every role:
    /// its own dialog shows the model (Lite, or the standard model on a card with 26 GB+) and Tencent's license terms, and the
    /// run window shows the progress. This PC without its host service sets that up first, in the same window.</summary>
    private async Task SetUpMotionAsync(PairedHost? host, string key)
    {
        if (store is null || setupService is null || closing) return;
        if (motionPendingHost is not null)
        {
            ActionText.Text = $"Motion is still being set up on {(motionPendingHost == MotionThisPc ? "this PC" : motionPendingHost)}; " +
                "its window shows progress, so set up the next one when it's done.";
            return;
        }
        var thisPc = ThisPcHost();
        var target = host ?? thisPc;
        var where = host is null ? "this PC" : host.HostId;
        motionFailures.Remove(key);
        motionPendingHost = key;
        RenderTab();
        try
        {
            if (target is not null)
            {
                var done = await RunHostActionAsync(target, HostRoles.Get(HostRoles.Motion).Add);
                if (closing) return;
                if (done is null) motionFailures[key] = $"Setting it up on {where} stopped; its run window has details.";
                else if (await WaitForMotionAsync(target, lifetime.Token) is { } why)
                    motionFailures[key] = $"Setup finished, but Martlet can't see Motion on {where} yet ({why}); check it in a minute.";
                else ActionText.Text = $"Motion is ready on {where}, and Martlet makes body motions there for VRM characters.";
                return;
            }
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                await InstallRoleHereAsync(run, pc, new(HostRoles.Motion, "Motion", new Dictionary<string, string>(),
                    "Installing Motion on this PC (about 20 GB of downloads)...",
                    exit => $"Installing Motion stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped)}. The output has details.")
                    { Questions = LocalRoleQuestions.Role, Stopped = why => stopped ??= why });
                run.Status("Checking Motion on this PC...");
                if (await WaitForMotionAsync(pc, run.Token) is { } why)
                    throw new InvalidOperationException(stopped = $"Setup finished, but Martlet can't see Motion yet ({why}); check this PC in a minute.");
                return "Motion is ready on this PC.";
            }
            // No host service on this PC yet (with one, the branch above set the role up): it is set up first, in the same window.
            var status = await RunRoleSetupHereAsync(null, "Set up Motion on this PC", "Set up Motion on this PC", Continue,
                () => "Motion wasn't set up; the run window has details.");
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith("Motion is ready", StringComparison.Ordinal)) motionFailures[key] = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or HostConnectionException || ClusterSync.IsHostFailure(error))
        {
            motionFailures[key] = error.Message;
            ActionText.Text = error.Message;
        }
        finally
        {
            motionPendingHost = null;
            if (!closing && openTab is not null) RenderTab();
        }
    }

    /// <summary>After martlet-host added the role, checks <paramref name="host"/> (about a minute) until its gateway offers the
    /// motion route and its service says it is provisioned. Returns null once it does, else what it saw last.</summary>
    private Task<string?> WaitForMotionAsync(PairedHost host, CancellationToken token) => WaitForRoleAsync(host, async check =>
    {
        if (check.Routes?.Any(r => r.RouteId == HostConnection.MotionRouteId) != true || store is null) return new(check.Text);
        using var maker = new HostMotionMaker(store.DataDirectory, host.HostId);
        var availability = await maker.GetAvailabilityAsync(token);
        return availability.Available ? null : new(availability.Reason ?? check.Text);
    }, token);
}
