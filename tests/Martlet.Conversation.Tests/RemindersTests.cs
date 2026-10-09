using System.Text.Json.Nodes;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class RemindersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 12, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private static ReminderBoard Board(params (string Device, ReminderEntry Entry)[] entries) =>
        new(entries.ToDictionary(e => e.Device, e => e.Entry));

    [Fact]
    public void SetsListsAndCancelsAcrossComputers()
    {
        var set = Reminders.Run("""{"action":"set","text":"do the dishes","in_minutes":60}""", ReminderBoard.Empty, "desktop-a",
            ReminderEntry.Empty, Now, Zone, () => "abc123");
        Assert.False(set.Result.IsError, set.Result.Output);
        Assert.Contains("abc123", set.Result.Output);
        Assert.Contains("4:12 PM", set.Result.Output);
        var a = ReminderEntry.Read(set.Own!.Write())!;
        Assert.Equal(Now.AddHours(1), Assert.Single(a.Reminders).Due);

        // Another computer sees it, lists it and cancels it in its own entry.
        var board = Board(("desktop-a", a));
        var listed = Reminders.Run("""{"action":"list"}""", board, "desktop-b", ReminderEntry.Empty, Now, Zone);
        Assert.Contains("do the dishes", listed.Result.Output);
        Assert.Null(listed.Own);
        var canceled = Reminders.Run("""{"action":"cancel","id":"abc123"}""", board, "desktop-b", ReminderEntry.Empty, Now, Zone);
        Assert.False(canceled.Result.IsError, canceled.Result.Output);
        var after = Board(("desktop-a", a), ("desktop-b", canceled.Own!));
        Assert.Equal(ReminderState.Canceled, after.Find("abc123")!.State);
        Assert.Equal("desktop-b", after.Find("abc123")!.SettledBy);
        Assert.Empty(after.Pending);
        Assert.Empty(Reminders.Decide(after, "desktop-a", Now.AddHours(2), alone: true));

        Assert.True(Reminders.Run("""{"action":"cancel","id":"nope"}""", after, "desktop-a", a, Now, Zone).Result.IsError);
        Assert.True(Reminders.Run("""{"action":"set","text":"x"}""", after, "desktop-a", a, Now, Zone).Result.IsError);
        Assert.True(Reminders.Run("not json", after, "desktop-a", a, Now, Zone).Result.IsError);
    }

    [Theory]
    [InlineData("17:30", "2026-10-06T17:30:00Z")]
    [InlineData("5:30 pm", "2026-10-06T17:30:00Z")]
    [InlineData("9am", "2026-10-07T09:00:00Z")]
    [InlineData("tomorrow 9:00", "2026-10-07T09:00:00Z")]
    [InlineData("tomorrow at 8:15 a.m.", "2026-10-07T08:15:00Z")]
    [InlineData("2026-12-24 18:00", "2026-12-24T18:00:00Z")]
    public void ReadsLocalTimes(string at, string expected)
    {
        var (due, problem) = Reminders.At(at, Now, Zone);
        Assert.Null(problem);
        Assert.Equal(DateTimeOffset.Parse(expected), due);
    }

    [Theory]
    [InlineData("today 9:00")]
    [InlineData("whenever")]
    [InlineData("2020-01-01 10:00")]
    public void RefusesTimesItCantUse(string at) => Assert.NotNull(Reminders.At(at, Now, Zone).Problem);

    [Fact]
    public void ACompanionPcAloneSaysItAtOnce()
    {
        var entry = ReminderEntry.Empty.With(new() { Id = "r1", Text = "stretch", Due = Now, Set = Now.AddMinutes(-30) });
        var board = Board(("desktop-a", entry));
        Assert.Empty(Reminders.Decide(board, "desktop-a", Now.AddSeconds(-1), alone: true));
        Assert.Equal(ReminderStep.Claim, Assert.Single(Reminders.Decide(board, "desktop-a", Now, alone: true)).Step);

        var claimed = Board(("desktop-a", entry.Mark("r1", ReminderMarkKind.Claim, Now)));
        Assert.Equal(ReminderStep.Deliver, Assert.Single(Reminders.Decide(claimed, "desktop-a", Now.AddSeconds(2), alone: true)).Step);
        var said = Board(("desktop-a", entry.Mark("r1", ReminderMarkKind.Claim, Now).Mark("r1", ReminderMarkKind.Done, Now.AddSeconds(5))));
        Assert.Equal(ReminderState.Done, said.Find("r1")!.State);
        Assert.Empty(Reminders.Decide(said, "desktop-a", Now.AddSeconds(10), alone: true));

        // Nobody ran when it was due and it is far too late now: let go, not said.
        Assert.Equal(ReminderStep.Miss, Assert.Single(Reminders.Decide(board, "desktop-a", Now + Reminders.LatestLate + TimeSpan.FromMinutes(1), alone: true)).Step);
    }

    [Fact]
    public void TheCompanionPcUsedMostRecentlySaysIt()
    {
        var a = ReminderEntry.Empty.With(new() { Id = "r1", Text = "do the dishes", Due = Now, Set = Now.AddHours(-1) });
        var b = ReminderEntry.Empty;
        // Both bid: A's user has been away 10 minutes, B's typed 5 seconds ago.
        Assert.Equal(ReminderStep.Bid, Assert.Single(Reminders.Decide(Board(("desktop-a", a), ("desktop-b", b)), "desktop-a", Now, false)).Step);
        a = a.Mark("r1", ReminderMarkKind.Bid, Now, idle: 600);
        b = b.Mark("r1", ReminderMarkKind.Bid, Now.AddSeconds(1), idle: 5);
        var bids = Board(("desktop-a", a), ("desktop-b", b));
        Assert.Empty(Reminders.Decide(bids, "desktop-b", Now.AddSeconds(3), false));
        Assert.Empty(Reminders.Decide(bids, "desktop-a", Now.AddSeconds(8), false));
        Assert.Equal(ReminderStep.Claim, Assert.Single(Reminders.Decide(bids, "desktop-b", Now.AddSeconds(8), false)).Step);

        b = b.Mark("r1", ReminderMarkKind.Claim, Now.AddSeconds(8));
        var claimed = Board(("desktop-a", a), ("desktop-b", b));
        Assert.Empty(Reminders.Decide(claimed, "desktop-a", Now.AddSeconds(9), false));
        Assert.Equal(ReminderStep.Deliver, Assert.Single(Reminders.Decide(claimed, "desktop-b", Now.AddSeconds(9), false)).Step);

        var aBid = a;
        // Two that took it at once: the earlier claim says it, the other stands down.
        a = a.Mark("r1", ReminderMarkKind.Claim, Now.AddSeconds(9));
        var both = Board(("desktop-a", a), ("desktop-b", b));
        Assert.Empty(Reminders.Decide(both, "desktop-a", Now.AddSeconds(10), false));
        Assert.Equal(ReminderStep.Deliver, Assert.Single(Reminders.Decide(both, "desktop-b", Now.AddSeconds(10), false)).Step);

        // B never said it: after its claim runs out, the others try again.
        Assert.Equal(ReminderStep.Bid, Assert.Single(Reminders.Decide(Board(("desktop-a", aBid), ("desktop-b", b)), "desktop-a",
            Now + Reminders.ClaimLife + TimeSpan.FromMinutes(1), false)).Step);
    }

    [Fact]
    public void PrunesWhatNobodyNeeds()
    {
        var entry = ReminderEntry.Empty
            .With(new() { Id = "old", Text = "old one", Due = Now.AddDays(-5), Set = Now.AddDays(-6) })
            .With(new() { Id = "new", Text = "new one", Due = Now.AddHours(1), Set = Now })
            .Mark("old", ReminderMarkKind.Done, Now.AddDays(-5))
            .Mark("gone", ReminderMarkKind.Cancel, Now.AddDays(-4))
            .Mark("new", ReminderMarkKind.Bid, Now.AddHours(-2), 10);
        var pruned = entry.Pruned(Board(("desktop-a", entry)), Now);
        Assert.Equal(["new"], pruned.Reminders.Select(r => r.Id));
        Assert.Empty(pruned.Marks);
    }

    [Fact]
    public async Task ADueReminderIsBroughtUpWithItsOwnPrompt()
    {
        using var jobs = new BackgroundJobs();
        var reminder = new Reminder { Id = "r1", Text = "do the dishes", Due = Now, Set = Now.AddHours(-1) };
        var started = jobs.Start(Reminders.Kind, reminder.Text, (_, _) => Task.FromResult(BackgroundJobOutcome.Done(Reminders.Due(reminder, Now, Zone))));
        Assert.True(started.Started);
        for (var i = 0; i < 200 && !started.Job!.Finished; i++) await Task.Delay(10);
        Assert.True(jobs.HasNotice);

        var notes = BackgroundJobs.ReportNotes(null, jobs.Undelivered)!;
        Assert.Contains("do the dishes", notes);
        Assert.Contains("by the way", notes);
        Assert.DoesNotContain("Background work", notes);

        var think = jobs.Start(new("think", 1, 6, TimeSpan.FromMinutes(1)), "a plan", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("The plan.")));
        for (var i = 0; i < 200 && !think.Job!.Finished; i++) await Task.Delay(10);
        var notices = jobs.Take(onItsOwn: true, noticesOnly: true)!;
        Assert.Equal([started.Job!], notices.Jobs);
        var message = BackgroundJobs.ReportMessage(null, notices.Jobs).UserText;
        Assert.StartsWith("(Martlet's note, not said by the user: a reminder", message);
        Assert.Contains("- do the dishes (they asked for it at 2:12 PM, for 3:12 PM)", message);
        notices.Complete();
        Assert.False(jobs.HasNotice);
        Assert.True(jobs.HasNews);

        var mixed = BackgroundJobs.ReportMessage(null, [started.Job!, think.Job!]).UserText;
        Assert.Contains("a reminder they asked you for", mixed);
        Assert.Contains("The plan.", mixed);
        Assert.Contains(PromptCatalog.ReminderDue, PromptCatalog.All.Select(p => p.Id));
    }

    [Fact]
    public void TheToolStaysTheSame()
    {
        Assert.Equal(Reminders.Definition.Description, Reminders.Definition.Description);
        Assert.NotNull(JsonNode.Parse(Reminders.ParametersJson));
        Assert.True(Reminders.Description.Length < 400);
    }

    [Fact]
    public void TheRemindersCheckInTakesTheToolOverAndTheReplyOnlyLists()
    {
        Assert.Equal([Reminders.ToolName], CheckInToolSets.ReminderSet.Replaces);
        Assert.Contains(CheckInToolSets.ReminderSet.Tools, t => t.Name == Reminders.ToolName);
        // The read-only tool has its own name, no set offers it, and its text never changes (request starts stay the same).
        Assert.NotEqual(Reminders.ToolName, Reminders.ListDefinition.Name);
        Assert.DoesNotContain(CheckInToolSets.All.SelectMany(s => s.Tools), t => t.Name == Reminders.ListToolName);
        Assert.Same(Reminders.ListDefinition, Reminders.ListDefinition);
        Assert.NotNull(JsonNode.Parse(Reminders.ListDefinition.ParametersJson));
        Assert.True(Reminders.ListDescription.Length < 400);

        // list_reminders runs the list action alone: it lists and never changes this computer's entry.
        var now = new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);
        var own = Reminders.Run("""{"action":"set","text":"do the dishes","in_minutes":60}""", ReminderBoard.Empty, "desktop-a",
            ReminderEntry.Empty, now, TimeZoneInfo.Utc, () => "r-1").Own!;
        var board = new ReminderBoard(new Dictionary<string, ReminderEntry> { ["desktop-a"] = own });
        var listed = Reminders.Run(Reminders.ListArgumentsJson, board, "desktop-a", own, now, TimeZoneInfo.Utc);
        Assert.Null(listed.Own);
        Assert.False(listed.Result.IsError);
        Assert.Contains("do the dishes", listed.Result.Output);
        Assert.Equal("listed 1", listed.Outcome);
    }
}
