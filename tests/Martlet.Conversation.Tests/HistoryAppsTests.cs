using Martlet.Conversation;
using static Martlet.Conversation.Tests.ConversationHistoryTests;

namespace Martlet.Conversation.Tests;

/// <summary>Exchanges from messaging apps in the record: their source, single-message deletes and edits, what each app is asked
/// to do, and the queue of changes for the apps.</summary>
public sealed class HistoryAppsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "Martlet.HistoryApps.Tests." + Guid.NewGuid().ToString("N"));
    private readonly SettableClock clock = new(new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static HistorySource Discord(string? server = "800", string[]? user = null, string[]? reply = null) =>
        new(HistoryApps.Discord, "900", server, "#general", user ?? ["1"], reply ?? ["2", "3"]);

    [Fact]
    public async Task SourcesAndMessageIdsAreKeptAndFoundAgainAfterARestart()
    {
        var history = new ConversationHistory(folder, clock);
        await history.LoadAsync();
        var conversation = Guid.NewGuid();
        var id = Guid.NewGuid();
        await history.AppendAsync(conversation, HistoryInputKind.Typed, "Hi from Telegram", "Hello!", "Sam",
            new HistorySource(HistoryApps.Telegram, "42", null, "Sam", ["101"]), id);
        await history.AppendAsync(conversation, HistoryInputKind.Typed, "Hi here", "Hey!", null, new HistorySource(HistoryApps.Pc));
        var found = history.FindMessage(HistoryApps.Telegram, "42", "101");
        Assert.Equal(id, found?.Id);
        await history.ChangeAsync(id, exchange => exchange with { Source = exchange.Source! with { ReplyMessages = ["102", "103"] } });

        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync();
        var telegram = reread.Exchange(id)!;
        Assert.Equal(HistoryApps.Telegram, telegram.App);
        Assert.Equal(["101"], telegram.Source!.UserMessages);
        Assert.Equal(["102", "103"], telegram.Source.ReplyMessages);
        Assert.Equal("Sam", telegram.Source.ChatName);
        var pc = reread.Exchanges(conversation)[1];
        Assert.Null(pc.Source);
        Assert.Equal(HistoryApps.Pc, pc.App);
        var listed = Assert.Single(reread.Conversations());
        Assert.Equal([HistoryApps.Telegram, HistoryApps.Pc], listed.Apps);
        Assert.Equal(1, reread.Stats.Apps![HistoryApps.Telegram]);
    }

    [Fact]
    public async Task EditingAndDeletingOneExchangeRewritesOnlyItsLineAndTheIndex()
    {
        var history = new ConversationHistory(folder, clock);
        await history.LoadAsync();
        var conversation = Guid.NewGuid();
        var kept = await history.AppendAsync(conversation, HistoryInputKind.Typed, "Tell me about otters", "Otters hold hands.", null);
        var edited = await history.AppendAsync(conversation, HistoryInputKind.Typed, "And penguins?", "Penguins waddle.", null);
        var gone = await history.AppendAsync(conversation, HistoryInputKind.Typed, "Goodbye walrus", "Bye!", null);

        var (before, after) = await history.ChangeAsync(edited.Id, exchange => exchange with { Reply = "Penguins slide on their bellies.", Edited = clock.Now });
        Assert.Equal("Penguins waddle.", before!.Reply);
        Assert.Equal(edited.At, after!.At);
        var (_, deleted) = await history.ChangeAsync(gone.Id, _ => null);
        Assert.Null(deleted);
        Assert.Empty(history.Search(["walrus"], null, null, null, 5));
        Assert.Single(history.Search(["bellies"], null, null, null, 5));
        Assert.Empty(history.Search(["waddle"], null, null, null, 5));
        var missing = await history.ChangeAsync(Guid.NewGuid(), exchange => exchange);
        Assert.Null(missing.Before);
        Assert.Null(missing.After);

        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync();
        Assert.Equal([kept.Id, edited.Id], reread.Exchanges(conversation).Select(exchange => exchange.Id));
        Assert.Equal("Penguins slide on their bellies.", reread.Exchange(edited.Id)!.Reply);
        Assert.NotNull(reread.Exchange(edited.Id)!.Edited);
    }

    [Fact]
    public async Task DiscordIsNeverRecalledInTheTalkWindow()
    {
        var history = new ConversationHistory(folder, clock);
        await history.LoadAsync();
        await history.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "The lighthouse mural is by Mira.", "Nice!", "Ana", Discord());
        await history.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "My dentist is on Friday.", "Noted.", "Sam",
            new HistorySource(HistoryApps.Telegram, "42"));
        var zone = TimeZoneInfo.Utc;
        Assert.Empty(PastConversations.Recall(history, "Do you remember who painted the lighthouse mural?", clock.Now, zone, null));
        Assert.Single(PastConversations.Recall(history, "Do you remember what I said about the dentist?", clock.Now, zone, null));
    }

    [Fact]
    public void DeletingAsksEachAppOnlyForWhatItAllows()
    {
        var now = clock.Now;
        var server = new HistoryExchange(Guid.NewGuid(), Guid.NewGuid(), now.AddDays(-30), HistoryInputKind.Typed, "u", "r", "Ana", Discord());
        var both = HistoryPlatforms.Delete(server, null, now);
        Assert.Equal(["1", "2", "3"], both.Changes.Select(change => change.Message));
        Assert.Equal([false, true, true], both.Changes.Select(change => change.Own));
        Assert.Null(both.Source!.UserMessages);
        Assert.Null(both.Source.ReplyMessages);

        var dm = server with { Source = Discord(server: null) };
        var plan = HistoryPlatforms.Delete(dm, HistorySide.User, now);
        Assert.Empty(plan.Changes);
        Assert.Contains("DM", Assert.Single(plan.KeptThere));

        var telegram = server with { Source = new HistorySource(HistoryApps.Telegram, "42", null, null, ["9"], ["10"]) };
        Assert.Empty(HistoryPlatforms.Delete(telegram, null, now).Changes);
        Assert.Equal(2, HistoryPlatforms.Delete(telegram with { At = now.AddHours(-47) }, null, now).Changes.Count);

        var whatsapp = server with { Source = new HistorySource(HistoryApps.WhatsApp, "15551234", null, null, ["wamid.1"], ["wamid.2"]) };
        Assert.Empty(HistoryPlatforms.Delete(whatsapp, null, now).Changes);
        Assert.Empty(HistoryPlatforms.Delete(server with { Source = null }, null, now).Changes);
    }

    [Fact]
    public void EditingAReplyEditsItsPiecesAndDeletesOrCutsTheRest()
    {
        var now = clock.Now;
        var exchange = new HistoryExchange(Guid.NewGuid(), Guid.NewGuid(), now, HistoryInputKind.Typed, "u", "r", null, Discord());
        var shorter = HistoryPlatforms.Edit(exchange, "Short now.", now);
        Assert.Equal([(PlatformChangeKind.Edit, "2", "Short now."), (PlatformChangeKind.Delete, "3", null)],
            shorter.Changes.Select(change => (change.Kind, change.Message, change.Text)));
        Assert.Equal(["2"], shorter.Source!.ReplyMessages);
        Assert.False(shorter.Truncated);

        var two = HistoryPlatforms.Edit(exchange, new string('a', 1500) + " " + new string('b', 1500), now);
        Assert.Equal([PlatformChangeKind.Edit, PlatformChangeKind.Edit], two.Changes.Select(change => change.Kind));
        Assert.False(two.Truncated);

        var longer = HistoryPlatforms.Edit(exchange, new string('x', 7000), now);
        Assert.True(longer.Truncated);
        Assert.All(longer.Changes, change => Assert.True(change.Text!.Length <= 2000));
        Assert.EndsWith("…", longer.Changes[^1].Text);
        Assert.Single(longer.KeptThere);

        Assert.NotNull(HistoryPlatforms.WhyNotEdit(exchange.Source!, HistorySide.User));
        Assert.Empty(HistoryPlatforms.Edit(exchange with { Source = exchange.Source! with { App = HistoryApps.WhatsApp } }, "x", now).Changes);
    }

    [Fact]
    public async Task TheQueueGoesAtEachAppsPaceWaitsOutSlowDownsAndKeepsWhatWaits()
    {
        var file = Path.Combine(folder, PlatformChanges.FileName);
        var queue = new PlatformChanges(file, clock);
        var app = new FakeApp(clock) { Answers = new([PlatformFailure.RateLimited, null, PlatformFailure.Refused, PlatformFailure.Unavailable, null]) };
        PlatformChange Change(string on, string message) => new(Guid.NewGuid(), on, "900", null, message, PlatformChangeKind.Delete, true, null, clock.Now);
        queue.Enqueue([Change(HistoryApps.Discord, "a"), Change(HistoryApps.Discord, "b"), Change(HistoryApps.Discord, "c"),
            Change(HistoryApps.Telegram, "t")]);
        Assert.Null(await queue.RunDueAsync());
        queue.Connect(app);
        var start = clock.Now;
        for (var round = 0; round < 20; round++)
        {
            if (await queue.RunDueAsync() is not { } wait) break;
            clock.Now += wait;
        }
        // Slowed down for 3 s, then "a" at 3 s, "b" refused at 4 s, "c" unreachable at 5 s and made 5 s later.
        Assert.Equal([("a", 3.0), ("c", 10.0)], app.Made.Select(made => (made.Message, (made.At - start).TotalSeconds)));
        var status = queue.Status;
        Assert.Equal((1, 2, 1, 0), (status.Pending, status.Done, status.Refused, status.GaveUp));
        Assert.Equal(1, status.PendingByApp[HistoryApps.Telegram]);
        Assert.Contains("not connected", status.Describe());

        var restarted = new PlatformChanges(file, clock);
        Assert.Equal("t", Assert.Single(restarted.Pending).Message);
        Assert.Equal(1, restarted.Clear());
        Assert.Empty(new PlatformChanges(file, clock).Pending);
    }

    [Fact]
    public async Task AnAppThatStaysUnreachableIsGivenUpOn()
    {
        var queue = new PlatformChanges(null, clock);
        var app = new FakeApp(clock) { Answers = new(Enumerable.Repeat<PlatformFailure?>(PlatformFailure.Unavailable, 20)) };
        queue.Enqueue([new(Guid.NewGuid(), HistoryApps.Discord, "900", null, "a", PlatformChangeKind.Delete, true, null, clock.Now)]);
        queue.Connect(app);
        for (var round = 0; round < 40; round++)
        {
            if (await queue.RunDueAsync() is not { } wait) break;
            clock.Now += wait;
        }
        Assert.Equal((0, 1), (queue.Status.Pending, queue.Status.GaveUp));
        Assert.Equal(PlatformChanges.MaximumAttempts, app.Calls);
    }

    private sealed class FakeApp(SettableClock clock) : IPlatformMessages
    {
        public Queue<PlatformFailure?> Answers { get; init; } = new();
        public List<(string Message, DateTimeOffset At)> Made { get; } = [];
        public int Calls { get; private set; }
        public string App => HistoryApps.Discord;
        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public Task ApplyAsync(PlatformChange change, CancellationToken token)
        {
            Calls++;
            var answer = Answers.Count > 0 ? Answers.Dequeue() : null;
            if (answer is { } failure)
                throw new PlatformChangeException(failure, "fixture", failure == PlatformFailure.RateLimited ? TimeSpan.FromSeconds(3) : null);
            Made.Add((change.Message, clock.Now));
            return Task.CompletedTask;
        }
    }
}
