using System.Globalization;

namespace Martlet.Core.Planning;

/// <summary>Martlet's internal model catalog (docs/MODEL_CATALOG.md): one record for each model and each hosted route, joined
/// from the sources in <see cref="ModelCatalogData"/> and worked out by <see cref="CatalogResolver"/>. Look a model up by any
/// of its names with <see cref="Find"/>, a route with <see cref="Route"/>, and how smart a model is with
/// <see cref="Smartness(CatalogModel)"/>. Building it reads no network and nothing on the reply path waits for it.</summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<string, CatalogModel> byKey;
    private readonly Dictionary<string, CatalogMatch> byName;
    private readonly Dictionary<string, List<CatalogModel>> byComparison;
    private readonly Dictionary<string, List<CatalogRoute>> routesByModel;
    private readonly Dictionary<string, CatalogRoute> routeIndex;

    private static readonly Lazy<ModelCatalog> EmptyCatalog = new(() => Build(new()));

    public static ModelCatalog Empty => EmptyCatalog.Value;

    public DateTimeOffset Built { get; }
    /// <summary>When the catalog read each source (the date of every answer that has no date of its own).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> SourceDates { get; }
    public IReadOnlyList<CatalogModel> Models { get; }
    public IReadOnlyList<CatalogRoute> Routes { get; }

    /// <summary>The local models Martlet itself installs (FootprintCatalog's Ollama tags) and their Hugging Face repositories,
    /// so they have records even when no hosted catalog lists them.</summary>
    public static IReadOnlyList<CatalogObservation> MartletModels { get; } = Array.AsReadOnly<CatalogObservation>(
    [
        new() { Id = "google/gemma-4-E2B-it", HuggingFace = "google/gemma-4-E2B-it", Name = "Gemma 4 E2B", Ollama = ["gemma4:e2b"] },
        new() { Id = "google/gemma-4-E4B-it", HuggingFace = "google/gemma-4-E4B-it", Name = "Gemma 4 E4B", Ollama = ["gemma4:e4b"] },
        new() { Id = "google/gemma-4-12B-it", HuggingFace = "google/gemma-4-12B-it", Name = "Gemma 4 12B", Ollama = ["gemma4:12b"] },
        new() { Id = "google/gemma-4-26B-A4B-it", HuggingFace = "google/gemma-4-26B-A4B-it", Name = "Gemma 4 26B A4B", Ollama = ["gemma4:26b"] },
        new() { Id = "Qwen/Qwen3.5-4B", HuggingFace = "Qwen/Qwen3.5-4B", Name = "Qwen3.5 4B", Ollama = ["qwen3.5:4b"] },
        new() { Id = "Qwen/Qwen2.5-VL-7B-Instruct", HuggingFace = "Qwen/Qwen2.5-VL-7B-Instruct", Name = "Qwen2.5-VL 7B", Ollama = ["qwen2.5vl:7b"] }
    ]);

    private ModelCatalog(DateTimeOffset built, IReadOnlyDictionary<string, DateTimeOffset> dates, List<CatalogModel> models, List<CatalogRoute> routes)
    {
        Built = built;
        SourceDates = dates;
        Models = models.AsReadOnly();
        Routes = routes.AsReadOnly();
        byKey = models.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        routesByModel = routes.GroupBy(r => r.ModelKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        routeIndex = new(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes) routeIndex.TryAdd(route.Provider + "|" + route.ModelId, route);
        byName = new(StringComparer.OrdinalIgnoreCase);
        void Name(string? name, CatalogModel model, string how)
        {
            if (!string.IsNullOrWhiteSpace(name)) byName.TryAdd(name.Trim(), new(model, how));
        }
        foreach (var model in models) Name(model.Key, model, model.HuggingFaceRepo is null ? "model ID" : "Hugging Face repository");
        foreach (var model in models) Name(model.Names.HuggingFace, model, "Hugging Face repository");
        foreach (var model in models) Name(model.Names.OpenRouter, model, "OpenRouter ID");
        foreach (var model in models) Name(model.Names.NvidiaBuild, model, "NVIDIA Build ID");
        foreach (var model in models) Name(model.Names.ModelsDev, model, "models.dev ID");
        foreach (var model in models) foreach (var tag in model.Names.Ollama) Name(tag, model, "Ollama tag");
        foreach (var route in routes) if (byKey.TryGetValue(route.ModelKey, out var served)) Name(route.ModelId, served, $"{route.Provider} model ID");
        foreach (var model in models) Name(model.Names.LmArena, model, "LMArena name");
        byComparison = new(StringComparer.Ordinal);
        foreach (var model in models)
            foreach (var name in new[] { model.Key, model.Names.HuggingFace, model.Names.OpenRouter, model.Names.NvidiaBuild, model.Names.ModelsDev })
                if (name is not null && CatalogNaming.Key(name) is { Length: > 0 } key)
                {
                    if (!byComparison.TryGetValue(key, out var list)) byComparison[key] = list = [];
                    if (!list.Contains(model)) list.Add(model);
                }
    }

    public CatalogModel? Model(string key) => byKey.GetValueOrDefault(key);

    /// <summary>The model with this name on any route or source: its Hugging Face repository, an OpenRouter, NVIDIA Build,
    /// models.dev or provider model ID (":free" and other OpenRouter variants included), an Ollama tag (Martlet's own, or
    /// "gemma4:26b" by family and size), an Ollama "hf.co/{repo}:{quant}" name, or an LMArena name. Null when none.</summary>
    public CatalogMatch? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Trim();
        if (byName.TryGetValue(text, out var exact)) return exact;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf('/', StringComparison.Ordinal) is var slash && slash > 0 && slash < colon &&
            !text.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase) && byName.TryGetValue(text[..colon], out var variant))
            return variant with { How = variant.How + " (a variant)" };
        if (CatalogNaming.HuggingFaceOf(text) is { } repo)
        {
            if (byName.TryGetValue(repo, out var hosted)) return hosted with { How = "Hugging Face repository (Ollama hf.co name)" };
            var leaf = CatalogNaming.Leaf(repo);
            var same = Models.Where(m => m.HuggingFaceRepo is { } r && string.Equals(CatalogNaming.Leaf(r), leaf, StringComparison.OrdinalIgnoreCase)).ToList();
            if (same.Count == 1) return new(same[0], "the same model name from another publisher");
            return BySize(CatalogNaming.CompactFamily(leaf), CatalogNaming.MainSize(leaf), "a Hugging Face model's family and size");
        }
        if (!text.Contains('/') && CatalogNaming.Ollama(text) is { } ollama && BySize(ollama.Family, ollama.Size, "Ollama name, by family and size") is { } local)
            return local;
        if (byComparison.TryGetValue(CatalogNaming.Key(text), out var similar) && similar.Count == 1) return new(similar[0], "a similar name");
        return null;
    }

    private CatalogMatch? BySize(string family, string? size, string how)
    {
        if (family.Length == 0 || size is null) return null;
        var candidates = Models.Where(m => m.HuggingFaceRepo is not null && m.Family == family &&
            CatalogNaming.MainSize(m.HuggingFaceRepo) == size).ToList();
        if (candidates.Count == 0) return null;
        // Prefer the maker's instruction-tuned model, then the one most sources know.
        var best = candidates.OrderByDescending(m => Tuned(m.HuggingFaceRepo!))
            .ThenByDescending(m => (m.Names.OpenRouter is null ? 0 : 1) + (m.Names.NvidiaBuild is null ? 0 : 1) + (m.Names.ModelsDev is null ? 0 : 1) + m.Names.Ollama.Count)
            .ToList();
        return best.Count == 1 || Tuned(best[0].HuggingFaceRepo!) != Tuned(best[1].HuggingFaceRepo!) || Known(best[0]) > Known(best[1])
            ? new(best[0], how) : null;

        static int Known(CatalogModel m) => (m.Names.OpenRouter is null ? 0 : 1) + (m.Names.NvidiaBuild is null ? 0 : 1) +
            (m.Names.ModelsDev is null ? 0 : 1) + m.Names.Ollama.Count;
        static bool Tuned(string repo) => CatalogNaming.Leaf(repo).ToLowerInvariant() is var leaf &&
            (leaf.EndsWith("-it", StringComparison.Ordinal) || leaf.Contains("instruct", StringComparison.Ordinal));
    }

    public IReadOnlyList<CatalogRoute> RoutesOf(CatalogModel model) =>
        routesByModel.TryGetValue(model.Key, out var routes) ? routes : [];

    /// <summary>The route for <paramref name="modelId"/> on a provider, named by Martlet's provider ID ("openrouter",
    /// "nvidia-build", "google-gemini", models.dev's "groq") or by its API's base URL.</summary>
    public CatalogRoute? Route(string providerOrBaseUrl, string modelId)
    {
        var provider = ProviderOf(providerOrBaseUrl) ?? providerOrBaseUrl.Trim();
        return routeIndex.GetValueOrDefault(provider + "|" + modelId.Trim());
    }

    /// <summary>Martlet's provider ID for an API base URL ("https://openrouter.ai/api/v1" is "openrouter"), or the ID itself.</summary>
    public static string? ProviderOf(string providerOrBaseUrl)
    {
        var text = providerOrBaseUrl.Trim().TrimEnd('/');
        return CatalogReaders.RowProviders.FirstOrDefault(p => string.Equals(p.Id, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.BaseUrl.TrimEnd('/'), text, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>A route fact with a Martlet test on that route as the first answer (route level 1): what model-abilities.json
    /// found (<paramref name="tested"/>, from <paramref name="testSource"/> at <paramref name="testedAt"/>).</summary>
    public CatalogFact RouteFact(CatalogRoute route, string key, bool? tested, string? testSource = null, DateTimeOffset? testedAt = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        var fact = route.Fact(key);
        if (tested is not { } value) return fact;
        var answers = fact.Answers.Where(a => a.Source is not (CatalogSources.MartletTest or CatalogResolver.ModelSource)).Append(new CatalogAnswer
        {
            Source = CatalogSources.MartletTest, Value = CatalogValues.Of(value), Note = testSource, Checked = testedAt
        }).ToList();
        var model = Model(route.ModelKey)?.Fact(key) ?? CatalogFact.Unknown;
        return CatalogResolver.ResolveRoute(key, answers, route.Server, model);
    }

    /// <summary>How smart <paramref name="model"/> is: its own scores (LMArena, and Artificial Analysis's index from
    /// OpenRouter's list, used only to rank), else a scored model of the same family and size, else scored models of a similar
    /// active size and release date, else the quality tier from its size (<see cref="ServedModels.Tier"/>).</summary>
    public CatalogSmartness Smartness(CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var credit = model.Rating is not null || model.VisionRating is not null ? CatalogSources.LmArenaCredit : null;
        CatalogSmartness Ranked(double rank, string from)
        {
            var tier = CatalogSmartness.TierOf(rank);
            return new()
            {
                Rating = model.Rating, VisionRating = model.VisionRating, Credit = credit, Rank = rank, Tier = tier,
                Words = CatalogSmartness.WordsOf(tier), From = from
            };
        }
        if (model.Rank is { } own) return Ranked(own, "its own scores");
        if (model.Family.Length > 0 && model.Size.Length > 0 && Models.Where(o => o.Rank is not null && o.Key != model.Key &&
                o.Family == model.Family && o.Size == model.Size).OrderByDescending(o => o.RatingVotes ?? 0).FirstOrDefault() is { } twin)
            return Ranked(twin.Rank!.Value, $"{twin.Name}, a scored model of the same family and size");
        if (Similar(model) is { } similar) return Ranked(similar, "scored models of a similar active size and release date");
        var sized = ServedModels.Tier(model.HuggingFaceRepo ?? model.Key);
        return new()
        {
            Rating = model.Rating, VisionRating = model.VisionRating, Credit = credit, Tier = sized, Words = CatalogSmartness.WordsOf(sized),
            From = "the quality tier from its size"
        };
    }

    /// <summary>How smart the model with this name is; a model the catalog doesn't know gets the quality tier from its name.</summary>
    public CatalogSmartness Smartness(string name)
    {
        if (Find(name) is { } match) return Smartness(match.Model);
        var tier = ServedModels.Tier(name);
        return new() { Tier = tier, Words = CatalogSmartness.WordsOf(tier), From = "the quality tier from its name" };
    }

    private double? Similar(CatalogModel model)
    {
        if (Active(model) is not { } active || active <= 0) return null;
        var released = Released(model);
        var ranks = Models.Where(o => o.Rank is not null && o.Key != model.Key && Active(o) is { } a && a >= active / 1.5 && a <= active * 1.5 &&
                (released is null || Released(o) is { } r && Math.Abs(r.DayNumber - released.Value.DayNumber) <= 365))
            .Select(o => o.Rank!.Value).OrderBy(r => r).ToList();
        if (ranks.Count < 3) return null;
        return ranks.Count % 2 == 1 ? ranks[ranks.Count / 2] : (ranks[ranks.Count / 2 - 1] + ranks[ranks.Count / 2]) / 2;
    }

    private static double? Active(CatalogModel model) => model.Fact(CatalogFacts.ParametersActive).Number;

    private static DateOnly? Released(CatalogModel model)
    {
        var value = model.Fact(CatalogFacts.ReleaseDate).Value;
        if (value is null) return null;
        if (value.Length == 7) value += "-01";
        return DateOnly.TryParseExact(value.Length >= 10 ? value[..10] : value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
    }

    // ---- Building -------------------------------------------------------------------------------------------------------

    private sealed class Draft(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; set; } = name;
        public string? HuggingFace, OpenRouter, Nvidia, ModelsDev, Arena, ArenaVision;
        public List<string> Ollama { get; } = [];
        public Dictionary<string, List<CatalogAnswer>> Answers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AnsweredBy { get; } = new(StringComparer.Ordinal);
        public double? AnalysisRank, ArenaRank, Rating, VisionRating;
        public int? Votes;
        public LocalModelFacts? Local;

        public void Add(string source, CatalogObservation item, string? note = null)
        {
            if (item.Facts is null || !AnsweredBy.Add(source)) return;
            foreach (var (key, value) in item.Facts)
                Add(key, new() { Source = source, Value = value, Note = item.Notes?.GetValueOrDefault(key) ?? note });
        }

        public void Add(string key, CatalogAnswer answer)
        {
            if (!Answers.TryGetValue(key, out var list)) Answers[key] = list = [];
            list.Add(answer);
        }
    }

    private sealed class RouteDraft(string provider, string modelId, Draft model)
    {
        public string Provider { get; } = provider;
        public string ModelId { get; } = modelId;
        public Draft Model { get; } = model;
        public string? BaseUrl, Server, FreeNote, Expires;
        public bool? Free;
        public bool Deprecated;
        public Dictionary<string, List<CatalogAnswer>> Answers { get; } = new(StringComparer.Ordinal);

        public void Add(string source, CatalogObservation item)
        {
            if (item.Facts is null) return;
            foreach (var (key, value) in item.Facts)
            {
                if (!Answers.TryGetValue(key, out var list)) Answers[key] = list = [];
                if (list.All(a => a.Source != source)) list.Add(new() { Source = source, Value = value, Note = item.Notes?.GetValueOrDefault(key) });
            }
        }
    }

    /// <summary>Joins <paramref name="data"/> into model and route records and works out every fact. With
    /// <paramref name="architectures"/> (a Hugging Face repository's config.json <c>architectures</c>), vLLM's table is joined by
    /// the exact architecture instead of by family name.</summary>
    public static ModelCatalog Build(ModelCatalogData data, IReadOnlyDictionary<string, IReadOnlyList<string>>? architectures = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var drafts = new Dictionary<string, Draft>(StringComparer.OrdinalIgnoreCase);
        var byHuggingFace = new Dictionary<string, Draft>(StringComparer.OrdinalIgnoreCase);
        Draft Get(string? huggingFace, string key, string? name)
        {
            if (huggingFace is not null && byHuggingFace.TryGetValue(huggingFace, out var found)) return found;
            if (!drafts.TryGetValue(huggingFace ?? key, out found) && (huggingFace is null || !drafts.TryGetValue(key, out found)))
                drafts[huggingFace ?? key] = found = new(huggingFace ?? key, name ?? CatalogNaming.Leaf(huggingFace ?? key));
            Attach(found, huggingFace);
            return found;
        }
        void Attach(Draft draft, string? huggingFace)
        {
            if (huggingFace is null || draft.HuggingFace is not null || byHuggingFace.ContainsKey(huggingFace)) return;
            draft.HuggingFace = huggingFace;
            byHuggingFace[huggingFace] = draft;
        }
        IReadOnlyList<CatalogObservation> Items(string source) => data.Block(source)?.Items ?? [];

        foreach (var seed in MartletModels)
        {
            var draft = Get(seed.HuggingFace, seed.Id, seed.Name);
            foreach (var tag in seed.Ollama ?? []) if (!draft.Ollama.Contains(tag, StringComparer.OrdinalIgnoreCase)) draft.Ollama.Add(tag);
        }

        var modelsDev = new Dictionary<string, Draft>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(CatalogSources.ModelsDev))
        {
            var draft = Get(item.HuggingFace, item.Id, item.Name);
            draft.ModelsDev ??= item.Id;
            if (item.Name is { } name) draft.Name = name;
            draft.Add(CatalogSources.ModelsDev, item);
            modelsDev.TryAdd(item.Id, draft);
        }

        var rows = Items(CatalogSources.ModelsDevRows);
        var canonical = rows.Where(r => r.Canonical is not null && r.Provider is not null)
            .GroupBy(r => r.Provider + "|" + r.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Canonical!, StringComparer.OrdinalIgnoreCase);
        Draft? Canonical(string provider, string id) =>
            canonical.TryGetValue(provider + "|" + id, out var model) ? modelsDev.GetValueOrDefault(model) : null;

        var routes = new Dictionary<string, RouteDraft>(StringComparer.OrdinalIgnoreCase);
        var openRouter = Items(CatalogSources.OpenRouter);
        var openRouterIds = openRouter.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in openRouter)
        {
            var baseId = OpenRouterBase(item.Id);
            var draft = Canonical(ChatCompletionsEndpointCatalogIds.OpenRouter, baseId) ?? Canonical(ChatCompletionsEndpointCatalogIds.OpenRouter, item.Id);
            if (draft is not null) Attach(draft, item.HuggingFace);
            draft ??= Get(item.HuggingFace, baseId, Plain(item.Name));
            draft.OpenRouter ??= baseId;
            if (item.Id == baseId || !openRouterIds.Contains(baseId)) draft.Add(CatalogSources.OpenRouter, item);
            if (item.Rank is { } rank) draft.AnalysisRank = Math.Max(draft.AnalysisRank ?? 0, rank);
            var route = routes[ChatCompletionsEndpointCatalogIds.OpenRouter + "|" + item.Id] =
                new(ChatCompletionsEndpointCatalogIds.OpenRouter, item.Id, draft)
                {
                    BaseUrl = Settings.ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, Server = CatalogSources.OpenRouter, Free = item.Free,
                    Expires = item.Expires
                };
            route.Add(CatalogSources.OpenRouter, item);
        }

        foreach (var item in Items(CatalogSources.NvidiaBuild))
        {
            var draft = Canonical(ChatCompletionsEndpointCatalogIds.Nvidia, item.Id);
            if (draft is not null) Attach(draft, item.HuggingFace);
            draft ??= Get(item.HuggingFace, item.Id, item.Name);
            draft.Nvidia ??= item.Id;
            draft.Add(CatalogSources.NvidiaBuild, item);
            var route = routes[ChatCompletionsEndpointCatalogIds.Nvidia + "|" + item.Id] = new(ChatCompletionsEndpointCatalogIds.Nvidia, item.Id, draft)
            {
                BaseUrl = Settings.ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, Server = CatalogSources.NvidiaBuild, Free = true,
                FreeNote = "NVIDIA Build's free trial tier"
            };
            route.Add(CatalogSources.NvidiaBuild, item);
        }

        foreach (var row in rows)
        {
            if (row.Provider is null || row.Canonical is null || modelsDev.GetValueOrDefault(row.Canonical) is not { } draft) continue;
            var key = row.Provider + "|" + row.Id;
            if (row.Provider is ChatCompletionsEndpointCatalogIds.OpenRouter or ChatCompletionsEndpointCatalogIds.Nvidia)
            {
                // These providers' own lists decide which routes exist; models.dev's rows only add their answer (level 4).
                if (routes.TryGetValue(key, out var listed)) listed.Add(CatalogSources.ModelsDevRows, row);
                continue;
            }
            if (!routes.TryGetValue(key, out var route))
                routes[key] = route = new(row.Provider, row.Id, draft) { BaseUrl = row.BaseUrl, Free = row.Free, Deprecated = row.Deprecated };
            route.Add(CatalogSources.ModelsDevRows, row);
        }

        foreach (var item in Items(CatalogSources.HuggingFace))
        {
            if (item.HuggingFace is null) continue;
            var draft = Get(item.HuggingFace, item.HuggingFace, item.Local?.HuggingFaceRepo);
            draft.Local ??= item.Local;
            foreach (var tag in item.Ollama ?? []) if (!draft.Ollama.Contains(tag, StringComparer.OrdinalIgnoreCase)) draft.Ollama.Add(tag);
            if (item.Facts is null || !draft.AnsweredBy.Add(CatalogSources.HuggingFace)) continue;
            foreach (var (key, value) in item.Facts)
            {
                var input = key.StartsWith("input.", StringComparison.Ordinal);
                var source = !input ? CatalogSources.HuggingFace
                    : item.Notes?.ContainsKey(key) == true ? CatalogSources.OllamaRegistry : CatalogSources.ConfigJson;
                draft.Add(key, new() { Source = source, Value = value, Note = item.Notes?.GetValueOrDefault(key) });
            }
        }

        JoinVllm(drafts.Values, Items(CatalogSources.Vllm), architectures);
        JoinArena(drafts.Values, Items(CatalogSources.LmArenaText), vision: false);
        JoinArena(drafts.Values, Items(CatalogSources.LmArenaVision), vision: true);

        var models = new List<CatalogModel>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finished = new Dictionary<Draft, CatalogModel>();
        foreach (var draft in drafts.Values)
        {
            var naming = draft.HuggingFace ?? draft.OpenRouter ?? draft.Nvidia ?? draft.Key;
            var (total, active) = CatalogNaming.Parameters(naming);
            if (total is { } t) draft.Add(CatalogFacts.ParametersTotal, new() { Source = CatalogSources.Name, Value = CatalogValues.Of(t) });
            if (active is { } a) draft.Add(CatalogFacts.ParametersActive, new() { Source = CatalogSources.Name, Value = CatalogValues.Of(a) });
            var facts = new Dictionary<string, CatalogFact>(StringComparer.Ordinal);
            foreach (var (key, answers) in draft.Answers)
                if (CatalogFacts.ModelFacts.Contains(key)) facts[key] = CatalogResolver.ResolveModel(key, answers);
            if (facts.GetValueOrDefault(CatalogFacts.InputImage)?.Value is { } sees && sees is CatalogValues.Yes or CatalogValues.No)
                facts[CatalogFacts.InputVideoFrames] = Frames(sees);
            double?[] ranks = [draft.AnalysisRank, draft.ArenaRank];
            var model = new CatalogModel
            {
                Key = draft.HuggingFace ?? draft.Key, Name = draft.Name,
                Names = new()
                {
                    HuggingFace = draft.HuggingFace, OpenRouter = draft.OpenRouter, NvidiaBuild = draft.Nvidia, ModelsDev = draft.ModelsDev,
                    LmArena = draft.Arena, LmArenaVision = draft.ArenaVision, Ollama = draft.Ollama.ToList()
                },
                Family = CatalogNaming.CompactFamily(naming), Size = CatalogNaming.Size(naming), Facts = facts,
                Rating = draft.Rating, RatingVotes = draft.Votes, VisionRating = draft.VisionRating, Local = draft.Local,
                Rank = ranks.Any(r => r is not null) ? Math.Round(ranks.Where(r => r is not null).Average()!.Value, 1) : null
            };
            if (!keys.Add(model.Key)) model = model with { Key = draft.Key };
            keys.Add(model.Key);
            models.Add(model);
            finished[draft] = model;
        }

        var built = data.Built == default ? DateTimeOffset.UtcNow : data.Built;
        var today = DateOnly.FromDateTime(built.UtcDateTime);
        var routeList = new List<CatalogRoute>();
        foreach (var route in routes.Values)
        {
            var model = finished[route.Model];
            var facts = new Dictionary<string, CatalogFact>(StringComparer.Ordinal);
            foreach (var key in CatalogFacts.RouteFacts)
            {
                if (key == CatalogFacts.InputVideoFrames) continue;
                var answers = route.Answers.GetValueOrDefault(key) ?? [];
                var fact = CatalogResolver.ResolveRoute(key, answers, route.Server, model.Fact(key));
                if (fact.Answers.Count > 0) facts[key] = fact;
            }
            if (facts.GetValueOrDefault(CatalogFacts.InputImage)?.Value is { } sees && sees is CatalogValues.Yes or CatalogValues.No)
                facts[CatalogFacts.InputVideoFrames] = Frames(sees);
            DateOnly? expires = route.Expires is { Length: >= 10 } text &&
                DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
            var context = facts.GetValueOrDefault(CatalogFacts.Context)?.Number;
            routeList.Add(new()
            {
                Provider = route.Provider, ModelId = route.ModelId, ModelKey = model.Key, BaseUrl = route.BaseUrl, Server = route.Server,
                Free = route.Free, FreeNote = route.FreeNote ?? (route.Free == true ? "no price for input or output" : null),
                Context = context is { } c and > 0 and < int.MaxValue ? (int)c : null, Expires = expires, Retired = expires < today,
                Deprecated = route.Deprecated, Facts = facts
            });
        }

        var dates = data.Sources.ToDictionary(s => s.Key, s => s.Value.Read, StringComparer.Ordinal);
        return new(built, dates, models.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToList(),
            routeList.OrderBy(r => r.Provider, StringComparer.Ordinal).ThenBy(r => r.ModelId, StringComparer.OrdinalIgnoreCase).ToList());

        static CatalogFact Frames(string sees) => new()
        {
            Value = sees, From = CatalogResolver.ModelSource,
            Answers = [new() { Source = CatalogResolver.ModelSource, Value = sees, Note = "video can go as frames when it sees pictures" }]
        };
    }

    private static void JoinVllm(IEnumerable<Draft> drafts, IReadOnlyList<CatalogObservation> rows,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? architectures)
    {
        if (rows.Count == 0) return;
        var byArchitecture = rows.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var byExample = rows.SelectMany(r => (r.Examples ?? []).Select(e => (Example: e, Row: r)))
            .GroupBy(x => x.Example, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList(), StringComparer.OrdinalIgnoreCase);
        var byFamily = rows.Where(r => !r.TextOnly).SelectMany(r => (r.Examples ?? []).Select(e => (Family: CatalogNaming.CompactFamily(e), Row: r)))
            .Where(x => x.Family.Length >= 3).GroupBy(x => x.Family, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Row).Distinct().ToList(), StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            if (draft.HuggingFace is not { } repo) continue;
            List<CatalogObservation>? matched = null;
            string how;
            if (architectures is not null && architectures.TryGetValue(repo, out var named) &&
                named.SelectMany(a => byArchitecture.GetValueOrDefault(a) ?? []).ToList() is { Count: > 0 } exact)
                (matched, how) = (exact, "its config.json architecture");
            else if (byExample.TryGetValue(repo, out var examples))
                (matched, how) = (examples.Any(r => !r.TextOnly) ? examples.Where(r => !r.TextOnly).ToList() : examples, "an example vLLM names");
            else if (byFamily.TryGetValue(CatalogNaming.CompactFamily(repo), out var family))
                (matched, how) = (family, "by family name");
            else continue;
            var architecturesText = string.Join(", ", matched.Select(r => r.Id).Distinct(StringComparer.Ordinal));
            var source = how == "by family name" ? CatalogSources.VllmFamily : CatalogSources.Vllm;
            foreach (var key in new[] { CatalogFacts.InputText, CatalogFacts.InputImage, CatalogFacts.InputVideo, CatalogFacts.InputAudio })
            {
                var values = matched.Select(r => r.Fact(key)).Where(v => v is not null).Distinct(StringComparer.Ordinal).ToList();
                if (values.Count == 0) continue;
                var note = values.Count > 1 ? $"{architecturesText} ({how}) differ"
                    : matched.Select(r => r.Notes?.GetValueOrDefault(key)).FirstOrDefault(n => n is not null) is { } footnote
                        ? $"{architecturesText} ({how}): {footnote}" : $"{architecturesText} ({how})";
                draft.Add(key, new() { Source = source, Value = values.Count == 1 ? values[0]! : CatalogValues.Some, Note = note });
            }
        }
    }

    private static void JoinArena(IEnumerable<Draft> drafts, IReadOnlyList<CatalogObservation> rows, bool vision)
    {
        if (rows.Count == 0) return;
        var index = new Dictionary<string, List<Draft>>(StringComparer.Ordinal);
        foreach (var draft in drafts)
            foreach (var name in new[] { draft.Key, draft.HuggingFace, draft.OpenRouter, draft.Nvidia, draft.ModelsDev })
                if (name is not null && CatalogNaming.Key(name) is { Length: > 0 } key)
                {
                    if (!index.TryGetValue(key, out var list)) index[key] = list = [];
                    if (!list.Contains(draft)) list.Add(draft);
                }
        var ratings = rows.Where(r => r.Rating is not null).Select(r => r.Rating!.Value).OrderBy(r => r).ToList();
        foreach (var row in rows)
        {
            if (row.Rating is not { } rating || !index.TryGetValue(CatalogNaming.Key(row.Id), out var found) || found.Count != 1) continue;
            var draft = found[0];
            if (vision)
            {
                if (draft.VisionRating is null || rating > draft.VisionRating) (draft.VisionRating, draft.ArenaVision) = (rating, row.Id);
                continue;
            }
            if (draft.Rating is not null && rating <= draft.Rating) continue;
            (draft.Rating, draft.Arena, draft.Votes) = (rating, row.Id, row.Votes);
            var below = ratings.Count(r => r < rating);
            draft.ArenaRank = Math.Round(100.0 * (below + ratings.Count(r => r == rating) / 2.0) / ratings.Count, 1);
            if (row.Facts?.GetValueOrDefault(CatalogFacts.License) is { } license)
                draft.Add(CatalogFacts.License, new() { Source = CatalogSources.LmArenaText, Value = license });
        }
    }

    private static string OpenRouterBase(string id)
    {
        var colon = id.IndexOf(':');
        return colon > 0 ? id[..colon] : id;
    }

    /// <summary>OpenRouter's names start with the maker ("Google: Gemma 4 26B A4B "); the catalog keeps the model's name.</summary>
    private static string? Plain(string? name)
    {
        if (name is null) return null;
        var colon = name.IndexOf(": ", StringComparison.Ordinal);
        return (colon > 0 ? name[(colon + 2)..] : name).Trim();
    }

    private static class ChatCompletionsEndpointCatalogIds
    {
        public const string OpenRouter = Settings.ChatCompletionsEndpointCatalog.OpenRouterId;
        public const string Nvidia = Settings.ChatCompletionsEndpointCatalog.NvidiaBuildId;
    }
}
