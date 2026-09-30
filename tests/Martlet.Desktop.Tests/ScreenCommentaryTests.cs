using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class ScreenCommentaryTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan time) => ticks += time.Ticks;
    }

    [Fact]
    public void Pacer_waits_for_warmup_and_never_looks_while_busy_or_right_after_talking()
    {
        var clock = new Clock();
        var pacer = new ScreenCommentaryPacer(Chattiness.Normal, clock, () => 0.0);
        Assert.Equal(PacerVerdict.WarmingUp, pacer.Decide(false, TimeSpan.Zero));
        clock.Advance(ScreenCommentaryPacer.Warmup);
        Assert.Equal(PacerVerdict.Busy, pacer.Decide(true, TimeSpan.Zero));
        pacer.NoteConversation();
        Assert.Equal(PacerVerdict.AfterConversation, pacer.Decide(false, TimeSpan.Zero));
        clock.Advance(pacer.Settings.AfterConversation);
        Assert.Equal(PacerVerdict.Look, pacer.Decide(false, TimeSpan.Zero));
    }

    [Fact]
    public void Pacer_holds_back_after_a_remark_and_after_a_silent_look_and_respects_the_hourly_budget()
    {
        var clock = new Clock();
        var pacer = new ScreenCommentaryPacer(Chattiness.Chatty, clock, () => 0.0);
        clock.Advance(ScreenCommentaryPacer.Warmup);
        pacer.NoteLook(commented: true);
        Assert.Equal(PacerVerdict.AfterComment, pacer.Decide(false, TimeSpan.Zero));
        clock.Advance(pacer.Settings.AfterComment);
        Assert.Equal(PacerVerdict.Look, pacer.Decide(false, TimeSpan.Zero));
        pacer.NoteLook(commented: false);
        Assert.Equal(PacerVerdict.AfterLook, pacer.Decide(false, TimeSpan.Zero));
        for (var i = pacer.LooksThisHour; i < pacer.Settings.LooksPerHour; i++)
        {
            clock.Advance(pacer.Settings.AfterLook);
            pacer.NoteLook(commented: false);
        }
        clock.Advance(pacer.Settings.AfterLook);
        Assert.Equal(PacerVerdict.HourlyLimit, pacer.Decide(false, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(PacerVerdict.Look, pacer.Decide(false, TimeSpan.Zero));
    }

    [Fact]
    public void Pacer_does_not_talk_to_an_empty_room_and_looks_more_when_the_picture_changes()
    {
        var clock = new Clock();
        var roll = 0.3;
        var pacer = new ScreenCommentaryPacer(Chattiness.Normal, clock, () => roll);
        clock.Advance(ScreenCommentaryPacer.Warmup);
        pacer.NoteLook(commented: false);
        clock.Advance(pacer.Settings.AfterLook);
        Assert.Equal(PacerVerdict.UserAway, pacer.Decide(false, ScreenCommentaryPacer.AwayAfter));
        Assert.Equal(PacerVerdict.NothingNew, pacer.Decide(false, TimeSpan.Zero));
        pacer.ObserveFrame(0.4);
        Assert.Equal(PacerVerdict.Look, pacer.Decide(false, ScreenCommentaryPacer.AwayAfter));
    }

    [Theory]
    [InlineData("[pass]", true)]
    [InlineData(" Pass. ", true)]
    [InlineData("[PASS] nothing new", true)]
    [InlineData("", true)]
    [InlineData("Nice dodge, that boss almost had you.", false)]
    [InlineData("Passing through the castle already?", false)]
    public void Silent_reply_is_recognized_without_swallowing_real_remarks(string text, bool silent) =>
        Assert.Equal(silent, LiveConversationController.IsSilentReply(text));

    [Fact]
    public void Scene_signature_notices_change_but_is_only_a_tiny_grey_thumbnail()
    {
        var dark = new byte[64 * 36 * 4];
        var bright = Enumerable.Repeat((byte)200, dark.Length).ToArray();
        var a = ScreenGlancer.Signature(dark, 64, 36);
        var b = ScreenGlancer.Signature(bright, 64, 36);
        Assert.Equal(16 * 9, a.Length);
        Assert.All(a, value => Assert.Equal(0, value));
        Assert.All(b, value => Assert.Equal(200, value));
    }
}
