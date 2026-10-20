using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.HostClient;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>The record of conversations on a host (docs/MEMORY.md, "One record on every computer"): one copy per account and
/// UTC month, for paired member devices where the account is signed in.</summary>
public sealed class ConversationRecordTests
{
    private const string Accounts = "/martlet/v1/conversations/accounts/";
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string October = "2026-10", September = "2026-09";
    private static readonly Guid Sam = Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7");
    private static readonly Guid Alex = Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Storage : IGatewayConversationStorage
    {
        internal readonly Dictionary<string, byte[]> Saved = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> List() => Saved.Keys.ToArray();
        public byte[]? Load(string key) => Saved.TryGetValue(key, out var bytes) ? (byte[])bytes.Clone() : null;
        public void Save(string key, byte[] bytes) => Saved[key] = (byte[])bytes.Clone();
    }

    private sealed class AccountStorage(byte[] initial) : IGatewayAccountStorage
    {
        public byte[]? Load() => initial;
        public void Save(byte[] bytes) { }
    }

    private static SharedExchange Said(Guid id, string said, long revision = 1) => new()
    {
        Id = id, Revision = revision, UpdatedAt = Now.AddMinutes(revision), UpdatedBy = "home-pc",
        Exchange = SharedConversations.Canonical(JsonSerializer.Serialize(new { id, said, reply = "Nice." }))
    };

    private static SharedExchange Deleted(Guid id, long revision) => new()
    {
        Id = id, Revision = revision, UpdatedAt = Now.AddMinutes(revision), UpdatedBy = "work-pc", Exchange = null
    };

    private static SharedConversations Month(string month, params SharedExchange[] exchanges) => SharedConversations.Of(month, exchanges);

    private static string Route(Guid account, string? month = null) => Accounts + account.ToString("N") + (month is null ? "" : "/" + month);

    private static async Task<GatewayRequestSigner> MemberAsync(GatewayTestHost host, string deviceId = "home-pc") =>
        new(host.Identity, await host.PairAsync(deviceId: deviceId), host.Clock);

    private static async Task<(string Account, string Month, string Digest, SharedConversations Record)> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        Assert.Equal("fixture-host", root.GetProperty("host_id").GetString());
        return (root.GetProperty("account").GetString()!, root.GetProperty("month").GetString()!, root.GetProperty("digest").GetString()!,
            SharedConversations.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("record"))));
    }

    private static async Task<Dictionary<string, string>> MonthsAsync(GatewayTestHost host, GatewayRequestSigner signer, Guid account)
    {
        using var response = await host.Client.SendAsync(host.SignedGet(Route(account), GatewayRole.Voice, signer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        Assert.Equal("fixture-host", root.GetProperty("host_id").GetString());
        Assert.Equal(account.ToString("N"), root.GetProperty("account").GetString());
        return root.GetProperty("months").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    [Fact]
    public async Task A_merged_month_reads_back_and_the_months_index_lists_it_with_its_digest()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        var storage = new Storage();
        host.Server.AttachConversationStorage(storage);
        var member = await MemberAsync(host);
        var october = Month(October, Said(Guid.NewGuid(), "Hello there."), Said(Guid.NewGuid(), "How is the weather?"));
        var september = Month(September, Said(Guid.NewGuid(), "Good night."));

        using (var merged = await host.Client.SendAsync(host.SignedPost(Route(Sam, October), GatewayRole.Voice, member, october.Write())))
        {
            var (account, month, digest, record) = await ReadAsync(merged);
            Assert.Equal((Sam.ToString("N"), October, october.Digest()), (account, month, digest));
            Assert.Equal(october.Digest(), record.Digest());
        }
        using (var merged = await host.Client.SendAsync(host.SignedPost(Route(Sam, September), GatewayRole.Voice, member, september.Write())))
            Assert.Equal(september.Digest(), (await ReadAsync(merged)).Digest);
        using (var read = await host.Client.SendAsync(host.SignedGet(Route(Sam, October), GatewayRole.Voice, member)))
        {
            var (_, month, digest, record) = await ReadAsync(read);
            Assert.Equal((October, october.Digest()), (month, digest));
            Assert.Equal(october.Exchanges.Select(e => e.Exchange), record.Exchanges.Select(e => e.Exchange));
        }

        Assert.Equal(new Dictionary<string, string> { [September] = september.Digest(), [October] = october.Digest() },
            await MonthsAsync(host, member, Sam));
        Assert.Empty(await MonthsAsync(host, member, Alex));
        // A month the host doesn't keep reads empty, and reading makes no month.
        using (var none = await host.Client.SendAsync(host.SignedGet(Route(Alex, October), GatewayRole.Voice, member)))
        {
            var (account, month, digest, record) = await ReadAsync(none);
            Assert.Equal((Alex.ToString("N"), October, SharedConversations.Empty(October).Digest()), (account, month, digest));
            Assert.Empty(record.Exchanges);
        }
        // A merge that brings no exchange makes no month.
        using (var nothing = await host.Client.SendAsync(host.SignedPost(Route(Sam, "2026-08"), GatewayRole.Voice, member,
            SharedConversations.Empty("2026-08").Write())))
            Assert.Empty((await ReadAsync(nothing)).Record.Exchanges);

        string[] kept = [Sam.ToString("N") + "-" + September, Sam.ToString("N") + "-" + October];
        Assert.Equal(kept, host.Server.ConversationRecords);
        Assert.Equal(kept, storage.Saved.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(october.Digest(), SharedConversations.Parse(storage.Saved[kept[1]]).Digest());
    }

    [Fact]
    public async Task A_deletion_with_a_higher_revision_wins_and_an_older_copy_never_brings_the_exchange_back()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        var member = await MemberAsync(host);
        var id = Guid.NewGuid();
        var kept = Said(Guid.NewGuid(), "Keep this one.");
        var recorded = Month(October, Said(id, "Forget this."), kept);

        using (var first = await host.Client.SendAsync(host.SignedPost(Route(Sam, October), GatewayRole.Voice, member, recorded.Write())))
            Assert.Equal(2, (await ReadAsync(first)).Record.Live.Count());
        using (var deleted = await host.Client.SendAsync(host.SignedPost(Route(Sam, October), GatewayRole.Voice, member,
            Month(October, Deleted(id, 2)).Write())))
        {
            var record = (await ReadAsync(deleted)).Record;
            Assert.True(record.Find(id)!.Deleted);
            Assert.Equal([kept.Id], record.Live.Select(e => e.Id));
        }
        using (var stale = await host.Client.SendAsync(host.SignedPost(Route(Sam, October), GatewayRole.Voice, member, recorded.Write())))
        {
            var (_, _, digest, record) = await ReadAsync(stale);
            Assert.True(record.Find(id)!.Deleted);
            Assert.Equal(Month(October, Deleted(id, 2), kept).Digest(), digest);
        }
    }

    [Fact]
    public async Task Only_devices_where_the_account_is_signed_in_may_use_its_record()
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var key = NetworkKey.Create("sams-pc");
        var now = host.Clock.GetUtcNow();
        var sam = Person("Sam", AccountRoles.Owner, Sam, "sams-pc");
        var alex = Person("Alex", AccountRoles.Member, Alex, "alexs-pc");
        host.Server.AttachAccountStorage(new AccountStorage(AccountDirectory.Empty.Put(key, sam, now).Put(key, alex, now).Write()));
        var sams = await MemberAsync(host, "sams-pc");
        var alexs = await MemberAsync(host, "alexs-pc");
        var body = Month(October, Said(Guid.NewGuid(), "Sam's secret.")).Write();

        using (var mine = await host.Client.SendAsync(host.SignedPost(Route(Sam, October), GatewayRole.Voice, sams, body)))
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        foreach (var request in new[]
        {
            host.SignedGet(Route(Sam), GatewayRole.Voice, alexs),
            host.SignedGet(Route(Sam, October), GatewayRole.Voice, alexs),
            host.SignedPost(Route(Sam, October), GatewayRole.Voice, alexs, body)
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("conversations.account_denied", await GatewayTestHost.FailureCode(response));
            }
        }
        Assert.Single(await MonthsAsync(host, sams, Sam));
        Assert.Empty(await MonthsAsync(host, alexs, Alex));

        using (var keyed = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + Route(Sam)))
        {
            keyed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "martlet_" + new string('a', 43));
            using var response = await host.Client.SendAsync(keyed);
            Assert.Equal("key.scope", await GatewayTestHost.FailureCode(response));
        }
        using (var unsigned = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + Route(Sam))))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    private static Account Person(string name, string role, Guid id, params string[] devices)
    {
        var account = Account.Create(name, role, id);
        foreach (var device in devices)
            account = account.WithDevice(AccountDevice.For(device, AccountLoginKey.ForWindows(device, Sid), DateTimeOffset.UtcNow));
        return account;
    }

    [Fact]
    public async Task A_body_for_another_month_or_that_is_not_a_record_is_refused()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        var member = await MemberAsync(host);
        foreach (var body in new[]
        {
            Month(September, Said(Guid.NewGuid(), "Wrong month.")).Write(),
            Encoding.UTF8.GetBytes("{\"schema_version\":1,\"month\":\"2026-10\"}"),
            Encoding.UTF8.GetBytes("{\"schema_version\":1,\"month\":\"2026-10\",\"exchanges\":[],\"extra\":1}"),
            Encoding.UTF8.GetBytes("not json")
        })
        {
            using var response = await host.Client.SendAsync(host.SignedPost(Route(Sam, October), GatewayRole.Voice, member, body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
        }
        using (var postIndex = await host.Client.SendAsync(host.SignedPost(Route(Sam), GatewayRole.Voice, member,
            Month(October, Said(Guid.NewGuid(), "x")).Write())))
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(postIndex));
        Assert.Empty(host.Server.ConversationRecords);
    }

    [Theory]
    [InlineData("/martlet/v1/conversations")]
    [InlineData("/martlet/v1/conversations/")]
    [InlineData("/martlet/v1/conversations/accounts")]
    [InlineData("/martlet/v1/conversations/accounts/")]
    [InlineData("/martlet/v1/conversations/accounts/5A3F0C9E8B7D4E21A6C3B2F1D0E9A8B7/2026-10")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b/2026-10")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/2026-13")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/2026-00")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/1999-10")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/2026-1")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/2026-10/x")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/2026-10?x=1")]
    [InlineData("/martlet/v1/conversations/accounts/5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7?x=1")]
    public async Task Anything_but_an_account_and_a_month_is_refused(string path)
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        var member = await MemberAsync(host);
        foreach (var request in new[]
        {
            host.SignedGet(path, GatewayRole.Voice, member),
            host.SignedPost(path, GatewayRole.Voice, member, Month(October, Said(Guid.NewGuid(), "x")).Write())
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
            }
        }
        Assert.Empty(host.Server.ConversationRecords);
    }

    [Fact]
    public async Task A_restarted_host_serves_the_months_it_saved_and_skips_files_that_are_not_months()
    {
        var storage = new Storage();
        var october = Month(October, Said(Guid.NewGuid(), "Hello there."));
        var alexs = Month(September, Said(Guid.NewGuid(), "Alex was here."));
        await using (var first = await GatewayTestHost.StartAsync())
        {
            first.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
            first.Server.AttachConversationStorage(storage);
            var member = await MemberAsync(first);
            using (var a = await first.Client.SendAsync(first.SignedPost(Route(Sam, October), GatewayRole.Voice, member, october.Write())))
                Assert.Equal(HttpStatusCode.OK, a.StatusCode);
            using (var b = await first.Client.SendAsync(first.SignedPost(Route(Alex, September), GatewayRole.Voice, member, alexs.Write())))
                Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        }
        storage.Saved["not-a-month"] = october.Write();
        storage.Saved[Sam.ToString("N").ToUpperInvariant() + "-" + October] = october.Write();
        // A copy saved under another month's key, and one that isn't a record, are skipped.
        storage.Saved[Sam.ToString("N") + "-2026-07"] = october.Write();
        storage.Saved[Sam.ToString("N") + "-2026-06"] = Encoding.UTF8.GetBytes("{\"broken\":true}");

        await using var second = await GatewayTestHost.StartAsync();
        second.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        second.Server.AttachConversationStorage(storage);
        Assert.Equal([Alex.ToString("N") + "-" + September, Sam.ToString("N") + "-" + October], second.Server.ConversationRecords);
        var again = await MemberAsync(second);
        Assert.Equal(new Dictionary<string, string> { [October] = october.Digest() }, await MonthsAsync(second, again, Sam));
        using var read = await second.Client.SendAsync(second.SignedGet(Route(Alex, September), GatewayRole.Voice, again));
        Assert.Equal(alexs.Digest(), (await ReadAsync(read)).Digest);
    }

    [Fact]
    public async Task A_desktop_syncs_months_through_the_host_peer_and_a_refused_account_reads_as_denied()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.MemorySpaceAccess = (principal, space, use) => space == MemorySpaceId.Account(Sam);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await HostTransport.PairAsync(host.Origin.CanonicalOrigin, card.HostId, card.SpkiFingerprint,
            "desktop-test", card.PairingId, card.Token.Reveal());
        HostConnection Connect() => new(pairing, secret, host.Clock);
        var october = Month(October, Said(Guid.NewGuid(), "Hello there."), Said(Guid.NewGuid(), "Lovely day."));

        using (var peer = new HostConversationPeer(card.HostId, Connect, Sam))
        {
            Assert.Empty(await peer.MonthsAsync(CancellationToken.None));
            Assert.Equal(october.Digest(), (await peer.MergeAsync(october, CancellationToken.None)).Digest());
            Assert.Equal(new Dictionary<string, string> { [October] = october.Digest() },
                (await peer.MonthsAsync(CancellationToken.None)).ToDictionary(p => p.Key, p => p.Value));
            Assert.Equal(october.Digest(), (await peer.ReadAsync(October, CancellationToken.None)).Digest());
            Assert.Empty((await peer.ReadAsync(September, CancellationToken.None)).Exchanges);
        }
        using (var refused = new HostConversationPeer(card.HostId, Connect, Alex))
        {
            var denied = await Assert.ThrowsAsync<ConversationHostException>(() => refused.MonthsAsync(CancellationToken.None));
            Assert.Equal(ConversationHostFailure.Denied, denied.Failure);
            denied = await Assert.ThrowsAsync<ConversationHostException>(() => refused.MergeAsync(october, CancellationToken.None));
            Assert.Equal(ConversationHostFailure.Denied, denied.Failure);
        }
        Assert.Equal([Sam.ToString("N") + "-" + October], host.Server.ConversationRecords);
    }

    [Fact]
    public void A_host_keeps_at_most_8192_months_and_refuses_a_new_one_past_that()
    {
        var records = new GatewayConversationRecords();
        for (var i = 0; i < GatewayConversationRecords.MaximumDocuments; i++)
            records.Merge(new Guid(i + 1, 0, 0, new byte[8]), Month(October, Said(Guid.NewGuid(), $"Hello {i}.")));
        Assert.Equal(GatewayConversationRecords.MaximumDocuments, records.Keys.Count);

        var full = Assert.Throws<GatewayProtocolException>(() => records.Merge(Sam, Month(October, Said(Guid.NewGuid(), "One too many."))));
        Assert.Equal("conversations.full", full.Failure.Code);
        Assert.Equal(409, full.Failure.HttpStatus);
        // A month it keeps still takes changes, and a merge with nothing still answers.
        var kept = new Guid(1, 0, 0, new byte[8]);
        Assert.Equal(2, records.Merge(kept, Month(October, Said(Guid.NewGuid(), "Another one."))).Exchanges.Count);
        Assert.Empty(records.Merge(Sam, SharedConversations.Empty(October)).Exchanges);
        Assert.Equal(GatewayConversationRecords.MaximumDocuments, records.Keys.Count);
    }
}
