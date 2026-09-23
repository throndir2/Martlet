using Martlet.Core.Settings;

namespace Martlet.Updates;

internal enum SelectionIoPoint
{
    BeforeCreate, AfterCreate, BeforeWrite, AfterWrite, BeforeFlush, AfterFlush,
    BeforePendingReplace, AfterPendingReplace, BeforeSelectionReplace, AfterSelectionReplace,
    BeforeRecoveryReplace, AfterRecoveryReplace, BeforeInitializeReplace,
    BeforePublicationRename, AfterPublicationRename,
    BeforeConfigurationRestore, AfterConfigurationRestore, BeforeRestoreAcknowledgment,
    BeforeAcknowledgmentVerification, BeforeAcknowledgmentPublication, AfterAcknowledgmentPublication
}

/// <summary>Private selection and explicitly approved configuration recovery. Never executable activation.</summary>
public sealed partial class LocalSelectionEngine
{
    private const int MaximumControlBytes = 32768;
    private const int MaximumJournalBytes = 131072;
    private const int MaximumChildren = 128;
    private const int MaximumTransactions = 32;
    private const int FormatVersion = 2;
    private const string JournalFile = "journal-v2.json";
    private const string JournalV3File = "journal-v3.json";
    private const string JournalV4File = "journal-v4.json";
    private readonly string root;
    private readonly LocalStagingEngine staging;
    private readonly SettingsStore settings;
    private int busy;
    private long sequence;
    internal Action<SelectionIoPoint, string, CancellationToken>? Io { get; init; }
    private string ControlPath => Path.Combine(root, "selection.json");

    public LocalSelectionEngine(string existingPrivateControlRoot, LocalStagingEngine staging, SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(settings);
        root = SafePath(existingPrivateControlRoot);
        if (root.Length > 166 || root == Path.GetPathRoot(root) ||
            Path.GetDirectoryName(staging.Root) != root ||
            LocalPaths.Within(SafePath(settings.DataDirectory), root) ||
            LocalPaths.Within(root, SafePath(settings.DataDirectory)))
            throw new SelectionException(SelectionFailure.AccessDenied);
        this.staging = staging; this.settings = settings;
    }

    public SelectionReceipt Initialize(InstalledVersionFacts unqualifiedBootstrap, CancellationToken token = default) =>
        Run(() =>
        {
            unqualifiedBootstrap.Validate();
            if (LocalPaths.Within(root, unqualifiedBootstrap.InstallationDirectory) ||
                LocalPaths.Within(unqualifiedBootstrap.InstallationDirectory, root))
                throw new SelectionException(SelectionFailure.Conflict);
            using var owner = LockRoot();
            var children = Children();
            if (LocalPaths.Exists(ControlPath) ||
                children.Any(p => Path.GetFileName(p).StartsWith("transaction-", StringComparison.Ordinal) ||
                    Path.GetFileName(p) == "initial.json"))
                throw new SelectionException(SelectionFailure.RecoveryRequired);
            using var pin = PinSettings(token);
            var current = CurrentSettings(token);
            if (current.Revision != unqualifiedBootstrap.SettingsRevision ||
                current.Settings!.SchemaVersion != unqualifiedBootstrap.SettingsSchemaVersion)
                throw new SelectionException(SelectionFailure.Conflict);
            var state = new ControlDocument(FormatVersion, 0, root, staging.Root, settings.FilePath, current.Settings.Profile.Id,
                unqualifiedBootstrap, null, null, null, null);
            var initial = Path.Combine(root, "initial.json");
            var bytes = Wire.Write(state);
            WriteNew(initial, bytes, token);
            using var replacement = PinReplacement(initial, bytes, token);
            Point(SelectionIoPoint.BeforeInitializeReplace, initial, token);
            RequireSettings(current.Revision!, current.Settings.Profile.Id, token);
            Publish(replacement, bytes, null, null, token);
            return new SelectionReceipt(state, SelectionOutcome.Committed);
        }, token);

    public SelectionReceipt Inspect(CancellationToken token = default) => Run(() =>
    {
        using var owner = LockRoot();
        var state = ReadState(token);
        VerifySelections(state, token);
        return Receipt(state, SelectionOutcome.Unchanged, token);
    }, token);

    public SelectionPlan PrepareActivation(string stagedDestination, long currentRevision, string freshSnapshotPath,
        CancellationToken token = default) => Prepare(SelectionKind.Activation, stagedDestination, currentRevision,
            freshSnapshotPath, token);

    // No candidate argument: only the exact previously recorded signed selection is eligible.
    public SelectionPlan PrepareRollback(long currentRevision, string freshSnapshotPath,
        CancellationToken token = default) => Prepare(SelectionKind.Rollback, null, currentRevision, freshSnapshotPath, token);

    private SelectionPlan Prepare(SelectionKind kind, string? destination, long revision, string snapshotPath,
        CancellationToken token) => Run(() =>
    {
        var planSequence = Interlocked.Increment(ref sequence);
        using var owner = LockRoot();
        var before = ReadState(token);
        RequireAvailable(before, revision);
        RequireCapacity();
        VerifySelections(before, token);
        using var settingsPin = PinSettings(token);
        var current = CurrentSettings(token);
        if (current.Settings!.Profile.Id != before.ProfileId)
            throw new SelectionException(SelectionFailure.InvalidSnapshot);
        snapshotPath = SnapshotSource(snapshotPath);
        token.ThrowIfCancellationRequested();
        using var snapshotPin = BoundedIo.OpenRead(snapshotPath);
        var bytes = BoundedIo.Read(snapshotPin, ConfigurationSnapshot.MaximumBytes, token);
        var snapshot = FreshSnapshot(bytes, current);
        var id = Guid.NewGuid();
        var fresh = Bind(snapshot, TransactionName(id) + "/configuration.martlet-config");
        var previous = kind == SelectionKind.Rollback
            ? before.Previous ?? throw new SelectionException(SelectionFailure.NoVerifiedPrevious) : null;
        if (previous is not null) destination = StagePath(previous.StageName);
        return staging.WithPinnedStage(destination!, previous?.ReceiptSha256, stage =>
        {
            RequireOrigin(before.Bootstrap, stage.Receipt.Installed);
            if (kind == SelectionKind.Activation &&
                (stage.Receipt.Installed.SettingsRevision != current.Revision ||
                 stage.Receipt.Installed.SettingsSchemaVersion != current.Settings!.SchemaVersion ||
                 Wire.Version(stage.Receipt.Version) <= Wire.Version(before.Current?.Version ?? before.Bootstrap.Version)))
                throw new SelectionException(SelectionFailure.Conflict);
            var manifest = stage.Candidate.Manifest;
            Compatible(manifest.SettingsMinimumReader, manifest.SettingsMaximumReader, snapshot.SettingsSchemaVersion);
            var entry = new SelectionEntry(Path.GetFileName(stage.Receipt.Destination), stage.Receipt.Version,
                stage.Receipt.SignerId, stage.Receipt.ArchiveSha256, stage.Receipt.ManifestSha256,
                stage.Receipt.ReceiptSha256, manifest.SettingsMinimumReader, manifest.SettingsMaximumReader, fresh, false);
            if (previous is not null)
            {
                RequireEntry(previous, stage);
                using var previousPin = PinSnapshot(previous.Snapshot, token);
                if (previous.Snapshot.ProfileId != current.Settings!.Profile.Id)
                    throw new SelectionException(SelectionFailure.InvalidSnapshot);
                Compatible(previous.MinimumReader, previous.MaximumReader, previous.Snapshot.Schema);
                // New restores emit the current schema even for a historical snapshot.
                Compatible(previous.MinimumReader, previous.MaximumReader, AppSettings.CurrentSchemaVersion);
                entry = previous with { RestoreRequired = true };
            }
            RequireState(before, token);
            RequireSettings(current.Revision!, current.Settings!.Profile.Id, token);
            token.ThrowIfCancellationRequested();
            return new SelectionPlan(this, planSequence, id, kind, before, entry, bytes, fresh,
                snapshotPath, stage.Receipt.Installed, snapshot, current.Settings.Profile.Id, current.Revision!);
        }, token);
    }, token);

    public SelectionReceipt CommitSelection(SelectionPlan plan, SelectionApproval approval,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        approval.Consume(plan);
        return Run(() =>
        {
            if (!ReferenceEquals(plan.Owner, this) || plan.Sequence != Volatile.Read(ref sequence))
                throw new SelectionException(SelectionFailure.Conflict);
            using var owner = LockRoot();
            RequireState(plan.Before, token);
            RequireAvailable(plan.Before, plan.ExpectedRevision);
            RequireCapacity();
            using var settingsPin = PinSettings(token);
            RequireSettings(plan.SettingsRevision, plan.Profile, token);
            SnapshotSource(plan.SourcePath);
            token.ThrowIfCancellationRequested();
            using var sourcePin = BoundedIo.OpenRead(plan.SourcePath);
            RequireExactSnapshot(sourcePin, plan, token);
            using var rollbackSnapshot = plan.Kind == SelectionKind.Rollback
                ? PinSnapshot(plan.Entry.Snapshot, token) : null;
            var incoming = new LocalStagingEngine.StageInspection(StagePath(plan.Entry.StageName),
                plan.Kind == SelectionKind.Rollback ? plan.Entry.ReceiptSha256 : null);
            return WithRetainedEvidence(plan.Before, incoming, (stages, reverify) =>
            {
                var stage = stages[^1];
                RequireEntry(plan.Entry, stage);
                if (stage.Receipt.Installed != plan.Origin) throw new SelectionException(SelectionFailure.Conflict);
                var directory = TransactionDirectory(plan.TransactionId);
                if (LocalPaths.Exists(directory)) throw new SelectionException(SelectionFailure.Conflict);
                Point(SelectionIoPoint.BeforeCreate, directory, token);
                Directory.CreateDirectory(directory);
                Point(SelectionIoPoint.AfterCreate, directory, token);
                var before = plan.Before;
                var pending = before with { Revision = before.Revision + 1, Pending = plan.TransactionId };
                var after = before with
                {
                    Revision = before.Revision + 2, Current = plan.Entry, Previous = plan.RetainedPrevious,
                    Pending = null, LastTransaction = plan.TransactionId
                };
                ValidateState(after);
                var journal = new SelectionJournal(before.FormatVersion, plan.TransactionId, plan.PlanDigest, before, pending, after);
                WriteNew(Path.Combine(directory, "before.json"), Wire.Write(before), token);
                WriteNew(Path.Combine(directory, "configuration.martlet-config"), plan.SnapshotBytes, token,
                    ConfigurationSnapshot.MaximumBytes);
                using var retainedSnapshot = PinSnapshot(plan.FreshSnapshot, token);
                WriteNew(Path.Combine(directory, JournalName(before.FormatVersion)),
                    JournalBytes(journal), token);
                var pendingBytes = Wire.Write(pending);
                WriteNew(Path.Combine(directory, "pending.json"), pendingBytes, token);
                using (var pendingReplacement = PinReplacement(Path.Combine(directory, "pending.json"), pendingBytes, token))
                {
                    Point(SelectionIoPoint.BeforePendingReplace, directory, token);
                    Recheck(before);
                    Publish(pendingReplacement, pendingBytes, Path.Combine(directory, "pending-publication.json"), before, token);
                }
                Point(SelectionIoPoint.AfterPendingReplace, directory, token);
                var selectedBytes = Wire.Write(after);
                WriteNew(Path.Combine(directory, "selected.json"), selectedBytes, token);
                using var selectedReplacement = PinReplacement(Path.Combine(directory, "selected.json"), selectedBytes, token);
                Point(SelectionIoPoint.BeforeSelectionReplace, directory, token);
                Recheck(pending);
                Publish(selectedReplacement, selectedBytes, Path.Combine(directory, "selected-publication.json"), pending, token);
                // No production IO/cancellation checks after the linearization point.
                // The internal observer can model process loss, not authorize more effects.
                Io?.Invoke(SelectionIoPoint.AfterSelectionReplace, directory, CancellationToken.None);
                return new SelectionReceipt(after, SelectionOutcome.Committed);

                void Recheck(ControlDocument expected)
                {
                    RequireState(expected, token);
                    var retainedJournal = ReadJournal(plan.TransactionId, token);
                    if (!Wire.Write(retainedJournal).AsSpan().SequenceEqual(Wire.Write(journal)))
                        throw new SelectionException(SelectionFailure.InvalidControl);
                    RequireSettings(plan.SettingsRevision, plan.Profile, token);
                    RequireExactSnapshot(sourcePin, plan, token);
                    using var named = BoundedIo.OpenRead(plan.SourcePath);
                    RequireExactSnapshot(named, plan, token);
                    using var retained = PinSnapshot(plan.FreshSnapshot, token);
                    if (rollbackSnapshot is not null)
                    {
                        using var namedRollback = PinSnapshot(plan.Entry.Snapshot, token);
                    }
                    reverify();
                    token.ThrowIfCancellationRequested();
                }
            }, token);
        }, token, plan.TransactionId);
    }

    // Explicit recovery never completes an interrupted selection on behalf of lost consent.
    // Before pending replacement there may only be an orphan; supply its returned transaction ID.
    public SelectionReceipt Recover(Guid? interruptedTransaction = null, CancellationToken token = default) => Run(() =>
    {
        using var owner = LockRoot();
        var state = ReadState(token);
        if (state.Pending is null)
        {
            if (interruptedTransaction is { } orphan && state.LastTransaction != orphan)
                InspectOrphan(orphan);
            VerifySelections(state, token);
            return Receipt(state, SelectionOutcome.Unchanged, token);
        }
        var id = state.Pending.Value;
        if (interruptedTransaction is not null && interruptedTransaction != id)
            throw new SelectionException(SelectionFailure.Conflict);
        var directory = TransactionDirectory(id);
        var journal = ReadJournal(id, token);
        if (RestoreBinding(id, token) is not null)
            throw new SelectionException(SelectionFailure.ConfigurationRestoreReconciliationRequired, id);
        if (!Same(state, journal.Pending!))
            throw new SelectionException(SelectionFailure.InvalidControl);
        return WithRetainedEvidence(journal.Before, null, (_, reverify) =>
        {
            var recovered = journal.Before with
            {
                Revision = journal.After.Revision, LastTransaction = id, Pending = null
            };
            var bytes = Wire.Write(recovered);
            var path = RecoveryReplacement(directory, bytes, token);
            using var replacement = PinReplacement(path, bytes, token);
            Point(SelectionIoPoint.BeforeRecoveryReplace, directory, token);
            RequireState(state, token);
            reverify();
            Publish(replacement, bytes, path + ".publication", state, token);
            Io?.Invoke(SelectionIoPoint.AfterRecoveryReplace, directory, CancellationToken.None);
            return new SelectionReceipt(recovered, SelectionOutcome.RecoveredOriginal);
        }, token);
    }, token, interruptedTransaction);

    private void InspectOrphan(Guid id)
    {
        var directory = TransactionDirectory(id);
        if (!Directory.Exists(directory)) throw new SelectionException(SelectionFailure.Conflict);
        LocalPaths.NoReparse(directory);
        // An orphan is not interpreted, promoted or deleted. Incomplete/unknown children
        // stay inert; only a valid authoritative record without a pending ID permits this.
    }

    private void VerifySelections(ControlDocument state, CancellationToken token)
    {
        if (state.Pending is { } id)
        {
            var journal = ReadJournal(id, token);
            if (!Same(state, journal.Pending!)) throw new SelectionException(SelectionFailure.InvalidControl);
        }
        WithRetainedEvidence(state, null, (_, reverify) => { reverify(); return 0; }, token);
    }

    private T WithRetainedEvidence<T>(ControlDocument state, LocalStagingEngine.StageInspection? incoming,
        Func<IReadOnlyList<LocalStagingEngine.PinnedStage>, Action, T> operation, CancellationToken token)
    {
        var entries = new[] { state.Current, state.Previous }.OfType<SelectionEntry>().ToArray();
        var bindings = entries.Select(entry => entry.Snapshot).Distinct().ToArray();
        var snapshots = new List<FileStream>();
        try
        {
            foreach (var binding in bindings) snapshots.Add(PinSnapshot(binding, token));
            var requests = entries.Select(entry =>
                new LocalStagingEngine.StageInspection(StagePath(entry.StageName), entry.ReceiptSha256)).ToList();
            if (incoming is not null) requests.Add(incoming);
            return staging.WithPinnedStages(requests, stages =>
            {
                for (var index = 0; index < entries.Length; index++)
                {
                    RequireEntry(entries[index], stages[index]);
                    RequireOrigin(state.Bootstrap, stages[index].Receipt.Installed);
                }
                void Reverify()
                {
                    foreach (var binding in bindings)
                    {
                        using var named = PinSnapshot(binding, token);
                    }
                    foreach (var stage in stages) stage.Reverify();
                    token.ThrowIfCancellationRequested();
                }
                return operation(stages, Reverify);
            }, token);
        }
        finally
        {
            foreach (var snapshot in snapshots) snapshot.Dispose();
        }
    }

    private SelectionJournal ReadJournal(Guid id, CancellationToken token)
    {
        var directory = TransactionDirectory(id);
        var v2 = LocalPaths.Exists(SafePath(Path.Combine(directory, JournalFile)));
        var v3 = LocalPaths.Exists(SafePath(Path.Combine(directory, JournalV3File)));
        var v4 = LocalPaths.Exists(SafePath(Path.Combine(directory, JournalV4File)));
        if (LocalPaths.Exists(SafePath(Path.Combine(directory, "journal.json"))) ||
            (v2 ? 1 : 0) + (v3 ? 1 : 0) + (v4 ? 1 : 0) != 1)
            throw new SelectionException(SelectionFailure.InvalidControl);
        SelectionJournal journal;
        RestoreTransactionBinding? restore = null;
        RestoreAcknowledgmentBinding? acknowledgment = null;
        if (v2)
            journal = ReadDocument<SelectionJournal>(Path.Combine(directory, JournalFile), MaximumJournalBytes, token);
        else if (v3)
        {
            var document = ReadDocument<SelectionJournalV3>(Path.Combine(directory, JournalV3File), MaximumJournalBytes, token);
            if (document.FormatVersion != 3 ||
                document.Kind is not (SelectionTransactionKind.Selection or SelectionTransactionKind.ConfigurationRestore) ||
                (document.Kind == SelectionTransactionKind.ConfigurationRestore) != (document.Restore is not null))
                throw new SelectionException(SelectionFailure.InvalidControl);
            restore = document.Restore;
            journal = new(document.FormatVersion, document.TransactionId, document.PlanDigest,
                document.Before, document.Pending, document.After);
        }
        else
        {
            var document = ReadV4(id, token);
            restore = document.Restore;
            acknowledgment = document.Acknowledgment;
            journal = new(document.FormatVersion, document.TransactionId, document.PlanDigest,
                document.Before, document.Pending, document.After);
        }
        ValidateState(journal.Before); ValidateState(journal.After);
        if (journal.FormatVersion != (v2 ? 2 : v3 ? 3 : 4) || journal.TransactionId != id ||
            !Wire.IsHash(journal.PlanDigest) ||
            !Same(journal.Before, ReadDocument<ControlDocument>(Path.Combine(directory, "before.json"), MaximumControlBytes, token)))
            throw new SelectionException(SelectionFailure.InvalidControl);
        if (acknowledgment is not null)
        {
            ValidateAcknowledgmentJournal(journal, acknowledgment, token);
            return journal;
        }
        if (journal.Pending is null) throw new SelectionException(SelectionFailure.InvalidControl);
        ValidateState(journal.Pending);
        var pendingFormat = restore is null ? journal.Before.FormatVersion : Math.Max(3, journal.Before.FormatVersion);
        if (journal.Pending.FormatVersion != journal.FormatVersion ||
            journal.After.FormatVersion != journal.FormatVersion ||
            journal.Pending.FormatVersion != pendingFormat ||
            restore is null && journal.Before.FormatVersion != journal.FormatVersion ||
            !v2 && restore is null && journal.Before.Current?.RestoreRequired == true ||
            journal.Before.Pending is not null || journal.Pending.Pending != id || journal.After.Pending is not null ||
            journal.After.LastTransaction != id || journal.Before.Revision > long.MaxValue - 2 ||
            journal.Pending.Revision != journal.Before.Revision + 1 || journal.After.Revision != journal.Before.Revision + 2 ||
            !Same(journal.Pending, journal.Before with { FormatVersion = pendingFormat, Revision = journal.Before.Revision + 1, Pending = id }) ||
            journal.Before.Bootstrap != journal.After.Bootstrap ||
            journal.Before.ProfileId != journal.After.ProfileId || journal.After.Current is null)
            throw new SelectionException(SelectionFailure.InvalidControl);
        if (restore is not null) ValidateRestoreJournal(journal, restore);
        return journal;
    }

    private static bool Same(ControlDocument a, ControlDocument b) => Wire.Write(a).AsSpan().SequenceEqual(Wire.Write(b));

    private ControlDocument ReadState(CancellationToken token)
    {
        if (!LocalPaths.Exists(ControlPath)) throw new SelectionException(SelectionFailure.Uninitialized);
        var state = ReadDocument<ControlDocument>(ControlPath, MaximumControlBytes, token);
        ValidateHistory(state, token);
        return state;
    }

    private void ValidateHistory(ControlDocument state, CancellationToken token)
    {
        ValidateState(state);
        var lineage = new Dictionary<Guid, ControlDocument>();
        var ancestor = state;
        for (var depth = 0; ; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (ancestor.Pending is null && ancestor.LastTransaction is null)
            {
                if (ancestor.Revision != 0 || ancestor.Current is not null || ancestor.Previous is not null)
                    throw new SelectionException(SelectionFailure.InvalidControl);
                break;
            }
            if (depth >= MaximumTransactions) throw new SelectionException(SelectionFailure.InvalidControl);
            var id = (ancestor.Pending ?? ancestor.LastTransaction)!.Value;
            if (!lineage.TryAdd(id, ancestor)) throw new SelectionException(SelectionFailure.InvalidControl);
            var journal = ReadJournal(id, token);
            var recovered = journal.Before with { Revision = journal.After.Revision, LastTransaction = id, Pending = null };
            var acknowledgment = AcknowledgmentBinding(id, token);
            if (!(ancestor.Pending is not null ? acknowledgment is null && Same(ancestor, journal.Pending!) :
                Same(ancestor, journal.After) || acknowledgment is null && RestoreBinding(id, token) is null && Same(ancestor, recovered)))
                throw new SelectionException(SelectionFailure.InvalidControl);
            ancestor = journal.Before;
        }
        RequirePublicationHistory(lineage, token);
    }

    private void RequirePublicationHistory(Dictionary<Guid, ControlDocument> lineage, CancellationToken token)
    {
        var directories = Children().Where(path =>
            Path.GetFileName(path).StartsWith("transaction-", StringComparison.Ordinal)).ToArray();
        if (directories.Length > MaximumTransactions) throw new SelectionException(SelectionFailure.CapacityExceeded);
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name.Length != 44 || !Guid.TryParseExact(name[12..], "N", out var id) ||
                name != TransactionName(id)) continue;
            SafePath(directory);
            // Earlier local checkpoints did not retain write-ahead publication evidence.
            // Missing receipts in that format cannot be interpreted as an abandoned transaction.
            if (LocalPaths.Exists(SafePath(Path.Combine(directory, "journal.json"))))
                throw new SelectionException(SelectionFailure.InvalidControl);
            var pending = SafePath(Path.Combine(directory, "pending-publication.json"));
            var selected = SafePath(Path.Combine(directory, "selected-publication.json"));
            var committedRestore = SafePath(Path.Combine(directory, "restore-committed.json"));
            var recoveries = Enumerable.Range(0, 8)
                .Select(attempt => SafePath(Path.Combine(directory, $"recovered-{attempt}.json.publication")))
                .Where(LocalPaths.Exists).ToArray();
            var publishedPending = LocalPaths.Exists(pending);
            var publishedSelection = LocalPaths.Exists(selected);
            if (!publishedPending && !publishedSelection && recoveries.Length == 0 &&
                !LocalPaths.Exists(committedRestore))
            {
                if (lineage.ContainsKey(id)) throw new SelectionException(SelectionFailure.InvalidControl);
                // Only v2 never-published orphans can be left inert, including partial scratch.
                continue;
            }
            if (!lineage.Remove(id, out var recorded) ||
                recoveries.Length > 1 || publishedSelection && recoveries.Length != 0)
                throw new SelectionException(SelectionFailure.InvalidControl);
            var journal = ReadJournal(id, token);
            if (AcknowledgmentBinding(id, token) is not null)
            {
                if (publishedPending || !publishedSelection || recoveries.Length != 0 ||
                    LocalPaths.Exists(committedRestore) || !Same(recorded, journal.After) ||
                    !Same(ReadDocument<ControlDocument>(selected, MaximumControlBytes, token), journal.After))
                    throw new SelectionException(SelectionFailure.InvalidControl);
                continue;
            }
            if (!publishedPending) throw new SelectionException(SelectionFailure.InvalidControl);
            var restore = RestoreBinding(id, token);
            if (restore is null && LocalPaths.Exists(committedRestore) ||
                restore is not null && (recoveries.Length != 0 || publishedSelection && !LocalPaths.Exists(committedRestore)))
                throw new SelectionException(SelectionFailure.InvalidControl);
            if (restore is not null && LocalPaths.Exists(committedRestore))
                ValidateRestoreCommit(journal, restore, token);
            if (!Same(ReadDocument<ControlDocument>(pending, MaximumControlBytes, token), journal.Pending!))
                throw new SelectionException(SelectionFailure.InvalidControl);
            ControlDocument expected;
            if (publishedSelection)
            {
                if (!Same(ReadDocument<ControlDocument>(selected, MaximumControlBytes, token), journal.After))
                    throw new SelectionException(SelectionFailure.InvalidControl);
                expected = journal.After;
            }
            else if (recoveries.Length == 1)
            {
                expected = journal.Before with { Revision = journal.After.Revision, LastTransaction = id, Pending = null };
                if (!Same(ReadDocument<ControlDocument>(recoveries[0], MaximumControlBytes, token), expected))
                    throw new SelectionException(SelectionFailure.InvalidControl);
            }
            else expected = journal.Pending!;
            if (!Same(recorded, expected)) throw new SelectionException(SelectionFailure.InvalidControl);
        }
        if (lineage.Count != 0) throw new SelectionException(SelectionFailure.InvalidControl);
    }

    private void ValidateState(ControlDocument state)
    {
        if (state.FormatVersion is not (2 or 3 or 4) || state.Revision < 0 || state.Bootstrap is null ||
            state.ControlRoot != root || state.StagingRoot != staging.Root ||
            state.SettingsPath != settings.FilePath || state.ProfileId == Guid.Empty ||
            state.Pending == Guid.Empty || state.LastTransaction == Guid.Empty ||
            state.Current is null && state.Previous is not null)
            throw new SelectionException(SelectionFailure.InvalidControl);
        state.Bootstrap.Validate();
        if (LocalPaths.Within(state.Bootstrap.InstallationDirectory, root) ||
            LocalPaths.Within(root, state.Bootstrap.InstallationDirectory))
            throw new SelectionException(SelectionFailure.InvalidControl);
        foreach (var entry in new[] { state.Current, state.Previous })
        {
            if (entry is null) continue;
            StagePath(entry.StageName);
            Wire.Version(entry.Version);
            if (!Wire.IsHash(entry.SignerId) || !Wire.IsHash(entry.ArchiveSha256) ||
                !Wire.IsHash(entry.ManifestSha256) || !Wire.IsHash(entry.ReceiptSha256) ||
                entry.MinimumReader < 1 || entry.MaximumReader < entry.MinimumReader || entry.MaximumReader > 1024 ||
                entry.Snapshot is null)
                throw new SelectionException(SelectionFailure.InvalidControl);
            SnapshotPath(entry.Snapshot);
            if (entry.Snapshot.SnapshotId == Guid.Empty || entry.Snapshot.ProfileId == Guid.Empty ||
                entry.Snapshot.ProfileId != state.ProfileId ||
                entry.Snapshot.Schema is < 1 or > AppSettings.CurrentSchemaVersion ||
                !UpperHash(entry.Snapshot.SourceRevision) || !UpperHash(entry.Snapshot.FileDigest) ||
                !UpperHash(entry.Snapshot.ManifestDigest))
                throw new SelectionException(SelectionFailure.InvalidControl);
            Compatible(entry.MinimumReader, entry.MaximumReader, entry.Snapshot.Schema);
        }
    }

    private void RequireState(ControlDocument expected, CancellationToken token)
    {
        if (!Same(ReadState(token), expected)) throw new SelectionException(SelectionFailure.Conflict);
    }

    private static void RequireAvailable(ControlDocument state, long revision)
    {
        if (state.Pending is not null) throw new SelectionException(SelectionFailure.RecoveryRequired, state.Pending);
        if (state.Current?.RestoreRequired == true)
            throw new SelectionException(SelectionFailure.ConfigurationRestoreRequired);
        if (state.Revision != revision) throw new SelectionException(SelectionFailure.Conflict);
        if (state.Revision > long.MaxValue - 2) throw new SelectionException(SelectionFailure.CapacityExceeded);
    }

    private FileStream PinSettings(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SafePath(settings.FilePath);
        LocalPaths.NoReparse(settings.FilePath);
        return BoundedIo.OpenRead(settings.FilePath);
    }

    private SettingsLoadResult CurrentSettings(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LocalPaths.NoReparse(settings.FilePath);
        var loaded = settings.LoadAsync(token).GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        if (loaded.State != SettingsLoadState.Loaded || loaded.Settings is null || loaded.Revision is null)
            throw new SelectionException(SelectionFailure.InvalidSnapshot);
        return loaded;
    }

    private void RequireSettings(string revision, Guid profile, CancellationToken token)
    {
        var loaded = CurrentSettings(token);
        if (loaded.Revision != revision || loaded.Settings!.Profile.Id != profile)
            throw new SelectionException(SelectionFailure.Conflict);
    }

    private static ConfigurationSnapshotInspection FreshSnapshot(byte[] bytes, SettingsLoadResult current)
    {
        var snapshot = ConfigurationSnapshot.Inspect(bytes);
        if (snapshot.SourceRevision != current.Revision || snapshot.ProfileId != current.Settings!.Profile.Id ||
            snapshot.SettingsSchemaVersion != current.Settings.SchemaVersion)
            throw new SelectionException(SelectionFailure.InvalidSnapshot);
        return snapshot;
    }

    private static SnapshotBinding Bind(ConfigurationSnapshotInspection snapshot, string relative) =>
        new(relative, snapshot.SnapshotId, snapshot.ProfileId, snapshot.SettingsSchemaVersion,
            snapshot.SourceRevision, snapshot.FileDigest, snapshot.ManifestDigest);

    private static void RequireExactSnapshot(FileStream stream, SelectionPlan plan, CancellationToken token)
    {
        stream.Position = 0;
        var bytes = BoundedIo.Read(stream, ConfigurationSnapshot.MaximumBytes, token);
        if (!bytes.AsSpan().SequenceEqual(plan.SnapshotBytes))
            throw new SelectionException(SelectionFailure.Conflict);
        var inspection = ConfigurationSnapshot.Inspect(bytes);
        if (inspection.SourceRevision != plan.SettingsRevision || inspection.ProfileId != plan.Profile)
            throw new SelectionException(SelectionFailure.InvalidSnapshot);
    }

    private FileStream PinSnapshot(SnapshotBinding binding, CancellationToken token)
    {
        var path = SnapshotPath(binding);
        token.ThrowIfCancellationRequested();
        var stream = BoundedIo.OpenRead(path);
        var transferred = false;
        try
        {
            var inspection = ConfigurationSnapshot.Inspect(BoundedIo.Read(stream, ConfigurationSnapshot.MaximumBytes, token));
            if (Bind(inspection, binding.RelativePath) != binding)
                throw new SelectionException(SelectionFailure.InvalidSnapshot);
            transferred = true;
            return stream;
        }
        finally { if (!transferred) stream.Dispose(); }
    }

    private static void RequireOrigin(InstalledVersionFacts bootstrap, InstalledVersionFacts staged)
    {
        if (bootstrap.Version != staged.Version || bootstrap.Rid != staged.Rid ||
            bootstrap.InstallationDirectory != staged.InstallationDirectory ||
            bootstrap.InstallationRevision != staged.InstallationRevision)
            throw new SelectionException(SelectionFailure.Conflict);
    }

    private static void RequireEntry(SelectionEntry entry, LocalStagingEngine.PinnedStage stage)
    {
        if (entry.StageName != Path.GetFileName(stage.Receipt.Destination) || entry.Version != stage.Receipt.Version ||
            entry.SignerId != stage.Receipt.SignerId || entry.ArchiveSha256 != stage.Receipt.ArchiveSha256 ||
            entry.ManifestSha256 != stage.Receipt.ManifestSha256 || entry.ReceiptSha256 != stage.Receipt.ReceiptSha256 ||
            entry.MinimumReader != stage.Candidate.Manifest.SettingsMinimumReader ||
            entry.MaximumReader != stage.Candidate.Manifest.SettingsMaximumReader)
            throw new SelectionException(SelectionFailure.Conflict);
    }

    private static void Compatible(int minimum, int maximum, int schema)
    {
        if (schema < minimum || schema > maximum) throw new SelectionException(SelectionFailure.IncompatibleSettings);
    }

    private string SnapshotSource(string path)
    {
        path = SafePath(path);
        if (Path.GetDirectoryName(path) != root || !path.EndsWith(".martlet-config", StringComparison.Ordinal))
            throw new SelectionException(SelectionFailure.AccessDenied);
        LocalPaths.NoReparse(path);
        return path;
    }

    private string SnapshotPath(SnapshotBinding binding)
    {
        var parts = binding.RelativePath?.Split('/');
        if (parts is not { Length: 2 } || parts[0].Length != 44 ||
            !parts[0].StartsWith("transaction-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(parts[0][12..], "N", out var id) || id == Guid.Empty ||
            parts[1] != "configuration.martlet-config")
            throw new SelectionException(SelectionFailure.InvalidControl);
        return SafePath(Path.Combine(TransactionDirectory(id), parts[1]));
    }

    private string StagePath(string name)
    {
        if (name is null || name.Length is 0 or > 100 || name.StartsWith('.'))
            throw new SelectionException(SelectionFailure.InvalidControl);
        LocalPaths.Entry("help/" + name);
        if (name.Contains('/') || name.Contains('\\')) throw new SelectionException(SelectionFailure.InvalidControl);
        return SafePath(Path.Combine(staging.Root, name));
    }

    private static string TransactionName(Guid id) => "transaction-" + id.ToString("N");
    private string TransactionDirectory(Guid id)
    {
        if (id == Guid.Empty) throw new SelectionException(SelectionFailure.InvalidControl);
        return SafePath(Path.Combine(root, TransactionName(id)));
    }

    private static string SafePath(string path)
    {
        var full = LocalPaths.Canonical(path);
        if (path != full || full.Length > 240 ||
            full.Split(Path.DirectorySeparatorChar).Any(p => p.EndsWith('.') || p.EndsWith(' ')))
            throw new SelectionException(SelectionFailure.AccessDenied);
        LocalPaths.NoReparse(full);
        return full;
    }

    private FileStream LockRoot()
    {
        if (!Directory.Exists(root)) throw new SelectionException(SelectionFailure.AccessDenied);
        SafePath(root);
        var path = SafePath(Path.Combine(root, ".martlet-selection.lock"));
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
        { throw new SelectionException(SelectionFailure.Busy); }
    }

    private string[] Children()
    {
        var children = Directory.EnumerateFileSystemEntries(root).Take(MaximumChildren + 1).ToArray();
        if (children.Length > MaximumChildren) throw new SelectionException(SelectionFailure.CapacityExceeded);
        return children;
    }

    private void RequireCapacity()
    {
        var children = Children();
        if (children.Length >= MaximumChildren ||
            children.Count(p => Path.GetFileName(p).StartsWith("transaction-", StringComparison.Ordinal)) >= MaximumTransactions)
            throw new SelectionException(SelectionFailure.CapacityExceeded);
    }

    private static bool UpperHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static T ReadDocument<T>(string path, int maximum, CancellationToken token)
    {
        SafePath(path);
        using var stream = BoundedIo.OpenRead(path);
        try { return Wire.Read<T>(BoundedIo.Read(stream, maximum, token), maximum, canonical: true); }
        catch (StagingException ex) when (ex.Failure is StagingFailure.InvalidManifest or StagingFailure.CapacityExceeded)
        { throw new SelectionException(SelectionFailure.InvalidControl); }
    }

    private void WriteNew(string path, byte[] bytes, CancellationToken token, int maximum = MaximumJournalBytes)
    {
        SafePath(path);
        Point(SelectionIoPoint.BeforeCreate, path, token);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            Point(SelectionIoPoint.AfterCreate, path, token);
            Point(SelectionIoPoint.BeforeWrite, path, token);
            stream.Write(bytes);
            Point(SelectionIoPoint.AfterWrite, path, token);
            Point(SelectionIoPoint.BeforeFlush, path, token);
            stream.Flush(flushToDisk: true);
            Point(SelectionIoPoint.AfterFlush, path, token);
        }
        using var read = BoundedIo.OpenRead(path);
        if (!BoundedIo.Read(read, maximum, token).AsSpan().SequenceEqual(bytes))
            throw new SelectionException(SelectionFailure.InvalidControl);
    }

    private static FileStream PinReplacement(string path, byte[] expected, CancellationToken token)
    {
        SafePath(path);
        token.ThrowIfCancellationRequested();
        // Delete sharing permits same-volume overwrite rename, but writes are denied.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var transferred = false;
        try
        {
            RequireReplacementBytes(stream, expected, token);
            transferred = true;
            return stream;
        }
        finally { if (!transferred) stream.Dispose(); }
    }

    private static void RequireReplacementBytes(FileStream stream, byte[] expected, CancellationToken token)
    {
        stream.Position = 0;
        if (!BoundedIo.Read(stream, MaximumControlBytes, token).AsSpan().SequenceEqual(expected))
            throw new SelectionException(SelectionFailure.InvalidControl);
    }

    private void Publish(FileStream replacement, byte[] expected, string? publication,
        ControlDocument? current, CancellationToken token, Action? committed = null, Action? beforeRename = null)
    {
        var currentBytes = current is null ? null : Wire.Write(current);
        using var currentPin = currentBytes is null ? null : PinReplacement(ControlPath, currentBytes, token);
        RequireReplacementBytes(replacement, expected, token);
        // Finish source validation before recording intent. Once intent exists, an interruption
        // without its matching pointer is ambiguous and must not silently rewind/reuse a revision.
        using (var preview = PinReplacement(replacement.Name, expected, token)) { }
        using var evidence = publication is null ? null : WritePublication(publication, expected, token);
        Point(SelectionIoPoint.BeforePublicationRename, replacement.Name, token);
        // A share-delete handle pins bytes, not the name. Reopen and compare the exact name
        // immediately before consuming it; keep both read handles until the OS operation ends.
        RequireReplacementBytes(replacement, expected, token);
        using var named = PinReplacement(replacement.Name, expected, token);
        using (var currentNamed = currentBytes is null ? null : PinReplacement(ControlPath, currentBytes, token)) { }
        beforeRename?.Invoke();
        // Windows overwrite rename cannot retire a target with open handles, even share-delete
        // handles. Release only the checked old target; the approved replacement remains pinned.
        currentPin?.Dispose();
        SafePath(ControlPath);
        token.ThrowIfCancellationRequested();
        restoreValidation?.Invoke();
        // Windows ReplaceFile requires write access to the replacement and conflicts with the
        // read pin. Same-volume MoveFileEx replacement preserves write denial through rename.
        File.Move(replacement.Name, ControlPath, overwrite: publication is not null);
        committed?.Invoke();
        Io?.Invoke(SelectionIoPoint.AfterPublicationRename, replacement.Name, CancellationToken.None);
    }

    private FileStream WritePublication(string path, byte[] expected, CancellationToken token)
    {
        SafePath(path);
        if (LocalPaths.Exists(path)) throw new SelectionException(SelectionFailure.InvalidControl);
        WriteNew(path, expected, token);
        var evidence = BoundedIo.OpenRead(path);
        var transferred = false;
        try
        {
            RequireReplacementBytes(evidence, expected, token);
            transferred = true;
            return evidence;
        }
        finally { if (!transferred) evidence.Dispose(); }
    }

    private string RecoveryReplacement(string directory, byte[] bytes, CancellationToken token)
    {
        // Partial recovery scratch is never authority and is never overwritten or deleted.
        // The finite retry slots prevent an unbounded journal under repeated disk failures.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var path = SafePath(Path.Combine(directory, $"recovered-{attempt}.json"));
            if (!LocalPaths.Exists(path)) { WriteNew(path, bytes, token); return path; }
        }
        throw new SelectionException(SelectionFailure.CapacityExceeded);
    }

    private void Point(SelectionIoPoint point, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        restoreValidation?.Invoke();
        Io?.Invoke(point, path, token);
        token.ThrowIfCancellationRequested();
        restoreValidation?.Invoke();
    }

    private T Run<T>(Func<T> operation, CancellationToken token, Guid? transactionId = null,
        bool preserveRecovery = false)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new SelectionException(SelectionFailure.Busy);
        try { return RunOwned(operation, token, transactionId, preserveRecovery); }
        finally { Volatile.Write(ref busy, 0); }
    }

    private T RunOwned<T>(Func<T> operation, CancellationToken token, Guid? transactionId = null,
        bool preserveRecovery = false)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (restoreCleanup?.IsPending == true) throw new SelectionException(SelectionFailure.CleanupPending);
            return operation();
        }
        catch (SelectionException ex) when (transactionId is not null && ex.TransactionId is null)
        { throw new SelectionException(ex.Failure, transactionId); }
        catch (StagingException ex) when (ex.Failure is StagingFailure.Unavailable or StagingFailure.InsufficientDisk or
            StagingFailure.AccessDenied or StagingFailure.Cancelled or StagingFailure.Busy)
        {
            throw new SelectionException(ex.Failure switch
            {
                StagingFailure.AccessDenied => SelectionFailure.AccessDenied,
                StagingFailure.InsufficientDisk => SelectionFailure.InsufficientDisk,
                StagingFailure.Cancelled => SelectionFailure.Cancelled,
                StagingFailure.Busy => SelectionFailure.Busy,
                _ => SelectionFailure.Unavailable
            }, transactionId);
        }
        catch (RecoveryException) when (!preserveRecovery)
        { throw new SelectionException(SelectionFailure.InvalidSnapshot, transactionId); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { throw new SelectionException(SelectionFailure.Cancelled, transactionId); }
        catch (UnauthorizedAccessException) { throw new SelectionException(SelectionFailure.AccessDenied, transactionId); }
        catch (IOException ex)
        {
            throw new SelectionException((ex.HResult & 0xffff) is 112 or 39 or 28
                ? SelectionFailure.InsufficientDisk : SelectionFailure.Unavailable, transactionId);
        }
    }
}
