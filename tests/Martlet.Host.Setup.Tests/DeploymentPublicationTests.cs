namespace Martlet.Host.Setup.Tests;

public sealed class DeploymentPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publishes_actual_owned_files_and_reopens_without_runtime_authority(bool acquire)
    {
        await using var harness = await DeploymentHarness.CreateAsync(acquire: acquire);
        var preview = await harness.PreviewAsync();
        Assert.Empty(Directory.GetFileSystemEntries(harness.Store.RootPath));
        var result = await harness.PublishAsync(preview);
        Assert.True(result.ConfigurationPublished, result.Failure?.ToString());
        Assert.False(result.HostReady);
        Assert.False(result.RuntimeEnabled);
        Assert.False(result.ExecutionAuthorized);
        foreach (var file in preview.Bundle.Files)
            Assert.Equal(file.Content.ToArray(), File.ReadAllBytes(Path.Combine(result.FinalPath!, file.Name)));
        var reopened = await harness.PreviewAsync();
        Assert.True(reopened.AlreadyPublished);
        var before = DeploymentHarness.Tree(harness.Store.RootPath);
        Assert.Equal(HostDeploymentPublicationState.AlreadyPublished, (await harness.PublishAsync(reopened)).State);
        Assert.Equal(before.OrderBy(p => p.Key), DeploymentHarness.Tree(harness.Store.RootPath).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("scope")]
    [InlineData("expired")]
    [InlineData("review")]
    [InlineData("cancel")]
    public async Task Invalid_permission_or_changed_binding_never_writes(string cause)
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var preview = await harness.PreviewAsync();
        var approval = cause == "default" ? preview.Approve() : cause == "scope" ?
            preview.Approve(HostDeploymentConfigurationDecision.Publish) :
            preview.Approve(HostDeploymentConfigurationDecision.Publish, [HostDeploymentConfigurationScope.LocalConfigurationFiles]);
        if (cause == "expired") harness.Images.Clock.UtcNow += TimeSpan.FromMinutes(11);
        if (cause == "review") harness.Images.Review.AppendWhitespace();
        var result = await harness.Coordinator.PublishAsync(harness.Definition, preview, approval,
            new CancellationToken(cause == "cancel"));
        Assert.False(result.ConfigurationPublished);
        Assert.NotNull(result.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(harness.Store.RootPath));
    }

    [Fact]
    public async Task Approval_is_one_use_and_preview_is_exact()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var preview = await harness.PreviewAsync();
        var approval = preview.Approve(HostDeploymentConfigurationDecision.Publish,
            [HostDeploymentConfigurationScope.LocalConfigurationFiles]);
        Assert.True((await harness.Coordinator.PublishAsync(harness.Definition, preview, approval)).ConfigurationPublished);
        Assert.Equal(HostDeploymentFailure.ConsentConsumed,
            (await harness.Coordinator.PublishAsync(harness.Definition, preview, approval)).Failure);
        Assert.Equal(HostDeploymentFailure.PreviewChanged, (await harness.PublishAsync(preview)).Failure);
    }

    [Fact]
    public async Task New_configuration_revision_preserves_previous_files_and_identity()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var before = await harness.PreviewAsync();
        Assert.True((await harness.PublishAsync(before)).ConfigurationPublished);
        var oldFiles = DeploymentHarness.Tree(before.FinalPath);
        using var acquisition = harness.Images.Coordinator();
        Assert.True((await harness.Images.RunAsync(acquisition)).Published);
        var after = await harness.PreviewAsync();
        Assert.True((await harness.PublishAsync(after)).ConfigurationPublished);
        Assert.Equal(oldFiles.OrderBy(p => p.Key), DeploymentHarness.Tree(before.FinalPath).OrderBy(p => p.Key));
        Assert.Equal(before.Bundle.ProjectName, after.Bundle.ProjectName);
        Assert.Equal(2, DeploymentJournalCodec.Read(File.ReadAllBytes(harness.Journal)).Revisions.Length);
    }
}
