using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class LatencyFirstDefaultsTests
{
    [Fact]
    public void TheRecommendedLocalModelIsTheFastestOnEveryCardAndTheLargestThatFitsIsOfferedBeside()
    {
        // MainWindow's statics include pack:// resources: register the scheme as a WPF app would before using them.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        foreach (var vram in new double?[] { null, 8, 11.6, 16, 24 })
            Assert.Equal("gemma4:e2b", MainWindow.RecommendedLocalModel(vram).Id);
        Assert.Equal("gemma4:e2b", MainWindow.LargestLocalModel(8).Id);
        Assert.Equal("gemma4:e4b", MainWindow.LargestLocalModel(11.6).Id);
        Assert.Equal("gemma4:12b", MainWindow.LargestLocalModel(16).Id);
        Assert.Equal("gemma4:26b", MainWindow.LargestLocalModel(24).Id);
    }

    [Fact]
    public void EverySuggestedLocalModelSeesAndSaysWhetherItHears()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        // Gemma 4 E2B, E4B and 12B hear in Ollama; Qwen3.5 and Gemma 4 26B get the transcript. Qwen3-VL 8B, which kept
        // thinking with Thinking steps Off, is no longer suggested.
        Assert.Equal(["gemma4:e2b", "gemma4:e4b", "gemma4:12b"], MainWindow.LocalChatModels.Where(m => m.Hears).Select(m => m.Id));
        Assert.DoesNotContain(MainWindow.LocalChatModels, m => m.Id == "qwen3-vl:8b");
        Assert.All(MainWindow.LocalChatModels, m => Assert.Equal(Martlet.Providers.VisionSupport.Supported, Martlet.Providers.VisionModelCatalog.Classify(m.Id)));
        // Those that don't hear never get a recording by name either, so their replies use the transcript.
        Assert.All(MainWindow.LocalChatModels.Where(m => !m.Hears), m =>
            Assert.NotEqual(Martlet.Providers.HearingSupport.Supported, Martlet.Providers.HearingModelCatalog.Classify(m.Id)));
        var qwen = MainWindow.LocalChatModels.Single(m => m.Id == "qwen3.5:4b");
        var options = JobOptions.OllamaModels(MainWindow.LocalChatModels, null, null, 11.6).ToDictionary(o => o.Key);
        Assert.Contains("gets the transcript", options[qwen.Id].Summary, StringComparison.Ordinal);
        Assert.StartsWith("Fastest", options["gemma4:e2b"].Summary, StringComparison.Ordinal);
        Assert.Equal("recommended", options["gemma4:e2b"].Badge);
        Assert.Equal("smartest that fits", options[MainWindow.LargestLocalModel(11.6).Id].Badge);
    }
}
