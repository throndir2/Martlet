namespace Martlet.Memory.Tests;

public sealed class MemoryStoreTests
{
    [Fact]
    public async Task ExplicitSaveInspectEditDeleteCascadesThroughDerivedStateAndDisk()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var created = MemoryFixtures.Provenance(clock);
        using var store = scope.Open(clock);

        var saved = await store.SaveAsync(new()
        {
            Content = "The quasarneedle launch code is violet.",
            Provenance = created,
            Retention = MemoryRetention.UntilDeleted()
        });
        Assert.Equal(1, saved.StoreRevision);
        var inspected = await store.InspectAsync();
        var fact = Assert.Single(inspected.Facts);
        Assert.Equal(created, fact.CreatedFrom);
        Assert.Equal(created, fact.LastModifiedBy);

        var retrieved = await store.RetrieveAsync(new() { Text = "quasarneedle violet" });
        Assert.Equal(fact.Id, Assert.Single(retrieved.Hits).Fact.Id);
        Assert.Equal(1, store.DerivedState.CachedQueries);
        Assert.True(store.DerivedIndexContains("quasarneedle"));

        clock.Advance(TimeSpan.FromMinutes(1));
        var editor = MemoryFixtures.Provenance(clock, MemorySourceKind.UserReviewedImport);
        var edited = await store.EditAsync(new()
        {
            Id = fact.Id,
            ExpectedRevision = fact.Revision,
            Content = "The auroraneedle launch code is amber.",
            Provenance = editor,
            Retention = MemoryRetention.UntilDeleted(),
            VoiceId = null
        });
        Assert.Equal(2, edited.Fact.Revision);
        Assert.Equal(created, edited.Fact.CreatedFrom);
        Assert.Equal(editor, edited.Fact.LastModifiedBy);
        Assert.Empty((await store.RetrieveAsync(new() { Text = "quasarneedle" })).Hits);
        Assert.Single((await store.RetrieveAsync(new() { Text = "auroraneedle" })).Hits);

        await store.DeleteAsync(new()
        {
            Id = edited.Fact.Id,
            ExpectedRevision = edited.Fact.Revision,
            ConsentId = Guid.NewGuid()
        });
        Assert.Empty((await store.InspectAsync()).Facts);
        Assert.Equal(0, store.DerivedState.IndexedFacts);
        Assert.Equal(0, store.DerivedState.CachedQueries);
        Assert.False(store.DerivedIndexContains("auroraneedle"));
        var persisted = await File.ReadAllTextAsync(scope.StorePath);
        Assert.DoesNotContain("quasarneedle", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auroraneedle", persisted, StringComparison.OrdinalIgnoreCase);
        await MemoryFixtures.FailureAsync(MemoryFailure.NotFound, async () =>
            await store.DeleteAsync(new()
            {
                Id = edited.Fact.Id,
                ExpectedRevision = edited.Fact.Revision,
                ConsentId = Guid.NewGuid()
            }));
    }

    [Fact]
    public async Task ConflictingEditAndDeleteNeverClaimSuccess()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var saved = await store.SaveAsync(MemoryFixtures.Save(clock, "Original exact value."));
        await store.EditAsync(new()
        {
            Id = saved.Fact.Id,
            ExpectedRevision = saved.Fact.Revision,
            Content = "Current exact value.",
            Provenance = MemoryFixtures.Provenance(clock),
            Retention = MemoryRetention.UntilDeleted(),
            VoiceId = null
        });

        await MemoryFixtures.FailureAsync(MemoryFailure.Conflict, async () =>
            await store.EditAsync(new()
            {
                Id = saved.Fact.Id,
                ExpectedRevision = saved.Fact.Revision,
                Content = "Stale overwrite.",
                Provenance = MemoryFixtures.Provenance(clock),
                Retention = MemoryRetention.UntilDeleted(),
                VoiceId = null
            }));
        await MemoryFixtures.FailureAsync(MemoryFailure.Conflict, async () =>
            await store.DeleteAsync(new()
            {
                Id = saved.Fact.Id,
                ExpectedRevision = saved.Fact.Revision,
                ConsentId = Guid.NewGuid()
            }));
        Assert.Equal("Current exact value.", Assert.Single((await store.InspectAsync()).Facts).Content);
    }

    [Fact]
    public async Task ExpiryPurgesAuthoritativeSourceIndexAndCacheOnExplicitMaintenance()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var expiring = await store.SaveAsync(MemoryFixtures.Save(clock, "ephemeral zephyrmarker",
            MemoryRetention.ExpiringAt(clock.Utc + TimeSpan.FromHours(1))));
        await store.SaveAsync(MemoryFixtures.Save(clock, "persistent anchor"));
        Assert.Single((await store.RetrieveAsync(new() { Text = "zephyrmarker" })).Hits);

        clock.Advance(TimeSpan.FromHours(2));
        var receipt = await store.PurgeExpiredAsync();
        Assert.Equal(1, receipt.DeletedFacts);
        Assert.DoesNotContain((await store.InspectAsync()).Facts, fact => fact.Id == expiring.Fact.Id);
        Assert.Empty((await store.RetrieveAsync(new() { Text = "zephyrmarker" })).Hits);
        Assert.False(store.DerivedIndexContains("zephyrmarker"));
        Assert.DoesNotContain("zephyrmarker", await File.ReadAllTextAsync(scope.StorePath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentExplicitSavesSerializeWithoutLostUpdates()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);

        var saves = Enumerable.Range(0, 32)
            .Select(index => store.SaveAsync(MemoryFixtures.Save(clock, $"parallel authored fact {index:D2}")))
            .ToArray();
        var receipts = await Task.WhenAll(saves);
        var inspection = await store.InspectAsync();

        Assert.Equal(32, inspection.Facts.Count);
        Assert.Equal(Enumerable.Range(1, 32).Select(value => (long)value),
            receipts.Select(receipt => receipt.StoreRevision).Order());
        Assert.Equal(32, inspection.StoreRevision);
    }

    [Fact]
    public async Task FactsKeepWhoseTheyAreThroughEditsReopenAndSyncJson()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        const string sam = "5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7";
        Guid samId, plainId;
        using (var store = scope.Open(clock))
        {
            var saved = await store.SaveAsync(MemoryFixtures.Save(clock, "Sam's dog is called Biscuit.") with { VoiceId = sam });
            Assert.Equal(sam, saved.Fact.VoiceId);
            samId = saved.Fact.Id;
            plainId = (await store.SaveAsync(MemoryFixtures.Save(clock, "The wifi router is upstairs."))).Fact.Id;

            // An edit says whose the fact is afterwards: the same, another voice or no one.
            var kept = await store.EditAsync(new()
            {
                Id = samId, ExpectedRevision = 1, Content = "Sam's dog Biscuit is four.", Provenance = MemoryFixtures.Provenance(clock),
                Retention = MemoryRetention.UntilDeleted(), VoiceId = sam
            });
            Assert.Equal(sam, kept.Fact.VoiceId);
            foreach (var invalid in new[] { "", "has space", "../x", new string('a', MemoryLimits.MaximumVoiceIdCharacters + 1), "\u00e9t\u00e9" })
            {
                await MemoryFixtures.FailureAsync(MemoryFailure.InvalidData, async () =>
                    await store.SaveAsync(MemoryFixtures.Save(clock, "Rejected voice.") with { VoiceId = invalid }));
                await MemoryFixtures.FailureAsync(MemoryFailure.InvalidData, async () => await store.EditAsync(new()
                {
                    Id = samId, ExpectedRevision = 2, Content = "Rejected voice.", Provenance = MemoryFixtures.Provenance(clock),
                    Retention = MemoryRetention.UntilDeleted(), VoiceId = invalid
                }));
            }
        }

        // Facts without a voice are written exactly as before voices existed.
        var persisted = await File.ReadAllTextAsync(scope.StorePath);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(persisted, "\"voice_id\""));
        using var reopened = scope.Open(clock);
        var facts = (await reopened.InspectAsync()).Facts;
        Assert.Equal(sam, facts.Single(f => f.Id == samId).VoiceId);
        Assert.Null(facts.Single(f => f.Id == plainId).VoiceId);
        var shared = MemoryFactJson.Write(facts.Single(f => f.Id == samId));
        Assert.Contains($"\"voice_id\":\"{sam}\"", shared);
        Assert.Equal(sam, MemoryFactJson.Read(shared).VoiceId);
        Assert.DoesNotContain("voice_id", MemoryFactJson.Write(facts.Single(f => f.Id == plainId)));
        Assert.Throws<MemoryException>(() => MemoryFactJson.Read(shared.Replace(sam, "not a voice", StringComparison.Ordinal)));

        var cleared = await reopened.EditAsync(new()
        {
            Id = samId, ExpectedRevision = 2, Content = "A dog called Biscuit lives here.", Provenance = MemoryFixtures.Provenance(clock),
            Retention = MemoryRetention.UntilDeleted(), VoiceId = null
        });
        Assert.Null(cleared.Fact.VoiceId);
        Assert.DoesNotContain("voice_id", await File.ReadAllTextAsync(scope.StorePath));
        var preview = await reopened.CreateExportPreviewAsync();
        Assert.DoesNotContain("voice_id", System.Text.Encoding.UTF8.GetString(preview.Preview()));
        preview.Dispose();
    }

    [Fact]
    public void ExclusiveOwnerRejectsSecondStore()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var first = scope.Open(clock);
        var preview = MemoryStoreActivationPreview.Create(scope.StoreDirectory);
        MemoryFixtures.Failure(MemoryFailure.Busy,
            () => MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow), clock));
    }

    [Fact]
    public async Task OwnedWritesNeverCreateBackupOrTouchUnrelatedFiles()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(scope.StoreDirectory);
        var unrelated = Path.Combine(scope.StoreDirectory, "user-owned.txt");
        await File.WriteAllTextAsync(unrelated, MemoryFixtures.Canary);
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        await store.SaveAsync(MemoryFixtures.Save(clock, "owned store only"));

        Assert.Equal(MemoryFixtures.Canary, await File.ReadAllTextAsync(unrelated));
        Assert.Empty(Directory.GetFiles(scope.StoreDirectory, "*.bak"));
        Assert.Empty(Directory.GetFiles(scope.StoreDirectory, "*.tmp"));
        Assert.False(File.Exists(scope.PendingPath));
    }
}
