using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

public enum ProbeOutcome { Passed, Failed, Warning, Unknown, Running, Skipped, NotConfigured }
public enum EvidenceFreshness { Unknown, Current, Stale }

public sealed record ProbeResult : IContract
{
    public required string Id { get; init; }
    public required Stage Stage { get; init; }
    public required bool Required { get; init; }
    public required ProbeOutcome Outcome { get; init; }
    public required EvidenceProvenance Provenance { get; init; }
    public required EvidenceFreshness Freshness { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public required string Summary { get; init; }
    public MartletError? Error { get; init; }

    public void Validate()
    {
        ContractRules.Identifier(Id);
        ContractRules.Defined(Stage);
        ContractRules.Defined(Outcome);
        ContractRules.Defined(Provenance);
        ContractRules.Defined(Freshness);
        ContractRules.Text(Summary, 512);
        ContractRules.Require(!string.IsNullOrWhiteSpace(Summary), "A probe result needs a summary.");
        var observed = Provenance is EvidenceProvenance.Fixture or EvidenceProvenance.Live;
        ContractRules.Require(!observed || ObservedAt is not null, "Observed evidence requires a timestamp.");
        ContractRules.Require(observed || (ObservedAt is null && Freshness == EvidenceFreshness.Unknown),
            "Unobserved evidence cannot have an observation timestamp or known freshness.");
        ContractRules.Require(ObservedAt != DateTimeOffset.MinValue, "An observation timestamp cannot be empty.");
        ContractRules.Require(Outcome != ProbeOutcome.Passed || (observed && Error is null), "A pass requires observed evidence and no error.");
        ContractRules.Require(Outcome != ProbeOutcome.Failed || Error is not null, "A failed probe needs an actionable error.");
        Error?.Validate();
        ContractRules.Require(Error is null || Error.Stage == Stage, "Probe and error stages must agree.");
    }
}

public sealed record DoctorReport : IContract
{
    public required ContractVersion Version { get; init; }
    public required string ApplicationVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public Guid? ProfileId { get; init; }
    public ProfileKind? ProfileKind { get; init; }
    public SettingsLoadState? SettingsState { get; init; }
    public required IReadOnlyList<ProbeResult> Probes { get; init; }
    public MartletError? InvocationError { get; init; }
    public int ExitCode => DoctorExitCodes.Evaluate(this);
    public bool Ready => ExitCode == 0;

    public void Validate()
    {
        ContractRules.Require(Version is not null, "A report version is required.");
        Version!.Validate();
        ContractRules.Identifier(ApplicationVersion);
        ContractRules.Require(CreatedAt != DateTimeOffset.MinValue, "A report timestamp is required.");
        ContractRules.Require(ProfileId != Guid.Empty && (ProfileId is null) == (ProfileKind is null), "Profile identity and kind must agree.");
        if (ProfileKind is { } kind)
            ContractRules.Defined(kind);
        if (SettingsState is { } state)
        {
            ContractRules.Defined(state);
            ContractRules.Require((state == SettingsLoadState.Loaded) == (ProfileId is not null),
                "Only loaded settings may expose profile identity.");
        }
        ContractRules.Require(Probes is { Count: <= 64 }, "At most 64 probes are supported.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var probe in Probes!)
        {
            ContractRules.Require(probe is not null, "A probe cannot be null.");
            probe!.Validate();
            ContractRules.Require(ids.Add(probe.Id), "Probe IDs must be unique.");
        }
        InvocationError?.Validate();
    }
}

public static class DoctorExitCodes
{
    public static int Evaluate(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        report.Validate();
        if (report.InvocationError is not null || report.SettingsState is SettingsLoadState.Invalid or SettingsLoadState.Inaccessible)
            return 3;
        if (report.Probes.Any(probe => probe.Outcome == ProbeOutcome.Failed))
            return 1;
        if (!report.Probes.Any(probe => probe.Required) || report.Probes.Any(probe =>
            probe.Outcome != ProbeOutcome.Passed || probe.Freshness != EvidenceFreshness.Current ||
            probe.Provenance is not (EvidenceProvenance.Live or EvidenceProvenance.Fixture)))
            return 2;
        return 0;
    }
}
