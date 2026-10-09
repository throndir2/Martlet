namespace Martlet.Conversation.Tests;

public sealed class UnpromptedSpeechTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static async Task<BackgroundJob> Finished(BackgroundJobs jobs, BackgroundJobKind kind)
    {
        var job = jobs.Start(kind, "fixture", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("notice text"))).Job!;
        for (var i = 0; i < 500 && !job.Finished; i++) await Task.Delay(10);
        Assert.True(job.Finished);
        return job;
    }

    [Fact]
    public void Each_kind_of_what_martlet_says_on_its_own_is_known_from_its_jobs()
    {
        var think = new BackgroundJobKind("think", 1, null, null);
        Assert.Equal(UnpromptedKind.Reminder, UnpromptedSpeech.Of(Reminders.Kind));
        Assert.Equal(UnpromptedKind.CheckIn, UnpromptedSpeech.Of(CheckIns.SayKind));
        Assert.Equal(UnpromptedKind.FinishedWork, UnpromptedSpeech.Of(think));
        // A report is the most valuable of what it carries.
        Assert.Equal(UnpromptedKind.Reminder, UnpromptedSpeech.Of([CheckIns.SayKind, Reminders.Kind, think]));
        Assert.Equal(UnpromptedKind.FinishedWork, UnpromptedSpeech.Of([CheckIns.SayKind, think]));
        Assert.Equal(UnpromptedKind.CheckIn, UnpromptedSpeech.Of([CheckIns.SayKind]));
    }

    [Theory]
    [InlineData(UnpromptedKind.Reminder, 600, true, UnpromptedAfterTalkOver.Resume)]
    [InlineData(UnpromptedKind.Reminder, 4000, true, UnpromptedAfterTalkOver.Resume)]
    [InlineData(UnpromptedKind.FinishedWork, 1500, true, UnpromptedAfterTalkOver.Resume)]
    [InlineData(UnpromptedKind.FinishedWork, 1520, true, UnpromptedAfterTalkOver.Drop)]
    [InlineData(UnpromptedKind.CheckIn, 600, true, UnpromptedAfterTalkOver.Resume)]
    [InlineData(UnpromptedKind.CheckIn, 3000, true, UnpromptedAfterTalkOver.Drop)]
    [InlineData(UnpromptedKind.Remark, 100, false, UnpromptedAfterTalkOver.Drop)]
    public void Words_not_for_martlet_resume_or_drop_what_it_said_on_its_own(UnpromptedKind kind, int pausedMs, bool judged,
        UnpromptedAfterTalkOver expect)
    {
        Assert.Equal(judged, UnpromptedSpeech.Judged(kind));
        Assert.Equal(expect, UnpromptedSpeech.AfterNotForMe(kind, TimeSpan.FromMilliseconds(pausedMs)));
    }

    [Fact]
    public async Task A_waiting_check_in_waits_while_you_talk_and_is_dropped_when_old_or_the_conversation_moved_on()
    {
        var clock = new ConversationHistoryTests.SettableClock(Start);
        using var jobs = new BackgroundJobs(clock);
        var checkIn = await Finished(jobs, CheckIns.SayKind);
        var due = checkIn.FinishedUtc!.Value;

        Assert.Equal(NoticeAction.Say, UnpromptedSpeech.Recheck(checkIn, due.AddSeconds(5), null).Action);
        Assert.Equal("mid_utterance", UnpromptedSpeech.Recheck(checkIn, due.AddSeconds(5), null, midUtterance: true).Code);
        Assert.Equal(NoticeAction.Wait, UnpromptedSpeech.Recheck(checkIn, due.AddSeconds(5), null, floor: LiveFloorLevel.Live).Action);
        // An exchange kept before it became due doesn't count; one after it does.
        Assert.Equal(NoticeAction.Say, UnpromptedSpeech.Recheck(checkIn, due.AddSeconds(5), due.AddSeconds(-1)).Action);
        var moved = UnpromptedSpeech.Recheck(checkIn, due.AddSeconds(5), due.AddSeconds(1), midUtterance: true);
        Assert.Equal((NoticeAction.Drop, "moved_on"), (moved.Action, moved.Code));
        var old = UnpromptedSpeech.Recheck(checkIn, due + UnpromptedSpeech.CheckInMaxWait + TimeSpan.FromSeconds(1), null);
        Assert.Equal((NoticeAction.Drop, "too_old"), (old.Action, old.Code));
        Assert.DoesNotContain("notice text", old.Why);
    }

    [Fact]
    public async Task A_due_reminder_and_finished_work_are_never_dropped_and_drop_stale_takes_only_the_old_check_in()
    {
        var clock = new ConversationHistoryTests.SettableClock(Start);
        using var jobs = new BackgroundJobs(clock);
        var reminder = await Finished(jobs, Reminders.Kind);
        var work = await Finished(jobs, new BackgroundJobKind("think", 1, null, null));
        var checkIn = await Finished(jobs, CheckIns.SayKind);
        var later = Start + TimeSpan.FromHours(5);
        Assert.Equal(NoticeAction.Say, UnpromptedSpeech.Recheck(reminder, later, later.AddMinutes(-1)).Action);
        Assert.Equal(NoticeAction.Say, UnpromptedSpeech.Recheck(work, later, later.AddMinutes(-1)).Action);

        Assert.Empty(UnpromptedSpeech.DropStale(jobs, Start.AddMinutes(1), null));
        var dropped = Assert.Single(UnpromptedSpeech.DropStale(jobs, later, null));
        Assert.Same(checkIn, dropped.Job);
        Assert.Equal("too_old", dropped.Check.Code);
        Assert.Equal(BackgroundDeliveryState.Dropped, checkIn.Delivery);
        Assert.Equal(BackgroundDeliveryState.Pending, reminder.Delivery);
        // What is left is taken as before; the dropped check-in never comes up.
        var delivery = jobs.Take(onItsOwn: true)!;
        Assert.DoesNotContain(checkIn, delivery.Jobs);
        Assert.Equal(2, delivery.Jobs.Count);
    }
}
