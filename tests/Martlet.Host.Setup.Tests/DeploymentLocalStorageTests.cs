using System.Text;

namespace Martlet.Host.Setup.Tests;

public sealed class DeploymentLocalStorageTests
{
    [Theory]
    [InlineData("final")]
    [InlineData("stage")]
    [InlineData("lease")]
    [InlineData("pending")]
    public async Task Foreign_state_is_preserved_even_with_matching_bytes(string kind)
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var preview = await harness.PreviewAsync();
        var snapshot = await harness.Store.InspectAsync(preview.Bundle, CancellationToken.None);
        if (kind is "final" or "stage")
        {
            var path = kind == "final" ? snapshot.FinalPath : snapshot.StagingPath;
            Directory.CreateDirectory(path);
            foreach (var file in preview.Bundle.Files) File.WriteAllBytes(Path.Combine(path, file.Name), file.Content.ToArray());
        }
        else File.WriteAllText(harness.Journal + (kind == "lease" ? ".lease" : ".pending"), new string('a', 64));
        var before = DeploymentHarness.Tree(harness.Store.RootPath);
        Assert.False((await harness.PublishAsync(preview)).ConfigurationPublished);
        Assert.Equal(before.OrderBy(p => p.Key), DeploymentHarness.Tree(harness.Store.RootPath).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Failure_at_each_commit_is_explicit_and_recovery_never_adopts_orphans(int failOn)
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var preview = await harness.PreviewAsync();
        harness.Committer.FailOnCall = failOn;
        var result = await harness.PublishAsync(preview);
        Assert.False(result.ConfigurationPublished);
        Assert.NotNull(result.Failure);
        harness.Committer.FailOnCall = null;
        try
        {
            var resumed = await harness.PreviewAsync();
            var recovered = await harness.PublishAsync(resumed);
            Assert.True(recovered.ConfigurationPublished, recovered.Failure?.ToString());
            foreach (var file in preview.Bundle.Files)
                Assert.Equal(file.Content.ToArray(), File.ReadAllBytes(Path.Combine(recovered.FinalPath!, file.Name)));
        }
        catch (HostDeploymentException error)
        {
            // Creation before durable ownership leaves a preserved bootstrap conflict, not resumable authority.
            Assert.Contains(failOn, new[] { 1, 3 });
            Assert.Contains(error.Failure, new[] { HostDeploymentFailure.StorageConflict, HostDeploymentFailure.ContentChanged });
            Assert.NotEmpty(Directory.GetFileSystemEntries(harness.Store.RootPath));
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("corrupt")]
    public async Task Incompatible_journals_are_preserved(string mutation)
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        Assert.True((await harness.PublishAsync(await harness.PreviewAsync())).ConfigurationPublished);
        var text = File.ReadAllText(harness.Journal);
        text = mutation switch
        {
            "version" => text.Replace("\"FormatVersion\":2", "\"FormatVersion\":1", StringComparison.Ordinal),
            "purpose" => text.Replace("HostDeploymentConfiguration", "OciImageAcquisition", StringComparison.Ordinal),
            "duplicate" => text.Replace("\"FormatVersion\":2", "\"FormatVersion\":2,\"FormatVersion\":2", StringComparison.Ordinal),
            "extra" => text.Insert(1, "\"Extra\":true,"),
            _ => text[..^1]
        };
        File.WriteAllText(harness.Journal, text, new UTF8Encoding(false));
        var before = File.ReadAllBytes(harness.Journal);
        await Assert.ThrowsAsync<HostDeploymentException>(() => harness.PreviewAsync());
        Assert.Equal(before, File.ReadAllBytes(harness.Journal));
    }

    [Fact]
    public async Task Same_bytes_replaced_file_is_not_owned()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var preview = await harness.PreviewAsync();
        Assert.True((await harness.PublishAsync(preview)).ConfigurationPublished);
        var path = Path.Combine(preview.FinalPath, "deployment.json");
        File.Move(path, path + ".old");
        File.WriteAllBytes(path, File.ReadAllBytes(path + ".old"));
        File.Delete(path + ".old");
        await Assert.ThrowsAsync<HostDeploymentException>(() => harness.PreviewAsync());
    }

    [Fact]
    public async Task Concurrent_writers_have_one_owner_and_do_not_overwrite()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var first = await harness.PreviewAsync();
        var second = await harness.PreviewAsync();
        var results = await Task.WhenAll(Task.Run(() => harness.PublishAsync(first)), Task.Run(() => harness.PublishAsync(second)));
        Assert.Single(results, result => result.ConfigurationPublished);
        Assert.Single(results, result => !result.ConfigurationPublished);
        Assert.True((await harness.PreviewAsync()).AlreadyPublished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_validates_real_owned_partial_prefix(bool corrupt)
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var preview = await harness.PreviewAsync();
        harness.Committer.FailOnCall = 5;
        Assert.False((await harness.PublishAsync(preview)).ConfigurationPublished);
        harness.Committer.FailOnCall = null;
        var snapshot = await harness.Store.InspectAsync(preview.Bundle, CancellationToken.None);
        var file = preview.Bundle.Files[0];
        var prefix = file.Content[..17].ToArray();
        if (corrupt) prefix[0] ^= 1;
        File.WriteAllBytes(Path.Combine(snapshot.StagingPath, file.Name), prefix);
        if (corrupt)
            await Assert.ThrowsAsync<HostDeploymentException>(() => harness.PreviewAsync());
        else
        {
            var fresh = await harness.PreviewAsync();
            Assert.True((await harness.PublishAsync(fresh)).ConfigurationPublished);
            Assert.Equal(file.Content.ToArray(), File.ReadAllBytes(Path.Combine(fresh.FinalPath, file.Name)));
        }
    }

    [Fact]
    public async Task Replaced_journal_between_preview_and_publish_is_a_conflict()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        Assert.True((await harness.PublishAsync(await harness.PreviewAsync())).ConfigurationPublished);
        var preview = await harness.PreviewAsync();
        File.Move(harness.Journal, harness.Journal + ".old");
        File.WriteAllBytes(harness.Journal, File.ReadAllBytes(harness.Journal + ".old"));
        Assert.Equal(HostDeploymentFailure.PreviewChanged, (await harness.PublishAsync(preview)).Failure);
    }

    [Fact]
    public async Task Windows_alias_and_replaced_private_root_are_refused()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        if (OperatingSystem.IsWindows())
            Assert.Throws<ArtifactAcquisitionException>(() =>
                new LocalHostDeploymentStore(harness.Store.RootPath + ".", harness.Committer));
        var preview = await harness.PreviewAsync();
        Directory.Move(harness.Store.RootPath, harness.Store.RootPath + ".old");
        Directory.CreateDirectory(harness.Store.RootPath);
        Assert.False((await harness.PublishAsync(preview)).ConfigurationPublished);
        Assert.Empty(Directory.GetFileSystemEntries(harness.Store.RootPath));
    }
}
