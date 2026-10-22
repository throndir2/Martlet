using Martlet.Conversation;
using Martlet.Core.Installation;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Background thinking's simple list (Where it runs), read through the page model with a fake app.</summary>
public sealed class BackgroundThinkingPageTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("martlet-background-thinking-").FullName;

    // MainWindow's static resources use pack:// addresses, so the scheme must be known before MainWindow.LocalChatModels.
    public BackgroundThinkingPageTests() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Ollama_on_this_pc_is_a_machine_to_tick_until_a_member_uses_it()
    {
        var services = new FakeServices(directory) { OllamaModels = ["gemma4:e4b", "qwen3:8b"] };
        var line = Assert.Single(new ThinkingPoolPageModel(services).Candidates());
        Assert.Equal(("this-pc", ThinkingPoolPageModel.JoinKind.ThisPc, "This PC (Ollama)", (string?)null),
            (line.Id, line.Kind, line.Title, line.Cannot));
        Assert.Equal("gemma4:e4b", line.Selected);
        Assert.Contains("qwen3:8b", line.Models);
        Assert.Contains("Downloaded: gemma4:e4b, qwen3:8b.", line.Detail, StringComparison.Ordinal);

        new ThinkingPoolSettings().Add(new DeepThinkingSettings
        {
            Place = DeepThinkingPlace.Endpoint, Origin = MainWindow.LocalOllamaBaseUrl, ModelId = "qwen3:8b"
        }).Save(directory);
        var model = new ThinkingPoolPageModel(services);
        Assert.Empty(model.Candidates());
        // The member's own row keeps its box and model; the next model to offer here skips the one in the list.
        Assert.Equal("qwen3:8b", model.Member(0).Member.ModelId);
        Assert.Equal("gemma4:e4b", model.LocalStart(model.LocalChoices));
    }

    [Fact]
    public void A_cloud_members_model_list_starts_with_its_model()
    {
        var list = ThinkingPoolPageModel.CloudModels(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "acme/my-model");
        Assert.Equal("acme/my-model", list[0]);
        Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class FakeServices(string directory) : IThinkingPoolPageServices
    {
        public string? DataDirectory => directory;
        public AppSettings? Settings => null;
        public IReadOnlyList<PairedHost> PairedHosts => [];
        public HostCheck? CheckOf(string hostId) => null;
        public HostHardware? HardwareOf(string hostId) => null;
        public ModelAbilities SavedModelAbilities() => ModelAbilities.Load(directory);
        public IReadOnlyList<string>? OllamaModels { get; init; }
        public MachineInfo Machine => MachineInfo.Unknown;
        public bool IsHost => false;
        public bool ToolsRefused(SetupRoute route) => false;
        public string? CannotHand(string hostId, string roleKind, string job) => null;
        public bool ChangesRolesOn(PairedHost host) => true;
        public IReadOnlyList<ThinkingPoolCooling> PoolCooling() => [];
        public FirstWordTimes? FirstWords => null;
        public ThinkingPoolStatus? PoolStatusNow() => null;
        public bool Closing => false;
        public CancellationToken Lifetime => CancellationToken.None;
        public void ShowStatus(string text) { }
        public void Redraw() { }
        public void RedrawIfOpen() { }
        public void MarkEdited() { }
        public void UseAutoSave(AutoSave autoSave) { }
        public void ReloadThinkingPool() { }
        public Task<bool> SaveRepliesAsync(Func<GenerationSettings?, GenerationSettings?> update, Action<GenerationSettings?> saved, string done) =>
            Task.FromResult(false);
        public Task<string?> RunHostActionAsync(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers = null) =>
            Task.FromResult<string?>(null);
        public Task<HostCheck> CheckHostAsync(PairedHost host) => throw new NotSupportedException();
        public void RunNodeAction(NodeAction action) { }
        public Task CheckOllamaAsync() => Task.CompletedTask;
        public bool Confirm(string question, string title, string yes, string no, string questionId) => false;
    }
}
