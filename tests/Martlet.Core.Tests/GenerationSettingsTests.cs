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
        Assert.Equal(GenerationSettingUse.Unused, GenerationSupport.Use(SetupRouteType.ChatCompletions,
            ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, GenerationSetting.ContextTokens));
    }
}
