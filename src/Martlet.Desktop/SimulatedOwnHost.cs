using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>FIXTURE for checking through MCP how Martlet keeps this PC's own host service on its version, without Docker: with
/// <see cref="Variable"/> set before Martlet starts to an older version (for example <c>0.1.0</c>), or to <c>0.1.0,busy</c>,
/// this PC reads as running a host service of that version (for keeping it current only; the host dashboard and the other
/// update routes still read Docker). Updating it contacts nothing and changes nothing anywhere: it takes 20 seconds (about
/// as long as a real one, so the window can be checked while it runs), the first try with <c>busy</c> stops as the engine
/// does when the host is busy with another change, and afterwards the host service reads as the version it was updated to.</summary>
internal static class SimulatedOwnHost
{
    internal const string Variable = "MARTLET_SIMULATE_OWN_HOST";
    private static readonly TimeSpan UpdateTakes = TimeSpan.FromSeconds(20);
    private static string? version;
    private static bool busy;

    static SimulatedOwnHost()
    {
        var parts = (Environment.GetEnvironmentVariable(Variable) ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (!System.Version.TryParse(parts[0], out var parsed) || parsed.Build < 0 || parsed.Revision >= 0) return;
        version = parsed.ToString(3);
        busy = parts.Skip(1).Contains("busy", StringComparer.OrdinalIgnoreCase);
    }

    internal static bool Active => version is not null;

    internal static OwnHostReading Read() => new(version, Running: true);

    /// <summary>Stands in for building the image and running martlet-host update; returns the engine's exit code.</summary>
    internal static async Task<int> UpdateAsync(string target, IProgress<string> output, CancellationToken token)
    {
        output.Report($"FIXTURE ({Variable}): updating a simulated host service from {version} to {target}; Docker is not used.");
        await Task.Delay(UpdateTakes, token);
        if (busy)
        {
            busy = false;
            output.Report(HostEngineBusy.Marker + "installing ollama (FIXTURE, 1 min so far)");
            return HostEngineBusy.ExitCode;
        }
        version = target;
        output.Report("FIXTURE: Host updated.");
        return 0;
    }
}
