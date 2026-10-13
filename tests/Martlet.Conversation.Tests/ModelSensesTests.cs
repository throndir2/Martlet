using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

// What a model takes (ModelSenses): one order of facts for every place, and one rule for a model Martlet can't tell about.
// Fixture model names and origins only: nothing is sent.
public sealed class ModelSensesTests
{
    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl, Cloud = "https://openrouter.ai/api/v1", Diva = "https://diva.local:9443";

    private static DeepThinkingSettings Endpoint(string model, string origin = Ollama) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    private static DeepThinkingSettings Host(string model) => new()
    {
        Place = DeepThinkingPlace.Host, ModelId = model, HostId = "diva", HostOrigin = Diva,
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid()
    };

    private static SetupRoute OnDiva(string model) => new()
    {
        RouteType = SetupRouteType.GatewayOllama, Role = SetupRole.Llm, ProviderAlias = SelfHostSetup.GatewayOllamaAlias, Origin = Diva,
        ModelId = model, ConfigurationRevision = Guid.NewGuid(), Enabled = true,
        Gateway = new() { SchemaVersion = 1, Origin = Diva, HostId = "diva", SpkiFingerprint = "sha256:" + new string('0', 64), DeviceRole = SelfHostSetup.GatewayRole }
    };

    private static SetupRoute Chat(string model, string origin) => new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin,
        ModelId = model, ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static ModelAbilities Tested(string origin, string model, bool? sees = null, bool? hears = null) => new ModelAbilities().With(new()
    {
        Origin = origin, ModelId = model, Sees = sees, Hears = hears, Source = ModelAbility.TestRequest, CheckedAt = DateTimeOffset.UtcNow
    });

    private static bool PoolSees(DeepThinkingSettings member, ModelAbilities? abilities) =>
        ThinkingPoolCapabilities.For(member, abilities).HasFlag(ThinkingCapability.Vision);

    [Fact]
    public void A_paired_computers_model_that_failed_test_vision_gets_no_pictures_anywhere_even_with_a_vision_name()
    {
        var failed = Tested(Diva, "qwen2.5vl:7b", sees: false);
        var member = Host("qwen2.5vl:7b");

        Assert.True(PoolSees(member, null));
        Assert.False(PoolSees(member, failed));
        Assert.True(SensePool.Refuses(SenseKind.Image, member, failed));
        Assert.Equal(VisionSupport.Unsupported, ModelSenses.For(OnDiva("qwen2.5vl:7b"), failed).Sees);
    }

    [Fact]
    public void A_paired_computers_model_that_passed_test_vision_gets_pictures_even_with_an_unknown_name()
    {
        var passed = Tested(Diva, "house-eyes", sees: true);
        var member = Host("house-eyes");

        Assert.False(PoolSees(member, null));
        Assert.True(PoolSees(member, passed));
        Assert.Equal(VisionSupport.Supported, ModelSenses.For(member, passed).Sees);
        // The same facts as for a Thinking route on that computer.
        Assert.Equal(ModelSenses.For(OnDiva("house-eyes"), passed).Sees, ModelSenses.For(member, passed).Sees);
    }

    [Fact]
    public void A_model_martlet_cannot_tell_about_is_tried_by_the_list_and_for_reply_pictures_but_not_by_the_pool()
    {
        var unknown = ModelSenses.For(Endpoint("acme/house-model", Cloud), null);
        Assert.Equal(VisionSupport.Unknown, unknown.Sees);
        Assert.Equal(HearingSupport.Unknown, unknown.Hears);

        Assert.True(unknown.Takes(SenseKind.Image, SenseUse.List));
        Assert.True(unknown.Takes(SenseKind.Audio, SenseUse.List));
        Assert.True(unknown.Takes(SenseKind.Image, SenseUse.Reply));
        Assert.False(unknown.Takes(SenseKind.Audio, SenseUse.Reply));
        Assert.False(unknown.Takes(SenseKind.Image, SenseUse.Pool));
        Assert.False(unknown.Takes(SenseKind.Audio, SenseUse.Pool));

        Assert.False(SensePool.Refuses(SenseKind.Image, Endpoint("acme/house-model", Cloud), null));
        Assert.Equal(ThinkingCapability.Text | ThinkingCapability.Tools, ThinkingPoolCapabilities.For(Endpoint("acme/house-model", Cloud), null));
    }

    [Fact]
    public void A_known_answer_decides_for_every_use()
    {
        foreach (var use in Enum.GetValues<SenseUse>())
            foreach (var kind in Enum.GetValues<SenseKind>())
            {
                Assert.True(ModelSenses.Takes(kind, true, use));
                Assert.False(ModelSenses.Takes(kind, false, use));
            }
    }

    [Fact]
    public void A_retired_model_takes_nothing_in_any_place()
    {
        // NVIDIA Build retired Llama 4 Maverick (Martlet's built-in list); its name says it sees.
        const string maverick = "meta/llama-4-maverick-17b-128e-instruct";
        var member = Endpoint(maverick, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl);
        Assert.Equal(VisionSupport.Supported, VisionModelCatalog.Classify(maverick));

        Assert.Equal(new ModelSenses(VisionSupport.Unsupported, HearingSupport.Unsupported, ToolSupport.Unsupported), ModelSenses.For(member, null));
        Assert.Equal(ThinkingCapability.Text, ThinkingPoolCapabilities.For(member, null));
        Assert.True(SensePool.Refuses(SenseKind.Image, member, null));
        var thinking = Chat(maverick, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl);
        Assert.NotNull(ModelSenses.Retired(thinking, null));
        Assert.Equal(VisionSupport.Unsupported, ModelSenses.For(thinking, null).Sees);
        Assert.Null(ModelSenses.Retired(OnDiva("qwen3:8b"), null));
    }

    [Fact]
    public void What_each_route_carries_comes_first()
    {
        // A paired computer's gateway carries recordings but no tool calls; OpenAI's Responses route carries tool calls, no recordings.
        var host = ModelSenses.For(Host("gemma4:e4b"), null);
        Assert.Equal(HearingSupport.Supported, host.Hears);
        Assert.Equal(ToolSupport.Unsupported, host.Tools);
        Assert.False(host.CallsTools);
        var openAi = ModelSenses.For(SetupRouteType.OpenAi, "https://api.openai.com", "gpt-4.1-mini-2025-04-14", null);
        Assert.Equal(HearingSupport.Unsupported, openAi.Hears);
        Assert.Equal(ToolSupport.Supported, openAi.Tools);
        // A tool call is offered to a model Martlet can't tell about.
        Assert.True(ModelSenses.For(Endpoint("acme/house-model", Cloud), null).CallsTools);
        Assert.Equal(ModelSenses.Unknown, ModelSenses.For((SetupRoute?)null, null));
    }

    [Fact]
    public void The_pool_sends_recordings_only_to_an_endpoint()
    {
        // The pool's runner doesn't check whether a paired computer's Martlet is new enough for recordings; the Hearing list does.
        Assert.Equal(ThinkingCapability.Text | ThinkingCapability.Vision, ThinkingPoolCapabilities.For(Host("gemma4:e4b"), null));
        Assert.Equal(ThinkingCapability.Text | ThinkingCapability.Vision | ThinkingCapability.Audio | ThinkingCapability.Tools,
            ThinkingPoolCapabilities.For(Endpoint("gemma4:e4b"), null));
        Assert.False(SensePool.Refuses(SenseKind.Audio, Host("gemma4:e4b"), null));
    }

    [Fact]
    public void A_model_in_ollama_on_this_pc_is_the_same_as_an_endpoint_there()
    {
        var found = Tested(Ollama, "house-eyes", sees: true, hears: false);
        var local = ModelSenses.For(SetupRouteType.ChatCompletions, Ollama, "house-eyes", found);
        Assert.Equal(true, local.Known(SenseKind.Image));
        Assert.Equal(false, local.Known(SenseKind.Audio));
        Assert.Equal(local, ModelSenses.For(Endpoint("house-eyes"), found));
    }
}
