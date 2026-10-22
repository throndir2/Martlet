using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class GenerationSettingsTests
{
    [Fact]
    public void Saved_generation_settings_round_trip_and_defaults_stay_absent()
    {
        var settings = CompanionSettings.Begin(null);
        Assert.DoesNotContain("\"generation\"", Encoding.UTF8.GetString(ContractJson.Write(settings)));

        var tuned = settings with
        {
            Generation = new() { Temperature = 1.1, TopP = 0.9, TopK = 40, RepeatPenalty = 1.15, MaxReplyTokens = 512, ContextTokens = 16_384 }
        };
        var json = Encoding.UTF8.GetString(ContractJson.Write(tuned));
        Assert.Contains("\"max_reply_tokens\"", json);
        Assert.DoesNotContain("\"min_p\"", json);
        var read = SettingsJson.Read(Encoding.UTF8.GetBytes(json));
        Assert.Equal(tuned.Generation, read.Generation);
        Assert.Equal(512, read.Generation!.ReplyTokens);

        Assert.Null(GenerationSettings.Normalize(new GenerationSettings()));
        Assert.Throws<ContractException>(() => (settings with { Generation = new GenerationSettings() }).Validate());
    }

    [Fact]
    public void Old_deep_thinking_time_and_hourly_limits_still_load_and_are_dropped()
    {
        // Older versions saved a time limit (2, 5 or 10 minutes) and an hourly limit (3, 6 or 12); a think has neither now.
        var old = CompanionSettings.Begin(null) with
        {
            Generation = new() { ThinkLonger = new() { Minutes = 10, PerHour = 3, Effort = ThinkEffort.High } }
        };
        var json = Encoding.UTF8.GetString(ContractJson.Write(old));
        var read = SettingsJson.Read(Encoding.UTF8.GetBytes(json));
        read.Generation!.ThinkLonger!.Validate();
        var normalized = ThinkLongerSettings.Normalize(read.Generation.ThinkLonger);
        Assert.Equal(new ThinkLongerSettings { Effort = ThinkEffort.High }, normalized);
        Assert.Null(ThinkLongerSettings.Normalize(new ThinkLongerSettings { Minutes = 7, PerHour = 100 }));
    }

    [Fact]
    public void Web_research_is_on_unless_saved_off_and_an_explicit_off_survives_a_save()
    {
        // Settings saved without a web_research value (never touched) now research; turning it off saves false, which loads off.
        var untouched = CompanionSettings.Begin(null) with { Generation = new() { ThinkLonger = new() { Effort = ThinkEffort.High } } };
        var untouchedJson = Encoding.UTF8.GetString(ContractJson.Write(untouched));
        Assert.DoesNotContain("web_research", untouchedJson);
        Assert.True(ThinkLongerSettings.Of(SettingsJson.Read(Encoding.UTF8.GetBytes(untouchedJson)).Generation).Researches);

        var turnedOff = untouched with
        {
            Generation = GenerationSettings.Normalize(untouched.Generation! with
            {
                ThinkLonger = ThinkLongerSettings.Normalize(untouched.Generation!.ThinkLonger! with { WebResearch = false })
            })
        };
        var offJson = Encoding.UTF8.GetString(ContractJson.Write(turnedOff));
        Assert.Matches("\"web_research\":\\s*false", offJson);
        var read = ThinkLongerSettings.Of(SettingsJson.Read(Encoding.UTF8.GetBytes(offJson)).Generation);
        Assert.False(read.WebResearchOn);
        Assert.False(read.Researches);

        // Turning it back on drops the value again, so the saved settings stay lean.
        Assert.Equal(new ThinkLongerSettings { Effort = ThinkEffort.High }, ThinkLongerSettings.Normalize(read with { WebResearch = true }));
        Assert.Null(ThinkLongerSettings.Normalize(new ThinkLongerSettings { WebResearch = true }));
    }

    [Theory]
    [InlineData(null, 0.0, null, null)]
    [InlineData(null, null, 8, null)]
    [InlineData(null, null, 2048, 2048)]
    [InlineData(null, null, GenerationSettings.MaximumReplyTokens + 1, null)]
    [InlineData(null, null, GenerationSettings.MaximumReplyTokens, GenerationSettings.MaximumReplyTokens)]
    public void Out_of_range_values_are_rejected(double? temperature, double? topP, int? replyTokens, int? contextTokens) =>
        Assert.Throws<ContractException>(() => new GenerationSettings
        {
            Temperature = temperature, TopP = topP, MaxReplyTokens = replyTokens,
            ContextTokens = contextTokens
        }.Validate());

    [Fact]
    public void Max_reply_length_goes_up_to_the_providers_output_ceiling()
    {
        // Above every route's default (4,096 on Chat Completions and paired hosts), so a reply can be made longer, not only shorter.
        Assert.Equal(32_768, GenerationSettings.MaximumReplyTokens);
        Assert.True(GenerationSettings.MaximumReplyTokens > GenerationSettings.ChatReplyTokens);
        new GenerationSettings { MaxReplyTokens = GenerationSettings.MaximumReplyTokens }.Validate();
        new GenerationSettings { MaxReplyTokens = GenerationSettings.MaximumReplyTokens, ContextTokens = 65_536 }.Validate();
        Assert.Equal(GenerationSettings.MaximumReplyTokens,
            GenerationSupport.ReplyTokens(SetupRouteType.GatewayOllama, new() { MaxReplyTokens = GenerationSettings.MaximumReplyTokens }));
    }

    [Fact]
    public void Each_route_marks_the_settings_its_api_cannot_use()
    {
        Assert.Equal(GenerationSettingUse.Used,
            GenerationSupport.Use(SetupRouteType.GatewayOllama, null, GenerationSetting.ContextTokens));
        Assert.Equal(GenerationSettingUse.Used, GenerationSupport.Use(SetupRouteType.OpenAi, null, GenerationSetting.TopP));
        Assert.Equal(GenerationSettingUse.Unused, GenerationSupport.Use(SetupRouteType.OpenAi, null, GenerationSetting.FrequencyPenalty));
        Assert.Equal(GenerationSettingUse.Used, GenerationSupport.Use(SetupRouteType.ChatCompletions,
            ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, GenerationSetting.TopK));
        Assert.Equal(GenerationSettingUse.ServerDependent, GenerationSupport.Use(SetupRouteType.ChatCompletions,
            "http://127.0.0.1:1234/v1", GenerationSetting.MinP));
        Assert.Equal(GenerationSettingUse.Unused, GenerationSupport.Use(SetupRouteType.ChatCompletions,
            GenerationSupport.LocalOllamaChatBaseUrl, GenerationSetting.RepeatPenalty));
        Assert.Equal(GenerationSettingUse.Unused, GenerationSupport.Use(SetupRouteType.ChatCompletions,
            "https://api.openai.com/v1", GenerationSetting.TopK));
        Assert.Equal(GenerationSettingUse.Used, GenerationSupport.Use(SetupRouteType.ChatCompletions,
            ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, GenerationSetting.ContextTokens));
    }

    [Fact]
    public void Thinking_steps_are_off_unless_on_is_chosen()
    {
        Assert.False(GenerationSettings.ThinkingSteps(null));
        Assert.False(GenerationSettings.ThinkingSteps(new() { Temperature = 0.7 }));
        Assert.False(GenerationSettings.ThinkingSteps(new() { Reasoning = false }));
        Assert.True(GenerationSettings.ThinkingSteps(new() { Reasoning = true }));
        // Every request says Off or On; only a retry after a refusal leaves the model's own default.
        Assert.False(GenerationSettings.WithReasoning(null).Reasoning);
        Assert.Equal(new GenerationSettings { Temperature = 0.7, Reasoning = false },
            GenerationSettings.WithReasoning(new() { Temperature = 0.7 }));
        Assert.True(GenerationSettings.WithReasoning(new() { Reasoning = true }).Reasoning);
        Assert.Null(GenerationSettings.WithoutReasoning(GenerationSettings.WithReasoning(null)));
    }

    [Fact]
    public void Context_size_defaults_to_100k_within_the_models_limit()
    {
        var cloud = ContextBudget.For(SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, null, null);
        Assert.Equal((GenerationSettings.DefaultContextTokens, ContextSource.Default), (cloud.Tokens, cloud.Source));
        Assert.Equal(GenerationSettings.DefaultContextTokens - GenerationSettings.ChatReplyTokens, cloud.InputTokens);
        var small = ContextBudget.For(SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, null, 32_768);
        Assert.Equal((32_768, ContextSource.ModelLimit), (small.Tokens, small.Source));
        var saved = ContextBudget.For(SetupRouteType.OpenAi, null, new() { ContextTokens = 1_000_000 }, ModelContextCatalog.Catalog(null, "gpt-4.1-2025-04-14"));
        Assert.Equal((1_000_000, ContextSource.Saved), (saved.Tokens, saved.Source));

        // A paired host loads up to 262,144 tokens now (32,768 before); a larger saved size is capped there.
        Assert.Equal(262_144, GenerationSettings.MaximumHostContextTokens);
        Assert.Equal(200_000, ContextBudget.For(SetupRouteType.GatewayOllama, null, new() { ContextTokens = 200_000 }, null).Tokens);
        var host = ContextBudget.For(SetupRouteType.GatewayOllama, null, new() { ContextTokens = 1_000_000 }, null);
        Assert.Equal(GenerationSettings.MaximumHostContextTokens, host.Tokens);
        Assert.Equal(GenerationSettings.DefaultHostContextTokens, ContextBudget.For(SetupRouteType.GatewayOllama, null, null, null).Tokens);

        var local = ContextBudget.For(SetupRouteType.ChatCompletions, GenerationSupport.LocalOllamaChatBaseUrl, null, null);
        Assert.Equal((ContextBudget.AssumedLocalOllamaTokens, ContextSource.OllamaAssumed), (local.Tokens, local.Source));
        Assert.Equal(ContextBudget.MinimumInputTokens, local.InputTokens);
        var loaded = ContextBudget.For(SetupRouteType.ChatCompletions, GenerationSupport.LocalOllamaChatBaseUrl,
            new() { ContextTokens = 100_000 }, 32_768);
        Assert.Equal((32_768, ContextSource.Ollama), (loaded.Tokens, loaded.Source));
    }

    [Fact]
    public void A_hosts_planned_window_is_room_and_never_more_input_per_reply()
    {
        // Before any plan: the old 8,192-token window and 6,144 tokens of text per reply.
        var before = ContextBudget.For(SetupRouteType.GatewayOllama, null, null, null);
        Assert.Equal((8_192, ContextSource.HostDefault, 6_144), (before.Tokens, before.Source, before.InputTokens));
        // Planned from the card: the window grows, each reply sends the same text, and the reply keeps its room.
        var planned = ContextBudget.For(SetupRouteType.GatewayOllama, null, null, 32_768);
        Assert.Equal((32_768, ContextSource.Planned), (planned.Tokens, planned.Source));
        Assert.Equal(before.InputTokens, planned.InputTokens);
        Assert.Equal(GenerationSettings.ChatReplyTokens, planned.ReplyTokens);
        Assert.True(planned.InputTokens + planned.ReplyTokens <= planned.Tokens);
        Assert.Contains("planned from the graphics card's free memory", planned.Describe(), StringComparison.Ordinal);
        // A saved size still says how much each reply sends; the window loaded is the larger one.
        var small = ContextBudget.For(SetupRouteType.GatewayOllama, null, new() { ContextTokens = 16_384 }, 32_768);
        Assert.Equal((32_768, ContextSource.Saved, 16_384 - 4_096), (small.Tokens, small.Source, small.InputTokens));
        var large = ContextBudget.For(SetupRouteType.GatewayOllama, null, new() { ContextTokens = 65_536 }, 32_768);
        Assert.Equal((65_536, 65_536 - 4_096), (large.Tokens, large.InputTokens));
        // A planned window below Martlet's usual one never shrinks it.
        Assert.Equal(8_192, ContextBudget.For(SetupRouteType.GatewayOllama, null, null, 4_096).Tokens);
    }

    [Fact]
    public void Model_limits_keep_one_entry_per_model_newest_first()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-model-limits-" + Guid.NewGuid().ToString("N"));
        try
        {
            var at = DateTimeOffset.UnixEpoch;
            var limits = new ModelLimits()
                .With(new() { Origin = "https://a/v1", ModelId = "m", ContextTokens = 8_192, Source = "test", CheckedAt = at })
                .With(new() { Origin = "https://b/v1", ModelId = "m", ContextTokens = 16_384, Source = "test", CheckedAt = at })
                .With(new() { Origin = "https://a/v1", ModelId = "m", ContextTokens = 131_072, Source = "test", CheckedAt = at });
            Assert.True(limits.Save(directory));
            var read = ModelLimits.Load(directory);
            Assert.Equal(2, read.Models.Count);
            Assert.Equal(131_072, read.Find("https://a/v1", "m")!.ContextTokens);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
