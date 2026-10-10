using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Each account's settings (docs/ACCOUNTS.md, "Account settings"): every host keeps one settings document per account,
/// apart from the household's and from every other account's, never with an API key, for member devices where the account is
/// signed in.</summary>
public sealed class AccountSettingsRouteTests
{
    private const string Accounts = "/martlet/v1/settings/accounts/";
    private const string Sam = "5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7";
    private const string Alex = "0b1c2d3e4f5061728394a5b6c7d8e9f0";
    private static readonly string EmptyDigest = SharedSettings.Empty.Digest();
    private static readonly DateTimeOffset At = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class AccountStorage : IGatewayAccountSettingsStorage
    {
        internal readonly Dictionary<string, byte[]> Saved = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> List() => Saved.Keys.ToArray();
        public byte[]? Load(string account) => Saved.TryGetValue(account, out var bytes) ? bytes : null;
        public void Save(string account, byte[] bytes) => Saved[account] = (byte[])bytes.Clone();
    }

    private static SharedSettings Theme(string theme, string by = "sams-pc") =>
        SharedSettings.Empty.Put(SettingScopes.Appearance, JsonSerializer.Serialize(theme), null, by, At);

    private static async Task<(string? Account, string Digest, SharedSettings Settings)> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        Assert.Equal("fixture-host", root.GetProperty("host_id").GetString());
        var account = root.TryGetProperty("account", out var named) ? named.GetString() : null;
        return (account, root.GetProperty("digest").GetString()!,
            SharedSettings.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("settings"))));
    }

    private static async Task<(string? Account, string Digest)> DigestAsync(GatewayTestHost host, GatewayRequestSigner signer, string account)
    {
        using var response = await host.Client.SendAsync(host.SignedGet(Accounts + account + "/digest", GatewayRole.Voice, signer));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (document.RootElement.GetProperty("account").GetString(), document.RootElement.GetProperty("digest").GetString()!);
    }

    private static async Task<GatewayRequestSigner> MemberAsync(GatewayTestHost host, string deviceId = "sams-pc") =>
        new(host.Identity, await host.PairAsync(deviceId: deviceId), host.Clock);

    [Fact]
    public async Task Each_account_keeps_its_own_settings_apart_from_the_others_and_from_the_household()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        var storage = new AccountStorage();
        host.Server.AttachAccountSettingsStorage(storage);
        var member = await MemberAsync(host);
        var dark = Theme("dark");

        using (var merged = await host.Client.SendAsync(host.SignedPost(Accounts + Sam, GatewayRole.Voice, member, dark.Write())))
        {
            var (account, digest, settings) = await ReadAsync(merged);
            Assert.Equal((Sam, dark.Digest()), (account, digest));
            Assert.Equal("\"dark\"", settings.Find(SettingScopes.Appearance)!.Value);
        }
        using (var read = await host.Client.SendAsync(host.SignedGet(Accounts + Sam, GatewayRole.Voice, member)))
        {
            var (account, digest, _) = await ReadAsync(read);
            Assert.Equal((Sam, dark.Digest()), (account, digest));
        }
        Assert.Equal((Sam, dark.Digest()), await DigestAsync(host, member, Sam));

        // Another account and the household's settings stay empty; a read never makes an account's copy.
        using (var other = await host.Client.SendAsync(host.SignedGet(Accounts + Alex, GatewayRole.Voice, member)))
        {
            var (account, digest, settings) = await ReadAsync(other);
            Assert.Equal((Alex, EmptyDigest), (account, digest));
            Assert.Empty(settings.Settings);
        }
        using (var household = await host.Client.SendAsync(host.SignedGet("/martlet/v1/settings", GatewayRole.Voice, member)))
        {
            var (account, _, settings) = await ReadAsync(household);
            Assert.Null(account);
            Assert.Empty(settings.Settings);
        }
        Assert.Equal([Sam], host.Server.AccountSettings);
        Assert.Equal([Sam], storage.Saved.Keys);

        // A later change merges as last writer wins; the household's route keeps its own document.
        var light = SharedSettings.Empty.Put(SettingScopes.Appearance, "\"light\"", null, "alexs-pc", At.AddMinutes(1));
        using (var newer = await host.Client.SendAsync(host.SignedPost(Accounts + Sam, GatewayRole.Voice, member, light.Write())))
            Assert.Equal("\"light\"", (await ReadAsync(newer)).Settings.Find(SettingScopes.Appearance)!.Value);
        using (var nothing = await host.Client.SendAsync(host.SignedPost(Accounts + Alex, GatewayRole.Voice, member, SharedSettings.Empty.Write())))
            Assert.Empty((await ReadAsync(nothing)).Settings.Settings);
        Assert.Equal([Sam], host.Server.AccountSettings);
    }

    [Fact]
    public async Task A_restarted_host_serves_the_accounts_it_saved_and_ignores_files_that_are_not_accounts()
    {
        var storage = new AccountStorage();
        var dark = Theme("dark");
        await using (var first = await GatewayTestHost.StartAsync())
        {
            first.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
            first.Server.AttachAccountSettingsStorage(storage);
            var member = await MemberAsync(first);
            using var response = await first.Client.SendAsync(first.SignedPost(Accounts + Sam, GatewayRole.Voice, member, dark.Write()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        storage.Saved["5A3F0C9E8B7D4E21A6C3B2F1D0E9A8B7"] = dark.Write();

        await using var second = await GatewayTestHost.StartAsync();
        second.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        second.Server.AttachAccountSettingsStorage(storage);
        Assert.Equal([Sam], second.Server.AccountSettings);
        Assert.Equal((Sam, dark.Digest()), await DigestAsync(second, await MemberAsync(second), Sam));
    }

    [Theory]
    [InlineData("")]
    [InlineData("5A3F0C9E8B7D4E21A6C3B2F1D0E9A8B7")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b")]
    [InlineData("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7")]
    [InlineData("account-5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/x")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7?x=1")]
    [InlineData("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7/digest/digest")]
    [InlineData("..%2Fdigest")]
    public async Task Anything_but_an_account_ID_is_refused(string account)
    {
        await using var host = await GatewayTestHost.StartAsync();
        var member = await MemberAsync(host);
        foreach (var request in new[]
        {
            host.SignedGet(Accounts + account, GatewayRole.Voice, member),
            host.SignedGet(Accounts + account + "/digest", GatewayRole.Voice, member),
            host.SignedPost(Accounts + account, GatewayRole.Voice, member, Theme("dark").Write())
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
            }
        }
        Assert.Empty(host.Server.AccountSettings);
    }

    [Fact]
    public async Task An_accounts_settings_never_carry_an_API_key()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        var member = await MemberAsync(host);
        var keyed = SharedSettings.Empty.Put(SettingScopes.Appearance, "\"dark\"", "sk-not-for-an-account", "sams-pc", At);

        using (var response = await host.Client.SendAsync(host.SignedPost(Accounts + Sam, GatewayRole.Voice, member, keyed.Write())))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
        }
        Assert.Empty(host.Server.AccountSettings);
    }

    [Fact]
    public async Task Friends_API_keys_unsigned_requests_and_posts_to_a_digest_are_refused()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var owner = await FriendAccessTests.SetUpSignInAsync(host);
        var friend = await FriendAccessTests.FriendAsync(host, owner);
        var body = Theme("dark").Write();

        foreach (var request in new[]
        {
            host.SignedGet(Accounts + Sam, GatewayRole.Voice, friend),
            host.SignedGet(Accounts + Sam + "/digest", GatewayRole.Voice, friend),
            host.SignedPost(Accounts + Sam, GatewayRole.Voice, friend, body)
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("access.friend", await GatewayTestHost.FailureCode(response));
            }
        }
        using (var keyed = new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + Accounts + Sam))
        {
            keyed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "martlet_" + new string('a', 43));
            using var response = await host.Client.SendAsync(keyed);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("key.scope", await GatewayTestHost.FailureCode(response));
        }
        using (var unsigned = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, host.Origin.CanonicalOrigin + Accounts + Sam)))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using (var postDigest = host.SignedPost(Accounts + Sam + "/digest", GatewayRole.Voice, owner, body))
        using (var response = await host.Client.SendAsync(postDigest))
            Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
        Assert.Empty(host.Server.AccountSettings);
    }

    [Fact]
    public async Task Only_a_device_that_may_use_the_accounts_space_may_read_or_write_its_settings()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var sams = await MemberAsync(host, "sams-pc");
        var alexs = await MemberAsync(host, "alexs-pc");
        var asked = new List<(string Device, string Space, GatewayMemorySpaceUse Use)>();
        host.Server.MemorySpaceAccess = (principal, space, use) =>
        {
            lock (asked) asked.Add((principal.DeviceId, space, use));
            return principal.DeviceId == "sams-pc" && space == MemorySpaceId.AccountPrefix + Sam;
        };
        var dark = Theme("dark");

        using (var mine = await host.Client.SendAsync(host.SignedPost(Accounts + Sam, GatewayRole.Voice, sams, dark.Write())))
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal([("sams-pc", "account-" + Sam, GatewayMemorySpaceUse.Read), ("sams-pc", "account-" + Sam, GatewayMemorySpaceUse.Write)], asked);

        foreach (var request in new[]
        {
            host.SignedGet(Accounts + Sam, GatewayRole.Voice, alexs),
            host.SignedGet(Accounts + Sam + "/digest", GatewayRole.Voice, alexs),
            host.SignedPost(Accounts + Sam, GatewayRole.Voice, alexs, Theme("light", "alexs-pc").Write())
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("settings.account_denied", await GatewayTestHost.FailureCode(response));
            }
        }
        Assert.Equal((Sam, dark.Digest()), await DigestAsync(host, sams, Sam));
    }

    [Fact]
    public async Task A_host_keeps_the_settings_of_at_most_64_accounts()
    {
        await using var host = await GatewayTestHost.StartAsync();
        host.Server.MemorySpaceAccess = GatewayMemorySpaces.EveryMember;
        var member = await MemberAsync(host);
        for (var i = 0; i < 64; i++)
        {
            var account = new Guid(i + 1, 0, 0, new byte[8]).ToString("N");
            using var response = await host.Client.SendAsync(host.SignedPost(Accounts + account, GatewayRole.Voice, member, Theme("dark").Write()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var full = await host.Client.SendAsync(host.SignedPost(Accounts + Sam, GatewayRole.Voice, member, Theme("dark").Write())))
        {
            Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
            Assert.Equal("settings.accounts_full", await GatewayTestHost.FailureCode(full));
        }
        Assert.Equal(64, host.Server.AccountSettings.Count);
    }
}
