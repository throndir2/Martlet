namespace Martlet.Host.Setup;

public enum SetupFailure
{
    InvalidConfiguration,
    InvalidPrerequisiteFacts,
    PlanBlocked,
    ConsentRequired,
    ConsentScopeMismatch,
    ConsentConsumed,
    PlanStale,
    PlanChanged,
    ConfigurationChanged,
    HostFactsChanged,
    ArtifactFactsChanged,
    JournalCorrupt,
    JournalTooLarge,
    JournalConcurrentChange,
    JournalDestinationChanged,
    JournalIoFailure,
    ExternalStateUnknown,
    ExternalStateConflict,
    PrivilegeRequired,
    DeferredStep,
    ExecutorRefused,
    StepFailed,
    Interrupted,
    JournalPurposeMismatch
}

public sealed record SetupRemedy(string Code, string Summary, string Instruction);

public static class SetupRemedies
{
    public static SetupRemedy For(SetupFailure failure) => failure switch
    {
        SetupFailure.JournalPurposeMismatch => new("SETUP_JOURNAL_PURPOSE",
            "This journal belongs to a different, unsupported purpose.",
            "Preserve its exact bytes. A review record or historical completion is never execution permission or observed ownership."),
        SetupFailure.InvalidConfiguration => new("SETUP_CONFIG_INVALID",
            "The proposed host setup configuration is invalid or outside H05a bounds.",
            "Correct the exact profile, revision, canonical Ubuntu paths, selected roles, and private gateway port; then build a new preview."),
        SetupFailure.InvalidPrerequisiteFacts => new("SETUP_FACTS_INVALID",
            "The H01 or H02 prerequisite facts are invalid or inconsistent.",
            "Preserve the source reports, rerun the matching read-only inspector, and build a new plan. Do not substitute guessed host or artifact values."),
        SetupFailure.PlanBlocked => new("SETUP_PLAN_BLOCKED",
            "The proposal contains unresolved prerequisite or deferred-mutation blockers.",
            "Review every blocker and its owning H01/H02/H05 slice. H05a cannot approve package, driver, Docker, download, firewall, service, model, or Compose mutation."),
        SetupFailure.ConsentRequired => new("SETUP_CONSENT_REQUIRED",
            "Approval defaults to No and no setup action was authorized.",
            "Review the exact immutable preview and explicitly approve it only when the listed scopes, privileges, resources, and no-rollback boundary are acceptable."),
        SetupFailure.ConsentScopeMismatch => new("SETUP_CONSENT_SCOPE",
            "The supplied consent scopes do not exactly match the reviewed plan.",
            "Preview again and approve exactly the displayed scopes. Do not add a blanket scope or reuse consent from another plan."),
        SetupFailure.ConsentConsumed => new("SETUP_CONSENT_CONSUMED",
            "This one-use approval was already consumed.",
            "Reconcile current state, create a fresh preview, and explicitly approve the remaining exact work."),
        SetupFailure.PlanStale => new("SETUP_PLAN_STALE",
            "The bounded setup preview expired or was created in the future.",
            "Rerun H01/H02 inspection as needed and create a new preview. Never execute an expired plan."),
        SetupFailure.PlanChanged => new("SETUP_PLAN_CHANGED",
            "The plan fingerprint no longer matches the approved or journaled plan.",
            "Preserve the journal, review the new complete plan, and provide fresh scoped approval. Do not splice steps into an existing journal."),
        SetupFailure.ConfigurationChanged => new("SETUP_CONFIG_CHANGED",
            "The desired host configuration changed after preview or journal creation.",
            "Preserve the journal and build a new plan from the current exact configuration revision, paths, roles, and port."),
        SetupFailure.HostFactsChanged => new("SETUP_HOST_FACTS_CHANGED",
            "The H01 host facts changed after preview or journal creation.",
            "Rerun read-only preflight, review the changed observations and remedies, and approve a newly fingerprinted plan."),
        SetupFailure.ArtifactFactsChanged => new("SETUP_ARTIFACT_FACTS_CHANGED",
            "The H02 artifact-manifest facts changed after preview or journal creation.",
            "Inspect the exact local manifest bytes again, review changed hashes/bytes/gaps/rights, and approve a newly fingerprinted plan."),
        SetupFailure.JournalCorrupt => new("SETUP_JOURNAL_CORRUPT",
            "The setup journal is malformed, inconsistent, or fails its integrity seal.",
            "Preserve the exact journal and owned pending file for review. Do not delete unrelated files, infer completed work, or overwrite the journal."),
        SetupFailure.JournalTooLarge => new("SETUP_JOURNAL_TOO_LARGE",
            "The setup journal exceeds the 256 KiB H05a limit.",
            "Preserve the file and investigate unexpected growth. H05a will not truncate or overwrite it."),
        SetupFailure.JournalConcurrentChange => new("SETUP_JOURNAL_CHANGED",
            "The setup journal changed after preview or an owned pending write already exists.",
            "Stop concurrent setup processes, preserve both exact files, then preview the authoritative journal again."),
        SetupFailure.JournalDestinationChanged => new("SETUP_JOURNAL_DESTINATION",
            "The selected setup journal path changed after preview.",
            "Preview the exact intended local journal path and approve again. Consent for one path never authorizes another destination."),
        SetupFailure.JournalIoFailure => new("SETUP_JOURNAL_IO",
            "The exact local setup journal could not be read or durably replaced.",
            "Check only the selected journal directory, storage availability, and permissions. Do not run setup as root merely to hide the failure."),
        SetupFailure.ExternalStateUnknown => new("SETUP_STATE_UNKNOWN",
            "The expected external state could not be established.",
            "Use a reviewed read-only state probe for the exact step. Unknown is not pending, complete, or permission to execute."),
        SetupFailure.ExternalStateConflict => new("SETUP_STATE_CONFLICT",
            "External state conflicts with the reviewed or previously completed step.",
            "Preserve the journal and conflicting state. Reconcile the named resource explicitly; H05a will not overwrite, remove, or rerun it blindly."),
        SetupFailure.PrivilegeRequired => new("SETUP_PRIVILEGE_REQUIRED",
            "The next step requires privilege beyond the approved operator boundary.",
            "Stop and review the exact administrator-owned step locally. Do not inject sudo, elevate automatically, grant Docker-group access, or reuse desktop credentials."),
        SetupFailure.DeferredStep => new("SETUP_STEP_DEFERRED",
            "The proposed step belongs to a later H05 slice and has no executable command.",
            "Complete and review the owning download, Compose, gateway, or lifecycle slice before producing a new actionable plan."),
        SetupFailure.ExecutorRefused => new("SETUP_EXECUTOR_REFUSED",
            "The injected operator-owned executor refused the exact command.",
            "Review the command and local policy. The production default is intentionally non-executing; never fall back to a shell or implicit elevation."),
        SetupFailure.StepFailed => new("SETUP_STEP_FAILED",
            "The injected executor did not establish the expected state.",
            "Inspect the stable step code and external state, then reconcile explicitly. No automatic rollback is available in H05a."),
        SetupFailure.Interrupted => new("SETUP_INTERRUPTED",
            "Setup was canceled or interrupted before completion.",
            "Create a fresh preview. Completed external state will be recognized idempotently; conflicting or unknown state will be refused."),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };
}

public sealed class SetupException : Exception
{
    public SetupFailure Failure { get; }
    public SetupRemedy Remedy => SetupRemedies.For(Failure);

    internal SetupException(SetupFailure failure) : base(SetupRemedies.For(failure).Summary) =>
        Failure = failure;
}
