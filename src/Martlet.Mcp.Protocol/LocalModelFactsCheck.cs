using System.IO;
using System.Net.Http;
using Martlet.Core.Planning;
using Martlet.Providers.LocalModels;

namespace Martlet.Mcp;

/// <summary>local_model_facts: what an open-weight model needs to run locally, through the production
/// <see cref="LocalModelFactsReader"/> (Hugging Face's model API, <c>config.json</c> and GGUF search; the Ollama registry, never
/// ollama.com) and <see cref="LocalModelMemory"/>: parameters, inputs from config.json, files for each quantization, install names, a
/// memory estimate at a context, a rough speed for a graphics card, Martlet's footprint catalog entry for the same Ollama tag,
/// and what Ollama measured on this PC (model-memory.json in a data directory). fixture=true runs the same code on a FIXTURE
/// transport (NOT the real services) and checks the estimate against the measured Gemma 4 and Qwen3.5 numbers. Keyless; downloads
/// no model; saves nothing.</summary>
internal static class LocalModelFactsCheck
{
    internal static async Task<object> RunAsync(string? model, string? huggingFaceRepo, string? ollamaTag, string? quantization,
        int? contextTokens, string? card, double? bandwidthGbps, string? dataDirectory, bool fixture, CancellationToken cancellation)
    {
        var context = Math.Clamp(contextTokens ?? LocalModelMemory.DefaultContextTokens, 256, 1_048_576);
        if (fixture && model is null && huggingFaceRepo is null && ollamaTag is null) return await FixtureAsync(context, cancellation);
        if (model is null && huggingFaceRepo is null && ollamaTag is null)
            throw new ArgumentException("Give model (a Hugging Face repository, hf.co/{repo}:{quant} or an Ollama tag), huggingFaceRepo or ollamaTag.");
        var reader = fixture ? LocalModelFactsFixture.Reader() : LocalModelFactsReader.Shared;
        var (sentHf, sentOllama) = (reader.HuggingFace.Sent, reader.Ollama.Sent);
        LocalModelFacts facts;
        if (model is not null)
        {
            var (repo, quant, tag) = LocalModelFactsReader.Parse(model);
            if (repo is null && tag is null) throw new ArgumentException($"{model} isn't a Hugging Face repository or an Ollama tag.");
            quantization ??= quant;
            facts = await reader.LookupAsync(repo ?? huggingFaceRepo, tag ?? ollamaTag, cancellation, model, quantization);
        }
        else facts = await reader.LookupAsync(huggingFaceRepo, ollamaTag, cancellation, null, quantization);
        var estimate = facts.Estimate(quantization, context);
        return new
        {
            source = fixture ? "FIXTURE transport shaped like Hugging Face and the Ollama registry (NOT the real services)" : "live",
            facts = Facts(facts),
            estimate = Estimate(estimate),
            speed = Speed(estimate, card, bandwidthGbps),
            footprintCatalog = Footprint(facts.OllamaTag),
            measured = Measured(dataDirectory, facts.OllamaTag),
            requests = new { huggingFace = reader.HuggingFace.Sent - sentHf, ollamaRegistry = reader.Ollama.Sent - sentOllama }
        };
    }

    // Every fixture tag through the production reader and estimator: the estimate within 10% of what the tag measured on a
    // graphics card, Gemma 4's KV cache as Resource footprints works it out, inputs from config.json, install names, and the
    // request count staying small with the cache.
    private static async Task<object> FixtureAsync(int context, CancellationToken cancellation)
    {
        var seen = new List<string>();
        var reader = LocalModelFactsFixture.Reader(seen);
        var rows = new List<object>();
        var ok = true;
        foreach (var (tag, repo) in LocalModelFactsFixture.Repos)
        {
            var facts = await reader.LookupAsync(repo, tag, cancellation);
            var estimate = facts.Estimate(null, context)!;
            double? onCard = LocalModelFactsFixture.MeasuredGraphicsGb.TryGetValue(tag, out var gb) ? gb : null;
            var off = onCard is { } m ? Math.Round((estimate.GraphicsBytes / 1e9 - m) / m * 100, 1) : (double?)null;
            var row = ok && (off is null || Math.Abs(off.Value) <= 10) && facts.Problems.All(p => p.Contains("GGUF", StringComparison.Ordinal));
            ok = row;
            rows.Add(new { tag, repo, estimate = Estimate(estimate), measuredGraphicsGb = onCard, percentOff = off, problems = facts.Problems });
        }
        var e2b = await reader.LookupAsync("google/gemma-4-E2B-it", cancellation);
        var a4b = await reader.LookupAsync("google/gemma-4-26B-A4B-it", "gemma4:26b", cancellation);
        var requestsBefore = reader.HuggingFace.Sent + reader.Ollama.Sent;
        await reader.LookupAsync("google/gemma-4-E2B-it", cancellation);
        var cached = reader.HuggingFace.Sent + reader.Ollama.Sent == requestsBefore;
        var checks = new
        {
            ggufRepoPicked = e2b.GgufRepo == "ggml-org/gemma-4-E2B-it-GGUF",
            installName = e2b.Quantization("Q8_0")?.InstallName == "hf.co/ggml-org/gemma-4-E2B-it-GGUF:Q8_0",
            e2bHears = e2b.Inputs is { Image: true, Audio: true, Video: true, Source: "config.json" },
            a4bDoesntHear = a4b.Inputs is { Image: true, Audio: false },
            e2bCacheGb = Math.Round(e2b.Architecture!.KvCacheBytes(8_192) / 1e9, 3),
            a4bCacheGb = Math.Round(a4b.Architecture!.KvCacheBytes(8_192) / 1e9, 3),
            a4bActiveBillions = Math.Round((a4b.ActiveParameters ?? 0) / 1e9, 2),
            answeredFromCache = cached
        };
        var checksOk = checks is { ggufRepoPicked: true, installName: true, e2bHears: true, a4bDoesntHear: true, answeredFromCache: true } &&
            checks.e2bCacheGb is > 0.05 and < 0.06 && checks.a4bCacheGb is > 0.37 and < 0.39 && checks.a4bActiveBillions is > 3.5 and < 4.2;
        var measured = await MeasuredRehearsalAsync(cancellation);
        return new
        {
            ok = ok && checksOk && measured.Ok,
            source = "FIXTURE transport shaped like Hugging Face and the Ollama registry (NOT the real services); real sizes read 2026-10-10",
            contextTokens = context,
            calibration = rows,
            checks,
            measured = measured.Report,
            e2b = Facts(e2b),
            requests = seen.Count
        };
    }

    // The production OllamaMeasuredMemory against a FIXTURE /api/ps (in process, NOT Ollama), into a temporary folder of its own
    // that is deleted afterwards: the loaded model's measurement is saved once, not again while it is the same, and read back.
    private static async Task<(bool Ok, object Report)> MeasuredRehearsalAsync(CancellationToken cancellation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-model-memory-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new PsFixture());
        try
        {
            var origin = new Uri("http://127.0.0.1:11434/");
            var first = await OllamaMeasuredMemory.RecordAsync(client, origin, "gemma4:e2b", directory, cancellation);
            var again = await OllamaMeasuredMemory.RecordAsync(client, origin, "gemma4:e2b", directory, cancellation);
            var kept = MeasuredModelMemory.Load(directory).Find(OllamaMeasuredMemory.HostKey(origin), "gemma4:e2b");
            var ok = first.Saved && !again.Saved && kept is { GraphicsBytes: 3_300_000_000, ContextTokens: 8_192 };
            return (ok, new
            {
                ok, note = "FIXTURE /api/ps (NOT Ollama) into a temporary folder, deleted afterwards", savedFirst = first.Saved,
                savedAgain = again.Saved, kept?.Host, gb = kept is null ? (double?)null : Gb(kept.Bytes),
                graphicsGb = kept is null ? (double?)null : Gb(kept.GraphicsBytes), kept?.ContextTokens
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class PsFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/api/ps"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"models":[{"name":"gemma4:e2b","model":"gemma4:e2b","size":5140000000,"size_vram":3300000000,"context_length":8192,"digest":"fixture"}]}""")
                }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private static object Facts(LocalModelFacts facts) => new
    {
        facts.Query, facts.HuggingFaceRepo, facts.GgufRepo, facts.OllamaTag, facts.LocallyHostable,
        parametersBillions = Billions(facts.Parameters), weightsParametersBillions = Billions(facts.WeightsParameters),
        activeParametersBillions = Billions(facts.ActiveParameters), facts.License, facts.PipelineTag, facts.MaxContext, facts.Gated,
        inputs = facts.Inputs,
        architecture = facts.Architecture is not { } a ? null : new
        {
            a.ModelType, a.Layers, a.FullLayers, a.SlidingLayers, a.LinearLayers, a.SharedCacheLayers, a.KvHeads, a.HeadSize,
            a.FullKvHeads, a.FullHeadSize, a.SlidingWindow, a.Experts, a.ActiveExperts, a.MixtureOfExperts,
            fullAttentionKibPerToken = Math.Round(a.FullBytesPerToken() / 1024.0, 1),
            perLayerEmbeddingBillions = Billions(a.PerLayerEmbeddingParameters), a.MaxContext
        },
        quantizations = facts.Quantizations.Select(q => new
        {
            q.Name, weightsGb = Gb(q.WeightsBytes), encoderGb = Gb(q.EncoderBytes), draftGb = Gb(q.DraftBytes), q.InstallName, q.Source
        }),
        encoders = facts.Encoders.Select(f => new { f.Name, f.Quantization, gb = Gb(f.Bytes) }),
        drafts = facts.Drafts.Select(f => new { f.Name, f.Quantization, gb = Gb(f.Bytes) }),
        facts.GgufRepos, facts.Sources, facts.Problems, checkedAt = facts.CheckedAt.ToString("O")
    };

    private static object? Estimate(LocalMemoryEstimate? e) => e is null ? null : new
    {
        e.Quantization, e.ContextTokens, weightsGb = Gb(e.WeightsBytes), encoderGb = Gb(e.EncoderBytes), draftGb = Gb(e.DraftBytes),
        kvCacheGb = Gb(e.KvCacheBytes), e.KvCacheKnown, buffersGb = Gb(e.BuffersBytes), totalGb = Gb(e.TotalBytes),
        graphicsGb = Gb(e.GraphicsBytes), systemMemoryGb = Gb(e.SystemMemoryBytes), gbReadPerToken = Gb(e.BytesPerToken),
        described = e.Describe()
    };

    private static object? Speed(LocalMemoryEstimate? estimate, string? card, double? bandwidthGbps)
    {
        if (estimate is null || card is null && bandwidthGbps is null) return null;
        var known = GraphicsCardBandwidth.Find(card);
        var bandwidth = bandwidthGbps ?? known?.Gbps;
        return new
        {
            card, matched = known?.Name, bandwidthGbps = bandwidth,
            maxTokensPerSecond = bandwidth is { } b ? estimate.TokensPerSecond(b) : null,
            note = bandwidth is null
                ? "Martlet doesn't know this card's memory bandwidth; pass bandwidthGbps."
                : "An upper bound: bandwidth divided by the bytes read for each token. Real speed is often 50-70% of it."
        };
    }

    private static object? Footprint(string? tag)
    {
        if (tag is null || FootprintCatalog.Default.FindModel(PlanComponent.Thinking, tag) is not { } option) return null;
        return new { option.Id, vramGb = option.Steady.VramGb, diskGb = option.Steady.DiskGb, evidence = option.Evidence.ToString(), option.Source };
    }

    private static object? Measured(string? dataDirectory, string? tag) => dataDirectory is null ? null : new
    {
        file = MeasuredModelMemory.FileName,
        models = MeasuredModelMemory.Load(dataDirectory).Models
            .Where(m => tag is null || string.Equals(m.Model, tag, StringComparison.OrdinalIgnoreCase))
            .Select(m => new
            {
                m.Host, m.Model, gb = Gb(m.Bytes), graphicsGb = Gb(m.GraphicsBytes), m.OnGraphicsCard, m.ContextTokens,
                measuredAt = m.MeasuredAt.ToString("O")
            })
    };

    private static double Gb(long bytes) => Math.Round(bytes / 1e9, 2);

    private static double? Billions(long? count) => count is { } n ? Math.Round(n / 1e9, 2) : null;
}
