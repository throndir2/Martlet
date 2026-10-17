using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>FIXTURE for checking through MCP how the Devices page shows many computers: with <see cref="Variable"/> set before
/// Martlet starts to a number (1-<see cref="Most"/>), this PC lists that many other Martlet computers that are not real,
/// SIM-001, SIM-002 and so on. Most are members of your Martlet network (every fourth idle), every third is a host PC that runs
/// sim-NNN-host (pair hosts by those IDs in hosts.json to show them as one device with their computer), every seventh uses your
/// hosts outside the network and every eleventh asks to join. Martlet contacts none of them; they only show where this PC lists
/// your other computers.</summary>
internal static class SimulatedDevices
{
    internal const string Variable = "MARTLET_SIMULATE_DEVICES";
    internal const int Most = 500;
    private static IReadOnlyList<MartletComputer>? computers;

    /// <summary>The fixture computers after this PC's real ones, or the real ones alone when <see cref="Variable"/> is not set.</summary>
    internal static IReadOnlyList<MartletComputer> With(IReadOnlyList<MartletComputer> real)
    {
        if (computers is null)
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable(Variable), out var n) ? Math.Clamp(n, 0, Most) : 0;
            computers = Build(count);
            if (count > 0)
                ErrorLog.Info($"Devices: {count} simulated computers from {Variable} (FIXTURE, not real computers); Martlet contacts none of them.");
        }
        return computers.Count == 0 ? real : [.. real, .. computers];
    }

    internal static IReadOnlyList<MartletComputer> Build(int count) => [.. Enumerable.Range(1, count).Select(i =>
    {
        var standing = i % 11 == 0 ? ComputerStanding.Asking : i % 7 == 0 ? ComputerStanding.Outside : ComputerStanding.Member;
        var active = i % 4 != 0;
        var host = i % 3 == 0;
        return new MartletComputer($"desktop-sim-{i:000}", $"SIM-{i:000}", standing,
            active ? "Active now (FIXTURE)." : "Last active yesterday (FIXTURE).", active,
            CheckNumber: standing == ComputerStanding.Asking ? $"{i * 37 % 1000:000} {i * 91 % 1000:000}" : null,
            Through: standing == ComputerStanding.Asking ? "sim-host" : null,
            Role: host ? DeviceRole.Host : DeviceRole.Companion, HostId: host ? $"sim-{i:000}-host" : null);
    })];

    /// <summary>Gives the simulated computers' devices made-up jobs (FIXTURE: none runs anywhere), from none to four, so their
    /// cards and rows have one or two rows of chips, as real ones do. Real devices are left as they are.</summary>
    internal static IReadOnlyList<NetworkNode> WithJobs(IReadOnlyList<NetworkNode> nodes)
    {
        if (computers is not { Count: > 0 }) return nodes;
        return [.. nodes.Select(node =>
        {
            if (!node.Title.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase) || !int.TryParse(node.Title[4..], out var i)) return node;
            var jobs = new[] { ("Listens", "Listening", i % 2 == 0), ("Thinks", "Thinking", i % 4 == 1), ("Speaks", "Speaking", i % 4 == 1),
                    ("Lip-sync", "Lip-sync", i % 5 == 0) }
                .Where(j => j.Item3).Select(j => new HostedRole(j.Item1, j.Item2, "FIXTURE: a made-up job; nothing runs it."));
            return node with { Roles = [.. node.Roles, .. jobs] };
        })];
    }
}
