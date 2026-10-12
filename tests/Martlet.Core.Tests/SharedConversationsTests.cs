using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

public sealed class SharedConversationsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 22, 0, 0, TimeSpan.Zero);

    private static SharedExchange Entry(Guid id, long revision, string? text, string by = "desk-a", int minutes = 0) => new()
    {
        Id = id, Revision = revision, UpdatedAt = At.AddMinutes(minutes), UpdatedBy = by,
        Exchange = text is null ? null : $"{{\"v\":1,\"id\":\"{id}\",\"user\":\"{text}\"}}"
    };

    [Fact]
    public void MergingIsTheSameInAnyOrderAndADeletionWinsOverTheVersionItDeleted()
    {
        Guid kept = Guid.NewGuid(), gone = Guid.NewGuid();
        var a = SharedConversations.Of("2026-10", [Entry(kept, 1, "Hello"), Entry(gone, 1, "My PIN")]);
        var b = SharedConversations.Of("2026-10", [Entry(kept, 2, "Hello there", "laptop-b", 5), Entry(gone, 2, null, "laptop-b", 6)]);

        var ab = SharedConversations.Merge(a, b);
        var ba = SharedConversations.Merge(b, a);

        Assert.Equal(ab.Digest(), ba.Digest());
        Assert.Equal(ab.Digest(), SharedConversations.Merge(ab, a).Digest());
        Assert.Contains("Hello there", ab.Find(kept)!.Exchange);
        Assert.True(ab.Find(gone)!.Deleted);
        Assert.Single(ab.Live);
    }

    [Fact]
    public void TheSameRevisionMadeApartKeepsTheLaterEditEverywhere()
    {
        var id = Guid.NewGuid();
        var earlier = SharedConversations.Of("2026-10", [Entry(id, 2, "Saturday", "desk-a", 10)]);
        var later = SharedConversations.Of("2026-10", [Entry(id, 2, "Sunday", "laptop-b", 11)]);

        Assert.Contains("Sunday", SharedConversations.Merge(earlier, later).Find(id)!.Exchange);
        Assert.Contains("Sunday", SharedConversations.Merge(later, earlier).Find(id)!.Exchange);
    }

    [Fact]
    public void ACopyReadBackIsTheSameAndExchangesStayAsTheyAre()
    {
        var id = Guid.NewGuid();
        var copy = SharedConversations.Of("2026-10",
            [Entry(id, 1, "Grüße 👋") with { Exchange = SharedConversations.Canonical($"{{ \"v\": 2, \"id\": \"{id}\", \"user\": \"Grüße 👋\", \"mood\": {{ \"a\": 1 }} }}") }]);

        var read = SharedConversations.Parse(copy.Write());

        Assert.Equal(copy.Digest(), read.Digest());
        Assert.Equal(copy.Find(id)!.Exchange, read.Find(id)!.Exchange);
        Assert.Contains("\"mood\":{\"a\":1}", read.Find(id)!.Exchange);
        Assert.Equal("Grüße 👋", System.Text.Json.JsonDocument.Parse(read.Find(id)!.Exchange!).RootElement.GetProperty("user").GetString());
    }

    [Theory]
    [InlineData("2026-10", true)]
    [InlineData("2026-01", true)]
    [InlineData("2026-12", true)]
    [InlineData("2026-13", false)]
    [InlineData("2026-00", false)]
    [InlineData("1999-10", false)]
    [InlineData("2026-1", false)]
    [InlineData("2026/10", false)]
    [InlineData(null, false)]
    public void OnlyMonthsAreMonths(string? month, bool valid) => Assert.Equal(valid, SharedConversations.IsMonth(month));

    [Fact]
    public void TwoMonthsCantBeMergedAndBadCopiesAreRefused()
    {
        Assert.Throws<ContractException>(() => SharedConversations.Merge(SharedConversations.Empty("2026-10"), SharedConversations.Empty("2026-11")));
        Assert.Throws<ContractException>(() => SharedConversations.Parse("{\"schema_version\":1,\"month\":\"2026-13\",\"exchanges\":[]}"u8));
        Assert.Throws<ContractException>(() => SharedConversations.Parse("{\"schema_version\":2,\"month\":\"2026-10\",\"exchanges\":[]}"u8));
        Assert.Throws<ContractException>(() => SharedConversations.Canonical("[1,2]"));
    }
}
