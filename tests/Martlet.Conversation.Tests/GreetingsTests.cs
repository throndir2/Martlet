using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class GreetingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 8, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    [Fact]
    public void TheFactsSayTheTimeTheLastTalkTheRemindersComingUpAndTheApps()
    {
        var conversation = Guid.NewGuid();
        var facts = new GreetingFacts
        {
            Now = Now, Zone = Zone, Companion = "Ivy",
            LastConversation = [new(Guid.NewGuid(), conversation, Now.AddHours(-10), HistoryInputKind.Spoken, "night [all]", "Sleep well!", null)],
            Reminders =
            [
                new() { Id = "a", Text = "stand-up", Due = Now.AddMinutes(30), Set = Now.AddDays(-1) },
                new() { Id = "b", Text = "next week", Due = Now.AddDays(7), Set = Now.AddDays(-1) }
            ],
            Apps = [new("Visual Studio Code", false), new("Spotify", true)]
        };

        var text = Greetings.Facts(facts);

        Assert.StartsWith("It's Sunday, October 11, 8:30 AM: morning.\n- You last talked 10 h ago.", text);
        Assert.Contains("The user: \"night (all)\" Ivy: \"Sleep well!\"", text);
        Assert.Contains("Reminders coming up: \"stand-up\" at 9:00 AM (in 30 min).", text);
        Assert.DoesNotContain("next week", text);
        Assert.EndsWith("Apps open on this PC: Spotify (in front) and Visual Studio Code.", text);
        Assert.Equal("the time of day, your last conversation, 1 reminder coming up and 2 open apps", Greetings.Describe(facts));
    }

    [Fact]
    public void WithoutTheRecordOnlyTheTimeIsSaid()
    {
        var facts = new GreetingFacts { Now = Now.AddHours(15), Zone = Zone };
        Assert.Equal("It's Sunday, October 11, 11:30 PM: night.", Greetings.Facts(facts));
        Assert.Equal("the time of day", Greetings.Describe(facts));
        Assert.Contains("this may be your first", Greetings.Facts(facts with { LastConversation = [] }));
        Assert.Equal("the time of day and that you haven't talked before", Greetings.Describe(facts with { LastConversation = [] }));
    }

    [Fact]
    public async Task TheGreetingIsANoticeWithItsOwnPrompts()
    {
        Greetings.Kind.Validate();
        Assert.True(Greetings.Kind.Notice);
        Assert.Equal(UnpromptedKind.CheckIn, UnpromptedSpeech.Of(Greetings.Kind));
        Assert.True(PromptCatalog.Required(PromptCatalog.Greeting));
        Assert.Equal(["facts"], PromptCatalog.All.Single(p => p.Id == PromptCatalog.GreetingNotes).Placeholders);

        using var jobs = new BackgroundJobs();
        var job = jobs.Start(Greetings.Kind, "Greeting", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("It's Sunday."))).Job!;
        for (var i = 0; i < 200 && !job.Finished; i++) await Task.Delay(10);
        Assert.Contains("Answer what the user just said first", BackgroundJobs.ReportNotes(null, jobs.Undelivered));
        var delivery = jobs.Take(onItsOwn: true, noticesOnly: true)!;
        var message = BackgroundJobs.ReportMessage(null, delivery.Jobs).UserText;
        Assert.StartsWith("(Martlet's note, not said by the user: Martlet just started on the user's PC", message);
        Assert.Contains("\n- It's Sunday.\n", message);
        delivery.Complete();
        Assert.False(jobs.HasNotice);
    }
}
