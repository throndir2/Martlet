using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Core.Creations;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Creations per account (docs/ACCOUNTS.md, docs/CREATIONS.md): every host keeps one creation list per account, apart
/// from the others and from the old single list, with one pool of pieces, for paired member devices that may use the account.</summary>
public sealed class AccountCreationTests
{
    private const string Accounts = "/martlet/v1/creations/accounts/";
    private static readonly Guid Sam = Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7");
    private static readonly Guid Alex = Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0");
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class Storage : IGatewayCreationStorage, IGatewayAccountCreationStorage
    {
        internal byte[]? Library;
        internal readonly Dictionary<Guid, byte[]> Lists = [];
        internal readonly Dictionary<string, byte[]> Chunks = new(StringComparer.Ordinal);
        public byte[]? LoadLibrary() => Library;
        public void SaveLibrary(byte[] bytes) => Library = bytes;
        public bool HasChunk(string sha256) => Chunks.ContainsKey(sha256);
        public byte[]? LoadChunk(string sha256) => Chunks.TryGetValue(sha256, out var bytes) ? (byte[])bytes.Clone() : null;
        public void SaveChunk(string sha256, byte[] bytes) => Chunks[sha256] = (byte[])bytes.Clone();
        public void RemoveChunk(string sha256) => Chunks.Remove(sha256);
        public IReadOnlyCollection<Guid> List() => Lists.Keys.ToArray();
        public byte[]? Load(Guid account) => Lists.TryGetValue(account, out var bytes) ? bytes : null;
        public void Save(Guid account, byte[] bytes) => Lists[account] = (byte[])bytes.Clone();
    }

    private static (CreationLibrary Library, Creation Creation, byte[] Data) OneCreation(string title, byte[]? data = null)
    {
        data ??= Encoding.UTF8.GetBytes("fixture audio for " + title);
        var creation = new Creation
        {
            Id = CreationLibrary.NewId(), Kind = "fixture", KindVersion = 1, Title = title,
            CreatedBy = new() { Device = "home-pc", Computer = "HOME-PC" }, CreatedAt = Now,
            Assets = [CreationLibrary.Asset("audio", "audio/flac", data)], Revision = 1, UpdatedAt = Now, UpdatedBy = "home-pc"
        };
        return (CreationLibrary.Empty.Add(creation, Now), creation, data);
    }

    private static string Route(Guid account) => Accounts + account.ToString("N");

    private static byte[] ChunkBody(byte[] data) => Encoding.ASCII.GetBytes("{\"data_base64\":\"" + Convert.ToBase64String(data) + "\"}");

    private static async Task<(string? Account, CreationLibrary Library, string[] Present)> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        Assert.Equal("fixture-host", root.GetProperty("host_id").GetString());
        var account = root.TryGetProperty("account", out var named) ? named.GetString() : null;
        return (account, CreationLibrary.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("library"))),
            root.GetProperty("present").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    private static async Task<(string? Account, string Digest, int Present)> DigestAsync(GatewayTestHost host, GatewayRequestSigner signer, string route)
    {
        using var response = await host.Client.SendAsync(host.SignedGet(route + "/digest", GatewayRole.Voice, signer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        return (root.TryGetProperty("account", out var named) ? named.GetString() : null, root.GetProperty("digest").GetString()!,
            root.GetProperty("present_count").GetInt32());
    }

    private static async Task<GatewayRequestSigner> MemberAsync(GatewayTestHost host, string deviceId = "home-pc") =>
        new(host.Identity, await host.PairAsync(deviceId: deviceId), host.Clock);

    [Fact]
    public async Task Each_account_keeps_its_own_creations_apart_from_the_others_and_from_the_old_list()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var storage = new Storage();
        host.Server.AttachAccountCreationStorage(storage);
        host.Server.AttachCreationStorage(storage);
        var member = await MemberAsync(host);
        var (library, creation, data) = OneCreation("Sam's song");
        var sha256 = creation.Assets!.Single().Chunks.Single();

        using (var merged = await host.Client.SendAsync(host.SignedPost(Route(Sam), GatewayRole.Voice, member, library.Write())))
        {
            var (account, copy, present) = await ReadAsync(merged);
            Assert.Equal(Sam.ToString("N"), account);
            Assert.Equal(library.Digest(), copy.Digest());
            Assert.Empty(present);
        }
        using (var sent = await host.Client.SendAsync(host.SignedPost(Route(Sam) + "/chunks/" + sha256, GatewayRole.Voice, member, ChunkBody(data))))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            using var document = await JsonDocument.ParseAsync(await sent.Content.ReadAsStreamAsync());
            Assert.Equal(Sam.ToString("N"), document.RootElement.GetProperty("account").GetString());
            Assert.Equal([sha256], document.RootElement.GetProperty("present").EnumerateArray().Select(e => e.GetString()));
        }
        Assert.Equal((Sam.ToString("N"), library.Digest(), 1), await DigestAsync(host, member, Route(Sam)));
        using (var piece = await host.Client.SendAsync(host.SignedGet(Route(Sam) + "/chunks/" + sha256, GatewayRole.Voice, member)))
        {
            Assert.Equal(HttpStatusCode.OK, piece.StatusCode);
            using var document = await JsonDocument.ParseAsync(await piece.Content.ReadAsStreamAsync());
            Assert.Equal(data, Convert.FromBase64String(document.RootElement.GetProperty("data_base64").GetString()!));
        }

        // Alex's list and the old single list stay empty and see none of Sam's pieces; a read makes no list.
        var empty = CreationLibrary.Empty.Digest();
        Assert.Equal((Alex.ToString("N"), empty, 0), await DigestAsync(host, member, Route(Alex)));
        Assert.Equal(((string?)null, empty, 0), await DigestAsync(host, member, "/martlet/v1/creations"));
        foreach (var route in new[] { Route(Alex), "/martlet/v1/creations" })
        {
            using (var read = await host.Client.SendAsync(host.SignedGet(route, GatewayRole.Voice, member)))
                Assert.Empty((await ReadAsync(read)).Library.Creations);
            using var other = await host.Client.SendAsync(host.SignedGet(route + "/chunks/" + sha256, GatewayRole.Voice, member));
            Assert.Equal("chunk.missing", await GatewayTestHost.FailureCode(other));
        }
        Assert.Equal([Sam], host.Server.CreationAccounts);
        Assert.Equal([Sam], storage.Lists.Keys);
        Assert.Null(storage.Library);

        // A merge that brings nothing makes no list.
        using (var nothing = await host.Client.SendAsync(host.SignedPost(Route(Alex), GatewayRole.Voice, member, CreationLibrary.Empty.Write())))
            Assert.Empty((await ReadAsync(nothing)).Library.Creations);
        Assert.Equal([Sam], host.Server.CreationAccounts);
    }

    [Fact]
    public async Task One_pool_keeps_a_piece_until_no_list_uses_it_and_a_restarted_host_serves_every_list()
    {
        var storage = new Storage();
        var (library, creation, data) = OneCreation("The owner's song");
        var sha256 = creation.Assets!.Single().Chunks.Single();
        await using (var first = await GatewayTestHost.StartAsync())
        {
            first.Server.AttachAccountCreationStorage(storage);
            first.Server.AttachCreationStorage(storage);
            var member = await MemberAsync(first);
            // The owner's list and the old single list hold the same creation (the owner bridge); the piece is sent once.
            using (var a = await first.Client.SendAsync(first.SignedPost(Route(Sam), GatewayRole.Voice, member, library.Write())))
                Assert.Equal(HttpStatusCode.OK, a.StatusCode);
            using (var b = await first.Client.SendAsync(first.SignedPost("/martlet/v1/creations", GatewayRole.Voice, member, library.Write())))
                Assert.Empty((await ReadAsync(b)).Present);
            using (var sent = await first.Client.SendAsync(first.SignedPost(Route(Sam) + "/chunks/" + sha256, GatewayRole.Voice, member, ChunkBody(data))))
                Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            Assert.Equal(1, (await DigestAsync(first, member, "/martlet/v1/creations")).Present);

            // Deleting it from the owner's list keeps the piece for the old list.
            var removed = library.Remove(creation.Id, "home-pc", Now.AddMinutes(1));
            using (var c = await first.Client.SendAsync(first.SignedPost(Route(Sam), GatewayRole.Voice, member, removed.Write())))
                Assert.Empty((await ReadAsync(c)).Library.Live);
            Assert.True(storage.Chunks.ContainsKey(sha256));
            Assert.Equal(0, (await DigestAsync(first, member, Route(Sam))).Present);
            Assert.Equal(1, (await DigestAsync(first, member, "/martlet/v1/creations")).Present);
        }

        storage.Lists[Alex] = Encoding.UTF8.GetBytes("not a creation list");
        await using var second = await GatewayTestHost.StartAsync();
        second.Server.AttachAccountCreationStorage(storage);
        second.Server.AttachCreationStorage(storage);
        var again = await MemberAsync(second);
        Assert.Equal([Sam], second.Server.CreationAccounts);
        Assert.Equal(1, (await DigestAsync(second, again, "/martlet/v1/creations")).Present);
        Assert.Empty((await ReadAsync(await second.Client.SendAsync(second.SignedGet(Route(Sam), GatewayRole.Voice, again)))).Library.Live);

        // Once no list uses the piece, the host deletes it.
        var gone = library.Remove(creation.Id, "home-pc", Now.AddMinutes(2));
        using (var d = await second.Client.SendAsync(second.SignedPost("/martlet/v1/creations", GatewayRole.Voice, again, gone.Write())))
            Assert.Empty((await ReadAsync(d)).Library.Live);
        Assert.Empty(storage.Chunks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5A3F0C9E8B7D4E21A6C3B2F1D0E9A8B7")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b")]
    [InlineData("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7z")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/digest/digest")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/chunks/xyz")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7?x=1")]
    [InlineData("..%2F")]
    public async Task Anything_but_an_account_ID_and_its_routes_is_refused(string rest)
    {
        await using var host = await GatewayTestHost.StartAsync();
        var member = await MemberAsync(host);
        foreach (var request in new[]
        {
            host.SignedGet(Accounts + rest, GatewayRole.Voice, member),
            host.SignedPost(Accounts + rest, GatewayRole.Voice, member, OneCreation("x").Library.Write())
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
            }
        }
        Assert.Empty(host.Server.CreationAccounts);
    }

    [Fact]
    public async Task Friends_and_API_keys_are_refused_and_the_access_hook_decides_which_device_may_use_which_account()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var owner = await FriendAccessTests.SetUpSignInAsync(host);
        var friend = await FriendAccessTests.FriendAsync(host, owner);
        var body = OneCreation("Sam's song").Library.Write();
        foreach (var request in new[]
        {
            host.SignedGet(Route(Sam), GatewayRole.Voice, friend),
            host.SignedGet(Route(Sam) + "/digest", GatewayRole.Voice, friend),
            host.SignedPost(Route(Sam), GatewayRole.Voice, friend, body)
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
                Assert.Equal("access.friend", await GatewayTestHost.FailureCode(response));
        }
        using (var keyed = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + Route(Sam)))
        {
            keyed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "martlet_" + new string('a', 43));
            using var response = await host.Client.SendAsync(keyed);
            Assert.Equal("key.scope", await GatewayTestHost.FailureCode(response));
        }

        var sams = await MemberAsync(host, "sams-pc");
        var alexs = await MemberAsync(host, "alexs-pc");
        var asked = new List<(string Device, string Space, GatewayMemorySpaceUse Use)>();
        host.Server.MemorySpaceAccess = (principal, space, use) =>
        {
            lock (asked) asked.Add((principal.DeviceId, space, use));
            return principal.DeviceId == "sams-pc" && space == MemorySpaceId.Account(Sam);
        };
        using (var mine = await host.Client.SendAsync(host.SignedPost(Route(Sam), GatewayRole.Voice, sams, body)))
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal([("sams-pc", MemorySpaceId.Account(Sam), GatewayMemorySpaceUse.Read), ("sams-pc", MemorySpaceId.Account(Sam), GatewayMemorySpaceUse.Write)], asked);
        foreach (var request in new[]
        {
            host.SignedGet(Route(Sam), GatewayRole.Voice, alexs),
            host.SignedGet(Route(Sam) + "/digest", GatewayRole.Voice, alexs),
            host.SignedGet(Route(Sam) + "/chunks/" + new string('a', 64), GatewayRole.Voice, alexs),
            host.SignedPost(Route(Sam), GatewayRole.Voice, alexs, body)
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("creations.account_denied", await GatewayTestHost.FailureCode(response));
            }
        }
        // The old single list keeps working for every paired device, as before.
        using (var old = await host.Client.SendAsync(host.SignedGet("/martlet/v1/creations", GatewayRole.Voice, alexs)))
            Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Equal([Sam], host.Server.CreationAccounts);
    }

    [Fact]
    public async Task A_host_keeps_lists_for_at_most_64_accounts()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var member = await MemberAsync(host);
        for (var i = 0; i < 64; i++)
        {
            using var response = await host.Client.SendAsync(host.SignedPost(Route(new Guid(i + 1, 0, 0, new byte[8])), GatewayRole.Voice, member,
                OneCreation($"Song {i}").Library.Write()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var full = await host.Client.SendAsync(host.SignedPost(Route(Sam), GatewayRole.Voice, member, OneCreation("One too many").Library.Write())))
        {
            Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
            Assert.Equal("creations.accounts_full", await GatewayTestHost.FailureCode(full));
        }
        using (var more = await host.Client.SendAsync(host.SignedPost(Route(new Guid(1, 0, 0, new byte[8])), GatewayRole.Voice, member,
            OneCreation("Another song").Library.Write())))
            Assert.Equal(2, (await ReadAsync(more)).Library.Live.Count);
        Assert.Equal(64, host.Server.CreationAccounts.Count);
    }
}
