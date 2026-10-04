using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class DeepThinkingPlanTests
{
    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl, OpenRouter = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
    private static readonly SetupRoute LocalThinking = Chat(SetupRole.Llm, Ollama, "gemma4:e4b");
    private static readonly SetupRoute CloudThinking = Chat(SetupRole.Llm, OpenRouter, "x-ai/grok-4.3");

    private static SetupRoute Chat(SetupRole role, string origin, string model) => new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = role, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin, ModelId = model,
        ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static SetupRoute Gateway(SetupRole role, SetupRouteType type, string hostId, string origin) => new()
    {
        RouteType = type, Role = role, ProviderAlias = SelfHostSetup.Gateway(type).Alias, Origin = origin, ModelId = "fixture",
        ConfigurationRevision = Guid.NewGuid(), Enabled = true,
        Gateway = new() { SchemaVersion = 1, Origin = origin, HostId = hostId, SpkiFingerprint = "sha256:" + new string('0', 64), DeviceRole = SelfHostSetup.GatewayRole }
    };

    private static DeepThinkingSettings LocalOllama(string model) => new() { Place = DeepThinkingPlace.Endpoint, Origin = Ollama, ModelId = model };

    private static DeepThinkingSettings Host(string hostId) => new()
    {
        Place = DeepThinkingPlace.Host, ModelId = "gemma4:27b", HostId = hostId, HostOrigin = $"https://{hostId}.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid()
    };

    [Fact]
    public void Thinkings_own_model_on_this_PC_or_a_paired_computer_cant_think_alongside_itself()
    {
        var local = DeepThinkingPlan.For(new(), [LocalThinking]);
        Assert.False(local.Available);
        Assert.Contains("gemma4:e4b", local.Why, StringComparison.Ordinal);
        Assert.False(DeepThinkingPlan.For(LocalOllama("gemma4:e4b"), [LocalThinking]).Available);
        Assert.False(DeepThinkingPlan.For(new(), [Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva", "https://diva.local:9443")]).Available);
        Assert.False(DeepThinkingPlan.For(Host("diva"), [Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva", "https://diva.local:9443")]).Available);
        Assert.False(DeepThinkingPlan.For(new(), []).Available);
    }

    [Fact]
    public void A_model_of_its_own_always_thinks_in_parallel()
    {
        Assert.True(DeepThinkingPlan.For(new(), [CloudThinking]).Available);
        Assert.True(DeepThinkingPlan.For(new() { Place = DeepThinkingPlace.Endpoint, Origin = OpenRouter, ModelId = "x-ai/grok-4.3" }, [LocalThinking]).Available);
        Assert.True(DeepThinkingPlan.For(Host("diva"), [LocalThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "imouto", "https://imouto.local:9443")]).Available);
        var sharing = DeepThinkingPlan.For(Host("diva"), [LocalThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "diva", "https://diva.local:9443")]);
        Assert.True(sharing.Available);
        Assert.Contains("the voice", sharing.Why, StringComparison.Ordinal);
        var withVoice = DeepThinkingPlan.For(LocalOllama("gemma4:12b"),
            [CloudThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "this-pc", "https://127.0.0.1:9443")]);
        Assert.True(withVoice.Available);
        Assert.False(withVoice.ChecksFit);
        Assert.All(new[] { DeepThinkingPlan.For(new(), [CloudThinking]), withVoice, sharing }, plan => Assert.False(plan.ChecksFit));
    }

    [Fact]
    public void A_second_model_in_Thinkings_Ollama_is_checked_to_fit_first()
    {
        var plan = DeepThinkingPlan.For(LocalOllama("gemma4:12b"), [LocalThinking]);
        Assert.True(plan.Available);
        Assert.True(plan.ChecksFit);
        Assert.Contains("second model beside Thinking's gemma4:e4b", plan.Why, StringComparison.Ordinal);
        // Another server on this PC (LM Studio beside Ollama) runs its own models; there is nothing of Ollama's to check.
        var other = DeepThinkingPlan.For(new() { Place = DeepThinkingPlace.Endpoint, Origin = "http://127.0.0.1:1234/v1", ModelId = "qwen3-8b" }, [LocalThinking]);
        Assert.True(other.Available);
        Assert.False(other.ChecksFit);
    }
}
