using System.Text;

namespace Martlet.Memory.Tests;

public sealed class ActivationAndContractTests
{
    [Fact]
    public void ActivationIsPureAndDefaultsToOff()
    {
        using var scope = new TestScope();
        var selected = Path.Combine(scope.Root, "not-created");
        var preview = MemoryStoreActivationPreview.Create(selected);

        Assert.False(preview.EnabledByDefault);
        Assert.False(Directory.Exists(selected));
        MemoryFixtures.Failure(MemoryFailure.ConsentRequired, () => preview.Authorize());
        Assert.False(Directory.Exists(selected));
        Assert.DoesNotContain(selected, preview.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitScopeValidationPerformsNoStoreWrite()
    {
        using var scope = new TestScope();
        var selected = Path.Combine(scope.Root, "validated-not-created");
        var preview = MemoryStoreActivationPreview.Create(selected);

        preview.ValidateLocalScope();

        Assert.False(Directory.Exists(selected));
        Assert.False(File.Exists(Path.Combine(selected, MemoryStore.StoreFileName)));
        Assert.False(File.Exists(Path.Combine(selected, MemoryStore.LockFileName)));
    }

    [Fact]
    public async Task ActivationIsPathBoundOneUseAndCreatesNoFactFile()
    {
        using var scope = new TestScope();
        var first = MemoryStoreActivationPreview.Create(scope.StoreDirectory);
        var second = MemoryStoreActivationPreview.Create(Path.Combine(scope.Root, "other"));
        var authorization = first.Authorize(MemoryConsentDecision.Allow);

        MemoryFixtures.Failure(MemoryFailure.ConsentMismatch,
            () => MemoryStore.Open(second, authorization));
        using (var store = MemoryStore.Open(first, authorization))
        {
            var inspection = await store.InspectAsync();
            Assert.Empty(inspection.Facts);
            Assert.False(File.Exists(scope.StorePath));
        }
        MemoryFixtures.Failure(MemoryFailure.ConsentConsumed,
            () => MemoryStore.Open(first, authorization));
        Assert.DoesNotContain(scope.StoreDirectory, authorization.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("version")]
    public async Task StrictStoreRejectsUnknownDuplicateAndFutureJson(string change)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using (var store = scope.Open(clock))
            await store.SaveAsync(MemoryFixtures.Save(clock, "A bounded authored fact."));

        var original = await File.ReadAllTextAsync(scope.StorePath);
        var changed = change switch
        {
            "unknown" => original.Replace("\"facts\":", "\"credential\": \"secret\", \"facts\":",
                StringComparison.Ordinal),
            "duplicate" => original.Replace("\"store_revision\": 1,",
                "\"store_revision\": 1, \"store_revision\": 1,", StringComparison.Ordinal),
            _ => original.Replace("\"schema_version\": 1", "\"schema_version\": 2", StringComparison.Ordinal)
        };
        await File.WriteAllTextAsync(scope.StorePath, changed, new UTF8Encoding(false));

        var expected = change == "version" ? MemoryFailure.UnsupportedVersion : MemoryFailure.CorruptStore;
        var preview = MemoryStoreActivationPreview.Create(scope.StoreDirectory);
        MemoryFixtures.Failure(expected,
            () => MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow), clock));
        Assert.Equal(changed, await File.ReadAllTextAsync(scope.StorePath));
    }

    [Fact]
    public async Task BoundsRejectInvalidContentQueryAndRetentionWithoutChangingStore()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        await MemoryFixtures.FailureAsync(MemoryFailure.LimitExceeded, async () =>
            await store.SaveAsync(MemoryFixtures.Save(clock,
                new string('x', MemoryLimits.MaximumContentUtf8Bytes + 1))));
        await MemoryFixtures.FailureAsync(MemoryFailure.InvalidData, async () =>
            await store.SaveAsync(MemoryFixtures.Save(clock, " \r\n\t ")));
        await MemoryFixtures.FailureAsync(MemoryFailure.InvalidData, async () =>
            await store.SaveAsync(MemoryFixtures.Save(clock, "future",
                MemoryRetention.ExpiringAt(clock.Utc + MemoryLimits.MaximumExpiringRetention + TimeSpan.FromSeconds(1)))));
        await MemoryFixtures.FailureAsync(MemoryFailure.LimitExceeded, async () =>
            await store.RetrieveAsync(new() { Text = new string('q', MemoryLimits.MaximumQueryCharacters + 1) }));
        Assert.Empty((await store.InspectAsync()).Facts);
        Assert.False(File.Exists(scope.StorePath));
    }

    [Fact]
    public void BoundedSourceQueryHandlesConversationLimitsUnicodeAndDistinctTerms()
    {
        var source = "server region " +
            string.Join(' ', Enumerable.Range(0, 40).Select(index => $"term{index}")) +
            " " + new string('\u00e9', 3_600);

        var query = Assert.IsType<MemoryQuery>(
            MemoryQuery.TryFromBoundedSource(source, maximumResults: 3));
        var terms = LexicalIndex.QueryTerms(query.Text);

        Assert.Equal(3, query.MaximumResults);
        Assert.InRange(query.Text.Length, 1, MemoryLimits.MaximumQueryCharacters);
        Assert.InRange(Encoding.UTF8.GetByteCount(query.Text), 1,
            MemoryLimits.MaximumQueryUtf8Bytes);
        Assert.InRange(terms.Length, 1, MemoryLimits.MaximumQueryTerms);
        Assert.Equal("server", terms[0]);
        Assert.Equal("region", terms[1]);
        Assert.Null(MemoryQuery.TryFromBoundedSource("😀 🎉"));
        Assert.Null(MemoryQuery.TryFromBoundedSource(new string('a', 100)));
        MemoryFixtures.Failure(MemoryFailure.InvalidData,
            () => MemoryQuery.TryFromBoundedSource(
                new string('x', MemoryLimits.MaximumContentCharacters + 1)));
    }

    [Fact]
    public void PublicSurfaceHasNoTranscriptProviderNetworkOrGenericIngestHook()
    {
        var methods = typeof(MemoryStore).GetMethods(System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        var names = methods.Select(method => method.Name).ToArray();
        Assert.Contains(nameof(MemoryStore.SaveAsync), names);
        Assert.Contains(nameof(MemoryStore.InspectAsync), names);
        Assert.Contains(nameof(MemoryStore.EditAsync), names);
        Assert.Contains(nameof(MemoryStore.DeleteAsync), names);
        Assert.Contains(nameof(MemoryStore.RetrieveAsync), names);
        Assert.Contains(nameof(MemoryStore.CreateExportPreviewAsync), names);
        Assert.DoesNotContain(methods, method =>
            method.Name.Contains("Transcript", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Backup", StringComparison.OrdinalIgnoreCase) ||
            method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(object) ||
                typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
    }

    [Theory]
    [InlineData("store")]
    [InlineData("fact")]
    public async Task NonIncrementablePersistedRevisionsAreRejectedWithoutOverflow(string field)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using (var store = scope.Open(clock))
            await store.SaveAsync(MemoryFixtures.Save(clock, "revision-bound fact"));
        var json = await File.ReadAllTextAsync(scope.StorePath);
        json = field == "store"
            ? json.Replace("\"store_revision\": 1", $"\"store_revision\": {long.MaxValue}",
                StringComparison.Ordinal)
            : json.Replace("\"revision\": 1", $"\"revision\": {long.MaxValue}",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(scope.StorePath, json);

        var preview = MemoryStoreActivationPreview.Create(scope.StoreDirectory);
        MemoryFixtures.Failure(MemoryFailure.CorruptStore,
            () => MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow), clock));
    }

    [Fact]
    public void OnlyKnownLocalDriveTypesAreAccepted()
    {
        Assert.True(MemoryPaths.IsAcceptedDriveType(DriveType.Fixed));
        Assert.True(MemoryPaths.IsAcceptedDriveType(DriveType.Removable));
        Assert.True(MemoryPaths.IsAcceptedDriveType(DriveType.Ram));
        Assert.False(MemoryPaths.IsAcceptedDriveType(DriveType.Network));
        Assert.False(MemoryPaths.IsAcceptedDriveType(DriveType.Unknown));
        Assert.False(MemoryPaths.IsAcceptedDriveType(DriveType.NoRootDirectory));
    }
}
