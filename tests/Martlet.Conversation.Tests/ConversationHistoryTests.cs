using System.Text;
using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

public sealed class ConversationHistoryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "Martlet.History.Tests." + Guid.NewGuid().ToString("N"));
    private readonly SettableClock clock = new(new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task AppendedExchangesAreReadBackAfterARestart()
    {
        var history = new ConversationHistory(folder, clock);
        var conversation = Guid.NewGuid();
        var appended = await history.AppendAsync(conversation, HistoryInputKind.Spoken, "My dog is called Biscuit.",
            "What a lovely name!", "Sam");
        clock.Now = clock.Now.AddMinutes(1);
        await history.AppendAsync(conversation, HistoryInputKind.Report, "", "I finished the song.", null);
        Assert.False(history.Loaded);

        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync();
        var exchanges = reread.Exchanges(conversation);
        Assert.Equal(2, exchanges.Count);
        Assert.Equal(appended, exchanges[0]);
        Assert.Equal(HistoryInputKind.Report, exchanges[1].Kind);
        Assert.Null(exchanges[1].Speaker);
        var only = Assert.Single(reread.Conversations());
        Assert.Equal(2, only.Exchanges);
        Assert.Equal("My dog is called Biscuit.", only.Preview);
        Assert.Equal(["history-2026-10.jsonl"], ConversationHistory.MonthFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ALineCutShortByACrashIsSkippedAndTheNextOneStartsOnItsOwnLine()
    {
        var history = new ConversationHistory(folder, clock);
        var conversation = Guid.NewGuid();
        await history.AppendAsync(conversation, HistoryInputKind.Typed, "First", "One", null);
        var file = ConversationHistory.MonthFiles(folder).Single();
        await File.AppendAllTextAsync(file, "{\"v\":1,\"id\":\"cut sh");
        await history.AppendAsync(conversation, HistoryInputKind.Typed, "Second", "Two", null);
        await File.AppendAllTextAsync(file, "not json at all\n\n");

        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync();
        Assert.Equal(["First", "Second"], reread.Exchanges(conversation).Select(e => e.User));
        Assert.Equal(2, reread.Stats.Skipped);
    }

    [Fact]
    public async Task AnExchangeAppendedWhileTheRecordLoadsIsReadExactlyOnce()
    {
        var history = new ConversationHistory(folder, clock);
        var conversation = Guid.NewGuid();
        for (var i = 0; i < 50; i++) await history.AppendAsync(conversation, HistoryInputKind.Typed, $"before {i}", "ok", null);
        var appends = Enumerable.Range(0, 30).Select(i => history.AppendAsync(conversation, HistoryInputKind.Typed, $"during {i}", "ok", null)).ToArray();
        await Task.WhenAll(appends.Append(history.LoadAsync()));
        Assert.Equal(80, history.Stats.Exchanges);
        Assert.Equal(80, history.Exchanges(conversation).Select(e => e.Id).Distinct().Count());
        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync();
        Assert.Equal(80, reread.Stats.Exchanges);
    }

    [Fact]
    public async Task SearchRanksMatchesAndHonorsTheWindowAndTheConversationLeftOut()
    {
        var history = new ConversationHistory(folder, clock);
        await history.LoadAsync();
        Guid trip = Guid.NewGuid(), cat = Guid.NewGuid(), current = Guid.NewGuid();
        clock.Now = clock.Now.AddDays(-10);
        var kyoto = await history.AppendAsync(trip, HistoryInputKind.Spoken, "I'm going to Kyoto in April.", "Kyoto in April has cherry blossoms.", null);
        clock.Now = clock.Now.AddDays(9);
        var vet = await history.AppendAsync(cat, HistoryInputKind.Typed, "The cat goes to the vet on Tuesday.", "Good luck at the vet!", null);
        clock.Now = clock.Now.AddDays(1);
        await history.AppendAsync(current, HistoryInputKind.Typed, "Tell me about Kyoto food.", "Try yudofu.", null);

        var hits = history.Search(["kyoto", "april"], null, null, current, 5);
        Assert.Equal(kyoto.Id, Assert.Single(hits).Exchange.Id);
        Assert.Equal(2, hits[0].MatchedTerms);
        Assert.Equal(2, history.Search(["kyoto"], null, null, null, 5).Count);
        Assert.Empty(history.Search(["kyoto"], clock.Now.AddDays(-2), clock.Now, current, 5));
        Assert.Equal(vet.Id, Assert.Single(history.Search(["VET"], null, null, null, 5)).Exchange.Id);
        Assert.Empty(history.Search(["submarine"], null, null, null, 5));
        Assert.Equal([vet.Id], history.Between(clock.Now.AddDays(-2), clock.Now.AddSeconds(1), current, 5).Select(e => e.Id));
    }

    [Fact]
    public async Task DeletingAConversationRewritesOnlyItsLinesAndDeletingEverythingRemovesTheFiles()
    {
        var history = new ConversationHistory(folder, clock);
        Guid keep = Guid.NewGuid(), drop = Guid.NewGuid();
        await history.AppendAsync(keep, HistoryInputKind.Typed, "Keep me", "Kept", null);
        await history.AppendAsync(drop, HistoryInputKind.Typed, "Forget the treehouse", "Done", null);
        clock.Now = clock.Now.AddMonths(-1);
        await history.AppendAsync(drop, HistoryInputKind.Typed, "Old treehouse plans", "Noted", null);
        Assert.Equal(2, ConversationHistory.MonthFiles(folder).Count);

        Assert.Equal(2, await history.DeleteAsync(drop));
        Assert.Empty(history.Search(["treehouse"], null, null, null, 5));
        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync();
        Assert.Equal(["Keep me"], reread.Conversations().Select(c => c.Preview));
        Assert.Single(ConversationHistory.MonthFiles(folder));

        await reread.DeleteAllAsync();
        Assert.Empty(ConversationHistory.MonthFiles(folder));
        Assert.Equal(0, reread.Stats.Exchanges);
    }

    [Fact]
    public async Task ALongExchangeInAnyLanguageIsReadBackAndDeletingFindsEvenAnUnreadableLineOfIt()
    {
        var history = new ConversationHistory(folder, clock);
        Guid kept = Guid.NewGuid(), dropped = Guid.NewGuid();
        var said = new string('猫', ConversationHistory.MaximumUserCharacters);
        var reply = string.Concat(Enumerable.Repeat("é’", ConversationHistory.MaximumReplyCharacters / 2));
        await history.AppendAsync(kept, HistoryInputKind.Typed, said, reply, "Zoë");
        await history.AppendAsync(dropped, HistoryInputKind.Typed, "Forget me", "Gone", null);
        var file = ConversationHistory.MonthFiles(folder).Single();
        await File.AppendAllTextAsync(file, "{\"v\":1,\"conversation\":\"" + dropped.ToString("D") + "\",\"broken\n");

        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync();
        var exchange = Assert.Single(reread.Exchanges(kept));
        Assert.Equal((said, reply, "Zoë"), (exchange.User, exchange.Reply, exchange.Speaker));
        Assert.Equal(1, reread.Stats.Skipped);
        Assert.Contains("猫猫", await File.ReadAllTextAsync(file));

        Assert.Equal(2, await reread.DeleteAsync(dropped));
        Assert.DoesNotContain(dropped.ToString("D"), await File.ReadAllTextAsync(file));
        Assert.Single(reread.Exchanges(kept));
    }

    [Fact]
    public async Task TextIsBoundedAndControlCharactersAreCleaned()
    {
        var history = new ConversationHistory(folder, clock);
        var tooLong = new string('a', ConversationHistory.MaximumUserCharacters + 50);
        var exchange = await history.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, tooLong, "line\u0007one\nline two",
            new string('n', 100));
        Assert.Equal(ConversationHistory.MaximumUserCharacters, exchange.User.Length);
        Assert.Equal("line one\nline two", exchange.Reply);
        Assert.Equal(ConversationHistory.MaximumSpeakerCharacters, exchange.Speaker!.Length);
        await Assert.ThrowsAsync<ArgumentException>(() => history.AppendAsync(Guid.Empty, HistoryInputKind.Typed, "x", "y", null));
    }

    [Fact]
    public async Task ACopiedFileDoesNotDoubleTheRecord()
    {
        var history = new ConversationHistory(folder, clock);
        await history.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "Once", "Only once", null);
        var file = ConversationHistory.MonthFiles(folder).Single();
        File.Copy(file, Path.Combine(folder, "history-2025-01.jsonl"));
        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync();
        Assert.Equal(1, reread.Stats.Exchanges);
    }

    [Fact]
    public async Task ToLineIsWhatTheRecordReads()
    {
        var exchange = new HistoryExchange(Guid.NewGuid(), Guid.NewGuid(), clock.Now, HistoryInputKind.Spoken, "Hi", "Hello", "Ana");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, ConversationHistory.FileName(clock.Now)), ConversationHistory.ToLine(exchange) + "\n",
            new UTF8Encoding(false));
        var history = new ConversationHistory(folder, clock);
        await history.LoadAsync();
        Assert.Equal(exchange, Assert.Single(history.Exchanges(exchange.ConversationId)));
    }

    internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
