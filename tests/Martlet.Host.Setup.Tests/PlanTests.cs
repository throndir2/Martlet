using Martlet.Host.Inventory;

namespace Martlet.Host.Setup.Tests;

public sealed class PlanTests
{
    [Fact]
    public void Current_h01_h02_facts_build_exact_bounded_blocked_proposal()
    {
        var host = SetupTestData.HostReport();
        var artifacts = SetupTestData.ArtifactReport();
        var clock = new MutableTimeProvider(host.CreatedAt.AddMinutes(1));
        var configuration = new SetupConfiguration("candidate-1", [SetupRole.Llm, SetupRole.Tts]);

        var plan = new SetupPlanBuilder(clock).Build(configuration, host, artifacts);

        Assert.Equal(SetupPlanDisposition.Blocked, plan.Disposition);
        Assert.Equal("h05a-single-host-v1", plan.Id);
        Assert.Equal(configuration.Fingerprint, plan.ConfigurationFingerprint);
        Assert.Equal(6, plan.Steps.Length);
        Assert.Equal([1, 2, 3, 4, 5, 6], plan.Steps.Select(step => step.Order));
        Assert.All(plan.Steps, step => Assert.False(step.RollbackAvailable));
        Assert.All(plan.Steps, step => Assert.Null(step.Command));
        Assert.Equal(2, plan.Steps.Count(step => step.Execution == SetupStepExecution.ObservationOnly));
        Assert.Equal(4, plan.Steps.Count(step => step.Execution == SetupStepExecution.Deferred));
        Assert.Equal(2_836_353_046L, plan.DiskBudget.KnownListedPayloadBytes);
        Assert.Equal(5_672_706_092L, plan.DiskBudget.ReservationFloorBytes);
        Assert.False(plan.DiskBudget.FitsObservedDisk);
        Assert.Null(plan.DiskBudget.HostAvailableBytes);
        Assert.False(plan.DiskBudget.Complete);
        Assert.Contains(plan.ExpectedResources, resource =>
            resource is { Id: "private-gateway-port", Value: "tcp/7443", MutationDeferred: true });
        Assert.Contains(plan.ExpectedResources, resource =>
            resource is { Id: "gateway-service", Value: "martlet-gateway.service", MutationDeferred: true });
        Assert.Contains(plan.ExpectedResources, resource =>
            resource.Id == "artifact-manifest-input" &&
            resource.Value == "sha256:" + artifacts.DocumentSha256 &&
            resource.DeclaredBytes == 2_836_353_046L);
        Assert.Contains(plan.ExpectedResources, resource => resource is { Id: "role-llm", Value: "ollama-llm" });
        Assert.Contains(plan.ExpectedResources, resource => resource is { Id: "role-tts", Value: "f5-tts" });
        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.HostFactsNotLive);
        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.HostPrerequisitesIncomplete);
        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.GatewayPortConflict);
        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.ArtifactCandidateIncomplete);
        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.DiskBudgetIncomplete);
        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.MutationDeferred);
        Assert.DoesNotContain(plan.ExpectedResources, resource =>
            resource.Kind == SetupExpectedResourceKind.File &&
            resource.Value.Contains("compose", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Steps, step => step.Description.Contains("download now", StringComparison.OrdinalIgnoreCase));
        Assert.False(plan.RollbackAvailable);
        Assert.Equal(64, plan.Fingerprint.Length);
    }

    [Fact]
    public void Plan_is_deterministic_and_owns_configuration_collections()
    {
        var host = SetupTestData.HostReport();
        var roles = new[] { SetupRole.Tts, SetupRole.Llm };
        var configuration = new SetupConfiguration("candidate-1", roles);
        roles[0] = SetupRole.Llm;
        var clock = new MutableTimeProvider(host.CreatedAt.AddMinutes(1));
        var builder = new SetupPlanBuilder(clock);

        var first = builder.Build(configuration, host, SetupTestData.ArtifactReport());
        var second = builder.Build(configuration, host, SetupTestData.ArtifactReport());

        Assert.Equal(new[] { SetupRole.Llm, SetupRole.Tts }, configuration.Roles.ToArray());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.HostFactsFingerprint, second.HostFactsFingerprint);
        Assert.Equal(first.ArtifactFactsFingerprint, second.ArtifactFactsFingerprint);
    }

    [Fact]
    public void Stale_host_facts_are_explicitly_blocked()
    {
        var host = SetupTestData.HostReport();
        var builder = new SetupPlanBuilder(new MutableTimeProvider(host.CreatedAt.AddMinutes(16)));

        var plan = builder.Build(new SetupConfiguration("candidate-1", [SetupRole.Llm]),
            host, SetupTestData.ArtifactReport("ollama-llm"));

        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.HostFactsStale);
    }

    [Fact]
    public void Plan_expiry_is_capped_by_oldest_required_observation()
    {
        var host = SetupTestData.HostReport("inventory", DoctorScope.Inventory);
        var oldest = host.Probes.Where(probe => probe.Required)
            .Min(probe => probe.ObservedAt!.Value);
        var now = oldest.AddMinutes(14);
        var plan = new SetupPlanBuilder(new MutableTimeProvider(now)).Build(
            new SetupConfiguration("candidate-1", [SetupRole.Llm]),
            host, SetupTestData.ArtifactReport("ollama-llm"));

        Assert.Equal(oldest.AddMinutes(15), plan.ExpiresAtUtc);
        Assert.True(plan.ExpiresAtUtc < now.Add(SetupPlanBuilder.DefaultPlanLifetime));
    }

    [Fact]
    public void Port_conflict_is_bound_to_the_exact_configured_port()
    {
        var host = SetupTestData.HostReport("port-in-use", DoctorScope.Inventory);
        var builder = new SetupPlanBuilder(new MutableTimeProvider(host.CreatedAt.AddMinutes(1)));

        var plan = builder.Build(new SetupConfiguration("candidate-1", [SetupRole.Llm]),
            host, SetupTestData.ArtifactReport("ollama-llm"));

        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.GatewayPortConflict);
        Assert.Contains(plan.Blockers, blocker => blocker.Code == SetupBlockerCode.HostPrerequisitesIncomplete);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(70000)]
    public void Non_private_or_invalid_ports_are_rejected(int port)
    {
        var error = Assert.Throws<SetupException>(() =>
            new SetupConfiguration("candidate-1", [SetupRole.Llm], gatewayPort: port));
        Assert.Equal(SetupFailure.InvalidConfiguration, error.Failure);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/var/lib/../secret")]
    [InlineData("/var/lib/martlet/")]
    public void Noncanonical_paths_are_rejected(string path)
    {
        var error = Assert.Throws<SetupException>(() =>
            new SetupConfiguration("candidate-1", [SetupRole.Llm], dataDirectory: path,
                artifactDirectory: path + "/artifacts"));
        Assert.Equal(SetupFailure.InvalidConfiguration, error.Failure);
    }

    [Fact]
    public void Changed_typed_host_or_artifact_facts_change_plan_fingerprints()
    {
        var firstHost = SetupTestData.HostReport();
        var secondHost = SetupTestData.HostReport("missing-tools");
        var firstClock = new MutableTimeProvider(firstHost.CreatedAt.AddMinutes(1));
        var secondClock = new MutableTimeProvider(secondHost.CreatedAt.AddMinutes(1));
        var configuration = new SetupConfiguration("candidate-1", [SetupRole.Llm, SetupRole.Tts]);

        var first = new SetupPlanBuilder(firstClock).Build(configuration, firstHost, SetupTestData.ArtifactReport());
        var second = new SetupPlanBuilder(secondClock).Build(configuration, secondHost, SetupTestData.ArtifactReport());
        var roleOnly = new SetupPlanBuilder(firstClock).Build(
            new SetupConfiguration("candidate-1", [SetupRole.Llm]),
            firstHost, SetupTestData.ArtifactReport("ollama-llm"));

        Assert.NotEqual(first.HostFactsFingerprint, second.HostFactsFingerprint);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.DesiredStateFingerprint, second.DesiredStateFingerprint);
        Assert.NotEqual(first.ArtifactFactsFingerprint, roleOnly.ArtifactFactsFingerprint);
        Assert.NotEqual(first.DesiredStateFingerprint, roleOnly.DesiredStateFingerprint);
    }
}
