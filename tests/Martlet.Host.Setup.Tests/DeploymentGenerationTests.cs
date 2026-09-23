using System.Text;
using System.Text.Json;

namespace Martlet.Host.Setup.Tests;

public sealed class DeploymentGenerationTests
{
    [Theory]
    [InlineData("ollama-llm", true, 2)]
    [InlineData("ollama-llm", false, 1)]
    [InlineData("both", true, 2)]
    [InlineData("f5-tts", true, 1)]
    public async Task Exact_selected_content_produces_only_truthful_partial_files(string roles, bool acquire, int files)
    {
        await using var harness = await DeploymentHarness.CreateAsync(roles, acquire);
        var before = DeploymentHarness.Tree(harness.Images.Root);
        var first = await harness.PreviewAsync();
        var second = await harness.PreviewAsync();
        Assert.Equal(before.OrderBy(p => p.Key), DeploymentHarness.Tree(harness.Images.Root).OrderBy(p => p.Key));
        Assert.Equal(first.Bundle.Fingerprint, second.Bundle.Fingerprint);
        Assert.Equal(files, first.Bundle.Files.Length);
        Assert.False(first.Bundle.RunnableComposeAvailable);
        Assert.False(first.Bundle.ExecutionAuthorized);
        Assert.False(first.Bundle.HostReady);
        foreach (var file in first.Bundle.Files)
        {
            Assert.Equal(file.Content.ToArray(), second.Bundle.Files.Single(other => other.Name == file.Name).Content.ToArray());
            using var parsed = JsonDocument.Parse(file.Content);
            Assert.False(parsed.RootElement.TryGetProperty("services", out _));
            var text = Encoding.UTF8.GetString(file.Content.Span);
            Assert.DoesNotContain("/app/martlet-health", text);
            Assert.DoesNotContain("docker.sock", text);
            Assert.DoesNotContain("unless-stopped", text);
            Assert.DoesNotContain(harness.Images.Root, text);
        }
        Assert.DoesNotContain(first.Bundle.Files, file => file.Name == "compose.yaml" || file.Name == "gateway.json");
        Assert.Contains(first.Bundle.Findings, row => row.Subject == "gateway");
        if (roles == "ollama-llm")
            Assert.DoesNotContain(first.Bundle.Findings, row => row.Subject is "f5-tts" or "stt");
        var fragment = first.Bundle.Files.FirstOrDefault(file => file.Name.EndsWith(".compose-fragment.json", StringComparison.Ordinal));
        if (fragment is not null)
        {
            using var json = JsonDocument.Parse(fragment.Content);
            Assert.EndsWith(harness.Images.Selection.ImageCandidates.Single(image => image.RoleIds.Contains("ollama-llm")).Digest,
                json.RootElement.GetProperty("image").GetString());
            Assert.Equal("never", json.RootElement.GetProperty("pull_policy").GetString());
            Assert.False(json.RootElement.TryGetProperty("healthcheck", out _));
            Assert.False(json.RootElement.TryGetProperty("ports", out _));
            Assert.False(json.RootElement.TryGetProperty("user", out _));
        }
    }

    [Fact]
    public async Task Content_changes_revision_not_project_or_permanent_state()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var before = await harness.PreviewAsync();
        using var acquisition = harness.Images.Coordinator();
        Assert.True((await harness.Images.RunAsync(acquisition)).Published);
        var after = await harness.PreviewAsync();
        Assert.NotEqual(before.Bundle.Fingerprint, after.Bundle.Fingerprint);
        Assert.Equal(before.Bundle.ProjectName, after.Bundle.ProjectName);
        Assert.Equal(before.Bundle.Definition.ProposedIdentityDirectory, after.Bundle.Definition.ProposedIdentityDirectory);
        Assert.NotEqual(before.FinalPath, after.FinalPath);
    }

    [Fact]
    public async Task Host_owner_and_selection_cannot_be_substituted()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var definition = harness.Definition;
        Assert.Throws<HostDeploymentException>(() => new HostDeploymentDefinition(definition.Configuration,
            definition.Plan, definition.Selection, definition.Identity with { OwnerId = Guid.NewGuid() }));
        Assert.Throws<HostDeploymentException>(() => new HostDeploymentDefinition(definition.Configuration,
            definition.Plan, definition.Selection, definition.Identity with { HostId = Guid.NewGuid() }));
        Assert.Throws<HostDeploymentException>(() => new HostDeploymentDefinition(definition.Configuration,
            definition.Plan, harness.Images.Manifest.DescribeAcquisition(["f5-tts"], "ubuntu-24.04-x64", "linux/amd64"),
            definition.Identity));
        using var acquisition = harness.Images.Coordinator();
        var wrong = await acquisition.ObservePublishedImagesAsync(
            harness.Images.Manifest.DescribeAcquisition(["f5-tts"], "ubuntu-24.04-x64", "linux/amd64"));
        Assert.Throws<HostDeploymentException>(() => new HostComposeGenerator().Generate(definition, wrong));
    }

    [Fact]
    public async Task Returned_file_memory_cannot_mutate_approved_bundle()
    {
        await using var harness = await DeploymentHarness.CreateAsync();
        var preview = await harness.PreviewAsync();
        var file = preview.Bundle.Files[0];
        var content = file.Content.ToArray();
        Array.Fill(content, (byte)0);
        Assert.Equal(file.Sha256, FingerprintBuilder.Bytes(file.Content.Span));
    }
}
