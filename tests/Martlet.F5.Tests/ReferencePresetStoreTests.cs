using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.F5;

namespace Martlet.F5.Tests;

public sealed class ReferencePresetStoreTests
{
    [Fact]
    public async Task Disposal_during_snapshot_commit_retains_ownership_and_persisted_audio()
    {
        using var scope = new F5TestScope();
        using var clock = new CommitBarrierClock();
        F5TestData.WriteWav(scope.SourcePath);
        F5ReferenceSnapshot snapshot;
        using (var store = scope.OpenStore(clock))
        {
            clock.BlockOnCall(2);
            var pending = Task.Run(() => F5TestData.SnapshotAsync(store, scope.SourcePath));
            Exception? disposeError;
            Exception? reopenError;
            try
            {
                await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                disposeError = Record.Exception(store.Dispose);
                reopenError = Record.Exception(() =>
                {
                    using var _ = scope.OpenStore();
                });
            }
            finally
            {
                clock.Release();
            }
            snapshot = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(F5Failure.Busy, Assert.IsType<F5Exception>(disposeError).Failure);
            Assert.Equal(F5Failure.Busy, Assert.IsType<F5Exception>(reopenError).Failure);
        }

        using var reopened = scope.OpenStore();
        using var lease = await reopened.AcquireForPreviewAsync(
            snapshot.PresetId, snapshot.ReferenceRevision);
        Assert.Equal(snapshot.ReferenceRevision, lease.ReferenceRevision);
        Assert.Equal(snapshot.ReferenceRevision,
            Assert.Single(Assert.Single(reopened.Inspect().Presets).Snapshots).ReferenceRevision);
    }

    [Fact]
    public async Task Apply_and_applied_acquisition_preserve_selection_in_both_orders()
    {
        using var scope = new F5TestScope();
        using var clock = new CommitBarrierClock();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore(clock);
        var first = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        var second = await F5TestData.SnapshotAsync(store, scope.SourcePath,
            first.PresetId, transcript: "A different reviewed transcript.");
        await F5TestData.ApplyAsync(store, first);
        var preview = await store.CreateApplyPreviewAsync(second.PresetId, second.ReferenceRevision);
        var authorization = preview.Authorize(F5ApplyDecision.Allow);

        using (var lease = await store.AcquireAppliedAsync(first.ReferenceRevision))
        {
            await F5TestData.FailureAsync(F5Failure.Busy, async () =>
                await store.ApplyAsync(preview, authorization));
            Assert.Equal(first.ReferenceRevision, store.Inspect().AppliedReferenceRevision);
        }

        clock.BlockOnCall(1);
        var apply = Task.Run(() => store.ApplyAsync(preview, authorization));
        try
        {
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await F5TestData.FailureAsync(F5Failure.Busy, async () =>
            {
                using var _ = await store.AcquireAppliedAsync(first.ReferenceRevision);
            });
        }
        finally
        {
            clock.Release();
        }
        await apply.WaitAsync(TimeSpan.FromSeconds(10));
        await F5TestData.FailureAsync(F5Failure.Conflict, async () =>
        {
            using var _ = await store.AcquireAppliedAsync(first.ReferenceRevision);
        });
        using var selected = await store.AcquireAppliedAsync(second.ReferenceRevision);
        Assert.Equal(second.ReferenceRevision, selected.ReferenceRevision);
        using var previewLease = await store.AcquireForPreviewAsync(
            first.PresetId, first.ReferenceRevision);
        Assert.Equal(first.ReferenceRevision, previewLease.ReferenceRevision);
    }

    private sealed class CommitBarrierClock : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim release = new();
        private int remainingCalls;
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void BlockOnCall(int calls) => remainingCalls = calls;
        internal void Release() => release.Set();

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Decrement(ref remainingCalls) == 0)
            {
                Entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test did not release the commit barrier.");
            }
            return F5TestData.Now;
        }

        public void Dispose() => release.Dispose();
    }

    [Fact]
    public async Task Snapshot_persists_exact_bytes_digests_transcript_and_reopens()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath, seed: 7);
        var sourceBytes = await File.ReadAllBytesAsync(scope.SourcePath);
        F5ReferenceSnapshot snapshot;
        using (var store = scope.OpenStore())
        {
            snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
            Assert.Null(store.Inspect().AppliedPresetId);
        }

        var storedPath = Path.Combine(scope.StoreDirectory, "audio",
            snapshot.PresetId.ToString("N"), snapshot.ReferenceRevision + ".wav");
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(storedPath));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),
            snapshot.AudioSha256);
        Assert.NotEqual(snapshot.AudioSha256, snapshot.TranscriptRevision);

        using var reopened = scope.OpenStore();
        var persisted = Assert.Single(Assert.Single(reopened.Inspect().Presets).Snapshots);
        Assert.Equal(snapshot.ReferenceRevision, persisted.ReferenceRevision);
        Assert.Equal(snapshot.TranscriptRevision, persisted.TranscriptRevision);
        Assert.Equal(F5TestData.Destination, persisted.Rights.ProcessingDestinationId);
    }

    [Fact]
    public async Task Same_path_replacement_is_detected_and_requires_new_snapshot_and_apply()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath, seed: 1);
        using var store = scope.OpenStore();
        var first = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, first);

        F5TestData.WriteWav(scope.SourcePath, seed: 2);
        var status = await store.CheckSourceAsync(first.PresetId, first.ReferenceRevision);
        Assert.Equal(F5ReferenceSourceState.Changed, status.State);
        await F5TestData.FailureAsync(F5Failure.SourceChanged, async () =>
        {
            using var _ = await store.AcquireAppliedAsync(first.ReferenceRevision);
        });

        var second = await F5TestData.SnapshotAsync(store, scope.SourcePath,
            presetId: first.PresetId);
        Assert.NotEqual(first.ReferenceRevision, second.ReferenceRevision);
        Assert.Equal(first.ReferenceRevision, store.Inspect().AppliedReferenceRevision);
        await F5TestData.ApplyAsync(store, second);
        Assert.Equal(second.ReferenceRevision, store.Inspect().AppliedReferenceRevision);
        using var lease = await store.AcquireAppliedAsync(second.ReferenceRevision);
        Assert.Equal(second.ReferenceRevision, lease.ReferenceRevision);
    }

    [Fact]
    public async Task Missing_external_source_does_not_destroy_or_hide_persisted_preset()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        F5ReferenceSnapshot snapshot;
        using (var store = scope.OpenStore())
            snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        File.Delete(scope.SourcePath);

        using var reopened = scope.OpenStore();
        Assert.Equal(snapshot.ReferenceRevision,
            Assert.Single(Assert.Single(reopened.Inspect().Presets).Snapshots).ReferenceRevision);
        var status = await reopened.CheckSourceAsync(
            snapshot.PresetId, snapshot.ReferenceRevision);
        Assert.Equal(F5ReferenceSourceState.Missing, status.State);
    }

    [Fact]
    public async Task Transcript_change_produces_new_revision_without_changing_audio_digest()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var first = await F5TestData.SnapshotAsync(store, scope.SourcePath,
            transcript: "First reviewed transcript.");
        var second = await F5TestData.SnapshotAsync(store, scope.SourcePath,
            first.PresetId, transcript: "Corrected reviewed transcript.");

        Assert.Equal(first.AudioSha256, second.AudioSha256);
        Assert.NotEqual(first.TranscriptRevision, second.TranscriptRevision);
        Assert.NotEqual(first.ReferenceRevision, second.ReferenceRevision);
        Assert.Equal(2, Assert.Single(store.Inspect().Presets).Snapshots.Count);
    }

    [Fact]
    public async Task Apply_is_one_use_revision_bound_and_blocked_during_active_use()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        var preview = await store.CreateApplyPreviewAsync(
            snapshot.PresetId, snapshot.ReferenceRevision);
        Assert.Throws<F5Exception>(() => preview.Authorize());
        var authorization = preview.Authorize(F5ApplyDecision.Allow);
        await store.ApplyAsync(preview, authorization);
        await F5TestData.FailureAsync(F5Failure.AuthorizationConsumed, async () =>
            await store.ApplyAsync(preview, authorization));

        using var lease = await store.AcquireAppliedAsync(snapshot.ReferenceRevision);
        var secondPreview = await store.CreateApplyPreviewAsync(
            snapshot.PresetId, snapshot.ReferenceRevision);
        await F5TestData.FailureAsync(F5Failure.Busy, async () =>
            await store.ApplyAsync(secondPreview,
                secondPreview.Authorize(F5ApplyDecision.Allow)));
    }

    [Fact]
    public async Task Changed_source_blocks_apply_and_preserves_previous_selection()
    {
        using var scope = new F5TestScope();
        var other = Path.Combine(scope.Root, "other.wav");
        F5TestData.WriteWav(scope.SourcePath, seed: 1);
        F5TestData.WriteWav(other, seed: 2);
        using var store = scope.OpenStore();
        var first = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        await F5TestData.ApplyAsync(store, first);
        var second = await F5TestData.SnapshotAsync(store, other, name: "Other");
        var preview = await store.CreateApplyPreviewAsync(
            second.PresetId, second.ReferenceRevision);
        F5TestData.WriteWav(other, seed: 3);

        await F5TestData.FailureAsync(F5Failure.SourceChanged, async () =>
            await store.ApplyAsync(preview,
                preview.Authorize(F5ApplyDecision.Allow)));
        Assert.Equal(first.ReferenceRevision, store.Inspect().AppliedReferenceRevision);
    }

    [Theory]
    [InlineData("transcript")]
    [InlineData("transcript_revision")]
    [InlineData("reference_revision")]
    [InlineData("audio_format")]
    public async Task Corrupt_manifest_content_and_nulls_fail_with_typed_store_error(
        string field)
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        F5ReferenceSnapshot snapshot;
        using (var store = scope.OpenStore())
            snapshot = await F5TestData.SnapshotAsync(store, scope.SourcePath);
        var manifestPath = Path.Combine(
            scope.StoreDirectory, F5ReferencePresetStore.StoreFileName);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        var persisted = root["presets"]![0]!["snapshots"]![0]!.AsObject();
        switch (field)
        {
            case "transcript":
                persisted[field] = "A different but still valid reviewed transcript.";
                break;
            case "transcript_revision":
                persisted[field] = new string('f', 64);
                break;
            case "reference_revision":
                var replacement = new string('e', 64);
                persisted[field] = replacement;
                persisted["audio_relative_path"] =
                    $"audio/{snapshot.PresetId:N}/{replacement}.wav";
                break;
            default:
                persisted[field] = null;
                break;
        }
        await File.WriteAllTextAsync(manifestPath, root.ToJsonString(new()
        {
            WriteIndented = true
        }));

        var error = Assert.Throws<F5Exception>(() =>
        {
            using var _ = scope.OpenStore();
        });
        Assert.Equal(F5Failure.CorruptStore, error.Failure);
    }
}
