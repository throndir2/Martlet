using System.Globalization;
using System.Text.RegularExpressions;

namespace Martlet.Core.Planning;

/// <summary>Reads model names the way the catalog joins them: the family ("gemma-4" in "google/gemma-4-26B-A4B-it"), the
/// size ("26b-a4b"), the parameter counts a name states, and a key for comparing names across sources (LMArena's
/// "claude-opus-4-6-high" is OpenRouter's "anthropic/claude-opus-4.6").</summary>
public static partial class CatalogNaming
{
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "it", "instruct", "chat", "base", "hf", "thinking", "reasoning", "fp8", "bf16", "fp16", "awq", "gptq", "gguf", "int4", "int8",
        "nvfp4", "mxfp8", "mlx", "qat", "latest", "free", "pt", "sft", "rl"
    };

    private static readonly HashSet<string> KeyGeneric = new(StringComparer.Ordinal) { "it", "instruct", "chat", "latest", "hf" };

    /// <summary>The last part of a name: the repository or model name without its owner or tag ("google/gemma-4-31B-it:free"
    /// is "gemma-4-31B-it").</summary>
    public static string Leaf(string name)
    {
        var leaf = name.Trim();
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        var colon = leaf.IndexOf(':');
        return colon > 0 ? leaf[..colon] : leaf;
    }

    private static string[] Tokens(string leaf) =>
        leaf.ToLowerInvariant().Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The model's family: its name without sizes, dates and words such as "instruct" ("gemma-4", "qwen3.5",
    /// "llama-3.2-vision").</summary>
    public static string Family(string name)
    {
        var tokens = Tokens(Leaf(name)).Where(t => !SizeToken().IsMatch(t) && !Generic.Contains(t) && !DateToken().IsMatch(t) &&
            !QuantToken().IsMatch(t));
        return string.Join('-', tokens);
    }

    /// <summary>The family without separators, so "gemma-4" and Ollama's "gemma4" compare equal.</summary>
    public static string CompactFamily(string name) => Family(name).Replace("-", "", StringComparison.Ordinal);

    /// <summary>The sizes in the name, joined ("26b-a4b", "e2b", "122b-a10b"); empty when it states none.</summary>
    public static string Size(string name) => string.Join('-', Tokens(Leaf(name)).Where(t => SizeToken().IsMatch(t)));

    /// <summary>The first size in the name ("26b" for "26b-a4b"), what an Ollama tag such as "gemma4:26b" gives.</summary>
    public static string? MainSize(string name) => Tokens(Leaf(name)).FirstOrDefault(t => SizeToken().IsMatch(t) && !t.StartsWith('a'));

    /// <summary>The total and active parameters (billions) the name states: "26B-A4B" is 26 and 4, "31B" (a dense model) 31 and
    /// 31, "E2B" (effective) only 2 active, "17B-16E" (17 billion active across 16 experts) only 17 active.</summary>
    public static (double? Total, double? Active) Parameters(string name)
    {
        var tokens = Tokens(Leaf(name));
        double? total = null, active = null;
        var experts = false;
        foreach (var token in tokens)
        {
            if (ExpertsToken().IsMatch(token)) experts = true;
            else if (Billions(token) is { } billions)
            {
                if (token[0] == 'a') active ??= billions;
                else if (token[0] == 'e') { active ??= billions; experts = true; }
                else total ??= billions;
            }
        }
        if (experts && active is null && total is not null) (active, total) = (total, null);
        else if (active is null && total is not null && !experts) active = total;
        return (total, active);
    }

    private static double? Billions(string token)
    {
        var match = CountToken().Match(token);
        if (!match.Success || !double.TryParse(match.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        return match.Groups["unit"].Value switch { "m" => n / 1000, "t" => n * 1000, _ => n };
    }

    /// <summary>A key for comparing names from different sources: the last part, lower case, without its owner, dates, effort
    /// words ("high", "max") and words such as "instruct"; dots and spaces read as dashes.</summary>
    public static string Key(string name)
    {
        var text = Parenthesis().Replace(name.ToLowerInvariant(), " ");
        var slash = text.LastIndexOf('/');
        if (slash >= 0) text = text[(slash + 1)..];
        var colon = text.IndexOf(':');
        if (colon > 0) text = text[..colon];
        var tokens = text.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries).ToList();
        bool changed;
        do
        {
            changed = false;
            while (tokens.Count > 1 && (Effort().IsMatch(tokens[^1]) || LongDate().IsMatch(tokens[^1]) || ContextSize().IsMatch(tokens[^1])))
            {
                tokens.RemoveAt(tokens.Count - 1);
                changed = true;
            }
            // yyyy-mm-dd split into three tokens.
            if (tokens.Count > 3 && tokens[^3].Length == 4 && tokens[^3].StartsWith("20", StringComparison.Ordinal) &&
                tokens[^2].Length == 2 && tokens[^1].Length == 2 && tokens[^2].All(char.IsDigit) && tokens[^1].All(char.IsDigit))
            {
                tokens.RemoveRange(tokens.Count - 3, 3);
                changed = true;
            }
        }
        while (changed);
        return string.Join('-', tokens.Where(t => !KeyGeneric.Contains(t)));
    }

    /// <summary>An Ollama name ("gemma4:26b", "qwen3.5:4b-q4_K_M"): its compact family and main size; null for another name.</summary>
    public static (string Family, string? Size)? Ollama(string name)
    {
        var match = OllamaName().Match(name.Trim().ToLowerInvariant());
        if (!match.Success) return null;
        var tag = match.Groups["tag"].Value;
        var size = tag.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => SizeToken().IsMatch(t) && !t.StartsWith('a'));
        return (match.Groups["family"].Value.Replace("-", "", StringComparison.Ordinal), size);
    }

    /// <summary>The Hugging Face repository of an Ollama "hf.co/{repo}:{quant}" name, without a "-GGUF" ending; null otherwise.</summary>
    public static string? HuggingFaceOf(string name)
    {
        var match = HfName().Match(name.Trim());
        if (!match.Success) return null;
        var repo = match.Groups["repo"].Value;
        return GgufEnding().Replace(repo, "");
    }

    [GeneratedRegex(@"^(?:[ae]?\d+(?:\.\d+)?[bmt]|\d+x\d+(?:\.\d+)?b)$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeToken();

    [GeneratedRegex(@"^(?<pre>[ae]?)(?<n>\d+(?:\.\d+)?)(?<unit>[bmt])$", RegexOptions.CultureInvariant)]
    private static partial Regex CountToken();

    [GeneratedRegex(@"^\d+e$", RegexOptions.CultureInvariant)]
    private static partial Regex ExpertsToken();

    [GeneratedRegex(@"^(?:\d{4}|\d{6}|\d{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateToken();

    [GeneratedRegex(@"^(?:q\d.*|k|m|s|l|xl|xs|iq\d.*|f16|f32)$", RegexOptions.CultureInvariant)]
    private static partial Regex QuantToken();

    [GeneratedRegex(@"^(?:minimal|low|medium|high|xhigh|max)$", RegexOptions.CultureInvariant)]
    private static partial Regex Effort();

    [GeneratedRegex(@"^(?:\d{8}|\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex LongDate();

    [GeneratedRegex(@"^\d+k$", RegexOptions.CultureInvariant)]
    private static partial Regex ContextSize();

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Parenthesis();

    [GeneratedRegex(@"^(?<family>[a-z][a-z0-9.\-]*):(?<tag>[a-z0-9._\-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex OllamaName();

    [GeneratedRegex(@"^(?:https?://)?(?:hf\.co|huggingface\.co)/(?<repo>[^/:\s]+/[^/:\s]+)(?::\S*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HfName();

    [GeneratedRegex(@"[-_.]gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GgufEnding();
}
