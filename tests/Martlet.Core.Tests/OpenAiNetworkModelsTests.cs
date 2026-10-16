using Martlet.Core.Access;

namespace Martlet.Core.Tests;

public sealed class OpenAiNetworkModelsTests
{
    [Fact]
    public void Network_model_names_have_a_character_no_ollama_model_name_has()
    {
        Assert.Equal(["martlet@any", "martlet@fast", "martlet@smart"], OpenAiNetworkModels.All);
        Assert.All(OpenAiNetworkModels.All, name => Assert.Contains('@', name));
        Assert.All(OpenAiNetworkModels.All, name => Assert.True(OpenAiNetworkModels.IsNetworkModel(name)));
        Assert.False(OpenAiNetworkModels.IsNetworkModel("martlet/any"));
        Assert.False(OpenAiNetworkModels.IsNetworkModel("qwen3:8b"));
        // A network model name is never a model a name resolves to.
        Assert.False(OpenAiNetworkModels.Answers(OpenAiNetworkModels.Any, OpenAiNetworkModels.Fast, OpenAiEndpoint.ChatPath));
    }

    [Theory]
    [InlineData("martlet@any", "qwen3:8b", true)]
    [InlineData("martlet@any", "nomic-embed-text:latest", false)]
    [InlineData("martlet@fast", "gemma4:e2b", true)]
    [InlineData("martlet@fast", "qwen3:4b", true)]
    [InlineData("martlet@fast", "qwen3:8b", false)]
    [InlineData("martlet@smart", "gemma4:27b", true)]
    [InlineData("martlet@smart", "llama3.3:70b", true)]
    [InlineData("martlet@smart", "qwen3:8b", false)]
    [InlineData("martlet@smart", "mxbai-embed-large", false)]
    public void Chat_names_resolve_to_chat_models_of_their_rating(string network, string model, bool answers) =>
        Assert.Equal(answers, OpenAiNetworkModels.Answers(network, model, OpenAiEndpoint.ChatPath));

    [Theory]
    [InlineData("nomic-embed-text:latest", true)]
    [InlineData("mxbai-embed-large", true)]
    [InlineData("qwen3-embedding:0.6b", true)]
    [InlineData("embeddinggemma", true)]
    [InlineData("bge-m3", true)]
    [InlineData("all-minilm:22m", true)]
    [InlineData("hf.co/someone/snowflake-arctic-embed-m", true)]
    [InlineData("qwen3:8b", false)]
    [InlineData("gemma4:e4b", false)]
    public void Embedding_models_are_known_by_their_names(string model, bool embeds)
    {
        Assert.Equal(embeds, OpenAiNetworkModels.MakesEmbeddings(model));
        Assert.Equal(embeds, OpenAiNetworkModels.Answers(OpenAiNetworkModels.Any, model, OpenAiEndpoint.EmbeddingsPath));
        Assert.False(OpenAiNetworkModels.Answers(OpenAiNetworkModels.Fast, model, OpenAiEndpoint.EmbeddingsPath));
    }

    [Fact]
    public void A_name_lists_the_models_it_may_run_on_and_says_clearly_when_there_are_none()
    {
        string[] network = ["gemma4:e4b", "qwen3:8b", "nomic-embed-text"];
        Assert.Equal(network, OpenAiNetworkModels.Resolving(OpenAiNetworkModels.Any, network));
        Assert.Equal(["gemma4:e4b"], OpenAiNetworkModels.Resolving(OpenAiNetworkModels.Fast, network));
        Assert.Empty(OpenAiNetworkModels.Resolving(OpenAiNetworkModels.Smart, network));
        var (smart, smartInvalid) = OpenAiNetworkModels.NoModel(OpenAiNetworkModels.Smart, OpenAiEndpoint.ChatPath);
        Assert.Contains("rated smart", smart, StringComparison.Ordinal);
        Assert.False(smartInvalid);
        var (embed, embedInvalid) = OpenAiNetworkModels.NoModel(OpenAiNetworkModels.Fast, OpenAiEndpoint.EmbeddingsPath);
        Assert.Contains("can't make embeddings", embed, StringComparison.Ordinal);
        Assert.True(embedInvalid);
    }
}
