using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Core.Tests;

/// <summary>Martlet's built-in prompts stay small: long or contradicting instructions make small models leak them into
/// replies. The sizes are Martlet's own estimate (<see cref="BoundedTextInput.TextTokens"/>), as Companion › Prompts shows.</summary>
public sealed class PromptBudgetTests
{
    // Every prompt a spoken reply can carry at once, with every feature on (tools, research, guides, singing, voice tags, the
    // character, always listening, PC sound, a picture, adult content, chattiness, memory and lore), without the persona's
    // own text and the tag lists.
    private static readonly string[] EverythingOnReply =
    [
        PromptCatalog.Persona, PromptCatalog.Tools, PromptCatalog.ThinkLonger, PromptCatalog.HandedOffTools, PromptCatalog.WebResearch,
        PromptCatalog.AppGuides, PromptCatalog.Singing, PromptCatalog.VoiceTags, PromptCatalog.CharacterActions,
        PromptCatalog.CharacterGaze, PromptCatalog.Listening, PromptCatalog.PcAudio, PromptCatalog.Moment, PromptCatalog.AdultContent,
        PromptCatalog.ChattinessDecides, PromptCatalog.SeenWithMessage, PromptCatalog.SeenTag, PromptCatalog.PersonalPrivate,
        PromptCatalog.Notes, PromptCatalog.Voices, PromptCatalog.MemoryRecall, PromptCatalog.MemoryPeople, PromptCatalog.MemoryBrief,
        PromptCatalog.Lorebook, PromptCatalog.PastConversations, PromptCatalog.ConversationMoves, PromptCatalog.ShortFirstSentence,
        PromptCatalog.ReplyLength, PromptCatalog.SaidLately
    ];

    [Fact]
    public void AllBuiltInPromptsTogetherStayUnderTenThousandTokens()
    {
        var total = PromptCatalog.All.Sum(p => BoundedTextInput.TextTokens(p.Default));
        Assert.True(total <= 10_000, $"All built-in prompts together: about {total:N0} tokens.");
    }

    [Fact]
    public void AReplyWithEveryFeatureOnCarriesAtMostTwoThousandTokensOfBuiltInPrompts()
    {
        var reply = EverythingOnReply.Sum(id => BoundedTextInput.TextTokens(PromptCatalog.Default(id)));
        Assert.True(reply <= 2_000, $"A reply with every feature on: about {reply:N0} tokens of built-in prompts.");
    }

    [Fact]
    public void NoBuiltInPromptIsLong()
    {
        var longest = PromptCatalog.All.MaxBy(p => BoundedTextInput.TextTokens(p.Default))!;
        Assert.True(BoundedTextInput.TextTokens(longest.Default) <= 350, $"{longest.Id}: about {BoundedTextInput.TextTokens(longest.Default)} tokens.");
    }
}
