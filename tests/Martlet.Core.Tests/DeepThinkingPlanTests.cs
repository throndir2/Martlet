using Martlet.Core.Contracts;
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
    public void Each_place_runs_as_many_thinks_at_once_as_its_slots()
    {
        Assert.Equal(1, Host("diva").ThinksAtOnce);
        Assert.Equal(1, LocalOllama("gemma4:12b").ThinksAtOnce);
        Assert.Equal(DeepThinkingSettings.CloudThinksAtOnce,
            new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = OpenRouter, ModelId = "x-ai/grok-4.3" }.ThinksAtOnce);
        Assert.Equal(DeepThinkingSettings.CloudThinksAtOnce, new DeepThinkingSettings().ThinksAtOnce);
        var twin = Host("twin") with { HostRouteId = SelfHostSetup.DeepThinkingRouteId, Slots = 3 };
        twin.Validate();
        Assert.Equal(3, twin.ThinksAtOnce);
        Assert.Throws<ContractException>(() => (twin with { Slots = 0 }).Validate());
        Assert.Throws<ContractException>(() => (twin with { Slots = DeepThinkingSettings.MaxPlaces + 1 }).Validate());
        var directory = Path.Combine(Path.GetTempPath(), "martlet-deep-slots-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(Host("diva").WithPool([twin]).Save(directory));
            var loaded = DeepThinkingSettings.Load(directory);
            Assert.Equal([1, 3], loaded.Places.Select(p => p.ThinksAtOnce));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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

    [Fact]
    public void A_paired_computers_Deep_thinking_role_thinks_beside_its_own_Thinking()
    {
        var diva = Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva", "https://diva.local:9443");
        var role = Host("diva") with { ModelId = "qwen3-8b", HostRouteId = SelfHostSetup.DeepThinkingRouteId };
        role.Validate();
        Assert.True(role.OnHostRole);
        Assert.Equal(SelfHostSetup.DeepThinkingRouteId, role.HostRoute);
        Assert.Equal("diva's Thinking pool (qwen3-8b)", role.Describe());
        var beside = DeepThinkingPlan.For(role, [diva]);
        Assert.True(beside.Available);
        Assert.False(beside.ChecksFit);
        Assert.Contains("beside Thinking there", beside.Why, StringComparison.Ordinal);
        Assert.True(DeepThinkingPlan.For(role, [LocalThinking]).Available);
        // Without the role, the computer's Ollama is Thinking's own model: the plan says why pool jobs stay off it (replies
        // would wait) and to add the role or untick the computer.
        var ollama = DeepThinkingPlan.For(Host("diva"), [diva]);
        Assert.False(ollama.Available);
        Assert.Contains("also answers you with this model", ollama.Why, StringComparison.Ordinal);
        Assert.Contains("would make your replies wait", ollama.Why, StringComparison.Ordinal);
        Assert.Contains("add the Thinking pool role there", ollama.Why, StringComparison.Ordinal);
        Assert.Contains("untick In the pool", ollama.Why, StringComparison.Ordinal);
        Assert.Equal(SelfHostSetup.OllamaRouteId, Host("diva").HostRoute);
        Assert.False(Host("diva").OnHostRole);

        // Saved and read back; a route that isn't one of a computer's Ollama roles, or one on an endpoint, is refused.
        var folder = Directory.CreateTempSubdirectory("martlet-deep-").FullName;
        try
        {
            Assert.True(role.Save(folder));
            Assert.Equal((role, "loaded"), DeepThinkingSettings.Read(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => (role with { HostRouteId = "martlet.gateway.f5-synthesis.v1" }).Validate());
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() =>
            (LocalOllama("gemma4:12b") with { HostRouteId = SelfHostSetup.DeepThinkingRouteId }).Validate());
    }

    private static DeepThinkingSettings Role(string hostId) => Host(hostId) with { HostRouteId = SelfHostSetup.DeepThinkingRouteId };

    [Fact]
    public void A_file_saved_before_the_pool_reads_as_one_place_and_a_pool_round_trips()
    {
        var folder = Directory.CreateTempSubdirectory("martlet-deep-").FullName;
        try
        {
            // deep-thinking.json as one place was saved before Think here too existed: it reads unchanged, with no pool.
            File.WriteAllText(Path.Combine(folder, DeepThinkingSettings.FileName),
                """{"Place":"Endpoint","Origin":"https://openrouter.ai/api/v1","ModelId":"x-ai/grok-4.3","ChosenAt":"2026-09-01T10:00:00+00:00"}""");
            var (old, state) = DeepThinkingSettings.Read(folder);
            Assert.Equal("loaded", state);
            Assert.Equal(DeepThinkingPlace.Endpoint, old.Place);
            Assert.Equal("x-ai/grok-4.3", old.ModelId);
            Assert.Null(old.Pool);
            Assert.Single(old.Places);

            var pooled = old.WithPool([Role("diva"), Role("ripley"), Role("diva"), new DeepThinkingSettings()]);
            Assert.Equal([$"endpoint:{OpenRouter}|x-ai/grok-4.3", "host:diva", "host:ripley"], pooled.Places.Select(p => p.Key));
            Assert.True(pooled.Save(folder));
            var (read, readState) = DeepThinkingSettings.Read(folder);
            Assert.Equal("loaded", readState);
            Assert.Equal(pooled.Places.Select(p => p.Key), read.Places.Select(p => p.Key));
            Assert.Equal("openrouter.ai (x-ai/grok-4.3), diva's Thinking pool (gemma4:27b) and ripley's Thinking pool (gemma4:27b)", read.DescribeAll());
        }
        finally { Directory.Delete(folder, recursive: true); }
        // The same computer twice, a nested pool or Same as Thinking among the others is refused.
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => (Role("diva") with { Pool = [Role("diva")] }).Validate());
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => (Role("diva") with { Pool = [Role("ripley") with { Pool = [Role("imouto")] }] }).Validate());
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => (Role("diva") with { Pool = [new DeepThinkingSettings()] }).Validate());
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() =>
            (Role("a") with { Pool = [.. Enumerable.Range(0, DeepThinkingSettings.MaxPlaces).Select(i => Role($"h{i}"))] }).Validate());
    }

    [Fact]
    public void A_pool_thinks_on_every_usable_place_and_ranks_what_shares_least_with_the_conversation_first()
    {
        var divaThinks = Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva", "https://diva.local:9443");
        var imoutoSpeaks = Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "imouto", "https://imouto.local:9443");
        // diva's Ollama does Thinking (it can't think there without its role); ripley is free; imouto speaks.
        var deep = Host("diva").WithPool([Role("imouto"), Role("ripley"), LocalOllama("gemma4:12b")]);
        var pool = DeepThinkingPool.For(deep, [divaThinks, imoutoSpeaks]);
        Assert.Equal(["diva", "imouto", "ripley", "this PC"], pool.Spots.Select(s => s.Computer));
        Assert.Equal(["imouto", "ripley", "this PC"], pool.Usable.Select(s => s.Computer));
        Assert.Equal([1, 0, 1], pool.Usable.Select(s => s.Plan.Rank));
        Assert.True(pool.Plan.Available);
        // Three slots in all: two thinks at once, because the last free slot stays free for quick jobs.
        Assert.StartsWith("Up to 2 thinks run at once alongside the conversation on its places (imouto, ripley and this PC)", pool.Plan.Why,
            StringComparison.Ordinal);
        Assert.EndsWith("The last free slot stays free for quick jobs (judges and summaries).", pool.Plan.Why, StringComparison.Ordinal);
        Assert.Equal("ripley", pool.Find("host:ripley")!.Computer);
        // One place that can't think is the whole story when it is the only one.
        var only = DeepThinkingPool.For(Host("diva"), [divaThinks]);
        Assert.False(only.Plan.Available);
        Assert.Equal(DeepThinkingPlan.For(Host("diva"), [divaThinks]), only.Plan);
        // Same as Thinking on a cloud provider shares Thinking's provider; a second model beside Thinking's on this PC ranks last.
        Assert.Equal(2, DeepThinkingPlan.For(new(), [CloudThinking]).Rank);
        Assert.Equal(3, DeepThinkingPlan.For(LocalOllama("gemma4:12b"), [LocalThinking]).Rank);
        Assert.Equal("openrouter.ai", new DeepThinkingSettings().Computer(CloudThinking));
        Assert.Equal("this PC", new DeepThinkingSettings().Computer(LocalThinking));
        Assert.Equal("diva", new DeepThinkingSettings().Computer(divaThinks));
    }

    [Fact]
    public void A_pool_runs_one_think_fewer_than_its_slots_at_once_and_keeps_the_last_for_quick_jobs()
    {
        Assert.Equal(new[] { 0, 1, 1, 2, 3, 8, 8 }, new[] { 0, 1, 2, 3, 4, 9, 64 }.Select(DeepThinkingPool.AtOnce));
        // Two computers with one slot each: one think at a time, and the other slot stays free for quick jobs.
        var two = DeepThinkingPool.For(Role("diva").WithPool([Role("ripley")]), [LocalThinking]);
        Assert.StartsWith("One think runs at a time alongside the conversation on its places (diva and ripley)", two.Plan.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_place_on_an_offline_computer_is_left_out_until_it_answers_and_the_plan_says_so()
    {
        var deep = Role("diva").WithPool([Role("ripley") with { Slots = 2 }, Role("imouto"), LocalOllama("gemma4:12b")]);
        var all = DeepThinkingPool.For(deep, [CloudThinking]);
        Assert.StartsWith("Up to 4 thinks run at once", all.Plan.Why, StringComparison.Ordinal);

        // ripley is offline: its two slots leave the pool, the others go on; a place on this PC never counts as offline.
        var some = DeepThinkingPool.For(deep, [CloudThinking], offline: ["ripley", "this PC"]);
        Assert.Equal(["diva", "imouto", "this PC"], some.Usable.Select(s => s.Computer));
        Assert.StartsWith("Up to 2 thinks run at once alongside the conversation on its places (diva, imouto and this PC)", some.Plan.Why,
            StringComparison.Ordinal);
        Assert.Equal(DeepThinkingPlan.Away("ripley"), some.Find("host:ripley")!.Plan);

        // Every computer that would run is offline: one says so by name, several together.
        var hosts = Role("diva").WithPool([Role("ripley")]);
        Assert.Equal("diva is offline; its slots come back when it answers again.",
            DeepThinkingPool.For(Role("diva"), [CloudThinking], offline: ["diva"]).Plan.Why);
        var none = DeepThinkingPool.For(hosts, [CloudThinking], offline: ["diva", "ripley"]).Plan;
        Assert.Equal(new DeepThinkingPlan(false, "Every Thinking pool computer is offline (diva and ripley); their slots come back when they answer again.")
            { Offline = true }, none);
        // Devices › Sharing work's reason wins over offline: it is the one the owner can act on.
        var never = new Martlet.Core.Cluster.WorkSharingSettings().With(new Martlet.Core.Cluster.WorkSharingJob
        {
            Job = Martlet.Core.Cluster.WorkSharingJobs.DeepThinking, Never = ["diva"]
        });
        var kept = DeepThinkingPool.For(Role("diva"), [CloudThinking], never, offline: ["diva"]).Plan;
        Assert.False(kept.Offline);
        Assert.Contains("never uses diva", kept.Why, StringComparison.Ordinal);
    }
}
