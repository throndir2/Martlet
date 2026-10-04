using Martlet.Core.Settings;
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
    public void Retuning_follows_the_new_level_without_forgetting_the_looks_so_far()
    {
        var clock = new Clock();
        var pacer = new ScreenCommentaryPacer(Chattiness.Chatty, clock, () => 0.0);
        clock.Advance(ScreenCommentaryPacer.Warmup);
        for (var i = 0; i < 12; i++)
        {
            pacer.NoteLook(commented: false);
            clock.Advance(pacer.Settings.AfterLook);
        }
        Assert.Equal(PacerVerdict.Look, pacer.Decide(false, TimeSpan.Zero));
        // Martlet went quiet: Quiet's budget (12 looks an hour) is already used by the looks taken while chatty.
        pacer.Retune(Chattiness.Quiet);
        Assert.Equal(Chattiness.Quiet, pacer.Chattiness);
        Assert.Equal(ScreenCommentaryPacer.For(Chattiness.Quiet), pacer.Settings);
        Assert.Equal(PacerVerdict.AfterLook, pacer.Decide(false, TimeSpan.Zero));
        clock.Advance(pacer.Settings.AfterLook);
        Assert.Equal(PacerVerdict.HourlyLimit, pacer.Decide(false, TimeSpan.Zero));
        pacer.Retune(Chattiness.Chatty);
        Assert.Equal(PacerVerdict.Look, pacer.Decide(false, TimeSpan.Zero));
    }

    [Fact]
    public void Each_level_paces_what_the_pc_plays_and_martlet_decides_may_cost_up_to_chatty()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), ScreenCommentaryPacer.PcPace(Chattiness.Quiet));
        Assert.Equal(TimeSpan.FromSeconds(20), ScreenCommentaryPacer.PcPace(Chattiness.Normal));
        Assert.Equal(TimeSpan.FromSeconds(12), ScreenCommentaryPacer.PcPace(Chattiness.Chatty));
        Assert.Equal(ScreenCommentaryPacer.For(Chattiness.Chatty), ScreenCommentaryPacer.AtMost(ChattinessChoice.MartletDecides));
        Assert.Equal(ScreenCommentaryPacer.For(Chattiness.Quiet), ScreenCommentaryPacer.AtMost(ChattinessChoice.Quiet));
        // What Companion says before vision is turned on covers the most Martlet may pick.
        Assert.Contains("up to 45 screenshots per hour", LiveConversationConfiguration.ScreenDisclosure(null,
            ChattinessChoice.MartletDecides, new WatchSource(WatchKind.ActiveWindow)));
        Assert.Contains("up to 24 screenshots per hour", LiveConversationConfiguration.ScreenDisclosure(null,
            ChattinessChoice.Normal, new WatchSource(WatchKind.ActiveWindow)));
    }

    [Fact]
    public void The_talk_window_and_companion_say_what_martlet_decided()
    {
        Assert.Equal("", LiveConversationWindow.ChattinessLine(ChattinessChoice.MartletDecides, Chattiness.Quiet, false, null));
        Assert.Equal("", LiveConversationWindow.ChattinessLine(ChattinessChoice.Quiet, Chattiness.Quiet, true, null));
        Assert.Equal("Martlet decides how chatty it is: normal right now.",
            LiveConversationWindow.ChattinessLine(ChattinessChoice.MartletDecides, Chattiness.Normal, true, null));
        Assert.StartsWith("Martlet decides how chatty it is: chatty right now (since ",
            LiveConversationWindow.ChattinessLine(ChattinessChoice.MartletDecides, Chattiness.Chatty, true, DateTime.Now));
        Assert.EndsWith("Right now it is quiet.", MainWindow.ChattinessStatus(ChattinessChoice.MartletDecides, Chattiness.Quiet));
        Assert.Equal(MainWindow.ChattinessAbout, MainWindow.ChattinessStatus(ChattinessChoice.MartletDecides, null));
        Assert.StartsWith("Chatty:", MainWindow.ChattinessStatus(ChattinessChoice.Chatty, Chattiness.Quiet));
        Assert.Contains("went quiet", LiveConversationWindow.ChattinessSwitched(Chattiness.Normal, Chattiness.Quiet));
        Assert.Contains("chattier", LiveConversationWindow.ChattinessSwitched(Chattiness.Quiet, Chattiness.Chatty));
    }

    [Theory]
    [InlineData(3, ChattinessChoice.MartletDecides)]
    [InlineData(0, ChattinessChoice.Quiet)]
    [InlineData(2, ChattinessChoice.Chatty)]
    [InlineData(7, ChattinessChoice.Normal)]
    [InlineData(-1, ChattinessChoice.Normal)]
    public void Martlet_decides_is_saved_and_out_of_range_choices_load_as_normal(int saved, ChattinessChoice loaded)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Talk.Chattiness." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(new TalkPreferences(ScreenChattiness: saved).Save(directory));
            Assert.Equal(loaded, ChattinessTags.Choice(TalkPreferences.Load(directory).ScreenChattiness));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Vision_is_on_and_looks_at_the_whole_screen_by_default_and_saved_choices_are_kept()
    {
        Assert.True(new TalkPreferences().Watch);
        Assert.Equal(WatchKind.ActiveScreen, (WatchKind)new TalkPreferences().ScreenScope);
        Assert.True(TalkPreferences.Load(null).Watch);
        // Before Thinking is set up, Companion › Vision says what it needs rather than asking to turn vision on.
        Assert.Equal("Set up Thinking so Martlet can see.", LiveConversationConfiguration.VisionAdvice(null));
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Talk.Vision." + Guid.NewGuid().ToString("N"));
        async Task<System.Text.Json.JsonElement> Status() => System.Text.Json.JsonSerializer.SerializeToElement(
            await Martlet.Mcp.ChattinessCheck.RunAsync(directory, null, CancellationToken.None));
        try
        {
            // Nothing saved yet: on, at the whole screen; chattiness_status says the same.
            var fresh = TalkPreferences.Load(directory);
            Assert.True(fresh.Watch);
            Assert.Equal(WatchKind.ActiveScreen, (WatchKind)fresh.ScreenScope);
            var status = await Status();
            Assert.True(status.GetProperty("visionOn").GetBoolean());
            Assert.Equal("whole screen", status.GetProperty("visionLooksAt").GetString());

            // A file saved without them (other talk choices only) takes the same defaults.
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), "{\"HandsFree\":false,\"Version\":4}");
            var partial = TalkPreferences.Load(directory);
            Assert.True(partial.Watch);
            Assert.Equal(WatchKind.ActiveScreen, (WatchKind)partial.ScreenScope);
            Assert.True((await Status()).GetProperty("visionOn").GetBoolean());

            // Choices saved in the file are kept: vision turned off, looking at the active window.
            Assert.True((new TalkPreferences() with { Watch = false, ScreenScope = (int)WatchKind.ActiveWindow }).Save(directory));
            var saved = TalkPreferences.Load(directory);
            Assert.False(saved.Watch);
            Assert.Equal(WatchKind.ActiveWindow, (WatchKind)saved.ScreenScope);
            status = await Status();
            Assert.False(status.GetProperty("visionOn").GetBoolean());
            Assert.Equal("active window", status.GetProperty("visionLooksAt").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Duplication_downscale_averages_the_requested_region_into_opaque_pixels()
    {
        // A 40x20 source: the left half blue (B=200), the right half red (R=100); read only the right 20x20.
        const int sourceWidth = 40, sourceHeight = 20;
        var source = new byte[sourceWidth * sourceHeight * 4];
        for (var y = 0; y < sourceHeight; y++)
        for (var x = 0; x < sourceWidth; x++)
            source[(y * sourceWidth + x) * 4 + (x < 20 ? 0 : 2)] = (byte)(x < 20 ? 200 : 100);
        var destination = new byte[4 * 4 * 4];
        DesktopDuplication.Downscale((row, column, into, count) =>
            Buffer.BlockCopy(source, (row * sourceWidth + column) * 4, into, 0, count * 4), 20, 0, 20, 20, destination, 4, 4);
        for (var i = 0; i < 16; i++)
            Assert.Equal((byte[])[0, 0, 100, 255], destination[(i * 4)..(i * 4 + 4)]);
    }

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
