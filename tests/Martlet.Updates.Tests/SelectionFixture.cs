using Martlet.Core.Settings;

namespace Martlet.Updates.Tests;

internal sealed class SelectionFixture : IDisposable
{
    internal SignedPackageFixture Package { get; }
    internal string Root => Path.Combine(Package.Root, "control");
    internal string Control => Path.Combine(Root, "selection.json");
    internal SettingsStore Settings { get; }
    internal LocalStagingEngine Staging => Package.Engine();
    internal SelectionFixture(SigningKeys keys, bool legacy = false)
    {
        Package = new(keys, selectionLayout: true);
        Settings = new(Path.Combine(Package.Root, "data"));
        var settings = legacy ? AppSettings.CreateUnconfigured() : SetupSettings.Begin(null);
        if (!legacy) settings = settings with
        {
            Setup = settings.Setup! with
            {
                PendingRemovals = [new PendingCredentialRemoval { Role = SetupRole.Tts, CredentialId = Guid.NewGuid() }]
            }
        };
        Assert.True(Settings.SaveAsync(settings, null).GetAwaiter().GetResult().Saved);
        RefreshFacts();
    }
    internal void RefreshFacts()
    {
        var loaded = Settings.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal(SettingsLoadState.Loaded, loaded.State);
        Package.Current = Package.Current with
        {
            SettingsRevision = loaded.Revision!, SettingsSchemaVersion = loaded.Settings!.SchemaVersion
        };
    }
    internal LocalSelectionEngine Engine(Action<SelectionIoPoint, string, CancellationToken>? io = null,
        LocalStagingEngine? staging = null) => new(Root, staging ?? Staging, Settings) { Io = io };
    internal string Snapshot()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".martlet-config");
        Settings.CreateConfigurationSnapshotAsync(path).GetAwaiter().GetResult();
        return path;
    }
    internal string Stage(string version = "0.2.0.0", int maximumReader = 2)
    {
        Package.Build(version);
        Package.Sign(Package.Manifest with { SettingsMaximumReader = maximumReader });
        var engine = Staging;
        var path = Path.Combine(Package.StagingRoot, "version-" + version);
        var plan = engine.Preview(Package.Archive, Package.Envelope, path);
        engine.Stage(plan, SignedPackageFixture.Approve(plan));
        return path;
    }
    internal SelectionReceipt Initialize() => Engine().Initialize(Package.Current);
    internal SelectionPlan Prepare(LocalSelectionEngine engine, string stage) =>
        engine.PrepareActivation(stage, engine.Inspect().Revision, Snapshot());
    internal static SelectionApproval Approve(SelectionPlan plan) =>
        plan.Approve(plan.TransactionId, plan.ExpectedRevision, plan.PlanDigest);
    internal SelectionReceipt Select(string stage)
    {
        var engine = Engine();
        var plan = Prepare(engine, stage);
        return engine.CommitSelection(plan, Approve(plan));
    }
    internal void ChangeSettings()
    {
        var current = Settings.LoadAsync().GetAwaiter().GetResult();
        var changed = current.Settings! with
        {
            Profile = current.Settings.Profile with { Kind = ProfileKind.Api }
        };
        Assert.True(Settings.SaveAsync(changed, current.Revision).GetAwaiter().GetResult().Saved);
        RefreshFacts();
    }
    public void Dispose() => Package.Dispose();
}
