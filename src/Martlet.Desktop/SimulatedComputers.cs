namespace Martlet.Desktop;

/// <summary>FIXTURE for checking the Devices map through MCP with many computers, never real ones: with <see cref="Variable"/>
/// set before Martlet starts to a number (1 to 60), the Devices page shows that many made-up host PCs in your Martlet network
/// (SIM-001, SIM-002, ...). Nothing is paired, contacted or saved; they only add cards to the map and the list.</summary>
internal static class SimulatedComputers
{
    internal const string Variable = "MARTLET_SIMULATE_COMPUTERS";

    internal static IReadOnlyList<MartletComputer> All { get; } =
        int.TryParse(Environment.GetEnvironmentVariable(Variable), out var count) && count is > 0 and <= 60
            ? [.. Enumerable.Range(1, count).Select(i => new MartletComputer($"sim-{i:000}", $"SIM-{i:000}", ComputerStanding.Member,
                "Simulated (FIXTURE).", true, Role: DeviceRole.Host, HostId: $"sim-{i:000}"))]
            : [];
}
