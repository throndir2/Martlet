using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Core.Singing;

namespace Martlet.Core.Tests;

public sealed class ExternalEndpointsTests
{
    private static SetupRoute Route(SetupRole role, SetupRouteType type, string origin, string model, bool? enabled = true) => new()
    {
        RouteSchemaVersion = 1, RouteType = type, Enabled = enabled, Role = role, ProviderAlias = "test", Origin = origin, ModelId = model,
        ConfigurationRevision = Guid.NewGuid()
    };

    private static DeepThinkingSettings Endpoint(string origin, string model) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    [Fact]
    public void Each_endpoint_is_listed_once_with_every_use_and_never_this_pc_or_a_paired_computer()
    {
        var pool = new ThinkingPoolSettings
        {
            Members = [Endpoint(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "x-ai/grok-4.3"), Endpoint("http://127.0.0.1:11434/v1", "gemma4:12b")],
            OffMembers = [Endpoint("http://192.168.1.20:11434/v1", "qwen3:8b")]
        };
        var pictures = new PoolList
        {
            Area = PoolAreas.PictureMaking.Id,
            Members =
            [
                PoolMember.Cloud(ChatCompletionsEndpointCatalog.OpenRouterId, "google/gemini-3.1-flash-image") with { Off = true },
                PoolMember.Service("http://lab:8188/"),
                PoolMember.Computer("diva")
            ]
        };
        var singing = new PoolList { Area = PoolAreas.Singing.Id, Members = [PoolMember.Cloud(SingingCloud.Mureka, SingingCloud.MurekaModel)] };
        var speaking = new PoolList { Area = PoolAreas.Speaking.Id, Members = [PoolMember.Cloud("openai", "gpt-4o-mini-tts")] };
        var endpoints = ExternalEndpoints.Find(new()
        {
            Routes =
            [
                Route(SetupRole.Tts, SetupRouteType.OpenAi, OpenAiSetup.Origin, "gpt-4o-mini-tts"),
                Route(SetupRole.Llm, SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "google/gemma-4-26b-a4b-it"),
                Route(SetupRole.Stt, SetupRouteType.LocalParakeet, "local:parakeet", "parakeet")
            ],
            Fallback = new() { Origin = "https://user:secret@api.example.com/v1", ModelId = "small", ConfigurationRevision = Guid.NewGuid() },
            ThinkingPool = pool,
            Pools = [pictures, singing, speaking],
            Tools = [new("home", "https://mcp.example.net/s/token/mcp", false), new("files", null, false)]
        });

        Assert.Equal(["api.example.com", "mcp.example.net", "Mureka", "OpenAI", "OpenRouter", "192.168.1.20", "lab"], endpoints.Select(e => e.Name));
        var openRouter = endpoints.Single(e => e.Name == "OpenRouter");
        Assert.True(openRouter.Cloud);
        Assert.Equal("https://openrouter.ai", openRouter.Address);
        Assert.Equal("openrouter.ai", openRouter.Id);
        Assert.Equal("Cloud provider at https://openrouter.ai. Used for Thinking (google/gemma-4-26b-a4b-it); Thinking pool (x-ai/grok-4.3); " +
            "Picture making (google/gemini-3.1-flash-image, turned off).", openRouter.Describe());
        // The Speaking route and the same model in the Speaking list are one use.
        Assert.Equal("Speaking (gpt-4o-mini-tts)", Assert.Single(endpoints.Single(e => e.Name == "OpenAI").Uses).Describe());
        Assert.Equal("Thinking pool (qwen3:8b, turned off)", Assert.Single(endpoints.Single(e => e.Name == "192.168.1.20").Uses).Describe());
        Assert.False(endpoints.Single(e => e.Name == "lab").Cloud);
        // Never a key, a user name or an address path.
        Assert.Equal("https://api.example.com", endpoints.Single(e => e.Name == "api.example.com").Address);
        Assert.Equal("If Thinking fails (small)", endpoints.Single(e => e.Name == "api.example.com").Uses[0].Describe());
        Assert.Equal("https://mcp.example.net", endpoints.Single(e => e.Name == "mcp.example.net").Address);
        Assert.Equal("Tools (home)", endpoints.Single(e => e.Name == "mcp.example.net").Uses[0].Describe());
        Assert.Equal("https://api.mureka.ai", endpoints.Single(e => e.Name == "Mureka").Address);
        Assert.DoesNotContain(endpoints, e => e.Describe().Contains("secret", StringComparison.Ordinal) || e.Describe().Contains("token", StringComparison.Ordinal));
        Assert.Equal("5 cloud providers and 2 servers on your network. Each line says what Martlet uses it for.", ExternalEndpoints.Summary(endpoints));
    }

    [Fact]
    public void Nothing_outside_your_computers_says_so()
    {
        var endpoints = ExternalEndpoints.Find(new()
        {
            Routes = [Route(SetupRole.Llm, SetupRouteType.ChatCompletions, "http://localhost:11434/v1", "gemma4:12b")],
            Pools = [new PoolList { Area = PoolAreas.Speaking.Id, Members = [PoolMember.ThisPc(), PoolMember.Computer("diva")] }]
        });
        Assert.Empty(endpoints);
        Assert.Equal("None. Martlet sends work only to this PC and your own computers.", ExternalEndpoints.Summary(endpoints));
    }

    [Fact]
    public void A_turned_off_route_and_an_unknown_provider_still_show()
    {
        var endpoints = ExternalEndpoints.Find(new()
        {
            Routes = [Route(SetupRole.Stt, SetupRouteType.OpenAi, OpenAiSetup.Origin, "gpt-4o-transcribe", enabled: false)],
            Pools = [new PoolList { Area = PoolAreas.Vision.Id, Members = [PoolMember.Cloud("acme", "eye-1")] }]
        });
        Assert.Equal("Listening (gpt-4o-transcribe, turned off)", endpoints.Single(e => e.Name == "OpenAI").Uses[0].Describe());
        var acme = endpoints.Single(e => e.Name == "acme");
        Assert.Null(acme.Address);
        Assert.Equal("Cloud provider. Used for Vision (eye-1).", acme.Describe());
    }
}
