using System.Collections.Immutable;
using Martlet.Core.Installation;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public sealed record HostDeploymentIdentity(Guid HostId, Guid DeploymentId, Guid OwnerId)
{
    public string ProjectName => "martlet-" + DeploymentId.ToString("N");
}

public sealed class HostDeploymentDefinition
{
    public SetupConfiguration Configuration { get; }
    public SetupPlan Plan { get; }
    public ArtifactAcquisitionSelection Selection { get; }
    public HostDeploymentIdentity Identity { get; }
    public string Fingerprint { get; }
    public string ProposedIdentityDirectory => Configuration.DataDirectory + "/deployments/" +
        Identity.DeploymentId.ToString("N") + "/gateway-identity";

    public HostDeploymentDefinition(SetupConfiguration configuration, SetupPlan plan,
        ArtifactAcquisitionSelection selection, HostDeploymentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(identity);
        var machine = plan.InstallationMachine;
        DeploymentRules.Require(identity.HostId != Guid.Empty && identity.DeploymentId != Guid.Empty &&
            identity.OwnerId != Guid.Empty && plan.TargetHostId == identity.HostId &&
            machine?.HostId == identity.HostId && machine.Runtimes.Length > 0 &&
            machine.Runtimes.All(runtime => runtime.Kind == InstallationRuntimeKind.Compose &&
                runtime.Owner.Mode == InstallationOwnership.GuidedManaged && runtime.Owner.Id == identity.OwnerId) &&
            machine.Resources.All(resource => resource.Owner.Mode == InstallationOwnership.GuidedManaged &&
                resource.Owner.Id == identity.OwnerId) &&
            plan.ConfigurationFingerprint == configuration.Fingerprint &&
            plan.InstallationRequestFingerprint is not null &&
            selection.Target == "ubuntu-24.04-x64" && selection.Platform == "linux/amd64" &&
            !selection.DeclaredMismatch && selection.FormatVersion == 2,
            HostDeploymentFailure.InvalidDefinition);
        var roles = configuration.Roles.Select(role => role switch
        {
            SetupRole.Llm => "ollama-llm", SetupRole.Tts => "f5-tts",
            _ => throw new HostDeploymentException(HostDeploymentFailure.InvalidDefinition)
        }).Order(StringComparer.Ordinal);
        DeploymentRules.Require(roles.SequenceEqual(selection.RoleIds) &&
            plan.ExpectedResources.Where(resource => resource.Kind == SetupExpectedResourceKind.RoleArtifactSet)
                .Select(resource => resource.Value).Order(StringComparer.Ordinal).SequenceEqual(selection.RoleIds) &&
            plan.ExpectedResources.Any(resource => resource.Kind == SetupExpectedResourceKind.ArtifactManifest &&
                resource.Value == "sha256:" + selection.ManifestSha256),
            HostDeploymentFailure.InvalidDefinition);
        Configuration = configuration;
        Plan = plan;
        Selection = selection;
        Identity = identity;
        Fingerprint = FingerprintBuilder.Create("host-deployment-definition-v2", plan.DesiredStateFingerprint,
            selection.Fingerprint, identity.HostId.ToString("N"), identity.DeploymentId.ToString("N"),
            identity.OwnerId.ToString("N"));
    }
}

public sealed record HostDeploymentFinding(string Subject, string Code);

public sealed class HostDeploymentFile
{
    private readonly byte[] bytes;
    public string Name { get; }
    public ReadOnlyMemory<byte> Content => bytes.ToArray();
    public string Sha256 { get; }
    public int Length => bytes.Length;
    internal HostDeploymentFile(string name, byte[] content)
    {
        DeploymentRules.Require(name.Length <= 96 && name.All(c => char.IsAsciiLetterOrDigit(c) ||
            c is '-' or '.') && !name.Contains("..", StringComparison.Ordinal) && content.Length <= 262_144,
            HostDeploymentFailure.InvalidDefinition);
        Name = name;
        bytes = content.ToArray();
        Sha256 = FingerprintBuilder.Bytes(bytes);
    }
}

public sealed class HostDeploymentBundle
{
    public HostDeploymentDefinition Definition { get; }
    public string ProjectName => Definition.Identity.ProjectName;
    public ImmutableArray<HostDeploymentFile> Files { get; }
    public ImmutableArray<HostDeploymentFinding> Findings { get; }
    public string ContentObservationFingerprint { get; }
    public string Fingerprint { get; }
    public bool RunnableComposeAvailable => false;
    public bool ExecutionAuthorized => false;
    public bool RuntimeEnabled => false;
    public bool HostReady => false;
    internal HostDeploymentBundle(HostDeploymentDefinition definition, ArtifactPublishedImageObservation observation,
        IEnumerable<HostDeploymentFile> files, IEnumerable<HostDeploymentFinding> findings)
    {
        Definition = definition;
        Files = files.OrderBy(file => file.Name, StringComparer.Ordinal).ToImmutableArray();
        Findings = findings.ToImmutableArray();
        DeploymentRules.Require(Files.Length is > 0 and <= 32 && Files.Sum(file => file.Length) <= 1_048_576,
            HostDeploymentFailure.InvalidDefinition);
        ContentObservationFingerprint = observation.Fingerprint;
        Fingerprint = FingerprintBuilder.Create(["host-deployment-bundle-v2", definition.Fingerprint,
            observation.Fingerprint, .. Files.SelectMany(file => new[] { file.Name, file.Sha256 })]);
    }
}

public enum HostDeploymentConfigurationDecision { No, Publish }
public enum HostDeploymentConfigurationScope { LocalConfigurationFiles }
public enum HostDeploymentPublicationState { Refused, Blocked, Interrupted, Published, AlreadyPublished }

public sealed class HostDeploymentPreview
{
    public HostDeploymentBundle Bundle { get; }
    public string FinalPath { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public bool AlreadyPublished { get; }
    internal string StorageFingerprint { get; }
    internal string ReviewVersion { get; }
    internal DateTimeOffset CreatedAtUtc { get; }
    internal string Fingerprint { get; }
    internal HostDeploymentPreview(HostDeploymentBundle bundle, DeploymentStorageSnapshot storage,
        string reviewVersion, DateTimeOffset now, DateTimeOffset expires)
    {
        Bundle = bundle;
        FinalPath = storage.FinalPath;
        StorageFingerprint = storage.Fingerprint;
        ReviewVersion = reviewVersion;
        CreatedAtUtc = now;
        ExpiresAtUtc = expires;
        AlreadyPublished = storage.Document?.State == DeploymentJournalState.Published;
        Fingerprint = FingerprintBuilder.Create(bundle.Fingerprint, StorageFingerprint, ReviewVersion,
            now.ToString("O"), expires.ToString("O"));
    }
    public HostDeploymentApproval Approve(HostDeploymentConfigurationDecision decision = HostDeploymentConfigurationDecision.No,
        IEnumerable<HostDeploymentConfigurationScope>? scopes = null)
    {
        var exact = scopes?.Take(2).ToArray() ?? [];
        return new(Fingerprint, decision == HostDeploymentConfigurationDecision.Publish &&
            exact.SequenceEqual([HostDeploymentConfigurationScope.LocalConfigurationFiles]));
    }
}

public sealed class HostDeploymentApproval
{
    private int consumed;
    internal string Fingerprint { get; }
    public bool IsApproved { get; }
    internal HostDeploymentApproval(string fingerprint, bool approved)
    { Fingerprint = fingerprint; IsApproved = approved; }
    internal bool Consume() => Interlocked.Exchange(ref consumed, 1) == 0;
}

public sealed record HostDeploymentPublicationResult(
    HostDeploymentPublicationState State, HostDeploymentFailure? Failure, string? FinalPath, string? BundleFingerprint)
{
    public string? Remedy => Failure is { } failure ? HostDeploymentRemedies.For(failure) : null;
    public bool ConfigurationPublished => State is HostDeploymentPublicationState.Published or HostDeploymentPublicationState.AlreadyPublished;
    public bool ExecutionAuthorized => false;
    public bool RuntimeEnabled => false;
    public bool HostReady => false;
}
