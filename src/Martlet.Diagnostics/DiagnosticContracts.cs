using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Sessions;

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
    public string? DiagnosticCode { get; init; }
    public string? ActionId { get; init; }
    public string? Remedy { get; init; }
    public ProbeExecution? Execution { get; init; }
    public IReadOnlyList<ProbeEffect>? Effects { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? DurationMilliseconds { get; init; }
    public double? AgeMilliseconds { get; init; }
    public double? MaximumAgeMilliseconds { get; init; }
    public bool OperationStillRunning { get; init; }

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
        if (DiagnosticCode is not null)
            ContractRules.Identifier(DiagnosticCode);
        if (ActionId is not null)
            ContractRules.Identifier(ActionId);
        if (Remedy is not null)
            ContractRules.Text(Remedy, 512);
        ContractRules.Require(DiagnosticCode is null || ActionId is not null && !string.IsNullOrWhiteSpace(Remedy),
            "A catalog finding requires an action and user-safe remedy.");
        if (Execution is { } execution)
        {
            ContractRules.Defined(execution);
            ContractRules.Require(execution != ProbeExecution.Completed || StartedAt is not null && CompletedAt is not null,
                "Completed execution requires timing evidence.");
            ContractRules.Require(Outcome != ProbeOutcome.Passed || execution == ProbeExecution.Completed,
                "A pass requires completed execution.");
            ContractRules.Require(execution is not (ProbeExecution.Canceled or ProbeExecution.TimedOut) || Outcome == ProbeOutcome.Warning,
                "Interrupted execution must remain incomplete.");
            ContractRules.Require(execution != ProbeExecution.Faulted || Outcome == ProbeOutcome.Failed,
                "A faulted execution requires a failure.");
            ContractRules.Require(execution != ProbeExecution.Running || Outcome == ProbeOutcome.Running,
                "Running execution cannot carry a terminal outcome.");
            ContractRules.Require(execution != ProbeExecution.NotRun ||
                Outcome is ProbeOutcome.Skipped or ProbeOutcome.NotConfigured or ProbeOutcome.Unknown,
                "An unrun entry cannot claim completed work.");
            ContractRules.Require(execution is not (ProbeExecution.NotRun or ProbeExecution.Running or ProbeExecution.Canceled or ProbeExecution.TimedOut) ||
                !observed && ObservedAt is null, "Unfinished execution cannot claim observed evidence.");
        }
        if (Effects is not null)
        {
            ContractRules.Require(Effects.Count is > 0 and <= 5 && Effects.Distinct().Count() == Effects.Count, "Effects must be unique.");
            foreach (var effect in Effects)
                ContractRules.Defined(effect);
        }
        ContractRules.Require(CompletedAt is null || StartedAt is not null && CompletedAt >= StartedAt,
            "Completion cannot precede start.");
        ContractRules.Require(DurationMilliseconds is null || double.IsFinite(DurationMilliseconds.Value) && DurationMilliseconds >= 0,
            "Duration must be finite and nonnegative.");
        ContractRules.Require(AgeMilliseconds is null || ObservedAt is not null && double.IsFinite(AgeMilliseconds.Value) && AgeMilliseconds >= 0,
            "Age needs an observation and a nonnegative finite value.");
        ContractRules.Require(MaximumAgeMilliseconds is null || double.IsFinite(MaximumAgeMilliseconds.Value) && MaximumAgeMilliseconds is > 0 and <= 86_400_000,
            "Evidence expiry must be bounded.");
        ContractRules.Require(Freshness != EvidenceFreshness.Current || AgeMilliseconds is null || MaximumAgeMilliseconds is null ||
            AgeMilliseconds < MaximumAgeMilliseconds, "Expired evidence cannot claim current freshness.");
        ContractRules.Require(!OperationStillRunning || Execution is ProbeExecution.TimedOut or ProbeExecution.Canceled,
            "Only interrupted execution can retain an outstanding operation.");
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
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int ExitCode => DoctorExitCodes.Evaluate(this);
    public bool Ready => ExitCode == 0;
    public FixtureSessionSnapshot? Fixture { get; init; }
    public SetupStatus? Setup { get; init; }

    public void Validate()
    {
        ContractRules.Require(Version is not null, "A report version is required.");
        Version!.Validate();
        ContractRules.Identifier(ApplicationVersion);
        ContractRules.Require(CreatedAt != DateTimeOffset.MinValue, "A report timestamp is required.");
        ContractRules.Require(CompletedAt is null || StartedAt is not null && CompletedAt >= StartedAt,
            "Report completion cannot precede start.");
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
        Fixture?.Validate();
        Setup?.Validate();
        ContractRules.Require(Setup is null || SettingsState == SettingsLoadState.Loaded,
            "Setup metadata requires loaded settings.");
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
