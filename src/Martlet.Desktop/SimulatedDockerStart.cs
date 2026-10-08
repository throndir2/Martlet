using System.Globalization;

namespace Martlet.Desktop;

/// <summary>FIXTURE for checking through MCP what Martlet shows while a run starts Docker Desktop (the host dashboard's greyed-out
/// Docker Desktop step, for example on a PC that just became a host PC), without touching Docker Desktop or Windows: with
/// <see cref="Variable"/> set before Martlet starts to a number of seconds (1-600), each start of Docker Desktop
/// (<see cref="HostLocal.EnsureDockerAsync"/>, one start that every run needing it shares) waits that long and then stops as a
/// start that failed. Nothing is started, changed or contacted.</summary>
internal static class SimulatedDockerStart
{
    internal const string Variable = "MARTLET_SIMULATE_DOCKER_START";

    private static readonly TimeSpan? takes =
        int.TryParse(Environment.GetEnvironmentVariable(Variable), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
        seconds is >= 1 and <= 600 ? TimeSpan.FromSeconds(seconds) : null;

    internal static bool Active => takes is not null;

    /// <summary>Stands in for starting Docker Desktop and waiting for its engine.</summary>
    internal static async Task StartAsync(HostRunWindow run)
    {
        run.Status("Waiting for Docker Desktop to start (FIXTURE)...");
        run.Output.Report($"FIXTURE ({Variable}): simulating Docker Desktop starting for {takes!.Value.TotalSeconds:0} seconds. " +
            "Docker Desktop and Windows are not touched.");
        await Task.Delay(takes.Value, run.Token);
        throw new InvalidOperationException($"FIXTURE ({Variable}): the simulated start of Docker Desktop ended without an engine.");
    }
}
