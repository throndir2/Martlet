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

    [Theory]
    [InlineData(2.5, null, null, null)]
    [InlineData(null, 0.0, null, null)]
    [InlineData(null, null, 8, null)]
    [InlineData(null, null, 2048, 2048)]
    public void Out_of_range_values_are_rejected(double? temperature, double? topP, int? replyTokens, int? contextTokens) =>
        Assert.Throws<ContractException>(() => new GenerationSettings
        {
            Temperature = temperature, TopP = topP, MaxReplyTokens = replyTokens,
            ContextTokens = contextTokens
        }.Validate());

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

        var host = ContextBudget.For(SetupRouteType.GatewayOllama, null, new() { ContextTokens = 200_000 }, null);
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
