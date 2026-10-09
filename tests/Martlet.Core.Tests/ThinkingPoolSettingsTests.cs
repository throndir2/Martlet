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
    public void A_computer_taken_out_is_kept_out_across_a_save_and_ticking_it_again_clears_that()
    {
        var pool = new ThinkingPoolSettings().Add(Role("diva")).Add(Role("ripley")).WithAnswers("host:diva", true).TakeOut("diva");
        Assert.Equal(["host:ripley"], pool.Members.Select(m => m.Key));
        Assert.Empty(pool.AnswersForConversation);
        Assert.True(pool.Left("diva"));
        Assert.False(pool.Left("ripley"));
        // Remove alone (other callers) takes a member out without keeping its computer out.
        Assert.False(pool.Remove("host:ripley").Left("ripley"));

        Assert.True(pool.Save(directory));
        var (loaded, state) = ThinkingPoolSettings.Read(directory);
        Assert.Equal("loaded", state);
        Assert.Equal(["diva"], loaded.LeftByOwner);
        Assert.Contains("\"LeftByOwner\"", File.ReadAllText(Path.Combine(directory, ThinkingPoolSettings.FileName)), StringComparison.Ordinal);

        var back = loaded.KeepOut("diva", false);
        Assert.Empty(back.LeftByOwner);
        Assert.Equal(["diva"], back.KeepOut("diva", true).KeepOut("diva", true).LeftByOwner);
    }

    [Fact]
    public void Quick_and_long_jobs_are_ticked_per_member_saved_and_cleared_when_it_leaves()
    {
        var pool = new ThinkingPoolSettings().Add(Role("diva")).Add(Role("ripley"));
        Assert.True(pool.TakesQuickJobs("host:diva") && pool.TakesLongJobs("host:diva"));
        Assert.Same(pool, pool.ForLongJobs());

        pool = pool.WithJobs("host:diva", quick: false).WithJobs("host:ripley", @long: false).WithJobs("host:ripley", @long: false);
        Assert.False(pool.TakesQuickJobs("host:diva"));
        Assert.True(pool.TakesLongJobs("host:diva"));
        Assert.True(pool.TakesQuickJobs("host:ripley"));
        Assert.Equal(["host:ripley"], pool.NoLongJobs);
        // Long jobs (thinking longer, research, lyrics) see only the members that take them.
        Assert.Equal(["host:diva"], pool.ForLongJobs().Members.Select(m => m.Key));
        Assert.Equal(2, pool.Members.Count);
        Assert.Empty(pool.WithJobs("host:ripley", @long: true).NoLongJobs);

        Assert.True(pool.Save(directory));
        var (loaded, state) = ThinkingPoolSettings.Read(directory);
        Assert.Equal("loaded", state);
        Assert.Equal(["host:diva"], loaded.NoQuickJobs);
        Assert.Equal(["host:ripley"], loaded.NoLongJobs);

        var left = loaded.Remove("host:diva").TakeOut("ripley");
        Assert.Empty(left.NoQuickJobs);
        Assert.Empty(left.NoLongJobs);
        Assert.Throws<ContractException>(() => new ThinkingPoolSettings { NoLongJobs = ["bad\nkey"] }.Validate());
    }

    [Fact]
    public void An_older_file_without_the_job_lists_reads_as_every_member_taking_every_job()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ThinkingPoolSettings.FileName),
            """{ "SchemaVersion": 1, "Members": [], "UseConversationModelWhenEmpty": true, "NoQuickJobs": null }""");
        var (loaded, state) = ThinkingPoolSettings.Read(directory);
        Assert.Equal("loaded", state);
        Assert.Empty(loaded.NoQuickJobs);
        Assert.Empty(loaded.NoLongJobs);
        Assert.True(loaded.TakesQuickJobs("host:diva") && loaded.TakesLongJobs("host:diva"));
    }

    [Fact]
    public void Priority_and_retry_choices_have_defaults_and_ranges()
    {
        var pool = new ThinkingPoolSettings();
        Assert.True(pool.PreemptLowerPriority);
        Assert.Equal(3, pool.RaisePriorityAfterStops);
        Assert.Equal(1, pool.RetriesOnFailure);
        pool.Validate();
        new ThinkingPoolSettings { PreemptLowerPriority = false, RaisePriorityAfterStops = 1, RetriesOnFailure = 0 }.Validate();
        new ThinkingPoolSettings { RaisePriorityAfterStops = 20, RetriesOnFailure = 10 }.Validate();
        Assert.Throws<ContractException>(() => new ThinkingPoolSettings { RaisePriorityAfterStops = 0 }.Validate());
        Assert.Throws<ContractException>(() => new ThinkingPoolSettings { RaisePriorityAfterStops = 21 }.Validate());
        Assert.Throws<ContractException>(() => new ThinkingPoolSettings { RetriesOnFailure = -1 }.Validate());
        Assert.Throws<ContractException>(() => new ThinkingPoolSettings { RetriesOnFailure = 11 }.Validate());
    }

    [Fact]
    public void Priority_and_retry_choices_are_saved_and_an_older_file_reads_as_defaults()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ThinkingPoolSettings.FileName),
            """{ "SchemaVersion": 1, "Members": [], "UseConversationModelWhenEmpty": true }""");
        var (loaded, state) = ThinkingPoolSettings.Read(directory);
        Assert.Equal("loaded", state);
        Assert.True(loaded.PreemptLowerPriority);
        Assert.Equal(ThinkingPoolSettings.DefaultRaiseAfterStops, loaded.RaisePriorityAfterStops);
        Assert.Equal(ThinkingPoolSettings.DefaultRetries, loaded.RetriesOnFailure);

        Assert.True((loaded with { PreemptLowerPriority = false, RaisePriorityAfterStops = 7, RetriesOnFailure = 4 }).Save(directory));
        var saved = ThinkingPoolSettings.Load(directory);
        Assert.False(saved.PreemptLowerPriority);
        Assert.Equal(7, saved.RaisePriorityAfterStops);
        Assert.Equal(4, saved.RetriesOnFailure);
    }

    [Fact]
    public void An_older_file_without_the_kept_out_list_reads_as_empty()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ThinkingPoolSettings.FileName),
            """{ "SchemaVersion": 1, "Members": [], "UseConversationModelWhenEmpty": true, "AnswersForConversation": [] }""");
        var (loaded, state) = ThinkingPoolSettings.Read(directory);
        Assert.Equal("loaded", state);
        Assert.Empty(loaded.LeftByOwner);
        Assert.False(loaded.Left("diva"));

        File.WriteAllText(Path.Combine(directory, ThinkingPoolSettings.FileName), """{ "SchemaVersion": 1, "LeftByOwner": null }""");
        Assert.Empty(ThinkingPoolSettings.Read(directory).Settings.LeftByOwner);
    }

    [Fact]
    public void The_kept_out_list_is_bounded()
    {
        var many = Enumerable.Range(0, ThinkingPoolSettings.MaxLeftByOwner + 5)
            .Aggregate(new ThinkingPoolSettings(), (pool, i) => pool.KeepOut("gpu-" + i, true));
        Assert.Equal(ThinkingPoolSettings.MaxLeftByOwner, many.LeftByOwner.Count);
        Assert.True(many.Left("gpu-" + (ThinkingPoolSettings.MaxLeftByOwner + 4)));
        Assert.False(many.Left("gpu-0"));
        many.Validate();
        Assert.Throws<ContractException>(() => new ThinkingPoolSettings { LeftByOwner = ["diva", "diva"] }.Validate());
        Assert.Throws<ContractException>(() => new ThinkingPoolSettings { LeftByOwner = [""] }.Validate());
        Assert.Throws<ContractException>(() => new ThinkingPoolSettings { LeftByOwner = ["bad\nname"] }.Validate());
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

    [Fact]
    public void A_member_whose_computer_is_offline_leaves_the_usable_places_and_its_slots_with_it()
    {
        var routes = new[] { Chat(SetupRole.Llm, OpenRouter, "x-ai/grok-4.3") };
        var settings = new ThinkingPoolSettings().Add(Role("diva") with { Slots = 2 }).Add(Role("ripley"));
        var all = settings.Plan(routes);
        Assert.Equal(3, all.Usable.Sum(s => s.Settings.ThinksAtOnce));

        var live = settings.Plan(routes, offline: ["diva"]);
        var diva = live.Find("host:diva")!;
        Assert.False(diva.Plan.Available);
        Assert.True(diva.Plan.Offline);
        Assert.Equal("diva is offline; its slots come back when it answers again.", diva.Plan.Why);
        Assert.Equal(["host:ripley"], live.Usable.Select(s => s.Key));
        Assert.Equal(1, live.Usable.Sum(s => s.Settings.ThinksAtOnce));
        Assert.True(live.Plan.Available);
        // A computer that isn't a member changes nothing, and the warnings say why diva can't run now.
        Assert.Equal(all.Usable.Select(s => s.Key), settings.Plan(routes, offline: ["imouto"]).Usable.Select(s => s.Key));
        Assert.Contains(ThinkingPoolWarnings.For(live, routes), w => w.Contains("diva is offline", StringComparison.Ordinal));
    }

    [Fact]
    public void With_every_member_offline_the_conversation_model_stands_in_only_when_allowed_and_able()
    {
        var cloud = new[] { Chat(SetupRole.Llm, OpenRouter, "x-ai/grok-4.3") };
        var settings = new ThinkingPoolSettings().Add(Role("diva")).Add(Role("ripley"));
        var live = settings.Plan(cloud, offline: ["diva", "ripley"]);
        // As with an empty pool: thinking longer and research use the conversation model (its provider thinks in parallel).
        Assert.True(live.Plan.Available);
        var standIn = Assert.Single(live.Usable);
        Assert.False(standIn.Settings.Separate);
        Assert.StartsWith("Every Thinking pool computer is offline (diva and ripley)", live.Plan.Why, StringComparison.Ordinal);
        Assert.Contains("use the conversation model", live.Plan.Why, StringComparison.Ordinal);
        // The offline members stay in the plan, so status and warnings still name them.
        Assert.All(live.Spots.Where(s => s.Settings.Separate), s => Assert.True(s.Plan.Offline));

        // Not allowed: it says clearly that every pool computer is offline.
        var off = (settings with { UseConversationModelWhenEmpty = false }).Plan(cloud, offline: ["diva", "ripley"]);
        Assert.False(off.Plan.Available);
        Assert.True(off.Plan.Offline);
        Assert.Equal("Every Thinking pool computer is offline (diva and ripley); their slots come back when they answer again.", off.Plan.Why);

        // Allowed, but the conversation model runs on this PC and can't think in parallel: the pool's own reason stands.
        var local = settings.Plan([Chat(SetupRole.Llm, Ollama, "gemma4:e4b")], offline: ["diva", "ripley"]);
        Assert.False(local.Plan.Available);
        Assert.StartsWith("Every Thinking pool computer is offline", local.Plan.Why, StringComparison.Ordinal);
        Assert.All(local.Spots, s => Assert.True(s.Settings.Separate));
    }

    [Fact]
    public void An_offline_member_that_couldnt_run_anyway_keeps_its_own_reason()
    {
        // diva also does Thinking for the conversation with its Ollama role, so it can't think something over there.
        var routes = new[] { Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva") };
        var plain = Role("diva") with { HostRouteId = null };
        var live = new ThinkingPoolSettings().Add(plain).Plan(routes, offline: ["diva"]);
        Assert.False(live.Plan.Offline);
        Assert.Contains("also does Thinking", live.Plan.Why, StringComparison.Ordinal);
    }
}
