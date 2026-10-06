using Martlet.Discord;

namespace Martlet.Discord.Tests;

public sealed class DiscordTextChatTests
{
    private static readonly DiscordPlace Server = new(2001, 3001, "#general in Home", Direct: false);
    private static readonly DiscordPlace Dm = new(4001, null, "DM with Ann", Direct: true);
    private static readonly DiscordSpeaker Ann = new(1001, "Ann", IsOwner: true);

    private static DiscordIncoming Said(string text, DiscordPlace? place = null, ulong id = 1, bool mention = false,
        bool reply = false, bool bot = false) => new(id, place ?? Server, Ann, text, mention, reply, bot);

    [Theory]
    [InlineData("hey martlet, how are you", true)]
    [InlineData("I love martlets", false)]
    [InlineData("Jane what do you think?", true)]
    [InlineData("Jane Doe!", true)]
    [InlineData("nothing to see", false)]
    public void NameSaidMatchesWholeWordsOfNames(string text, bool expected) =>
        Assert.Equal(expected, DiscordAddressing.NameSaid(text, ["Martlet", "Jane Doe"]));

    [Fact]
    public void AddressedByDmMentionReplyOrName()
    {
        string[] names = ["Martlet"];
        Assert.True(DiscordAddressing.IsAddressed(Said("hi", Dm), names));
        Assert.True(DiscordAddressing.IsAddressed(Said("hi", mention: true), names));
        Assert.True(DiscordAddressing.IsAddressed(Said("hi", reply: true), names));
        Assert.True(DiscordAddressing.IsAddressed(Said("hi Martlet"), names));
        Assert.False(DiscordAddressing.IsAddressed(Said("hi all"), names));
    }

    [Fact]
    public void WithoutMentionRemovesTheBotsMentionOnly() =>
        Assert.Equal("hello <@42>", DiscordAddressing.WithoutMention("<@!99> hello <@42> <@99>", 99));

    [Fact]
    public void SanitizeNeutralizesMassAndRoleMentions()
    {
        var clean = DiscordTextFormat.Sanitize("@everyone and @here and <@&123> but <@5>");
        Assert.DoesNotContain("@everyone", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("@here", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("<@&123>", clean, StringComparison.Ordinal);
        Assert.Contains("<@5>", clean);
    }

    [Fact]
    public void SplitKeepsEveryPieceUnderTheLimitAtBreaks()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => new string('a', 90) + $" paragraph {i}."));
        var pieces = DiscordTextFormat.Split(text);
        Assert.True(pieces.Count > 1);
        Assert.All(pieces, piece => Assert.InRange(piece.Length, 1, DiscordTextFormat.Limit));
        Assert.All(pieces, piece => Assert.EndsWith(".", piece));
        Assert.Equal(text.Replace("\n", "").Replace(" ", ""), string.Concat(pieces).Replace("\n", "").Replace(" ", ""));
    }

    [Fact]
    public void SplitHardCutsTextWithoutBreaks()
    {
        var pieces = DiscordTextFormat.Split(new string('x', 4500));
        Assert.Equal([2000, 2000, 500], pieces.Select(piece => piece.Length));
        Assert.Equal(["short"], DiscordTextFormat.Split("  short  "));
    }

    [Fact]
    public void RecentLinesAreBoundedPerPlaceAndInPlaces()
    {
        var recent = new DiscordRecentLines(perPlace: 3, places: 2);
        for (var i = 0; i < 5; i++) recent.Add(Server, new("Ann", $"line {i}", DateTimeOffset.UnixEpoch, false));
        Assert.Equal(["line 2", "line 3", "line 4"], recent.Before(Server).Select(line => line.Text));
        var id = recent.Add(Dm, new("Ann", "dm 1", DateTimeOffset.UnixEpoch, false));
        recent.Add(Dm, new("Ann", "dm 2", DateTimeOffset.UnixEpoch, false));
        Assert.Equal(["dm 1"], recent.Before(Dm, id + 1).Select(line => line.Text));
        recent.Add(new(5, 6, "other", false), new("Bo", "x", DateTimeOffset.UnixEpoch, false));
        Assert.Equal(2, recent.Places);
        Assert.Empty(recent.Before(Server)); // least recently used place forgotten
    }

    [Theory]
    [InlineData(DiscordChatMode.Off, false, DiscordTextOutcome.NotConsidered)]
    [InlineData(DiscordChatMode.Off, true, DiscordTextOutcome.NotConsidered)]
    [InlineData(DiscordChatMode.Mentions, false, DiscordTextOutcome.NotConsidered)]
    [InlineData(DiscordChatMode.Mentions, true, DiscordTextOutcome.Answered)]
    [InlineData(DiscordChatMode.Sometimes, false, DiscordTextOutcome.Passed)]
    [InlineData(DiscordChatMode.Sometimes, true, DiscordTextOutcome.Answered)]
    [InlineData(DiscordChatMode.Always, false, DiscordTextOutcome.Answered)]
    public async Task ModeGatesTurns(DiscordChatMode mode, bool mention, DiscordTextOutcome expected)
    {
        var (chat, transport, engine) = Make(mode);
        var result = await chat.HandleAsync(Said("what's up", mention: mention));
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected is DiscordTextOutcome.Answered ? 1 : 0, transport.Sent.Count);
        if (expected != DiscordTextOutcome.NotConsidered) Assert.Equal(mention, engine.Turns.Single().Addressed);
    }

    [Fact]
    public async Task AnswersAddressedTurnAsAReplyWithRecentLinesAndTyping()
    {
        var (chat, transport, engine) = Make(DiscordChatMode.Mentions);
        await chat.HandleAsync(Said("earlier chatter", id: 1));
        var result = await chat.HandleAsync(Said("Martlet, hi", id: 2));
        Assert.Equal(DiscordTextOutcome.Answered, result.Outcome);
        Assert.Equal((Server.ChannelId, "Reply to: Martlet, hi", (ulong?)2), transport.Sent.Single());
        Assert.Equal(1, transport.Typed);
        Assert.Equal(["earlier chatter"], engine.Turns.Single().Recent.Select(line => line.Text));
        Assert.Contains(chat.Recent.Before(Server), line => line.FromMartlet);
        var stats = chat.Stats;
        Assert.Equal((2, 1, 1, "server"), (stats.Seen, stats.Considered, stats.Answered, stats.LastPlaceKind));
    }

    [Fact]
    public async Task AmbientReplyIsNotAReplyAndLongRepliesSplit()
    {
        var (chat, transport, engine) = Make(DiscordChatMode.Always, options: new() { MinimumInterval = TimeSpan.Zero });
        engine.Answer = _ => new string('w', 2500) + " @everyone";
        await chat.HandleAsync(Said("talk"));
        Assert.Equal(2, transport.Sent.Count);
        Assert.All(transport.Sent, sent => Assert.Null(sent.ReplyTo));
        Assert.DoesNotContain("@everyone", transport.Sent[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoresBotsAndEmptyMessagesButKeepsBotLinesAsContext()
    {
        var (chat, transport, engine) = Make(DiscordChatMode.Always);
        Assert.Equal(DiscordTextOutcome.IgnoredBot, (await chat.HandleAsync(Said("Martlet beep", bot: true))).Outcome);
        Assert.Equal(DiscordTextOutcome.Empty, (await chat.HandleAsync(Said("   "))).Outcome);
        Assert.Empty(engine.Turns);
        Assert.Empty(transport.Sent);
        Assert.Single(chat.Recent.Before(Server));
    }

    [Fact]
    public async Task NoEngineIsReportedAsAProblem()
    {
        var transport = new FakeTransport();
        var chat = new DiscordTextChat(transport, () => null, _ => DiscordChatMode.Always, () => ["Martlet"]);
        var result = await chat.HandleAsync(Said("hi"));
        Assert.Equal(DiscordTextOutcome.NoEngine, result.Outcome);
        Assert.Equal((1, "The reply engine isn't ready yet."), (chat.Stats.Failed, chat.Stats.LastError));
    }

    [Fact]
    public async Task DropsAStaleTurnAndTheNewerOneRepliesToTheAddressedMessage()
    {
        var (chat, transport, engine) = Make(DiscordChatMode.Always);
        var release = new TaskCompletionSource();
        var thinking = new TaskCompletionSource();
        engine.Wait = async turn =>
        {
            if (turn.Text == "Martlet, first") { thinking.TrySetResult(); await release.Task; }
        };
        var first = chat.HandleAsync(Said("Martlet, first", id: 10));
        await thinking.Task;
        var second = chat.HandleAsync(Said("second", id: 11));
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(DiscordTextOutcome.Dropped, results[0].Outcome);
        Assert.Equal(DiscordTextOutcome.Answered, results[1].Outcome);
        Assert.True(results[1].Addressed); // carried over from the dropped addressed turn
        Assert.Equal((ulong?)10, transport.Sent.Single().ReplyTo);
        Assert.Contains("Martlet, first", engine.Turns[1].Recent.Select(line => line.Text));
        Assert.Equal(1, chat.Stats.Dropped);
    }

    [Fact]
    public async Task KeepsStaleTurnsWhenDroppingIsOff()
    {
        var (chat, transport, engine) = Make(DiscordChatMode.Always,
            options: new() { DropStaleTurns = false, MinimumInterval = TimeSpan.Zero });
        var release = new TaskCompletionSource();
        var thinking = new TaskCompletionSource();
        engine.Wait = async turn => { if (turn.Text == "first") { thinking.TrySetResult(); await release.Task; } };
        var first = chat.HandleAsync(Said("first", id: 1));
        await thinking.Task;
        var second = chat.HandleAsync(Said("second", id: 2));
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(["Reply to: first", "Reply to: second"], transport.Sent.Select(sent => sent.Text));
    }

    [Fact]
    public async Task CommandsAreAddressedAndDeliveredByTheCaller()
    {
        var (chat, transport, engine) = Make(DiscordChatMode.Mentions);
        List<string> delivered = [];
        var result = await chat.CommandAsync(Said("no name here", Dm), pieces => { delivered.AddRange(pieces); return Task.CompletedTask; });
        Assert.Equal(DiscordTextOutcome.Answered, result.Outcome);
        Assert.Equal(["Reply to: no name here"], delivered);
        Assert.Empty(transport.Sent);
        Assert.Equal(0, transport.Typed);
        Assert.Equal("DM", chat.Stats.LastPlaceKind);
    }

    [Fact]
    public async Task PacesMessagesPerChannel()
    {
        var (chat, transport, engine) = Make(DiscordChatMode.Always, options: new() { MinimumInterval = TimeSpan.FromMilliseconds(300) });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await chat.HandleAsync(Said("one", id: 1));
        await chat.HandleAsync(Said("two", id: 2));
        Assert.Equal(2, transport.Sent.Count);
        Assert.True(watch.ElapsedMilliseconds >= 250, $"took {watch.ElapsedMilliseconds} ms");
    }

    private static (DiscordTextChat, FakeTransport, FakeEngine) Make(DiscordChatMode mode, DiscordTextOptions? options = null)
    {
        var transport = new FakeTransport();
        var engine = new FakeEngine { Mode = mode };
        return (new DiscordTextChat(transport, () => engine, _ => mode, () => ["Martlet"], options ?? new() { MinimumInterval = TimeSpan.Zero }),
            transport, engine);
    }

    private sealed class FakeTransport : IDiscordTextTransport
    {
        public List<(ulong Channel, string Text, ulong? ReplyTo)> Sent { get; } = [];
        public int Typed;

        public IDisposable Typing(DiscordPlace place)
        {
            Interlocked.Increment(ref Typed);
            return new Done();
        }

        public Task SendAsync(DiscordPlace place, string text, ulong? replyTo, CancellationToken token)
        {
            lock (Sent) Sent.Add((place.ChannelId, text, replyTo));
            return Task.CompletedTask;
        }

        private sealed class Done : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeEngine : IDiscordReplyEngine
    {
        public List<DiscordTurn> Turns { get; } = [];
        public Func<DiscordTurn, string?> Answer { get; set; } = turn => "Reply to: " + turn.Text;
        public Func<DiscordTurn, Task> Wait { get; set; } = _ => Task.CompletedTask;

        public async Task<DiscordReply?> ReplyAsync(DiscordTurn turn, CancellationToken token)
        {
            lock (Turns) Turns.Add(turn);
            await Wait(turn);
            // Like the real engine, stay quiet on an ambient turn it may pass (Sometimes mode).
            if (DiscordChatRules.MayPass(Mode, turn.Addressed)) return null;
            return Answer(turn) is { } text ? new(text) : null;
        }

        public DiscordChatMode Mode { get; set; }
    }
}
