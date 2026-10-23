using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed partial class CheckInsTests
{
    [Fact]
    public void TheDirectorRunsOnTheUsersTurnWithEverySetThatReplacesReplyTools()
    {
        var director = Built(CheckIns.Director);
        Assert.Equal((true, CheckInOutcome.Tools, PromptCatalog.CheckInDirector, CheckInTriggers.UserTurn, ThinkingJobKind.Director),
            (director.On, director.Outcome, director.PromptId, director.Triggers, director.JobKind));
        Assert.Equal(CheckInFacts.Asked | CheckInFacts.Latest | CheckInFacts.Work | CheckInFacts.Skills, director.Facts);
        Assert.Equal(CheckInConditions.None, director.Conditions);
        // Its sets are computed, in the card's order: Martlet's next words, every set that takes over reply tools, People, skills
        // and knowledge.
        Assert.Equal(CheckInToolSets.All.Where(s => s.Replaces.Count > 0 || s.Id is CheckInToolSets.NextReplyId or SkillTools.SetId or
            KnowledgeTools.SetId or PeopleTools.SetId).Select(s => s.Id), director.ToolSets);
        Assert.Equal([CheckInToolSets.NextReplyId, SkillTools.SetId, KnowledgeTools.SetId, PcTools.SetId, GuideToolSet.SetId],
            new[] { CheckInToolSets.NextReplyId, SkillTools.SetId, KnowledgeTools.SetId, PcTools.SetId, GuideToolSet.SetId }.Where(director.ToolSets.Contains));
        Assert.True(director.Needs.HasFlag(ThinkingCapability.Tools));
        Assert.Equal(CheckInFacts.Asked | CheckInFacts.Latest | CheckInFacts.Work | CheckInFacts.Skills,
            CheckInPrompts.Placed(PromptSettings.Text(null, PromptCatalog.CheckInDirector)));
        Assert.Equal("you to say or type something to Martlet", CheckIns.TriggerWords(CheckInTriggers.UserTurn));
        Assert.Equal(CheckIns.ByTouches | CheckInTriggers.ExchangeEnded | CheckInTriggers.Spoke | CheckInTriggers.UserTurn, CheckIns.AllTriggers);
        Assert.DoesNotContain(CheckIns.BuiltIn, c => c.Id == CheckIns.Actions);
        // Its pool kind sits between thinking longer and the summaries, and waits while you talk on shared hardware.
        var row = ThinkingJobKinds.Row(ThinkingJobKind.Director);
        Assert.Equal(("director", ThinkingPriority.Director, false, ThinkingJobWhileLive.Stops), (row.Key, row.Priority, row.Fast, row.WhileLive));
        Assert.True(ThinkingPriority.ThinkLonger < ThinkingPriority.Director && ThinkingPriority.Director < ThinkingPriority.Digest);
    }

    [Fact]
    public void AnExchangeTriggerCountsTheExchangesItKeepsAndWaitsLongerThanTouches()
    {
        var first = CheckIns.ExchangeTrigger(Now.AddSeconds(-20));
        var second = CheckIns.ExchangeTrigger(Now, first);
        Assert.Equal((CheckInTriggers.ExchangeEnded, "1 exchange"), (first.Fired, first.What));
        Assert.Equal((Now, "2 exchanges"), (second.At, second.What));
        Assert.Equal("3 exchanges", CheckIns.ExchangeTrigger(Now, second).What);
        Assert.Equal("1 exchange", CheckIns.ExchangeTrigger(Now, new(CheckInTriggers.TouchesEnded, Now, "4 touches")).What);
        Assert.Equal(CheckIns.ExchangeAge, CheckIns.Age(first));
        Assert.Equal(CheckIns.TriggerAge, CheckIns.Age(new(CheckInTriggers.TouchesEnded, Now, "1 touch")));

        var turn = CheckIns.TurnTrigger(Now.AddSeconds(-5));
        Assert.Equal((CheckInTriggers.UserTurn, "you said something to Martlet"), (turn.Fired, turn.What));
        Assert.Equal(CheckIns.ExchangeAge, CheckIns.Age(turn));
        var director = Built(CheckIns.Director);
        Assert.True(CheckIns.Fires(director, turn, Now));
        Assert.False(CheckIns.Fires(director, turn with { At = Now - CheckIns.ExchangeAge - TimeSpan.FromSeconds(1) }, Now));
        Assert.False(CheckIns.Fires(director, first, Now));
        Assert.False(CheckIns.Fires(Built(CheckIns.Reactions), turn, Now));
    }

    [Fact]
    public void TheUsersTurnHasNoCooldownSoEveryMessageGetsTheDirector()
    {
        var director = Built(CheckIns.Director);
        var justRan = new CheckInRun(Now.AddSeconds(-5), 3, "called 1 tool", true);
        Assert.Null(CheckInSchedule.Wait(director, State(), justRan, fired: CheckIns.TurnTrigger(Now)));
        Assert.StartsWith("it waits for you to say", CheckInSchedule.Wait(director, State(), justRan));
    }

    [Fact]
    public void TheDirectorReadsWhatTheUserJustSaidAndTheSkillIndex()
    {
        Assert.Equal("The latest exchange:\nUser: This bug is driving me crazy.\nMira: Ooh, want to talk it through?", CheckInPrompts.Latest(State()));
        var since = CheckInPrompts.Latest(State() with { Since = 2 });
        Assert.StartsWith("The latest 2 exchanges, oldest first:\nUser: Back to work.", since);
        Assert.DoesNotContain("oven", since);
        Assert.Contains("check the oven", CheckInPrompts.Latest(State() with { Since = 0 }));
        Assert.Equal("Nothing has been said in this conversation yet.", CheckInPrompts.Latest(State() with { Exchanges = [], Exchanged = 0 }));
        var index = Director.SkillIndex(null)!;
        Assert.DoesNotContain(SkillCatalog.LookIntoIt, index);
        Assert.Contains("web-research: When a fact may be unknown", index);
        var message = CheckInPrompts.Message(Built(CheckIns.Director),
            State() with { Since = 3, Asked = "What's the weather in Oslo?", Skills = index }, null)!;
        Assert.Contains("The user just said: What's the weather in Oslo?", message);
        Assert.Contains("User: This bug is driving me crazy.", message);
        Assert.Contains("a trip plan", message);
        Assert.Contains("songs: When the user asks for a song", message);
        Assert.Contains("Use your tools for what needs doing", message);
        Assert.DoesNotContain("{asked}", message);
        Assert.DoesNotContain("{skills}", message);
        var job = CheckInPrompts.Prepare(Built(CheckIns.Director), State() with { Asked = "hi" }, null)!;
        Assert.Equal(ThinkingJobKind.Director, job.Kind);
        Assert.Equal("Director: The director", job.Label);
    }

    [Fact]
    public void LoadSkillGivesASkillsTextAndSaysWhenThereIsNone()
    {
        var loaded = Director.Load(null, "songs", "Mira", "pass");
        Assert.False(loaded.IsError);
        Assert.StartsWith("Loaded the skill songs.\n", loaded.Output);
        Assert.Contains("reply [pass] unless talked to", loaded.Output);
        Assert.True(Director.Load(null, "nope", "Mira", "pass").IsError);
        Assert.True(Director.Load(null, null, "Mira", "pass").IsError);
        var own = new SkillSettings { Skills = [new Skill { Key = "recipes", Name = "Recipes", UseWhen = "Recipes.", Body = "One recipe, {name}." }] };
        Assert.Equal("Loaded the skill recipes.\nOne recipe, Mira.", Director.Load(own, "recipes", "Mira", "pass").Output);
        Assert.Contains("recipes: Recipes.", Director.SkillIndex(own));
    }

    [Fact]
    public void KnowledgeAnswersBestFirstWithCountsOnlyOnItsFirstLine()
    {
        Assert.Equal("Found nothing. Martlet doesn't keep anything about it on this PC.", KnowledgeTools.Answer([]).Output);
        var answer = KnowledgeTools.Answer([new("memory", "Likes tea.", 0.4), new("guide: Elden Ring › Bosses", "Margit is weak to bleed.", 0.9),
            new("lorebook: Places", new string('x', 900), 0.5)]).Output;
        var lines = answer.Split('\n');
        Assert.Equal("Found 3 things.", lines[0]);
        Assert.StartsWith("- (guide: Elden Ring › Bosses) Margit", lines[1]);
        Assert.EndsWith("...", lines[2]);
        Assert.True(lines[2].Length < 450);
    }

    [Fact]
    public void ACallPcToolCallNamesTheToolAndItsArguments()
    {
        var (call, problem) = PcTools.Parse(new("c1", PcTools.CallName, """{"name":"files_read","arguments":{"path":"a.txt"}}"""));
        Assert.Null(problem);
        Assert.Equal(("c1", "files_read", """{"path":"a.txt"}"""), (call!.CallId, call.Name, call.ArgumentsJson));
        Assert.Equal("{}", PcTools.Parse(new("c2", PcTools.CallName, """{"name":"x"}""")).Call!.ArgumentsJson);
        Assert.NotNull(PcTools.Parse(new("c3", PcTools.CallName, "{}")).Problem);
        Assert.StartsWith("1 tool of this PC.", PcTools.List([new("files_read", "Reads a file.", """{"type":"object"}""")]).Output);
    }

    [Fact]
    public void TheReplyHandsToolsOffOnlyWhileTheDirectorIsOnAndThePoolCallsTools()
    {
        var replaced = CheckInToolSets.All.Where(s => s.Replaces.Count > 0).SelectMany(s => s.Replaces).Distinct(StringComparer.Ordinal);
        Assert.Equal(replaced, CheckIns.HandOffs(null, null, poolCallsTools: true).Select(h => h.Tool));
        Assert.Contains(PcTools.ServersName, replaced);
        Assert.All(CheckIns.HandOffs(null, null, poolCallsTools: true), h => Assert.Equal(CheckIns.Director, h.CheckInId));
        Assert.Empty(CheckIns.HandOffs(null, null, poolCallsTools: false));
        // The director's own pool kind decides, not the check-ins'.
        Assert.Empty(CheckIns.HandOffs(null, null, kind => kind == ThinkingJobKind.CheckIn));
        Assert.NotEmpty(CheckIns.HandOffs(null, null, kind => kind == ThinkingJobKind.Director));
        var off = new CheckInSettings().With(CheckIns.Director, false, 1);
        Assert.Empty(CheckIns.HandOffs(off, null, poolCallsTools: true));
        // The pool is asked only when a check-in would take a tool over.
        var asked = false;
        Assert.Empty(CheckIns.HandOffs(off, null, () => asked = true));
        Assert.False(asked);
        // An emptied prompt never runs, so it takes nothing over.
        var empty = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInDirector] = "" } };
        Assert.Empty(CheckIns.HandOffs(null, empty, poolCallsTools: true));
    }

    [Fact]
    public void ActOnWhatWasSaidTurnedOffStaysOffAsTheDirector()
    {
        var old = new CheckInSettings { BuiltIn = new Dictionary<string, CheckInChoice> { [CheckIns.Actions] = new(false, 1) } };
        Assert.False(old.BuiltIn.ContainsKey(CheckIns.Actions));
        Assert.False(CheckIns.All(old).Single(c => c.Id == CheckIns.Director).On);
        old.Validate();
        var on = new CheckInSettings { BuiltIn = new Dictionary<string, CheckInChoice> { [CheckIns.Actions] = new(true, 1) } };
        Assert.True(CheckIns.All(on).Single(c => c.Id == CheckIns.Director).On);
        // A choice for the director itself wins.
        var both = new CheckInSettings
        {
            BuiltIn = new Dictionary<string, CheckInChoice> { [CheckIns.Actions] = new(false, 1), [CheckIns.Director] = new(true, 1) }
        };
        Assert.True(CheckIns.All(both).Single(c => c.Id == CheckIns.Director).On);
        Assert.Null(PromptSettings.Normalize(new Dictionary<string, string> { ["check_in_actions"] = "old" }));
    }

    [Fact]
    public void TheReplyIsToldBrieflyThatHandedOffThingsHappenAfterIt()
    {
        CheckInHandOff Hand(string tool, string set) => new(tool, CheckIns.Director, "The director", set.ToLowerInvariant(), set);
        Assert.Null(CheckIns.HandOffGuidance([], null));
        var one = CheckIns.HandOffGuidance([Hand("reminders", "Reminders")], null)!;
        Assert.StartsWith("These happen on their own while you answer: Reminders.", one);
        Assert.Contains("just say you'll do it", one);
        Assert.Contains(": Reminders; Memory; Songs, pictures and creations.",
            CheckIns.HandOffGuidance([Hand("reminders", "Reminders"), Hand("manage_memories", "Memory"), Hand("x", "Memory"), Hand("sing_song", "Songs, pictures and creations")], null));
        Assert.Null(CheckIns.HandOffGuidance([Hand("reminders", "Reminders")], SkillSettings.Edited(SkillCatalog.HandedOff, "")));
    }
}
