namespace Martlet.Host.Setup.Tests;

internal sealed class DeploymentHarness : IAsyncDisposable
{
    internal required ImageAcquisitionHarness Images { get; init; }
    internal required HostDeploymentDefinition Definition { get; init; }
    internal required LocalHostDeploymentStore Store { get; init; }
    internal required HostDeploymentCoordinator Coordinator { get; init; }
    internal required RecordingDirectoryCommitter Committer { get; init; }

    internal static async Task<DeploymentHarness> CreateAsync(string roles = "ollama-llm", bool acquire = false)
    {
        var images = await ImageAcquisitionHarness.CreateAsync(roles);
        try
        {
            if (acquire)
            {
                using var acquisition = images.Coordinator();
                Assert.True((await images.RunAsync(acquisition)).Published);
            }
            var config = new SetupConfiguration(roles == "both" ? "image-fixture" : "acquisition-fixture",
                roles == "both" ? [SetupRole.Llm, SetupRole.Tts] :
                    roles == "f5-tts" ? [SetupRole.Tts] : [SetupRole.Llm]);
            var identity = new HostDeploymentIdentity(images.SetupPlan.TargetHostId!.Value,
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                images.SetupPlan.InstallationMachine!.Runtimes[0].Owner.Id);
            var definition = new HostDeploymentDefinition(config, images.SetupPlan, images.Selection, identity);
            var output = Path.Combine(images.Root, "configuration-output");
            Directory.CreateDirectory(output);
            var committer = new RecordingDirectoryCommitter();
            var store = new LocalHostDeploymentStore(output, committer);
            return new()
            {
                Images = images, Definition = definition, Store = store,
                Coordinator = new(store, images.Review, images.Storage, images.Clock),
                Committer = committer
            };
        }
        catch { await images.DisposeAsync(); throw; }
    }
    internal async Task<HostDeploymentPreview> PreviewAsync() =>
        await Coordinator.PreviewAsync(Definition, Images.SetupPreview);
    internal Task<HostDeploymentPublicationResult> PublishAsync(HostDeploymentPreview preview) =>
        Coordinator.PublishAsync(Definition, preview, preview.Approve(HostDeploymentConfigurationDecision.Publish,
            [HostDeploymentConfigurationScope.LocalConfigurationFiles])).AsTask();
    internal string Journal => Path.Combine(Store.RootPath, Definition.Identity.ProjectName + ".configuration.json");
    internal static Dictionary<string, string> Tree(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => FingerprintBuilder.Bytes(File.ReadAllBytes(path)));
    public ValueTask DisposeAsync() => Images.DisposeAsync();
}
