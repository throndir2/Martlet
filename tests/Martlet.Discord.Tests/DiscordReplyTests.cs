namespace Martlet.Discord.Tests;

public sealed class DiscordReplyTests
{
    private static readonly DiscordPlace Channel = new(20, 10, "general", Direct: false);
    private static readonly DiscordPlace Dm = new(30, null, "Ana", Direct: true);
    private static readonly DiscordSpeaker Owner = new(1, "Ana", IsOwner: true);
    private static readonly DiscordSpeaker Friend = new(2, "Bo", IsOwner: false);

    private static DiscordTurn Turn(string text, DiscordPlace? place = null, DiscordSpeaker? speaker = null, bool addressed = true,
        DiscordTurnSource source = DiscordTurnSource.Text, IReadOnlyList<DiscordLine>? recent = null,
        DiscordChatMode mode = DiscordChatMode.Sometimes) =>
        new(place ?? Channel, speaker ?? Friend, text, source, addressed, recent ?? [], mode);

    [Fact]
    public void DiscordHistoryKeepsTheNewestLinesPerPlaceAndForgetsTheOldestPlace()
    {
        var history = new DiscordHistory(new() { HistoryLines = 3, MaxPlaces = 2, MaxLineCharacters = 10 });
        var at = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 5; i++) history.Add("a", new("Bo", $"line {i}", at.AddSeconds(i), false));
        history.Add("b", new("Bo", "a very long line indeed", at, false));
        Assert.Equal(["line 2", "line 3", "line 4"], history.Lines("a").Select(line => line.Text));
        Assert.Equal("a very lon…", history.Lines("b").Single().Text);
        history.Add("c", new("Bo", "hi", at, false));
        Assert.Equal(2, history.Places);
        Assert.Empty(history.Lines("a"));
    }

    [Fact]
    public void DiscordHistoryMergesRecentLinesOnce()
    {
        var history = new DiscordHistory();
        var at = DateTimeOffset.UnixEpoch;
        history.Add("a", new("Bo", "hello", at, false));
        var lines = history.Lines("a", [new("Bo", "hello", at.AddSeconds(2), false), new("Cy", "earlier", at.AddSeconds(-5), false)]);
        Assert.Equal(["earlier", "hello"], lines.Select(line => line.Text));
    }

    [Fact]
    public void DiscordPromptFramesSpeakersAndGroupsLinesIntoAlternatingMessages()
    {
        var at = DateTimeOffset.UnixEpoch;
        var context = new DiscordTurnContext(Turn("what do you think?", addressed: false), [
            new("Martlet", "an orphan reply", at, true),
            new("Ana", "morning all", at, false),
            new("Bo", "hey", at, false),
            new("Martlet", "Morning!", at, true),
            new("Cy", "anyone seen the match?", at, false)
        ], "Ana");

        var prompt = DiscordPrompts.Shape(context);

        Assert.Equal([new(false, "Ana: morning all\nBo: hey"), new(true, "Morning!")], prompt.History);
        Assert.Equal("Cy: anyone seen the match?\nBo: what do you think?", prompt.Message);
        Assert.True(prompt.MayPass);
        Assert.Contains("[pass]", prompt.Note);
        Assert.Contains("#general channel of a Discord server", prompt.Instructions);
        Assert.Contains("Ana is your owner", prompt.Instructions);
        Assert.Contains("2000 characters", prompt.Instructions);
        // The instructions stay the same whether or not the turn may pass, so prompt caches keep the place's conversation.
        Assert.Equal(prompt.Instructions, DiscordPrompts.Shape(context with { Turn = context.Turn with { Addressed = true } }).Instructions);
        Assert.Null(DiscordPrompts.Shape(context with { Turn = context.Turn with { Addressed = true } }).Note);
        Assert.Null(DiscordPrompts.Shape(context with { Turn = context.Turn with { Mode = DiscordChatMode.Always } }).Note);
    }

    [Fact]
    public void DiscordPromptForVoiceAsksForShortSpokenSentences()
    {
        var prompt = DiscordPrompts.Shape(new(Turn("hi", source: DiscordTurnSource.Voice), [], null));
        Assert.Contains("voice call", prompt.Instructions);
        Assert.Contains("no markdown, emojis", prompt.Instructions);
        Assert.DoesNotContain("is your owner", prompt.Instructions);
        Assert.Contains("direct message with Ana", DiscordPrompts.Shape(new(Turn("hi", Dm, Owner), [], "Ana")).Instructions);
    }

    [Theory]
    [InlineData("[pass]", true)]
    [InlineData("  [PASS] ", true)]
    [InlineData("pass.", true)]
    [InlineData("", true)]
    [InlineData("I'd pass on that one.", false)]
    [InlineData("Sure!", false)]
    public void DiscordPassIsRecognized(string text, bool pass) => Assert.Equal(pass, DiscordReplyText.IsPass(text));

    [Fact]
    public void DiscordReplyKeepsWithinTheDiscordLimit()
    {
        var sentence = "This is a sentence that goes on. ";
        var reply = DiscordReplyText.Clean(string.Concat(Enumerable.Repeat(sentence, 100)), DiscordTurnSource.Text, ["Martlet"])!;
        Assert.True(reply.Length <= DiscordReplyText.TextLimit);
        Assert.EndsWith("goes on.", reply);
        var words = DiscordReplyText.Limit(string.Concat(Enumerable.Repeat("word ", 1000)), DiscordReplyText.TextLimit);
        Assert.True(words.Length <= DiscordReplyText.TextLimit);
        Assert.EndsWith("…", words);
        Assert.Equal("Hi there", DiscordReplyText.Clean("Martlet: Hi there", DiscordTurnSource.Text, ["Martlet"]));
    }

    [Fact]
    public void DiscordVoiceReplyIsPlainSpokenText()
    {
        var spoken = DiscordReplyText.Clean("**Sure!** Check [this](https://x.y) <:wave:123> 😀\n- one\n- two", DiscordTurnSource.Voice, []);
        Assert.Equal("Sure! Check this one two", spoken);
    }

    [Fact]
    public void DiscordAmbientGateSpeaksUpOnlyOnceInAWhile()
    {
        var clock = new ManualClock();
        var chance = 0.9;
        var gate = new DiscordAmbientGate(new() { AmbientChance = 0.3, AmbientLinesBetween = 3, AmbientPerHour = 2 }, clock, () => chance);
        var ambient = Turn("nice weather", addressed: false);
        IReadOnlyList<string> names = ["Martlet"];

        Assert.Equal("chance", gate.Check(ambient, [], names));
        Assert.Null(gate.Check(ambient with { Text = "martlet would love this" }, [], names));
        Assert.Null(gate.Check(Turn("nice weather"), [], names));
        Assert.Null(gate.Check(ambient with { Mode = DiscordChatMode.Always }, [], names));
        chance = 0.1;
        Assert.Null(gate.Check(ambient, [], names));

        // Martlet just spoke up unprompted: a cooldown, then others must talk before it does again.
        gate.Spoke(ambient);
        List<DiscordLine> lines = [new("Martlet", "Lovely, isn't it?", clock.GetUtcNow(), true)];
        Assert.Equal("cooldown", gate.Check(ambient, lines, names));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("not_twice_in_a_row", gate.Check(ambient, lines, names));
        lines.Add(new("Bo", "yes", clock.GetUtcNow(), false));
        lines.Add(new("Cy", "very", clock.GetUtcNow(), false));
        Assert.Null(gate.Check(ambient, lines, names));
        gate.Spoke(ambient);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("hourly_limit", gate.Check(ambient, [], names));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(gate.Check(ambient, [], names));
        Assert.True(DiscordAmbientGate.Mentions("Hey MARTLET!", names));
        Assert.False(DiscordAmbientGate.Mentions("martletish", names));
    }

    [Fact]
    public async Task DiscordReplierAnswersPassesAndRemembersThePlace()
    {
        var clock = new ManualClock();
        var asked = new List<DiscordTurnContext>();
        var answers = new Queue<string?>(["Hello Bo!", "[pass]", new string('x', 2500)]);
        var replier = new DiscordReplier((context, _) =>
        {
            asked.Add(context);
            return Task.FromResult(answers.Dequeue());
        }, () => ["Martlet"], new() { AmbientChance = 1, AmbientCooldown = TimeSpan.Zero }, clock, () => 0);

        Assert.Equal("Hello Bo!", (await replier.ReplyAsync(Turn("hi Martlet"), default))?.Text);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await replier.ReplyAsync(Turn("lol", addressed: false), default));
        clock.Advance(TimeSpan.FromSeconds(1));
        var long_ = await replier.ReplyAsync(Turn("tell me everything"), default);

        Assert.Equal(DiscordReplyText.TextLimit, long_!.Text.Length);
        Assert.Equal(["hi Martlet", "Hello Bo!", "lol"], asked[2].Earlier.Select(line => line.Text));
        Assert.True(asked[2].Earlier[1].FromMartlet);
        var stats = replier.Stats;
        Assert.Equal((2, 1, 0, 0), (stats.Replies, stats.Passes, stats.Skipped, stats.Failures));
    }

    [Fact]
    public async Task DiscordReplierSkipsAmbientTurnsWithoutAskingAndReportsFailures()
    {
        var calls = 0;
        var replier = new DiscordReplier((_, _) =>
        {
            calls++;
            throw new DiscordReplyException("thinking.not_set_up");
        }, () => ["Martlet"], new() { AmbientChance = 0 }, new ManualClock(), () => 0.5);

        Assert.Null(await replier.ReplyAsync(Turn("chatter", addressed: false), default));
        Assert.Equal(0, calls);
        Assert.Null(await replier.ReplyAsync(Turn("hey you"), default));
        Assert.Equal(1, calls);
        var stats = replier.Stats;
        Assert.Equal((1, 1, "chance", "thinking.not_set_up"), (stats.Skipped, stats.Failures, stats.LastSkip, stats.LastError));
    }

    [Fact]
    public async Task DiscordReplierAnswersOnePlaceAtATimeAndDropsAmbientTurnsWhileBusy()
    {
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        var replier = new DiscordReplier(async (_, token) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            try { return await release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref running); }
        }, () => ["Martlet"], new() { AmbientChance = 1, MaxConcurrent = 2 }, new ManualClock(), () => 0);

        var first = replier.ReplyAsync(Turn("one"), default);
        var second = replier.ReplyAsync(Turn("two"), default);
        Assert.Null(await replier.ReplyAsync(Turn("aside", addressed: false), default));
        Assert.Equal("place_busy", replier.Stats.LastSkip);
        release.SetResult("ok");
        Assert.Equal("ok", (await first)?.Text);
        Assert.Equal("ok", (await second)?.Text);
        Assert.Equal(1, most);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => now += by;
    }
}
