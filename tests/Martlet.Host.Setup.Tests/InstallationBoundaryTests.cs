using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Host.Inventory;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

public sealed class InstallationBoundaryTests
{
    private static readonly Guid Host = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Runtime = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Resource = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly InstallationOwner Owner = new(InstallationOwnership.GuidedManaged,
        Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly InstallationFact Eligible = new(PlanningState.Eligible, "unauthenticated-claim");

    [Theory]
    [InlineData(1, Provenance.AuthoredFixture)]
    [InlineData(1, Provenance.LiveLocal)]
    [InlineData(2, Provenance.AuthoredFixture)]
    [InlineData(2, Provenance.LiveLocal)]
    public async Task Imported_claims_and_eligible_planner_never_authorize_host_effects(int version, Provenance provenance)
    {
        var host = ImportedReport(provenance);
        var request = Request();
        Assert.Equal(PlanningState.Eligible, InstallationPlanner.Create(request).State);
        var plan = Build(request, host, version);
        Assert.Equal(SetupPlanDisposition.Blocked, plan.Disposition);
        Assert.False(plan.ExecutionAuthorized);
        Assert.Contains(plan.Blockers, b => b.Code == SetupBlockerCode.HostFactsNotLive);
        Assert.DoesNotContain(plan.Prerequisites, p => p.State == SetupPrerequisiteState.Satisfied);
        Assert.Null(plan.DiskBudget.HostAvailableBytes);
        Assert.False(plan.DiskBudget.Complete);
        Assert.Equal(Host, plan.TargetHostId);
        Assert.NotNull(plan.InstallationRequestFingerprint);
        Assert.All(plan.Steps, step => Assert.Null(step.Command));
        using var store = new ReviewStore();
        store.Clock.UtcNow = plan.CreatedAtUtc;
        var preview = await store.Coordinator.PreviewReviewAsync(plan);
        var result = await store.Coordinator.RecordReviewAsync(plan, ReviewJournalTests.Approve(preview));
        Assert.True(result.ReviewRecorded);
        Assert.False(result.ExternalActionsCompleted);
        Assert.All((await store.Coordinator.PreviewReviewAsync(plan)).Steps,
            step => Assert.Equal(SetupObservationState.Unknown, step.Observation));
    }

    [Fact]
    public void Disabled_saved_roles_do_not_create_artifact_or_runtime_work()
    {
        var request = Request() with
        {
            Destinations = Request().Destinations.Add(new(InstallationRole.Tts, Runtime,
                "f5-tts", CapabilitySupport.Supported, Eligible, Gates()))
        };
        var plan = Build(request, ImportedReport(Provenance.LiveLocal), 2);
        Assert.Equal(InstallationRole.Llm, Assert.Single(Assert.Single(plan.InstallationMachine!.Runtimes).Roles));
        Assert.DoesNotContain(plan.ExpectedResources, r => r.Id == "role-tts");
        Assert.Single(plan.ExpectedResources.Where(r => r.Kind == SetupExpectedResourceKind.RoleArtifactSet));
        var selected = ArtifactInspector.Inspect(Manifest(2), "ollama-llm", "ubuntu-24.04-x64");
        Assert.Equal(selected.KnownPayloadBytes, plan.DiskBudget.KnownListedPayloadBytes);
    }

    [Fact]
    public void Collocated_roles_and_shared_dependencies_are_projected_once_by_the_core_planner()
    {
        var request = Request() with
        {
            Features = [InstallationFeature.SpokenReplies],
            Destinations =
            [
                Request().Destinations[0],
                new(InstallationRole.Tts, Runtime, "f5-tts", CapabilitySupport.Supported, Eligible, Gates())
            ]
        };
        var host = ImportedReport(Provenance.LiveLocal);
        var plan = new SetupPlanBuilder(new MutableTimeProvider(host.CreatedAt.AddMinutes(1))).Build(
            new SetupConfiguration("bound", [SetupRole.Llm, SetupRole.Tts]), request, Host, host, Manifest(2));
        Assert.Equal(2, Assert.Single(plan.InstallationMachine!.Runtimes).Roles.Length);
        Assert.Single(plan.InstallationMachine.Resources);
        Assert.Equal(new InstallationResources(1, 2, 3, 4), plan.InstallationMachine.Demand);
        Assert.Equal(1, plan.Steps.Count(s => s.Kind == SetupStepKind.ConfigureContainerPrerequisites));
        Assert.Equal(1, plan.Steps.Count(s => s.Kind == SetupStepKind.ProvisionArtifacts));
        Assert.All(plan.ExpectedResources, r => Assert.True(r.MutationDeferred));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Dual_role_adapter_resolves_exact_IDs_without_adopting_optional_catalog_roles(int version)
    {
        var doc = JsonNode.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, $"host-artifacts.v{version}.json")))!.AsObject();
        var tts = doc["roles"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == "f5-tts")!;
        var extraRole = tts.DeepClone();
        var extraRuntime = doc["runtimes"]!.AsArray().Single(r =>
            r!["id"]!.GetValue<string>() == tts["runtime_id"]!.GetValue<string>())!.DeepClone();
        extraRole["id"] = "optional-tts";
        extraRole["runtime_id"] = "optional-runtime";
        extraRuntime["id"] = "optional-runtime";
        doc["roles"]!.AsArray().Add(extraRole);
        doc["runtimes"]!.AsArray().Add(extraRuntime);
        var request = Request() with
        {
            Features = [InstallationFeature.SpokenReplies],
            Destinations = Request().Destinations.Add(new(InstallationRole.Tts, Runtime,
                "f5-tts", CapabilitySupport.Supported, Eligible, Gates()))
        };
        var host = ImportedReport(Provenance.LiveLocal);
        var builder = new SetupPlanBuilder(new MutableTimeProvider(host.CreatedAt.AddMinutes(1)));
        var configuration = new SetupConfiguration("bound", [SetupRole.Llm, SetupRole.Tts]);
        var manifest = ArtifactManifestReader.Read(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        var plan = builder.Build(configuration, request, Host, host, manifest);
        var selected = ArtifactInspector.InspectRoles(manifest, ["ollama-llm", "f5-tts"], "ubuntu-24.04-x64");
        Assert.Equal(FingerprintBuilder.Bytes(Encoding.UTF8.GetBytes(selected.ToJson())), plan.ArtifactFactsFingerprint);
        Assert.Equal(selected.KnownPayloadBytes, plan.DiskBudget.KnownListedPayloadBytes);
        tts["id"] = "renamed-tts";
        manifest = ArtifactManifestReader.Read(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        Assert.Equal("inspection.invalid_invocation", Assert.Throws<ArtifactManifestException>(() =>
            builder.Build(configuration, request, Host, host, manifest)).DiagnosticCode);
    }

    [Theory]
    [InlineData(InstallationOwnership.ExistingService)]
    [InlineData(InstallationOwnership.UserManagedCompose)]
    public void Externally_owned_runtime_cannot_be_adopted_by_legacy_role_configuration(InstallationOwnership mode)
    {
        var request = Request();
        request = request with
        {
            Runtimes = request.Runtimes.Select(r => r with { Owner = r.Owner with { Mode = mode } }).ToImmutableArray()
        };
        Assert.Equal(SetupFailure.InvalidConfiguration,
            Assert.Throws<SetupException>(() => Build(request, ImportedReport(Provenance.LiveLocal), 2)).Failure);
    }

    [Fact]
    public void Mixed_external_llm_and_managed_tts_only_selects_managed_artifacts()
    {
        var request = Request();
        var external = Guid.NewGuid();
        request = request with
        {
            Features = [InstallationFeature.SpokenReplies],
            Runtimes = request.Runtimes.Add(new(external, null, "external-api", "external-llm",
                InstallationRuntimeKind.ExternalApi, Owner with { Mode = InstallationOwnership.ExistingService },
                Eligible, [], [])),
            Destinations =
            [
                new(InstallationRole.Llm, external, "external-llm", CapabilitySupport.Supported,
                    Eligible, [new(InstallationPrerequisite.CredentialBinding, Eligible), new(InstallationPrerequisite.DataConsent, Eligible)]),
                new(InstallationRole.Tts, Runtime, "f5-tts", CapabilitySupport.Supported, Eligible, Gates())
            ]
        };
        var host = ImportedReport(Provenance.LiveLocal);
        var plan = new SetupPlanBuilder(new MutableTimeProvider(host.CreatedAt.AddMinutes(1))).Build(
            new SetupConfiguration("bound", [SetupRole.Tts]), request, Host, host, Manifest(2));
        Assert.DoesNotContain(plan.ExpectedResources, r => r.Id == "role-llm");
        Assert.Equal(InstallationRole.Tts, Assert.Single(Assert.Single(plan.InstallationMachine!.Runtimes).Roles));
        Assert.Equal(ArtifactInspector.Inspect(Manifest(2), "f5-tts", "ubuntu-24.04-x64").KnownPayloadBytes,
            plan.DiskBudget.KnownListedPayloadBytes);
        Assert.False(plan.ExecutionAuthorized);
    }

    [Fact]
    public async Task Changed_owner_identity_cannot_roll_forward_an_existing_review_journal()
    {
        var host = ImportedReport(Provenance.LiveLocal);
        var request = Request();
        var first = Build(request, host, 2);
        var owner = Owner with { Id = Guid.NewGuid() };
        var second = Build(request with
        {
            Runtimes = request.Runtimes.Select(r => r with { Owner = owner }).ToImmutableArray(),
            Resources = request.Resources.Select(r => r with { Owner = owner }).ToImmutableArray()
        }, host, 2);
        Assert.NotEqual(first.DesiredStateFingerprint, second.DesiredStateFingerprint);
        using var store = new ReviewStore();
        store.Clock.UtcNow = first.CreatedAtUtc;
        await store.Coordinator.RecordReviewAsync(first,
            ReviewJournalTests.Approve(await store.Coordinator.PreviewReviewAsync(first)));
        var bytes = File.ReadAllBytes(store.Path);
        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await store.Coordinator.PreviewReviewAsync(second));
        Assert.Equal(SetupFailure.PlanChanged, error.Failure);
        Assert.Equal(bytes, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public void Metadata_version_and_selected_document_identity_are_frozen_not_acquisition_evidence()
    {
        var request = Request();
        var host = ImportedReport(Provenance.LiveLocal);
        var v1 = Build(request, host, 1);
        var v2 = Build(request, host, 2);
        Assert.NotEqual(v1.ArtifactFactsFingerprint, v2.ArtifactFactsFingerprint);
        Assert.NotEqual(v1.DesiredStateFingerprint, v2.DesiredStateFingerprint);
        Assert.False(v1.ExecutionAuthorized);
        Assert.False(v2.ExecutionAuthorized);
        Assert.Equal(v1.InstallationRequestFingerprint, v2.InstallationRequestFingerprint);
    }

    private static SetupPlan Build(InstallationRequest request, HostReport host, int version) =>
        new SetupPlanBuilder(new MutableTimeProvider(host.CreatedAt.AddMinutes(1))).Build(
            new SetupConfiguration("bound", [SetupRole.Llm]), request, Host, host, Manifest(version));

    private static ArtifactManifest Manifest(int version) =>
        ArtifactManifestReader.Read(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, $"host-artifacts.v{version}.json")));

    private static HostReport ImportedReport(Provenance provenance)
    {
        var report = SetupTestData.HostReport();
        report = report with
        {
            Provenance = provenance,
            Probes = report.Probes.Select(p => p.Provenance == Provenance.NotObserved
                ? p : p with { Provenance = provenance }).ToImmutableArray()
        };
        return HostJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(HostJson.Serialize(report)));
    }

    private static ImmutableArray<PrerequisiteFact> Gates(bool runtime = false) =>
        (runtime ? new[] { InstallationPrerequisite.HostQualification, InstallationPrerequisite.RuntimeCompatibility,
            InstallationPrerequisite.ArtifactEligibility, InstallationPrerequisite.LicenseReview,
            InstallationPrerequisite.LocalApproval, InstallationPrerequisite.Pairing } :
            [InstallationPrerequisite.DataConsent])
        .Select(kind => new PrerequisiteFact(kind, Eligible)).ToImmutableArray();

    private static InstallationRequest Request() => new()
    {
        ClientHostId = Host,
        Features = [InstallationFeature.TypedConversation],
        Hosts = [new(Host, "Unverified host identity", new(100, 100, 100, 100))],
        Resources = [new(Resource, Host, "shared-runtime", Owner, new(1, 2, 3, 4), false, Eligible, [])],
        Runtimes = [new(Runtime, Host, "ollama", "candidate", InstallationRuntimeKind.Compose,
            Owner, Eligible, [Resource], Gates(runtime: true))],
        Destinations = [new(InstallationRole.Llm, Runtime, "ollama-llm", CapabilitySupport.Supported, Eligible, Gates())]
    };
}
