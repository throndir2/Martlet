using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Core.Tests;

/// <summary>Martlet's built-in prompts stay small: long or contradicting instructions make small models leak them into
/// replies. The sizes are Martlet's own estimate (<see cref="BoundedTextInput.TextTokens"/>), as Companion › Prompts shows.</summary>
public sealed class PromptBudgetTests
{
    // Every prompt a spoken reply can carry at once, with every feature on (tools, research, guides, singing, voice tags, the
    // character, always listening, PC sound, a picture, adult content, chattiness, memory and lore), without the persona's
    // own text and the tag lists. (Things done after the reply is left out: a reply offered every tool has none handed off.)
    private static readonly string[] EverythingOnReply =
    [
        PromptCatalog.Persona, PromptCatalog.SeenWithMessage, PromptCatalog.PersonalPrivate,
        PromptCatalog.Notes, PromptCatalog.MemoryRecall, PromptCatalog.MemoryPeople, PromptCatalog.MemoryBrief,
        PromptCatalog.Lorebook, PromptCatalog.PastConversations, PromptCatalog.SaidLately
    ];

    // The skills one reply never carries together with the others: the other forms of a recorded voice, the Discord call
    // (instead of PC sound), singing without tools (instead of the song tools), Things done on their own (instead of the tools) and Pictures as words (instead of the picture).
    private static readonly string[] OtherForms =
        [SkillCatalog.HeardVoiceOnly, SkillCatalog.VoiceDescribed, SkillCatalog.DiscordCall, SkillCatalog.SongTags, SkillCatalog.HandedOff,
            SkillCatalog.ScreenAsWords];

    [Fact]
    public void AllBuiltInPromptsTogetherStayUnderTenThousandTokens()
    {
        var total = PromptCatalog.All.Sum(p => BoundedTextInput.TextTokens(p.Default));
        Assert.True(total <= 10_000, $"All built-in prompts together: about {total:N0} tokens.");
    }

    [Fact]
    public void AReplyWithEveryFeatureOnCarriesAtMostTwoThousandTokensOfBuiltInPrompts()
    {
        // With every skill a feature attaches, and the pinned ones.
        var reply = EverythingOnReply.Sum(id => BoundedTextInput.TextTokens(PromptCatalog.Default(id))) +
            SkillCatalog.All.Where(s => !OtherForms.Contains(s.Key)).Sum(s => BoundedTextInput.TextTokens(s.Body));
        Assert.True(reply <= 2_000, $"A reply with every feature on: about {reply:N0} tokens of built-in prompts.");
    }

    [Fact]
    public void AnAverageReplyCarriesOnlyThePersonaWrapperOneMomentAndThePinnedSkills()
    {
        // Typed, with no tools and no feature on: what every reply to you starts with, besides the persona's own text.
        var always = BoundedTextInput.TextTokens(PromptCatalog.Default(PromptCatalog.Persona)) +
            BoundedTextInput.TextTokens(SkillSettings.Guidance(null, SkillCatalog.OneMoment)!) +
            BoundedTextInput.TextTokens(PromptSettings.ReplyClosing(null, null, false, "pass")!);
        Assert.True(always <= 400, $"An average reply: about {always} tokens of built-in instructions.");
        Assert.Equal([SkillCatalog.LookIntoIt, SkillCatalog.KeepItGoing, SkillCatalog.ReplyLength],
            SkillSettings.All(null).Where(s => s.Pinned).Select(s => s.Key));
    }

    [Fact]
    public void NoBuiltInSkillIsLong()
    {
        var longest = SkillCatalog.All.MaxBy(s => BoundedTextInput.TextTokens(s.Body))!;
        Assert.True(BoundedTextInput.TextTokens(longest.Body) <= 350, $"{longest.Key}: about {BoundedTextInput.TextTokens(longest.Body)} tokens.");
    }

    [Fact]
    public void NoBuiltInPromptIsLong()
    {
        var longest = PromptCatalog.All.MaxBy(p => BoundedTextInput.TextTokens(p.Default))!;
        Assert.True(BoundedTextInput.TextTokens(longest.Default) <= 350, $"{longest.Id}: about {BoundedTextInput.TextTokens(longest.Default)} tokens.");
    }
}
