using System.Text;
using System.Text.Json;

namespace Martlet.Memory.Tests;

public sealed class ExportTests
{
    [Fact]
    public async Task FrozenReadableExportRequiresDefaultNoDestinationBoundAuthorization()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        await store.SaveAsync(MemoryFixtures.Save(clock, "Explicit export content."));
        using var preview = await store.CreateExportPreviewAsync();
        var bytes = preview.Preview();

        Assert.False(preview.ExportApprovedByDefault);
        MemoryFixtures.Failure(MemoryFailure.ConsentRequired,
            () => preview.Authorize(scope.ExportPath));
        Assert.False(File.Exists(scope.ExportPath));
        using (var json = JsonDocument.Parse(bytes))
        {
            Assert.Equal(1, json.RootElement.GetProperty("schema_version").GetInt32());
            Assert.Equal(preview.StoreRevision, json.RootElement.GetProperty("store_revision").GetInt64());
            Assert.Equal(1, json.RootElement.GetProperty("facts").GetArrayLength());
            Assert.Contains("Explicit export content.",
                json.RootElement.GetProperty("facts")[0].GetProperty("content").GetString(),
                StringComparison.Ordinal);
        }
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(scope.Root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", text, StringComparison.OrdinalIgnoreCase);

        var authorization = preview.Authorize(scope.ExportPath, MemoryExportDecision.Export);
        Assert.DoesNotContain(scope.ExportPath, authorization.ToString(), StringComparison.OrdinalIgnoreCase);
        var receipt = await store.ExportAsync(preview, authorization, scope.ExportPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(scope.ExportPath));
        Assert.Equal(bytes.Length, receipt.Bytes);
        await MemoryFixtures.FailureAsync(MemoryFailure.ConsentConsumed, async () =>
            await store.ExportAsync(preview, authorization, scope.ExportPath));
    }

    [Fact]
    public async Task StoreRevisionChangeInvalidatesFrozenExport()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var saved = await store.SaveAsync(MemoryFixtures.Save(clock, "soon deleted export fact"));
        using var preview = await store.CreateExportPreviewAsync();
        var authorization = preview.Authorize(scope.ExportPath, MemoryExportDecision.Export);
        await store.DeleteAsync(new()
        {
            Id = saved.Fact.Id,
            ExpectedRevision = saved.Fact.Revision,
            ConsentId = Guid.NewGuid()
        });

        await MemoryFixtures.FailureAsync(MemoryFailure.QueryInvalidated, async () =>
            await store.ExportAsync(preview, authorization, scope.ExportPath));
        Assert.False(File.Exists(scope.ExportPath));
        Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
    }

    [Fact]
    public async Task DeleteDuringExportWriteInvalidatesBeforeFinalization()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hooks = new MemoryTestHooks
        {
            Io = (point, _) =>
            {
                if (point != MemoryIoPoint.AfterExportStageWrite)
                    return;
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        using var store = scope.Open(clock, hooks);
        var saved = await store.SaveAsync(MemoryFixtures.Save(clock, "delete races export"));
        using var preview = await store.CreateExportPreviewAsync();
        var authorization = preview.Authorize(scope.ExportPath, MemoryExportDecision.Export);

        var exporting = Task.Run(() => store.ExportAsync(preview, authorization, scope.ExportPath));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await store.DeleteAsync(new()
        {
            Id = saved.Fact.Id,
            ExpectedRevision = saved.Fact.Revision,
            ConsentId = Guid.NewGuid()
        });
        release.Set();

        await MemoryFixtures.FailureAsync(MemoryFailure.QueryInvalidated, async () => await exporting);
        Assert.False(File.Exists(scope.ExportPath));
        Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
    }

    [Fact]
    public async Task ExistingDestinationAndCancellationArePreservedWithoutPartialSuccess()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var canceled = new CancellationTokenSource();
        var cancelStage = false;
        var hooks = new MemoryTestHooks
        {
            Io = (point, _) =>
            {
                if (cancelStage && point == MemoryIoPoint.AfterExportStageWrite)
                    canceled.Cancel();
            }
        };
        using var store = scope.Open(clock, hooks);
        await store.SaveAsync(MemoryFixtures.Save(clock, "bounded export"));

        using (var existingPreview = await store.CreateExportPreviewAsync())
        {
            var authorization = existingPreview.Authorize(scope.ExportPath, MemoryExportDecision.Export);
            await File.WriteAllTextAsync(scope.ExportPath, MemoryFixtures.Canary);
            await MemoryFixtures.FailureAsync(MemoryFailure.DestinationExists, async () =>
                await store.ExportAsync(existingPreview, authorization, scope.ExportPath));
            Assert.Equal(MemoryFixtures.Canary, await File.ReadAllTextAsync(scope.ExportPath));
        }

        File.Delete(scope.ExportPath);
        using var canceledPreview = await store.CreateExportPreviewAsync();
        var canceledAuthorization = canceledPreview.Authorize(scope.ExportPath, MemoryExportDecision.Export);
        cancelStage = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.ExportAsync(canceledPreview, canceledAuthorization, scope.ExportPath, canceled.Token));
        Assert.False(File.Exists(scope.ExportPath));
        Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
    }

    [Fact]
    public async Task ExpiryAfterPreviewInvalidatesExportAndPurgesSource()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var hooks = new MemoryTestHooks
        {
            Io = (point, _) =>
            {
                if (point == MemoryIoPoint.BeforeExportCommit)
                    clock.Advance(TimeSpan.FromMinutes(6));
            }
        };
        using var store = scope.Open(clock, hooks);
        await store.SaveAsync(MemoryFixtures.Save(clock, "expires before export",
            MemoryRetention.ExpiringAt(clock.Utc + TimeSpan.FromMinutes(5))));
        using var preview = await store.CreateExportPreviewAsync();
        var authorization = preview.Authorize(scope.ExportPath, MemoryExportDecision.Export);

        await MemoryFixtures.FailureAsync(MemoryFailure.QueryInvalidated, async () =>
            await store.ExportAsync(preview, authorization, scope.ExportPath));
        Assert.False(File.Exists(scope.ExportPath));
        Assert.Empty((await store.InspectAsync()).Facts);
        Assert.DoesNotContain("expires before export", await File.ReadAllTextAsync(scope.StorePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReservedStoreFilenameAndCaseChangedDestinationAreRejected()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        using var preview = await store.CreateExportPreviewAsync();
        var reserved = Path.Combine(scope.StoreDirectory, MemoryStore.StoreFileName);
        MemoryFixtures.Failure(MemoryFailure.InvalidPath,
            () => preview.Authorize(reserved, MemoryExportDecision.Export));

        var authorizedPath = Path.Combine(scope.Root, "Memory-Export.json");
        var changedCase = Path.Combine(scope.Root, "memory-export.json");
        var authorization = preview.Authorize(authorizedPath, MemoryExportDecision.Export);
        await MemoryFixtures.FailureAsync(MemoryFailure.ConsentMismatch, async () =>
            await store.ExportAsync(preview, authorization, changedCase));
        Assert.False(File.Exists(authorizedPath));
        Assert.False(File.Exists(changedCase));
    }
}
