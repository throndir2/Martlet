using Martlet.Core.Settings;

namespace Martlet.Core.Access;

/// <summary>
/// The network model names of the OpenAI-compatible endpoint (docs/OPENAI_ENDPOINT.md#network-model-names): names that
/// <c>GET /v1/models</c> lists and the POST routes take, which Martlet resolves at request time to a real model of the
/// network, a free one first. Each name has an <c>@</c>, which an Ollama model name or tag can never have, so a real model
/// can never have the same name.
/// <list type="bullet">
/// <item><see cref="Any"/>: any chat model of the network (on <c>/v1/embeddings</c>: any embedding model).</item>
/// <item><see cref="Fast"/>: a model Martlet rates Fast (<see cref="ThinkingSmartsGuess"/>, as the Thinking pool does).</item>
/// <item><see cref="Smart"/>: a model Martlet rates Smart.</item>
/// </list>
/// </summary>
public static class OpenAiNetworkModels
{
    public const string Any = "martlet@any";
    public const string Fast = "martlet@fast";
    public const string Smart = "martlet@smart";

    /// <summary>The names, in the order <c>GET /v1/models</c> lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Any, Fast, Smart];

    public static bool IsNetworkModel(string? model) => model is Any or Fast or Smart;

    /// <summary>Whether <paramref name="model"/> only makes embeddings (it can't chat), guessed from its name as Ollama's
    /// embedding models are named: nomic-embed-text, mxbai-embed-large, snowflake-arctic-embed, qwen3-embedding,
    /// embeddinggemma, bge-m3, all-minilm and similar.</summary>
    public static bool MakesEmbeddings(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var name = model.Trim().ToLowerInvariant();
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Contains("embed", StringComparison.Ordinal) || name.StartsWith("bge-", StringComparison.Ordinal) ||
            name.StartsWith("bge:", StringComparison.Ordinal) || name.StartsWith("all-minilm", StringComparison.Ordinal) ||
            name.StartsWith("paraphrase-multilingual", StringComparison.Ordinal) || name.StartsWith("multilingual-e5", StringComparison.Ordinal) ||
            name.StartsWith("e5-", StringComparison.Ordinal) || name.StartsWith("gte-", StringComparison.Ordinal);
    }

    /// <summary>Whether a request for the network model name <paramref name="network"/> on <paramref name="path"/> (one of
    /// <see cref="OpenAiEndpoint.RelayPaths"/>) may run on the real model <paramref name="model"/>: on
    /// <see cref="OpenAiEndpoint.EmbeddingsPath"/> only <see cref="Any"/>, on an embedding model; on the other paths a model that
    /// can chat, of the name's rating.</summary>
    public static bool Answers(string network, string model, string path)
    {
        if (IsNetworkModel(model) || string.IsNullOrWhiteSpace(model)) return false;
        var embeds = MakesEmbeddings(model);
        if (path == OpenAiEndpoint.EmbeddingsPath) return network == Any && embeds;
        if (embeds) return false;
        return network switch
        {
            Any => true,
            Fast => ThinkingSmartsGuess.From(model) == ThinkingSmarts.Fast,
            Smart => ThinkingSmartsGuess.From(model) == ThinkingSmarts.Smart,
            _ => false
        };
    }

    /// <summary>The real models of <paramref name="models"/> that <paramref name="network"/> may run on, on any path.</summary>
    public static IReadOnlyList<string> Resolving(string network, IEnumerable<string> models) => models
        .Where(m => Answers(network, m, OpenAiEndpoint.ChatPath) || Answers(network, m, OpenAiEndpoint.EmbeddingsPath))
        .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Why a request for <paramref name="network"/> on <paramref name="path"/> can't run on any model of the network
    /// (it has none the name fits), for the 404 or 400 answer; and whether the request itself is wrong (400).</summary>
    public static (string Message, bool Invalid) NoModel(string network, string path) => (network, path) switch
    {
        (Fast or Smart, OpenAiEndpoint.EmbeddingsPath) =>
            ($"{network} picks a chat model, so it can't make embeddings. Name an embedding model, or {Any} for any embedding " +
             "model of this Martlet network.", true),
        (Any, OpenAiEndpoint.EmbeddingsPath) =>
            ("No host of this Martlet network runs an embedding model (for example nomic-embed-text) now. GET /v1/models lists " +
             "the models.", false),
        (Any, _) => ("No host of this Martlet network runs a chat model now. GET /v1/models lists the models.", false),
        _ =>
            ($"No model of this Martlet network is rated {network[(network.IndexOf('@') + 1)..]} now (Martlet rates each model " +
             $"Fast, Standard or Smart from its name). Use {Any}, or a name GET /v1/models lists.", false)
    };
}
