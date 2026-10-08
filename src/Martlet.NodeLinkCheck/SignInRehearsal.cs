using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses joining from outside home by signing in, end to end with the production code: a real gateway (Kestrel, pinned
/// TLS) on 127.0.0.1 with in-memory network.json and signin.json, a member desktop at home and a laptop "away" that only has
/// the owner's invite, and a tablet that signs in at an OpenID Connect issuer in this process through a simulated browser. The home PC founds the network, sets up the owner account with a real authenticator secret (codes
/// computed like an authenticator app) and makes an invite whose outside address is "localhost" (so the certificate's name
/// doesn't match and only the pin is trusted). The laptop pins the host from the invite, is refused with a wrong password,
/// a reused code and a forged pin, signs in with the right ones, asks to join and is let into the network by the home PC on
/// the host's sign-in attestation without a check number; a non-member can't change sign-in, and removing the owner
/// account takes the laptop's access away. The home PC also shares the host with a friend (an identity allowed as a friend):
/// the friend's computer reaches only the host's engines, never joins, can't sign in under another computer's ID and loses
/// access as soon as sharing stops. Nothing leaves loopback; nothing is written to disk or the credential vault.
/// </summary>
internal static class SignInRehearsal
{
    private const string Password = "rehearsal owner passphrase";

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<Step>();
        var started = DateTimeOffset.UtcNow;
        await using var host = await LabHost.StartAsync("lab-signin-host");
        await using var host2 = await LabHost.StartAsync("lab-signin-host-2");
        using var keyHome = NetworkKey.Create("lab-home-pc");
        using var keyLaptop = NetworkKey.Create("lab-laptop");
        using var keyOther = NetworkKey.Create("lab-other-pc");
        var home = new LabDesktop(keyHome, "HOME-PC");
        var laptop = new LabDesktop(keyLaptop, "LAPTOP");
        var other = new LabDesktop(keyOther, "OTHER-PC");

        async Task Run(string name, Func<Task<(bool Ok, string Detail)>> action)
        {
            try
            {
                var (ok, detail) = await action();
                steps.Add(new(name, ok, detail));
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                steps.Add(new(name, false, $"{error.GetType().Name}: {error.Message}"));
            }
        }

        await Run("The home PC pairs with two hosts by typed codes and founds a network that binds both", async () =>
        {
            await home.PairByCodeAsync(host, token);
            await home.SyncAsync(token);
            await home.PairByCodeAsync(host2, token);
            await home.SyncAsync(token);
            return (host.Server.NetworkState.State == "bound" && host2.Server.NetworkState.State == "bound",
                $"hosts {host.Server.NetworkState.State} and {host2.Server.NetworkState.State} in {host.Server.NetworkState.NetworkId}");
        });
        var secret = Totp.NewSecret();
        var setupCode = "";
        IReadOnlyList<string> recovery = [];
        await Run("The home PC sets up the owner account with an authenticator and gets ten recovery codes (no secret read back)", async () =>
        {
            var wrong = await FailureAsync(() => home.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "owner", ["user"] = "owner", ["password"] = Password, ["totp_secret"] = secret, ["code"] = "000000"
            }, token));
            var settings = await home.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "owner", ["user"] = "owner", ["password"] = Password, ["totp_secret"] = secret,
                ["code"] = setupCode = Totp.Code(secret, DateTimeOffset.UtcNow)
            }, token);
            recovery = settings.RecoveryCodes ?? [];
            var saved = System.Text.Encoding.UTF8.GetString(host.SignInBytes ?? []);
            return (wrong == "signin.invalid" && settings.OwnerUser == "owner" && recovery.Count == 10 && !saved.Contains(Password),
                $"wrong authenticator code: {wrong}; owner {settings.OwnerUser}; recovery codes {recovery.Count}; password kept only as a verifier: {!saved.Contains(Password)}");
        });
        await Run("A computer outside the network can't change sign-in", async () =>
        {
            await other.PairByCodeAsync(host, token);
            var refused = await FailureAsync(() => other.ChangeAsync(host.HostId, new JsonObject { ["action"] = "remove-owner" }, token));
            return (refused == "signin.denied", $"OTHER-PC: {refused}");
        });
        var outside = $"localhost:{new Uri(host.Origin).Port}";
        var invite = new NetworkInvite
        {
            HostId = host.HostId, SpkiFingerprint = host.Fingerprint, Origin = host.Origin, Addresses = [outside],
            NetworkId = host.Server.NetworkState.NetworkId, Label = "Lab home"
        };
        string? origin = null;
        await Run("The laptop pins the host from the invite (outside address by name, so only the pin is trusted) and lists the sign-ins", async () =>
        {
            var parsed = NetworkInvite.Parse(invite.Write());
            var (answered, providers) = await HostSignInClient.ReadProvidersAsync(parsed, token);
            origin = answered;
            var forged = parsed with { SpkiFingerprint = "sha256:" + new string('0', 64) };
            var refused = await FailureAsync(() => HostSignInClient.ReadProvidersAsync(forged, token));
            return (answered == "https://" + outside && providers.Any(p => p.Kind == "owner") && refused == "host.unreachable",
                $"answered at {answered}: {string.Join(", ", providers.Select(p => p.Id))}; with a forged pin: {refused}");
        });
        await Run("A wrong password and a reused authenticator code are refused", async () =>
        {
            var wrong = await FailureAsync(() => HostSignInClient.SignInAsOwnerAsync(invite, origin!, "owner", "not the passphrase at all",
                Totp.Code(secret, DateTimeOffset.UtcNow), keyLaptop.DeviceId, "LAPTOP", token));
            // The code used to set the account up (same 30-second step) works only once.
            var reused = await FailureAsync(() => HostSignInClient.SignInAsOwnerAsync(invite, origin!, "owner", Password,
                setupCode, keyLaptop.DeviceId, "LAPTOP", token));
            return (wrong == "signin.invalid" && reused is "signin.invalid", $"wrong password: {wrong}; reused code: {reused}");
        });
        await Run("The laptop signs in with the owner account and a recovery code and is paired (signed requests accepted)", async () =>
        {
            var (pairing, credential, who) = await HostSignInClient.SignInAsOwnerAsync(invite, origin!, "owner", Password, recovery[0],
                keyLaptop.DeviceId, "LAPTOP", token);
            laptop.Keep(pairing, credential);
            var works = await laptop.CanUseAsync(host.HostId, token);
            return (works && who.Provider == "owner" && pairing.Origin == host.Origin,
                $"signed in as {who}; pairing kept under {pairing.Origin}; signed request {(works ? "accepted" : "refused")}");
        });
        await Run("The laptop asks to join; the home PC sees the host's sign-in attestation and lets it in with no check number", async () =>
        {
            await laptop.SyncAsync(token);
            var seen = await home.SyncAsync(token);
            var join = seen.Joins.FirstOrDefault(j => j.DeviceId == keyLaptop.DeviceId);
            var approved = home.ApproveSignedIn(seen.Joins);
            await home.SyncAsync(token);
            await laptop.SyncAsync(token);
            var member = laptop.State.Roster?.Trusts(keyLaptop.DeviceId, keyLaptop.PublicKey) == true;
            return (join?.SignIn is { Provider: "owner" } && approved.Count == 1 && member,
                $"join attested as {join?.SignIn?.Provider}:{join?.SignIn?.Subject}; let in: {approved.Count}; laptop is a member: {member}");
        });
        await Run("OTHER-PC (paired by code, no sign-in) still waits for a check number", async () =>
        {
            await other.SyncAsync(token);
            var seen = await home.SyncAsync(token);
            var join = seen.Joins.FirstOrDefault(j => j.DeviceId == keyOther.DeviceId);
            var approved = home.ApproveSignedIn(seen.Joins);
            return (join is { SignIn: null } && approved.Count == 0, $"OTHER-PC join attested: {join?.SignIn is not null}; let in: {approved.Count}");
        });
        using var issuer = new LabIssuer();
        host.Server.UseSignInProviderHandler(issuer);
        using var keyTablet = NetworkKey.Create("lab-tablet");
        var tablet = new LabDesktop(keyTablet, "TABLET");
        await Run("The home PC adds an OpenID Connect provider (client secret kept on the host, never read back)", async () =>
        {
            var settings = await home.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "provider", ["provider_config"] = new JsonObject
                {
                    ["id"] = "authentik", ["kind"] = "oidc", ["name"] = "Authentik", ["issuer"] = LabIssuer.Issuer,
                    ["client_id"] = LabIssuer.ClientId, ["client_secret"] = LabIssuer.ClientSecret
                }
            }, token);
            var provider = settings.Providers.Single();
            var listed = (await HostSignInClient.ReadProvidersAsync(invite, token)).Providers;
            return (provider.HasClientSecret && listed.Any(p => p.Id == "authentik" && p.InBrowser),
                $"provider {provider.Id} ({provider.Kind}), secret kept: {provider.HasClientSecret}; offered: {string.Join(", ", listed.Select(p => p.Id))}");
        });
        await Run("A tablet signs in in the (simulated) browser: refused until the home PC allows the identity it saw, then paired " +
            "and let into the network on the attestation", async () =>
        {
            var refused = await FailureAsync(() => HostSignInClient.SignInInBrowserAsync(invite, origin!, "authentik", keyTablet.DeviceId, "TABLET",
                issuer.Browse, TimeSpan.FromSeconds(30), token));
            var waiting = (await home.ReadAsync(host.HostId, token)).Refused.FirstOrDefault();
            if (waiting is null) return (false, $"first try: {refused}; nothing waiting to be allowed");
            await home.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "allow", ["provider"] = waiting.Provider, ["subject"] = waiting.Subject, ["label"] = waiting.Label
            }, token);
            var (pairing, secret, who) = await HostSignInClient.SignInInBrowserAsync(invite, origin!, "authentik", keyTablet.DeviceId, "TABLET",
                issuer.Browse, TimeSpan.FromSeconds(30), token);
            tablet.Keep(pairing, secret);
            await tablet.SyncAsync(token);
            var approved = home.ApproveSignedIn((await home.SyncAsync(token)).Joins);
            await home.SyncAsync(token);
            await tablet.SyncAsync(token);
            var member = tablet.State.Roster?.Trusts(keyTablet.DeviceId, keyTablet.PublicKey) == true;
            return (refused == "signin.not_allowed" && who.Label == "me@example.net" && approved.Count == 1 && member && issuer.SecretSeen,
                $"first try: {refused}; allowed {waiting.Label} ({waiting.Provider}:{waiting.Subject}); signed in as {who}; client secret used by the host: " +
                $"{issuer.SecretSeen}; let in: {approved.Count}; member: {member}");
        });
        await Run("Removing the tablet's identity on the host removes the tablet from the network: the home PC signs the removal on " +
            "its next sync, so the other host revokes it too", async () =>
        {
            await tablet.SyncAsync(token);
            var before = await tablet.CanUseAsync(host2.HostId, token);
            var settings = await home.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "disallow", ["provider"] = "authentik", ["subject"] = "lab-user-42"
            }, token);
            var pending = settings.RemovedFromNetwork.Any(r => r.DeviceId == keyTablet.DeviceId);
            var result = await home.SyncAsync(token);
            var removed = home.State.Roster?.Desktop(keyTablet.DeviceId) is { Removed: true };
            var onHost2 = await tablet.FailureCodeAsync(host2.HostId, token);
            var left = await tablet.SyncAsync(token);
            var cleared = (await home.ReadAsync(host.HostId, token)).RemovedFromNetwork.Count == 0;
            return (before && pending && removed && onHost2 is "auth.revoked" or "auth.invalid" && tablet.State.RemovedFrom is not null && cleared,
                $"tablet used lab-signin-host-2 before: {before}; host listed it for removal: {pending}; removed in the roster by the home PC: " +
                $"{removed} ({result.Events.FirstOrDefault(e => e.StartsWith("Removed", StringComparison.Ordinal))}); tablet on lab-signin-host-2: " +
                $"{onHost2}; tablet left the network: {tablet.State.RemovedFrom is not null}; host's removal list cleared: {cleared}");
        });
        await Run("A Steam account allowed at home by its SteamID64 signs in (OpenID 2.0 assertion confirmed with Steam by the host)", async () =>
        {
            await home.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "provider", ["provider_config"] = new JsonObject { ["id"] = "steam", ["kind"] = "steam", ["name"] = "Steam" }
            }, token);
            await home.ChangeAsync(host.HostId, new JsonObject { ["action"] = "allow", ["provider"] = "steam", ["subject"] = LabIssuer.SteamId }, token);
            using var keyGaming = NetworkKey.Create("lab-gaming-pc");
            var (_, _, who) = await HostSignInClient.SignInInBrowserAsync(invite, origin!, "steam", keyGaming.DeviceId, "GAMING-PC",
                issuer.Browse, TimeSpan.FromSeconds(30), token);
            return (who.Subject == LabIssuer.SteamId && issuer.SteamChecks == 1, $"signed in as {who}; assertions confirmed with Steam: {issuer.SteamChecks}");
        });
        using var keyFriend = NetworkKey.Create("lab-friend-pc");
        var friend = new LabDesktop(keyFriend, "FRIEND-PC");
        await Run("The home PC shares the host with a friend: the OpenID Connect identity is allowed as a friend (this host's engines only)", async () =>
        {
            var settings = await home.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "allow", ["provider"] = "authentik", ["subject"] = "lab-user-42", ["label"] = "Ana", ["access"] = "friend"
            }, token);
            var saved = System.Text.Encoding.UTF8.GetString(host.SignInBytes ?? []);
            return (settings.Allowed.Any(a => a.Subject == "lab-user-42") && saved.Contains("\"access\": \"friend\"", StringComparison.Ordinal),
                $"allowed: {string.Join(", ", settings.Allowed.Select(a => a.Label ?? a.Subject))}; kept as a friend in signin.json: " +
                saved.Contains("\"access\": \"friend\"", StringComparison.Ordinal));
        });
        await Run("The friend's computer signs in in the (simulated) browser and gets a friend's credential: it lists the host's engines", async () =>
        {
            var (pairing, secret, who) = await HostSignInClient.SignInInBrowserAsync(invite, origin!, "authentik", keyFriend.DeviceId, "FRIEND-PC",
                issuer.Browse, TimeSpan.FromSeconds(30), token);
            friend.Keep(pairing, secret);
            var engines = await friend.CanUseAsync(host.HostId, token);
            var access = host.Server.Credentials.PairedDevices().FirstOrDefault(d => d.DeviceId == keyFriend.DeviceId)?.Access;
            return (engines && access == GatewayAccess.Friend && who.Label == "me@example.net",
                $"signed in as {who}; host keeps it as {access}; capabilities: {(engines ? "allowed" : "refused")}");
        });
        await Run("Everything else on the host refuses the friend (access.friend), and the friend never joins the network", async () =>
        {
            var codes = await friend.RefusalsAsync(host.HostId, token);
            await friend.SyncAsync(token);
            var seen = await home.SyncAsync(token);
            var asked = seen.Joins.Any(j => j.DeviceId == keyFriend.DeviceId);
            var member = home.State.Roster?.Desktop(keyFriend.DeviceId) is not null;
            var refused = codes.All(c => c.Code == "access.friend");
            return (refused && codes.Count >= 15 && !asked && !member,
                $"{string.Join("; ", codes.Select(c => $"{c.Route}: {c.Code}"))}; asked to join: {asked}; in the roster: {member}");
        });
        await Run("A sign-in can't take over another computer's pairing: the friend signing in as HOME-PC is refused (signin.device_taken)", async () =>
        {
            var refused = await FailureAsync(() => HostSignInClient.SignInInBrowserAsync(invite, origin!, "authentik", keyHome.DeviceId, "HOME-PC",
                issuer.Browse, TimeSpan.FromSeconds(30), token));
            var homeStill = await home.CanUseAsync(host.HostId, token);
            return (refused == "signin.device_taken" && homeStill, $"as HOME-PC: {refused}; HOME-PC still uses the host: {homeStill}");
        });
        await Run("Stopping sharing revokes the friend's computer at once, with no network removal (it never joined)", async () =>
        {
            var settings = await home.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "disallow", ["provider"] = "authentik", ["subject"] = "lab-user-42"
            }, token);
            var after = await friend.FailureCodeAsync(host.HostId, token);
            var recorded = settings.RemovedFromNetwork.Any(r => r.DeviceId == keyFriend.DeviceId);
            return (after is "auth.revoked" or "auth.invalid" && !recorded, $"friend now: {after}; listed for network removal: {recorded}");
        });
        await Run("Removing the owner account takes the laptop's access away", async () =>
        {
            var settings = await home.ChangeAsync(host.HostId, new JsonObject { ["action"] = "remove-owner" }, token);
            var revoked = await laptop.FailureCodeAsync(host.HostId, token);
            return (settings.OwnerUser is null && revoked is "auth.revoked" or "auth.invalid", $"owner account: {settings.OwnerUser ?? "none"}; laptop: {revoked}");
        });
        await Run("The host's security audit recorded the sign-ins (never a secret)", () =>
        {
            var events = host.Server.Guard.Recent().Where(e => e.RouteClass == "signin").ToArray();
            var text = string.Join(" ", events.Select(e => $"{e.Outcome}:{e.Code}:{e.Subject}"));
            return Task.FromResult((events.Any(e => e.Outcome == "success") && events.Any(e => e.Outcome == "failure") && !text.Contains(Password),
                text));
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "One real gateway on 127.0.0.1 (Kestrel, pinned TLS, in-memory signin.json and network.json), simulated desktops using " +
                "the desktop's sign-in client and network sync engine, authenticator codes computed from the secret, an OpenID Connect issuer in this process and a simulated " +
                "browser that follows the redirect to the desktop's real loopback listener. Not covered: the desktop windows, Windows " +
                "Credential Manager, a host reached over the internet, a real browser and a real issuer (Authentik, Google, ...).",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    private static async Task<string?> FailureAsync<T>(Func<Task<T>> action)
    {
        try { await action(); return null; }
        catch (Audio2FaceHostException error) { return error.Code; }
    }

    private sealed record Step(string Name, bool Ok, string Detail);

/// <summary>An OpenID Connect issuer in this process (discovery, an RSA key set, a token endpoint that signs ID tokens) and a
    /// simulated browser: <see cref="Browse"/> "signs in" at the authorize URL and follows the redirect to the computer's real
    /// loopback listener.</summary>
    internal sealed class LabIssuer : HttpMessageHandler
    {
        internal const string Issuer = "https://idp.lab.invalid";
        internal const string ClientId = "martlet-lab";
        internal const string ClientSecret = "lab-client-secret";
        private readonly RSA key = RSA.Create(2048);
        private readonly Dictionary<string, string> nonces = new(StringComparer.Ordinal);
        internal const string SteamId = "76561198000000042";
        internal bool SecretSeen;
        internal int SteamChecks;

        internal void Browse(string url)
        {
            var uri = new Uri(url);
            var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            if (uri.Host == "steamcommunity.com")
            {
                // Steam's positive assertion, sent back to return_to (which carries the attempt's state).
                var claimed = "https://steamcommunity.com/openid/id/" + SteamId;
                var assertion = new Dictionary<string, string>
                {
                    ["openid.ns"] = "http://specs.openid.net/auth/2.0", ["openid.mode"] = "id_res",
                    ["openid.op_endpoint"] = "https://steamcommunity.com/openid/login", ["openid.claimed_id"] = claimed,
                    ["openid.identity"] = claimed, ["openid.return_to"] = query["openid.return_to"],
                    ["openid.response_nonce"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "lab",
                    ["openid.assoc_handle"] = "1234567890", ["openid.signed"] = "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
                    ["openid.sig"] = "bGFi"
                };
                var target = query["openid.return_to"] + "&" + string.Join("&", assertion.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
                _ = Task.Run(async () =>
                {
                    using var steamBrowser = new HttpClient();
                    using var _ = await steamBrowser.GetAsync(target);
                });
                return;
            }
            var code = Base64Url(RandomNumberGenerator.GetBytes(16));
            lock (nonces) nonces[code] = query["nonce"];
            var back = query["redirect_uri"] + "?code=" + code + "&state=" + Uri.EscapeDataString(query["state"]);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                using var _ = await browser.GetAsync(back);
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object? body = null;
            if (path == "/.well-known/openid-configuration")
                body = new { issuer = Issuer, authorization_endpoint = Issuer + "/authorize", token_endpoint = Issuer + "/token", jwks_uri = Issuer + "/jwks" };
            else if (path == "/jwks")
            {
                var p = key.ExportParameters(false);
                body = new { keys = new[] { new { kty = "RSA", kid = "lab", n = Base64Url(p.Modulus!), e = Base64Url(p.Exponent!) } } };
            }
            else if (request.RequestUri.Host == "steamcommunity.com" && request.Method == HttpMethod.Post)
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Interlocked.Increment(ref SteamChecks);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ns:http://specs.openid.net/auth/2.0\nis_valid:" + (form.Contains("openid.mode=check_authentication") ? "true" : "false") + "\n")
                };
            }
            else if (path == "/token")
            {
                var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                SecretSeen |= request.Headers.Authorization?.Parameter is { } basic &&
                    System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(basic)) == ClientId + ":" + ClientSecret;
                string? nonce;
                lock (nonces) nonces.Remove(form["code"], out nonce);
                if (nonce is null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var header = Base64Url(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "lab" }));
                var claims = Base64Url(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                {
                    iss = Issuer, aud = ClientId, sub = "lab-user-42", email = "me@example.net", email_verified = true, iat = now, exp = now + 300, nonce
                }));
                var signature = key.SignData(System.Text.Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                body = new { access_token = "lab", token_type = "Bearer", id_token = header + "." + claims + "." + Base64Url(signature) };
            }
            return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
            };
        }

        private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

        protected override void Dispose(bool disposing)
        {
            if (disposing) key.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class LabDesktop(NetworkKey key, string name)
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly NetworkSyncEngine engine = new(key, name);
        internal NetworkLocalState State { get; private set; } = NetworkLocalState.Empty;

        internal async Task PairByCodeAsync(LabHost host, CancellationToken token)
        {
            var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, card.Code.Reveal(), key.DeviceId, name, token);
            Keep(pairing, secret);
        }

        internal void Keep(Audio2FaceHostPairing pairing, string secret)
        {
            pairings[pairing.HostId] = (pairing, secret);
            State = State.WithAdopted(pairing.HostId);
        }

        internal async Task<NetworkSyncResult> SyncAsync(CancellationToken token)
        {
            var result = await engine.SyncAsync(State, pairings.Values.Select(p => p.Pairing).ToArray(),
                pairing => new Audio2FaceHostConnection(pairing, pairings[pairing.HostId].Secret), token);
            foreach (var (pairing, secret) in result.Paired) pairings[pairing.HostId] = (pairing, secret);
            foreach (var id in result.Forget) pairings.Remove(id);
            State = NetworkLocalState.Parse(result.State.Write());
            return result;
        }

        internal IReadOnlyList<HostJoinRequest> ApproveSignedIn(IReadOnlyList<HostJoinRequest> joins)
        {
            var (state, approved) = engine.ApproveSignedIn(State, joins);
            State = state;
            return approved;
        }

        internal async Task<HostSignInSettings> ChangeAsync(string hostId, JsonObject change, CancellationToken token)
        {
            using var connection = new Audio2FaceHostConnection(pairings[hostId].Pairing, pairings[hostId].Secret);
            return await connection.ChangeSignInSettingsAsync(change, token);
        }

        internal async Task<HostSignInSettings> ReadAsync(string hostId, CancellationToken token)
        {
            using var connection = new Audio2FaceHostConnection(pairings[hostId].Pairing, pairings[hostId].Secret);
            return await connection.ReadSignInSettingsAsync(token);
        }

        internal async Task<bool> CanUseAsync(string hostId, CancellationToken token) => await FailureCodeAsync(hostId, token) is null;

        /// <summary>What the host answers this computer on each route that isn't an engine (null when it was allowed).</summary>
        internal async Task<IReadOnlyList<(string Route, string? Code)>> RefusalsAsync(string hostId, CancellationToken token)
        {
            var (pairing, secret) = pairings[hostId];
            using var c = new Audio2FaceHostConnection(pairing, secret);
            (string, Func<Task<object?>>)[] calls =
            [
                ("network", async () => await c.ReadNetworkAsync(token)),
                ("machine", async () => await c.ReadMachineReportAsync(token)),
                ("cluster", async () => await c.ReadClusterAsync(token)),
                ("settings", async () => await c.ReadSettingsAsync(token)),
                ("memories", async () => await c.ReadMemoriesAsync(token)),
                ("voices", async () => await c.ReadVoicesAsync(token)),
                ("speaking-voices", async () => await c.ReadSpeakingVoicesAsync(token)),
                ("character-models", async () => await c.ReadCharacterModelsAsync(token)),
                ("creations", async () => await c.ReadCreationsAsync(token)),
                ("home-assistant", async () => await c.ReadHomeAssistantAsync(token)),
                ("api-keys", async () => await c.ReadApiKeysAsync(token)),
                ("commands", async () => await c.ReadCommandsAsync(token)),
                ("priority", async () => await c.ReadPriorityAsync(token)),
                ("security audit", async () => await c.ReadSecurityAuditAsync(token)),
                ("sign-in settings", async () => await c.ReadSignInSettingsAsync(token))
            ];
            var codes = new List<(string, string?)>();
            foreach (var (route, call) in calls) codes.Add((route, await FailureAsync(call)));
            return codes;
        }

        internal async Task<string?> FailureCodeAsync(string hostId, CancellationToken token)
        {
            if (!pairings.TryGetValue(hostId, out var pairing)) return "not-paired";
            return await FailureAsync(async () =>
            {
                using var connection = new Audio2FaceHostConnection(pairing.Pairing, pairing.Secret);
                return await connection.ReadRoutesAsync(token);
            });
        }
    }

    internal sealed class LabHost : IAsyncDisposable, IGatewayNetworkStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private readonly SignInStore signIn = new();
        internal GatewayServer Server { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal string Fingerprint { get; private set; } = "";
        internal byte[]? SignInBytes => signIn.Bytes;

        internal static async Task<LabHost> StartAsync(string hostId)
        {
            var host = new LabHost { HostId = hostId };
            try
            {
                host.certificate = Certificate();
                host.Origin = $"https://127.0.0.1:{FreePort()}";
                var origin = new GatewayOrigin(host.Origin);
                var identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
                host.Fingerprint = identity.SpkiFingerprint;
                host.Server = new GatewayServer(identity, origin, [], host);
                host.Server.AttachNetworkStorage(host);
                host.Server.AttachSignInStorage(host.signIn);
                host.listener = await host.Server.StartAsync(new GatewayTlsBinding(origin, identity, host.certificate, null), new KestrelGatewayListenerFactory());
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public byte[]? Load() => null;
        public void Save(byte[] bytes) { }
        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            certificate?.Dispose();
        }

        private sealed class SignInStore : IGatewaySignInStorage
        {
            internal byte[]? Bytes;
            public byte[]? Load() => Bytes;
            public void Save(byte[] bytes) => Bytes = bytes;
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        private static X509Certificate2 Certificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Martlet sign-in rehearsal", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var now = DateTimeOffset.UtcNow;
            using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pfx = ephemeral.Export(X509ContentType.Pkcs12, password);
            try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
}
