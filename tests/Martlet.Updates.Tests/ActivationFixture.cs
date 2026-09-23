namespace Martlet.Updates.Tests;

internal sealed class ActivationFixture : IDisposable
{
    internal SelectionFixture Selection { get; }
    internal string Root { get; }
    internal string Control => Path.Combine(Root, "active.json");
    internal LocalSelectionEngine SelectionEngine { get; }

    internal ActivationFixture(SigningKeys keys)
    {
        Selection = new(keys);
        Root = Path.Combine(Selection.Package.Root, "activation");
        Directory.CreateDirectory(Root);
        SelectionEngine = Selection.Engine();
    }

    internal LocalActivationEngine Engine(
        Func<ActivationReadinessRequest, CancellationToken, ActivationProbeOutcome>? probe = null,
        Action<ActivationIoPoint, string, CancellationToken>? io = null,
        TimeProvider? clock = null,
        TimeSpan? readinessLimit = null,
        LocalSelectionEngine? selection = null) =>
        new(Root, selection ?? SelectionEngine)
        {
            ReadinessProbe = probe,
            Io = io,
            ReadinessClock = clock ?? TimeProvider.System,
            ReadinessLimit = readinessLimit ?? TimeSpan.FromSeconds(10)
        };

    internal ActivationReceipt Initialize(LocalActivationEngine engine, long selectionRevision) =>
        engine.Initialize(selectionRevision);

    internal ActivationPlan Prepare(LocalActivationEngine engine, long activationRevision,
        long selectionRevision) =>
        engine.PrepareActivation(activationRevision, selectionRevision);

    internal static ActivationApproval Approve(ActivationPlan plan) =>
        plan.Approve(plan.TransactionId, plan.ExpectedActivationRevision,
            plan.ExpectedSelectionRevision, plan.ExpectedSettingsRevision, plan.PlanDigest);

    public void Dispose() => Selection.Dispose();
}
