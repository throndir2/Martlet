using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Memory spaces (docs/ACCOUNTS.md, "Memory spaces"): every host keeps one memory document per space, apart from the
/// others and from the old single document, for paired member devices only.</summary>
public sealed class MemorySpaceTests
{
    private const string Spaces = "/martlet/v1/memories/spaces/";
    private static readonly string Sam = MemorySpaceId.Account(Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7"));
    private static readonly string Alex = MemorySpaceId.Account(Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0"));
    private static readonly string EmptyDigest = SharedMemories.Empty.Digest();

    private sealed class SpaceStorage : IGatewayMemorySpaceStorage
    {
        internal readonly Dictionary<string, byte[]> Saved = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> List() => Saved.Keys.ToArray();
        public byte[]? Load(string space) => Saved.TryGetValue(space, out var bytes) ? bytes : null;
        public void Save(string space, byte[] bytes) => Saved[space] = (byte[])bytes.Clone();
    }

    private static SharedMemories OneFact(string content, Guid? id = null)
    {
        var factId = id ?? Guid.NewGuid();
        return SharedMemories.Empty.With(new SharedMemory
        {
            Id = factId, Revision = 1, UpdatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), UpdatedBy = "home-pc",
            Fact = SharedMemories.Canonical(JsonSerializer.Serialize(new { id = factId, content }))
        });
    }

    private static async Task<(string? Space, string Digest, SharedMemories Memories)> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        Assert.Equal("fixture-host", root.GetProperty("host_id").GetString());
        var space = root.TryGetProperty("space", out var named) ? named.GetString() : null;
        return (space, root.GetProperty("digest").GetString()!,
            SharedMemories.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("memories"))));
    }

    private static async Task<(string? Space, string Digest)> DigestAsync(GatewayTestHost host, GatewayRequestSigner signer, string space)
    {
        using var response = await host.Client.SendAsync(host.SignedGet(Spaces + space + "/digest", GatewayRole.Voice, signer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (document.RootElement.GetProperty("space").GetString(), document.RootElement.GetProperty("digest").GetString()!);
    }

    private static async Task<GatewayRequestSigner> MemberAsync(GatewayTestHost host, string deviceId = "home-pc") =>
        new(host.Identity, await host.PairAsync(deviceId: deviceId), host.Clock);

    [Fact]
    public async Task Each_space_keeps_its_own_memories_apart_from_the_others_and_from_the_old_document()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var storage = new SpaceStorage();
        host.Server.AttachMemorySpaceStorage(storage);
        var member = await MemberAsync(host);
        var cat = OneFact("Sam's cat is called Miso.");

        using (var merged = await host.Client.SendAsync(host.SignedPost(Spaces + Sam, GatewayRole.Voice, member, cat.Write())))
        {
            var (space, digest, memories) = await ReadAsync(merged);
            Assert.Equal(Sam, space);
            Assert.Equal(cat.Digest(), digest);
            Assert.Single(memories.Live);
        }
        using (var read = await host.Client.SendAsync(host.SignedGet(Spaces + Sam, GatewayRole.Voice, member)))
        {
            var (space, digest, memories) = await ReadAsync(read);
            Assert.Equal((Sam, cat.Digest()), (space, digest));
            Assert.Equal(cat.Facts.Single().Fact, memories.Live.Single().Fact);
        }
        Assert.Equal((Sam, cat.Digest()), await DigestAsync(host, member, Sam));

        // Another account's space, the household and the old single document stay empty; a read never makes a space.
        foreach (var other in new[] { Alex, MemorySpaceId.Household })
        {
            using var read = await host.Client.SendAsync(host.SignedGet(Spaces + other, GatewayRole.Voice, member));
            var (space, digest, memories) = await ReadAsync(read);
            Assert.Equal((other, EmptyDigest), (space, digest));
            Assert.Empty(memories.Facts);
            Assert.Equal((other, EmptyDigest), await DigestAsync(host, member, other));
        }
        using (var old = await host.Client.SendAsync(host.SignedGet("/martlet/v1/memories", GatewayRole.Voice, member)))
        {
            var (space, _, memories) = await ReadAsync(old);
            Assert.Null(space);
            Assert.Empty(memories.Facts);
        }
        Assert.Equal([Sam], host.Server.MemorySpaces);
        Assert.Equal([Sam], storage.Saved.Keys);

        // The old route keeps working on its own document.
        var tea = OneFact("The owner drinks green tea.");
        using (var old = await host.Client.SendAsync(host.SignedPost("/martlet/v1/memories", GatewayRole.Voice, member, tea.Write())))
            Assert.Equal(tea.Digest(), (await ReadAsync(old)).Digest);
        using (var read = await host.Client.SendAsync(host.SignedGet(Spaces + Sam, GatewayRole.Voice, member)))
            Assert.Equal(cat.Digest(), (await ReadAsync(read)).Digest);

        // A merge that brings nothing makes no space.
        using (var nothing = await host.Client.SendAsync(host.SignedPost(Spaces + Alex, GatewayRole.Voice, member, SharedMemories.Empty.Write())))
            Assert.Empty((await ReadAsync(nothing)).Memories.Facts);
        Assert.Equal([Sam], host.Server.MemorySpaces);
    }

    [Fact]
    public async Task A_restarted_host_serves_the_spaces_it_saved_and_ignores_files_that_are_not_spaces()
    {
        var storage = new SpaceStorage();
        var cat = OneFact("Sam's cat is called Miso.");
        var household = OneFact("The household's Wi-Fi is called Nest.");
        await using (var first = await GatewayTestHost.StartAsync())
        {
            first.Server.AttachMemorySpaceStorage(storage);
            var member = await MemberAsync(first);
            using (var a = await first.Client.SendAsync(first.SignedPost(Spaces + Sam, GatewayRole.Voice, member, cat.Write())))
                Assert.Equal(HttpStatusCode.OK, a.StatusCode);
            using (var b = await first.Client.SendAsync(first.SignedPost(Spaces + MemorySpaceId.Household, GatewayRole.Voice, member, household.Write())))
                Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        }
        storage.Saved["Account-NOT-A-SPACE"] = cat.Write();

        await using var second = await GatewayTestHost.StartAsync();
        second.Server.AttachMemorySpaceStorage(storage);
        Assert.Equal([Sam, MemorySpaceId.Household], second.Server.MemorySpaces);
        var again = await MemberAsync(second);
        Assert.Equal((Sam, cat.Digest()), await DigestAsync(second, again, Sam));
        Assert.Equal((MemorySpaceId.Household, household.Digest()), await DigestAsync(second, again, MemorySpaceId.Household));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Household")]
    [InlineData("households")]
    [InlineData("account-5A3F0C9E8B7D4E21A6C3B2F1D0E9A8B7")]
    [InlineData("account-5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b")]
    [InlineData("account-5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7")]
    [InlineData("character-5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7z")]
    [InlineData("friend-5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7")]
    [InlineData("household/x")]
    [InlineData("household?x=1")]
    [InlineData("household/digest/digest")]
    [InlineData("..%2Fmemories")]
    public async Task Anything_but_a_space_ID_is_refused(string space)
    {
        await using var host = await GatewayTestHost.StartAsync();
        var member = await MemberAsync(host);
        foreach (var request in new[]
        {
            host.SignedGet(Spaces + space, GatewayRole.Voice, member),
            host.SignedGet(Spaces + space + "/digest", GatewayRole.Voice, member),
            host.SignedPost(Spaces + space, GatewayRole.Voice, member, OneFact("x").Write())
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
            }
        }
        Assert.Empty(host.Server.MemorySpaces);
    }

    [Fact]
    public async Task Friends_API_keys_unsigned_requests_and_posts_to_a_digest_are_refused()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var owner = await FriendAccessTests.SetUpSignInAsync(host);
        var friend = await FriendAccessTests.FriendAsync(host, owner);
        var body = OneFact("Sam's cat is called Miso.").Write();

        foreach (var request in new[]
        {
            host.SignedGet(Spaces + Sam, GatewayRole.Voice, friend),
            host.SignedGet(Spaces + Sam + "/digest", GatewayRole.Voice, friend),
            host.SignedPost(Spaces + Sam, GatewayRole.Voice, friend, body)
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("access.friend", await GatewayTestHost.FailureCode(response));
            }
        }

        using (var keyed = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + Spaces + Sam))
        {
            keyed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "martlet_" + new string('a', 43));
            using var response = await host.Client.SendAsync(keyed);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("key.scope", await GatewayTestHost.FailureCode(response));
        }
        using (var unsigned = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + Spaces + Sam)))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using (var postDigest = host.SignedPost(Spaces + Sam + "/digest", GatewayRole.Voice, owner, body))
        using (var response = await host.Client.SendAsync(postDigest))
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
        Assert.Empty(host.Server.MemorySpaces);
    }

    [Fact]
    public async Task The_access_hook_decides_which_device_may_read_or_write_which_space()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var sams = await MemberAsync(host, "sams-pc");
        var alexs = await MemberAsync(host, "alexs-pc");
        var asked = new List<(string Device, string Space, GatewayMemorySpaceUse Use)>();
        host.Server.MemorySpaceAccess = (principal, space, use) =>
        {
            lock (asked) asked.Add((principal.DeviceId, space, use));
            return space == MemorySpaceId.Household || principal.DeviceId == "sams-pc" && space == Sam;
        };
        var cat = OneFact("Sam's cat is called Miso.");

        using (var mine = await host.Client.SendAsync(host.SignedPost(Spaces + Sam, GatewayRole.Voice, sams, cat.Write())))
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal([("sams-pc", Sam, GatewayMemorySpaceUse.Read), ("sams-pc", Sam, GatewayMemorySpaceUse.Write)], asked);

        foreach (var request in new[]
        {
            host.SignedGet(Spaces + Sam, GatewayRole.Voice, alexs),
            host.SignedGet(Spaces + Sam + "/digest", GatewayRole.Voice, alexs),
            host.SignedPost(Spaces + Sam, GatewayRole.Voice, alexs, OneFact("Alex wrote here.").Write())
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("memories.space_denied", await GatewayTestHost.FailureCode(response));
            }
        }
        using (var household = await host.Client.SendAsync(host.SignedGet(Spaces + MemorySpaceId.Household, GatewayRole.Voice, alexs)))
            Assert.Equal(HttpStatusCode.OK, household.StatusCode);
        Assert.Equal((Sam, cat.Digest()), await DigestAsync(host, sams, Sam));
    }

    [Fact]
    public async Task A_host_keeps_at_most_64_spaces_and_refuses_a_new_one_past_that()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var member = await MemberAsync(host);
        for (var i = 0; i < 64; i++)
        {
            var space = MemorySpaceId.Character(new Guid(i + 1, 0, 0, new byte[8]));
            using var response = await host.Client.SendAsync(host.SignedPost(Spaces + space, GatewayRole.Voice, member, OneFact($"Fact {i}.").Write()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(64, host.Server.MemorySpaces.Count);

        using (var full = await host.Client.SendAsync(host.SignedPost(Spaces + Sam, GatewayRole.Voice, member, OneFact("One too many.").Write())))
        {
            Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
            Assert.Equal("memories.spaces_full", await GatewayTestHost.FailureCode(full));
        }
        // Spaces it keeps still take changes, and reading a space it doesn't keep still works.
        var kept = MemorySpaceId.Character(new Guid(1, 0, 0, new byte[8]));
        using (var more = await host.Client.SendAsync(host.SignedPost(Spaces + kept, GatewayRole.Voice, member, OneFact("Another fact.").Write())))
            Assert.Equal(2, (await ReadAsync(more)).Memories.Live.Count());
        using (var read = await host.Client.SendAsync(host.SignedGet(Spaces + Sam, GatewayRole.Voice, member)))
            Assert.Empty((await ReadAsync(read)).Memories.Facts);
        Assert.Equal(64, host.Server.MemorySpaces.Count);
    }

    [Theory]
    [InlineData("household", true)]
    [InlineData("account-5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7", true)]
    [InlineData("character-00000000000000000000000000000000", true)]
    [InlineData("account-", false)]
    [InlineData("character-5a3f0c9e8b7d4e21a6c3b2f1d0e9a8bG", false)]
    [InlineData(" household", false)]
    [InlineData(null, false)]
    public void Space_IDs_match_the_shared_contract(string? value, bool valid)
    {
        Assert.Equal(valid, MemorySpaceId.IsValid(value));
        Assert.Equal(value is not null && System.Text.RegularExpressions.Regex.IsMatch(value,
            "^(household|account-[0-9a-f]{32}|character-[0-9a-f]{32})$"), MemorySpaceId.IsValid(value));
    }
}
