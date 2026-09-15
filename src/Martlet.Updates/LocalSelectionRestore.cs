using System.Security.Cryptography;
using Martlet.Core.Settings;

namespace Martlet.Updates;

public sealed partial class LocalSelectionEngine
{
    private Action? restoreValidation;
    private ConfigurationRestoreCleanup? restoreCleanup;
    private SetupOperationRunner? cleanupEffects;
    private Guid cleanupOperation;
    internal TimeProvider RestoreClock { get; init; } = TimeProvider.System;

    public RollbackRestoreOperation<RollbackRestorePlan> PreviewRollbackConfigurationRestore(
        long expectedSelectionRevision, SetupOperationRunner sharedEffects)
    {
        ArgumentNullException.ThrowIfNull(sharedEffects);
        var lifetime = new RestoreLifetime(RestoreClock);
        return StartRestoreWork(sharedEffects, (token, admittedSequence) => RunOwned(() =>
        {
            lifetime.Check();
            using var owner = LockRoot();
            var before = ReadState(token);
            RequireRestoreAvailable(before, expectedSelectionRevision);
            RequireCapacity();
            return WithRetainedEvidence(before, null, (_, reverify) =>
            {
                using var settingsPin = PinSettings(token);
                var current = CurrentSettings(token);
                if (current.Settings!.Profile.Id != before.ProfileId)
                    throw new SelectionException(SelectionFailure.InvalidSnapshot);
                var binding = before.Current!.Snapshot;
                using var source = PinSnapshot(binding, token);
                source.Position = 0;
                var inspection = ConfigurationSnapshot.Inspect(
                    BoundedIo.Read(source, ConfigurationSnapshot.MaximumBytes, token));
                var preview = settings.PreviewConfigurationRestoreAsync(SnapshotPath(binding), token)
                    .GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                lifetime.Check();
                if (preview.ExpectedRevision != current.Revision || preview.ProfileId != before.ProfileId ||
                    preview.Destination != settings.FilePath || preview.SnapshotDigest != binding.ManifestDigest)
                    throw new SelectionException(SelectionFailure.Conflict);
                RequireSettings(preview.ExpectedRevision, preview.ProfileId, token);
                RequireState(before, token);
                reverify();
                lifetime.Check();
                return new RollbackRestorePlan(this, sharedEffects, admittedSequence, before, preview,
                    current.Settings.SchemaVersion, inspection, lifetime);
            }, token);
        }, token, preserveRecovery: true), _ => throw new SelectionException(SelectionFailure.Unavailable),
            invalidatePreviews: true);
    }

    public RollbackRestoreOperation<RollbackRestoreResult> RestoreRollbackConfiguration(
        RollbackRestorePlan plan, RollbackRestoreApproval approval, SetupOperationRunner sharedEffects)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(sharedEffects);
        var coreApproval = approval.Consume(plan);
        if (!ReferenceEquals(plan.Owner, this) || !ReferenceEquals(plan.Effects, sharedEffects))
            throw new SelectionException(SelectionFailure.Conflict);
        return StartRestoreWork(sharedEffects, (token, _) => RunOwned(() =>
        {
            if (plan.Sequence != Volatile.Read(ref sequence))
                throw new SelectionException(SelectionFailure.Conflict);
            void Check()
            {
                token.ThrowIfCancellationRequested();
                plan.Lifetime.Check();
            }
            Check();
            using var owner = LockRoot();
            RequireState(plan.Before, token);
            RequireRestoreAvailable(plan.Before, plan.ExpectedSelectionRevision);
            RequireCapacity();
            using var history = PinHistory(token);
            restoreValidation = Check;
            try
            {
                return WithRetainedEvidence(plan.Before, null, (_, reverify) =>
                    RestoreOwned(plan, coreApproval, sharedEffects, reverify, Check, token), token);
            }
            finally { restoreValidation = null; }
        }, token, plan.OperationId, preserveRecovery: true), result => result.WithOwnerFailure());
    }

    private RollbackRestoreResult RestoreOwned(RollbackRestorePlan plan, ConfigurationRestoreApproval approval,
        SetupOperationRunner effects, Action reverify, Action check, CancellationToken token)
    {
        var scope = settings.OpenConfigurationRestoreAsync(plan.CorePlan, approval, token).GetAwaiter().GetResult();
        ConfigurationRestoreEvidence? evidence = null;
        SelectionReceipt? selection = null;
        var acknowledgment = RollbackAcknowledgment.NotRecorded;
        try
        {
            check();
            if (scope.Destination != settings.FilePath || scope.ProfileId != plan.ProfileId ||
                scope.ExpectedRevision != plan.ExpectedSettingsRevision ||
                scope.SourceFileDigest != plan.Snapshot.FileDigest || scope.CandidateDigest != plan.CandidateDigest)
                throw new SelectionException(SelectionFailure.Conflict);
            var before = plan.Before;
            var format = Math.Max(3, before.FormatVersion);
            var pending = before with { FormatVersion = format, Revision = before.Revision + 1, Pending = plan.OperationId };
            var after = before with
            {
                FormatVersion = format, Revision = before.Revision + 2, Pending = null, LastTransaction = plan.OperationId,
                Current = before.Current! with { RestoreRequired = false }
            };
            var binding = new RestoreTransactionBinding(before.Current!.Snapshot, plan.ExpectedSettingsRevision,
                plan.CandidateDigest, Path.GetFileName(scope.OriginalSnapshotPath));
            if (OriginalPath(binding.OriginalFileName) != scope.OriginalSnapshotPath)
                throw new SelectionException(SelectionFailure.Conflict);
            var journal = new SelectionJournal(format, plan.OperationId, plan.PlanDigest, before, pending, after);
            ValidateRestoreJournal(journal, binding);
            var directory = TransactionDirectory(plan.OperationId);
            if (LocalPaths.Exists(directory)) throw new SelectionException(SelectionFailure.Conflict);
            Point(SelectionIoPoint.BeforeCreate, directory, token);
            Directory.CreateDirectory(directory);
            Point(SelectionIoPoint.AfterCreate, directory, token);
            WriteNew(Path.Combine(directory, "before.json"), Wire.Write(before), token);
            var journalBytes = JournalBytes(journal, binding);
            WriteNew(Path.Combine(directory, JournalName(format)), journalBytes, token);
            using var journalPin = BoundedIo.OpenRead(Path.Combine(directory, JournalName(format)), 1);
            using var beforePin = BoundedIo.OpenRead(Path.Combine(directory, "before.json"), 1);
            var pendingBytes = Wire.Write(pending);
            WriteNew(Path.Combine(directory, "pending.json"), pendingBytes, token);
            using (var scratch = PinReplacement(Path.Combine(directory, "pending.json"), pendingBytes, token))
            {
                Point(SelectionIoPoint.BeforePendingReplace, directory, token);
                Recheck(before);
                Publish(scratch, pendingBytes, Path.Combine(directory, "pending-publication.json"), before, token);
            }
            Point(SelectionIoPoint.AfterPendingReplace, directory, token);
            using var pendingPublication = BoundedIo.OpenRead(Path.Combine(directory, "pending-publication.json"), 1);
            using var pendingPin = BoundedIo.OpenRead(ControlPath, 1);
            Point(SelectionIoPoint.BeforeConfigurationRestore, directory, token);
            scope.CommitAsync(check, () => Recheck(pending)).GetAwaiter().GetResult();
            var observed = scope.VerifyCommittedAsync().GetAwaiter().GetResult();
            RequireCoreEvidence(observed, journal, binding);
            evidence = observed;
            Point(SelectionIoPoint.AfterConfigurationRestore, directory, token);
            Recheck(pending);
            var committedBytes = Wire.Write(CommitDocument(journal, binding));
            using var committedPin = WritePublication(Path.Combine(directory, "restore-committed.json"), committedBytes, token);
            Point(SelectionIoPoint.BeforeRestoreAcknowledgment, directory, token);
            observed = scope.VerifyCommittedAsync().GetAwaiter().GetResult();
            RequireCoreEvidence(observed, journal, binding);
            evidence = observed;
            Recheck(pending);
            var afterBytes = Wire.Write(after);
            WriteNew(Path.Combine(directory, "selected.json"), afterBytes, token);
            using var replacement = PinReplacement(Path.Combine(directory, "selected.json"), afterBytes, token);
            Point(SelectionIoPoint.BeforeSelectionReplace, directory, token);
            Recheck(pending);
            scope.VerifyCommittedAsync().GetAwaiter().GetResult();
            check();
            pendingPin.Dispose();
            acknowledgment = RollbackAcknowledgment.PublicationAmbiguous;
            Publish(replacement, afterBytes, Path.Combine(directory, "selected-publication.json"), pending, token, () =>
            {
                acknowledgment = RollbackAcknowledgment.Recorded;
                selection = new(after, SelectionOutcome.Committed, RollbackRestoreProgress.Recorded);
            });
            Io?.Invoke(SelectionIoPoint.AfterSelectionReplace, directory, CancellationToken.None);
            return Result();

            void Recheck(ControlDocument state)
            {
                check();
                RequireState(state, token);
                using var named = BoundedIo.OpenRead(Path.Combine(directory, JournalName(format)), 1);
                if (!BoundedIo.Read(named, MaximumJournalBytes, token).AsSpan().SequenceEqual(journalBytes))
                    throw new SelectionException(SelectionFailure.InvalidControl);
                reverify();
                check();
            }
        }
        catch (Exception error) when (RestoreError(error))
        {
            if (evidence is null && scope.CommitState is (ConfigurationRestoreCommitState.ReplacementReturned or
                ConfigurationRestoreCommitState.VerifiedCommitted))
            {
                try { evidence = scope.VerifyCommittedAsync().GetAwaiter().GetResult(); }
                catch (RecoveryException) { evidence = null; }
            }
            if (scope.Cleanup is { IsPending: true } cleanup)
            {
                restoreCleanup = cleanup;
                cleanupEffects = effects;
                cleanupOperation = plan.OperationId;
            }
            return Result(error);
        }
        finally { scope.DisposeAsync().GetAwaiter().GetResult(); }

        RollbackRestoreResult Result(Exception? error = null)
        {
            var progress = evidence is not null ? RollbackSettingsProgress.VerifiedCommitted : scope.CommitState switch
            {
                ConfigurationRestoreCommitState.NotStarted => RollbackSettingsProgress.NotAttempted,
                ConfigurationRestoreCommitState.NotCommitted => RollbackSettingsProgress.NotCommitted,
                _ => RollbackSettingsProgress.OutcomeUnproven
            };
            var failure = error switch
            {
                SelectionException selected => selected.Failure,
                OperationCanceledException => SelectionFailure.Cancelled,
                UnauthorizedAccessException => SelectionFailure.AccessDenied,
                IOException io => (io.HResult & 0xffff) is 112 or 39 or 28
                    ? SelectionFailure.InsufficientDisk : SelectionFailure.Unavailable,
                _ => (SelectionFailure?)null
            };
            if (acknowledgment == RollbackAcknowledgment.Recorded && failure is SelectionFailure.Cancelled or SelectionFailure.ConsentExpired)
                failure = null;
            else if (progress == RollbackSettingsProgress.VerifiedCommitted && failure is SelectionFailure.Cancelled or SelectionFailure.ConsentExpired)
                failure = SelectionFailure.ConfigurationRestoreReconciliationRequired;
            return new(plan.OperationId, progress, acknowledgment, selection, evidence, restoreCleanup?.Path,
                failure, (error as RecoveryException)?.Failure, (error as StagingException)?.Failure);
        }
    }

    public RollbackRestoreOperation<RollbackRestoreResult> RetryRollbackRestoreCleanup(SetupOperationRunner sharedEffects)
    {
        ArgumentNullException.ThrowIfNull(sharedEffects);
        return StartRestoreWork(sharedEffects, (token, _) =>
        {
            if (!ReferenceEquals(cleanupEffects, sharedEffects) || restoreCleanup is not { IsPending: true } cleanup)
                throw new SelectionException(SelectionFailure.Conflict);
            using var owner = LockRoot();
            cleanup.RetryAsync(token).GetAwaiter().GetResult();
            restoreCleanup = null;
            cleanupEffects = null;
            return new RollbackRestoreResult(cleanupOperation, RollbackSettingsProgress.OutcomeUnproven,
                RollbackAcknowledgment.NotRecorded, null, null, null);
        }, result => result.WithOwnerFailure());
    }

    private RollbackRestoreOperation<T> StartRestoreWork<T>(SetupOperationRunner effects,
        Func<CancellationToken, long, T> action, Func<T, T> ownerFailed, bool invalidatePreviews = false)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new SelectionException(SelectionFailure.Busy);
        SetupOperation? operation = null;
        try
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var admitted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation = effects.TryStart(async token =>
            {
                var admittedSequence = await admitted.Task.ConfigureAwait(false);
                try
                {
                    result.SetResult(action(token, admittedSequence));
                    return new SetupWorkResult(SetupWorkOutcome.Completed);
                }
                catch (Exception error) when (RestoreError(error))
                {
                    result.SetException(error);
                    return new SetupWorkResult(SetupWorkOutcome.Failed);
                }
                finally
                {
                    if (!result.Task.IsCompleted)
                        result.TrySetException(new SelectionException(SelectionFailure.Unavailable));
                }
            });
            if (operation is null) throw new SelectionException(SelectionFailure.Busy);
            // Admission, not worker execution, invalidates previews. Ownership prevents older
            // restores from reentering even if a pre-start cancellation already retired the runner.
            var admittedSequence = invalidatePreviews ? Interlocked.Increment(ref sequence) : Volatile.Read(ref sequence);
            admitted.SetResult(admittedSequence);
            return new(operation, result.Task, ownerFailed, () => Volatile.Write(ref busy, 0));
        }
        finally { if (operation is null) Volatile.Write(ref busy, 0); }
    }

    private static bool RestoreError(Exception error) =>
        error is SelectionException or StagingException or RecoveryException or OperationCanceledException or
            IOException or UnauthorizedAccessException;

    private static void RequireRestoreAvailable(ControlDocument state, long revision)
    {
        if (state.Pending is not null)
            throw new SelectionException(SelectionFailure.ConfigurationRestoreReconciliationRequired, state.Pending);
        if (state.Revision != revision || state.Current?.RestoreRequired != true || state.LastTransaction is null)
            throw new SelectionException(SelectionFailure.Conflict);
        if (state.Revision > long.MaxValue - 2) throw new SelectionException(SelectionFailure.CapacityExceeded);
        Compatible(state.Current.MinimumReader, state.Current.MaximumReader, state.Current.Snapshot.Schema);
        Compatible(state.Current.MinimumReader, state.Current.MaximumReader, AppSettings.CurrentSchemaVersion);
    }

    private static string JournalName(int format) => format switch
    {
        2 => JournalFile, 3 => JournalV3File, 4 => JournalV4File,
        _ => throw new SelectionException(SelectionFailure.InvalidControl)
    };

    private static byte[] JournalBytes(SelectionJournal journal, RestoreTransactionBinding? restore = null,
        RestoreAcknowledgmentBinding? acknowledgment = null) =>
        journal.FormatVersion == 2 ? Wire.Write(journal) :
        journal.FormatVersion == 3 ? Wire.Write(new SelectionJournalV3(3,
            journal.TransactionId, restore is null ? SelectionTransactionKind.Selection : SelectionTransactionKind.ConfigurationRestore,
            journal.PlanDigest, journal.Before, journal.Pending!, journal.After, restore)) :
        Wire.Write(new SelectionJournalV4(4, journal.TransactionId,
            acknowledgment is not null ? SelectionTransactionKind.ConfigurationAcknowledgment :
            restore is null ? SelectionTransactionKind.Selection : SelectionTransactionKind.ConfigurationRestore,
            journal.PlanDigest, journal.Before, journal.Pending, journal.After, restore, acknowledgment));

    private RestoreTransactionBinding? RestoreBinding(Guid id, CancellationToken token)
    {
        var path = SafePath(Path.Combine(TransactionDirectory(id), JournalV3File));
        return LocalPaths.Exists(path) ? ReadDocument<SelectionJournalV3>(path, MaximumJournalBytes, token).Restore :
            LocalPaths.Exists(SafePath(Path.Combine(TransactionDirectory(id), JournalV4File))) ? ReadV4(id, token).Restore : null;
    }

    private void ValidateRestoreJournal(SelectionJournal journal, RestoreTransactionBinding binding)
    {
        var before = journal.Before;
        if (before.Current?.RestoreRequired != true || before.LastTransaction is null ||
            binding.Snapshot != before.Current.Snapshot || !UpperHash(binding.ExpectedRevision) ||
            !UpperHash(binding.CandidateDigest) ||
            !Same(journal.After, before with
            {
                FormatVersion = Math.Max(3, before.FormatVersion), Revision = before.Revision + 2, Pending = null,
                LastTransaction = journal.TransactionId, Current = before.Current with { RestoreRequired = false }
            }))
            throw new SelectionException(SelectionFailure.InvalidControl);
        OriginalPath(binding.OriginalFileName);
        Compatible(before.Current.MinimumReader, before.Current.MaximumReader, AppSettings.CurrentSchemaVersion);
    }

    private string OriginalPath(string name)
    {
        if (name is null || name.Length != 54 || !name.StartsWith("settings.recovery.", StringComparison.Ordinal) ||
            !name.EndsWith(".bak", StringComparison.Ordinal) || !Wire.IsHex(name[18..50], 32))
            throw new SelectionException(SelectionFailure.InvalidControl);
        return SafePath(Path.Combine(settings.DataDirectory, name));
    }

    private static RestoreCommitDocument CommitDocument(SelectionJournal journal, RestoreTransactionBinding binding) =>
        new(1, journal.TransactionId, journal.PlanDigest, journal.Before.ProfileId, binding.CandidateDigest,
            AppSettings.CurrentSchemaVersion, binding.OriginalFileName, binding.ExpectedRevision);

    private void RequireCoreEvidence(ConfigurationRestoreEvidence evidence, SelectionJournal journal,
        RestoreTransactionBinding binding)
    {
        if (evidence.Path != settings.FilePath || evidence.ProfileId != journal.Before.ProfileId ||
            evidence.SchemaVersion != AppSettings.CurrentSchemaVersion || evidence.Revision != binding.CandidateDigest ||
            evidence.OriginalRevision != binding.ExpectedRevision ||
            evidence.OriginalSnapshot != OriginalPath(binding.OriginalFileName))
            throw new SelectionException(SelectionFailure.Conflict);
    }

    private void ValidateRestoreCommit(SelectionJournal journal, RestoreTransactionBinding binding, CancellationToken token)
    {
        var path = Path.Combine(TransactionDirectory(journal.TransactionId), "restore-committed.json");
        var committed = ReadDocument<RestoreCommitDocument>(path, MaximumControlBytes, token);
        if (committed != CommitDocument(journal, binding))
            throw new SelectionException(SelectionFailure.InvalidControl);
        using var original = BoundedIo.OpenRead(OriginalPath(binding.OriginalFileName), 1);
        if (Convert.ToHexString(SHA256.HashData(BoundedIo.Read(original, AppSettings.MaxFileBytes, token))) != binding.ExpectedRevision)
            throw new SelectionException(SelectionFailure.InvalidControl);
    }

    private SelectionReceipt Receipt(ControlDocument state, SelectionOutcome outcome, CancellationToken token)
    {
        if (state.Pending is null && state.LastTransaction is { } last &&
            AcknowledgmentBinding(last, token) is { } acknowledged)
            return new(state, outcome, RollbackRestoreProgress.AcknowledgedVerifiedState, acknowledged.RestoreTransactionId);
        var progress = RollbackRestoreProgress.None;
        if ((state.Pending ?? state.LastTransaction) is { } id && RestoreBinding(id, token) is not null)
        {
            progress = state.Pending is null ? RollbackRestoreProgress.Recorded :
                LocalPaths.Exists(Path.Combine(TransactionDirectory(id), "restore-committed.json"))
                    ? RollbackRestoreProgress.SettingsCommittedSelectionPending : RollbackRestoreProgress.OutcomeUnproven;
        }
        return new(state, outcome, progress);
    }

    private HistoryPins PinHistory(CancellationToken token)
    {
        var owned = new HistoryPins();
        var transferred = false;
        try
        {
            foreach (var directory in Children())
            {
                var name = Path.GetFileName(directory);
                if (name.Length != 44 || !name.StartsWith("transaction-", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name[12..], "N", out var id) || name != TransactionName(id))
                    continue;
                foreach (var file in new[] { "before.json", JournalFile, JournalV3File, JournalV4File, "pending-publication.json",
                    "selected-publication.json", "restore-committed.json" }
                    .Concat(Enumerable.Range(0, 8).Select(index => $"recovered-{index}.json.publication")))
                {
                    token.ThrowIfCancellationRequested();
                    var path = SafePath(Path.Combine(directory, file));
                    if (LocalPaths.Exists(path)) owned.Add(BoundedIo.OpenRead(path, 1));
                }
                if (LocalPaths.Exists(Path.Combine(directory, "restore-committed.json")) &&
                    RestoreBinding(id, token) is { } restore)
                    owned.Add(BoundedIo.OpenRead(OriginalPath(restore.OriginalFileName), 1));
            }
            transferred = true;
            return owned;
        }
        finally { if (!transferred) owned.Dispose(); }
    }

    private sealed class HistoryPins : IDisposable
    {
        private readonly List<FileStream> pins = [];
        internal void Add(FileStream pin) => pins.Add(pin);
        public void Dispose()
        {
            foreach (var pin in pins) pin.Dispose();
        }
    }
}
