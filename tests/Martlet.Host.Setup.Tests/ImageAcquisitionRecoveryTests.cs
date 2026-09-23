using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

public sealed class ImageAcquisitionRecoveryTests
{
    [Fact]
    public async Task Interrupted_quarantine_move_still_discloses_full_reacquisition_budget()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var acquired = await harness.RunAsync(coordinator);
        var layer = harness.Selection.ImageContentInventory.Contents.First(content => content.Kind == ArtifactImageContentKind.Layer);
        using (var stream = new FileStream(Path.Combine(acquired.LayoutPath!, "blobs", "sha256", layer.Digest[7..]),
            FileMode.Open, FileAccess.Write, FileShare.None))
            stream.WriteByte(0xa5);
        var recovery = await harness.PreviewAsync(coordinator);
        var failed = false;
        harness.Committer.ShouldFail = () =>
        {
            if (failed || !Directory.Exists(recovery.Plan.QuarantinePath)) return false;
            failed = true;
            return true;
        };
        var interrupted = await coordinator.RunImagesAsync(recovery.Plan,
            recovery.Approve(ArtifactAcquisitionDecision.Approve, recovery.Plan.RequiredConsentScopes));
        Assert.True(failed);
        Assert.False(interrupted.Published);
        Assert.Equal(ArtifactImageJournalState.Quarantining,
            ArtifactImageJournalCodec.Read(await File.ReadAllBytesAsync(recovery.Plan.JournalPath)).State);
        var resumed = await harness.PreviewAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionPreviewState.RecoveryRequired, resumed.State);
        Assert.True(resumed.Plan.QuarantineAndReacquire);
        Assert.Equal(resumed.Plan.KnownContentBytes, resumed.Plan.RemainingContentBytes);
        Assert.True(resumed.Plan.MaximumContentResponseBytes >= resumed.Plan.KnownContentBytes);
        Assert.True((await harness.RunAsync(coordinator)).Published);
    }

    [Fact]
    public async Task Canceled_published_revalidation_preserves_location_state_for_fresh_verification()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        Assert.True((await harness.RunAsync(coordinator)).Published);
        var preview = await harness.PreviewAsync(coordinator);
        var requests = harness.Server.Requests.Count;
        using var cancel = new CancellationTokenSource();
        harness.Committer.ShouldFail = () => { cancel.Cancel(); return false; };
        var canceled = await coordinator.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes), cancel.Token);
        harness.Committer.ShouldFail = null;
        Assert.Equal(ArtifactAcquisitionFailure.Canceled, canceled.Failure);
        var journal = ArtifactImageJournalCodec.Read(await File.ReadAllBytesAsync(preview.Plan.JournalPath));
        Assert.Equal(ArtifactImageJournalState.Published, journal.State);
        Assert.Equal(ArtifactAcquisitionFailure.Canceled, journal.LastFailure);
        Assert.Equal(ArtifactAcquisitionRunState.AlreadyAcquired, (await harness.RunAsync(coordinator)).State);
        Assert.Equal(requests, harness.Server.Requests.Count);
    }

    [Fact]
    public async Task Aggregate_corruption_growth_is_rejected_before_hashing_any_file()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        Assert.True((await harness.RunAsync(coordinator)).Published);
        var preview = await harness.PreviewAsync(coordinator);
        foreach (var content in harness.Selection.ImageContentInventory.Contents.Where(content => content.Kind == ArtifactImageContentKind.Layer))
        {
            using var stream = new FileStream(Path.Combine(preview.Plan.DestinationPath, "blobs", "sha256", content.Digest[7..]),
                FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(content.ExpectedBytes!.Value + ArtifactAcquisitionCoordinator.DefaultFreeSpaceReserveBytes / 2 + 1);
        }
        var hashChecks = 0;
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await harness.Storage.InspectImageAsync(preview.Plan.Paths, harness.Selection,
                _ => { hashChecks++; return ValueTask.CompletedTask; }, CancellationToken.None));
        Assert.Equal(ArtifactAcquisitionFailure.FinalConflict, error.Failure);
        Assert.Equal(0, hashChecks);
        Assert.False(Directory.Exists(preview.Plan.QuarantinePath));
    }

    [Fact]
    public async Task Interrupted_empty_partial_directory_removal_is_reconciled_without_downloading_again()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var preview = await harness.PreviewAsync(coordinator);
        var failed = false;
        harness.Committer.ShouldFail = () =>
        {
            if (failed || !File.Exists(Path.Combine(preview.Plan.StagingPath, "index.json")) ||
                Directory.Exists(Path.Combine(preview.Plan.StagingPath, "partials")))
                return false;
            failed = true;
            return true;
        };
        var interrupted = await coordinator.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes));
        Assert.True(failed);
        Assert.False(interrupted.Published);
        var requests = harness.Server.Requests.Count;
        Assert.True((await harness.RunAsync(coordinator)).Published);
        Assert.Equal(requests, harness.Server.Requests.Count);
    }

    [Fact]
    public async Task Post_rename_commit_failure_is_not_success_and_fresh_review_finishes_without_network()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var preview = await harness.PreviewAsync(coordinator);
        var failed = false;
        harness.Committer.ShouldFail = () =>
        {
            if (failed || !Directory.Exists(preview.Plan.DestinationPath)) return false;
            failed = true;
            return true;
        };
        var result = await coordinator.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes));
        Assert.True(failed);
        Assert.False(result.Published);
        Assert.Null(result.LayoutPath);
        Assert.True(Directory.Exists(preview.Plan.DestinationPath));
        Assert.False(Directory.Exists(preview.Plan.StagingPath));
        Assert.Equal(ArtifactImageJournalState.Publishing,
            ArtifactImageJournalCodec.Read(await File.ReadAllBytesAsync(preview.Plan.JournalPath)).State);
        var requests = harness.Server.Requests.Count;
        using var reopened = harness.Coordinator();
        var again = await harness.PreviewAsync(reopened);
        Assert.Equal(ArtifactAcquisitionPreviewState.FinalizationPending, again.State);
        Assert.Equal(ArtifactAcquisitionRunState.AlreadyAcquired, (await harness.RunAsync(reopened)).State);
        Assert.Equal(requests, harness.Server.Requests.Count);
    }

    [Fact]
    public async Task Cancel_before_atomic_rename_exposes_no_layout_and_reuses_verified_blobs()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var cancel = new CancellationTokenSource();
        using var first = harness.Coordinator(new ImageProgressAction(progress =>
        {
            if (progress.Phase == ArtifactAcquisitionProgressPhase.Finalizing) cancel.Cancel();
        }));
        var preview = await harness.PreviewAsync(first);
        var result = await first.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes), cancel.Token);
        Assert.False(result.Published);
        Assert.False(Directory.Exists(preview.Plan.DestinationPath));
        Assert.True(Directory.Exists(preview.Plan.StagingPath));
        var requests = harness.Server.Requests.Count;
        using var second = harness.Coordinator();
        Assert.True((await harness.RunAsync(second)).Published);
        Assert.Equal(requests, harness.Server.Requests.Count);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("tail")]
    [InlineData("truncate")]
    public async Task Owned_partial_corruption_is_quarantined_only_with_fresh_full_budget(string change)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync(layerBytes: 300_000);
        harness.Server.AllowClientDisconnect = true;
        var preview = await InterruptAsync(harness);
        var journal = ArtifactImageJournalCodec.Read(await File.ReadAllBytesAsync(preview.Plan.JournalPath));
        var row = Assert.Single(journal.Contents, row => row.State == ArtifactAcquisitionJournalState.Downloading && row.Bytes > 0);
        var partial = Path.Combine(preview.Plan.StagingPath, "partials", row.Digest[7..] + ".partial");
        using (var stream = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            if (change == "checksum") stream.WriteByte(0xa5);
            else if (change == "truncate") stream.SetLength(3);
            else { stream.Position = stream.Length; stream.WriteByte(0xa5); }
        }
        var before = await File.ReadAllBytesAsync(partial);
        using var reopened = harness.Coordinator();
        var recovery = await harness.PreviewAsync(reopened);
        Assert.Equal(ArtifactAcquisitionPreviewState.RecoveryRequired, recovery.State);
        Assert.Equal(recovery.Plan.KnownContentBytes, recovery.Plan.RemainingContentBytes);
        Assert.Equal(before, await File.ReadAllBytesAsync(partial));
        Assert.True((await harness.RunAsync(reopened)).Published);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(recovery.Plan.QuarantinePath, "partials", row.Digest[7..] + ".partial")));
    }

    [Fact]
    public async Task Replaced_partial_is_foreign_even_with_identical_bytes_and_is_not_recovered()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync(layerBytes: 300_000);
        harness.Server.AllowClientDisconnect = true;
        var preview = await InterruptAsync(harness);
        var journal = ArtifactImageJournalCodec.Read(await File.ReadAllBytesAsync(preview.Plan.JournalPath));
        var row = Assert.Single(journal.Contents, row => row.State == ArtifactAcquisitionJournalState.Downloading && row.Bytes > 0);
        var partial = Path.Combine(preview.Plan.StagingPath, "partials", row.Digest[7..] + ".partial");
        var original = await File.ReadAllBytesAsync(partial);
        File.Move(partial, Path.Combine(harness.Root, "retained-original-partial"));
        await File.WriteAllBytesAsync(partial, original);
        using var reopened = harness.Coordinator();
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () => await harness.PreviewAsync(reopened));
        Assert.Equal(ArtifactAcquisitionFailure.FinalConflict, error.Failure);
        Assert.Equal(original, await File.ReadAllBytesAsync(partial));
        Assert.False(Directory.Exists(preview.Plan.QuarantinePath));
    }

    [Fact]
    public async Task Range_ignored_does_not_append_and_new_full_reacquisition_needs_fresh_review()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync(layerBytes: 300_000);
        harness.Server.AllowClientDisconnect = true;
        var preview = await InterruptAsync(harness);
        var journal = ArtifactImageJournalCodec.Read(await File.ReadAllBytesAsync(preview.Plan.JournalPath));
        var row = Assert.Single(journal.Contents, row => row.State == ArtifactAcquisitionJournalState.Downloading && row.Bytes > 0);
        var partial = Path.Combine(preview.Plan.StagingPath, "partials", row.Digest[7..] + ".partial");
        var before = await File.ReadAllBytesAsync(partial);
        harness.OverrideResponse = request => request.Headers.Range is not null
            ? ImageAcquisitionHarness.Bytes(harness.Payloads[row.Digest], "application/octet-stream") : null;
        using var reopened = harness.Coordinator();
        var refused = await harness.RunAsync(reopened);
        Assert.Equal(ArtifactAcquisitionFailure.RangeUnsupported, refused.Failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(partial));
        var recovery = await harness.PreviewAsync(reopened);
        Assert.True(recovery.Plan.QuarantineAndReacquire);
        Assert.Equal(recovery.Plan.KnownContentBytes, recovery.Plan.RemainingContentBytes);
        Assert.True((await harness.RunAsync(reopened)).Published);
    }

    [Fact]
    public async Task Concurrent_journal_change_is_preserved_and_no_image_is_published()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync(layerBytes: 300_000);
        harness.Server.AllowClientDisconnect = true;
        string? journalPath = null;
        var changed = false;
        using var coordinator = harness.Coordinator(new ImageProgressAction(progress =>
        {
            if (changed || progress.Phase != ArtifactAcquisitionProgressPhase.Downloading || progress.PersistedBytes < 70_000) return;
            File.AppendAllText(journalPath!, " ");
            changed = true;
        }));
        var preview = await harness.PreviewAsync(coordinator);
        journalPath = preview.Plan.JournalPath;
        var result = await coordinator.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes));
        Assert.True(changed);
        Assert.Equal(ArtifactAcquisitionFailure.JournalChanged, result.Failure);
        Assert.EndsWith(" ", await File.ReadAllTextAsync(journalPath));
        Assert.False(Directory.Exists(preview.Plan.DestinationPath));
    }

    [Fact]
    public async Task Pending_and_foreign_published_children_are_not_removed()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var preview = await harness.PreviewAsync(coordinator);
        await File.WriteAllTextAsync(preview.Plan.JournalPath + ".pending", "foreign");
        var refused = await coordinator.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes));
        Assert.False(refused.Published);
        Assert.Equal("foreign", await File.ReadAllTextAsync(preview.Plan.JournalPath + ".pending"));
        Assert.Empty(harness.Server.Requests);
        File.Delete(preview.Plan.JournalPath + ".pending");
        var acquired = await harness.RunAsync(coordinator);
        var foreign = Path.Combine(acquired.LayoutPath!, "foreign.txt");
        await File.WriteAllTextAsync(foreign, "must remain");
        await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () => await harness.PreviewAsync(coordinator));
        Assert.Equal("must remain", await File.ReadAllTextAsync(foreign));
        Assert.False(Directory.Exists(preview.Plan.QuarantinePath));
    }

    [Fact]
    public async Task Strict_image_journal_preserves_unknown_wrong_purpose_and_corrupt_content()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        Assert.True((await harness.RunAsync(coordinator)).Published);
        var preview = await harness.PreviewAsync(coordinator);
        var original = await File.ReadAllBytesAsync(preview.Plan.JournalPath);
        foreach (var mutation in new[] { "purpose", "formatVersion", "unknown", "integritySha256", "null-key" })
        {
            var json = JsonNode.Parse(original)!.AsObject();
            if (mutation == "purpose") json["purpose"] = "ArtifactAcquisition";
            else if (mutation == "formatVersion") json["formatVersion"] = 2;
            else if (mutation == "integritySha256") json["integritySha256"] = new string('0', 64);
            else if (mutation == "null-key") json["publicationFiles"]![0]!["key"] = null;
            else json["unknown"] = true;
            var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
            await File.WriteAllBytesAsync(preview.Plan.JournalPath, bytes);
            await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () => await harness.PreviewAsync(coordinator));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(preview.Plan.JournalPath));
        }
        await File.WriteAllBytesAsync(preview.Plan.JournalPath, original);
        var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("\"formatVersion\":3", "\"formatVersion\":3,\"formatVersion\":3", StringComparison.Ordinal));
        Assert.Throws<ArtifactAcquisitionException>(() => ArtifactImageJournalCodec.Read(duplicate));
        Assert.Throws<ArtifactAcquisitionException>(() => ArtifactImageJournalCodec.Read(new byte[ArtifactImageJournalCodec.MaximumBytes + 1]));
    }

    [Fact]
    public async Task Native_directory_identity_handle_does_not_adopt_a_replacement()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        var directory = Path.Combine(harness.Root, "empty-owned-directory");
        var moved = Path.Combine(harness.Root, "retained-directory");
        Directory.CreateDirectory(directory);
        var before = ArtifactAcquisitionFileIdentity.DirectoryIdentity(directory);
        using (var handle = ArtifactAcquisitionFileIdentity.OpenDirectory(directory))
        {
            Assert.Equal(before, ArtifactAcquisitionFileIdentity.ReadDirectory(handle));
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => Directory.Move(directory, moved));
        }
        Directory.Move(directory, moved);
        Directory.CreateDirectory(directory);
        Assert.NotEqual(before, ArtifactAcquisitionFileIdentity.DirectoryIdentity(directory));
        Assert.Equal(before, ArtifactAcquisitionFileIdentity.DirectoryIdentity(moved));
    }

    private static async Task<ArtifactImageAcquisitionPreview> InterruptAsync(ImageAcquisitionHarness harness)
    {
        using var cancel = new CancellationTokenSource();
        using var coordinator = harness.Coordinator(new CancelingAcquisitionProgress(cancel, 70_000));
        var preview = await harness.PreviewAsync(coordinator);
        var result = await coordinator.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes), cancel.Token);
        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, result.State);
        return preview;
    }
}

internal sealed class ImageProgressAction(Action<ArtifactAcquisitionProgress> action) : IArtifactAcquisitionProgressSink
{
    public ValueTask ReportAsync(ArtifactAcquisitionProgress progress, CancellationToken cancellationToken)
    {
        action(progress);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
