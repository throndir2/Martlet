namespace Martlet.Host.Setup.Tests;

public sealed class PublishedImageObservationTests
{
    [Fact]
    public async Task Existing_owner_revalidates_bytes_without_transport_or_writes()
    {
        await using var harness = await DeploymentHarness.CreateAsync(acquire: true);
        var before = DeploymentHarness.Tree(harness.Images.Root);
        harness.Images.OverrideResponse = _ => throw new InvalidOperationException("Observation must not contact transport.");
        using var coordinator = harness.Images.Coordinator();
        var observation = await coordinator.ObservePublishedImagesAsync(harness.Images.Selection);
        Assert.True(observation.ContentVerified);
        Assert.False(observation.PublisherAuthenticated);
        Assert.False(observation.EngineImageAvailable);
        Assert.False(observation.ExecutionAuthorized);
        Assert.Equal(before.OrderBy(p => p.Key), DeploymentHarness.Tree(harness.Images.Root).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("replace")]
    [InlineData("extra")]
    [InlineData("pending")]
    public async Task Changed_content_cannot_become_verified(string mutation)
    {
        await using var harness = await DeploymentHarness.CreateAsync(acquire: true);
        var paths = harness.Images.Storage.GetImagePaths(harness.Images.Selection);
        var blob = Directory.GetFiles(Path.Combine(paths.Destination, "blobs", "sha256"))[0];
        if (mutation == "extra") File.WriteAllText(Path.Combine(paths.Destination, "foreign"), "foreign");
        else if (mutation == "pending") File.WriteAllText(paths.Journal + ".pending", "foreign");
        else
        {
            var content = File.ReadAllBytes(blob);
            if (mutation == "replace")
            {
                File.Move(blob, blob + ".old");
                File.WriteAllBytes(blob, content);
                File.Delete(blob + ".old");
            }
            else { content[0] ^= 1; File.WriteAllBytes(blob, content); }
        }
        using var coordinator = harness.Images.Coordinator();
        await Assert.ThrowsAsync<ArtifactAcquisitionException>(() =>
            coordinator.ObservePublishedImagesAsync(harness.Images.Selection).AsTask());
    }

    [Fact]
    public async Task Missing_content_does_not_create_lease_or_journal()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var before = DeploymentHarness.Tree(harness.Images.Root);
        using var coordinator = harness.Images.Coordinator();
        Assert.False((await coordinator.ObservePublishedImagesAsync(harness.Images.Selection)).ContentVerified);
        Assert.Equal(before.OrderBy(p => p.Key), DeploymentHarness.Tree(harness.Images.Root).OrderBy(p => p.Key));
    }

    [Fact]
    public async Task Held_content_lease_blocks_another_observer()
    {
        await using var harness = await DeploymentHarness.CreateAsync(acquire: true);
        using var scope = await ArtifactAcquisitionCoordinator.OpenPublishedImagesAsync(harness.Images.Storage,
            harness.Images.Selection, CancellationToken.None);
        using var coordinator = harness.Images.Coordinator();
        await Assert.ThrowsAsync<ArtifactAcquisitionException>(() =>
            coordinator.ObservePublishedImagesAsync(harness.Images.Selection).AsTask());
    }

    [Fact]
    public async Task Resealed_publication_history_cannot_substitute_another_index()
    {
        await using var harness = await DeploymentHarness.CreateAsync(acquire: true);
        var paths = harness.Images.Storage.GetImagePaths(harness.Images.Selection);
        var indexPath = Path.Combine(paths.Destination, "index.json");
        var bytes = "{}"u8.ToArray();
        File.WriteAllBytes(indexPath, bytes);
        var journal = ArtifactImageJournalCodec.Read(File.ReadAllBytes(paths.Journal));
        journal = journal with
        {
            PublicationFiles = journal.PublicationFiles.Select(file => file.Key == "index" ?
                file with { Bytes = bytes.Length, Sha256 = FingerprintBuilder.Bytes(bytes) } : file).ToArray()
        };
        File.WriteAllBytes(paths.Journal, ArtifactImageJournalCodec.Write(journal));
        using var coordinator = harness.Images.Coordinator();
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(() =>
            coordinator.ObservePublishedImagesAsync(harness.Images.Selection).AsTask());
        Assert.Equal(ArtifactAcquisitionFailure.ImageInventoryMismatch, error.Failure);
    }
}
