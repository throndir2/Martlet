using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>One skill as the user keeps it: an edit of a built-in skill (its <see cref="Key"/> is a
/// <see cref="SkillCatalog"/> key) or one of the user's own skills. An empty <see cref="Body"/> sends nothing for it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Skill : IContract
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string UseWhen { get; init; }
    public required string Body { get; init; }
    /// <summary>Every reply gets it (in its closing instructions).</summary>
    public bool Pinned { get; init; }

    [JsonIgnore]
    public bool BuiltIn => SkillCatalog.Find(Key) is not null;

    /// <summary>The reply tools it explains (a built-in skill's; the user's own explain none).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Tools => SkillCatalog.Find(Key)?.Tools ?? [];

    public void Validate()
    {
        ContractRules.Require(SkillSettings.IsKey(Key), "A skill's key is 1-40 lowercase letters, digits and hyphens.");
        ContractRules.Require(!string.IsNullOrWhiteSpace(Name), "A skill needs a name.");
        ContractRules.Text(Name, SkillSettings.MaximumNameCharacters);
        ContractRules.Text(UseWhen, SkillSettings.MaximumUseWhenCharacters);
        ContractRules.Text(Body, PromptSettings.MaximumTextCharacters);
    }

    internal static Skill From(SkillDefinition definition) =>
        new() { Key = definition.Key, Name = definition.Name, UseWhen = definition.UseWhen, Body = definition.Body, Pinned = definition.Pinned };
}

/// <summary>The user's skills (Companion › Skills; docs/CONVERSATION.md#skills): edits of the built-in skills and the user's own,
/// shared by every computer (settings.json's <c>skills</c> section). A built-in skill that isn't listed is as built in. Absent
/// from settings while nothing is edited. A request reads them through <see cref="PromptSettings.Skills"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillSettings : IContract
{
    public const int MaximumSkills = 64, MaximumKeyCharacters = 40, MaximumNameCharacters = 60, MaximumUseWhenCharacters = 200;
    public const int MaximumAggregateUtf8Bytes = 65_536;

    /// <summary>Edited built-in skills and the user's own, in the order the user keeps them.</summary>
    public required IReadOnlyList<Skill> Skills { get; init; }

    [JsonIgnore]
    public bool IsDefault => Skills.Count == 0;

    public static bool IsKey(string? key) => key is { Length: > 0 and <= MaximumKeyCharacters } &&
        key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') && key[0] != '-' && key[^1] != '-';

    public void Validate()
    {
        ContractRules.Require(Skills is { Count: <= MaximumSkills }, $"Keep at most {MaximumSkills} skills.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in Skills!)
        {
            ContractRules.Require(skill is not null, "A skill can't be null.");
            skill!.Validate();
            ContractRules.Require(keys.Add(skill.Key), $"Two skills have the key '{skill.Key}'.");
        }
        try
        {
            ContractRules.Require(Skills.Sum(s => new UTF8Encoding(false, true).GetByteCount(s.Name + s.UseWhen + s.Body)) <= MaximumAggregateUtf8Bytes,
                "Skills exceed the combined 64 KiB settings limit.");
        }
        catch (EncoderFallbackException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "A skill contains invalid Unicode.");
        }
    }

    /// <summary>Only what differs from the built-in skills (and the user's own skills); null when nothing does.</summary>
    public static SkillSettings? Normalize(IEnumerable<Skill>? skills)
    {
        if (skills is null) return null;
        var kept = skills.Where(s => SkillCatalog.Find(s.Key) is not { } built || s != Skill.From(built)).ToArray();
        return kept.Length == 0 ? null : new SkillSettings { Skills = kept };
    }

    /// <summary>Every skill: the built-in ones (with the user's edits) first, then the user's own in their order.</summary>
    public static IReadOnlyList<Skill> All(SkillSettings? settings)
    {
        var edits = settings?.Skills ?? [];
        return
        [
            .. SkillCatalog.All.Select(d => edits.FirstOrDefault(s => s.Key == d.Key) ?? Skill.From(d)),
            .. edits.Where(s => !s.BuiltIn)
        ];
    }

    public static Skill? Find(SkillSettings? settings, string key) =>
        settings?.Skills.FirstOrDefault(s => s.Key == key) ?? (SkillCatalog.Find(key) is { } built ? Skill.From(built) : null);

    /// <summary>The skill's text with its placeholders filled in, or null when it is empty or unknown.</summary>
    public static string? Fill(PromptSettings? prompts, string key, params (string Name, string Value)[] values) =>
        Find(prompts?.Skills, key) is { Body: var body } && !string.IsNullOrWhiteSpace(body) ? PromptSettings.FillText(body, values) : null;

    /// <summary>What a reply offered the skill's tools is told: <see cref="Fill"/>, or null while the skill is pinned (every reply
    /// already gets it in its closing instructions).</summary>
    public static string? Guidance(PromptSettings? prompts, string key, params (string Name, string Value)[] values) =>
        Find(prompts?.Skills, key) is { Pinned: true } ? null : Fill(prompts, key, values);

    /// <summary>The pinned skills' texts, in their order, for the closing instructions of every reply; null when none is pinned.
    /// <paramref name="silent"/> fills {silent}. From saved settings only, so the start of every request stays the same.</summary>
    public static string? Pinned(PromptSettings? prompts, string silent)
    {
        var texts = All(prompts?.Skills).Where(s => s.Pinned && !string.IsNullOrWhiteSpace(s.Body))
            .Select(s => PromptSettings.FillText(s.Body.Trim(), ("silent", silent))).ToArray();
        return texts.Length == 0 ? null : string.Join("\n\n", texts);
    }

    /// <summary><paramref name="settings"/> with the prompt edits that became built-in skills (<paramref name="moved"/>, by
    /// prompt ID) as their skills' bodies, unless a skill is already edited. Null stays null when nothing moves.</summary>
    public static SkillSettings? WithMovedPrompts(SkillSettings? settings, IReadOnlyDictionary<string, string>? moved)
    {
        if (moved is not { Count: > 0 }) return settings;
        var skills = (settings?.Skills ?? []).ToList();
        foreach (var (id, text) in moved.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!SkillCatalog.MovedPrompts.TryGetValue(id, out var key) || skills.Any(s => s.Key == key)) continue;
            skills.Add(Skill.From(SkillCatalog.Find(key)!) with { Body = text });
        }
        return Normalize(skills);
    }
}
