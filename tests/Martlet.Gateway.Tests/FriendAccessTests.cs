using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>A host shared with a friend: the friend signs in with an identity the owner allowed as a friend and gets a credential
/// that reaches only the host's engines; everything else stays the owner's.</summary>
public sealed class FriendAccessTests
{
    internal const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private sealed class MemorySignInStorage : IGatewaySignInStorage
    {
        internal byte[]? Bytes;
        public byte[]? Load() => Bytes;
        public void Save(byte[] bytes) => Bytes = bytes;
    }

    /// <summary>A provider whose proof <c>{"code": "&lt;subject&gt;"}</c> signs in as idp:&lt;subject&gt;.</summary>
    private sealed class SubjectProvider : IGatewaySignInProvider
    {
        public ValueTask<string?> AuthorizeAsync(GatewaySignInAttempt attempt, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("https://idp.example/authorize?state=" + attempt.State);

        public ValueTask<GatewaySignInIdentity> VerifyAsync(GatewaySignInAttempt attempt, JsonElement proof, CancellationToken cancellationToken)
        {
            var subject = proof.GetProperty("code").GetString()!;
            return ValueTask.FromResult(new GatewaySignInIdentity("idp", subject, subject + "@example.net"));
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage Post(GatewayTestHost host, string path, object body) =>
        new(HttpMethod.Post, host.Origin.CanonicalOrigin + path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    /// <summary>Sets up sign-in on the host (provider "idp") and returns the owner's signer: <paramref name="owner"/>, or a desktop
    /// "home-pc" paired by card.</summary>
    internal static async Task<GatewayRequestSigner> SetUpSignInAsync(GatewayTestHost host, GatewayRequestSigner? owner = null)
    {
        host.Server.AttachSignInStorage(new MemorySignInStorage());
        host.Server.SignIn.Providers = config => config.Id == "idp" ? new SubjectProvider() : null;
        owner ??= new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "home-pc"), host.Clock);
        await ChangeAsync(host, owner, new
        {
            action = "provider", provider_config = new { id = "idp", kind = "oidc", name = "IdP", issuer = "https://idp.example", client_id = "martlet" }
        });
        return owner;
    }

    internal static async Task<JsonElement> ChangeAsync(GatewayTestHost host, GatewayRequestSigner owner, object change)
    {
        using var response = await host.Client.SendAsync(host.SignedPost("/martlet/v1/signin/settings", GatewayRole.Voice, owner,
            JsonSerializer.SerializeToUtf8Bytes(change)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    internal static async Task<(HttpStatusCode Status, JsonElement Body)> SignInAsync(GatewayTestHost host, string subject, string device)
    {
        using var begin = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/begin", new
        {
            protocol_version = new { major = 2, minor = 0 }, provider = "idp", code_challenge = Challenge, redirect_uri = "http://127.0.0.1:53111/"
        }));
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var attempt = (await ReadAsync(begin)).GetProperty("attempt_id").GetString();
        using var complete = await host.Client.SendAsync(Post(host, "/martlet/v1/signin/complete", new
        {
            protocol_version = new { major = 2, minor = 0 }, attempt_id = attempt, device_id = device, display_name = device.ToUpperInvariant(),
            proof = new { code = subject }
        }));
        return (complete.StatusCode, await ReadAsync(complete));
    }

    internal static GatewayRequestSigner Signer(GatewayTestHost host, JsonElement signedIn) => new(host.Identity, new()
    {
        CredentialId = signedIn.GetProperty("credential_id").GetString()!, DeviceId = signedIn.GetProperty("device_id").GetString()!,
        Roles = [GatewayRole.Voice], Secret = new(signedIn.GetProperty("credential_secret").GetString()!), Lifetime = new PairedDeviceLifetime()
    }, host.Clock);

    /// <summary>The owner allows <paramref name="subject"/> as a friend and the friend signs in from <paramref name="device"/>.</summary>
    internal static async Task<GatewayRequestSigner> FriendAsync(GatewayTestHost host, GatewayRequestSigner owner, string subject = "friend-1",
        string device = "friend-pc")
    {
        await ChangeAsync(host, owner, new { action = "allow", provider = "idp", subject, label = "Ana", access = "friend" });
        var (status, body) = await SignInAsync(host, subject, device);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("friend", body.GetProperty("access").GetString());
        return Signer(host, body);
    }

    [Fact]
    public async Task A_friend_reaches_only_the_engines_and_every_other_route_refuses_it()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var owner = await SetUpSignInAsync(host);
        var friend = await FriendAsync(host, owner);

        using (var version = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, friend)))
        {
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);
            Assert.Equal("friend", (await ReadAsync(version)).GetProperty("access").GetString());
        }
        using (var owners = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, owner)))
            Assert.False((await ReadAsync(owners)).TryGetProperty("access", out _));
        foreach (var path in new[] { "/martlet/v1/capabilities", "/martlet/v1/status" })
        {
            using var allowed = await host.Client.SendAsync(host.SignedGet(path, GatewayRole.Voice, friend));
            Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"{path}: {allowed.StatusCode}");
        }

        // Everything else is the owner's: refused before anything is read or changed.
        string[] gets =
        [
            "/martlet/v1/machine", "/martlet/v1/network", "/martlet/v1/cluster", "/martlet/v1/voices", "/martlet/v1/speaking-voices",
            "/martlet/v1/character-models", "/martlet/v1/creations", "/martlet/v1/home-assistant", "/martlet/v1/settings",
            "/martlet/v1/memories", "/martlet/v1/api-keys", "/martlet/v1/commands", "/martlet/v1/logs?after=0", "/martlet/v1/priority",
            "/martlet/v1/security/audit", "/martlet/v1/signin/settings"
        ];
        foreach (var path in gets)
        {
            using var refused = await host.Client.SendAsync(host.SignedGet(path, GatewayRole.Voice, friend));
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{path}: {refused.StatusCode}");
            Assert.Equal("access.friend", await GatewayTestHost.FailureCode(refused));
        }
        (string Path, string Body)[] posts =
        [
            ("/martlet/v1/network/join", """{"protocol_version":{"major":2,"minor":0},"display_name":"FRIEND","key":"key"}"""),
            ("/martlet/v1/network", "{}"),
            ("/martlet/v1/signin/settings", """{"action":"remove-owner"}"""),
            ("/martlet/v1/api-keys", "{}"),
            ("/martlet/v1/commands", """{"kind":"martlet.update","arguments":{"version":"9.9.9"}}"""),
            ("/martlet/v1/priority/hold", """{"routes":["martlet.gateway.ollama-chat.v1"],"ttl_ms":1000}"""),
            ("/martlet/v1/cluster", "{}"),
            ("/martlet/v1/settings", "{}"),
            ("/martlet/v1/memories", "{}"),
            ("/martlet/v1/voices", "{}")
        ];
        foreach (var (path, body) in posts)
        {
            using var refused = await host.Client.SendAsync(host.SignedPost(path, GatewayRole.Voice, friend, Encoding.UTF8.GetBytes(body)));
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"POST {path}: {refused.StatusCode}");
            Assert.Equal("access.friend", await GatewayTestHost.FailureCode(refused));
        }

        // The owner sees the friend's computer for what it is, and no request to join came from it.
        using (var network = await host.Client.SendAsync(host.SignedGet("/martlet/v1/network", GatewayRole.Voice, owner)))
        {
            var root = await ReadAsync(network);
            var device = Assert.Single(root.GetProperty("devices").EnumerateArray(), d => d.GetProperty("device_id").GetString() == "friend-pc");
            Assert.Equal("friend", device.GetProperty("access").GetString());
            Assert.False(Assert.Single(root.GetProperty("devices").EnumerateArray(), d => d.GetProperty("device_id").GetString() == "home-pc")
                .TryGetProperty("access", out _));
            Assert.Empty(root.GetProperty("joins").EnumerateArray());
        }
        using (var settings = await host.Client.SendAsync(host.SignedGet("/martlet/v1/signin/settings", GatewayRole.Voice, owner)))
        {
            var root = await ReadAsync(settings);
            Assert.Equal("friend", Assert.Single(root.GetProperty("allowed").EnumerateArray()).GetProperty("access").GetString());
            var enrolled = Assert.Single(root.GetProperty("enrolled").EnumerateArray());
            Assert.Equal(("friend-pc", "friend"), (enrolled.GetProperty("device_id").GetString(), enrolled.GetProperty("access").GetString()));
        }
        Assert.Contains(host.Server.Credentials.ListRegistrations(), r => r is { DeviceId: "friend-pc", Access: GatewayAccess.Friend });
        Assert.Contains(host.Server.Credentials.ListRegistrations(), r => r is { DeviceId: "home-pc", Access: GatewayAccess.Full });
        // Refusals don't lock a friend out: they aren't failed authentication.
        using (var still = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, friend)))
            Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        // Taking the friend off the list ends the friend's access at once.
        await ChangeAsync(host, owner, new { action = "disallow", provider = "idp", subject = "friend-1" });
        using (var revoked = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, friend)))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task A_sign_in_never_takes_over_another_computers_pairing()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var owner = await SetUpSignInAsync(host);
        await ChangeAsync(host, owner, new { action = "allow", provider = "idp", subject = "me" });
        await ChangeAsync(host, owner, new { action = "allow", provider = "idp", subject = "friend-1", access = "friend" });
        await ChangeAsync(host, owner, new { action = "allow", provider = "idp", subject = "friend-2", access = "friend" });

        // Neither the owner's own identity nor a friend signs in under the ID of a computer paired another way.
        foreach (var subject in new[] { "me", "friend-1" })
        {
            var (status, body) = await SignInAsync(host, subject, "home-pc");
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("signin.device_taken", body.GetProperty("code").GetString());
        }
        using (var still = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, owner)))
            Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        // A friend's computer stays theirs: another friend can't take its ID, while the same friend signs in again on it.
        var (first, firstBody) = await SignInAsync(host, "friend-1", "friend-pc");
        Assert.Equal(HttpStatusCode.Created, first);
        var friend = Signer(host, firstBody);
        var (other, otherBody) = await SignInAsync(host, "friend-2", "friend-pc");
        Assert.Equal((HttpStatusCode.Conflict, "signin.device_taken"), (other, otherBody.GetProperty("code").GetString()));
        using (var works = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, friend)))
            Assert.Equal(HttpStatusCode.OK, works.StatusCode);
        var (again, againBody) = await SignInAsync(host, "friend-1", "friend-pc");
        Assert.Equal(HttpStatusCode.Created, again);
        using (var old = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, friend)))
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var renewed = await host.Client.SendAsync(host.SignedGet("/martlet/v1/version", GatewayRole.Voice, Signer(host, againBody))))
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
    }

    private sealed class ServiceLab
    {
        internal ManualGatewayClock Clock { get; } = new(new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero));
        internal GatewayCredentialStore Credentials { get; }
        internal GatewaySignInService Service { get; }
        internal MemorySignInStorage Storage { get; } = new();
        internal List<string> Log { get; } = [];
        private static readonly JsonElement Proof = JsonDocument.Parse("{}").RootElement;

        internal ServiceLab()
        {
            Credentials = new(new GatewayHostIdentity { HostId = "home-host", SpkiFingerprint = "sha256:" + new string('a', 64) }, Clock);
            Service = new(Credentials, Clock, new SystemGatewayCrypto(), (_, message) => Log.Add(message))
            {
                IsMember = deviceId => deviceId == "member-pc"
            };
            Service.Attach(Storage);
            Service.Change(new()
            {
                Action = "provider", ProviderConfig = new() { Id = "idp", Kind = "oidc", Name = "IdP", Issuer = "https://idp.example", ClientId = "c" }
            }, "home-pc", CancellationToken.None);
            Service.Providers = _ => new SubjectProvider();
        }

        internal void Allow(string subject, string? access) =>
            Service.Change(new() { Action = "allow", Provider = "idp", Subject = subject, Access = access }, "home-pc", CancellationToken.None);

        internal async Task<IssuedDeviceCredential> SignInAsync(string subject, string device)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var (attempt, _) = await Service.BeginAsync("idp", Challenge, "http://127.0.0.1:53111/", CancellationToken.None);
            var proof = JsonDocument.Parse(JsonSerializer.Serialize(new { code = subject })).RootElement;
            return (await Service.CompleteAsync(attempt.Id, device, device.ToUpperInvariant(), proof, CancellationToken.None)).Credential;
        }

        internal bool Live(IssuedDeviceCredential credential) =>
            Credentials.ListRegistrations().Any(r => r.CredentialId == credential.CredentialId && !r.Revoked);
    }

    [Fact]
    public async Task A_member_desktops_id_is_never_signed_in_and_access_changes_revoke_the_old_credential()
    {
        var lab = new ServiceLab();
        lab.Allow("me", null);
        Assert.Equal("signin.device_taken", (await Assert.ThrowsAsync<GatewayProtocolException>(() => lab.SignInAsync("me", "member-pc")))
            .Failure.Code);

        // The owner's own identity signs a laptop in as a member; switching it to a friend revokes that credential, takes the
        // attestation away and records the laptop for removal from the network.
        var laptop = await lab.SignInAsync("me", "laptop");
        Assert.Equal(GatewayAccess.Full, laptop.Access);
        Assert.NotNull(lab.Service.Attestation("laptop"));
        lab.Allow("me", "friend");
        Assert.False(lab.Live(laptop));
        Assert.Null(lab.Service.Attestation("laptop"));
        Assert.Equal("laptop", Assert.Single(lab.Service.Snapshot().Removed).DeviceId);

        // As a friend it signs in again with friend access only, never attested for the network.
        var shared = await lab.SignInAsync("me", "laptop");
        Assert.Equal(GatewayAccess.Friend, shared.Access);
        Assert.True(lab.Service.FriendAllowed(shared.CredentialId));
        Assert.False(lab.Service.FriendAllowed(laptop.CredentialId));
        Assert.Null(lab.Service.Attestation("laptop"));

        // Back to a member: the friend's credential goes, and no network removal is recorded for a friend's computer.
        lab.Allow("me", "member");
        Assert.False(lab.Live(shared));
        Assert.False(lab.Service.FriendAllowed(shared.CredentialId));
        Assert.Single(lab.Service.Snapshot().Removed);
        Assert.Null(lab.Service.Snapshot().Allowed.Single().Access);
    }

    [Fact]
    public async Task A_friend_removed_behind_the_service_loses_access_within_the_recheck_and_never_without_sign_in()
    {
        var lab = new ServiceLab();
        lab.Allow("friend-1", "friend");
        var friend = await lab.SignInAsync("friend-1", "friend-pc");
        Assert.True(lab.Service.FriendAllowed(friend.CredentialId));
        Assert.Contains(lab.Log, line => line.Contains("a friend: it may use this host's engines and nothing else", StringComparison.Ordinal));

        // martlet-host owner-signin-disallow edits signin.json while the service runs.
        var edited = GatewaySignInDocument.Parse(lab.Storage.Bytes!);
        GatewaySignInSettings.Apply(edited, new() { Action = "disallow", Provider = "idp", Subject = "friend-1" }, lab.Clock.GetUtcNow());
        lab.Storage.Bytes = edited.Write();
        Assert.True(lab.Service.FriendAllowed(friend.CredentialId));
        lab.Clock.Advance(GatewaySignInService.FriendRecheck);
        Assert.False(lab.Service.FriendAllowed(friend.CredentialId));
        Assert.False(lab.Live(friend));
        Assert.Empty(lab.Service.Snapshot().Removed);

        // A friend credential that sign-in never recorded (or with sign-in detached) never gets in.
        var stray = lab.Credentials.Issue("stray-pc", "STRAY", [GatewayRole.Voice], CancellationToken.None, GatewayAccess.Friend);
        Assert.False(lab.Service.FriendAllowed(stray.CredentialId));
        var detached = new GatewaySignInService(lab.Credentials, lab.Clock, new SystemGatewayCrypto(), (_, _) => { });
        Assert.False(detached.FriendAllowed(stray.CredentialId));
    }

    [Fact]
    public async Task Pictures_and_singing_stay_the_owners_while_a_friend_lists_and_uses_the_other_engines()
    {
        await using var pictures = new Martlet.Gateway.Pictures.PictureRelayWorker(new Uri("http://127.0.0.1:50086/"));
        await using var singing = new Martlet.Gateway.Singing.SongRelayWorker(new Uri("http://127.0.0.1:1/"));
        await using var thinking = new Martlet.Gateway.Ollama.OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), "gemma4:e4b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [pictures, singing, thinking]);
        var owner = await SetUpSignInAsync(host);
        var friend = await FriendAsync(host, owner);
        async Task<string[]> RoutesAsync(GatewayRequestSigner who)
        {
            using var response = await host.Client.SendAsync(host.SignedGet("/martlet/v1/capabilities", GatewayRole.Voice, who));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await ReadAsync(response)).GetProperty("routes").EnumerateArray().Select(r => r.GetProperty("route_id").GetString()!).ToArray();
        }

        Assert.Equal(new[] { pictures.Route.RouteId, singing.Route.RouteId, thinking.Route.RouteId }.Order(StringComparer.Ordinal),
            await RoutesAsync(owner));
        Assert.Equal([thinking.Route.RouteId], await RoutesAsync(friend));
        // Their queues, histories, results and models are the whole host's: a friend is refused before anything is read.
        foreach (var path in new[] { pictures.Route.Path, singing.Route.Path })
        {
            using var refused = await host.Client.SendAsync(host.SignedPost(path, GatewayRole.Voice, friend,
                Encoding.UTF8.GetBytes("""{"operation":"status"}""")));
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{path}: {refused.StatusCode}");
            Assert.Equal("access.friend", await GatewayTestHost.FailureCode(refused));
        }
        Assert.True(GatewayInferenceRouteRegistry.FriendsMayUse(thinking.Route));
        Assert.False(GatewayInferenceRouteRegistry.FriendsMayUse(pictures.Route));
        Assert.False(GatewayInferenceRouteRegistry.FriendsMayUse(singing.Route));
    }

    [Fact]
    public async Task A_friend_keeps_at_most_three_computers_and_friends_never_fill_the_credential_table()
    {
        var lab = new ServiceLab();
        lab.Allow("friend-1", "friend");
        var first = await lab.SignInAsync("friend-1", "pc-1");
        var second = await lab.SignInAsync("friend-1", "pc-2");
        var third = await lab.SignInAsync("friend-1", "pc-3");
        // A fourth computer replaces the friend's oldest: its credential is revoked, not just forgotten.
        var fourth = await lab.SignInAsync("friend-1", "pc-4");
        Assert.False(lab.Live(first));
        Assert.All(new[] { second, third, fourth }, credential => Assert.True(lab.Live(credential)));
        Assert.Equal(["pc-2", "pc-3", "pc-4"], lab.Service.Snapshot().Enrolled.Select(e => e.DeviceId).Order());
        Assert.Contains(lab.Log, line => line.StartsWith("Revoked pc-1, the oldest computer of", StringComparison.Ordinal));

        // A friend credential sign-in no longer records (signin.json replaced behind the service) frees its slot on the next
        // friend's sign-in.
        var stray = lab.Credentials.Issue("stray-pc", "STRAY", [GatewayRole.Voice], CancellationToken.None, GatewayAccess.Friend);
        lab.Allow("friend-2", "friend");
        await lab.SignInAsync("friend-2", "other-pc");
        Assert.False(lab.Live(stray));

        // All friends together: at most MaximumFriendCredentials computers, then signin.friends_full; the owner's own
        // identities still sign in.
        var count = lab.Credentials.ListRegistrations().Count(r => r.Access == GatewayAccess.Friend);
        for (var i = 3; count < GatewaySignInService.MaximumFriendCredentials; i++)
        {
            lab.Allow($"friend-{i}", "friend");
            for (var d = 0; d < GatewaySignInService.MaximumFriendDevices && count < GatewaySignInService.MaximumFriendCredentials; d++, count++)
                await lab.SignInAsync($"friend-{i}", $"friend-{i}-pc-{d}");
        }
        lab.Allow("friend-late", "friend");
        Assert.Equal("signin.friends_full", (await Assert.ThrowsAsync<GatewayProtocolException>(() =>
            lab.SignInAsync("friend-late", "late-pc"))).Failure.Code);
        lab.Allow("me", null);
        Assert.Equal(GatewayAccess.Full, (await lab.SignInAsync("me", "my-laptop")).Access);
        Assert.Equal(GatewaySignInService.MaximumFriendCredentials,
            lab.Credentials.ListRegistrations().Count(r => r.Access == GatewayAccess.Friend));
    }

    [Fact]
    public void The_allow_list_takes_member_or_friend_access_only()
    {
        var document = new GatewaySignInDocument();
        var now = DateTimeOffset.UnixEpoch;
        GatewaySignInSettings.Apply(document, new() { Action = "allow", Provider = "idp", Subject = "a", Access = "friend" }, now);
        GatewaySignInSettings.Apply(document, new() { Action = "allow", Provider = "idp", Subject = "b", Access = "member" }, now);
        GatewaySignInSettings.Apply(document, new() { Action = "allow", Provider = "idp", Subject = "c" }, now);
        Assert.Equal(["friend", null, null], document.Allowed.Select(a => a.Access));
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() => GatewaySignInSettings.Apply(document,
            new() { Action = "allow", Provider = "idp", Subject = "d", Access = "owner" }, now)).Failure.Code);
        // A friend counts as someone who can sign in, so a host shared only with friends can be reached from outside.
        document.Providers.Add(new() { Id = "idp", Kind = "oidc", Name = "IdP", Issuer = "https://idp.example", ClientId = "c" });
        document.Allowed.RemoveAll(a => a.Access is null);
        Assert.Null(GatewaySignInSettings.BlockedReason(document));
        // Older signin.json files (no access field) keep their identities as the owner's computers.
        var old = GatewaySignInDocument.Parse("""{"schema_version":1,"allowed":[{"provider":"idp","subject":"x"}]}"""u8.ToArray());
        Assert.Null(old.AccessOf("idp", "x"));
    }
}
