using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

/// <summary>A key saved for a hosted provider anywhere in Martlet counts for that provider everywhere (SavedProviderKeys).</summary>
public sealed class SavedProviderKeysTests : IDisposable
{
    private const string Nvidia = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl;
    private readonly string data = Path.Combine(Path.GetTempPath(), "martlet-saved-keys-" + Guid.NewGuid().ToString("N"));

    public SavedProviderKeysTests() => Directory.CreateDirectory(data);

    public void Dispose()
    {
        if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
    }

    [Fact]
    public void A_provider_is_known_by_its_base_url_with_or_without_the_last_slash()
    {
        Assert.Equal("nvidia-build", SavedProviderKeys.ProviderOf(Nvidia));
        Assert.Equal("nvidia-build", SavedProviderKeys.ProviderOf(Nvidia + "/"));
        Assert.Equal("openai", SavedProviderKeys.ProviderOf(OpenAiSetup.Origin));
        Assert.Equal("openai", SavedProviderKeys.ProviderOf("https://api.openai.com/v1"));
        Assert.Equal("https://llm.example.com/v1", SavedProviderKeys.ProviderOf("https://llm.example.com/v1/"));
        Assert.Null(SavedProviderKeys.ProviderOf(" "));
    }

    [Fact]
    public void The_fallback_and_the_vision_list_keys_count_and_a_custom_server_is_not_a_planner_provider()
    {
        var settings = SetupSettings.Begin(null) with
        {
            ThinkingFallback = new() { Origin = "https://llm.example.com/v1", ModelId = "m", CredentialId = Guid.NewGuid(), ConfigurationRevision = Guid.NewGuid() }
        };
        Assert.Empty(SavedProviderKeys.Providers(settings, data));
        Assert.Equal("If Thinking fails", Assert.Single(SavedProviderKeys.For(settings, data, "https://llm.example.com/v1")).Where);

        var member = PoolMember.Cloud("nvidia-build", "meta/llama-vision", Nvidia);
        Assert.True(PoolSettings.SaveFor(data, PoolAreas.Vision, new PoolList { Area = PoolAreas.Vision.Id, Members = [member] }));
        Assert.True(PoolKeys.Load(data).With(PoolAreas.Vision.Id, member.Key, Guid.NewGuid()).Save(data));

        Assert.Equal(["nvidia-build"], SavedProviderKeys.Providers(settings, data));
        Assert.Equal("the Vision list", Assert.Single(SavedProviderKeys.For(settings, data, Nvidia)).Where);
        Assert.Empty(SavedProviderKeys.Providers(null, data));
    }
}
