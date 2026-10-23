using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Companion › Prompts says, without WPF: each prompt's state, the estimated tokens and how many prompts are edited.
/// Check-ins show their prompts' states with <see cref="PromptState"/> too.</summary>
internal static class PromptsPageModel
{
    /// <summary>A prompt's state in words, against what is saved: built in, edited, empty (sent as nothing), its estimated tokens,
    /// and whether it is still saving.</summary>
    internal static string PromptState(PromptDefinition prompt, string text, PromptSettings? saved)
    {
        var sent = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var state = string.IsNullOrWhiteSpace(text) ? "Empty: nothing is sent for this prompt"
            : (text == prompt.Default ? "Built-in text, about " : "Edited, about ") + Tokens(PromptTokens(sent));
        return (sent == PromptSettings.Text(saved, prompt.Id) ? state : state + ", saving") + ".";
    }

    /// <summary>The estimated tokens of every prompt together, as typed: what all of them would add if one request sent them all.</summary>
    internal static string DescribePromptTokens(IEnumerable<string> texts) =>
        "All prompts together: about " + Tokens(texts.Sum(t => string.IsNullOrWhiteSpace(t) ? 0
            : PromptTokens(t.Replace("\r\n", "\n", StringComparison.Ordinal)))) +
        " (each request sends only the prompts it needs).";

    /// <summary>A prompt's estimated tokens by Martlet's own request-size rule (see <see cref="Martlet.Providers.BoundedTextInput"/>).</summary>
    internal static int PromptTokens(string text) => Martlet.Providers.BoundedTextInput.TextTokens(text);

    private static string Tokens(int count) =>
        count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + (count == 1 ? " token" : " tokens");

    /// <summary>What every reply to you sends besides the conversation (PromptsReplyAlways), from saved settings: the persona
    /// prompt with the persona, the pinned skills by name and One moment, each with its estimated tokens. Never their text.</summary>
    internal static string DescribeEveryReply(PromptSettings? saved, SkillSettings? skills, string name, string persona)
    {
        var prompts = PromptSettings.ForPersonal(saved, null, skills);
        var (wrapped, moment, closing) = SkillSettings.EveryReply(prompts, name, persona, Martlet.Conversation.StayQuiet.Marker);
        int Count(string? text) => text is null ? 0 : PromptTokens(text);
        var pinned = SkillSettings.All(skills).Where(s => s.Pinned && !string.IsNullOrWhiteSpace(s.Body)).Select(s => s.Name).ToArray();
        var parts = new List<string> { $"the persona prompt with your persona (about {Tokens(Count(wrapped))})" };
        parts.Add(pinned.Length == 0 ? "no pinned skill"
            : $"the pinned skill{(pinned.Length == 1 ? "" : "s")} {Joined(pinned)} (about {Tokens(Count(closing))})");
        if (moment is not null) parts.Add($"One moment (about {Tokens(Count(moment))})");
        return $"Every reply to you sends about {Tokens(Count(wrapped) + Count(moment) + Count(closing))} of instructions: " +
            Joined(parts) + ". Everything else is a skill a feature attaches, or a note that goes only when it applies.";
    }

    /// <summary>What the last reply attached because a feature is on (PromptsReplyAttached): the skills besides the pinned ones
    /// and One moment, by name, and the reply's instructions and tools in tokens; or that no reply ran yet.</summary>
    internal static string DescribeAttached(ReplyPromptRecord? last, SkillSettings? skills)
    {
        if (last is null) return "No reply yet in this session. Companion › Skills says when a feature attaches each skill.";
        var all = SkillSettings.All(skills);
        var attached = last.Skills.Select(key => all.FirstOrDefault(s => s.Key == key))
            .Where(s => s is { Pinned: false } && s.Key != SkillCatalog.OneMoment).Select(s => s!.Name).ToArray();
        var tokens = all.Where(s => attached.Contains(s.Name)).Sum(s => PromptTokens(s.Body));
        return (attached.Length == 0 ? "The last reply carried no skill a feature attaches"
                : $"The last reply also carried {Joined(attached)}, because their feature is on (about {Tokens(tokens)})") +
            $". Its instructions were about {Tokens(last.InstructionTokens)}, with {last.Tools} tool{(last.Tools == 1 ? "" : "s")}" +
            (last.Tools == 0 ? "" : $" (about {Tokens(last.ToolTokens)})") + (last.Director ? "; the director had the other tools." : ".");
    }

    private static string Joined(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1]
    };

    internal static string DescribePrompts(PromptSettings? prompts)
    {
        var total = PromptCatalog.All.Count;
        if (prompts is null) return $"All {total} prompts use Martlet's built-in text.";
        var empty = prompts.Overrides.Count(o => string.IsNullOrWhiteSpace(o.Value));
        var edited = prompts.Overrides.Count - empty;
        var parts = new List<string>();
        if (edited > 0) parts.Add($"{edited} of {total} prompts edited");
        if (empty > 0) parts.Add($"{empty} emptied (not sent)");
        return string.Join(", ", parts) + "; the rest use Martlet's built-in text.";
    }
}
