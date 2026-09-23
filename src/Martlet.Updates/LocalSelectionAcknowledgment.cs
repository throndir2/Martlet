using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Updates;

public sealed partial class LocalSelectionEngine
{
    private static readonly string[] HistoryFiles = new[]
    {
        "before.json", JournalFile, JournalV3File, JournalV4File, "configuration.martlet-config",
        "pending.json", "selected.json", "pending-publication.json", "selected-publication.json", "restore-committed.json"
    }.Concat(Enumerable.Range(0, 8).SelectMany(index => new[]
        { $"recovered-{index}.json", $"recovered-{index}.json.publication" })).ToArray();

    public RollbackRestoreOperation<RollbackAcknowledgmentPlan> PreviewRollbackConfigurationAcknowledgment(
        long expectedSelectionRevision, SetupOperationRunner sharedEffects)
    {
        ArgumentNullException.ThrowIfNull(sharedEffects);
        var lifetime = new RestoreLifetime(RestoreClock);
        return StartRestoreWork(sharedEffects, (token, admittedSequence) => RunOwned(() =>
        {
            lifetime.Check();
            using var owner = LockRoot();
            var before = ReadState(token);
            RequireAcknowledgmentAvailable(before, expectedSelectionRevision);
            RequireCapacity();
            using var history = PinHistory(token);
            return WithRetainedEvidence(before, null, (_, reverify) =>
            {
                var source = ReadAcknowledgmentSource(before, token);
                using var original = BoundedIo.OpenRead(OriginalPath(source.OriginalFileName), 1);
                using var snapshot = PinSnapshot(source.Snapshot, token);
                var scope = settings.OpenCurrentConfigurationReadAsync(token).GetAwaiter().GetResult();
                try
                {
                    RequireCurrentAcknowledgment(scope.Inspection, before, source);
                    var revision = NextAcknowledgmentRevision(source.RestoreTerminalRevision, token);
                    var digest = AcknowledgmentHistoryDigest(token);
                    source = source with { HistoryDigest = digest };
                    Point(SelectionIoPoint.BeforeAcknowledgmentVerification, root, token);
                    var current = scope.VerifyAsync().GetAwaiter().GetResult();
                    RequireCurrentAcknowledgment(current, before, source);
                    RequireState(before, token);
                    RequireAcknowledgmentSource(source, before, token);
                    reverify();
                    if (AcknowledgmentHistoryDigest(token) != digest)
                        throw new SelectionException(SelectionFailure.Conflict);
                    lifetime.Check();
                    snapshot.Position = 0;
                    var inspection = ConfigurationSnapshot.Inspect(
                        BoundedIo.Read(snapshot, ConfigurationSnapshot.MaximumBytes, token));
                    token.ThrowIfCancellationRequested();
                    lifetime.Check();
                    return new RollbackAcknowledgmentPlan(this, sharedEffects, admittedSequence, before, source,
                        revision, OriginalPath(source.OriginalFileName), current.CurrentJson, inspection, lifetime);
                }
                finally { scope.DisposeAsync().GetAwaiter().GetResult(); }
            }, token);
        }, token, preserveRecovery: true), _ => throw new SelectionException(SelectionFailure.Unavailable),
            invalidatePreviews: true);
    }

    public RollbackRestoreOperation<RollbackAcknowledgmentResult> AcknowledgeRollbackConfiguration(
        RollbackAcknowledgmentPlan plan, RollbackAcknowledgmentApproval approval, SetupOperationRunner sharedEffects)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(sharedEffects);
        approval.Consume(plan);
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
            RequireAcknowledgmentAvailable(plan.Before, plan.ExpectedSelectionRevision);
            RequireCapacity();
            using var history = PinHistory(token);
            RequireAcknowledgmentSource(plan.Binding, plan.Before, token);
            if (AcknowledgmentHistoryDigest(token) != plan.HistoryDigest ||
                NextAcknowledgmentRevision(plan.SkippedRestoreTerminalRevision, token) != plan.ProposedSelectionRevision)
                throw new SelectionException(SelectionFailure.Conflict);
            restoreValidation = Check;
            try
            {
                return WithRetainedEvidence(plan.Before, null, (_, reverify) =>
                    AcknowledgeOwned(plan, reverify, Check, token), token);
            }
            finally { restoreValidation = null; }
        }, token, plan.OperationId, preserveRecovery: true), result => result.WithOwnerFailure());
    }

    private RollbackAcknowledgmentResult AcknowledgeOwned(RollbackAcknowledgmentPlan plan,
        Action reverify, Action check, CancellationToken token)
    {
        ConfigurationCurrentReadScope? scope = null;
        ConfigurationCurrentInspection? observed = null;
        SelectionReceipt? selection = null;
        var acknowledgment = RollbackAcknowledgment.NotRecorded;
        var historical = false;
        Exception? operationError = null;
        var ownershipFailed = false;
        try
        {
            check();
            RequireAcknowledgmentSource(plan.Binding, plan.Before, token);
            historical = true;
            using var original = BoundedIo.OpenRead(plan.OriginalSnapshotPath, 1);
            using var snapshot = PinSnapshot(plan.Binding.Snapshot, token);
            using var control = BoundedIo.OpenRead(ControlPath, 1);
            var currentScope = settings.OpenCurrentConfigurationReadAsync(token).GetAwaiter().GetResult();
            scope = currentScope;
            VerifyCurrent();
            var after = plan.Before with
            {
                FormatVersion = 4, Revision = plan.ProposedSelectionRevision, Pending = null,
                LastTransaction = plan.OperationId, Current = plan.Before.Current! with { RestoreRequired = false }
            };
            var journal = new SelectionJournal(4, plan.OperationId, plan.PlanDigest, plan.Before, null, after);
            ValidateAcknowledgmentJournal(journal, plan.Binding, token);
            var journalBytes = JournalBytes(journal, acknowledgment: plan.Binding);
            var beforeBytes = Wire.Write(plan.Before);
            var afterBytes = Wire.Write(after);
            if (journalBytes.Length > MaximumJournalBytes || beforeBytes.Length > MaximumControlBytes ||
                afterBytes.Length > MaximumControlBytes)
                throw new SelectionException(SelectionFailure.CapacityExceeded);
            var directory = TransactionDirectory(plan.OperationId);
            if (LocalPaths.Exists(directory)) throw new SelectionException(SelectionFailure.Conflict);
            RecheckBefore();
            Point(SelectionIoPoint.BeforeCreate, directory, token);
            RequireCapacity();
            Directory.CreateDirectory(directory);
            Point(SelectionIoPoint.AfterCreate, directory, token);
            WriteNew(Path.Combine(directory, "before.json"), beforeBytes, token);
            WriteNew(Path.Combine(directory, JournalV4File), journalBytes, token);
            using var beforePin = BoundedIo.OpenRead(Path.Combine(directory, "before.json"), 1);
            using var journalPin = BoundedIo.OpenRead(Path.Combine(directory, JournalV4File), 1);
            WriteNew(Path.Combine(directory, "selected.json"), afterBytes, token);
            using var replacement = PinReplacement(Path.Combine(directory, "selected.json"), afterBytes, token);
            Point(SelectionIoPoint.BeforeAcknowledgmentPublication, directory, token);
            RecheckBefore();
            var intent = Path.Combine(directory, "selected-publication.json");
            try
            {
                Publish(replacement, afterBytes, intent, plan.Before, token, () =>
                {
                    acknowledgment = RollbackAcknowledgment.Recorded;
                    selection = new(after, SelectionOutcome.Committed, RollbackRestoreProgress.AcknowledgedVerifiedState,
                        plan.InterruptedRestoreTransactionId);
                }, () =>
                {
                    // This is only our exact, owned publication, not authority to ignore an unmatched intent.
                    RequireReplacementBytes(control, beforeBytes, token);
                    using (var actual = PinReplacement(ControlPath, beforeBytes, token)) { }
                    RecheckEvidence();
                    RequireJournalBytes();
                    ValidateHistory(after, token);
                    check();
                    control.Dispose();
                });
            }
            catch
            {
                if (acknowledgment != RollbackAcknowledgment.Recorded && LocalPaths.Exists(intent))
                    acknowledgment = RollbackAcknowledgment.PublicationAmbiguous;
                throw;
            }
            Io?.Invoke(SelectionIoPoint.AfterAcknowledgmentPublication, directory, CancellationToken.None);

            void RequireJournalBytes()
            {
                using var named = BoundedIo.OpenRead(Path.Combine(directory, JournalV4File), 1);
                if (!BoundedIo.Read(named, MaximumJournalBytes, token).AsSpan().SequenceEqual(journalBytes))
                    throw new SelectionException(SelectionFailure.InvalidControl);
            }
            void RecheckBefore()
            {
                check();
                RequireState(plan.Before, token);
                RecheckEvidence();
            }
            void RecheckEvidence()
            {
                check();
                VerifyCurrent();
                reverify();
                RequireAcknowledgmentSource(plan.Binding, plan.Before, token);
                if (AcknowledgmentHistoryDigest(token, plan.OperationId) != plan.HistoryDigest)
                    throw new SelectionException(SelectionFailure.Conflict);
                check();
            }
            void VerifyCurrent()
            {
                Point(SelectionIoPoint.BeforeAcknowledgmentVerification, root, token);
                var fresh = currentScope.VerifyAsync().GetAwaiter().GetResult();
                RequireCurrentAcknowledgment(fresh, plan.Before, plan.Binding);
                observed = fresh;
                check();
            }
        }
        catch (Exception error) when (RestoreError(error))
        {
            operationError = error;
        }
        finally
        {
            try { scope?.DisposeAsync().GetAwaiter().GetResult(); }
            catch (Exception error) when (RestoreError(error))
            {
                operationError ??= error;
                ownershipFailed = true;
            }
        }
        return Result(operationError);

        RollbackAcknowledgmentResult Result(Exception? error = null)
        {
            var failure = error switch
            {
                SelectionException selected => selected.Failure,
                OperationCanceledException => SelectionFailure.Cancelled,
                UnauthorizedAccessException => SelectionFailure.AccessDenied,
                IOException io => (io.HResult & 0xffff) is 112 or 39 or 28
                    ? SelectionFailure.InsufficientDisk : SelectionFailure.Unavailable,
                _ => (SelectionFailure?)null
            };
            if (acknowledgment == RollbackAcknowledgment.Recorded &&
                failure is SelectionFailure.Cancelled or SelectionFailure.ConsentExpired)
                failure = null;
            return new(plan, historical, observed, acknowledgment, selection, failure,
                (error as RecoveryException)?.Failure, (error as StagingException)?.Failure, ownershipFailed);
        }
    }

    private static void RequireAcknowledgmentAvailable(ControlDocument state, long revision)
    {
        if (state.Revision != revision) throw new SelectionException(SelectionFailure.Conflict);
        if (state.FormatVersion is not (3 or 4) || state.Pending is null ||
            state.Current?.RestoreRequired != true || state.LastTransaction is null)
            throw new SelectionException(SelectionFailure.ConfigurationRestoreReconciliationRequired, state.Pending);
    }

    private RestoreAcknowledgmentBinding ReadAcknowledgmentSource(ControlDocument state, CancellationToken token)
    {
        RequireAcknowledgmentAvailable(state, state.Revision);
        var id = state.Pending!.Value;
        var restore = RestoreBinding(id, token) ??
            throw new SelectionException(SelectionFailure.ConfigurationRestoreReconciliationRequired, id);
        var journal = ReadJournal(id, token);
        if (!Same(state, journal.Pending!)) throw new SelectionException(SelectionFailure.InvalidControl);
        var directory = TransactionDirectory(id);
        if (LocalPaths.Exists(Path.Combine(directory, "selected-publication.json")) ||
            Enumerable.Range(0, 8).Any(index => LocalPaths.Exists(Path.Combine(directory, $"recovered-{index}.json.publication"))))
            throw new SelectionException(SelectionFailure.InvalidControl);
        if (!LocalPaths.Exists(Path.Combine(directory, "restore-committed.json")))
            throw new SelectionException(SelectionFailure.ConfigurationRestoreReconciliationRequired, id);
        var committed = ValidateRestoreCommit(journal, restore, token);
        using var original = BoundedIo.OpenRead(OriginalPath(restore.OriginalFileName), 1);
        var bytes = BoundedIo.Read(original, AppSettings.MaxFileBytes, token);
        AppSettings prior;
        try { prior = SettingsJson.Read(bytes); }
        catch (ContractException) { throw new SelectionException(SelectionFailure.InvalidControl); }
        if (prior.Profile.Id != state.ProfileId || Convert.ToHexString(SHA256.HashData(bytes)) != restore.ExpectedRevision)
            throw new SelectionException(SelectionFailure.InvalidControl);
        return new(id, Wire.Hash(JournalBytes(journal, restore)), journal.PlanDigest,
            Wire.Hash(Wire.Write(committed)), Wire.Hash(Wire.Write(state)), journal.After.Revision,
            restore.Snapshot, restore.OriginalFileName, restore.ExpectedRevision, prior.SchemaVersion,
            restore.CandidateDigest, committed.SchemaVersion, staging.TrustPolicyDigest, "");
    }

    private void RequireAcknowledgmentSource(RestoreAcknowledgmentBinding expected, ControlDocument before,
        CancellationToken token)
    {
        var actual = ReadAcknowledgmentSource(before, token);
        if (actual != (expected with { HistoryDigest = "" }))
            throw new SelectionException(SelectionFailure.Conflict);
    }

    private static void RequireCurrentAcknowledgment(ConfigurationCurrentInspection actual, ControlDocument state,
        RestoreAcknowledgmentBinding source)
    {
        if (actual.Path != state.SettingsPath || actual.ProfileId != state.ProfileId ||
            actual.SchemaVersion != source.SettingsSchema || actual.Revision != source.SettingsRevision)
            throw new SelectionException(SelectionFailure.Conflict);
        Compatible(state.Current!.MinimumReader, state.Current.MaximumReader, actual.SchemaVersion);
    }

    private SelectionJournalV4 ReadV4(Guid id, CancellationToken token)
    {
        var document = ReadDocument<SelectionJournalV4>(Path.Combine(TransactionDirectory(id), JournalV4File),
            MaximumJournalBytes, token);
        if (document.FormatVersion != 4 || document.Kind is not (SelectionTransactionKind.Selection or
            SelectionTransactionKind.ConfigurationRestore or SelectionTransactionKind.ConfigurationAcknowledgment) ||
            (document.Kind == SelectionTransactionKind.ConfigurationRestore) != (document.Restore is not null) ||
            (document.Kind == SelectionTransactionKind.ConfigurationAcknowledgment) != (document.Acknowledgment is not null))
            throw new SelectionException(SelectionFailure.InvalidControl);
        return document;
    }

    private RestoreAcknowledgmentBinding? AcknowledgmentBinding(Guid id, CancellationToken token) =>
        LocalPaths.Exists(SafePath(Path.Combine(TransactionDirectory(id), JournalV4File))) ? ReadV4(id, token).Acknowledgment : null;

    private void ValidateAcknowledgmentJournal(SelectionJournal journal, RestoreAcknowledgmentBinding binding,
        CancellationToken token)
    {
        if (journal.FormatVersion != 4 || journal.Pending is not null ||
            journal.Before.Pending != binding.RestoreTransactionId || binding.RestoreTransactionId == journal.TransactionId ||
            !Wire.IsHash(binding.HistoryDigest) || !Wire.IsHash(binding.TrustPolicyDigest) ||
            journal.After.Revision <= binding.RestoreTerminalRevision ||
            journal.After.Revision <= journal.Before.Revision ||
            journal.Before.Current?.RestoreRequired != true ||
            !Same(journal.After, journal.Before with
            {
                FormatVersion = 4, Revision = journal.After.Revision, Pending = null, LastTransaction = journal.TransactionId,
                Current = journal.Before.Current with { RestoreRequired = false }
            }))
            throw new SelectionException(SelectionFailure.InvalidControl);
        RestoreAcknowledgmentBinding actual;
        try { actual = ReadAcknowledgmentSource(journal.Before, token); }
        catch (SelectionException error) when (error.Failure == SelectionFailure.ConfigurationRestoreReconciliationRequired)
        { throw new SelectionException(SelectionFailure.InvalidControl); }
        // Policy and whole-store census are historical review facts; later legitimate policy/history can change.
        if (actual != (binding with { TrustPolicyDigest = actual.TrustPolicyDigest, HistoryDigest = "" }))
            throw new SelectionException(SelectionFailure.InvalidControl);
    }

    private long NextAcknowledgmentRevision(long reserved, CancellationToken token)
    {
        var generations = new HashSet<long>();
        foreach (var directory in TransactionDirectories())
        {
            var id = Guid.ParseExact(Path.GetFileName(directory)[12..], "N");
            if (!LocalPaths.Exists(SafePath(Path.Combine(directory, JournalV4File)))) continue;
            var journal = ReadJournal(id, token);
            if (AcknowledgmentBinding(id, token) is null) continue;
            if (!generations.Add(journal.After.Revision)) throw new SelectionException(SelectionFailure.InvalidControl);
            reserved = Math.Max(reserved, journal.After.Revision);
        }
        if (reserved == long.MaxValue) throw new SelectionException(SelectionFailure.CapacityExceeded);
        return checked(reserved + 1);
    }

    private string[] TransactionDirectories() => Children().Where(path =>
    {
        var name = Path.GetFileName(path);
        return name.Length == 44 && name.StartsWith("transaction-", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[12..], "N", out var id) && name == TransactionName(id);
    }).Order(StringComparer.Ordinal).ToArray();

    private string AcknowledgmentHistoryDigest(CancellationToken token, Guid? ownedAcknowledgment = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var child in Children().Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(child);
            if (ownedAcknowledgment is { } owned && name == TransactionName(owned)) continue;
            Add(name);
            if (name == "selection.json") AddFile(child, MaximumControlBytes);
        }
        foreach (var directory in TransactionDirectories())
        {
            if (ownedAcknowledgment is { } owned && Path.GetFileName(directory) == TransactionName(owned)) continue;
            foreach (var file in HistoryFiles)
            {
                token.ThrowIfCancellationRequested();
                var path = SafePath(Path.Combine(directory, file));
                if (!LocalPaths.Exists(path)) continue;
                Add(Path.GetFileName(directory) + "/" + file);
                AddFile(path, MaximumJournalBytes);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        void AddFile(string path, int maximum)
        {
            using var file = BoundedIo.OpenRead(path, 1);
            Add(Wire.Hash(BoundedIo.Read(file, maximum, token)));
        }
    }
}
