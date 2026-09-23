using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Installation;

namespace Martlet.Host.Setup;

public enum SetupProfile { SingleHost }
public enum SetupRole { Llm, Tts }
public enum SetupPlanDisposition { Reviewable, Blocked }
public enum SetupPrerequisiteState { Satisfied, Missing, Unknown, ReviewRequired, Conflict }
public enum SetupPrivilege { None, Operator, Administrator }
public enum SetupConsentScope
{
    LocalJournal,
    HostFilesystem,
    PackageRepositories,
    ContainerRuntime,
    NvidiaDriver,
    ArtifactDownload,
    ModelAndVoiceRights,
    Firewall,
    ServiceManagement
}
public enum SetupExpectedResourceKind { Directory, File, TcpPort, Service, ArtifactManifest, RoleArtifactSet }
public enum SetupStepKind
{
    VerifyHostFacts,
    ReviewArtifactManifest,
    PrepareHostFilesystem,
    ConfigureContainerPrerequisites,
    ProvisionArtifacts,
    ConfigureGatewayService
}
public enum SetupStepExecution { ObservationOnly, OperatorCommand, Deferred }
public enum SetupBlockerCode
{
    HostFactsNotLive,
    HostFactsStale,
    HostPrerequisitesIncomplete,
    HostTargetMismatch,
    GatewayPortConflict,
    ArtifactCandidateIncomplete,
    DiskBudgetIncomplete,
    DiskInsufficient,
    MutationDeferred,
    InstallationBindingRequired
}

public sealed class SetupConfiguration
{
    public SetupProfile Profile { get; }
    public string Revision { get; }
    public ImmutableArray<SetupRole> Roles { get; }
    public int GatewayPort { get; }
    public string ConfigurationDirectory { get; }
    public string DataDirectory { get; }
    public string ArtifactDirectory { get; }
    public string Fingerprint { get; }

    public SetupConfiguration(
        string revision,
        IEnumerable<SetupRole> roles,
        int gatewayPort = 7443,
        string configurationDirectory = "/etc/martlet",
        string dataDirectory = "/var/lib/martlet",
        string artifactDirectory = "/var/lib/martlet/artifacts",
        SetupProfile profile = SetupProfile.SingleHost)
    {
        SetupGuard.Require(Enum.IsDefined(profile), SetupFailure.InvalidConfiguration);
        SetupGuard.Identifier(revision, SetupFailure.InvalidConfiguration);
        ArgumentNullException.ThrowIfNull(roles);
        var ownedRoles = roles.Order().ToImmutableArray();
        SetupGuard.Require(ownedRoles.Length is > 0 and <= 4 &&
            ownedRoles.All(Enum.IsDefined) && ownedRoles.Distinct().Count() == ownedRoles.Length,
            SetupFailure.InvalidConfiguration);
        SetupGuard.Require(gatewayPort is >= 1024 and <= 65535, SetupFailure.InvalidConfiguration);
        SetupGuard.UnixPath(configurationDirectory, SetupFailure.InvalidConfiguration);
        SetupGuard.UnixPath(dataDirectory, SetupFailure.InvalidConfiguration);
        SetupGuard.UnixPath(artifactDirectory, SetupFailure.InvalidConfiguration);
        SetupGuard.Require(artifactDirectory.StartsWith(dataDirectory + "/", StringComparison.Ordinal),
            SetupFailure.InvalidConfiguration);

        Profile = profile;
        Revision = revision;
        Roles = ownedRoles;
        GatewayPort = gatewayPort;
        ConfigurationDirectory = configurationDirectory;
        DataDirectory = dataDirectory;
        ArtifactDirectory = artifactDirectory;
        Fingerprint = FingerprintBuilder.Create(
            "setup-config-v1", Profile.ToString(), Revision,
            string.Join(",", Roles.Select(r => r.ToString())),
            GatewayPort.ToString(CultureInfo.InvariantCulture),
            ConfigurationDirectory, DataDirectory, ArtifactDirectory);
    }
}

public sealed record SetupPrerequisite(
    string Id,
    SetupPrerequisiteState State,
    bool Required,
    string Observation,
    string Remedy)
{
    internal void Validate()
    {
        SetupGuard.Identifier(Id, SetupFailure.InvalidPrerequisiteFacts);
        SetupGuard.Require(Enum.IsDefined(State), SetupFailure.InvalidPrerequisiteFacts);
        SetupGuard.Text(Observation, 512, SetupFailure.InvalidPrerequisiteFacts);
        SetupGuard.Text(Remedy, 1024, SetupFailure.InvalidPrerequisiteFacts);
    }
}

public sealed record SetupExpectedResource(
    string Id,
    SetupExpectedResourceKind Kind,
    string Value,
    long? DeclaredBytes,
    bool MutationDeferred)
{
    internal void Validate()
    {
        SetupGuard.Identifier(Id, SetupFailure.InvalidConfiguration);
        SetupGuard.Require(Enum.IsDefined(Kind), SetupFailure.InvalidConfiguration);
        SetupGuard.Text(Value, 1024, SetupFailure.InvalidConfiguration);
        SetupGuard.Require(DeclaredBytes is null or > 0 and <= 70_368_744_177_664L,
            SetupFailure.InvalidConfiguration);
    }
}

public sealed record SetupDiskBudget(
    long KnownListedPayloadBytes,
    long ReservationFloorBytes,
    long? HostAvailableBytes,
    bool Complete)
{
    public bool FitsObservedDisk => HostAvailableBytes is { } available && available >= ReservationFloorBytes;

    internal void Validate()
    {
        SetupGuard.Require(KnownListedPayloadBytes is >= 0 and <= 70_368_744_177_664L &&
            ReservationFloorBytes >= KnownListedPayloadBytes &&
            ReservationFloorBytes <= 140_737_488_355_328L &&
            HostAvailableBytes is null or >= 0, SetupFailure.InvalidPrerequisiteFacts);
    }
}

public sealed record SetupBlocker(
    SetupBlockerCode Code,
    string Summary,
    string Remedy)
{
    internal void Validate()
    {
        SetupGuard.Require(Enum.IsDefined(Code), SetupFailure.InvalidPrerequisiteFacts);
        SetupGuard.Text(Summary, 512, SetupFailure.InvalidPrerequisiteFacts);
        SetupGuard.Text(Remedy, 1024, SetupFailure.InvalidPrerequisiteFacts);
    }
}

public sealed class SetupCommand
{
    public string Executable { get; }
    public ImmutableArray<string> Arguments { get; }
    public SetupPrivilege Privilege { get; }

    internal SetupCommand(string executable, IEnumerable<string> arguments, SetupPrivilege privilege)
    {
        SetupGuard.Text(executable, 512, SetupFailure.InvalidConfiguration);
        SetupGuard.Require(executable.StartsWith("/", StringComparison.Ordinal) &&
            !executable.Any(char.IsWhiteSpace) && Enum.IsDefined(privilege) &&
            privilege != SetupPrivilege.None, SetupFailure.InvalidConfiguration);
        ArgumentNullException.ThrowIfNull(arguments);
        var owned = arguments.ToImmutableArray();
        SetupGuard.Require(owned.Length <= 32, SetupFailure.InvalidConfiguration);
        foreach (var argument in owned)
            SetupGuard.Text(argument, 1024, SetupFailure.InvalidConfiguration, allowEmpty: true);
        Executable = executable;
        Arguments = owned;
        Privilege = privilege;
    }
}

public sealed class SetupStep
{
    public string Id { get; }
    public int Order { get; }
    public SetupStepKind Kind { get; }
    public SetupStepExecution Execution { get; }
    public string Description { get; }
    public SetupPrivilege Privilege { get; }
    public ImmutableArray<SetupConsentScope> ConsentScopes { get; }
    public ImmutableArray<string> PrerequisiteIds { get; }
    public ImmutableArray<string> ExpectedResourceIds { get; }
    public SetupCommand? Command { get; }
    public bool RollbackAvailable => false;

    internal SetupStep(
        string id,
        int order,
        SetupStepKind kind,
        SetupStepExecution execution,
        string description,
        SetupPrivilege privilege,
        IEnumerable<SetupConsentScope> consentScopes,
        IEnumerable<string> prerequisiteIds,
        IEnumerable<string> expectedResourceIds,
        SetupCommand? command = null)
    {
        SetupGuard.Identifier(id, SetupFailure.InvalidConfiguration);
        SetupGuard.Require(order is >= 1 and <= 32 && Enum.IsDefined(kind) &&
            Enum.IsDefined(execution) && Enum.IsDefined(privilege), SetupFailure.InvalidConfiguration);
        SetupGuard.Text(description, 512, SetupFailure.InvalidConfiguration);
        ArgumentNullException.ThrowIfNull(consentScopes);
        ArgumentNullException.ThrowIfNull(prerequisiteIds);
        ArgumentNullException.ThrowIfNull(expectedResourceIds);
        var scopes = consentScopes.Order().ToImmutableArray();
        var prerequisites = prerequisiteIds.ToImmutableArray();
        var resources = expectedResourceIds.ToImmutableArray();
        SetupGuard.Require(scopes.Length <= 16 && scopes.All(Enum.IsDefined) &&
            scopes.Distinct().Count() == scopes.Length &&
            prerequisites.Length <= 32 && prerequisites.Distinct(StringComparer.Ordinal).Count() == prerequisites.Length &&
            resources.Length <= 32 && resources.Distinct(StringComparer.Ordinal).Count() == resources.Length,
            SetupFailure.InvalidConfiguration);
        foreach (var value in prerequisites.Concat(resources))
            SetupGuard.Identifier(value, SetupFailure.InvalidConfiguration);
        SetupGuard.Require(execution == SetupStepExecution.OperatorCommand
                ? command is not null && command.Privilege == privilege && privilege != SetupPrivilege.None
                : command is null,
            SetupFailure.InvalidConfiguration);
        SetupGuard.Require(execution != SetupStepExecution.ObservationOnly ||
            privilege == SetupPrivilege.None && scopes.Length == 0, SetupFailure.InvalidConfiguration);

        Id = id;
        Order = order;
        Kind = kind;
        Execution = execution;
        Description = description;
        Privilege = privilege;
        ConsentScopes = scopes;
        PrerequisiteIds = prerequisites;
        ExpectedResourceIds = resources;
        Command = command;
    }
}

public sealed class SetupPlan
{
    public string Id { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string ConfigurationFingerprint { get; }
    public string HostFactsFingerprint { get; }
    public string ArtifactFactsFingerprint { get; }
    public SetupPlanDisposition Disposition { get; }
    public ImmutableArray<SetupPrerequisite> Prerequisites { get; }
    public ImmutableArray<SetupExpectedResource> ExpectedResources { get; }
    public SetupDiskBudget DiskBudget { get; }
    public ImmutableArray<SetupStep> Steps { get; }
    public ImmutableArray<SetupBlocker> Blockers { get; }
    public ImmutableArray<SetupConsentScope> RequiredConsentScopes { get; }
    public string DesiredStateFingerprint { get; }
    public string? InstallationRequestFingerprint { get; }
    public Guid? TargetHostId { get; }
    public MachineInstallationPlan? InstallationMachine { get; }
    public bool ExecutionAuthorized => false;
    public bool RollbackAvailable => false;
    public string Fingerprint { get; }

    internal SetupPlan(
        string id,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        string configurationFingerprint,
        string hostFactsFingerprint,
        string artifactFactsFingerprint,
        SetupPlanDisposition disposition,
        IEnumerable<SetupPrerequisite> prerequisites,
        IEnumerable<SetupExpectedResource> expectedResources,
        SetupDiskBudget diskBudget,
        IEnumerable<SetupStep> steps,
        IEnumerable<SetupBlocker> blockers,
        string? installationRequestFingerprint = null,
        Guid? targetHostId = null,
        MachineInstallationPlan? installationMachine = null)
    {
        SetupGuard.Identifier(id, SetupFailure.InvalidConfiguration);
        SetupGuard.Require(createdAtUtc != default && expiresAtUtc > createdAtUtc &&
            expiresAtUtc - createdAtUtc <= TimeSpan.FromHours(1), SetupFailure.InvalidConfiguration);
        SetupGuard.Fingerprint(configurationFingerprint, SetupFailure.InvalidConfiguration);
        SetupGuard.Fingerprint(hostFactsFingerprint, SetupFailure.InvalidPrerequisiteFacts);
        SetupGuard.Fingerprint(artifactFactsFingerprint, SetupFailure.InvalidPrerequisiteFacts);
        SetupGuard.Require(Enum.IsDefined(disposition), SetupFailure.InvalidConfiguration);
        SetupGuard.Require((installationRequestFingerprint is null) == (targetHostId is null) &&
            targetHostId != Guid.Empty, SetupFailure.InvalidConfiguration);
        if (installationRequestFingerprint is not null)
            SetupGuard.Fingerprint(installationRequestFingerprint, SetupFailure.InvalidConfiguration);
        SetupGuard.Require((installationMachine is null) == (targetHostId is null) &&
            installationMachine?.HostId == targetHostId, SetupFailure.InvalidConfiguration);
        ArgumentNullException.ThrowIfNull(prerequisites);
        ArgumentNullException.ThrowIfNull(expectedResources);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(blockers);
        var ownedPrerequisites = prerequisites.ToImmutableArray();
        var ownedResources = expectedResources.ToImmutableArray();
        var ownedSteps = steps.OrderBy(s => s.Order).ToImmutableArray();
        var ownedBlockers = blockers.OrderBy(b => b.Code).ToImmutableArray();
        SetupGuard.Require(ownedPrerequisites.Length is > 0 and <= 64 &&
            ownedResources.Length is > 0 and <= 64 &&
            ownedSteps.Length is > 0 and <= 32 &&
            ownedBlockers.Length <= 32, SetupFailure.InvalidConfiguration);
        foreach (var prerequisite in ownedPrerequisites) prerequisite.Validate();
        foreach (var resource in ownedResources) resource.Validate();
        foreach (var step in ownedSteps)
        {
            SetupGuard.Require(step is not null, SetupFailure.InvalidConfiguration);
        }
        foreach (var blocker in ownedBlockers) blocker.Validate();
        diskBudget.Validate();
        SetupGuard.Require(ownedPrerequisites.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == ownedPrerequisites.Length &&
            ownedResources.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == ownedResources.Length &&
            ownedSteps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == ownedSteps.Length &&
            ownedSteps.Select(s => s.Order).SequenceEqual(Enumerable.Range(1, ownedSteps.Length)),
            SetupFailure.InvalidConfiguration);
        var prerequisiteIds = ownedPrerequisites.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var resourceIds = ownedResources.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        SetupGuard.Require(ownedSteps.All(s => s.PrerequisiteIds.All(prerequisiteIds.Contains) &&
            s.ExpectedResourceIds.All(resourceIds.Contains)), SetupFailure.InvalidConfiguration);
        SetupGuard.Require(disposition == SetupPlanDisposition.Reviewable
            ? ownedBlockers.Length == 0 &&
              ownedSteps.All(s => s.Execution != SetupStepExecution.Deferred) &&
              ownedPrerequisites.Where(p => p.Required).All(p => p.State == SetupPrerequisiteState.Satisfied) &&
              ownedResources.All(resource => !resource.MutationDeferred) &&
              diskBudget.Complete && diskBudget.FitsObservedDisk
            : ownedBlockers.Length != 0, SetupFailure.InvalidConfiguration);

        Id = id;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ConfigurationFingerprint = configurationFingerprint;
        HostFactsFingerprint = hostFactsFingerprint;
        ArtifactFactsFingerprint = artifactFactsFingerprint;
        InstallationRequestFingerprint = installationRequestFingerprint;
        TargetHostId = targetHostId;
        InstallationMachine = installationMachine;
        Disposition = disposition;
        Prerequisites = ownedPrerequisites;
        ExpectedResources = ownedResources;
        DiskBudget = diskBudget;
        Steps = ownedSteps;
        Blockers = ownedBlockers;
        RequiredConsentScopes = ownedSteps
            .SelectMany(s => s.ConsentScopes)
            .Append(SetupConsentScope.LocalJournal)
            .Distinct()
            .Order()
            .ToImmutableArray();
        DesiredStateFingerprint = BuildDesiredStateFingerprint();
        Fingerprint = BuildFingerprint();
    }

    private string BuildDesiredStateFingerprint()
    {
        var values = new List<string>
        {
            "setup-desired-state-v2", Id, ConfigurationFingerprint, ArtifactFactsFingerprint,
            InstallationRequestFingerprint ?? "unbound", TargetHostId?.ToString("D") ?? "unbound",
            DiskBudget.KnownListedPayloadBytes.ToString(CultureInfo.InvariantCulture),
            DiskBudget.ReservationFloorBytes.ToString(CultureInfo.InvariantCulture),
            DiskBudget.Complete.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var prerequisite in Prerequisites)
            values.AddRange(["p", prerequisite.Id, prerequisite.Required.ToString()]);
        foreach (var resource in ExpectedResources)
            values.AddRange(["r", resource.Id, resource.Kind.ToString(), resource.Value,
                resource.DeclaredBytes?.ToString(CultureInfo.InvariantCulture) ?? "null",
                resource.MutationDeferred.ToString(CultureInfo.InvariantCulture)]);
        foreach (var step in Steps)
        {
            values.AddRange(["s", step.Id, step.Order.ToString(CultureInfo.InvariantCulture), step.Kind.ToString(),
                step.Execution.ToString(), step.Description, step.Privilege.ToString(),
                string.Join(",", step.ConsentScopes), string.Join(",", step.PrerequisiteIds),
                string.Join(",", step.ExpectedResourceIds), step.RollbackAvailable.ToString(CultureInfo.InvariantCulture)]);
            if (step.Command is { } command)
                values.AddRange(["c", command.Executable, string.Join("\u001f", command.Arguments), command.Privilege.ToString()]);
        }
        return FingerprintBuilder.Create(values);
    }

    private string BuildFingerprint()
    {
        var values = new List<string>
        {
            "setup-plan-instance-v1", DesiredStateFingerprint,
            CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            HostFactsFingerprint, Disposition.ToString(),
            DiskBudget.HostAvailableBytes?.ToString(CultureInfo.InvariantCulture) ?? "null"
        };
        foreach (var prerequisite in Prerequisites)
            values.AddRange(["p", prerequisite.Id, prerequisite.State.ToString(),
                prerequisite.Observation, prerequisite.Remedy]);
        foreach (var blocker in Blockers)
            values.AddRange(["b", blocker.Code.ToString(), blocker.Summary, blocker.Remedy]);
        return FingerprintBuilder.Create(values);
    }
}

internal static class SetupGuard
{
    internal static void Require(bool condition, SetupFailure failure)
    {
        if (!condition) throw new SetupException(failure);
    }

    internal static void Text(string? value, int maximum, SetupFailure failure, bool allowEmpty = false) =>
        Require(value is not null && value.Length <= maximum && (allowEmpty || value.Length != 0) &&
            !value.Any(char.IsControl), failure);

    internal static void Identifier(string? value, SetupFailure failure)
    {
        Text(value, 64, failure);
        Require(value![0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
            value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.'),
            failure);
    }

    internal static void Fingerprint(string? value, SetupFailure failure) =>
        Require(value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), failure);

    internal static void UnixPath(string? value, SetupFailure failure)
    {
        Text(value, 256, failure);
        Require(value!.StartsWith("/", StringComparison.Ordinal) && value != "/" &&
            !value.EndsWith("/", StringComparison.Ordinal) && !value.Contains("//", StringComparison.Ordinal) &&
            value.Split('/').Skip(1).All(segment => segment.Length is > 0 and <= 64 &&
                segment is not "." and not ".." &&
                segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')),
            failure);
    }
}

internal static class FingerprintBuilder
{
    internal static string Create(params string[] values) => Create((IEnumerable<string>)values);

    internal static string Create(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string Bytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
