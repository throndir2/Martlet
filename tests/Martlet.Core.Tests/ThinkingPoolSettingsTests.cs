using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ThinkingPoolSettingsTests : IDisposable
{
    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl, OpenRouter = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-pool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static SetupRoute Chat(SetupRole role, string origin, string model) => new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = role, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin, ModelId = model,
        ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static SetupRoute Gateway(SetupRole role, SetupRouteType type, string hostId) => new()
    {
        RouteType = type, Role = role, ProviderAlias = SelfHostSetup.Gateway(type).Alias, Origin = $"https://{hostId}.local:9443", ModelId = "fixture",
        ConfigurationRevision = Guid.NewGuid(), Enabled = true,
        Gateway = new() { SchemaVersion = 1, Origin = $"https://{hostId}.local:9443", HostId = hostId, SpkiFingerprint = "sha256:" + new string('0', 64), DeviceRole = SelfHostSetup.GatewayRole }
    };

    private static DeepThinkingSettings Role(string hostId) => new()
    {
        Place = DeepThinkingPlace.Host, ModelId = "gemma4:27b", HostId = hostId, HostOrigin = $"https://{hostId}.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid(),
        HostRouteId = SelfHostSetup.DeepThinkingRouteId
    };

    [Fact]
    public void Deep_thinking_places_become_pool_members_once()
    {
        Directory.CreateDirectory(directory);
        var cloud = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = OpenRouter, ModelId = "x-ai/grok-4.3" };
        Assert.True(new DeepThinkingSettings().WithPool([cloud, Role("diva") with { Slots = 3 }]).Save(directory));

        var (migrated, state) = ThinkingPoolSettings.Read(directory);
        Assert.Equal("migrated", state);
        Assert.True(File.Exists(Path.Combine(directory, ThinkingPoolSettings.FileName)));
        var (loaded, again) = ThinkingPoolSettings.Read(directory);
        Assert.Equal("loaded", again);
        // Same as Thinking is never a member: it is the empty-pool fallback, on by default.
        Assert.Equal(["endpoint:" + OpenRouter + "|x-ai/grok-4.3", "host:diva"], loaded.Members.Select(m => m.Key));
        Assert.Equal(3, loaded.Members[1].Slots);
        Assert.True(loaded.UseConversationModelWhenEmpty);
        Assert.NotNull(migrated.MigratedAt);
    }

    [Fact]
    public void A_read_only_read_does_not_write_the_new_file()
    {
        Directory.CreateDirectory(directory);
        Assert.True(Role("diva").Save(directory));
        Assert.Equal("migrated", ThinkingPoolSettings.Read(directory, save: false).State);
        Assert.False(File.Exists(Path.Combine(directory, ThinkingPoolSettings.FileName)));
    }

    [Fact]
    public void Members_are_added_replaced_and_removed_by_key()
    {
        var pool = new ThinkingPoolSettings().Add(Role("diva")).Add(Role("ripley"));
        pool = pool.Add(Role("diva") with { ModelId = "qwen3:8b" });
        Assert.Equal(["host:diva", "host:ripley"], pool.Members.Select(m => m.Key));
        Assert.Equal("qwen3:8b", pool.Members[0].ModelId);
        Assert.Equal(["host:ripley"], pool.Remove("host:diva").Members.Select(m => m.Key));
        Assert.Throws<ContractException>(() => pool.Add(new DeepThinkingSettings()));
    }

    [Fact]
    public void An_empty_pool_uses_the_conversation_model_only_when_allowed()
    {
        var thinking = Chat(SetupRole.Llm, OpenRouter, "x-ai/grok-4.3");
        Assert.True(new ThinkingPoolSettings().Plan([thinking]).Plan.Available);
        var off = new ThinkingPoolSettings { UseConversationModelWhenEmpty = false }.Plan([thinking]);
        Assert.False(off.Plan.Available);
        Assert.Contains("no member", off.Plan.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Warnings_name_members_beside_thinking_or_the_voice_but_never_block()
    {
        var routes = new[]
        {
            Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva"),
            Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "ripley")
        };
        var pool = new ThinkingPoolSettings().Add(Role("diva")).Add(Role("ripley")).Add(Role("quiet")).Plan(routes);
        Assert.All(pool.Spots, spot => Assert.True(spot.Plan.Available));
        var warnings = ThinkingPoolWarnings.For(pool, routes);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("diva's Thinking pool", StringComparison.Ordinal) && w.Contains("Thinking model", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("ripley's Thinking pool", StringComparison.Ordinal) && w.Contains("voice", StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_model_in_the_same_ollama_is_warned_about()
    {
        var routes = new[] { Chat(SetupRole.Llm, Ollama, "gemma4:e4b") };
        var pool = new ThinkingPoolSettings().Add(new() { Place = DeepThinkingPlace.Endpoint, Origin = Ollama, ModelId = "gemma4:12b" }).Plan(routes);
        Assert.Contains("same Ollama server", Assert.Single(ThinkingPoolWarnings.For(pool, routes)), StringComparison.Ordinal);
    }
}
