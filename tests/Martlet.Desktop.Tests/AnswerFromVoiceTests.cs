using System.IO;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class AnswerFromVoiceTests
{
    private static SetupRoute Thinking(string origin, string model) =>
        new() { Role = SetupRole.Llm, RouteType = SetupRouteType.ChatCompletions, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin,
            ModelId = model, ConfigurationRevision = Guid.NewGuid() };

    [Fact]
    public void AnswerFromMyVoiceIsOnByDefaultAndSaved()
    {
        Assert.True(new TalkPreferences().AnswerFromVoice);
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.AnswerFromVoice." + Guid.NewGuid().ToString("N"));
        try
        {
            // A file saved before the choice existed keeps it on.
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), """{"HandsFree":true,"Version":3}""");
            Assert.True(TalkPreferences.Load(directory).AnswerFromVoice);
            Assert.True(new TalkPreferences(AnswerFromVoice: false).Save(directory));
            Assert.False(TalkPreferences.Load(directory).AnswerFromVoice);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TheRecommendedLocalModelIsTheFastestOnEveryCardAndTheLargestThatFitsIsOfferedBeside()
    {
        foreach (var vram in new double?[] { null, 8, 11.6, 16, 24 })
            Assert.Equal("gemma4:e2b", MainWindow.RecommendedLocalModel(vram).Id);
        Assert.Equal("gemma4:e2b", MainWindow.LargestLocalModel(8).Id);
        Assert.Equal("gemma4:e4b", MainWindow.LargestLocalModel(11.6).Id);
        Assert.Equal("gemma4:12b", MainWindow.LargestLocalModel(16).Id);
        Assert.Equal("gemma4:26b", MainWindow.LargestLocalModel(24).Id);
    }

    [Fact]
    public void TheListeningPageSaysWhetherMartletAnswersFromTheRecording()
    {
        var ollama = Thinking(MainWindow.LocalOllamaBaseUrl, "gemma4:e2b");
        Assert.StartsWith("On. Ollama on this PC answers your recording", MainWindow.AnswerFromVoiceStatus(new(), ollama, null));
        Assert.StartsWith("Off.", MainWindow.AnswerFromVoiceStatus(new(AnswerFromVoice: false), ollama, null));
        Assert.Contains("only with Always listening", MainWindow.AnswerFromVoiceStatus(new(HandsFree: false), ollama, null));
        Assert.Contains("doesn't hear", MainWindow.AnswerFromVoiceStatus(new(), Thinking(MainWindow.LocalOllamaBaseUrl, "gemma4:12b"), null));
        // A cloud model or an Ollama cloud model needs Let Thinking hear my voice.
        var cloud = Thinking(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "google/gemini-2.5-flash");
        Assert.Contains("Let Thinking hear my voice", MainWindow.AnswerFromVoiceStatus(new(), cloud, null));
        Assert.Contains("Let Thinking hear my voice", MainWindow.AnswerFromVoiceStatus(new(), Thinking(MainWindow.LocalOllamaBaseUrl, "gemma4:e2b-cloud"), null));
        Assert.StartsWith("On. The Thinking model answers your recording", MainWindow.AnswerFromVoiceStatus(new(HearVoice: true), cloud, null));
        Assert.True(LiveConversationConfiguration.AnswersFromVoice(ollama, null, hearConsent: false));
        Assert.False(LiveConversationConfiguration.AnswersFromVoice(cloud, null, hearConsent: false));
        Assert.True(LiveConversationConfiguration.AnswersFromVoice(cloud, null, hearConsent: true));
    }
}
