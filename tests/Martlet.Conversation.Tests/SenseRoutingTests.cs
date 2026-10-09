using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

// Which model takes pictures and recordings (docs/SENSE_MODELS.md), by fixture model names: nothing is sent.
public sealed class SenseRoutingTests
{
    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl;

    private static SetupRoute Chat(string model, string origin = Ollama) => new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin,
        ModelId = model, ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static DeepThinkingSettings Endpoint(string model, string origin = Ollama) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    private static DeepThinkingSettings Host(string model) => new()
    {
        Place = DeepThinkingPlace.Host, ModelId = model, HostId = "diva", HostOrigin = "https://diva.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid()
    };

    private static SenseModel Own(DeepThinkingSettings model) => new() { Source = SenseSource.Own, Own = model };
    private static readonly SenseModel SameAsOther = new() { Source = SenseSource.OtherSense };

    // Gemma 4 E2B sees and hears; Qwen3 8B is text-only; Qwen2.5-VL sees; Gemma 3n hears; Nemotron 3 Nano Omni does both.
    private static readonly SetupRoute Omni = Chat("gemma4:e2b"), TextOnly = Chat("qwen3:8b");
    private static readonly SenseModel Eyes = Own(Endpoint("qwen2.5vl:7b")), Ears = Own(Endpoint("gemma3n:e4b"));
    private static readonly SenseModel Both = Own(Endpoint("nvidia/nemotron-3-nano-omni-30b-a3b-reasoning", "https://integrate.api.nvidia.com/v1"));

    private static (SensePath Image, SensePath Audio) Paths(SenseModels senses, SetupRoute? thinking, ModelAbilities? abilities = null) =>
        (SenseRouting.For(SenseKind.Image, senses, thinking, abilities).Path, SenseRouting.For(SenseKind.Audio, senses, thinking, abilities).Path);

    [Fact]
    public void An_omni_text_model_takes_both_itself_by_default()
    {
        Assert.Equal((SensePath.Thinking, SensePath.Thinking), Paths(new(), Omni));
        var image = SenseRouting.For(SenseKind.Image, new(), Omni, null);
        Assert.Null(image.Model);
        Assert.Equal("the text model (Thinking)", image.Name);
        Assert.Contains("sees them itself", image.Why);
    }

    [Fact]
    public void A_text_only_model_alone_sees_nothing_and_gets_the_transcript()
    {
        Assert.Equal((SensePath.None, SensePath.None), Paths(new(), TextOnly));
        Assert.Contains("text-only", SenseRouting.For(SenseKind.Image, new(), TextOnly, null).Why);
        Assert.Contains("transcript only", SenseRouting.For(SenseKind.Audio, new(), TextOnly, null).Why);
        Assert.Equal((SensePath.None, SensePath.None), Paths(new(), null));
    }

    [Fact]
    public void The_same_model_for_text_and_audio_with_an_image_model_of_its_own_describes_only_pictures()
    {
        Assert.Equal((SensePath.Described, SensePath.Thinking), Paths(new() { Image = Eyes }, Omni));
        var image = SenseRouting.For(SenseKind.Image, new() { Image = Eyes }, Omni, null);
        Assert.Equal("qwen2.5vl:7b", image.Model!.ModelId);
        Assert.Contains("describes pictures in words for the text model", image.Why);
    }

    [Fact]
    public void A_text_only_model_with_its_own_image_and_audio_models_gets_both_described()
    {
        Assert.Equal((SensePath.Described, SensePath.Described), Paths(new() { Image = Eyes, Audio = Ears }, TextOnly));
        Assert.Equal((SensePath.Described, SensePath.Described), Paths(new() { Image = Both, Audio = SameAsOther }, TextOnly));
        Assert.Equal((SensePath.Described, SensePath.Described), Paths(new() { Image = SameAsOther, Audio = Both }, TextOnly));
    }

    [Fact]
    public void The_audio_model_the_same_as_an_image_model_that_cannot_hear_takes_nothing_and_says_why()
    {
        var audio = SenseRouting.For(SenseKind.Audio, new() { Image = Eyes, Audio = SameAsOther }, TextOnly, null);
        Assert.Equal(SensePath.None, audio.Path);
        Assert.Equal("qwen2.5vl:7b", audio.Model!.ModelId);
        Assert.Contains("can't hear recordings", audio.Why);
        var image = SenseRouting.For(SenseKind.Image, new() { Image = SameAsOther, Audio = Ears }, TextOnly, null);
        Assert.Equal(SensePath.None, image.Path);
        Assert.Contains("doesn't see pictures", image.Why);
    }

    [Fact]
    public void Same_as_the_other_kind_both_ways_and_a_model_that_is_thinkings_own_go_to_the_text_model()
    {
        Assert.Equal((SensePath.Thinking, SensePath.Thinking), Paths(new() { Image = SameAsOther, Audio = SameAsOther }, Omni));
        Assert.Equal((SensePath.Thinking, SensePath.Thinking), Paths(new() { Image = Own(Endpoint("gemma4:e2b")), Audio = SameAsOther }, Omni));
        var said = SenseRouting.For(SenseKind.Audio, new() { Audio = SameAsOther }, Omni, null);
        Assert.Contains("the image model, which is the text model", said.Why);
    }

    [Fact]
    public void A_paired_computers_model_describes_pictures_but_takes_no_recordings()
    {
        var senses = new SenseModels { Image = Own(Host("qwen2.5vl:7b")), Audio = SameAsOther };
        Assert.Equal((SensePath.Described, SensePath.None), Paths(senses, TextOnly));
        Assert.Contains("paired computer", SenseRouting.For(SenseKind.Audio, senses, TextOnly, null).Why);
        // What Martlet found out about it is kept by its gateway's origin, as for a Thinking route there.
        var refused = new ModelAbilities().With(new()
        {
            Origin = "https://diva.local:9443", ModelId = "qwen2.5vl:7b", Sees = false, Source = "a refused picture", CheckedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(SensePath.None, SenseRouting.For(SenseKind.Image, senses, TextOnly, refused).Path);
    }

    [Fact]
    public void The_same_paired_computer_and_model_as_thinking_is_the_text_model()
    {
        var thinking = new SetupRoute
        {
            RouteType = SetupRouteType.GatewayOllama, Role = SetupRole.Llm, ProviderAlias = SelfHostSetup.GatewayOllamaAlias,
            Origin = "https://diva.local:9443", ModelId = "gemma4-e4b", ConfigurationRevision = Guid.NewGuid(), Enabled = true,
            Gateway = new() { SchemaVersion = 1, Origin = "https://diva.local:9443", HostId = "diva", SpkiFingerprint = "sha256:" + new string('0', 64), DeviceRole = SelfHostSetup.GatewayRole }
        };
        Assert.True(SenseRouting.IsThinking(Host("gemma4-e4b"), thinking));
        Assert.False(SenseRouting.IsThinking(Host("qwen2.5vl:7b"), thinking));
        Assert.False(SenseRouting.IsThinking(Host("gemma4-e4b") with { HostRouteId = SelfHostSetup.DeepThinkingRouteId }, thinking));
        Assert.Equal(SensePath.Thinking, SenseRouting.For(SenseKind.Image, new() { Image = Own(Host("gemma4-e4b")) }, thinking, null).Path);
    }

    [Fact]
    public void Thinking_on_a_paired_computer_hears_when_its_model_does()
    {
        SetupRoute OnHost(string model) => new()
        {
            RouteType = SetupRouteType.GatewayOllama, Role = SetupRole.Llm, ProviderAlias = SelfHostSetup.GatewayOllamaAlias,
            Origin = "https://miku-host.local:9443", ModelId = model, ConfigurationRevision = Guid.NewGuid(), Enabled = true,
            Gateway = new() { SchemaVersion = 1, Origin = "https://miku-host.local:9443", HostId = "miku-host", SpkiFingerprint = "sha256:" + new string('0', 64), DeviceRole = SelfHostSetup.GatewayRole }
        };
        Assert.Equal(HearingSupport.Supported, SenseRouting.ThinkingHears(OnHost("gemma4-e4b"), null));
        Assert.Equal(SensePath.Thinking, SenseRouting.For(SenseKind.Audio, new(), OnHost("gemma4-e4b"), null).Path);
        Assert.Equal(SensePath.None, SenseRouting.For(SenseKind.Audio, new(), OnHost("qwen3:8b"), null).Path);
        // A refused recording there is remembered by the computer's gateway origin.
        var refused = new ModelAbilities().With(new()
        {
            Origin = "https://miku-host.local:9443", ModelId = "gemma4-e4b", Hears = false, Source = "a refused recording", CheckedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(SensePath.None, SenseRouting.For(SenseKind.Audio, new(), OnHost("gemma4-e4b"), refused).Path);
    }

    [Fact]
    public void What_martlet_found_out_wins_over_names_and_an_unknown_model_is_tried()
    {
        var abilities = new ModelAbilities()
            .With(new() { Origin = Ollama, ModelId = "qwen2.5vl:7b", Sees = false, Source = "a refused picture", CheckedAt = DateTimeOffset.UtcNow })
            .With(new() { Origin = Ollama, ModelId = "my-own-model", Hears = true, Source = "a test request", CheckedAt = DateTimeOffset.UtcNow })
            .With(new() { Origin = Ollama, ModelId = "qwen3:8b", Hears = true, Sees = true, Source = "Ollama on this PC", CheckedAt = DateTimeOffset.UtcNow });
        Assert.Equal(SensePath.None, SenseRouting.For(SenseKind.Image, new() { Image = Eyes }, TextOnly, abilities).Path);
        Assert.Equal(SensePath.Described, SenseRouting.For(SenseKind.Audio, new() { Audio = Own(Endpoint("my-own-model")) }, TextOnly, abilities).Path);
        // Thinking's own abilities come from model-abilities.json too.
        Assert.Equal((SensePath.Thinking, SensePath.Thinking), Paths(new(), TextOnly, abilities));

        var unknown = SenseRouting.For(SenseKind.Image, new() { Image = Own(Endpoint("my-vlm")) }, TextOnly, null);
        Assert.Equal(SensePath.Described, unknown.Path);
        Assert.True(unknown.Unknown);
        var thinking = SenseRouting.For(SenseKind.Image, new(), Chat("my-own-model"), null);
        Assert.Equal(SensePath.Thinking, thinking.Path);
        Assert.True(thinking.Unknown);
        // Hearing that isn't known sends only the transcript: a recording is never tried on a guess.
        Assert.Equal(SensePath.None, SenseRouting.For(SenseKind.Audio, new(), Chat("my-own-model"), null).Path);
    }

    [Fact]
    public void The_desktops_own_answer_for_thinking_decides_its_path()
    {
        var senses = new SenseModels();
        Assert.Equal(SensePath.None, SenseRouting.For(SenseKind.Image, senses, Omni, null, VisionSupport.Unsupported, HearingSupport.Supported).Path);
        Assert.Equal(SensePath.Thinking, SenseRouting.For(SenseKind.Audio, senses, Omni, null, VisionSupport.Unsupported, HearingSupport.Supported).Path);
    }
}
