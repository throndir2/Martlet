using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Prompts says what one reply sends (the persona prompt, the pinned skills and One moment every time, the
/// skills the last reply carried because their feature is on), and Companion › Skills marks each skill pinned, attached now,
/// attached when its feature is on or loaded on demand by the director.</summary>
public sealed class ReplySummaryPageTests
{
    [Fact]
    public void ThePromptsPageSaysWhatEveryReplySendsWithItsTokens()
    {
        var always = PromptsPageModel.DescribeEveryReply(null, null, "Mira", "Kind and curious.");
        Assert.StartsWith("Every reply to you sends about ", always, StringComparison.Ordinal);
        Assert.Contains("the persona prompt with your persona (about ", always, StringComparison.Ordinal);
        Assert.Contains("the pinned skills Look into it, Keep the conversation going and Reply length (about ", always, StringComparison.Ordinal);
        Assert.Contains("One moment (about ", always, StringComparison.Ordinal);
        Assert.DoesNotContain("Kind and curious", always, StringComparison.Ordinal);
        // Unpinning a skill takes it out of every reply.
        var unpinned = SkillSettings.Normalize([SkillSettings.Find(null, SkillCatalog.KeepItGoing)! with { Pinned = false }]);
        Assert.Contains("the pinned skills Look into it and Reply length", PromptsPageModel.DescribeEveryReply(null, unpinned, "Mira", ""),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThePromptsPageSaysWhichSkillsTheLastReplyCarried()
    {
        Assert.StartsWith("No reply yet in this session.", PromptsPageModel.DescribeAttached(null, null), StringComparison.Ordinal);
        var last = new ReplyPromptRecord([SkillCatalog.LookIntoIt, SkillCatalog.OneMoment, SkillCatalog.AlwaysListening, SkillCatalog.VoiceTags],
            512, 3, 140, true, DateTimeOffset.UnixEpoch);
        var text = PromptsPageModel.DescribeAttached(last, null);
        Assert.StartsWith("The last reply also carried Always listening and Voice sounds and tones, because their feature is on", text,
            StringComparison.Ordinal);
        Assert.Contains("Its instructions were about 512 tokens, with 3 tools (about 140 tokens); the director had the other tools.", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Look into it", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EachSkillSaysHowAReplyGetsIt()
    {
        string State(string key, IReadOnlyCollection<string>? attached = null) =>
            SkillsPageModel.SkillState(SkillSettings.Find(null, key)!, null, attached);
        Assert.StartsWith("Built-in skill; Pinned: every reply to you gets it", State(SkillCatalog.LookIntoIt), StringComparison.Ordinal);
        Assert.StartsWith("Built-in skill; Attached when: replies to what always listening heard", State(SkillCatalog.AlwaysListening),
            StringComparison.Ordinal);
        Assert.StartsWith("Built-in skill; Attached now: the last reply carried it", State(SkillCatalog.AlwaysListening, [SkillCatalog.AlwaysListening]),
            StringComparison.Ordinal);
        var own = new Skill { Key = "recipes", Name = "Recipes", UseWhen = "Recipes.", Body = "Give one recipe." };
        Assert.StartsWith("Your own skill; Loaded on demand by the director", SkillsPageModel.SkillState(own, null), StringComparison.Ordinal);
        Assert.All(SkillCatalog.All, s => Assert.False(string.IsNullOrWhiteSpace(s.When)));
        Assert.EndsWith("The last reply carried 2 skills.", SkillsPageModel.DescribeSkills(null,
            new([SkillCatalog.LookIntoIt, SkillCatalog.ReplyLength], 100, 0, 0, false, DateTimeOffset.UnixEpoch)), StringComparison.Ordinal);
    }
}
