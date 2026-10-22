using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>The user's edits to Martlet's internal prompts (Companion › Prompts), by prompt ID. A prompt that isn't listed uses
/// its built-in text; an empty text sends nothing for that prompt. Absent from settings while nothing is edited. The prompts
/// and their built-in texts are in <see cref="PromptCatalog"/>; the rules for edits are in <see cref="PromptEditRules"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed partial record PromptSettings : IContract
{
    public const int MaximumTextCharacters = 8_192;
    public const int MaximumAggregateUtf8Bytes = 49_152;

    private readonly IReadOnlyDictionary<string, string> overrides = null!;
    private readonly IReadOnlyDictionary<string, string>? moved;

    /// <summary>The edits by prompt ID. Edits saved by an older Martlet are upgraded as they are read
    /// (<see cref="PromptEditRules.Upgrade"/>); edits of prompts that became skills go to <see cref="Moved"/>.</summary>
    public required IReadOnlyDictionary<string, string> Overrides
    {
        get => overrides;
        init => overrides = PromptEditRules.Upgrade(value, out moved);
    }

    /// <summary>Edits an older Martlet saved for prompts that are built-in skills now (<see cref="SkillCatalog.MovedPrompts"/>), by
    /// prompt ID, never saved again here: <see cref="SkillSettings.WithMovedPrompts"/> moves them to their skills.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? Moved => moved;

    [JsonIgnore]
    public bool IsDefault => Overrides.Count == 0;

    /// <summary>The user's skills (Companion › Skills) for the requests these prompts build, never saved here: settings.json keeps
    /// them in their own section (<see cref="AppSettings.Skills"/>). Set it with <see cref="ForPersonal"/>.</summary>
    [JsonIgnore]
    public SkillSettings? Skills { get; init; }

    /// <summary>Companion › Replies › Personal content for these prompts, never saved: <see cref="Fill"/> fills {private} with
    /// the Personal content: private prompt while it is Private, else with Personal content: shared. Set it with
    /// <see cref="ForPersonal"/>.</summary>
    [JsonIgnore]
    public PersonalContent Personal { get; init; }

    /// <summary><paramref name="prompts"/> as a conversation uses them with <paramref name="generation"/>'s Personal content and
    /// the user's <paramref name="skills"/>. Null stays null while it is Private and no skill is edited, so unedited prompts keep
    /// their usual shape.</summary>
    public static PromptSettings? ForPersonal(PromptSettings? prompts, GenerationSettings? generation, SkillSettings? skills = null)
    {
        var personal = GenerationSettings.Personal(generation);
        if (personal == PersonalContent.Private && prompts is null && skills is null) return null;
        return (prompts ?? new PromptSettings { Overrides = new Dictionary<string, string>(StringComparer.Ordinal) }) with
        {
            Personal = personal, Skills = skills
        };
    }

    /// <summary>What {private} says with <paramref name="settings"/> (empty when the owner emptied that prompt).</summary>
    public static string PersonalText(PromptSettings? settings) =>
        Text(settings, (settings?.Personal ?? PersonalContent.Private) == PersonalContent.Private
            ? PromptCatalog.PersonalPrivate : PromptCatalog.PersonalShared).Trim();

    public void Validate() => PromptEditRules.Validate(this);

    /// <summary>Only the prompts that differ from their built-in text; null when none do.</summary>
    public static PromptSettings? Normalize(IReadOnlyDictionary<string, string>? edits)
    {
        if (edits is null) return null;
        var kept = edits.Where(e => PromptCatalog.Find(e.Key) is { } definition && e.Value != definition.Default)
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        return kept.Count == 0 ? null : new PromptSettings { Overrides = kept };
    }

    /// <summary>The prompt's text: the user's edit, or the built-in text.</summary>
    public static string Text(PromptSettings? settings, string id) =>
        settings?.Overrides.TryGetValue(id, out var text) == true ? text : PromptCatalog.Default(id);

    /// <summary>The prompt's text with its placeholders filled in, or null when the user emptied it (nothing is sent). Values are
    /// inserted in one pass, so a placeholder inside a value (such as persona text) stays as written. {private} is filled with
    /// <see cref="PersonalText"/> unless a value for it is given.</summary>
    public static string? Fill(PromptSettings? settings, string id, params (string Name, string Value)[] values)
    {
        var text = Text(settings, id);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("{private}", StringComparison.Ordinal) && values.All(v => v.Name != "private"))
            values = [.. values, ("private", PersonalText(settings))];
        return FillText(text, values);
    }

    /// <summary><paramref name="text"/> with its placeholders filled in, in one pass; unknown placeholders stay as written.</summary>
    public static string FillText(string text, params (string Name, string Value)[] values)
    {
        if (values.Length == 0) return text;
        return Placeholder().Replace(text, match =>
            values.FirstOrDefault(v => v.Name == match.Groups[1].Value) is { Name: not null } found ? found.Value : match.Value);
    }

    /// <summary>What closes the instructions of a reply to what the user typed or said, the same text every time, so the start
    /// of every request stays the same and prompt caches keep it: the pinned skills (Companion › Skills,
    /// <see cref="SkillSettings.Pinned"/>), then the Keep the conversation going prompt, then the Short first
    /// sentence prompt when the reply is <paramref name="spoken"/> and Companion › Replies › Short first sentence is on
    /// (<see cref="GenerationSettings.StartsShort"/>), then the Reply length prompt last, where models weigh it most. Null when
    /// none is sent. <paramref name="silent"/> is the word the model answers to stay quiet.</summary>
    public static string? ReplyClosing(PromptSettings? settings, GenerationSettings? generation, bool spoken, string silent)
    {
        var pinned = SkillSettings.Pinned(settings, silent);
        var moves = Fill(settings, PromptCatalog.ConversationMoves);
        var first = spoken && GenerationSettings.StartsShort(generation)
            ? Fill(settings, PromptCatalog.ShortFirstSentence, ("silent", silent)) : null;
        var length = Fill(settings, PromptCatalog.ReplyLength);
        var parts = new[] { pinned, moves, first, length }.Where(part => part is not null).ToArray();
        return parts.Length == 0 ? null : string.Join("\n\n", parts);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{([a-z_]+)\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Placeholder();
}
