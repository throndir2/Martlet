using Martlet.Core.Settings;

namespace Martlet.Updates;

public sealed partial class LocalSelectionEngine
{
    internal string ControlRoot => root;
    internal string StagingRoot => staging.Root;
    internal string SettingsPath => settings.FilePath;

    internal T WithActivationEvidence<T>(long? expectedRevision,
        IReadOnlyList<ActivationEntry> retainedActivationEntries,
        Func<SelectionActivationEvidence, Action, T> operation,
        CancellationToken token) => Run(() =>
    {
        using var owner = LockRoot();
        using var history = PinHistory(token);
        var state = ReadState(token);
        if (state.Pending is { } pending)
        {
            if (RestoreBinding(pending, token) is not null)
                throw new SelectionException(SelectionFailure.ConfigurationRestoreReconciliationRequired, pending);
            throw new SelectionException(SelectionFailure.RecoveryRequired, pending);
        }
        if (expectedRevision is { } revision && state.Revision != revision)
            throw new SelectionException(SelectionFailure.Conflict);
        if (state.Current?.RestoreRequired == true)
            throw new SelectionException(SelectionFailure.ConfigurationRestoreRequired);

        var stateBytes = Wire.Write(state);
        using var controlPin = BoundedIo.OpenRead(ControlPath, 1);
        RequirePinnedControl();
        var selected = new[] { state.Current, state.Previous }.OfType<SelectionEntry>().ToArray();
        var extra = retainedActivationEntries.Where(entry =>
            selected.All(selection => selection.StageName != entry.Target.StageName)).ToArray();
        if (extra.Length > 1) throw new SelectionException(SelectionFailure.Conflict);
        var incoming = extra.Length == 0 ? null :
            new LocalStagingEngine.StageInspection(StagePath(extra[0].Target.StageName),
                extra[0].Target.ReceiptSha256);

        void RequirePinnedControl()
        {
            controlPin.Position = 0;
            if (!BoundedIo.Read(controlPin, MaximumControlBytes, token)
                .AsSpan().SequenceEqual(stateBytes))
                throw new SelectionException(SelectionFailure.Conflict);
        }

        return WithRetainedEvidence(state, incoming, (stages, reverify) =>
        {
            RequireOrigin(state.Bootstrap, staging.Current());
            var settingsScope = settings.OpenCurrentConfigurationReadAsync(token).GetAwaiter().GetResult();
            try
            {
                var currentSettings = CurrentSettings(token);
                if (currentSettings.Settings!.Profile.Id != state.ProfileId ||
                    currentSettings.Revision != settingsScope.Inspection.Revision)
                    throw new SelectionException(SelectionFailure.InvalidSnapshot);
                if (state.Current is not null)
                    Compatible(state.Current.MinimumReader, state.Current.MaximumReader,
                        currentSettings.Settings.SchemaVersion);
                var byName = new Dictionary<string, LocalStagingEngine.PinnedStage>(StringComparer.Ordinal);
                foreach (var stage in stages)
                {
                    if (!byName.TryAdd(Path.GetFileName(stage.Receipt.Destination), stage))
                        throw new SelectionException(SelectionFailure.InvalidControl);
                }
                foreach (var retained in retainedActivationEntries)
                {
                    if (!byName.TryGetValue(retained.Target.StageName, out var stage))
                        throw new SelectionException(SelectionFailure.Conflict);
                    RequireActivationEntry(retained, stage);
                }

                var receipt = Receipt(state, SelectionOutcome.Unchanged, token);
                var evidence = new SelectionActivationEvidence(state, currentSettings, receipt, byName);
                return operation(evidence, Reverify);

                void Reverify()
                {
                    RequirePinnedControl();
                    RequireState(state, token);
                    settingsScope.VerifyAsync().GetAwaiter().GetResult();
                    RequireSettings(currentSettings.Revision!, state.ProfileId, token);
                    RequireOrigin(state.Bootstrap, staging.Current());
                    reverify();
                    foreach (var retained in retainedActivationEntries)
                    {
                        if (!byName.TryGetValue(retained.Target.StageName, out var stage))
                            throw new SelectionException(SelectionFailure.Conflict);
                        RequireActivationEntry(retained, stage);
                    }
                    token.ThrowIfCancellationRequested();
                }
            }
            finally { settingsScope.DisposeAsync().GetAwaiter().GetResult(); }
        }, token);
    }, token);

    private static void RequireActivationEntry(ActivationEntry entry,
        LocalStagingEngine.PinnedStage stage)
    {
        var target = entry.Target;
        if (target.StageName != Path.GetFileName(stage.Receipt.Destination) ||
            target.Version != stage.Receipt.Version ||
            target.SignerId != stage.Receipt.SignerId ||
            target.ArchiveSha256 != stage.Receipt.ArchiveSha256 ||
            target.ManifestSha256 != stage.Receipt.ManifestSha256 ||
            target.ReceiptSha256 != stage.Receipt.ReceiptSha256 ||
            target.SettingsMinimumReader != stage.Candidate.Manifest.SettingsMinimumReader ||
            target.SettingsMaximumReader != stage.Candidate.Manifest.SettingsMaximumReader)
            throw new SelectionException(SelectionFailure.Conflict);
        var executable = stage.Candidate.Manifest.Files.SingleOrDefault(file =>
            file.Path == target.ExecutableRelativePath);
        if (executable is null || executable.Bytes != target.ExecutableBytes ||
            executable.Sha256 != target.ExecutableSha256)
            throw new SelectionException(SelectionFailure.Conflict);
    }
}

internal sealed class SelectionActivationEvidence
{
    internal ControlDocument Selection { get; }
    internal SettingsLoadResult Settings { get; }
    internal SelectionReceipt Receipt { get; }
    internal IReadOnlyDictionary<string, LocalStagingEngine.PinnedStage> Stages { get; }

    internal SelectionActivationEvidence(ControlDocument selection, SettingsLoadResult settings,
        SelectionReceipt receipt,
        IReadOnlyDictionary<string, LocalStagingEngine.PinnedStage> stages)
    {
        Selection = selection;
        Settings = settings;
        Receipt = receipt;
        Stages = stages;
    }
}
