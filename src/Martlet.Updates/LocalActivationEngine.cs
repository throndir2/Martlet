namespace Martlet.Updates;

internal enum ActivationIoPoint
{
    BeforeCreate, AfterCreate, BeforeWrite, AfterWrite, BeforeFlush, AfterFlush,
    BeforeInitializeReplace, BeforePendingReplace, AfterPendingReplace,
    BeforeReadiness, AfterReadiness, BeforeActiveReplace, AfterActiveReplace,
    BeforeRecoveryReplace, AfterRecoveryReplace,
    BeforePublicationRename, AfterPublicationRename
}

/// <summary>
/// Private local activation-pointer transaction. It never launches a candidate or supplies a production readiness probe.
/// </summary>
public sealed class LocalActivationEngine
{
    private const int FormatVersion = 1;
    private const int MaximumControlBytes = 65536;
    private const int MaximumJournalBytes = 262144;
    private const int MaximumReadinessBytes = 32768;
    private const int MaximumChildren = 128;
    private const int MaximumTransactions = 32;
    private const string JournalFile = "journal-v1.json";
    private const string ExecutableRelativePath = "Desktop/Martlet.Desktop.exe";
    private static readonly TimeSpan MaximumReadinessLimit = TimeSpan.FromSeconds(30);
    private static readonly string[] HistoryFiles = new[]
    {
        "before.json", JournalFile, "pending-publication.json", "active-publication.json",
        "readiness-intent.json", "readiness-result.json"
    }.Concat(Enumerable.Range(0, 8).Select(index => $"recovered-{index}.json.publication")).ToArray();

    private readonly string root;
    private readonly LocalSelectionEngine selection;
    private int busy;
    private long sequence;

    internal Action<ActivationIoPoint, string, CancellationToken>? Io { get; init; }
    internal Func<ActivationReadinessRequest, CancellationToken, ActivationProbeOutcome>? ReadinessProbe { get; init; }
    internal string ReadinessProbeId { get; init; } = "test-injected-v1";
    internal TimeSpan ReadinessLimit { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeProvider ReadinessClock { get; init; } = TimeProvider.System;

    private string ControlPath => Path.Combine(root, "active.json");

    public LocalActivationEngine(string existingPrivateActivationRoot, LocalSelectionEngine selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        root = SafePath(existingPrivateActivationRoot);
        if (!Directory.Exists(root) || root == Path.GetPathRoot(root) || root.Length > 166 ||
            Overlaps(root, selection.ControlRoot) || Overlaps(root, selection.StagingRoot) ||
            Overlaps(root, Path.GetDirectoryName(selection.SettingsPath)!))
            throw new ActivationException(ActivationFailure.AccessDenied);
        this.selection = selection;
    }

    public ActivationReceipt Initialize(long expectedSelectionRevision, CancellationToken token = default) =>
        Run(() =>
        {
            using var owner = LockRoot();
            var children = Children();
            if (children.Length >= MaximumChildren)
                throw new ActivationException(ActivationFailure.CapacityExceeded);
            if (LocalPaths.Exists(ControlPath) ||
                children.Any(path => Path.GetFileName(path) is "initial.json" or "activation.json" ||
                    Path.GetFileName(path).StartsWith("transaction-", StringComparison.Ordinal)))
                throw new ActivationException(ActivationFailure.RecoveryRequired);
            return selection.WithActivationEvidence(expectedSelectionRevision, [],
                (evidence, reverify) =>
                {
                    var selected = evidence.Selection;
                    if (Overlaps(root, selected.Bootstrap.InstallationDirectory))
                        throw new ActivationException(ActivationFailure.AccessDenied);
                    var state = new ActivationControlDocument(FormatVersion, 0, root,
                        selection.ControlRoot, selection.StagingRoot, selection.SettingsPath,
                        selected.ProfileId, selected.Bootstrap, null, null, null, null);
                    ValidateShape(state);
                    var bytes = Wire.Write(state);
                    var initial = Path.Combine(root, "initial.json");
                    WriteNew(initial, bytes, token);
                    using var replacement = PinReplacement(initial, bytes, token);
                    Point(ActivationIoPoint.BeforeInitializeReplace, initial, token);
                    reverify();
                    Publish(replacement, bytes, null, null, token, reverify);
                    return new ActivationReceipt(state, ActivationOutcome.Initialized);
                }, token);
        }, token);

    public ActivationReceipt Inspect(CancellationToken token = default) => Run(() =>
    {
        using var owner = LockRoot();
        using var history = PinHistory(token);
        var state = ReadState(token);
        return selection.WithActivationEvidence(null, Entries(state), (evidence, reverify) =>
        {
            RequireCurrentCompatibility(state, evidence);
            reverify();
            RequireState(state, token);
            return new ActivationReceipt(state, ActivationOutcome.Unchanged);
        }, token);
    }, token);

    public ActivationPlan PrepareActivation(long expectedActivationRevision,
        long expectedSelectionRevision, CancellationToken token = default) => Run(() =>
    {
        var planSequence = Interlocked.Increment(ref sequence);
        ValidateProbe();
        using var owner = LockRoot();
        using var history = PinHistory(token);
        var before = ReadState(token);
        RequireAvailable(before, expectedActivationRevision);
        RequireCapacity();
        return selection.WithActivationEvidence(expectedSelectionRevision, Entries(before),
            (evidence, reverify) =>
            {
                RequireCurrentCompatibility(before, evidence);
                var kind = Transition(before, evidence);
                var target = Target(evidence);
                var plan = new ActivationPlan(this, planSequence, kind, before, target, ReadinessProbeId);
                reverify();
                RequireState(before, token);
                return plan;
            }, token);
    }, token);

    public ActivationReceipt Activate(ActivationPlan plan, ActivationApproval approval,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        approval.Consume(plan);
        return Run(() =>
        {
            ValidateProbe();
            if (!ReferenceEquals(plan.Owner, this) || plan.Sequence != Volatile.Read(ref sequence) ||
                plan.ReadinessProbeId != ReadinessProbeId)
                throw new ActivationException(ActivationFailure.Conflict);
            using var owner = LockRoot();
            using var history = PinHistory(token);
            RequireState(plan.Before, token);
            RequireAvailable(plan.Before, plan.ExpectedActivationRevision);
            RequireCapacity();
            return selection.WithActivationEvidence(plan.ExpectedSelectionRevision, Entries(plan.Before),
                (evidence, reverify) =>
                {
                    RequireCurrentCompatibility(plan.Before, evidence);
                    return ActivateOwned(plan, evidence, reverify, token);
                }, token);
        }, token, plan.TransactionId);
    }

    private ActivationReceipt ActivateOwned(ActivationPlan plan, SelectionActivationEvidence evidence,
        Action reverify, CancellationToken token)
    {
        if (Transition(plan.Before, evidence) != plan.Kind || Target(evidence) != plan.Target)
            throw new ActivationException(ActivationFailure.Conflict);
        var directory = TransactionDirectory(plan.TransactionId);
        if (LocalPaths.Exists(directory)) throw new ActivationException(ActivationFailure.Conflict);
        RequireCapacity();
        Point(ActivationIoPoint.BeforeCreate, directory, token);
        Directory.CreateDirectory(directory);
        Point(ActivationIoPoint.AfterCreate, directory, token);

        var before = plan.Before;
        var pending = before with { Revision = before.Revision + 1, Pending = plan.TransactionId };
        var after = before with
        {
            Revision = before.Revision + 2,
            Current = plan.Entry,
            Previous = before.Current,
            Pending = null,
            LastTransaction = plan.TransactionId
        };
        ValidateShape(pending);
        ValidateShape(after);
        var targetDigest = Wire.Hash(Wire.Write(plan.Target));
        var journal = new ActivationJournal(FormatVersion, plan.TransactionId, plan.Kind,
            plan.PlanDigest, targetDigest, before, pending, after);
        ValidateJournal(journal);
        var journalBytes = Wire.Write(journal);
        if (journalBytes.Length > MaximumJournalBytes)
            throw new ActivationException(ActivationFailure.CapacityExceeded);

        WriteNew(Path.Combine(directory, "before.json"), Wire.Write(before), token);
        WriteNew(Path.Combine(directory, JournalFile), journalBytes, token);
        using var beforePin = BoundedIo.OpenRead(Path.Combine(directory, "before.json"), 1);
        using var journalPin = BoundedIo.OpenRead(Path.Combine(directory, JournalFile), 1);
        var pendingBytes = Wire.Write(pending);
        WriteNew(Path.Combine(directory, "pending.json"), pendingBytes, token);
        using (var replacement = PinReplacement(Path.Combine(directory, "pending.json"), pendingBytes, token))
        {
            Point(ActivationIoPoint.BeforePendingReplace, directory, token);
            Recheck(before);
            Publish(replacement, pendingBytes, Path.Combine(directory, "pending-publication.json"),
                before, token, () => RecheckEvidence(before));
        }
        Point(ActivationIoPoint.AfterPendingReplace, directory, token);
        using var pendingPublication = BoundedIo.OpenRead(
            Path.Combine(directory, "pending-publication.json"), 1);
        using var pendingControlPin = BoundedIo.OpenRead(ControlPath, 1);

        var startedTimestamp = ReadinessClock.GetTimestamp();
        var startedUtc = ReadinessClock.GetUtcNow();
        var expiresUtc = startedUtc + ReadinessLimit;
        var intent = new ActivationReadinessIntentDocument(1, plan.TransactionId, plan.PlanDigest,
            ReadinessProbeId, targetDigest, startedUtc, expiresUtc);
        var intentBytes = Wire.Write(intent);
        WriteNew(Path.Combine(directory, "readiness-intent.json"), intentBytes, token);
        using var intentPin = BoundedIo.OpenRead(Path.Combine(directory, "readiness-intent.json"), 1);
        Recheck(pending);

        var stage = evidence.Stages[plan.Target.StageName];
        var executablePath = Path.Combine(stage.Receipt.Destination, "payload",
            plan.Target.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var request = new ActivationReadinessRequest(plan, stage.Receipt.Destination,
            executablePath, expiresUtc);
        Point(ActivationIoPoint.BeforeReadiness, executablePath, token);
        Recheck(pending);
        RequireDeadline();
        ActivationProbeOutcome probeOutcome;
        try
        {
            probeOutcome = ReadinessProbe!(request, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new ActivationException(ActivationFailure.Cancelled, plan.TransactionId);
        }
        catch (Exception error) when (error is not ActivationException)
        {
            throw new ActivationException(ActivationFailure.Unavailable, plan.TransactionId);
        }
        Point(ActivationIoPoint.AfterReadiness, executablePath, token);

        var timedOut = ReadinessClock.GetElapsedTime(startedTimestamp) >= ReadinessLimit ||
            ReadinessClock.GetUtcNow() >= expiresUtc;
        var outcome = timedOut ? ActivationReadinessOutcome.TimedOut :
            probeOutcome == ActivationProbeOutcome.Ready
                ? ActivationReadinessOutcome.Passed : ActivationReadinessOutcome.Failed;
        var result = outcome == ActivationReadinessOutcome.Passed ? plan.PassedResult :
            new ActivationReadinessDocument(1, plan.TransactionId, plan.PlanDigest,
                ReadinessProbeId, plan.Kind, targetDigest, outcome);
        var resultBytes = Wire.Write(result);
        WriteNew(Path.Combine(directory, "readiness-result.json"), resultBytes, token);
        using var resultPin = BoundedIo.OpenRead(Path.Combine(directory, "readiness-result.json"), 1);
        if (outcome == ActivationReadinessOutcome.TimedOut)
            throw new ActivationException(ActivationFailure.ReadinessTimedOut, plan.TransactionId);
        if (outcome != ActivationReadinessOutcome.Passed)
            throw new ActivationException(ActivationFailure.ReadinessFailed, plan.TransactionId);
        if (Wire.Hash(resultBytes) != plan.Entry.ReadinessResultSha256)
            throw new ActivationException(ActivationFailure.InvalidControl, plan.TransactionId);

        Recheck(pending);
        RecheckReadiness();
        var afterBytes = Wire.Write(after);
        WriteNew(Path.Combine(directory, "active.json"), afterBytes, token);
        using var activeReplacement = PinReplacement(Path.Combine(directory, "active.json"), afterBytes, token);
        Point(ActivationIoPoint.BeforeActiveReplace, directory, token);
        Recheck(pending);
        pendingControlPin.Dispose();
        Publish(activeReplacement, afterBytes, Path.Combine(directory, "active-publication.json"),
            pending, token, () =>
            {
                reverify();
                if (Transition(plan.Before, evidence) != plan.Kind || Target(evidence) != plan.Target)
                    throw new ActivationException(ActivationFailure.Conflict, plan.TransactionId);
                RecheckReadiness();
                ValidateHistory(after, token);
            }, RequireDeadline);
        AfterPublication(ActivationIoPoint.AfterActiveReplace, directory, plan.TransactionId);
        return new ActivationReceipt(after, plan.Kind == ActivationTransitionKind.Rollback
            ? ActivationOutcome.RolledBack : ActivationOutcome.Activated);

        void Recheck(ActivationControlDocument expected)
        {
            RequireState(expected, token);
            RecheckEvidence(expected);
        }

        void RecheckEvidence(ActivationControlDocument expected)
        {
            using (var control = PinReplacement(ControlPath, Wire.Write(expected), token)) { }
            using var namedJournal = BoundedIo.OpenRead(Path.Combine(directory, JournalFile), 1);
            if (!BoundedIo.Read(namedJournal, MaximumJournalBytes, token)
                .AsSpan().SequenceEqual(journalBytes))
                throw new ActivationException(ActivationFailure.InvalidControl, plan.TransactionId);
            reverify();
            if (Transition(plan.Before, evidence) != plan.Kind || Target(evidence) != plan.Target)
                throw new ActivationException(ActivationFailure.Conflict, plan.TransactionId);
            token.ThrowIfCancellationRequested();
        }

        void RecheckReadiness()
        {
            RequireDeadline();
            RequireExactBytes(intentPin, intentBytes, MaximumReadinessBytes, token);
            RequireExactBytes(resultPin, resultBytes, MaximumReadinessBytes, token);
            using var namedIntent = BoundedIo.OpenRead(
                Path.Combine(directory, "readiness-intent.json"), 1);
            RequireExactBytes(namedIntent, intentBytes, MaximumReadinessBytes, token);
            using var namedResult = BoundedIo.OpenRead(
                Path.Combine(directory, "readiness-result.json"), 1);
            RequireExactBytes(namedResult, resultBytes, MaximumReadinessBytes, token);
            RequireDeadline();
        }

        void RequireDeadline()
        {
            if (ReadinessClock.GetElapsedTime(startedTimestamp) >= ReadinessLimit ||
                ReadinessClock.GetUtcNow() >= expiresUtc)
                throw new ActivationException(ActivationFailure.ReadinessTimedOut, plan.TransactionId);
        }
    }

    public ActivationReceipt Recover(Guid? interruptedTransaction = null,
        CancellationToken token = default) => Run(() =>
    {
        using var owner = LockRoot();
        using var history = PinHistory(token);
        var state = ReadState(token);
        if (state.Pending is null)
        {
            if (interruptedTransaction is { } orphan && state.LastTransaction != orphan)
                InspectOrphan(orphan);
            return selection.WithActivationEvidence(null, Entries(state), (evidence, reverify) =>
            {
                RequireCurrentCompatibility(state, evidence);
                reverify();
                RequireState(state, token);
                return new ActivationReceipt(state, ActivationOutcome.Unchanged);
            }, token);
        }
        var id = state.Pending.Value;
        if (interruptedTransaction is not null && interruptedTransaction != id)
            throw new ActivationException(ActivationFailure.Conflict, id);
        var journal = ReadJournal(id, token);
        if (!Same(state, journal.Pending))
            throw new ActivationException(ActivationFailure.InvalidControl, id);
        if (LocalPaths.Exists(Path.Combine(TransactionDirectory(id), "active-publication.json")))
            throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);

        return selection.WithActivationEvidence(journal.After.Current!.Target.SelectionRevision,
            Entries(journal.Before), (evidence, reverify) =>
            {
                RequireCurrentCompatibility(journal.Before, evidence);
                var recovered = journal.Before with
                {
                    Revision = journal.After.Revision,
                    Pending = null,
                    LastTransaction = id
                };
                var bytes = Wire.Write(recovered);
                var path = RecoveryReplacement(TransactionDirectory(id), bytes, token);
                using var replacement = PinReplacement(path, bytes, token);
                Point(ActivationIoPoint.BeforeRecoveryReplace, path, token);
                RequireState(state, token);
                reverify();
                Publish(replacement, bytes, path + ".publication", state, token, () =>
                {
                    reverify();
                    ValidateHistory(recovered, token);
                });
                AfterPublication(ActivationIoPoint.AfterRecoveryReplace, path, id);
                return new ActivationReceipt(recovered, ActivationOutcome.RecoveredPrevious);
            }, token);
    }, token, interruptedTransaction);

    private ActivationTransitionKind Transition(ActivationControlDocument before,
        SelectionActivationEvidence evidence)
    {
        var selected = evidence.Selection.Current ??
            throw new ActivationException(ActivationFailure.NoSelectedVersion);
        if (before.Current is null)
        {
            if (before.Previous is not null)
                throw new ActivationException(ActivationFailure.Conflict);
            return ActivationTransitionKind.Activation;
        }
        if (evidence.Selection.Previous is null ||
            !Matches(before.Current.Target, evidence.Selection.Previous))
            throw new ActivationException(ActivationFailure.Conflict);
        if (before.Previous is not null && Matches(before.Previous.Target, selected))
        {
            if (evidence.Receipt.ConfigurationRestoreProgress is not
                (RollbackRestoreProgress.Recorded or RollbackRestoreProgress.AcknowledgedVerifiedState))
                throw new ActivationException(ActivationFailure.ConfigurationRestoreRequired);
            return ActivationTransitionKind.Rollback;
        }
        if (Wire.Version(selected.Version) <= Wire.Version(before.Current.Target.Version))
            throw new ActivationException(ActivationFailure.Conflict);
        return ActivationTransitionKind.Activation;
    }

    private static bool Matches(ActivationTargetBinding active, SelectionEntry selected) =>
        active.StageName == selected.StageName &&
        active.Version == selected.Version &&
        active.SignerId == selected.SignerId &&
        active.ArchiveSha256 == selected.ArchiveSha256 &&
        active.ManifestSha256 == selected.ManifestSha256 &&
        active.ReceiptSha256 == selected.ReceiptSha256 &&
        active.SettingsMinimumReader == selected.MinimumReader &&
        active.SettingsMaximumReader == selected.MaximumReader;

    private static void RequireCurrentCompatibility(ActivationControlDocument active,
        SelectionActivationEvidence evidence)
    {
        if (active.ProfileId != evidence.Selection.ProfileId ||
            active.Bootstrap != evidence.Selection.Bootstrap)
            throw new ActivationException(ActivationFailure.Conflict);
        if (active.Current is not { } current) return;
        var schema = evidence.Settings.Settings!.SchemaVersion;
        if (schema < current.Target.SettingsMinimumReader ||
            schema > current.Target.SettingsMaximumReader)
            throw new ActivationException(ActivationFailure.IncompatibleSettings);
    }

    private static ActivationTargetBinding Target(SelectionActivationEvidence evidence)
    {
        var selected = evidence.Selection.Current ??
            throw new ActivationException(ActivationFailure.NoSelectedVersion);
        if (!evidence.Stages.TryGetValue(selected.StageName, out var stage))
            throw new ActivationException(ActivationFailure.InvalidSelectedVersion);
        var executable = stage.Candidate.Manifest.Files.SingleOrDefault(file =>
            file.Path == ExecutableRelativePath) ??
            throw new ActivationException(ActivationFailure.InvalidSelectedVersion);
        var settings = evidence.Settings;
        return new(selected.StageName, selected.Version, selected.SignerId,
            selected.ArchiveSha256, selected.ManifestSha256, selected.ReceiptSha256,
            selected.MinimumReader, selected.MaximumReader, ExecutableRelativePath,
            executable.Bytes, executable.Sha256, evidence.Selection.Revision,
            evidence.Selection.LastTransaction, settings.Revision!,
            settings.Settings!.SchemaVersion);
    }

    private void ValidateProbe()
    {
        if (ReadinessProbe is null)
            throw new ActivationException(ActivationFailure.ReadinessUnavailable);
        if (ReadinessLimit <= TimeSpan.Zero || ReadinessLimit > MaximumReadinessLimit ||
            ReadinessProbeId is not { Length: > 0 and <= 64 } ||
            ReadinessProbeId.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ActivationException(ActivationFailure.ReadinessUnavailable);
    }

    private ActivationControlDocument ReadState(CancellationToken token)
    {
        if (LocalPaths.Exists(Path.Combine(root, "activation.json")))
            throw new ActivationException(ActivationFailure.InvalidControl);
        if (!LocalPaths.Exists(ControlPath))
            throw new ActivationException(ActivationFailure.Uninitialized);
        var state = ReadDocument<ActivationControlDocument>(ControlPath, MaximumControlBytes, token);
        ValidateHistory(state, token);
        return state;
    }

    private void ValidateHistory(ActivationControlDocument state, CancellationToken token)
    {
        ValidateShape(state);
        var lineage = new Dictionary<Guid, ActivationControlDocument>();
        var ancestor = state;
        for (var depth = 0; ; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (ancestor.Pending is null && ancestor.LastTransaction is null)
            {
                if (ancestor.Revision != 0 || ancestor.Current is not null || ancestor.Previous is not null)
                    throw new ActivationException(ActivationFailure.InvalidControl);
                break;
            }
            if (depth >= MaximumTransactions)
                throw new ActivationException(ActivationFailure.InvalidControl);
            var id = (ancestor.Pending ?? ancestor.LastTransaction)!.Value;
            if (!lineage.TryAdd(id, ancestor))
                throw new ActivationException(ActivationFailure.InvalidControl);
            var journal = ReadJournal(id, token);
            var recovered = journal.Before with
            {
                Revision = journal.After.Revision,
                Pending = null,
                LastTransaction = id
            };
            if (!(ancestor.Pending is not null
                ? Same(ancestor, journal.Pending)
                : Same(ancestor, journal.After) || Same(ancestor, recovered)))
                throw new ActivationException(ActivationFailure.InvalidControl);
            ancestor = journal.Before;
        }
        RequirePublicationHistory(lineage, token);
        foreach (var entry in Entries(state)) ValidateEntryEvidence(entry, token);
    }

    private void RequirePublicationHistory(
        Dictionary<Guid, ActivationControlDocument> lineage, CancellationToken token)
    {
        var directories = TransactionDirectories();
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.EnumerateFiles(directory, "journal*.json")
                .Select(Path.GetFileName)
                .Any(name => name != JournalFile))
                throw new ActivationException(ActivationFailure.InvalidControl);
            var id = Guid.ParseExact(Path.GetFileName(directory)[12..], "N");
            var pendingPath = Path.Combine(directory, "pending-publication.json");
            var activePath = Path.Combine(directory, "active-publication.json");
            var recoveries = Enumerable.Range(0, 8)
                .Select(index => Path.Combine(directory, $"recovered-{index}.json.publication"))
                .Where(LocalPaths.Exists).ToArray();
            var publishedPending = LocalPaths.Exists(pendingPath);
            var publishedActive = LocalPaths.Exists(activePath);
            if (!publishedPending && !publishedActive && recoveries.Length == 0)
            {
                if (lineage.ContainsKey(id))
                    throw new ActivationException(ActivationFailure.InvalidControl);
                continue;
            }
            if (!lineage.Remove(id, out var recorded) || !publishedPending ||
                recoveries.Length > 1 || publishedActive && recoveries.Length != 0)
                throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);
            ActivationJournal journal;
            try
            {
                journal = ReadJournal(id, token);
                if (!Same(ReadDocument<ActivationControlDocument>(
                    pendingPath, MaximumControlBytes, token), journal.Pending))
                    throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);
            }
            catch (ActivationException error) when (error.Failure == ActivationFailure.InvalidControl)
            {
                throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);
            }

            ValidateReadinessFiles(directory, journal, publishedActive, token);
            ActivationControlDocument expected;
            if (publishedActive)
            {
                try
                {
                    if (!Same(ReadDocument<ActivationControlDocument>(
                        activePath, MaximumControlBytes, token), journal.After))
                        throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);
                }
                catch (ActivationException error) when (error.Failure == ActivationFailure.InvalidControl)
                {
                    throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);
                }
                expected = journal.After;
            }
            else if (recoveries.Length == 1)
            {
                expected = journal.Before with
                {
                    Revision = journal.After.Revision,
                    Pending = null,
                    LastTransaction = id
                };
                try
                {
                    if (!Same(ReadDocument<ActivationControlDocument>(
                        recoveries[0], MaximumControlBytes, token), expected))
                        throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);
                }
                catch (ActivationException error) when (error.Failure == ActivationFailure.InvalidControl)
                {
                    throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);
                }
            }
            else expected = journal.Pending;
            if (!Same(recorded, expected))
                throw new ActivationException(ActivationFailure.PublicationAmbiguous, id);
        }
        if (lineage.Count != 0)
            throw new ActivationException(ActivationFailure.InvalidControl);
    }

    private void ValidateReadinessFiles(string directory, ActivationJournal journal,
        bool publishedActive, CancellationToken token)
    {
        var intentPath = Path.Combine(directory, "readiness-intent.json");
        var resultPath = Path.Combine(directory, "readiness-result.json");
        var hasIntent = LocalPaths.Exists(intentPath);
        var hasResult = LocalPaths.Exists(resultPath);
        if (!publishedActive) return;
        if (!hasIntent || !hasResult)
            throw new ActivationException(ActivationFailure.PublicationAmbiguous, journal.TransactionId);
        try
        {
            var intent = ReadDocument<ActivationReadinessIntentDocument>(
                intentPath, MaximumReadinessBytes, token);
            if (intent.FormatVersion != 1 || intent.TransactionId != journal.TransactionId ||
                intent.PlanDigest != journal.PlanDigest ||
                intent.ProbeId is not { Length: > 0 and <= 64 } ||
                intent.ProbeId.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '.' or '_' or '-')) ||
                intent.TargetDigest != journal.TargetDigest || intent.StartedUtc >= intent.ExpiresUtc ||
                intent.ExpiresUtc - intent.StartedUtc > MaximumReadinessLimit)
                throw new ActivationException(ActivationFailure.InvalidControl, journal.TransactionId);
            var result = ReadDocument<ActivationReadinessDocument>(
                resultPath, MaximumReadinessBytes, token);
            if (result.FormatVersion != 1 || result.TransactionId != journal.TransactionId ||
                result.PlanDigest != journal.PlanDigest || result.Kind != journal.Kind ||
                result.ProbeId != intent.ProbeId || result.TargetDigest != journal.TargetDigest ||
                result.Outcome != ActivationReadinessOutcome.Passed ||
                Wire.Hash(Wire.Write(result)) != journal.After.Current!.ReadinessResultSha256)
                throw new ActivationException(ActivationFailure.PublicationAmbiguous, journal.TransactionId);
        }
        catch (ActivationException error) when (error.Failure == ActivationFailure.InvalidControl)
        {
            throw new ActivationException(ActivationFailure.PublicationAmbiguous, journal.TransactionId);
        }
    }

    private void ValidateEntryEvidence(ActivationEntry entry, CancellationToken token)
    {
        var path = Path.Combine(TransactionDirectory(entry.TransitionId), "readiness-result.json");
        var result = ReadDocument<ActivationReadinessDocument>(path, MaximumReadinessBytes, token);
        if (result.TransactionId != entry.TransitionId ||
            result.Outcome != ActivationReadinessOutcome.Passed ||
            result.TargetDigest != Wire.Hash(Wire.Write(entry.Target)) ||
            Wire.Hash(Wire.Write(result)) != entry.ReadinessResultSha256)
            throw new ActivationException(ActivationFailure.InvalidControl, entry.TransitionId);
    }

    private ActivationJournal ReadJournal(Guid id, CancellationToken token)
    {
        var directory = TransactionDirectory(id);
        if (LocalPaths.Exists(Path.Combine(directory, "journal.json")) ||
            Directory.Exists(directory) && Directory.EnumerateFiles(directory, "journal-*.json")
                .Any(path => Path.GetFileName(path) != JournalFile))
            throw new ActivationException(ActivationFailure.InvalidControl, id);
        var journal = ReadDocument<ActivationJournal>(
            Path.Combine(directory, JournalFile), MaximumJournalBytes, token);
        ValidateJournal(journal);
        if (journal.TransactionId != id ||
            !Same(journal.Before, ReadDocument<ActivationControlDocument>(
                Path.Combine(directory, "before.json"), MaximumControlBytes, token)))
            throw new ActivationException(ActivationFailure.InvalidControl, id);
        return journal;
    }

    private void ValidateJournal(ActivationJournal journal)
    {
        ValidateShape(journal.Before);
        ValidateShape(journal.Pending);
        ValidateShape(journal.After);
        if (journal.FormatVersion != FormatVersion || journal.TransactionId == Guid.Empty ||
            journal.Kind is not (ActivationTransitionKind.Activation or ActivationTransitionKind.Rollback) ||
            !Wire.IsHash(journal.PlanDigest) || !Wire.IsHash(journal.TargetDigest) ||
            journal.Before.Pending is not null || journal.Pending.Pending != journal.TransactionId ||
            journal.After.Pending is not null || journal.After.LastTransaction != journal.TransactionId ||
            journal.Before.Revision > long.MaxValue - 2 ||
            journal.Pending.Revision != journal.Before.Revision + 1 ||
            journal.After.Revision != journal.Before.Revision + 2 ||
            !Same(journal.Pending, journal.Before with
            {
                Revision = journal.Before.Revision + 1,
                Pending = journal.TransactionId
            }) ||
            journal.After.Current is null ||
            journal.After.Current.TransitionId != journal.TransactionId ||
            journal.TargetDigest != Wire.Hash(Wire.Write(journal.After.Current.Target)) ||
            !Same(journal.After, journal.Before with
            {
                Revision = journal.Before.Revision + 2,
                Current = journal.After.Current,
                Previous = journal.Before.Current,
                Pending = null,
                LastTransaction = journal.TransactionId
            }))
            throw new ActivationException(ActivationFailure.InvalidControl, journal.TransactionId);
    }

    private void ValidateShape(ActivationControlDocument state)
    {
        if (state.FormatVersion != FormatVersion || state.Revision < 0 ||
            state.ActivationRoot != root || state.SelectionRoot != selection.ControlRoot ||
            state.StagingRoot != selection.StagingRoot || state.SettingsPath != selection.SettingsPath ||
            state.ProfileId == Guid.Empty || state.Bootstrap is null ||
            state.Pending == Guid.Empty || state.LastTransaction == Guid.Empty ||
            state.Current is null && state.Previous is not null)
            throw new ActivationException(ActivationFailure.InvalidControl);
        try { state.Bootstrap.Validate(); }
        catch (StagingException) { throw new ActivationException(ActivationFailure.InvalidControl); }
        if (Overlaps(root, state.Bootstrap.InstallationDirectory))
            throw new ActivationException(ActivationFailure.InvalidControl);
        foreach (var entry in Entries(state)) ValidateEntry(entry);
        if (state.Current is not null && state.Previous is not null &&
            state.Current.Target.StageName == state.Previous.Target.StageName)
            throw new ActivationException(ActivationFailure.InvalidControl);
    }

    private static void ValidateEntry(ActivationEntry entry)
    {
        var target = entry.Target;
        try { Wire.Version(target.Version); }
        catch (StagingException) { throw new ActivationException(ActivationFailure.InvalidControl); }
        if (target.StageName is not { Length: > 0 and <= 100 } ||
            target.StageName.StartsWith('.') || target.StageName.Contains('/') || target.StageName.Contains('\\') ||
            !Wire.IsHash(target.SignerId) || !Wire.IsHash(target.ArchiveSha256) ||
            !Wire.IsHash(target.ManifestSha256) || !Wire.IsHash(target.ReceiptSha256) ||
            target.SettingsMinimumReader < 1 ||
            target.SettingsMaximumReader < target.SettingsMinimumReader ||
            target.SettingsMaximumReader > 1024 ||
            target.ExecutableRelativePath != ExecutableRelativePath ||
            target.ExecutableBytes < 0 || !Wire.IsHash(target.ExecutableSha256) ||
            target.SelectionRevision < 0 || target.SelectionTransactionId == Guid.Empty ||
            target.SettingsRevision is not { Length: > 0 and <= 128 } ||
            target.SettingsRevision.Any(char.IsControl) ||
            target.SettingsSchemaVersion is < 1 or > Martlet.Core.Settings.AppSettings.CurrentSchemaVersion ||
            target.SettingsSchemaVersion < target.SettingsMinimumReader ||
            target.SettingsSchemaVersion > target.SettingsMaximumReader ||
            entry.TransitionId == Guid.Empty || !Wire.IsHash(entry.ReadinessResultSha256))
            throw new ActivationException(ActivationFailure.InvalidControl);
        try { LocalPaths.Entry("help/" + target.StageName); }
        catch (StagingException) { throw new ActivationException(ActivationFailure.InvalidControl); }
    }

    private static IReadOnlyList<ActivationEntry> Entries(ActivationControlDocument state) =>
        new[] { state.Current, state.Previous }.OfType<ActivationEntry>().ToArray();

    private void RequireState(ActivationControlDocument expected, CancellationToken token)
    {
        if (!Same(ReadState(token), expected))
            throw new ActivationException(ActivationFailure.Conflict);
    }

    private static void RequireAvailable(ActivationControlDocument state, long revision)
    {
        if (state.Pending is not null)
            throw new ActivationException(ActivationFailure.RecoveryRequired, state.Pending);
        if (state.Revision != revision)
            throw new ActivationException(ActivationFailure.Conflict);
        if (state.Revision > long.MaxValue - 2)
            throw new ActivationException(ActivationFailure.CapacityExceeded);
    }

    private static bool Same(ActivationControlDocument left, ActivationControlDocument right) =>
        Wire.Write(left).AsSpan().SequenceEqual(Wire.Write(right));

    private string[] TransactionDirectories()
    {
        var directories = Children().Where(path =>
        {
            var name = Path.GetFileName(path);
            return name.Length == 44 && name.StartsWith("transaction-", StringComparison.Ordinal) &&
                Guid.TryParseExact(name[12..], "N", out var id) &&
                name == "transaction-" + id.ToString("N");
        }).Order(StringComparer.Ordinal).ToArray();
        if (directories.Length > MaximumTransactions)
            throw new ActivationException(ActivationFailure.CapacityExceeded);
        return directories;
    }

    private string[] Children()
    {
        var children = Directory.EnumerateFileSystemEntries(root).Take(MaximumChildren + 1).ToArray();
        if (children.Length > MaximumChildren)
            throw new ActivationException(ActivationFailure.CapacityExceeded);
        return children;
    }

    private void RequireCapacity()
    {
        var children = Children();
        if (children.Length >= MaximumChildren ||
            children.Count(path => Path.GetFileName(path)
                .StartsWith("transaction-", StringComparison.Ordinal)) >= MaximumTransactions)
            throw new ActivationException(ActivationFailure.CapacityExceeded);
    }

    private void InspectOrphan(Guid id)
    {
        var directory = TransactionDirectory(id);
        if (!Directory.Exists(directory))
            throw new ActivationException(ActivationFailure.Conflict);
        LocalPaths.NoReparse(directory);
    }

    private string TransactionDirectory(Guid id)
    {
        if (id == Guid.Empty) throw new ActivationException(ActivationFailure.InvalidControl);
        return SafePath(Path.Combine(root, "transaction-" + id.ToString("N")));
    }

    private string RecoveryReplacement(string directory, byte[] bytes, CancellationToken token)
    {
        for (var index = 0; index < 8; index++)
        {
            var path = SafePath(Path.Combine(directory, $"recovered-{index}.json"));
            if (!LocalPaths.Exists(path))
            {
                WriteNew(path, bytes, token);
                return path;
            }
        }
        throw new ActivationException(ActivationFailure.CapacityExceeded);
    }

    private FileStream LockRoot()
    {
        if (!Directory.Exists(root))
            throw new ActivationException(ActivationFailure.AccessDenied);
        LocalPaths.NoReparse(root);
        var path = SafePath(Path.Combine(root, ".martlet-activation.lock"));
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
        { throw new ActivationException(ActivationFailure.Busy); }
    }

    private ActivationHistoryPins PinHistory(CancellationToken token)
    {
        var pins = new ActivationHistoryPins();
        try
        {
            foreach (var directory in TransactionDirectories())
            {
                SafePath(directory);
                foreach (var name in HistoryFiles)
                {
                    token.ThrowIfCancellationRequested();
                    var path = SafePath(Path.Combine(directory, name));
                    if (LocalPaths.Exists(path)) pins.Streams.Add(BoundedIo.OpenRead(path, 1));
                }
            }
            return pins;
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    private sealed class ActivationHistoryPins : IDisposable
    {
        internal List<FileStream> Streams { get; } = [];
        public void Dispose()
        {
            foreach (var stream in Streams) stream.Dispose();
        }
    }

    private void WriteNew(string path, byte[] bytes, CancellationToken token)
    {
        SafePath(path);
        if (bytes.Length == 0 || bytes.Length > MaximumJournalBytes)
            throw new ActivationException(ActivationFailure.CapacityExceeded);
        Point(ActivationIoPoint.BeforeCreate, path, token);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough))
        {
            Point(ActivationIoPoint.AfterCreate, path, token);
            Point(ActivationIoPoint.BeforeWrite, path, token);
            stream.Write(bytes);
            Point(ActivationIoPoint.AfterWrite, path, token);
            Point(ActivationIoPoint.BeforeFlush, path, token);
            stream.Flush(flushToDisk: true);
            Point(ActivationIoPoint.AfterFlush, path, token);
        }
        using var read = BoundedIo.OpenRead(path);
        if (!BoundedIo.Read(read, MaximumJournalBytes, token).AsSpan().SequenceEqual(bytes))
            throw new ActivationException(ActivationFailure.InvalidControl);
    }

    private static FileStream PinReplacement(string path, byte[] expected, CancellationToken token)
    {
        SafePath(path);
        token.ThrowIfCancellationRequested();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, bufferSize: 1);
        var transferred = false;
        try
        {
            RequireReplacementBytes(stream, expected, token);
            transferred = true;
            return stream;
        }
        finally { if (!transferred) stream.Dispose(); }
    }

    private static void RequireReplacementBytes(FileStream stream, byte[] expected,
        CancellationToken token)
    {
        stream.Position = 0;
        if (!BoundedIo.Read(stream, MaximumControlBytes, token).AsSpan().SequenceEqual(expected))
            throw new ActivationException(ActivationFailure.InvalidControl);
    }

    private void Publish(FileStream replacement, byte[] expected, string? publication,
        ActivationControlDocument? current, CancellationToken token, Action? beforeRename = null,
        Action? finalCheck = null)
    {
        var currentBytes = current is null ? null : Wire.Write(current);
        using var currentPin = currentBytes is null ? null :
            PinReplacement(ControlPath, currentBytes, token);
        RequireReplacementBytes(replacement, expected, token);
        using (var preview = PinReplacement(replacement.Name, expected, token)) { }
        using var evidence = publication is null ? null :
            WritePublication(publication, expected, token);
        Point(ActivationIoPoint.BeforePublicationRename, replacement.Name, token);
        RequireReplacementBytes(replacement, expected, token);
        using var named = PinReplacement(replacement.Name, expected, token);
        using (var currentNamed = currentBytes is null ? null :
            PinReplacement(ControlPath, currentBytes, token)) { }
        beforeRename?.Invoke();
        RequireReplacementBytes(replacement, expected, token);
        using var finalSource = PinReplacement(replacement.Name, expected, token);
        if (currentPin is not null) RequireReplacementBytes(currentPin, currentBytes!, token);
        using (var finalCurrent = currentBytes is null ? null :
            PinReplacement(ControlPath, currentBytes, token)) { }
        currentPin?.Dispose();
        finalCheck?.Invoke();
        token.ThrowIfCancellationRequested();
        File.Move(replacement.Name, ControlPath, overwrite: publication is not null);
        AfterPublication(ActivationIoPoint.AfterPublicationRename, replacement.Name,
            current?.Pending);
    }

    private void AfterPublication(ActivationIoPoint point, string path, Guid? transactionId)
    {
        try { Io?.Invoke(point, path, CancellationToken.None); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            OperationCanceledException or ActivationException)
        {
            throw new ActivationException(ActivationFailure.PublishedOutcomeUncertain, transactionId);
        }
    }

    private FileStream WritePublication(string path, byte[] expected, CancellationToken token)
    {
        SafePath(path);
        if (LocalPaths.Exists(path))
            throw new ActivationException(ActivationFailure.InvalidControl);
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

    private static void RequireExactBytes(FileStream stream, byte[] expected,
        int maximum, CancellationToken token)
    {
        stream.Position = 0;
        if (!BoundedIo.Read(stream, maximum, token).AsSpan().SequenceEqual(expected))
            throw new ActivationException(ActivationFailure.InvalidControl);
    }

    private void Point(ActivationIoPoint point, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Io?.Invoke(point, path, token);
        token.ThrowIfCancellationRequested();
    }

    private static T ReadDocument<T>(string path, int maximum, CancellationToken token)
    {
        SafePath(path);
        try
        {
            using var stream = BoundedIo.OpenRead(path);
            return Wire.Read<T>(BoundedIo.Read(stream, maximum, token), maximum, canonical: true);
        }
        catch (StagingException error) when (error.Failure is
            StagingFailure.InvalidManifest or StagingFailure.CapacityExceeded)
        {
            throw new ActivationException(ActivationFailure.InvalidControl);
        }
    }

    private static string SafePath(string path)
    {
        try
        {
            var full = LocalPaths.Canonical(path);
            if (path != full || full.Length > 240 ||
                full.Split(Path.DirectorySeparatorChar).Any(part =>
                    part.EndsWith('.') || part.EndsWith(' ')))
                throw new ActivationException(ActivationFailure.AccessDenied);
            LocalPaths.NoReparse(full);
            return full;
        }
        catch (StagingException)
        {
            throw new ActivationException(ActivationFailure.AccessDenied);
        }
    }

    private static bool Overlaps(string left, string right) =>
        LocalPaths.Within(left, right) || LocalPaths.Within(right, left);

    private T Run<T>(Func<T> operation, CancellationToken token,
        Guid? transactionId = null)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new ActivationException(ActivationFailure.Busy);
        try
        {
            token.ThrowIfCancellationRequested();
            return operation();
        }
        catch (ActivationException error) when (transactionId is not null &&
            error.TransactionId is null)
        {
            throw new ActivationException(error.Failure, transactionId);
        }
        catch (SelectionException error)
        {
            throw new ActivationException(error.Failure switch
            {
                SelectionFailure.Busy => ActivationFailure.Busy,
                SelectionFailure.ConfigurationRestoreRequired or
                    SelectionFailure.ConfigurationRestoreReconciliationRequired =>
                    ActivationFailure.ConfigurationRestoreRequired,
                SelectionFailure.IncompatibleSettings => ActivationFailure.IncompatibleSettings,
                SelectionFailure.CapacityExceeded => ActivationFailure.CapacityExceeded,
                SelectionFailure.AccessDenied => ActivationFailure.AccessDenied,
                SelectionFailure.InsufficientDisk => ActivationFailure.InsufficientDisk,
                SelectionFailure.Cancelled => ActivationFailure.Cancelled,
                SelectionFailure.Unavailable => ActivationFailure.Unavailable,
                SelectionFailure.Conflict => ActivationFailure.Conflict,
                _ => ActivationFailure.InvalidSelectedVersion
            }, transactionId);
        }
        catch (StagingException error)
        {
            throw new ActivationException(error.Failure switch
            {
                StagingFailure.Busy => ActivationFailure.Busy,
                StagingFailure.IncompatibleSettings => ActivationFailure.IncompatibleSettings,
                StagingFailure.CapacityExceeded => ActivationFailure.CapacityExceeded,
                StagingFailure.AccessDenied => ActivationFailure.AccessDenied,
                StagingFailure.InsufficientDisk => ActivationFailure.InsufficientDisk,
                StagingFailure.Cancelled => ActivationFailure.Cancelled,
                StagingFailure.Unavailable => ActivationFailure.Unavailable,
                _ => ActivationFailure.InvalidSelectedVersion
            }, transactionId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new ActivationException(ActivationFailure.Cancelled, transactionId);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ActivationException(ActivationFailure.AccessDenied, transactionId);
        }
        catch (IOException error)
        {
            throw new ActivationException((error.HResult & 0xffff) is 112 or 39 or 28
                ? ActivationFailure.InsufficientDisk : ActivationFailure.Unavailable,
                transactionId);
        }
        finally
        {
            Volatile.Write(ref busy, 0);
        }
    }
}
