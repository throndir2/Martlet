using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Providers.Tests;

public sealed class VisionModelCatalogTests
{
    [Theory]
    [InlineData("gpt-4.1-mini-2025-04-14", VisionSupport.Supported)]
    [InlineData("gemma3-4b", VisionSupport.Supported)]
    [InlineData("gemma3:12b", VisionSupport.Supported)]
    [InlineData("google/gemma-3-27b-it", VisionSupport.Supported)]
    [InlineData("gemma4:e2b", VisionSupport.Supported)]
    [InlineData("gemma4-e4b", VisionSupport.Supported)]
    [InlineData("google/gemma-4-31b-it", VisionSupport.Supported)]
    [InlineData("google/gemma-4-26b-a4b-it", VisionSupport.Supported)]
    [InlineData("google/diffusiongemma-26b-a4b-it", VisionSupport.Supported)]
    [InlineData("qwen3-vl:8b", VisionSupport.Supported)]
    [InlineData("qwen3.5:4b", VisionSupport.Supported)]
    [InlineData("qwen3.5:2b", VisionSupport.Supported)]
    [InlineData("ministral-3:3b", VisionSupport.Supported)]
    [InlineData("ministral-8b-2410", VisionSupport.Unsupported)]
    [InlineData("moonshotai/kimi-k3", VisionSupport.Supported)]
    [InlineData("z-ai/glm-5.3-flash", VisionSupport.Supported)]
    [InlineData("meta/muse-glimmer-30b", VisionSupport.Supported)]
    [InlineData("deepseek-ai/deepseek-v4.1-flash", VisionSupport.Supported)]
    [InlineData("gemma4:e4b", VisionSupport.Supported)]
    [InlineData("qwen2.5vl-7b", VisionSupport.Supported)]
    [InlineData("qwen/qwen2.5-vl-72b-instruct", VisionSupport.Supported)]
    [InlineData("llama3.2-vision-11b", VisionSupport.Supported)]
    [InlineData("meta/llama-3.2-90b-vision-instruct", VisionSupport.Supported)]
    [InlineData("openai/gpt-4o-mini", VisionSupport.Supported)]
    [InlineData("anthropic/claude-sonnet-4", VisionSupport.Supported)]
    [InlineData("o4-mini", VisionSupport.Supported)]
    [InlineData("deepseek-vl2", VisionSupport.Supported)]
    [InlineData("mistralai/mistral-small-3.1-24b-instruct", VisionSupport.Supported)]
    [InlineData("llama3.2-3b", VisionSupport.Unsupported)]
    [InlineData("qwen2.5-14b", VisionSupport.Unsupported)]
    [InlineData("llama3.1-8b", VisionSupport.Unsupported)]
    [InlineData("gemma3:1b", VisionSupport.Unsupported)]
    [InlineData("gemma3n:e4b", VisionSupport.Unsupported)]
    [InlineData("o3-mini", VisionSupport.Unsupported)]
    [InlineData("deepseek/deepseek-chat", VisionSupport.Unsupported)]
    [InlineData("openai/gpt-oss-20b", VisionSupport.Unsupported)]
    [InlineData("moonshotai/kimi-k2", VisionSupport.Unknown)]
    [InlineData("my-finetune", VisionSupport.Unknown)]
    public void Classifies_known_vision_and_text_only_models(string model, VisionSupport expected) =>
        Assert.Equal(expected, VisionModelCatalog.Classify(model));

    [Fact]
    public void Every_local_recommendation_can_see()
    {
        Assert.NotEmpty(VisionModelCatalog.LocalRecommendations);
        Assert.All(VisionModelCatalog.LocalRecommendations,
            model => Assert.Equal(VisionSupport.Supported, VisionModelCatalog.Classify(model.Tag)));
    }

    [Fact]
    public void Every_named_endpoint_default_can_see_and_none_is_retired()
    {
        Assert.All(ChatCompletionsEndpointCatalog.NamedEndpoints, endpoint =>
        {
            Assert.Equal(VisionSupport.Supported, VisionModelCatalog.Classify(endpoint.DefaultModelId));
            Assert.False(endpoint.Retired(endpoint.DefaultModelId));
        });
        Assert.NotNull(ChatCompletionsEndpointCatalog.RetiredOn(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, "meta/llama-3.3-70b-instruct"));
        Assert.Null(ChatCompletionsEndpointCatalog.RetiredOn(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "meta/llama-3.3-70b-instruct"));
    }

    [Fact]
    public void Image_input_is_bounded_signature_checked_and_reserves_tokens()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF }.Concat(new byte[40]).ToArray();
        var image = new BoundedImage(jpeg, ImageMediaType.Jpeg, 16, 9);
        Assert.StartsWith("data:image/jpeg;base64,", image.ToDataUrl());
        Assert.DoesNotContain("/9j", image.ToString());
        Assert.Throws<ContractException>(() => new BoundedImage(jpeg, ImageMediaType.Png, 16, 9));
        Assert.Throws<ContractException>(() => new BoundedImage(new byte[BoundedImage.HardMaxBytes + 1], ImageMediaType.Jpeg, 16, 9));
        var plain = new BoundedTextInput("look");
        var glance = new BoundedTextInput("look", image: image);
        Assert.Equal(plain.InputTokenReservation + BoundedImage.TokenReservation, glance.InputTokenReservation);
    }
}
