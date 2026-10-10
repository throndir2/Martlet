using System.Globalization;
using System.Text.RegularExpressions;

namespace Martlet.Core.Planning;

public enum LocalModelFileKind { Weights, Encoder, Draft }

/// <summary>One file of an open-weight model: a quantization's weights (all parts together), the picture and sound encoder
/// (<c>mmproj</c>, Ollama's projector) or a speculative-decoding draft (<c>mtp</c>, Ollama's draft).</summary>
public sealed record LocalModelFile(string Name, LocalModelFileKind Kind, string? Quantization, long Bytes);

/// <summary>One quantization of a model as it is installed: its weights' size, the encoder and draft that come with it,
/// the install name (an Ollama tag, or <c>hf.co/{repo}:{quant}</c> for a Hugging Face GGUF) and where the sizes came from.</summary>
public sealed record LocalModelQuantization(string Name, long WeightsBytes, string InstallName, string Source)
{
    public long EncoderBytes { get; init; }
    public long DraftBytes { get; init; }
    public long DownloadBytes => WeightsBytes + EncoderBytes + DraftBytes;
}

/// <summary>What the weights take besides text; each null when no source says. <see cref="Video"/> is true when the model takes
/// video, at least as a series of frames.</summary>
public sealed record LocalModelInputs(bool? Image, bool? Audio, bool? Video, string Source);

/// <summary>What Martlet found about running one open-weight model locally, from Hugging Face (the model, its
/// <c>config.json</c> and its GGUF files) and the Ollama registry (one tag's layers). Model facts only: what the weights are and
/// take, not what a server offers. Look one up with <c>Martlet.Providers.LocalModels.LocalModelFactsReader.LookupAsync</c>; the
/// model catalog joins it on <see cref="HuggingFaceRepo"/>.</summary>
public sealed record LocalModelFacts
{
    /// <summary>What was asked for: a Hugging Face repository, <c>hf.co/{repo}:{quant}</c> or an Ollama tag.</summary>
    public required string Query { get; init; }
    /// <summary>The model's own (unquantized) Hugging Face repository, for example google/gemma-4-E2B-it.</summary>
    public string? HuggingFaceRepo { get; init; }
    /// <summary>The Hugging Face repository the GGUF quantizations came from.</summary>
    public string? GgufRepo { get; init; }
    public string? OllamaTag { get; init; }
    /// <summary>The model maker's parameter count (safetensors), encoders included.</summary>
    public long? Parameters { get; init; }
    /// <summary>The parameters in the quantized weights file (GGUF's or Ollama's count), without a separate encoder.</summary>
    public long? WeightsParameters { get; init; }
    /// <summary>Parameters a token uses: less than <see cref="WeightsParameters"/> for a mixture-of-experts model.</summary>
    public long? ActiveParameters { get; init; }
    public string? License { get; init; }
    public string? PipelineTag { get; init; }
    public int? MaxContext { get; init; }
    public bool Gated { get; init; }
    public LocalModelArchitecture? Architecture { get; init; }
    public LocalModelInputs? Inputs { get; init; }
    public IReadOnlyList<LocalModelQuantization> Quantizations { get; init; } = [];
    public IReadOnlyList<LocalModelFile> Encoders { get; init; } = [];
    public IReadOnlyList<LocalModelFile> Drafts { get; init; } = [];
    /// <summary>GGUF repositories made from the model, most downloaded first.</summary>
    public IReadOnlyList<string> GgufRepos { get; init; } = [];
    /// <summary>Where each fact came from, in words with the address asked.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];
    /// <summary>What couldn't be read and why.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>Can run locally: at least one quantization to install.</summary>
    public bool LocallyHostable => Quantizations.Count > 0;

    /// <summary>The quantization named <paramref name="name"/> (case-insensitive), else the Ollama tag's, else Q4_K_M, else
    /// another 4-bit one, else the smallest at 8 bits or less, else the first.</summary>
    public LocalModelQuantization? Quantization(string? name = null)
    {
        if (Quantizations.Count == 0) return null;
        if (name is { Length: > 0 })
            return Quantizations.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) ??
                Quantizations.FirstOrDefault(q => string.Equals(q.InstallName, name, StringComparison.OrdinalIgnoreCase));
        return Quantizations.FirstOrDefault(q => OllamaTag is not null && q.InstallName == OllamaTag) ??
            Quantizations.FirstOrDefault(q => string.Equals(q.Name, "Q4_K_M", StringComparison.OrdinalIgnoreCase)) ??
            Quantizations.FirstOrDefault(q => q.Name.Contains("Q4_K", StringComparison.OrdinalIgnoreCase)) ??
            Quantizations.FirstOrDefault(q => q.Name.Contains("Q4", StringComparison.OrdinalIgnoreCase)) ??
            Quantizations.Where(q => LocalModelQuantizations.Bits(q.Name) is <= 8).OrderBy(q => q.WeightsBytes).FirstOrDefault() ??
            Quantizations[0];
    }

    /// <summary>The memory <paramref name="quantization"/> (see <see cref="Quantization"/>) takes at
    /// <paramref name="contextTokens"/>; null when there is no quantization to install.</summary>
    public LocalMemoryEstimate? Estimate(string? quantization = null, int contextTokens = LocalModelMemory.DefaultContextTokens) =>
        Quantization(quantization) is { } chosen
            ? LocalModelMemory.Estimate(chosen, Architecture, WeightsParameters ?? Parameters, ActiveParameters, contextTokens)
            : null;
}

/// <summary>Quantization names in GGUF file names and how Ollama installs a model.</summary>
public static partial class LocalModelQuantizations
{
    /// <summary>Ollama installs any Hugging Face GGUF with this name: <c>hf.co/{repo}:{quant}</c>.</summary>
    public static string HuggingFaceInstallName(string repo, string quantization) => $"hf.co/{repo}:{quantization}";

    /// <summary>The quantization in a GGUF file name ("gemma-4-E2B-it-UD-Q4_K_XL.gguf" gives "UD-Q4_K_XL"), without a split
    /// file's part number; null when the name has none.</summary>
    public static string? FromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName.Replace('\\', '/').Split('/')[^1]);
        name = SplitPart().Replace(name, "");
        var match = QuantizationName().Match(name);
        return match.Success ? match.Groups["quant"].Value.ToUpperInvariant() : null;
    }

    /// <summary>Bits a weight of the quantization takes, roughly (Q4_K_M 4, BF16 16); null when unknown.</summary>
    public static int? Bits(string quantization)
    {
        var name = quantization.ToUpperInvariant();
        if (name.Contains("F32", StringComparison.Ordinal)) return 32;
        if (name.Contains("F16", StringComparison.Ordinal)) return 16;
        if (name.Contains("FP4", StringComparison.Ordinal)) return 4;
        var match = BitsDigit().Match(name);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A parameter count as Ollama writes it ("4.6B", "137M") in parameters; null when it isn't one.</summary>
    public static long? ParseParameters(string? text)
    {
        if (text is null) return null;
        var match = ParameterCount().Match(text.Trim());
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        var scale = char.ToUpperInvariant(match.Groups[2].Value[0]) switch { 'T' => 1e12, 'B' => 1e9, 'M' => 1e6, _ => 1e3 };
        return (long)Math.Round(number * scale);
    }

    /// <summary>Active parameters a mixture-of-experts name states ("Qwen3-30B-A3B" gives 3 billion), else null.</summary>
    public static long? ActiveFromName(string? name) =>
        name is not null && ActiveInName().Match(name) is { Success: true } match &&
        double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var billions)
            ? (long)Math.Round(billions * 1e9) : null;

    [GeneratedRegex(@"-\d{5}-of-\d{5}$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPart();

    [GeneratedRegex(@"(?:^|[-_.])(?<quant>(?:UD-)?(?:I?Q\d[A-Z0-9_]*|TQ\d_\d|BF16|F16|F32|MXFP4(?:_MOE)?|NVFP4))$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantizationName();

    [GeneratedRegex(@"Q(\d)")]
    private static partial Regex BitsDigit();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([KMBT])$", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterCount();

    [GeneratedRegex(@"(?:^|[-_ ])A(\d+(?:\.\d+)?)B(?:$|[-_ ])", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveInName();
}
