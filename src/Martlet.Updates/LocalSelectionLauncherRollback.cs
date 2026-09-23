using Martlet.Core.Settings;

namespace Martlet.Updates;

public sealed partial class LocalSelectionEngine
{
    internal SelectionPlan PrepareRetainedActivationRollback(ActivatedVersion activeCurrent,
        ActivatedVersion activePrevious, long currentRevision, string freshSnapshotPath,
        CancellationToken token) => Run(() =>
    {
        var planSequence = Interlocked.Increment(ref sequence);
        using var owner = LockRoot();
        using var history = PinHistory(token);
        var before = ReadState(token);
        RequireAvailable(before, currentRevision);
        RequireCapacity();
        VerifySelections(before, token);
        RequireOrigin(before.Bootstrap, staging.Current());
        var target = HistoricalActivationSelection(activePrevious, before, token);
        var retained = HistoricalActivationSelection(activeCurrent, before, token);
        if (target.StageName == retained.StageName || target.RestoreRequired || retained.RestoreRequired)
            throw new SelectionException(SelectionFailure.Conflict);
        using var settingsPin = PinSettings(token);
        var current = CurrentSettings(token);
        if (current.Settings!.Profile.Id != before.ProfileId)
            throw new SelectionException(SelectionFailure.InvalidSnapshot);
        freshSnapshotPath = SnapshotSource(freshSnapshotPath);
        using var snapshotPin = BoundedIo.OpenRead(freshSnapshotPath);
        var snapshotBytes = BoundedIo.Read(snapshotPin, ConfigurationSnapshot.MaximumBytes, token);
        var snapshot = FreshSnapshot(snapshotBytes, current);
        var id = Guid.NewGuid();
        var fresh = Bind(snapshot, TransactionName(id) + "/configuration.martlet-config");
        return staging.WithPinnedStage(StagePath(target.StageName), target.ReceiptSha256, stage =>
        {
            RequireOrigin(before.Bootstrap, stage.Receipt.Installed);
            RequireEntry(target, stage);
            using var historicalSnapshot = PinSnapshot(target.Snapshot, token);
            if (target.Snapshot.ProfileId != current.Settings.Profile.Id)
                throw new SelectionException(SelectionFailure.InvalidSnapshot);
            Compatible(target.MinimumReader, target.MaximumReader, target.Snapshot.Schema);
            // A new, separately approved restore writes schema 4; historical receipts keep their schema.
            Compatible(target.MinimumReader, target.MaximumReader, AppSettings.CurrentSchemaVersion);
            var retainedPrevious = fresh.Schema >= retained.MinimumReader && fresh.Schema <= retained.MaximumReader
                ? retained with { Snapshot = fresh } : retained;
            RequireState(before, token);
            RequireSettings(current.Revision!, current.Settings.Profile.Id, token);
            RequireOrigin(before.Bootstrap, staging.Current());
            token.ThrowIfCancellationRequested();
            return new SelectionPlan(this, planSequence, id, SelectionKind.Rollback, before,
                target with { RestoreRequired = true }, snapshotBytes, fresh, freshSnapshotPath,
                stage.Receipt.Installed, snapshot, current.Settings.Profile.Id, current.Revision!,
                retainedPrevious);
        }, token);
    }, token);

    private SelectionEntry HistoricalActivationSelection(ActivatedVersion activated,
        ControlDocument current, CancellationToken token)
    {
        if (activated.SelectionTransactionId is not { } id)
            throw new SelectionException(SelectionFailure.NoVerifiedPrevious);
        var journal = ReadJournal(id, token);
        var recovered = journal.Before with
        {
            Revision = journal.After.Revision, Pending = null, LastTransaction = id
        };
        _ = new[] { journal.After, recovered }.FirstOrDefault(state =>
            state.Revision == activated.SelectionRevision && state.Pending is null &&
            state.LastTransaction == id && state.Current is { } entry &&
            Matches(entry))?.Current ??
            throw new SelectionException(SelectionFailure.Conflict);
        // Follow only the already-validated authoritative lineage, newest first.
        // Leaving an active version may have retained a fresher compatible snapshot.
        var ancestor = current;
        for (var depth = 0; depth <= MaximumTransactions; depth++)
        {
            foreach (var entry in new[] { ancestor.Current, ancestor.Previous }.OfType<SelectionEntry>())
                if (Matches(entry) && !entry.RestoreRequired)
                    return entry;
            if (ancestor.LastTransaction is not { } previousId)
                break;
            ancestor = ReadJournal(previousId, token).Before;
        }
        throw new SelectionException(SelectionFailure.Conflict);

        bool Matches(SelectionEntry entry) =>
            activated.StageName == entry.StageName && activated.Version == entry.Version &&
            activated.SignerId == entry.SignerId && activated.ArchiveSha256 == entry.ArchiveSha256 &&
            activated.ManifestSha256 == entry.ManifestSha256 && activated.ReceiptSha256 == entry.ReceiptSha256 &&
            activated.SettingsMinimumReader == entry.MinimumReader &&
            activated.SettingsMaximumReader == entry.MaximumReader;
    }
}
