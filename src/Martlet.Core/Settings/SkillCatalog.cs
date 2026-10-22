namespace Martlet.Core.Settings;

/// <summary>One built-in skill (Companion › Skills): a named instruction pack that is not in every request. <paramref name="Key"/>
/// is its saved key, <paramref name="UseWhen"/> one line that says when it helps (the director reads it in the skill index),
/// <paramref name="Body"/> its built-in text, <paramref name="Pinned"/> whether every reply gets it by default, and
/// <paramref name="Tools"/> the reply tools it explains: a reply that is offered one of them gets it with that tool's guidance.
/// <paramref name="Placeholders"/> are filled in by Martlet, as in a prompt.</summary>
public sealed record SkillDefinition(string Key, string Name, string UseWhen, string Body, bool Pinned, IReadOnlyList<string> Tools,
    IReadOnlyList<string> Placeholders);

/// <summary>The built-in skills (docs/CONVERSATION.md#skills). The rules that explain Martlet's tools were prompts in every reply
/// once; they are skills now, so a reply gets them only while it is offered their tools, and the director loads them when it
/// needs them. <see cref="LookIntoIt"/> is pinned: every reply gets it. The user's edits and own skills are in
/// <see cref="SkillSettings"/>.</summary>
public static class SkillCatalog
{
    public const string LookIntoIt = "look-into-it", UsingTools = "using-tools", ThinkingLonger = "thinking-longer",
        WebResearch = "web-research", Songs = "songs", AppGuides = "app-guides";

    public const string DefaultLookIntoIt =
        "When something needs a tool, a look-up or more thought, say briefly that you'll do it or look into it. Never say " +
        "it's done before it is, or guess facts.";

    public static IReadOnlyList<SkillDefinition> All { get; } =
    [
        new(LookIntoIt, "Look into it", "Always: what to say when something needs a tool, a look-up or more thought.",
            DefaultLookIntoIt, true, [], []),
        new(UsingTools, "Using tools", "Before you call any tool.", PromptCatalog.DefaultToolInstructions, false, [], []),
        new(ThinkingLonger, "Thinking longer", "When a task needs careful multi-step work: lyrics, a story, a plan, hard math or code.",
            PromptCatalog.DefaultThinkLongerInstructions, false, ["think_longer"], []),
        new(WebResearch, "Web research", "When a fact may be unknown, uncertain or out of date, or the user asks you to research something.",
            PromptCatalog.DefaultWebResearchInstructions, false, ["research"], []),
        new(Songs, "Songs", "When the user asks for a song, to play one or to stop singing.",
            PromptCatalog.DefaultSingingInstructions, false, ["sing_song", "play_song", "stop_singing"], ["silent"]),
        new(AppGuides, "App guides", "When the user asks about a game or an app, or asks Martlet to read up on one.",
            PromptCatalog.DefaultAppGuidesInstructions, false, ["read_up_on", "search_guide", "skip_guide"], [])
    ];

    /// <summary>The prompts that became built-in skills, by prompt ID: an edit saved for one of them moves to its skill
    /// (<see cref="SkillSettings.WithMovedPrompts"/>).</summary>
    public static IReadOnlyDictionary<string, string> MovedPrompts { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tools"] = UsingTools, ["think_longer"] = ThinkingLonger, ["web_research"] = WebResearch, ["singing"] = Songs,
        ["app_guides"] = AppGuides
    };

    private static readonly Dictionary<string, SkillDefinition> ByKey = All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    public static SkillDefinition? Find(string? key) => key is null ? null : ByKey.GetValueOrDefault(key);
}
