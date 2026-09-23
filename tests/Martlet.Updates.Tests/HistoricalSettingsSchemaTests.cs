using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using static Martlet.Updates.Tests.RollbackAcknowledgmentTests;

namespace Martlet.Updates.Tests;

public sealed class HistoricalSettingsSchemaTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    [Fact]
    public async Task LargeValidPersonaSnapshotsSurviveSelectionRestoreAndAcknowledgment()
    {
        using var f = new SelectionFixture(keys);
        var loaded = await f.Settings.LoadAsync();
        var companion = loaded.Settings!.Companion!;
        var persona = companion.ActivePersona;
        companion = companion.Update(persona.Id, persona.Name, new string('\u00e9', 8192), persona.Styles);
        companion = companion.Add("Second", companion.ActivePersona);
        Assert.True((await f.Settings.SaveAsync(loaded.Settings with { Companion = companion }, loaded.Revision)).Saved);
        f.RefreshFacts();
        Assert.InRange(new FileInfo(f.Snapshot()).Length, 131_073, ConfigurationSnapshot.MaximumBytes);
        await Interrupt(f);
        var engine = f.Engine();
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationAcknowledgment(engine.Inspect().Revision, effects).Completion;
        var result = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
        Assert.Null(result.Failure);
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Equal(result.Selection!.Revision, f.Engine().Recover().Revision);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task HistoricalRestoreHistoryRetainsItsRecordedMeaning(bool interrupted, int schema)
    {
        using var f = new SelectionFixture(keys);
        var restoreId = CreateSchemaTwoHistory(f, interrupted, schema);
        var history = RollbackRestoreTests.History(f);
        var data = Data(f);
        var engine = f.Engine();
        var receipt = engine.Inspect();
        Assert.False(receipt.IsRunnable);
        Assert.Equal(interrupted ? RollbackRestoreProgress.SettingsCommittedSelectionPending :
            RollbackRestoreProgress.Recorded, receipt.ConfigurationRestoreProgress);
        Unchanged(history);
        Unchanged(data);
        if (interrupted)
        {
            var effects = new SetupOperationRunner();
            var plan = await engine.PreviewRollbackConfigurationAcknowledgment(receipt.Revision, effects).Completion;
            Assert.Equal(schema, plan.SettingsSchemaVersion);
            Assert.Equal(schema, plan.OriginalSchemaVersion);
            var result = await engine.AcknowledgeRollbackConfiguration(plan, ApproveAcknowledgment(plan), effects).Completion;
            Assert.Null(result.Failure);
            Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
            Assert.Equal(restoreId, result.Selection!.AcknowledgedRestoreTransactionId);
            Unchanged(history.Where(pair => pair.Key != f.Control).ToDictionary());
            Unchanged(data);
            receipt = result.Selection;
        }
        Assert.Equal(receipt.Revision, f.Engine().Recover().Revision);
        var recorded = RollbackRestoreTests.History(f);
        var loaded = await f.Settings.LoadAsync();
        Assert.True((await f.Settings.SaveAsync(CompanionSettings.Begin(loaded.Settings), loaded.Revision)).Saved);
        Assert.Equal(receipt.Revision, f.Engine().Inspect().Revision);
        Assert.Equal(receipt.Revision, f.Engine().Recover().Revision);
        Unchanged(recorded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(99)]
    public void RecordedSchemaMustBeSupportedAndFitTheSelectedReader(int schema)
    {
        using var f = new SelectionFixture(keys);
        var id = CreateSchemaTwoHistory(f, interrupted: true);
        var path = Path.Combine(f.Root, $"transaction-{id:N}", "restore-committed.json");
        var marker = Wire.Read<RestoreCommitDocument>(File.ReadAllBytes(path), 32768);
        File.WriteAllBytes(path, Wire.Write(marker with { SchemaVersion = schema }));
        Assert.Throws<SelectionException>(() => f.Engine().Inspect());
    }

    [Fact]
    public async Task AcknowledgmentRequiresPresentBytesToMatchTheHistoricalSchema()
    {
        using var f = new SelectionFixture(keys);
        CreateSchemaTwoHistory(f, interrupted: true);
        var revision = f.Engine().Inspect().Revision;
        var loaded = await f.Settings.LoadAsync();
        Assert.True((await f.Settings.SaveAsync(CompanionSettings.Begin(loaded.Settings), loaded.Revision)).Saved);
        var history = RollbackRestoreTests.History(f);
        var data = Data(f);
        await Assert.ThrowsAsync<SelectionException>(() =>
            f.Engine().PreviewRollbackConfigurationAcknowledgment(revision, new()).Completion);
        Unchanged(history);
        Unchanged(data);
    }

    [Fact]
    public void NewRollbackCannotRestoreIntoAReaderThatOnlySupportsSchemaTwo()
    {
        using var f = new SelectionFixture(keys);
        CreateSchemaTwoHistory(f, interrupted: false);
        var receipt = f.Engine().Inspect();
        Assert.Equal(SelectionFailure.IncompatibleSettings, Assert.Throws<SelectionException>(() =>
            f.Engine().PrepareRollback(receipt.Revision, f.Snapshot())).Failure);
    }

    private static Guid CreateSchemaTwoHistory(SelectionFixture f, bool interrupted, int schema = 2)
    {
        // Authored legacy wire fixture: real signed/staged selections, followed by
        // the unchanged v2 selection and v3 restore formats emitted before personas.
        var loaded = f.Settings.LoadAsync().GetAwaiter().GetResult();
        File.WriteAllBytes(f.Settings.FilePath, ContractJson.Write(loaded.Settings! with
        {
            SchemaVersion = schema, Companion = schema >= 3 ? loaded.Settings.Companion : null, Memory = null
        }));
        f.RefreshFacts();
        f.Initialize();
        f.Select(f.Stage(maximumReader: schema));
        f.Select(f.Stage("0.3.0.0", maximumReader: schema));
        f.ChangeSettings();
        var selected = Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768);
        var rollbackId = Guid.NewGuid();
        var rollbackDirectory = Path.Combine(f.Root, $"transaction-{rollbackId:N}");
        Directory.CreateDirectory(rollbackDirectory);
        var snapshotBytes = File.ReadAllBytes(f.Snapshot());
        var snapshot = ConfigurationSnapshot.Inspect(snapshotBytes);
        var fresh = new SnapshotBinding($"transaction-{rollbackId:N}/configuration.martlet-config",
            snapshot.SnapshotId, snapshot.ProfileId, snapshot.SettingsSchemaVersion, snapshot.SourceRevision,
            snapshot.FileDigest, snapshot.ManifestDigest);
        File.WriteAllBytes(Path.Combine(rollbackDirectory, "configuration.martlet-config"), snapshotBytes);
        var pending = selected with { Revision = selected.Revision + 1, Pending = rollbackId };
        var rollback = selected with
        {
            Revision = selected.Revision + 2, LastTransaction = rollbackId,
            Current = selected.Previous! with { RestoreRequired = true },
            Previous = selected.Current! with { Snapshot = fresh }
        };
        Write(rollbackDirectory, "before.json", selected);
        Write(rollbackDirectory, "journal-v2.json", new SelectionJournal(2, rollbackId, new('a', 64), selected, pending, rollback));
        Write(rollbackDirectory, "pending-publication.json", pending);
        Write(rollbackDirectory, "selected-publication.json", rollback);

        var original = File.ReadAllBytes(f.Settings.FilePath);
        var settings = SettingsJson.Read(original);
        var candidate = ContractJson.Write(settings with { Profile = settings.Profile with { Kind = ProfileKind.NotConfigured } });
        var restoreId = Guid.NewGuid();
        var directory = Path.Combine(f.Root, $"transaction-{restoreId:N}");
        Directory.CreateDirectory(directory);
        var originalName = $"settings.recovery.{Guid.NewGuid():N}.bak";
        File.WriteAllBytes(Path.Combine(f.Settings.DataDirectory, originalName), original);
        File.WriteAllBytes(f.Settings.FilePath, candidate);
        var binding = new RestoreTransactionBinding(rollback.Current!.Snapshot, Convert.ToHexString(SHA256.HashData(original)),
            Convert.ToHexString(SHA256.HashData(candidate)), originalName);
        var restorePending = rollback with { FormatVersion = 3, Revision = rollback.Revision + 1, Pending = restoreId };
        var restored = rollback with
        {
            FormatVersion = 3, Revision = rollback.Revision + 2, LastTransaction = restoreId,
            Current = rollback.Current with { RestoreRequired = false }
        };
        Write(directory, "before.json", rollback);
        Write(directory, "journal-v3.json", new SelectionJournalV3(3, restoreId, SelectionTransactionKind.ConfigurationRestore,
            new('b', 64), rollback, restorePending, restored, binding));
        Write(directory, "pending-publication.json", restorePending);
        Write(directory, "restore-committed.json", new RestoreCommitDocument(1, restoreId, new('b', 64),
            settings.Profile.Id, binding.CandidateDigest, schema, originalName, binding.ExpectedRevision));
        if (!interrupted) Write(directory, "selected-publication.json", restored);
        File.WriteAllBytes(f.Control, Wire.Write(interrupted ? restorePending : restored));
        return restoreId;
    }

    private static void Write<T>(string directory, string name, T document) =>
        File.WriteAllBytes(Path.Combine(directory, name), Wire.Write(document));
}
