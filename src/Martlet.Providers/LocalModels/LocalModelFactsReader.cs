using System.Net;
using System.Net.Http;
using Martlet.Core.Planning;

namespace Martlet.Providers.LocalModels;

/// <summary>Looks up what an open-weight model needs to run locally (<see cref="LocalModelFacts"/>): from Hugging Face the
/// model's license, task and parameters, its <c>config.json</c> (layers, KV heads, experts, inputs), and a GGUF repository's
/// quantizations with their file sizes and install names; from the Ollama registry one tag's exact layer sizes. Then
/// <see cref="LocalModelFacts.Estimate"/> gives the memory at a context. Keyless; at most five requests a lookup, each answer
/// cached for a day (<see cref="Shared"/> keeps them for the whole app). Never runs while a reply is on its way.</summary>
public sealed class LocalModelFactsReader
{
    /// <summary>GGUF publishers Martlet prefers, in order, when a model has several GGUF repositories.</summary>
    public static IReadOnlyList<string> GgufPublishers { get; } = ["ggml-org", "unsloth", "lmstudio-community", "bartowski"];
    /// <summary>Requests Martlet sends each source in any five minutes: well under Hugging Face's 500.</summary>
    public const int RequestBudget = 100;

    private static readonly Lazy<LocalModelFactsReader> shared = new(() => new(CreateClient()));
    private readonly TimeProvider clock;

    /// <summary>One reader for the whole app, so its cache and request budget hold across every lookup.</summary>
    public static LocalModelFactsReader Shared => shared.Value;

    public LocalModelFactsReader(HttpClient client, Uri? huggingFace = null, Uri? ollamaRegistry = null, TimeProvider? clock = null,
        int requestBudget = RequestBudget)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.clock = clock ?? TimeProvider.System;
        HuggingFace = new(new(client, this.clock, requestBudget, "Hugging Face"), huggingFace ?? HuggingFaceReader.DefaultOrigin);
        Ollama = new(new(client, this.clock, requestBudget, "the Ollama registry"), ollamaRegistry ?? OllamaRegistryReader.DefaultOrigin);
    }

    public HuggingFaceReader HuggingFace { get; }
    public OllamaRegistryReader Ollama { get; }

    /// <summary>A client for public metadata: follows redirects (Hugging Face's renamed repositories, the registry's blob store),
    /// sends no credentials or cookies.</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 5, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Martlet (+https://github.com/throndir2/Martlet)");
        return client;
    }

    /// <summary>What <paramref name="query"/> names: a Hugging Face repository (owner/name, <c>hf.co/{repo}:{quant}</c> or a
    /// huggingface.co address) with the quantization when given, or an Ollama tag (a name with a tag, or without a slash).</summary>
    public static (string? Repo, string? Quantization, string? OllamaTag) Parse(string? query)
    {
        var text = query?.Trim() ?? "";
        foreach (var prefix in new[] { "https://huggingface.co/", "http://huggingface.co/", "huggingface.co/", "hf.co/" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = text[prefix.Length..].TrimEnd('/');
                var colon = rest.IndexOf(':', StringComparison.Ordinal);
                var repo = colon < 0 ? rest : rest[..colon];
                var quantization = colon < 0 ? null : rest[(colon + 1)..];
                return HuggingFaceReader.IsRepo(repo) ? (repo, quantization is { Length: > 0 } ? quantization : null, null) : (null, null, null);
            }
        if (!text.Contains(':', StringComparison.Ordinal) && HuggingFaceReader.IsRepo(text)) return (text, null, null);
        return OllamaRegistryReader.Parse(text) is not null ? (null, null, text) : (null, null, null);
    }

    /// <summary>Looks up <paramref name="query"/> (see <see cref="Parse"/>).</summary>
    public Task<LocalModelFacts> LookupAsync(string query, CancellationToken token)
    {
        var (repo, quantization, tag) = Parse(query);
        return LookupAsync(repo, tag, token, query, quantization);
    }

    /// <summary>Looks up a model by its Hugging Face repository, an Ollama tag, or both (the repository gives the shape and
    /// inputs, the tag its exact Ollama download). Never throws for a source that doesn't answer: <see cref="LocalModelFacts.Problems"/>
    /// says what couldn't be read.</summary>
    public async Task<LocalModelFacts> LookupAsync(string? huggingFaceRepo, string? ollamaTag, CancellationToken token,
        string? query = null, string? quantization = null)
    {
        var sources = new List<string>();
        var problems = new List<string>();
        var quantizations = new List<LocalModelQuantization>();
        var encoders = new List<LocalModelFile>();
        var drafts = new List<LocalModelFile>();
        IReadOnlyList<string> ggufRepos = [];
        HuggingFaceModel? baseInfo = null, ggufInfo = null;
        LocalModelArchitecture? architecture = null;
        string? baseRepo = null;
        if (huggingFaceRepo is not null && !HuggingFaceReader.IsRepo(huggingFaceRepo))
        {
            problems.Add($"{huggingFaceRepo} isn't a Hugging Face repository name.");
            huggingFaceRepo = null;
        }
        if (huggingFaceRepo is not null)
        {
            var (info, problem) = await HuggingFace.ModelAsync(huggingFaceRepo, token).ConfigureAwait(false);
            if (info is null) problems.Add(Sentence(problem));
            else
            {
                sources.Add($"Hugging Face model API: huggingface.co/api/models/{info.Id}");
                baseRepo = info.Id;
                baseInfo = info;
                if (info.HasGgufWeights)
                {
                    ggufInfo = info;
                    // A GGUF repository names the model it was made from; that one has the config, license and parameters.
                    if (info.BaseModels.FirstOrDefault() is { } made)
                    {
                        baseRepo = made;
                        var (madeInfo, madeProblem) = await HuggingFace.ModelAsync(made, token).ConfigureAwait(false);
                        if (madeInfo is null) problems.Add(Sentence(madeProblem));
                        else
                        {
                            baseInfo = madeInfo;
                            baseRepo = madeInfo.Id;
                            sources.Add($"Hugging Face model API: huggingface.co/api/models/{madeInfo.Id}");
                        }
                    }
                }
                else
                {
                    var (repos, searchProblem) = await HuggingFace.GgufReposAsync(info.Id, token).ConfigureAwait(false);
                    ggufRepos = repos;
                    if (searchProblem is not null) problems.Add(Sentence(searchProblem));
                    else sources.Add($"Hugging Face GGUF search: base_model:quantized:{info.Id}");
                    if (Pick(repos) is { } pick)
                    {
                        var (picked, pickProblem) = await HuggingFace.ModelAsync(pick, token).ConfigureAwait(false);
                        if (picked is null) problems.Add(Sentence(pickProblem));
                        else
                        {
                            ggufInfo = picked;
                            sources.Add($"Hugging Face model API: huggingface.co/api/models/{picked.Id}");
                        }
                    }
                    else if (searchProblem is null) problems.Add($"Hugging Face lists no GGUF repository made from {info.Id}, so Ollama can't install it from there.");
                }
                var (config, configProblem) = await HuggingFace.ConfigAsync(baseRepo, token).ConfigureAwait(false);
                if (config is null && ggufInfo is not null && ggufInfo.Id != baseRepo && ggufInfo.Files.Any(f => f.Path == "config.json"))
                {
                    (config, var again) = await HuggingFace.ConfigAsync(ggufInfo.Id, token).ConfigureAwait(false);
                    if (config is not null) sources.Add($"config.json: huggingface.co/{ggufInfo.Id}/raw/main/config.json");
                    else configProblem = again ?? configProblem;
                }
                else if (config is not null) sources.Add($"config.json: huggingface.co/{baseRepo}/raw/main/config.json");
                if (config is null) problems.Add(Sentence(configProblem));
                architecture = config;
                if (ggufInfo is not null)
                {
                    var (found, foundEncoders, foundDrafts) = HuggingFaceReader.Quantizations(ggufInfo);
                    quantizations.AddRange(found);
                    encoders.AddRange(foundEncoders);
                    drafts.AddRange(foundDrafts);
                    if (ggufRepos.Count == 0) ggufRepos = [ggufInfo.Id];
                }
            }
        }
        OllamaRegistryTag? tag = null;
        if (ollamaTag is not null)
        {
            var (found, problem) = await Ollama.TagAsync(ollamaTag, token).ConfigureAwait(false);
            if (found is null) problems.Add(Sentence(problem));
            else
            {
                tag = found;
                var parts = OllamaRegistryReader.Parse(ollamaTag)!.Value;
                sources.Add($"Ollama registry: registry.ollama.ai/v2/{parts.Namespace}/{parts.Model}/manifests/{parts.Tag}");
                quantizations.Insert(0, new(found.Quantization ?? found.Name, found.WeightsBytes, ollamaTag, "Ollama registry")
                {
                    EncoderBytes = found.EncoderBytes, DraftBytes = found.DraftBytes
                });
                if (found.EncoderBytes > 0) encoders.Insert(0, new($"{found.Name} projector", LocalModelFileKind.Encoder, null, found.EncoderBytes));
                if (found.DraftBytes > 0) drafts.Insert(0, new($"{found.Name} draft", LocalModelFileKind.Draft, null, found.DraftBytes));
            }
        }
        var parameters = baseInfo?.Parameters;
        var weightsParameters = ggufInfo?.GgufParameters ?? tag?.Parameters ?? parameters;
        long? active = weightsParameters is { } total && architecture is { MixtureOfExperts: true }
            ? architecture.ActiveParameters(total)
            : LocalModelQuantizations.ActiveFromName(baseRepo ?? huggingFaceRepo) ?? weightsParameters;
        LocalModelInputs? inputs = architecture is not null
            ? new(architecture.Sees, architecture.Hears, architecture.VideoTokens ? true : architecture.Sees ? null : false, "config.json")
            : tag is { HasProjector: true } ? new(true, null, null, "Ollama registry (projector layer)") : null;
        var facts = new LocalModelFacts
        {
            Query = query ?? huggingFaceRepo ?? ollamaTag ?? "",
            HuggingFaceRepo = baseRepo,
            GgufRepo = ggufInfo?.Id,
            OllamaTag = tag is null ? null : ollamaTag,
            Parameters = parameters,
            WeightsParameters = weightsParameters,
            ActiveParameters = active,
            License = baseInfo?.License ?? ggufInfo?.License,
            PipelineTag = baseInfo?.PipelineTag ?? ggufInfo?.PipelineTag,
            MaxContext = architecture?.MaxContext ?? ggufInfo?.GgufContext,
            Gated = baseInfo?.Gated ?? false,
            Architecture = architecture,
            Inputs = inputs,
            Quantizations = quantizations,
            Encoders = encoders,
            Drafts = drafts,
            GgufRepos = ggufRepos,
            Sources = sources,
            Problems = problems,
            CheckedAt = clock.GetUtcNow()
        };
        if (quantization is not null && facts.Quantization(quantization) is null)
            facts = facts with { Problems = [.. problems, $"{facts.GgufRepo ?? facts.HuggingFaceRepo ?? facts.Query} has no {quantization} quantization."] };
        return facts;
    }

    /// <summary>The GGUF repository to read: the first of <see cref="GgufPublishers"/>, else the most downloaded.</summary>
    public static string? Pick(IReadOnlyList<string> repos)
    {
        foreach (var publisher in GgufPublishers)
            if (repos.FirstOrDefault(r => r.StartsWith(publisher + "/", StringComparison.OrdinalIgnoreCase)) is { } found) return found;
        return repos.FirstOrDefault();
    }

    private static string Sentence(string? text) => text is null ? "A source didn't answer." : text.EndsWith('.') ? text : text + ".";
}
