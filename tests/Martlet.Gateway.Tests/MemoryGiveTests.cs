using System.Net;
using System.Text.Json;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Sharing a fact with another account (docs/ACCOUNTS.md, "Sharing"): POST /memories/spaces/{space}/give adds new
/// facts to a space the giving device may not read, and answers only how many the space took.</summary>
public sealed class MemoryGiveTests
{
    private const string Spaces = "/martlet/v1/memories/spaces/";
    private static readonly string Sam = MemorySpaceId.Account(Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7"));
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static SharedMemory Fact(string content, Guid? id = null, long revision = 1, bool forgotten = false)
    {
        var factId = id ?? Guid.NewGuid();
        return new SharedMemory
        {
            Id = factId, Revision = revision, UpdatedAt = At, UpdatedBy = "alexs-pc",
            Fact = forgotten ? null : SharedMemories.Canonical(JsonSerializer.Serialize(new { id = factId, content }))
        };
    }

    private static SharedMemories Of(params SharedMemory[] facts) => new() { SchemaVersion = SharedMemories.SchemaVersion1, Facts = facts };

    private static async Task<int> TakenAsync(HttpResponseMessage response, string space)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        Assert.Equal("fixture-host", root.GetProperty("host_id").GetString());
        Assert.Equal(space, root.GetProperty("space").GetString());
        Assert.False(root.TryGetProperty("memories", out _));
        return root.GetProperty("taken").GetInt32();
    }

    private static async Task<SharedMemories> ReadAsync(GatewayTestHost host, GatewayRequestSigner signer, string space)
    {
        using var response = await host.Client.SendAsync(host.SignedGet(Spaces + space, GatewayRole.Voice, signer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return SharedMemories.Parse(JsonSerializer.SerializeToUtf8Bytes(document.RootElement.GetProperty("memories")));
    }

    [Fact]
    public async Task A_device_gives_new_facts_to_a_space_it_may_not_read_and_never_sees_the_space()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var sams = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "sams-pc"), host.Clock);
        var alexs = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "alexs-pc"), host.Clock);
        var asked = new List<(string Device, GatewayMemorySpaceUse Use)>();
        host.Server.MemorySpaceAccess = (principal, space, use) =>
        {
            lock (asked) asked.Add((principal.DeviceId, use));
            return use == GatewayMemorySpaceUse.Give || principal.DeviceId == "sams-pc";
        };
        var kept = Fact("Sam's cat is called Miso.");
        var gone = Fact("Sam's old phone number.", revision: 2, forgotten: true);
        using (var own = await host.Client.SendAsync(host.SignedPost(Spaces + Sam, GatewayRole.Voice, sams, Of(kept, gone).Write())))
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // Alex shares two facts; a fact Sam's space already has, or forgot, stays exactly as it is.
        var tea = Fact("Sam drinks green tea.");
        var concert = Fact("Sam and Alex go to a concert on Friday.");
        var overwrite = Fact("Sam's cat is called Biscuit.", kept.Id, revision: 9);
        var revive = Fact("Sam's old phone number is 555-0100.", gone.Id, revision: 9);
        asked.Clear();
        using (var given = await host.Client.SendAsync(host.SignedPost(Spaces + Sam + "/give", GatewayRole.Voice, alexs,
            Of(tea, concert, overwrite, revive).Write())))
            Assert.Equal(2, await TakenAsync(given, Sam));
        Assert.Equal([("alexs-pc", GatewayMemorySpaceUse.Give)], asked);

        var space = await ReadAsync(host, sams, Sam);
        Assert.Equal(3, space.Live.Count());
        Assert.Equal(kept, space.Find(kept.Id));
        Assert.Equal(gone, space.Find(gone.Id));
        Assert.Equal(tea, space.Find(tea.Id));
        Assert.Equal(concert, space.Find(concert.Id));

        // Giving the same facts again takes nothing; Alex still can't read Sam's space.
        using (var again = await host.Client.SendAsync(host.SignedPost(Spaces + Sam + "/give", GatewayRole.Voice, alexs, Of(tea).Write())))
            Assert.Equal(0, await TakenAsync(again, Sam));
        using (var read = await host.Client.SendAsync(host.SignedGet(Spaces + Sam, GatewayRole.Voice, alexs)))
            Assert.Equal("memories.space_denied", await GatewayTestHost.FailureCode(read));
    }

    [Fact]
    public async Task A_gift_makes_a_new_space_and_the_access_hook_can_refuse_it()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var alexs = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "alexs-pc"), host.Clock);
        var tea = Fact("Sam drinks green tea.");
        using (var given = await host.Client.SendAsync(host.SignedPost(Spaces + MemorySpaceId.Household + "/give", GatewayRole.Voice, alexs, Of(tea).Write())))
            Assert.Equal(1, await TakenAsync(given, MemorySpaceId.Household));
        Assert.Equal([MemorySpaceId.Household], host.Server.MemorySpaces);

        host.Server.MemorySpaceAccess = (_, _, use) => use != GatewayMemorySpaceUse.Give;
        using var refused = await host.Client.SendAsync(host.SignedPost(Spaces + Sam + "/give", GatewayRole.Voice, alexs, Of(Fact("x")).Write()));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("memories.space_denied", await GatewayTestHost.FailureCode(refused));
        Assert.Equal([MemorySpaceId.Household], host.Server.MemorySpaces);
    }

    [Fact]
    public async Task Empty_forgotten_too_many_or_unsigned_gifts_reads_and_friends_are_refused()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var owner = await FriendAccessTests.SetUpSignInAsync(host);
        var friend = await FriendAccessTests.FriendAsync(host, owner);
        var tooMany = Of([.. Enumerable.Range(0, 65).Select(i => Fact($"Fact {i}."))]);
        foreach (var body in new[] { SharedMemories.Empty, Of(Fact("gone", forgotten: true)), Of(Fact("a"), Fact("gone", forgotten: true)), tooMany })
        {
            using var response = await host.Client.SendAsync(host.SignedPost(Spaces + Sam + "/give", GatewayRole.Voice, owner, body.Write()));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
        }
        using (var badSpace = await host.Client.SendAsync(host.SignedPost(Spaces + "Household/give", GatewayRole.Voice, owner, Of(Fact("a")).Write())))
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(badSpace));
        using (var get = await host.Client.SendAsync(host.SignedGet(Spaces + Sam + "/give", GatewayRole.Voice, owner)))
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(get));
        using (var fromFriend = await host.Client.SendAsync(host.SignedPost(Spaces + Sam + "/give", GatewayRole.Voice, friend, Of(Fact("a")).Write())))
        {
            Assert.Equal(HttpStatusCode.Forbidden, fromFriend.StatusCode);
            Assert.Equal("access.friend", await GatewayTestHost.FailureCode(fromFriend));
        }
        using (var unsigned = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, host.Origin.CanonicalOrigin + Spaces + Sam + "/give")
        {
            Content = new ByteArrayContent(Of(Fact("a")).Write()) { Headers = { ContentType = new("application/json") } }
        }))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Empty(host.Server.MemorySpaces);
    }
}
