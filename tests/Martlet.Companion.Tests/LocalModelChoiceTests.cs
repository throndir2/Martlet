using Martlet.Providers;

namespace Martlet.Companion.Tests;

public sealed class LocalModelChoiceTests
{
    [Fact]
    public void Says_what_was_found_and_fills_in_the_first_app_with_a_model()
    {
        IReadOnlyList<LocalModelServer> found =
        [
            new("port-8000", "Model app on port 8000", "http://127.0.0.1:8000/v1", []) { NeedsKey = true },
            new("lm-studio", "LM Studio", "http://127.0.0.1:1234/v1", ["qwen/qwen3-8b", "google/gemma-3-12b", "a", "b", "c"])
        ];
        Assert.Equal("Found: Model app on port 8000 at http://127.0.0.1:8000/v1 (asks for a key); LM Studio at http://127.0.0.1:1234/v1 " +
            "(qwen/qwen3-8b, google/gemma-3-12b, a, b and 1 more).", LocalModelChoice.Describe(found));
        Assert.Equal(("http://127.0.0.1:1234/v1", "qwen/qwen3-8b"), LocalModelChoice.Pick(found, "")!.Value);
        // A model already typed that the app has is kept.
        Assert.Equal(("http://127.0.0.1:1234/v1", "google/gemma-3-12b"), LocalModelChoice.Pick(found, " google/gemma-3-12b ")!.Value);
    }

    [Fact]
    public void Nothing_found_changes_nothing()
    {
        Assert.Null(LocalModelChoice.Pick([], "llama3.2"));
        Assert.StartsWith("No model app answers on this computer.", LocalModelChoice.Describe([]), StringComparison.Ordinal);
        // An app with no model loaded keeps what was typed.
        Assert.Equal(("http://127.0.0.1:8080/v1", "llama3.2"),
            LocalModelChoice.Pick([new("llama-cpp", "llama.cpp server", "http://127.0.0.1:8080/v1", [])], "llama3.2")!.Value);
    }
}
