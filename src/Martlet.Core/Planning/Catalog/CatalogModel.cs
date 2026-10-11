namespace Martlet.Core.Planning;

/// <summary>A model's name on each route and source. <see cref="Ollama"/> holds the tags Martlet's own list gives it; an Ollama
/// name such as "gemma4:26b" or "hf.co/{repo}:{quant}" is also found by family and size (<see cref="ModelCatalog.Find"/>).</summary>
public sealed record CatalogModelNames
{
    public string? HuggingFace { get; init; }
    public string? OpenRouter { get; init; }
    public string? NvidiaBuild { get; init; }
    public string? ModelsDev { get; init; }
    /// <summary>The LMArena name its text rating came from ("gemma-4-26b-a4b").</summary>
    public string? LmArena { get; init; }
    public string? LmArenaVision { get; init; }
    public IReadOnlyList<string> Ollama { get; init; } = [];
}

/// <summary>One model in the catalog (docs/MODEL_CATALOG.md, The record): keyed by its Hugging Face repository when it has open
/// weights, else by the provider's model ID. <see cref="Facts"/> holds every <see cref="CatalogFacts"/> key with each source's
/// answer and the answer worked out from them. <see cref="Rank"/> (0 to 100) only orders models inside Martlet; it is never
/// shown as a number (it may come from Artificial Analysis's index, whose terms forbid that).</summary>
public sealed record CatalogModel
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public CatalogModelNames Names { get; init; } = new();
    /// <summary>The family without separators ("gemma4"), as <see cref="CatalogNaming.CompactFamily"/> reads the name.</summary>
    public string Family { get; init; } = "";
    /// <summary>The sizes in the name ("26b-a4b").</summary>
    public string Size { get; init; } = "";
    public IReadOnlyDictionary<string, CatalogFact> Facts { get; init; } = new Dictionary<string, CatalogFact>();
    /// <summary>LMArena's overall text rating (shown with <see cref="CatalogSources.LmArenaCredit"/>).</summary>
    public double? Rating { get; init; }
    public int? RatingVotes { get; init; }
    /// <summary>LMArena's overall vision rating.</summary>
    public double? VisionRating { get; init; }
    /// <summary>Where the model's own scores place it among scored models, 0 to 100; null when nothing scored it.</summary>
    public double? Rank { get; init; }
    /// <summary>What running it locally takes, when the catalog looked it up (Hugging Face and the Ollama registry): each
    /// quantization's install name and download size, the encoder, and config.json's shape for the memory estimate.</summary>
    public LocalModelFacts? Local { get; init; }

    public string? HuggingFaceRepo => Names.HuggingFace;

    /// <summary>Whether it can run on the owner's own computers: a quantization to install was found, or its weights are
    /// published; false when its weights are not published; null when nothing says.</summary>
    public bool? LocallyHostable => Local?.LocallyHostable == true || OpenWeights == true ? true : OpenWeights == false ? false : null;

    /// <summary>The memory it takes in Ollama or llama.cpp at <paramref name="contextTokens"/> (Martlet's 8,192 by default), for
    /// <paramref name="quantization"/> (else Q4_K_M or the Ollama tag's); null when the catalog has no local facts for it.</summary>
    public LocalMemoryEstimate? Memory(string? quantization = null, int contextTokens = LocalModelMemory.DefaultContextTokens) =>
        Local?.Estimate(quantization, contextTokens);

    public CatalogFact Fact(string key) => Facts.TryGetValue(key, out var fact) ? fact : CatalogFact.Unknown;

    public bool? Sees => Fact(CatalogFacts.InputImage).Yes;
    public bool? Hears => Fact(CatalogFacts.InputAudio).Yes;
    public bool? TakesVideo => Fact(CatalogFacts.InputVideo).Yes;
    public bool? VideoAsFrames => Fact(CatalogFacts.InputVideoFrames).Yes;
    public bool? CallsTools => Fact(CatalogFacts.Tools).Yes;
    public bool? Reasons => Fact(CatalogFacts.Reasoning).Yes;
    /// <summary>Whether its weights are published, so it can run on the owner's own computers.</summary>
    public bool? OpenWeights => Fact(CatalogFacts.OpenWeights).Yes;
}

/// <summary>One server and model (the same key as <c>ModelAbilities</c>: a provider and its model ID): what that server takes
/// for the model (<see cref="Facts"/>, by the route order: the server's own metadata, the model's facts, then models.dev's
/// row for this provider), whether it is free, its context, when it expires and whether it is retired.</summary>
public sealed record CatalogRoute
{
    /// <summary>Martlet's provider ID ("openrouter", "nvidia-build", "google-gemini") or models.dev's ("groq").</summary>
    public required string Provider { get; init; }
    public required string ModelId { get; init; }
    public required string ModelKey { get; init; }
    public string? BaseUrl { get; init; }
    /// <summary>The source that is this server's own metadata (route level 2): OpenRouter's list or NVIDIA's page.</summary>
    public string? Server { get; init; }
    public bool? Free { get; init; }
    public string? FreeNote { get; init; }
    public int? Context { get; init; }
    public DateOnly? Expires { get; init; }
    /// <summary>Past its expiration date (OpenRouter's <c>expiration_date</c>).</summary>
    public bool Retired { get; init; }
    /// <summary>models.dev marks the row deprecated: it still answers, but is going away.</summary>
    public bool Deprecated { get; init; }
    public IReadOnlyDictionary<string, CatalogFact> Facts { get; init; } = new Dictionary<string, CatalogFact>();

    public CatalogFact Fact(string key) => Facts.TryGetValue(key, out var fact) ? fact : CatalogFact.Unknown;
}

/// <summary>A model found by a name, and how it was found ("OpenRouter ID", "Ollama name by family and size").</summary>
public sealed record CatalogMatch(CatalogModel Model, string How);

/// <summary>How smart a model is, for choosing between models. <see cref="Rating"/> is LMArena's rating, which Martlet may show
/// with <see cref="Credit"/>. <see cref="Rank"/> (0 to 100) only orders models and is never shown; show <see cref="Words"/>.
/// <see cref="From"/> says where it came from: the model's own scores, a scored model of the same family and size, models of
/// a similar active size and release date, or the quality tier from its size.</summary>
public sealed record CatalogSmartness
{
    public double? Rating { get; init; }
    public double? VisionRating { get; init; }
    public string? Credit { get; init; }
    public double? Rank { get; init; }
    /// <summary>1 to 5, like <c>ComponentOption.QualityTier</c>.</summary>
    public int Tier { get; init; }
    public required string Words { get; init; }
    public required string From { get; init; }

    internal static int TierOf(double rank) => rank switch { >= 90 => 5, >= 70 => 4, >= 40 => 3, >= 15 => 2, _ => 1 };

    internal static string WordsOf(int tier) => tier switch
    {
        5 => "among the smartest", 4 => "smarter than most", 3 => "about average", 2 => "below average", _ => "basic"
    };
}
