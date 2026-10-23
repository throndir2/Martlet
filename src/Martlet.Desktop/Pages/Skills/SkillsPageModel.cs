using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Companion › Skills says, without WPF: how many skills there are, what every reply gets and each skill's state.</summary>
internal static class SkillsPageModel
{
    /// <summary>The page's Now line (SkillsNow): the skills there are, the pinned ones every reply gets, and how many the last reply
    /// carried. Counts and names only.</summary>
    internal static string DescribeSkills(SkillSettings? saved, ReplyPromptRecord? last = null)
    {
        var all = SkillSettings.All(saved);
        var own = all.Count(s => !s.BuiltIn);
        var pinned = all.Where(s => s.Pinned && !string.IsNullOrWhiteSpace(s.Body)).ToArray();
        var tokens = pinned.Sum(s => PromptsPageModel.PromptTokens(s.Body));
        var count = $"{all.Count - own} built-in skills" + (own == 0 ? "" : own == 1 ? " and 1 of your own" : $" and {own} of your own");
        var lastLine = last is null ? " No reply yet in this session."
            : $" The last reply carried {last.Skills.Count} skill{(last.Skills.Count == 1 ? "" : "s")}.";
        return (pinned.Length == 0
            ? count + ". No skill is pinned: replies get only the skills their features attach."
            : $"{count}. Every reply gets {(pinned.Length == 1 ? "1 pinned skill" : $"{pinned.Length} pinned skills")} " +
              $"({string.Join(", ", pinned.Select(s => s.Name))}), about {tokens:N0} tokens.") + lastLine;
    }

    /// <summary>A skill's state in words (SkillState-key): built in, edited or your own; pinned, attached in the last reply,
    /// attached when its feature is on (with when) or loaded on demand by the director; its estimated tokens; and whether it is
    /// still saving. Never the skill's text.</summary>
    internal static string SkillState(Skill typed, SkillSettings? saved, IReadOnlyCollection<string>? attached = null)
    {
        var kind = !typed.BuiltIn ? "Your own skill" : SkillSettings.Normalize([typed]) is null ? "Built-in skill" : "Edited built-in skill";
        var when = SkillCatalog.Find(typed.Key)?.When;
        var used = string.IsNullOrWhiteSpace(typed.Body) ? "empty: nothing is sent"
            : typed.Pinned ? "Pinned: every reply to you gets it"
            : attached?.Contains(typed.Key) == true ? "Attached now: the last reply carried it"
            : when is { Length: > 0 } ? "Attached when: " + char.ToLowerInvariant(when[0]) + when[1..]
            : "Loaded on demand by the director";
        var tokens = string.IsNullOrWhiteSpace(typed.Body) ? "" : $", about {PromptsPageModel.PromptTokens(typed.Body):N0} tokens";
        return $"{kind}; {used}{tokens}" + (SkillSettings.Find(saved, typed.Key) == typed ? "." : ", saving.");
    }

    /// <summary>A key for a new skill of the user's own that no skill has yet: skill-1, skill-2...</summary>
    internal static string NewKey(IEnumerable<Skill> skills)
    {
        var used = skills.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
            if (!used.Contains($"skill-{n}")) return $"skill-{n}";
    }
}
