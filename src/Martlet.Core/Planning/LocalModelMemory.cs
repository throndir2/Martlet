using System.Globalization;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;

namespace Martlet.Core.Planning;

/// <summary>What one model takes in memory once loaded at a context, estimated the way
/// <see href="../../../docs/RESOURCE_FOOTPRINTS.md">Resource footprints</see> does: the quantization's weights, its picture and
/// sound encoder (<c>mmproj</c> or Ollama's projector), a speculative-decoding draft Ollama loads with it, the KV cache at
/// <see cref="ContextTokens"/> and compute buffers. <see cref="SystemMemoryBytes"/> is the part llama.cpp and Ollama keep in system
/// memory, not on the graphics card (Gemma's per-layer embeddings). <see cref="BytesPerToken"/> is what is read for each token
/// generated (the active weights and the cache), which sets the speed. GB are 10^9 bytes.</summary>
public sealed record LocalMemoryEstimate(string Quantization, int ContextTokens, long WeightsBytes, long EncoderBytes, long DraftBytes,
    long KvCacheBytes, bool KvCacheKnown, long BuffersBytes, long SystemMemoryBytes, long BytesPerToken)
{
    public long TotalBytes => WeightsBytes + EncoderBytes + DraftBytes + KvCacheBytes + BuffersBytes;
    /// <summary>What goes on the graphics card when the whole model fits there.</summary>
    public long GraphicsBytes => TotalBytes - SystemMemoryBytes;

    /// <summary>At most this many tokens a second on a card or computer with <paramref name="bandwidthGbps"/> GB/s of memory
    /// bandwidth: the bandwidth divided by the bytes read for each token. Real speed is lower (often 50-70% of it).</summary>
    public double? TokensPerSecond(double bandwidthGbps) =>
        bandwidthGbps > 0 && BytesPerToken > 0 ? Math.Round(bandwidthGbps * 1e9 / BytesPerToken, 1) : null;

    public string Describe()
    {
        var graphics = SystemMemoryBytes > 0
            ? $"about {Gb(GraphicsBytes)} GB on the graphics card and {Gb(SystemMemoryBytes)} GB more in system memory"
            : $"about {Gb(TotalBytes)} GB of graphics memory";
        var parts = $"{Gb(WeightsBytes)} GB of weights" + (EncoderBytes > 0 ? $", {Gb(EncoderBytes)} GB encoder" : "") +
            (DraftBytes > 0 ? $", {Gb(DraftBytes)} GB draft" : "") +
            $", {Gb(KvCacheBytes)} GB {(KvCacheKnown ? "" : "guessed ")}cache, {Gb(BuffersBytes)} GB buffers";
        return $"{Quantization} at {ContextTokens:N0} tokens takes {graphics} ({parts}).";
    }

    internal static string Gb(long bytes) => (bytes / 1e9).ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>Estimates the memory and speed of an open-weight model run locally in Ollama or llama.cpp, from its quantization
/// file sizes and its <c>config.json</c> (<see cref="LocalModelArchitecture"/>). A mixture-of-experts model keeps all its experts
/// in memory; its active parameters set only its speed. Checked against Martlet's measured Gemma 4 and Qwen3.5 numbers in
/// Resource footprints (within about 6%).</summary>
public static class LocalModelMemory
{
    /// <summary>Martlet's context for a Thinking model on this PC or a host.</summary>
    public const int DefaultContextTokens = GenerationSettings.DefaultHostContextTokens;
    /// <summary>Compute buffers and the runtime's own memory beside the weights and cache (Resource footprints: about 0.5 GB).</summary>
    public const long BuffersBytes = 500_000_000;
    /// <summary>f16 keys and values, Ollama's default cache type.</summary>
    public const int KvBytesPerValue = 2;

    /// <summary>The memory <paramref name="quantization"/> takes at <paramref name="contextTokens"/>. Without an architecture the
    /// cache is guessed as a tenth of the weights. <paramref name="weightsParameters"/> is the parameter count of the weights file
    /// (GGUF's or Ollama's), else the model's; <paramref name="activeParameters"/> those a token uses (a mixture-of-experts model's).</summary>
    public static LocalMemoryEstimate Estimate(LocalModelQuantization quantization, LocalModelArchitecture? architecture,
        long? weightsParameters, long? activeParameters, int contextTokens = DefaultContextTokens)
    {
        ArgumentNullException.ThrowIfNull(quantization);
        contextTokens = Math.Clamp(contextTokens, 1, 10_000_000);
        var weights = Math.Max(0, quantization.WeightsBytes);
        var cache = architecture?.KvCacheBytes(contextTokens, KvBytesPerValue) ?? weights / 10;
        // Gemma's per-layer embeddings stay in system memory, and a token reads one row of each.
        var embeddings = architecture is { PerLayerEmbeddingParameters: > 0 } && weightsParameters is > 0
            ? (long)(weights * Math.Min(0.9, (double)architecture.PerLayerEmbeddingParameters / weightsParameters.Value))
            : 0;
        var dense = weights - embeddings;
        // A mixture-of-experts model reads only its active share of the weights for each token.
        var textParameters = (weightsParameters ?? 0) - (embeddings > 0 ? architecture!.PerLayerEmbeddingParameters : 0);
        var share = activeParameters is > 0 && textParameters > 0 && activeParameters < textParameters
            ? (double)activeParameters.Value / textParameters
            : 1;
        return new(quantization.Name, contextTokens, weights, Math.Max(0, quantization.EncoderBytes), Math.Max(0, quantization.DraftBytes),
            cache, architecture is not null, BuffersBytes, embeddings, (long)(dense * share) + cache);
    }
}

/// <summary>Memory bandwidth of common graphics cards and Apple chips, in GB/s, from the makers' specifications: what the rough
/// speed estimate (<see cref="LocalMemoryEstimate.TokensPerSecond"/>) divides. Unknown cards return null; pass the bandwidth instead.</summary>
public static class GraphicsCardBandwidth
{
    private static readonly (string Name, double Gbps)[] Cards =
    [
        ("RTX 5090", 1792), ("RTX 5080", 960), ("RTX 5070 Ti", 896), ("RTX 5070", 672), ("RTX 5060 Ti", 448), ("RTX 5060", 448),
        ("RTX 4090", 1008), ("RTX 4080 Super", 736), ("RTX 4080", 717), ("RTX 4070 Ti Super", 672), ("RTX 4070 Ti", 504),
        ("RTX 4070 Super", 504), ("RTX 4070", 504), ("RTX 4060 Ti", 288), ("RTX 4060", 272),
        ("RTX 3090 Ti", 1008), ("RTX 3090", 936), ("RTX 3080 Ti", 912), ("RTX 3080", 760), ("RTX 3070 Ti", 608), ("RTX 3070", 448),
        ("RTX 3060 Ti", 448), ("RTX 3060", 360), ("RTX 2080 Ti", 616), ("RTX 2080", 448), ("RTX 2070", 448), ("RTX 2060", 336),
        ("GTX 1080 Ti", 484), ("GTX 1080", 320), ("GTX 1070", 256), ("GTX 1660 Super", 336), ("GTX 1660", 192),
        ("RTX 6000 Ada", 960), ("RTX A6000", 768), ("RTX A5000", 768), ("RTX A4000", 448), ("Tesla T4", 320), ("L4", 300),
        ("RX 9070 XT", 640), ("RX 9070", 640), ("RX 7900 XTX", 960), ("RX 7900 XT", 800), ("RX 7800 XT", 624), ("RX 7700 XT", 432),
        ("RX 7600", 288), ("RX 6900 XT", 512), ("RX 6800 XT", 512), ("RX 6700 XT", 384),
        ("M1 Ultra", 800), ("M1 Max", 400), ("M1 Pro", 200), ("M1", 68), ("M2 Ultra", 800), ("M2 Max", 400), ("M2 Pro", 200), ("M2", 100),
        ("M3 Max", 400), ("M3 Pro", 150), ("M3", 100), ("M4 Max", 546), ("M4 Pro", 273), ("M4", 120)
    ];

    /// <summary>The bandwidth of the card whose name <paramref name="card"/> contains (the longest match: "RTX 4070 Ti" before
    /// "RTX 4070"), with the name matched; null when no known card matches.</summary>
    public static (string Name, double Gbps)? Find(string? card)
    {
        if (string.IsNullOrWhiteSpace(card)) return null;
        var text = " " + Regex.Replace(card, @"\s+", " ").Trim() + " ";
        foreach (var known in Cards.OrderByDescending(c => c.Name.Length))
            if (Regex.IsMatch(text, @"(?<![A-Za-z0-9])" + Regex.Escape(known.Name) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
                return known;
        return null;
    }
}
