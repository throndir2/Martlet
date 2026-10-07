using System.IO;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Where the desktop gets its <see cref="ILiveGpuHold"/> (live turn first): <see cref="Shared"/> asks a paired host
/// (hosts.json in the data directory, <see cref="WorkSharingRoster.DataDirectory"/>) with signed requests over the host's
/// pinned pairing to keep the graphics cards behind the live routes free of Thinking pool work. A host older than GPU
/// priority, or one this PC isn't paired with, is noted once in the desktop log and the live turn goes on.</summary>
internal static class LiveGpuHolds
{
    internal static ILiveGpuHold Shared { get; } = new HostLiveGpuHold(Connect, ErrorLog.Info);

    // A paired connection to the host, with the pairing secret read from Windows Credential Manager (never copied).
    private static IHostGpuHoldChannel? Connect(string hostId)
    {
        if (WorkSharingRoster.DataDirectory is not { } directory) return null;
        IReadOnlyList<PairedHost> hosts;
        try { hosts = HostRegistry.Load(directory); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
        return hosts.FirstOrDefault(h => h.HostId == hostId) is { } host
            ? HostTextClient.Connect(WorkSharingRoster.TextTarget(host, SelfHostSetup.OllamaRouteId)) : null;
    }
}
