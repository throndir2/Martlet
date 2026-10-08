using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class LocalModelAppTests
{
    private static SetupRoute Chat(string origin, string model = "qwen/qwen3-8b") => new()
    {
        RouteSchemaVersion = 1, RouteType = SetupRouteType.ChatCompletions, Enabled = true, Role = SetupRole.Llm,
        ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin, ModelId = model, ConfigurationRevision = Guid.NewGuid()
    };

    [Fact]
    public void A_model_app_on_this_PC_counts_as_This_PC_not_as_a_cloud_provider()
    {
        Assert.True(MainWindow.IsLocalServer(Chat("http://127.0.0.1:1234/v1")));
        Assert.True(MainWindow.IsLocalServer(Chat("http://[::1]:8080/v1")));
        Assert.False(MainWindow.IsLocalServer(Chat(MainWindow.LocalOllamaBaseUrl, "gemma4:e2b")));
        Assert.True(MainWindow.IsLocalOllama(Chat(MainWindow.LocalOllamaBaseUrl, "gemma4:e2b")));
        Assert.False(MainWindow.IsLocalServer(Chat(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl)));
        Assert.False(MainWindow.IsLocalServer(null));
    }

    [Fact]
    public void This_PC_opens_on_the_app_in_use_else_an_app_already_running_when_Ollama_isnt_installed()
    {
        Assert.Equal(MainWindow.LocalApp.Ollama, MainWindow.DefaultLocalApp(Chat(MainWindow.LocalOllamaBaseUrl, "gemma4:e2b"), false, 2));
        Assert.Equal(MainWindow.LocalApp.Other, MainWindow.DefaultLocalApp(Chat("http://127.0.0.1:1234/v1"), true, 0));
        Assert.Equal(MainWindow.LocalApp.Other, MainWindow.DefaultLocalApp(null, ollamaInstalled: false, otherAppsFound: 1));
        Assert.Equal(MainWindow.LocalApp.Ollama, MainWindow.DefaultLocalApp(null, ollamaInstalled: true, otherAppsFound: 1));
        Assert.Equal(MainWindow.LocalApp.Ollama, MainWindow.DefaultLocalApp(Chat(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl), false, 0));
    }

    [Fact]
    public void What_looking_found_reads_in_plain_words()
    {
        IReadOnlyList<LocalModelServer> found =
        [
            new("ollama", "Ollama", MainWindow.LocalOllamaBaseUrl, ["gemma4:e2b"]),
            new("lm-studio", "LM Studio", "http://127.0.0.1:1234/v1", ["a", "b", "c"]),
            new("port-8000", "Model app on port 8000", "http://127.0.0.1:8000/v1", []) { NeedsKey = true }
        ];
        Assert.Equal("Found on this PC: LM Studio at http://127.0.0.1:1234/v1 (3 models); Model app on port 8000 at " +
            "http://127.0.0.1:8000/v1 (asks for a key). Ollama is running too; choose Ollama above to use it.",
            MainWindow.LocalServersSummary(found, looking: false, ollamaRunning: false));
        Assert.Equal("Looking for model apps on this PC...", MainWindow.LocalServersSummary(null, looking: true, ollamaRunning: false));
        Assert.StartsWith("No other model app answers on this PC.", MainWindow.LocalServersSummary([], false, false), StringComparison.Ordinal);
        Assert.Equal("LM Studio · http://127.0.0.1:1234/v1 · 3 models", MainWindow.LocalServerItem(found[1]));
        Assert.Equal("Model app on port 8000 · http://127.0.0.1:8000/v1 · asks for a key", MainWindow.LocalServerItem(found[2]));
    }

    [Fact]
    public void Home_says_when_the_model_app_Thinking_uses_isnt_ready()
    {
        var route = Chat("http://127.0.0.1:1234/v1");
        static LocalServerAnswer Answer(LocalServerAnswerKind kind, params string[] models) => new(kind, models, null, "why");
        Assert.Null(MainWindow.LocalServerHealth(route, Answer(LocalServerAnswerKind.Models, "qwen/qwen3-8b"), "LM Studio"));
        // An app that lists nothing may load the model when asked.
        Assert.Null(MainWindow.LocalServerHealth(route, Answer(LocalServerAnswerKind.Models), "LM Studio"));
        Assert.Equal("Thinking uses qwen/qwen3-8b in LM Studio on this PC, but nothing answers at http://127.0.0.1:1234/v1. " +
            "Start LM Studio's server, or choose another way to think.",
            MainWindow.LocalServerHealth(route, Answer(LocalServerAnswerKind.NoAnswer), "LM Studio"));
        Assert.Contains("doesn't list it now (it lists gemma)",
            MainWindow.LocalServerHealth(route, Answer(LocalServerAnswerKind.Models, "gemma"), "LM Studio"), StringComparison.Ordinal);
        Assert.EndsWith("Start the app's server, or choose another way to think.", MainWindow.LocalServerHealth(Chat("http://127.0.0.1:8080/v1"),
            Answer(LocalServerAnswerKind.NoAnswer), "the model app on port 8080"), StringComparison.Ordinal);
        Assert.Contains("asks for an API key", MainWindow.LocalServerHealth(route, Answer(LocalServerAnswerKind.NeedsKey), "LM Studio"),
            StringComparison.Ordinal);
        // With a key saved, an app that wants one is fine (Home asks without it).
        Assert.Null(MainWindow.LocalServerHealth(route with { CredentialId = Guid.NewGuid() }, Answer(LocalServerAnswerKind.NeedsKey), "LM Studio"));
        Assert.Contains("Another program may be using its port",
            MainWindow.LocalServerHealth(route, Answer(LocalServerAnswerKind.NotAModelServer), "LM Studio"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_app_is_named_for_what_it_is_wherever_Thinking_is_described()
    {
        Assert.Equal("LM Studio", NetworkMap.ProviderName(Chat("http://127.0.0.1:1234/v1")));
        Assert.Equal("Ollama", NetworkMap.ProviderName(Chat(MainWindow.LocalOllamaBaseUrl, "gemma4:e2b")));
        Assert.Equal("A model app on this PC", NetworkMap.ProviderName(Chat("http://127.0.0.1:8080/v1")));
        Assert.Equal("LM Studio on this PC (http://127.0.0.1:1234/v1)",
            LiveConversationConfiguration.LlmDestinationName(Chat("http://127.0.0.1:1234/v1")));
        Assert.Equal("the model app on port 9000 on this PC (http://127.0.0.1:9000/v1)",
            LiveConversationConfiguration.LlmDestinationName(Chat("http://127.0.0.1:9000/v1")));
        Assert.Equal("the endpoint at https://api.groq.com/openai/v1",
            LiveConversationConfiguration.LlmDestinationName(Chat("https://api.groq.com/openai/v1")));
    }
}
