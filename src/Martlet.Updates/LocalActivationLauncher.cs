namespace Martlet.Updates;

public sealed partial class LocalActivationEngine
{
    private readonly ILauncherActivationProbe? launcherProbe;
    internal ILauncherActivationProbe? LauncherProbe => launcherProbe;

    internal LocalActivationEngine(string existingPrivateActivationRoot, LocalSelectionEngine selection,
        ILauncherActivationProbe probe) : this(existingPrivateActivationRoot, selection)
    {
        launcherProbe = probe;
        ReadinessProbeId = probe.Id;
        ReadinessLimit = probe.Timeout;
        // Keeps the inert-test hook private. Production execution uses the owned scope instead.
        ReadinessProbe = (_, _) => throw new ActivationException(ActivationFailure.ReadinessUnavailable);
        ValidateProbe();
    }

    internal T WithVerifiedActive<T>(long expectedRevision,
        Func<VerifiedActivationTarget, Action, T> operation, CancellationToken token)
        => Run(() =>
        {
            using var owner = LockRoot();
            using var history = PinHistory(token);
            var state = ReadState(token);
            if (state.Pending is { } pending)
                throw new ActivationException(ActivationFailure.RecoveryRequired, pending);
            if (state.Revision != expectedRevision)
                throw new ActivationException(ActivationFailure.Conflict);
            var current = state.Current ??
                throw new ActivationException(ActivationFailure.NoActiveVersion);
            using var control = PinReplacement(ControlPath, Wire.Write(state), token);
            return selection.WithActivationEvidence(null, Entries(state), (evidence, reverify) =>
            {
                RequireCurrentCompatibility(state, evidence);
                var stage = evidence.Stages[current.Target.StageName];
                var target = new VerifiedActivationTarget(state.Revision, current.TransitionId,
                    new LauncherCandidate(state.ProfileId, root, stage, current.Target,
                        evidence.Settings.Revision!), evidence.Settings.Settings!.SchemaVersion);
                Verify();
                return operation(target, Verify);

                void Verify()
                {
                    RequireState(state, token);
                    reverify();
                    using var named = PinReplacement(ControlPath, Wire.Write(state), token);
                    token.ThrowIfCancellationRequested();
                }
            }, token);
        }, token);

    public SelectionPlan PrepareRollbackSelection(long expectedActivationRevision,
        long expectedSelectionRevision, string freshSnapshotPath, CancellationToken token = default)
        => Run(() =>
        {
            using var owner = LockRoot();
            using var history = PinHistory(token);
            var state = ReadState(token);
            if (state.Pending is { } pending)
                throw new ActivationException(ActivationFailure.RecoveryRequired, pending);
            if (state.Revision != expectedActivationRevision ||
                state.Current is null || state.Previous is null)
                throw new ActivationException(ActivationFailure.Conflict);
            using var control = PinReplacement(ControlPath, Wire.Write(state), token);
            var plan = selection.PrepareRetainedActivationRollback(
                new ActivatedVersion(state.Current), new ActivatedVersion(state.Previous),
                expectedSelectionRevision, freshSnapshotPath, token);
            RequireState(state, token);
            return plan;
        }, token);
}
