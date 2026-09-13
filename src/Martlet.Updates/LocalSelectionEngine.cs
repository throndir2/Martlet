using Martlet.Core.Settings;

namespace Martlet.Updates;

internal enum SelectionIoPoint
{
    BeforeCreate, AfterCreate, BeforeWrite, AfterWrite, BeforeFlush, AfterFlush,
    BeforePendingReplace, AfterPendingReplace, BeforeSelectionReplace, AfterSelectionReplace,
    BeforeRecoveryReplace, AfterRecoveryReplace
}

/// <summary>Private candidate selection only. No executable launcher pointer or settings are changed.</summary>
public sealed class LocalSelectionEngine
{
    private const int MaximumControlBytes = 32768;
    private const int MaximumJournalBytes = 131072;
    private const int MaximumChildren = 128;
    private const int MaximumTransactions = 32;
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
        if (root.Length > 170 || root == Path.GetPathRoot(root) ||
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
            var state = new ControlDocument(1, 0, root, staging.Root, settings.FilePath, current.Settings.Profile.Id,
                unqualifiedBootstrap, null, null, null, null);
            var initial = Path.Combine(root, "initial.json");
            WriteNew(initial, Wire.Write(state), token);
            RequireSettings(current.Revision!, current.Settings.Profile.Id, token);
            token.ThrowIfCancellationRequested();
            File.Move(initial, ControlPath, overwrite: false);
            return new SelectionReceipt(state, SelectionOutcome.Committed);
        }, token);

    public SelectionReceipt Inspect(CancellationToken token = default) => Run(() =>
    {
        using var owner = LockRoot();
        var state = ReadState(token);
        VerifySelections(state, token);
        return new SelectionReceipt(state, SelectionOutcome.Unchanged);
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
        sequence++;
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
                // V07a restore currently emits schema 2, even for a v1 historical snapshot.
                Compatible(previous.MinimumReader, previous.MaximumReader, AppSettings.CurrentSchemaVersion);
                entry = previous with { RestoreRequired = true };
            }
            RequireState(before, token);
            RequireSettings(current.Revision!, current.Settings!.Profile.Id, token);
            token.ThrowIfCancellationRequested();
            return new SelectionPlan(this, sequence, id, kind, before, entry, bytes, fresh,
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
            if (!ReferenceEquals(plan.Owner, this) || plan.Sequence != sequence)
                throw new SelectionException(SelectionFailure.Conflict);
            using var owner = LockRoot();
            RequireState(plan.Before, token);
            RequireAvailable(plan.Before, plan.ExpectedRevision);
            RequireCapacity();
            VerifySelections(plan.Before, token);
            using var settingsPin = PinSettings(token);
            RequireSettings(plan.SettingsRevision, plan.Profile, token);
            SnapshotSource(plan.SourcePath);
            token.ThrowIfCancellationRequested();
            using var sourcePin = BoundedIo.OpenRead(plan.SourcePath);
            RequireExactSnapshot(sourcePin, plan, token);
            using var previousPin = plan.Kind == SelectionKind.Rollback ? PinSnapshot(plan.Entry.Snapshot, token) : null;
            return staging.WithPinnedStage(StagePath(plan.Entry.StageName),
                plan.Kind == SelectionKind.Rollback ? plan.Entry.ReceiptSha256 : null, stage =>
            {
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
                var journal = new SelectionJournal(1, plan.TransactionId, plan.PlanDigest, before, pending, after);
                WriteNew(Path.Combine(directory, "before.json"), Wire.Write(before), token);
                WriteNew(Path.Combine(directory, "configuration.martlet-config"), plan.SnapshotBytes, token);
                WriteNew(Path.Combine(directory, "journal.json"), Wire.Write(journal), token);
                WriteNew(Path.Combine(directory, "pending.json"), Wire.Write(pending), token);
                Point(SelectionIoPoint.BeforePendingReplace, directory, token);
                Recheck(before);
                File.Replace(Path.Combine(directory, "pending.json"), ControlPath, null);
                Point(SelectionIoPoint.AfterPendingReplace, directory, token);
                WriteNew(Path.Combine(directory, "selected.json"), Wire.Write(after), token);
                Point(SelectionIoPoint.BeforeSelectionReplace, directory, token);
                Recheck(pending);
                File.Replace(Path.Combine(directory, "selected.json"), ControlPath, null);
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
                    if (plan.Kind == SelectionKind.Rollback)
                    {
                        using var rollback = PinSnapshot(plan.Entry.Snapshot, token);
                    }
                    stage.Reverify();
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
            return new SelectionReceipt(state, SelectionOutcome.Unchanged);
        }
        var id = state.Pending.Value;
        if (interruptedTransaction is not null && interruptedTransaction != id)
            throw new SelectionException(SelectionFailure.Conflict);
        var directory = TransactionDirectory(id);
        var journal = ReadJournal(id, token);
        if (!Same(state, journal.Pending))
            throw new SelectionException(SelectionFailure.InvalidControl);
        VerifySelections(journal.Before, token);
        var recovered = journal.Before with
        {
            Revision = journal.After.Revision, LastTransaction = id, Pending = null
        };
        var replacement = RecoveryReplacement(directory, Wire.Write(recovered), token);
        Point(SelectionIoPoint.BeforeRecoveryReplace, directory, token);
        RequireState(state, token);
        token.ThrowIfCancellationRequested();
        File.Replace(replacement, ControlPath, null);
        Io?.Invoke(SelectionIoPoint.AfterRecoveryReplace, directory, CancellationToken.None);
        return new SelectionReceipt(recovered, SelectionOutcome.RecoveredOriginal);
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
            if (!Same(state, journal.Pending)) throw new SelectionException(SelectionFailure.InvalidControl);
        }
        foreach (var entry in new[] { state.Current, state.Previous })
        {
            if (entry is null) continue;
            using var snapshot = PinSnapshot(entry.Snapshot, token);
            staging.WithPinnedStage(StagePath(entry.StageName), entry.ReceiptSha256, stage =>
            {
                RequireEntry(entry, stage);
                RequireOrigin(state.Bootstrap, stage.Receipt.Installed);
                stage.Reverify();
                return 0;
            }, token);
        }
    }

    private SelectionJournal ReadJournal(Guid id, CancellationToken token)
    {
        var directory = TransactionDirectory(id);
        var journal = ReadDocument<SelectionJournal>(Path.Combine(directory, "journal.json"), MaximumJournalBytes, token);
        ValidateState(journal.Before); ValidateState(journal.Pending); ValidateState(journal.After);
        if (journal.FormatVersion != 1 || journal.TransactionId != id || !Wire.IsHash(journal.PlanDigest) ||
            journal.Before.Pending is not null || journal.Pending.Pending != id || journal.After.Pending is not null ||
            journal.After.LastTransaction != id || journal.Before.Revision > long.MaxValue - 2 ||
            journal.Pending.Revision != journal.Before.Revision + 1 || journal.After.Revision != journal.Before.Revision + 2 ||
            !Same(journal.Pending, journal.Before with { Revision = journal.Before.Revision + 1, Pending = id }) ||
            journal.Before.Bootstrap != journal.After.Bootstrap ||
            journal.Before.ProfileId != journal.After.ProfileId || journal.After.Current is null ||
            !Same(journal.Before, ReadDocument<ControlDocument>(Path.Combine(directory, "before.json"), MaximumControlBytes, token)))
            throw new SelectionException(SelectionFailure.InvalidControl);
        return journal;
    }

    private static bool Same(ControlDocument a, ControlDocument b) => Wire.Write(a).AsSpan().SequenceEqual(Wire.Write(b));

    private ControlDocument ReadState(CancellationToken token)
    {
        if (!LocalPaths.Exists(ControlPath)) throw new SelectionException(SelectionFailure.Uninitialized);
        var state = ReadDocument<ControlDocument>(ControlPath, MaximumControlBytes, token);
        ValidateState(state);
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
            var journal = ReadJournal(id, token);
            var recovered = journal.Before with { Revision = journal.After.Revision, LastTransaction = id, Pending = null };
            if (!(ancestor.Pending is not null ? Same(ancestor, journal.Pending) :
                Same(ancestor, journal.After) || Same(ancestor, recovered)))
                throw new SelectionException(SelectionFailure.InvalidControl);
            ancestor = journal.Before;
        }
        return state;
    }

    private void ValidateState(ControlDocument state)
    {
        if (state.FormatVersion != 1 || state.Revision < 0 || state.Bootstrap is null ||
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
                entry.Snapshot.Schema is not (1 or 2) ||
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
        if (Children().Count(p => Path.GetFileName(p).StartsWith("transaction-", StringComparison.Ordinal)) >= MaximumTransactions)
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

    private void WriteNew(string path, byte[] bytes, CancellationToken token)
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
        if (!BoundedIo.Read(read, MaximumJournalBytes, token).AsSpan().SequenceEqual(bytes))
            throw new SelectionException(SelectionFailure.InvalidControl);
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
        Io?.Invoke(point, path, token);
        token.ThrowIfCancellationRequested();
    }

    private T Run<T>(Func<T> operation, CancellationToken token, Guid? transactionId = null)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new SelectionException(SelectionFailure.Busy);
        try { token.ThrowIfCancellationRequested(); return operation(); }
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
        catch (RecoveryException) { throw new SelectionException(SelectionFailure.InvalidSnapshot, transactionId); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { throw new SelectionException(SelectionFailure.Cancelled, transactionId); }
        catch (UnauthorizedAccessException) { throw new SelectionException(SelectionFailure.AccessDenied, transactionId); }
        catch (IOException ex)
        {
            throw new SelectionException((ex.HResult & 0xffff) is 112 or 39 or 28
                ? SelectionFailure.InsufficientDisk : SelectionFailure.Unavailable, transactionId);
        }
        finally { Volatile.Write(ref busy, 0); }
    }
}
