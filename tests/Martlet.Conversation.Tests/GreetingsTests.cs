using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class GreetingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 8, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    [Fact]
    public void TheFactsSayTheTimeTheNameTheLastTalkTheRemindersTheUpdateAndTheApps()
    {
        var conversation = Guid.NewGuid();
        var facts = new GreetingFacts
        {
            Now = Now, Zone = Zone, Companion = "Ivy", UserName = "Sam",
            LastConversation = [new(Guid.NewGuid(), conversation, Now.AddHours(-10), HistoryInputKind.Spoken, "night [all]", "Sleep well!", null)],
            LastMessaging = [new(Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(-1), HistoryInputKind.Typed, "on the bus", "Safe trip!", null,
                new HistorySource(HistoryApps.WhatsApp, "chat"))],
            Reminders =
            [
                new() { Id = "a", Text = "stand-up", Due = Now.AddMinutes(30), Set = Now.AddDays(-1) },
                new() { Id = "b", Text = "next week", Due = Now.AddDays(7), Set = Now.AddDays(-1) }
            ],
            Missed = [new() { Id = "c", Text = "feed the cat", Due = Now.AddHours(-20), Set = Now.AddDays(-1) }],
            UpdatedFrom = "0.70.1", Version = "0.71.0",
            Apps = [new("Visual Studio Code", false), new("Spotify", true)]
        };

        var text = Greetings.Facts(facts);

        Assert.StartsWith("It's Sunday, October 11, 8:30 AM: morning, on the weekend.\n- The user's name is Sam.\n- You last talked on this PC 10 h ago.", text);
        Assert.Contains("The user: \"night (all)\" Ivy: \"Sleep well!\"", text);
        Assert.Contains("Since then, you also talked on WhatsApp 1 h ago. The end of that chat:\n  ", text);
        Assert.Contains("so you couldn't remind them: \"feed the cat\" (for Sat 12:30 PM).", text);
        Assert.Contains("Reminders coming up: \"stand-up\" at 9:00 AM (in 30 min).", text);
        Assert.DoesNotContain("next week", text);
        Assert.Contains("updated from 0.70.1 to 0.71.0", text);
        Assert.EndsWith("Apps open on this PC: Spotify (in front) and Visual Studio Code.", text);
        Assert.Equal("the time of day, your name, your last conversation, your last chat in a messaging app, 1 missed reminder, " +
            "1 reminder coming up, the update and 2 open apps", Greetings.Describe(facts));
    }

    [Fact]
    public void WithoutTheRecordOnlyTheTimeIsSaid()
    {
        var facts = new GreetingFacts { Now = Now.AddHours(15), Zone = Zone };
        Assert.Equal("It's Sunday, October 11, 11:30 PM: night, on the weekend.", Greetings.Facts(facts));
        Assert.Equal("the time of day", Greetings.Describe(facts));
        Assert.Contains("this may be your first", Greetings.Facts(facts with { LastConversation = [] }));
        Assert.Equal("the time of day and that you haven't talked before", Greetings.Describe(facts with { LastConversation = [] }));
        var christmas = facts with { Now = new(2026, 12, 25, 10, 0, 0, TimeSpan.Zero) };
        Assert.Equal("It's Friday, December 25, 10:00 AM: morning. It's Christmas Day.", Greetings.Facts(christmas));
        Assert.Equal("the time of day and a special day", Greetings.Describe(christmas));
    }

    [Theory]
    [InlineData("Sam Rivera", "Sam")]
    [InlineData("Sam", "Sam")]
    [InlineData("Mary-Jane Watson", "Mary-Jane")]
    [InlineData("Ana de la Cruz", "Ana")]
    [InlineData("McKenzie Hart", "McKenzie")]
    [InlineData("DeShawn", "DeShawn")]
    [InlineData("O'Brien", "O'Brien")]
    [InlineData("\u7530\u4E2D", "\u7530\u4E2D")]
    [InlineData("mozar", null)]
    [InlineData("sam rivera", null)]
    [InlineData("xXDarkLordXx", null)]
    [InlineData("DarkLord", null)]
    [InlineData("SAM", null)]
    [InlineData("gamer_42", null)]
    [InlineData("Player 1", null)]
    [InlineData("sam@example.com", null)]
    [InlineData("sam.rivera", null)]
    [InlineData("J. Smith", null)]
    [InlineData("Admin", null)]
    [InlineData("defaultuser0", null)]
    [InlineData("Owner", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyARealNameIsUsed(string? name, string? expected) => Assert.Equal(expected, Greetings.PersonName(name));

    [Fact]
    public async Task TheGreetingIsANoticeWithItsOwnPromptsThatMayStayQuiet()
    {
        Greetings.Kind.Validate();
        Assert.True(Greetings.Kind.Notice);
        Assert.Equal(UnpromptedKind.CheckIn, UnpromptedSpeech.Of(Greetings.Kind));
        Assert.True(PromptCatalog.Required(PromptCatalog.Greeting));
        Assert.Equal(["facts", "silent"], PromptCatalog.All.Single(p => p.Id == PromptCatalog.Greeting).Placeholders);
        Assert.Equal(["facts"], PromptCatalog.All.Single(p => p.Id == PromptCatalog.GreetingNotes).Placeholders);

        using var jobs = new BackgroundJobs();
        var job = jobs.Start(Greetings.Kind, "Greeting", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("It's Sunday."))).Job!;
        for (var i = 0; i < 200 && !job.Finished; i++) await Task.Delay(10);
        Assert.Contains("only if your character would", BackgroundJobs.ReportNotes(null, jobs.Undelivered));
        var delivery = jobs.Take(onItsOwn: true, noticesOnly: true)!;
        var message = BackgroundJobs.ReportMessage(null, delivery.Jobs).UserText;
        Assert.StartsWith("(Martlet's note, not said by the user: Martlet just started on the user's PC", message);
        Assert.Contains("\n- It's Sunday.\n", message);
        Assert.Contains("If your character would greet the user now", message);
        Assert.Contains("replies with exactly [pass]", message);
        Assert.DoesNotContain("{silent}", message);
        delivery.Complete();
        Assert.False(jobs.HasNotice);
    }
}
