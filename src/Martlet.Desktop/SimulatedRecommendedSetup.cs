using System.Globalization;
using Martlet.Core.Cluster;
using Martlet.Core.Planning;

namespace Martlet.Desktop;

/// <summary>FIXTURE for checking Reconfigure through MCP without your computers: with <see cref="Variable"/> set to a number of
/// seconds (1-600) before Martlet starts, Home's Recommended setup plans the built-in four-computer network
/// (<see cref="RecommendedSetupInputs.Fixture"/>, the one recommended_setup_status's fixture "network" plans) instead of yours,
/// and Reconfigure applies it to simulated computers: each role change takes that many seconds and writes FIXTURE lines in its
/// run window. Nothing is installed, contacted, saved or shared: who does each job, Sharing work and this PC's own routes only
/// say what they would do, and the run shows on this PC's Home but isn't published to your other computers.</summary>
internal static class SimulatedRecommendedSetup
{
    internal const string Variable = "MARTLET_SIMULATE_RECOMMENDED_SETUP";

    /// <summary>How long each simulated role change takes; null when the fixture is off.</summary>
    internal static TimeSpan? StepTakes { get; } =
        int.TryParse(Environment.GetEnvironmentVariable(Variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
        seconds is >= 1 and <= 600 ? TimeSpan.FromSeconds(seconds) : null;

    internal static bool Active => StepTakes is not null;

    /// <summary>With <see cref="Variable"/> set, "offline" here plans <see cref="RecommendedSetupInputs.OfflineFixture"/> (two
    /// hosts away for hours, so nobody can think) instead of the four-computer network.</summary>
    internal const string NetworkVariable = "MARTLET_SIMULATE_RECOMMENDED_SETUP_NETWORK";

    /// <summary>The FIXTURE network Home's Recommended setup plans while <see cref="Active"/>.</summary>
    internal static SetupSources Sources(DateTimeOffset now) =>
        string.Equals(Environment.GetEnvironmentVariable(NetworkVariable), "offline", StringComparison.OrdinalIgnoreCase)
            ? RecommendedSetupInputs.OfflineFixture(now)
            : RecommendedSetupInputs.Fixture(now);

    /// <summary>The simulated computers a run changes. <paramref name="publish"/> shows the run on this PC only.</summary>
    internal sealed class Targets(string device, Action<SetupRun> publish) : ISetupTargets
    {
        public string Device => device;

        public SetupReach Reach(string machineId) => SetupReach.Yes;

        public MachineSpecs? Specs(string machineId) => null;

        public string RoleName(string kind) => HostRoles.Names([kind]);

        public Task<SetupRoleNeeds> DescribeAsync(string machineId, string roleKind, CancellationToken cancel) =>
            Task.FromResult(new SetupRoleNeeds(RoleName(roleKind),
                $"FIXTURE ({Variable}): {RoleName(roleKind)} on {machineId} is simulated, so nothing is downloaded or installed."));

        public async Task<SetupStepResult> ChangeRoleAsync(SetupRoleCommand command, IProgress<string> progress, CancellationToken cancel)
        {
            var name = RoleName(command.RoleKind);
            progress.Report($"FIXTURE: {(command.Add ? "installing" : "removing")} {name} on {command.MachineId} takes " +
                $"{StepTakes!.Value.TotalSeconds:0} s here; nothing is installed.");
            await Task.Delay(StepTakes.Value, cancel);
            progress.Report($"FIXTURE: {name} {(command.Add ? "runs on" : "was removed from")} {command.MachineId}.");
            return SetupStepResult.Done($"{name} {(command.Add ? "runs on" : "was removed from")} {command.MachineId} (FIXTURE: nothing changed).");
        }

        public Task<SetupStepResult> AssignJobAsync(string job, string? hostId, bool off, CancellationToken cancel) =>
            Task.FromResult(SetupStepResult.Done($"FIXTURE: {ClusterSync.Title(job)} would go to {ClusterSync.Who(job, hostId, off)} " +
                "on all your computers. Nothing was saved."));

        public Task<SetupStepResult> ShareAsync(string job, string machineId, bool join, CancellationToken cancel) =>
            Task.FromResult(SetupStepResult.Done($"FIXTURE: {WorkSharingJobs.Title(job)} would {(join ? "" : "no longer ")}go to " +
                $"{machineId} when its computer is busy. Nothing was saved."));

        public Task<SetupRouteReading> ReadRouteAsync(string job, string optionId, CancellationToken cancel) =>
            Task.FromResult(new SetupRouteReading(SetupStepVerdict.Ready,
                $"FIXTURE: this PC would use {FootprintCatalog.Default.Find(optionId)?.DisplayName ?? optionId} for {job}."));

        public Task<SetupStepResult> UseRouteAsync(string job, string optionId, CancellationToken cancel) =>
            Task.FromResult(SetupStepResult.Done($"FIXTURE: this PC would use {FootprintCatalog.Default.Find(optionId)?.DisplayName ?? optionId} " +
                $"for {job}. Nothing was saved."));

        public Task<IReadOnlyDictionary<string, string>> CheckAsync(CancellationToken cancel) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task PublishAsync(SetupRun run, CancellationToken cancel)
        {
            publish(run);
            return Task.CompletedTask;
        }

        public void Accepted(SetupPreflightItem item) =>
            ErrorLog.Info($"FIXTURE ({Variable}): Reconfigure accepted the simulated terms of {item.Change.Summary}");
    }
}
