using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>How smart a Thinking pool member's model is (Companion › Thinking pool): <see cref="Fast"/> (a small model, about 4B
/// parameters or less), <see cref="Standard"/> or <see cref="Smart"/> (a large model, about 26B or more, or a big cloud model).
/// Martlet guesses it from the model name (<see cref="ThinkingSmartsGuess"/>); the owner can change it for each member
/// (<see cref="ThinkingPoolSettings.Smarts"/>). A job's <see cref="ThinkingRunsOn"/> uses it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThinkingSmarts>))]
public enum ThinkingSmarts { Fast = 0, Standard = 1, Smart = 2 }

/// <summary>Where a kind of Thinking pool job runs (Companion › Thinking pool, Runs on; and on each check-in card):
/// <see cref="Any"/> (any member that takes it, the one that shares least with the conversation first),
/// <see cref="PreferSmart"/> (the smartest free member; when none of the smartest comes free within a short wait, a less smart
/// one), <see cref="SmartOnly"/> (only <see cref="ThinkingSmarts.Smart"/> members; without one the job gets "no member" and its
/// caller's fallback runs) or <see cref="Members"/> (only the members the owner chose).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThinkingRunsOnMode>))]
public enum ThinkingRunsOnMode { Any = 0, PreferSmart = 1, SmartOnly = 2, Members = 3 }

/// <summary>Where a job runs: its <see cref="Mode"/> and, with <see cref="ThinkingRunsOnMode.Members"/>, the chosen members by
/// <see cref="DeepThinkingSettings.Key"/>. Compared by value.</summary>
public sealed record ThinkingRunsOn
{
    public const int MaxMembers = 2 * DeepThinkingSettings.MaxPlaces;

    public ThinkingRunsOnMode Mode { get; init; }

    /// <summary>The chosen members' keys (only with <see cref="ThinkingRunsOnMode.Members"/>); empty: no member.</summary>
    public IReadOnlyList<string> Members { get => members; init => members = value ?? []; }
    private readonly IReadOnlyList<string> members = [];

    public static ThinkingRunsOn Any { get; } = new();
    public static ThinkingRunsOn PreferSmart { get; } = new() { Mode = ThinkingRunsOnMode.PreferSmart };
    public static ThinkingRunsOn SmartOnly { get; } = new() { Mode = ThinkingRunsOnMode.SmartOnly };

    /// <summary>Only the members with <paramref name="keys"/>.</summary>
    public static ThinkingRunsOn Only(IEnumerable<string> keys) =>
        new() { Mode = ThinkingRunsOnMode.Members, Members = [.. keys.Distinct(StringComparer.Ordinal)] };

    /// <summary>Whether a member with <paramref name="key"/> and <paramref name="smarts"/> may take the job (Prefer smart only
    /// orders the members; it never keeps one out).</summary>
    public bool Allows(string key, ThinkingSmarts smarts) => Mode switch
    {
        ThinkingRunsOnMode.SmartOnly => smarts == ThinkingSmarts.Smart,
        ThinkingRunsOnMode.Members => Members.Contains(key, StringComparer.Ordinal),
        _ => true
    };

    /// <summary>The words the owner sees: "Any member", "Prefer smart", "Smart only", "These members (2)".</summary>
    public string Describe() => Mode switch
    {
        ThinkingRunsOnMode.PreferSmart => "Prefer smart",
        ThinkingRunsOnMode.SmartOnly => "Smart only",
        ThinkingRunsOnMode.Members => $"These members ({Members.Count})",
        _ => "Any member"
    };

    /// <summary>The mode's name in status files: any, prefer-smart, smart-only, members.</summary>
    [JsonIgnore]
    public string Name => Mode switch
    {
        ThinkingRunsOnMode.PreferSmart => "prefer-smart",
        ThinkingRunsOnMode.SmartOnly => "smart-only",
        ThinkingRunsOnMode.Members => "members",
        _ => "any"
    };

    public void Validate()
    {
        ContractRules.Require(Enum.IsDefined(Mode), "A job runs on any member, prefers smart ones, only smart ones or only chosen ones.");
        ContractRules.Require(Members.Count <= MaxMembers && Members.All(k => k is { Length: > 0 and <= 4096 } && !k.Any(char.IsControl)) &&
            (Mode == ThinkingRunsOnMode.Members || Members.Count == 0),
            "The members a job runs on are a short list of member keys, only for These members.");
    }

    public bool Equals(ThinkingRunsOn? other) =>
        other is not null && Mode == other.Mode && Members.SequenceEqual(other.Members, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Mode);
        foreach (var key in Members) hash.Add(key, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>Martlet's guess of how smart a model is, from its name: the parameter count in the name decides when there is one
/// (gemma4:e2b and qwen3:4b are <see cref="ThinkingSmarts.Fast"/>, qwen3:8b <see cref="ThinkingSmarts.Standard"/>, gemma4:27b
/// and llama3.3:70b <see cref="ThinkingSmarts.Smart"/>); else large cloud model families (Nemotron, GPT-4/5, Claude, Gemini,
/// DeepSeek and similar) are Smart, "tiny", "nano" and "small" models Fast, and every other model Standard.</summary>
public static partial class ThinkingSmartsGuess
{
    /// <summary>At most this many billion parameters is <see cref="ThinkingSmarts.Fast"/>.</summary>
    public const double FastAtMost = 4.5;
    /// <summary>At least this many billion parameters is <see cref="ThinkingSmarts.Smart"/>.</summary>
    public const double SmartFrom = 26;

    private static readonly string[] SmartFamilies =
    [
        "nemotron", "gpt-4", "gpt-5", "gpt-oss-120b", "o1", "o3", "o4", "claude", "gemini", "deepseek", "kimi", "glm-4", "glm4", "grok",
        "mistral-large", "mistral-medium", "command-r-plus", "llama-4", "llama4", "qwen3-max", "qwen-max", "minimax", "sonnet", "opus"
    ];
    private static readonly string[] FastWords = ["tiny", "nano", "small", "mini-", "lite", "flash-lite", "haiku"];

    public static ThinkingSmarts From(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return ThinkingSmarts.Standard;
        var name = model.Trim().ToLowerInvariant();
        // The largest size the name gives: "qwen3-30b-a3b" is 30B in all (3B active), "gemma4:e2b" about 2B.
        double? billions = null;
        foreach (Match match in Size().Matches(name))
        {
            var value = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (match.Groups["unit"].Value == "m") value /= 1000;
            if (match.Groups["x"].Success && int.TryParse(match.Groups["x"].Value, CultureInfo.InvariantCulture, out var experts)) value *= experts;
            if (value > 0 && (billions is null || value > billions)) billions = value;
        }
        if (billions is { } size) return size <= FastAtMost ? ThinkingSmarts.Fast : size >= SmartFrom ? ThinkingSmarts.Smart : ThinkingSmarts.Standard;
        var words = Words().Replace(name, "-");
        if (SmartFamilies.Any(family => Family(words, family))) return ThinkingSmarts.Smart;
        if (FastWords.Any(word => words.Contains(word.TrimEnd('-'), StringComparison.Ordinal))) return ThinkingSmarts.Fast;
        return ThinkingSmarts.Standard;
    }

    // A family name at the start of the name or of one of its parts ("nvidia/llama-3.1-nemotron-ultra", "openai-gpt-4o").
    private static bool Family(string words, string family)
    {
        for (var at = words.IndexOf(family, StringComparison.Ordinal); at >= 0; at = words.IndexOf(family, at + 1, StringComparison.Ordinal))
        {
            var starts = at == 0 || words[at - 1] == '-';
            var end = at + family.Length;
            // "o1" must not match "o1b" or "solo1": only a whole part or one followed by a dash or a letter suffix such as "o3-mini".
            var ends = end == words.Length || words[end] == '-' || char.IsLetter(words[end]) && family.Length > 3;
            if (starts && ends) return true;
        }
        return false;
    }

    // "8b", "e2b", "1.5b", "360m", "8x7b"; the size must stand on its own ("qwen3:8b", "llama-3.1-70b-instruct"), not inside a
    // word such as "web".
    [GeneratedRegex(@"(?<![a-z0-9.])(?:e|a)?(?:(?<x>\d+)x)?(?<n>\d+(?:\.\d+)?)(?<unit>[bm])(?![a-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex Size();

    [GeneratedRegex(@"[^a-z0-9.]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
