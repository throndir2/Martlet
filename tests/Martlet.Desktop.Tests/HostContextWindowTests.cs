using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.HostClient;

namespace Martlet.Desktop.Tests;

public sealed class HostContextWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private const string Rtx3090 = "GPU-3090aaaa-0000-0000-0000-000000000001";
    private const string Rtx3080 = "GPU-3080bbbb-0000-0000-0000-000000000002";
    private const string Rtx5060 = "GPU-5060cccc-0000-0000-0000-000000000003";

    // MainWindow's statics include pack:// resources: register the scheme as a WPF app would before using them.
    public HostContextWindowTests() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    private static HostRoute Route(string routeId, string model, params string[] gpus) => new(routeId, "/p", "c", "1.0", "d", "w", "1", model, "r",
        new string('a', 64), new string('b', 64), 1, 1, 1, 1, 1, 1, TimeSpan.FromSeconds(30), "request_abort") { Gpus = gpus };

    // Martruby's host: Gemma 4 12B for Thinking on an RTX 3090, the F5 voice on an RTX 3080 and the Thinking pool on a 5060 Ti.
    private static HostHardware Hardware(bool uuids) => new("linux-box", "https://192.168.1.30:9443", Now, Now, "docker", "Ubuntu", null, null,
        null, null, null, "yes",
        [
            new HostGpu("NVIDIA GeForce RTX 3090", "nvidia", 24_576, "570") { Uuid = uuids ? Rtx3090 : null },
            new HostGpu("NVIDIA GeForce RTX 3080", "nvidia", 10_240, "570") { Uuid = uuids ? Rtx3080 : null },
            new HostGpu("NVIDIA GeForce RTX 5060 Ti", "nvidia", 16_384, "570") { Uuid = uuids ? Rtx5060 : null }
        ]);

    private static readonly HostRoute Thinking = Route(SelfHostSetup.OllamaRouteId, "gemma4:12b", Rtx3090);
    private static readonly HostRoute[] Routes =
    [
        Thinking, Route(SelfHostSetup.F5RouteId, "f5tts-v1-base", Rtx3080), Route(SelfHostSetup.DeepThinkingRouteIdFor(2), "gemma4:12b", Rtx5060)
    ];

    [Fact]
    public void The_3090_that_runs_only_thinking_gets_the_largest_window()
    {
        var plan = HostContextWindow.Plan(Hardware(uuids: true), Thinking, Routes, null);

        Assert.Equal(GenerationSettings.PlannedContextTokens, plan.Tokens);
        Assert.Contains("the NVIDIA GeForce RTX 3090's 24 GB", plan.Why, StringComparison.Ordinal);
        Assert.DoesNotContain("F5", plan.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_that_doesnt_name_its_cards_plans_for_the_smallest_card_that_holds_the_model()
    {
        // Without the cards' UUIDs Martlet can't tell which card runs the model: it errs small and keeps the usual window.
        Assert.Equal(GenerationSettings.DefaultHostContextTokens, HostContextWindow.Plan(Hardware(uuids: false), Thinking, Routes, null).Tokens);
        // A host with one card needs no UUID.
        var one = Hardware(uuids: false) with { Gpus = [new HostGpu("NVIDIA GeForce RTX 3090", "nvidia", 24_576, "570")] };
        Assert.Equal(GenerationSettings.PlannedContextTokens, HostContextWindow.Plan(one, Route(SelfHostSetup.OllamaRouteId, "gemma4:12b"), [], null).Tokens);
        Assert.Equal(GenerationSettings.DefaultHostContextTokens, HostContextWindow.Plan(null, Thinking, Routes, null).Tokens);
    }

    [Fact]
    public void Another_model_app_on_the_host_takes_its_room_first()
    {
        // llama.cpp beside Martlet's Ollama holds 14 GB of the 3090: the conversation model gets the usual window, not 32,768.
        var shared = Hardware(uuids: true) with
        {
            Served = [new HostServedModel("llama-cpp", "llama.cpp", 8080, "/v1", "qwen3-14b", 9_000_000_000, 14_000_000_000)]
        };

        var plan = HostContextWindow.Plan(shared, Thinking, Routes, null);

        Assert.Equal(GenerationSettings.DefaultHostContextTokens, plan.Tokens);
        Assert.Contains("llama.cpp's qwen3-14b", plan.Why, StringComparison.Ordinal);
        // A model the app serves but hasn't loaded holds nothing, and the route's own model (served by that app too) isn't counted twice.
        Assert.Empty(HostContextWindow.OtherApps(Hardware(uuids: true) with
        {
            Served = [new HostServedModel("llama-cpp", "llama.cpp", 8080, "/v1", "qwen3-14b", 9_000_000_000),
                new HostServedModel("lm-studio", "LM Studio", 1234, "/v1", "gemma4-12b", 9_000_000_000, 9_000_000_000)]
        }, "gemma4:12b"));
    }

    [Fact]
    public void Roles_on_the_same_card_take_their_room_first()
    {
        var crowded = Hardware(uuids: true) with { Gpus = [new HostGpu("NVIDIA GeForce RTX 3060", "nvidia", 12_288, "570") { Uuid = Rtx3090 }] };
        HostRoute[] routes = [Thinking, Route(SelfHostSetup.F5RouteId, "f5tts-v1-base", Rtx3090)];

        var plan = HostContextWindow.Plan(crowded, Thinking, routes, null);

        Assert.Equal(GenerationSettings.DefaultHostContextTokens, plan.Tokens);
        Assert.Contains("the F5 voice", plan.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_thinking_pool_member_on_the_conversation_model_fits_its_one_window()
    {
        var member = new DeepThinkingSettings
        {
            Place = DeepThinkingPlace.Host, ModelId = "gemma4:12b", HostId = "linux-box", HostOrigin = "https://192.168.1.30:9443",
            HostSpkiFingerprint = "sha256:" + new string('a', 64), HostDeviceId = "desktop-test", HostCredentialId = Guid.NewGuid(),
            ChosenAt = Now
        };
        Assert.Equal(SelfHostSetup.OllamaRouteId, member.HostRoute);
        // Without a planned window it keeps the Thinking pool's own 32,768 tokens, as before.
        Assert.Equal(GenerationSettings.DeepThinkingContextTokens, DeepThinkTarget.For(member, ThinkEffort.Medium, null, null).Input.MaxContextTokens);
        // With the conversation model's planned window, a think's input fits that window less its output.
        var limits = new ModelLimits().With(new()
        {
            Origin = member.HostOrigin!, ModelId = "gemma4:12b", ContextTokens = 16_384, Source = "test", CheckedAt = Now
        });
        var target = DeepThinkTarget.For(member, ThinkEffort.Medium, null, limits);
        Assert.Equal(16_384, target.Input.MaxContextTokens);
        Assert.Equal(16_384 - 8_192, target.Bounds.MaxInputTokens);
    }

    [Fact]
    public void The_replies_page_says_the_planned_and_the_loaded_window_and_what_a_reply_sends()
    {
        var route = new SetupRoute
        {
            RouteType = SetupRouteType.GatewayOllama, Role = SetupRole.Llm, ProviderAlias = SelfHostSetup.GatewayOllamaAlias,
            Origin = "https://192.168.1.30:9443", ModelId = "gemma4:12b", ConfigurationRevision = Guid.NewGuid(), Enabled = true
        };
        var plan = HostContextWindow.Plan(Hardware(uuids: true), Thinking, Routes, null);
        var limits = new ModelLimits().With(new()
        {
            Origin = route.Origin, ModelId = route.ModelId, ContextTokens = plan.Tokens, Source = HostContextWindow.SourcePrefix + plan.Why, CheckedAt = Now
        });

        var text = MainWindow.ContextStatus(route, null, limits, "linux-box", loaded: 8_192);

        Assert.StartsWith("Martlet uses 32,768 tokens (planned from the graphics card's free memory); linux-box loads this window for every request " +
            "(32,768 tokens: gemma4:12b", text, StringComparison.Ordinal);
        Assert.Contains("it has 8,192 tokens loaded now", text, StringComparison.Ordinal);
        Assert.Contains("Each reply sends at most about 6,144 tokens", text, StringComparison.Ordinal);
        // Before Martlet has planned it, the old window and the same input.
        Assert.Contains("Martlet uses 8,192 tokens", MainWindow.ContextStatus(route, null, new(), "linux-box"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_loaded_is_the_planned_one_or_the_saved_size_when_larger()
    {
        Assert.Equal(32_768, HostContextWindow.Window(32_768, null));
        Assert.Equal(32_768, HostContextWindow.Window(32_768, 16_384));
        Assert.Equal(65_536, HostContextWindow.Window(32_768, 65_536));
        Assert.Equal(GenerationSettings.MaximumHostContextTokens, HostContextWindow.Window(8_192, 1_000_000));
    }
}
