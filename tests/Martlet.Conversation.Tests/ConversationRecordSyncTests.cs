using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Sync;

namespace Martlet.Conversation.Tests;

/// <summary>One record of conversations on every computer: simulated computers, each with its own production record and record
/// sync, kept the same through simulated hosts that keep each month's copy and merge the way a gateway does.</summary>
public sealed class ConversationRecordSyncTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Martlet.RecordSync.Tests." + Guid.NewGuid().ToString("N"));
    private readonly ConversationHistoryTests.SettableClock clock = new(new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero));
    private readonly FakeHost host = new("lab-host");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private Computer Make(string device) => new(new ConversationHistory(Path.Combine(root, device), clock), device);

    private sealed class Computer(ConversationHistory history, string device)
    {
        internal ConversationHistory History { get; } = history;
        internal ConversationRecordSync Sync { get; } = new(history, device);
    }

    private Task<ConversationSyncResult> SyncAsync(Computer computer, params IConversationRecordHost[] hosts) =>
        computer.Sync.RunAsync(hosts.Length == 0 ? [host] : hosts, clock.GetUtcNow(), CancellationToken.None);

    [Fact]
    public async Task AnExchangeRecordedOnOneComputerIsRecalledOnAnother()
    {
        var a = Make("desk-a");
        var b = Make("laptop-b");
        var trip = Guid.NewGuid();
        await a.History.AppendAsync(trip, HistoryInputKind.Spoken, "We should visit Kyoto in April.", "The cherry blossoms will be out then.", "Sam");
        clock.Now = clock.Now.AddMinutes(1);
        await a.History.AppendAsync(trip, HistoryInputKind.Typed, "Where should we stay?", "A ryokan near Gion would be lovely.", "Sam");
        await b.History.LoadAsync();

        var given = await SyncAsync(a);
        var taken = await SyncAsync(b);

        Assert.Null(given.Problem);
        Assert.Equal(2, given.Recorded);
        Assert.Equal(2, taken.Taken);
        Assert.Equal(1, taken.Same);
        Assert.Equal(["We should visit Kyoto in April.", "Where should we stay?"], b.History.Exchanges(trip).Select(e => e.User));
        var hit = Assert.Single(b.History.Search(["kyoto"], null, null, null, 5));
        Assert.Equal("Sam", hit.Exchange.Speaker);
        var reread = new ConversationHistory(b.History.Directory, clock);
        await reread.LoadAsync();
        Assert.Equal((await Reread(a)).Exchanges(trip), reread.Exchanges(trip));
    }

    [Fact]
    public async Task EditsAndDeletionsReachEveryComputerAndADeletedMessageNeverComesBack()
    {
        var a = Make("desk-a");
        var b = Make("laptop-b");
        var talk = Guid.NewGuid();
        var other = Guid.NewGuid();
        var kept = await a.History.AppendAsync(talk, HistoryInputKind.Typed, "My sister's name is Ana.", "Nice to know!", null);
        clock.Now = clock.Now.AddMinutes(1);
        var secret = await a.History.AppendAsync(talk, HistoryInputKind.Typed, "My PIN is 4321.", "I won't repeat that.", null);
        clock.Now = clock.Now.AddMinutes(1);
        await a.History.AppendAsync(other, HistoryInputKind.Typed, "Tell me a joke.", "Why did the owl cross the road?", null);
        await SyncAsync(a);
        await SyncAsync(b);

        // B edits Martlet's reply (a new length); A deletes your message of another exchange and a whole conversation.
        clock.Now = clock.Now.AddMinutes(5);
        await b.History.ChangeAsync(kept.Id, found => found with { Reply = "Nice to know! I'll remember Ana.", Edited = clock.GetUtcNow() });
        await a.History.ChangeAsync(secret.Id, found => found with { User = "", Edited = clock.GetUtcNow() });
        await a.History.DeleteAsync(other);
        var bGives = await SyncAsync(b);
        var aSyncs = await SyncAsync(a);
        var bTakes = await SyncAsync(b);

        Assert.Equal(1, bGives.Recorded);
        Assert.Equal(1, aSyncs.Taken);
        Assert.Equal(2, aSyncs.Recorded);
        Assert.Equal(1, bTakes.Taken);
        Assert.Equal(1, bTakes.Deleted);
        foreach (var computer in new[] { a, b })
        {
            var reread = new ConversationHistory(computer.History.Directory, clock);
            await reread.LoadAsync();
            Assert.Equal("Nice to know! I'll remember Ana.", reread.Exchange(kept.Id)!.Reply);
            Assert.Equal("", reread.Exchange(secret.Id)!.User);
            Assert.Empty(reread.Exchanges(other));
        }

        // More syncs in any order: nothing comes back.
        await SyncAsync(a);
        await SyncAsync(b);
        await SyncAsync(a);
        Assert.Empty(a.History.Exchanges(other));
        Assert.Empty(b.History.Exchanges(other));
        Assert.True(host.Copy(kept.At)!.Exchanges.Where(e => e.Deleted).Any());
    }

    [Fact]
    public async Task DeletingEverythingOnOneComputerDeletesItEverywhere()
    {
        var a = Make("desk-a");
        var b = Make("laptop-b");
        await a.History.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "First", "One", null);
        clock.Now = clock.Now.AddMonths(1);
        await a.History.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "Second", "Two", null);
        await SyncAsync(a);
        await SyncAsync(b);
        Assert.Equal(2, b.History.Months().Count);

        await b.History.DeleteAllAsync();
        var bGives = await SyncAsync(b);
        var aTakes = await SyncAsync(a);

        Assert.Equal(2, bGives.Recorded);
        Assert.Equal(2, aTakes.Deleted);
        Assert.Empty(a.History.Months());
        await SyncAsync(b);
        Assert.Empty(b.History.Months());
    }

    [Fact]
    public async Task OfflineEditsOfTheSameMessageEndAsTheLaterOneEverywhere()
    {
        var a = Make("desk-a");
        var b = Make("laptop-b");
        var said = await a.History.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "Plan the party.", "Sure!", null);
        await SyncAsync(a);
        await SyncAsync(b);

        clock.Now = clock.Now.AddMinutes(10);
        await a.History.ChangeAsync(said.Id, found => found with { Reply = "Sure, on Saturday!", Edited = clock.GetUtcNow() });
        clock.Now = clock.Now.AddMinutes(1);
        await b.History.ChangeAsync(said.Id, found => found with { Reply = "Sure, on Sunday at noon!", Edited = clock.GetUtcNow() });
        await SyncAsync(a);
        await SyncAsync(b);
        await SyncAsync(a);

        Assert.Equal("Sure, on Sunday at noon!", (await Reread(a)).Exchange(said.Id)!.Reply);
        Assert.Equal("Sure, on Sunday at noon!", (await Reread(b)).Exchange(said.Id)!.Reply);
    }

    [Fact]
    public async Task ANewComputerGetsTheWholeRecordFromAnyHostAndAnUnchangedRecordReadsNothing()
    {
        var a = Make("desk-a");
        var second = new FakeHost("lab-host-2");
        for (var i = 0; i < 3; i++)
        {
            await a.History.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, $"Question {i}", $"Answer {i}", null);
            clock.Now = clock.Now.AddMonths(1);
        }
        await SyncAsync(a, host, second);
        Assert.Equal(3, second.Months.Count);

        var c = Make("new-pc-c");
        var taken = await SyncAsync(c, host, second);
        Assert.Equal(3, taken.Taken);
        // Both hosts hold the same copies: each month is read once, from one of them.
        Assert.Equal(3, host.Reads + second.Reads);
        Assert.Equal(2, taken.Same);

        host.Reads = second.Reads = host.Merges = second.Merges = 0;
        var again = await SyncAsync(c, host, second);
        Assert.Equal(0, host.Reads + second.Reads + host.Merges + second.Merges);
        Assert.Equal(0, again.Taken + again.Recorded);
        Assert.Equal(3, again.Exchanges);
    }

    [Fact]
    public async Task ANewerMartletsVersionIsKeptAsItIsAndNeverWrittenOver()
    {
        var a = Make("desk-a");
        var said = await a.History.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "Hello", "Hi there!", null);
        await SyncAsync(a);
        var month = SharedConversations.MonthOf(said.At);
        var line = JsonNode.Parse(host.Copy(said.At)!.Find(said.Id)!.Exchange!)!.AsObject();
        line["v"] = 2;
        line["mood"] = "cheerful";
        var newer = new SharedExchange { Id = said.Id, Revision = 2, UpdatedAt = clock.GetUtcNow().AddMinutes(1), UpdatedBy = "newer-pc", Exchange = line.ToJsonString() };
        await host.MergeAsync(SharedConversations.Of(month, [newer]), CancellationToken.None);

        var first = await SyncAsync(a);
        var second = await SyncAsync(a);

        Assert.Equal(1, first.Unreadable);
        Assert.Equal(0, second.Recorded);
        Assert.Equal("Hi there!", (await Reread(a)).Exchange(said.Id)!.Reply);
        var onHost = host.Copy(said.At)!.Find(said.Id)!;
        Assert.Equal(2, onHost.Revision);
        Assert.Contains("cheerful", onHost.Exchange);
    }

    [Fact]
    public async Task AHostThatDoesntAnswerIsReportedAndTheOtherHostStillSyncs()
    {
        var a = Make("desk-a");
        await a.History.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "Hello", "Hi!", null);
        var down = new FakeHost("lab-down") { Failure = ConversationHostFailure.Unreachable };
        var old = new FakeHost("lab-old") { Failure = ConversationHostFailure.Old };

        var result = await SyncAsync(a, host, down, old);

        Assert.Equal(1, result.Same);
        Assert.Equal(1, result.Down);
        Assert.Equal(1, result.Old);
        Assert.Single(host.Months);
        Assert.Equal(1, ConversationRecordSync.LoadStatus(a.History.Directory)!.Exchanges);
    }

    private async Task<ConversationHistory> Reread(Computer computer)
    {
        var reread = new ConversationHistory(computer.History.Directory, clock);
        await reread.LoadAsync();
        return reread;
    }

    /// <summary>A host's copies of one account's record, merged the way the gateway merges them.</summary>
    private sealed class FakeHost(string hostId) : IConversationRecordHost
    {
        private readonly Dictionary<string, SharedConversations> months = new(StringComparer.Ordinal);
        internal ConversationHostFailure? Failure { get; init; }
        internal int Reads { get; set; }
        internal int Merges { get; set; }
        public string HostId => hostId;
        internal IReadOnlyDictionary<string, SharedConversations> Months => months;

        internal SharedConversations? Copy(DateTimeOffset at) => months.GetValueOrDefault(SharedConversations.MonthOf(at));

        public Task<IReadOnlyDictionary<string, string>> MonthsAsync(CancellationToken token)
        {
            Fail();
            return Task.FromResult<IReadOnlyDictionary<string, string>>(months.ToDictionary(pair => pair.Key, pair => pair.Value.Digest()));
        }

        public Task<SharedConversations> ReadAsync(string month, CancellationToken token)
        {
            Fail();
            Reads++;
            return Task.FromResult(SharedConversations.Parse(months.GetValueOrDefault(month, SharedConversations.Empty(month)).Write()));
        }

        public Task<SharedConversations> MergeAsync(SharedConversations copy, CancellationToken token)
        {
            Fail();
            Merges++;
            var merged = SharedConversations.Merge(months.GetValueOrDefault(copy.Month, SharedConversations.Empty(copy.Month)),
                SharedConversations.Parse(copy.Write()));
            if (merged.Exchanges.Count > 0) months[copy.Month] = merged;
            return Task.FromResult(merged);
        }

        private void Fail()
        {
            if (Failure is { } failure) throw new ConversationHostException($"{hostId} can't be used.", failure);
        }
    }
}
