using System.IO;
using Martlet.Core.Cluster;
using Martlet.Core.Reading;

namespace Martlet.Desktop;

/// <summary>Companion › Reading's list on this PC (<see cref="ReadingPool"/>, pools-local.json), from local files only: read again
/// only when a file changed (<see cref="WorkSharingRoster"/>), never the network. Until the page saves one, the list made from the
/// older choice (reading.json): for the Reading role, the computer chosen, then the other computers the shared plan says run
/// it (in the order a read tried them before lists).</summary>
internal static class ReadingList
{
    /// <summary>The saved list, or the one made from the older choice.</summary>
    internal static PoolList Load(string? directory) => WorkSharingRoster.Pool(directory, PoolAreas.Reading) ?? Migrate(directory);

    /// <summary>The list made from reading.json (not saved).</summary>
    internal static PoolList Migrate(string? directory)
    {
        var choice = ReadingSettings.Load(directory);
        if (choice.Place != ReadingPlace.Host || directory is null) return ReadingPool.FromChoice(choice, null, []);
        var others = WorkSharingRoster.Order(directory, WorkSharingJobs.Reading, ReadingPool.Role, null, choice.HostId)
            .Select(p => p.Host?.HostId).OfType<string>().Where(id => id != choice.HostId).ToList();
        // No computer chosen and none in the plan: every computer of the owner's own, as reads tried them before.
        if (choice.HostId is null && others.Count == 0) others = [.. WorkSharingRoster.Hosts(directory).Where(h => !h.Shared).Select(h => h.HostId)];
        return ReadingPool.FromChoice(choice, OwnHost(directory), others);
    }

    /// <summary>Saves the list made from the older choice when this PC has none yet, so the page shows what reads; false when
    /// it couldn't be saved.</summary>
    internal static bool Ensure(string directory)
    {
        if (WorkSharingRoster.Pool(directory, PoolAreas.Reading) is not null) return true;
        var list = Migrate(directory);
        if (!PoolSettings.SaveFor(directory, PoolAreas.Reading, list)) return false;
        WorkSharingRoster.Forget();
        ErrorLog.Info($"Pools: made the Reading list from the older choice: {(list.Members.Count == 0 ? "empty (off)" : string.Join(", ", list.Members.Select(m => m.Key)))}.");
        return true;
    }

    /// <summary>This PC's own host service: the one the main window found, else a pairing saved as this PC's (Docker Desktop).</summary>
    internal static string? OwnHost(string? directory) => WorkSharingRoster.OwnHostId ??
        (directory is null ? null : WorkSharingRoster.Hosts(directory).FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId);

    /// <summary>The places this PC tries for a read, first to last, each with its pairing (null: Windows OCR on this PC).</summary>
    internal static IReadOnlyList<(ReadingTarget Target, PairedHost? Host)> Targets(string? directory)
    {
        var hosts = directory is null ? [] : WorkSharingRoster.Hosts(directory);
        return [.. ReadingPool.Targets(Load(directory), WorkSharingRoster.Device, OwnHost(directory), id => hosts.Any(h => h.HostId == id))
            .Select(t => (t, t.HostId is null ? null : hosts.First(h => h.HostId == t.HostId)))];
    }

    /// <summary>The file a change to the list is saved in, for watching to follow it.</summary>
    internal static string? File(string? directory) => directory is null ? null : Path.Combine(directory, PoolSettings.File(PoolAreas.Reading.Shared));
}
