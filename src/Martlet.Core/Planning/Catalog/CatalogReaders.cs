using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;

namespace Martlet.Core.Planning;

/// <summary>Reads each catalog source's text into observations (docs/MODEL_CATALOG.md, Sources). These only parse: the daily
/// refresh (Martlet.Providers' ModelCatalogRefresh) downloads the text, with no key, a time limit and a size limit.</summary>
public static partial class CatalogReaders
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 32 };

    /// <summary>A hosted provider whose models.dev rows the catalog keeps, with Martlet's ID and its API's base URL.</summary>
    public sealed record RowProvider(string ModelsDev, string Id, string BaseUrl);

    /// <summary>The providers whose models.dev rows become routes: Martlet's named ones first, then well-known
    /// OpenAI-compatible ones a custom server can use.</summary>
    public static IReadOnlyList<RowProvider> RowProviders { get; } = Array.AsReadOnly<RowProvider>(
    [
        new("openrouter", ChatCompletionsEndpointCatalog.OpenRouterId, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl),
        new("nvidia", ChatCompletionsEndpointCatalog.NvidiaBuildId, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl),
        new("google", ChatCompletionsEndpointCatalog.GeminiId, ChatCompletionsEndpointCatalog.GeminiBaseUrl),
        new("openai", "openai", "https://api.openai.com/v1"),
        new("anthropic", "anthropic", "https://api.anthropic.com/v1"),
        new("groq", "groq", "https://api.groq.com/openai/v1"),
        new("deepinfra", "deepinfra", "https://api.deepinfra.com/v1/openai"),
        new("togetherai", "togetherai", "https://api.together.xyz/v1"),
        new("mistral", "mistral", "https://api.mistral.ai/v1"),
        new("cerebras", "cerebras", "https://api.cerebras.ai/v1"),
        new("fireworks-ai", "fireworks-ai", "https://api.fireworks.ai/inference/v1"),
        new("xai", "xai", "https://api.x.ai/v1"),
        new("deepseek", "deepseek", "https://api.deepseek.com"),
        new("ollama-cloud", "ollama-cloud", "https://ollama.com/v1"),
        new("huggingface", "huggingface", "https://router.huggingface.co/v1")
    ]);

    /// <summary>OpenRouter's <c>GET /api/v1/models</c>: inputs and outputs, context, tools and reasoning
    /// (<c>supported_parameters</c>), the Hugging Face repository, knowledge cutoff, expiration, whether it is free, and where
    /// its Artificial Analysis intelligence index ranks (<see cref="CatalogObservation.Rank"/>; the index itself is dropped).</summary>
    public static IReadOnlyList<CatalogObservation> OpenRouter(string json)
    {
        using var document = JsonDocument.Parse(json, Options);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("OpenRouter's model list has no data array.");
        var read = new List<(CatalogObservation Item, double? Index)>();
        foreach (var model in data.EnumerateArray())
        {
            if (Text(model, "id") is not { } id) continue;
            var facts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (model.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object)
            {
                Modalities(architecture, "input_modalities", facts, "input.", ["text", "image", "audio", "video"]);
                Modalities(architecture, "output_modalities", facts, "output.", ["text", "image", "audio"]);
            }
            if (Integer(model, "context_length") is { } context) facts[CatalogFacts.Context] = context.ToString(CultureInfo.InvariantCulture);
            if (model.TryGetProperty("supported_parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                var names = parameters.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()).ToHashSet();
                facts[CatalogFacts.Tools] = CatalogValues.Of(names.Contains("tools"));
                facts[CatalogFacts.Reasoning] = CatalogValues.Of(names.Contains("reasoning") || names.Contains("include_reasoning"));
            }
            if (Text(model, "knowledge_cutoff") is { } cutoff) facts[CatalogFacts.KnowledgeCutoff] = cutoff;
            var huggingFace = Text(model, "hugging_face_id");
            if (huggingFace is not null) facts[CatalogFacts.OpenWeights] = CatalogValues.Yes;
            bool? free = null;
            if (model.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object &&
                Price(pricing, "prompt") is { } prompt && Price(pricing, "completion") is { } completion)
                free = prompt == 0 && completion == 0;
            if (id.EndsWith(":free", StringComparison.Ordinal)) free = true;
            double? index = model.TryGetProperty("benchmarks", out var benchmarks) && benchmarks.ValueKind == JsonValueKind.Object &&
                benchmarks.TryGetProperty("artificial_analysis", out var analysis) && analysis.ValueKind == JsonValueKind.Object &&
                analysis.TryGetProperty("intelligence_index", out var intelligence) && intelligence.ValueKind == JsonValueKind.Number
                    ? intelligence.GetDouble() : null;
            read.Add((new()
            {
                Id = id, Name = Text(model, "name"), HuggingFace = huggingFace, Facts = facts, Free = free,
                Expires = Text(model, "expiration_date")
            }, index));
        }
        var scored = read.Where(r => r.Index is not null).Select(r => r.Index!.Value).OrderBy(i => i).ToList();
        return read.Select(r => r.Index is { } index ? r.Item with { Rank = Percentile(scored, index) } : r.Item).ToList();
    }

    /// <summary>models.dev's <c>models.json</c>: its own record for each model (inputs, outputs, tools, reasoning, open
    /// weights, license, release date, knowledge cutoff, context, and the Hugging Face repository of the weights).</summary>
    public static IReadOnlyList<CatalogObservation> ModelsDevModels(string json)
    {
        using var document = JsonDocument.Parse(json, Options);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("models.dev's models.json is not an object.");
        var items = new List<CatalogObservation>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var model = property.Value;
            if (model.ValueKind != JsonValueKind.Object) continue;
            var id = Text(model, "id") ?? property.Name;
            var facts = ModelsDevFacts(model);
            if (Text(model, "license") is { } license) facts[CatalogFacts.License] = license;
            if (Text(model, "release_date") is { } released) facts[CatalogFacts.ReleaseDate] = released;
            string? huggingFace = null;
            if (model.TryGetProperty("weights", out var weights) && weights.ValueKind == JsonValueKind.Array)
                huggingFace = weights.EnumerateArray().Select(w => Text(w, "url")).Select(HuggingFaceRepo).FirstOrDefault(r => r is not null);
            items.Add(new() { Id = id, Name = Text(model, "name"), HuggingFace = huggingFace, Facts = facts });
        }
        return items;
    }

    /// <summary>models.dev's <c>api.json</c>: each provider's rows for <see cref="RowProviders"/> (a route counts only its own
    /// provider's row), with the model they serve (<c>canonical_model_id</c>), whether they are free and deprecated.</summary>
    public static IReadOnlyList<CatalogObservation> ModelsDevRows(string json)
    {
        using var document = JsonDocument.Parse(json, Options);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("models.dev's api.json is not an object.");
        var items = new List<CatalogObservation>();
        foreach (var provider in RowProviders)
        {
            if (!document.RootElement.TryGetProperty(provider.ModelsDev, out var entry) || entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in models.EnumerateObject())
            {
                var model = property.Value;
                if (model.ValueKind != JsonValueKind.Object) continue;
                bool? free = model.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
                    Number(cost, "input") is { } input && Number(cost, "output") is { } output ? input == 0 && output == 0 : null;
                items.Add(new()
                {
                    Id = Text(model, "id") ?? property.Name, Name = Text(model, "name"), Provider = provider.Id, BaseUrl = provider.BaseUrl,
                    Canonical = Text(model, "canonical_model_id"), Facts = ModelsDevFacts(model), Free = free,
                    Deprecated = Text(model, "status") == "deprecated"
                });
            }
        }
        return items;
    }

    private static Dictionary<string, string> ModelsDevFacts(JsonElement model)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (model.TryGetProperty("modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Object)
        {
            Modalities(modalities, "input", facts, "input.", ["text", "image", "audio", "video"]);
            Modalities(modalities, "output", facts, "output.", ["text", "image", "audio"]);
        }
        if (Flag(model, "tool_call") is { } tools) facts[CatalogFacts.Tools] = CatalogValues.Of(tools);
        if (Flag(model, "reasoning") is { } reasoning) facts[CatalogFacts.Reasoning] = CatalogValues.Of(reasoning);
        if (Flag(model, "open_weights") is { } open) facts[CatalogFacts.OpenWeights] = CatalogValues.Of(open);
        if (Text(model, "knowledge") is { } knowledge) facts[CatalogFacts.KnowledgeCutoff] = knowledge;
        if (model.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object && Integer(limit, "context") is { } context and > 0)
            facts[CatalogFacts.Context] = context.ToString(CultureInfo.InvariantCulture);
        return facts;
    }

    /// <summary>The model pages NVIDIA's <c>models.md</c> links, each once and in order.</summary>
    public static IReadOnlyList<string> NvidiaLinks(string modelsMarkdown) =>
        NvidiaLink().Matches(modelsMarkdown).Select(m => m.Groups["page"].Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>One NVIDIA Build model page: null unless its header says <c>type: "endpoint"</c> (a hosted model). The
    /// <c>canonical</c> link gives the model ID; <c>## Specifications</c> the context, parameters, inputs and outputs;
    /// <c>## Capabilities</c> function calling and reasoning; the text the Hugging Face repository. Every NVIDIA Build model
    /// has a free trial tier (build.nvidia.com/llms.txt).</summary>
    public static CatalogObservation? NvidiaPage(string markdown, string page)
    {
        var header = FrontMatter().Match(markdown);
        if (!header.Success) return null;
        var fields = HeaderField().Matches(header.Groups["body"].Value).GroupBy(m => m.Groups["key"].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Groups["value"].Value, StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("type", out var type) || type != "endpoint" || !fields.TryGetValue("canonical", out var canonical)) return null;
        var id = CanonicalId().Match(canonical) is { Success: true } link ? link.Groups["id"].Value : null;
        if (id is null) return null;
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var specs = Bullets(Section(markdown, "Specifications") + "\n" + Section(markdown, "Capabilities"));
        var input = specs.GetValueOrDefault("Input") ?? (OldInput().Match(markdown) is { Success: true } old ? old.Groups["value"].Value : null);
        var output = specs.GetValueOrDefault("Output") ?? (OldOutput().Match(markdown) is { Success: true } oldOut ? oldOut.Groups["value"].Value : null);
        if (input is not null && Kinds(input) is { Count: > 0 } inputs)
            foreach (var kind in new[] { "text", "image", "audio", "video" }) facts["input." + kind] = CatalogValues.Of(inputs.Contains(kind));
        if (output is not null && Kinds(output) is { Count: > 0 } outputs)
            foreach (var kind in new[] { "text", "image", "audio" }) facts["output." + kind] = CatalogValues.Of(outputs.Contains(kind));
        var context = specs.GetValueOrDefault("Context Length") is { } length ? Count(length) : null;
        context ??= OldContext().Match(markdown) is { Success: true } isl ? Count(isl.Groups["value"].Value) : null;
        if (context is { } tokens) facts[CatalogFacts.Context] = CatalogValues.Of(Math.Round(tokens));
        if (specs.GetValueOrDefault("Parameters") is { } parameters && Count(parameters) is { } total)
            facts[CatalogFacts.ParametersTotal] = CatalogValues.Of(Math.Round(total / 1e9, 2));
        if (ActiveParameters().Match(markdown) is { Success: true } active && Count(active.Groups["value"].Value) is { } activeCount)
            facts[CatalogFacts.ParametersActive] = CatalogValues.Of(Math.Round(activeCount / 1e9, 2));
        if (specs.GetValueOrDefault("Function Calling") is { } calling) facts[CatalogFacts.Tools] = CatalogValues.Of(Supported(calling));
        if (specs.GetValueOrDefault("Reasoning") is { } reasoning) facts[CatalogFacts.Reasoning] = CatalogValues.Of(Supported(reasoning));
        var huggingFace = HuggingFaceLine().Match(markdown) is { Success: true } line ? HuggingFaceRepo(line.Groups["url"].Value) : null;
        huggingFace ??= HuggingFaceUrl().Matches(markdown).Select(m => HuggingFaceRepo(m.Value)).FirstOrDefault(r => r is not null);
        if (huggingFace is not null) facts[CatalogFacts.OpenWeights] = CatalogValues.Yes;
        return new()
        {
            Id = id, Name = fields.GetValueOrDefault("title"), HuggingFace = huggingFace, Facts = facts, Free = true, Page = page
        };
    }

    /// <summary>vLLM's supported_models.md: for each architecture, the inputs it takes (T, I, V, A). A footnoted input
    /// (<c>A&lt;sup&gt;*&lt;/sup&gt;</c>: only some variants) or one in brackets is <see cref="CatalogValues.Some"/>. Text-only
    /// tables give text alone (<see cref="CatalogObservation.TextOnly"/>).</summary>
    public static IReadOnlyList<CatalogObservation> Vllm(string markdown)
    {
        var items = new List<CatalogObservation>();
        var lines = markdown.Split('\n');
        var section = "";
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal)) section = line;
            if (!line.StartsWith("| Architecture", StringComparison.Ordinal)) continue;
            var columns = Cells(line);
            int architectureColumn = columns.FindIndex(c => c.StartsWith("Architecture", StringComparison.Ordinal)),
                modelsColumn = columns.FindIndex(c => c == "Models"), inputsColumn = columns.FindIndex(c => c == "Inputs"),
                examplesColumn = columns.FindIndex(c => c.StartsWith("Example HF Models", StringComparison.Ordinal));
            if (architectureColumn < 0 || examplesColumn < 0) continue;
            var textOnly = inputsColumn < 0;
            if (textOnly && !section.Contains("Text-only", StringComparison.OrdinalIgnoreCase)) continue;
            for (index += 2; index < lines.Length && lines[index].StartsWith('|'); index++)
            {
                var cells = Cells(lines[index].TrimEnd('\r'));
                if (cells.Count <= Math.Max(architectureColumn, Math.Max(examplesColumn, inputsColumn))) continue;
                var architectures = Backticked().Matches(cells[architectureColumn]).Select(m => m.Groups["v"].Value).ToList();
                var examples = Backticked().Matches(cells[examplesColumn]).Select(m => m.Groups["v"].Value).Where(v => v.Contains('/')).ToList();
                if (architectures.Count == 0) continue;
                var facts = new Dictionary<string, string>(StringComparer.Ordinal);
                Dictionary<string, string>? notes = null;
                if (textOnly)
                {
                    facts[CatalogFacts.InputText] = CatalogValues.Yes;
                    facts[CatalogFacts.InputImage] = facts[CatalogFacts.InputAudio] = facts[CatalogFacts.InputVideo] = CatalogValues.No;
                }
                else
                {
                    foreach (var (letter, key) in new[] { ('T', CatalogFacts.InputText), ('I', CatalogFacts.InputImage),
                        ('V', CatalogFacts.InputVideo), ('A', CatalogFacts.InputAudio) })
                    {
                        var marks = InputMark().Matches(cells[inputsColumn]).Where(m => m.Groups["m"].Value[0] == letter).ToList();
                        var some = marks.Count > 0 && marks.All(m => m.Groups["open"].Success || m.Groups["close"].Success ||
                            m.Groups["sup"].Value.Contains('*', StringComparison.Ordinal));
                        facts[key] = marks.Count == 0 ? CatalogValues.No : some ? CatalogValues.Some : CatalogValues.Yes;
                        if (some) (notes ??= new(StringComparer.Ordinal))[key] = "only some variants of the architecture (vLLM's footnote)";
                    }
                }
                var covers = modelsColumn >= 0 && modelsColumn < cells.Count ? Plain(cells[modelsColumn]) : null;
                items.AddRange(architectures.Select(architecture => new CatalogObservation
                {
                    Id = architecture, Covers = covers, Examples = examples, Facts = new(facts, StringComparer.Ordinal),
                    Notes = notes is null ? null : new(notes, StringComparer.Ordinal), TextOnly = textOnly
                }));
            }
            index--;
        }
        if (items.Count == 0) throw new FormatException("vLLM's supported models list has no architecture tables.");
        return items;
    }

    /// <summary>One page of LMArena's dataset rows (datasets-server <c>/rows</c>): the <c>overall</c> category's ratings.
    /// <paramref name="more"/> is false once a page holds another category or is short (the overall rows come first).</summary>
    public static IReadOnlyList<CatalogObservation> LmArena(string json, out bool more)
    {
        using var document = JsonDocument.Parse(json, Options);
        if (!document.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new FormatException("LMArena's dataset page has no rows.");
        var items = new List<CatalogObservation>();
        var count = 0;
        more = true;
        foreach (var wrapper in rows.EnumerateArray())
        {
            count++;
            if (!wrapper.TryGetProperty("row", out var row) || row.ValueKind != JsonValueKind.Object) continue;
            if (Text(row, "category") != "overall")
            {
                more = false;
                continue;
            }
            if (Text(row, "model_name") is not { } name || Number(row, "rating") is not { } rating) continue;
            var facts = Text(row, "license") is { } license && license != "Proprietary"
                ? new Dictionary<string, string>(StringComparer.Ordinal) { [CatalogFacts.License] = license } : null;
            items.Add(new()
            {
                Id = name, Name = Text(row, "organization"), Rating = Math.Round(rating, 1), Facts = facts,
                Votes = Number(row, "vote_count") is { } votes ? (int)votes : null
            });
        }
        if (count < 100) more = false;
        return items;
    }

    /// <summary>The "owner/name" of a Hugging Face model link, or null for any other link (a dataset, a space, a file).</summary>
    public static string? HuggingFaceRepo(string? url)
    {
        if (url is null) return null;
        var match = HuggingFaceUrl().Match(url);
        if (!match.Success) return null;
        var owner = match.Groups["owner"].Value;
        return owner is "datasets" or "spaces" or "docs" or "blog" or "papers" or "collections" or "models" or "organizations" or "api"
            ? null : owner + "/" + match.Groups["name"].Value.TrimEnd('.');
    }

    private static double Percentile(List<double> sorted, double value)
    {
        var below = sorted.Count(v => v < value);
        var same = sorted.Count(v => v == value);
        return Math.Round(100.0 * (below + same / 2.0) / sorted.Count, 1);
    }

    private static void Modalities(JsonElement owner, string property, Dictionary<string, string> facts, string prefix, string[] kinds)
    {
        if (!owner.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return;
        var listed = list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!.ToLowerInvariant()).ToHashSet();
        if (listed.Count == 0) return;
        foreach (var kind in kinds) facts[prefix + kind] = CatalogValues.Of(listed.Contains(kind));
    }

    private static HashSet<string> Kinds(string text)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in Parenthesis().Replace(text.ToLowerInvariant(), " ").Split([',', '/', '&', ';', '+'], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(w => w.Split(" and ", StringSplitOptions.RemoveEmptyEntries)).Select(w => w.Trim()))
        {
            if (word.StartsWith("text", StringComparison.Ordinal) || word == "string") kinds.Add("text");
            else if (word.StartsWith("image", StringComparison.Ordinal) || word == "picture") kinds.Add("image");
            else if (word.StartsWith("video", StringComparison.Ordinal)) kinds.Add("video");
            else if (word.StartsWith("audio", StringComparison.Ordinal) || word.StartsWith("speech", StringComparison.Ordinal)) kinds.Add("audio");
        }
        return kinds;
    }

    private static bool Supported(string value) => !value.Contains("not", StringComparison.OrdinalIgnoreCase) &&
        value.Contains("support", StringComparison.OrdinalIgnoreCase);

    /// <summary>A count in words: "262,144 tokens", "256K", "753B", "2.8T", "32,682,372,656".</summary>
    private static double? Count(string text)
    {
        var match = CountText().Match(text);
        if (!match.Success || !double.TryParse(match.Groups["n"].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var n)) return null;
        return match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "K" => n * 1024, "M" => n * 1e6, "B" => n * 1e9, "T" => n * 1e12, _ => n
        };
    }

    private static string Section(string markdown, string title)
    {
        var start = markdown.IndexOf("\n## " + title, StringComparison.Ordinal);
        if (start < 0) return "";
        var end = markdown.IndexOf("\n## ", start + 4, StringComparison.Ordinal);
        return end < 0 ? markdown[start..] : markdown[start..end];
    }

    private static Dictionary<string, string> Bullets(string text) =>
        Bullet().Matches(text).GroupBy(m => m.Groups["key"].Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Groups["value"].Value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static List<string> Cells(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

    private static string Plain(string cell) => Tag().Replace(cell, "").Replace("`", "", StringComparison.Ordinal).Trim();

    private static string? Text(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    private static long? Integer(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static double? Number(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static bool? Flag(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static decimal? Price(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) ? price : null;

    [GeneratedRegex(@"\]\((?<page>/[^)\s]+\.md)\)", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaLink();

    [GeneratedRegex(@"\A\s*---\r?\n(?<body>.*?)\r?\n---", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontMatter();

    [GeneratedRegex(@"^(?<key>[A-Za-z_]+):\s*""?(?<value>[^""\r\n]*)""?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderField();

    [GeneratedRegex(@"build\.nvidia\.com/(?<id>[^/\s""]+/[^/\s""]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalId();

    [GeneratedRegex(@"^\s*-\s*\*\*(?<key>[^*:]+):\*\*\s*(?<value>.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\*\*Input Type(?:s|\(s\))?:?\*\*:?\s*(?<value>[^\r\n<]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OldInput();

    [GeneratedRegex(@"\*\*Output Type(?:s|\(s\))?:?\*\*:?\s*(?<value>[^\r\n<]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OldOutput();

    [GeneratedRegex(@"\*\*Input Context Length[^*]*:\*\*\s*(?<value>[\d.,]+\s*[KkMm]?)", RegexOptions.CultureInvariant)]
    private static partial Regex OldContext();

    [GeneratedRegex(@"\*\*Active Parameters:?\*\*:?\s*(?<value>[\d.,]+\s*[BMTbmt])", RegexOptions.CultureInvariant)]
    private static partial Regex ActiveParameters();

    [GeneratedRegex(@"\*\*Huggingface:\*\*[^\r\n]*?\((?<url>https://huggingface\.co/[^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex HuggingFaceLine();

    [GeneratedRegex(@"https?://huggingface\.co/(?<owner>[A-Za-z0-9][\w.\-]*)/(?<name>[A-Za-z0-9][\w.\-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex HuggingFaceUrl();

    [GeneratedRegex(@"(?<n>\d[\d,]*(?:\.\d+)?)(?:\s*(?<unit>[KkMmBbTt])(?![A-Za-z]))?", RegexOptions.CultureInvariant)]
    private static partial Regex CountText();

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Parenthesis();

    [GeneratedRegex(@"`(?<v>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();

    [GeneratedRegex(@"(?<open>\(\s*)?(?<![A-Za-z])(?<m>[TIVA])(?![A-Za-z])(?:<sup>(?<sup>[^<]*)</sup>)?(?<close>\s*\))?", RegexOptions.CultureInvariant)]
    private static partial Regex InputMark();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();
}
