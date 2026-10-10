using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Diagnostics;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// A live sign-in lab for driving the desktop's Sign-in from outside window through Martlet MCP (signin_lab): a real
/// gateway on 127.0.0.1 with an owner account, an OpenID Connect provider (an issuer in this process) and an allowed
/// identity, already paired with the desktop that runs on <c>dataDirectory</c> (hosts.json written there, the pairing secret
/// in the lab credential folder named by MARTLET_LAB_CREDENTIALS, never Windows Credential Manager). A simulated laptop signs
/// in with that identity and keeps syncing the network, so the desktop lets it in and, once the owner removes the identity
/// in the window, removes it from the network again. The lab writes what it sees to signin-lab.json in the data directory
/// and stops when its standard input closes, signin-lab.stop appears there, or after 20 minutes.
/// </summary>
internal static class SignInLab
{
    internal const string StatusFile = "signin-lab.json";
    internal const string StopFile = "signin-lab.stop";
    internal const string DesktopDevice = "lab-desktop";

    internal static async Task<int> RunAsync(string dataDirectory, bool shareWithFriend = false)
    {
        if (!Path.IsPathFullyQualified(dataDirectory) || !Directory.Exists(dataDirectory))
            return Fail("The data directory must be an existing absolute folder (a disposable one).");
        if (LabCredentialNative.FromEnvironment() is null)
            return Fail($"Set {LabCredentialNative.Variable} to an existing folder (Invoke-MartletMcp.ps1 -LabCredentials); the lab never writes Windows Credential Manager.");
        if (File.Exists(Path.Combine(dataDirectory, "hosts.json")))
            return Fail("hosts.json already exists there; use a fresh disposable data directory.");

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        _ = Task.Run(() =>
        {
            try { while (Console.In.ReadLine() is not null) { } }
            catch (IOException) { }
            lifetime.Cancel();
        });
        var token = lifetime.Token;
        await using var host = await SignInRehearsal.LabHost.StartAsync("lab-signin-host");
        using var issuer = new SignInRehearsal.LabIssuer();
        host.Server.UseSignInProviderHandler(issuer);
        // An outside address set on the host itself: the desktop signs it into the roster and keeps it with its pairing.
        host.Server.Guard.Exposure = new() { OutsideAddresses = ["lab-home.example.net:9443"], OutsideAddressesSetAt = DateTimeOffset.UtcNow };

        using var adminKey = NetworkKey.Create("lab-admin");
        var admin = new SignInRehearsal.LabDesktop(adminKey, "LAB-ADMIN");
        await admin.PairByCodeAsync(host, token);
        var secret = Totp.NewSecret();
        await admin.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "owner", ["user"] = "owner", ["password"] = "lab owner passphrase", ["totp_secret"] = secret,
            ["code"] = Totp.Code(secret, DateTimeOffset.UtcNow)
        }, token);
        await admin.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "provider", ["provider_config"] = new JsonObject
            {
                ["id"] = "authentik", ["kind"] = "oidc", ["name"] = "Authentik (lab)", ["issuer"] = SignInRehearsal.LabIssuer.Issuer,
                ["client_id"] = SignInRehearsal.LabIssuer.ClientId, ["client_secret"] = SignInRehearsal.LabIssuer.ClientSecret
            }
        }, token);
        await admin.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "allow", ["provider"] = "authentik", ["subject"] = "lab-user-42", ["label"] = "me@example.net"
        }, token);
        // For a session without the desktop's window: the host is shared with the friend from the start (the lab's own admin
        // desktop allows the friend's identity as a friend before the desktop binds the host to its network).
        if (shareWithFriend)
            await admin.ChangeAsync(host.HostId, new JsonObject
            {
                ["action"] = "allow", ["provider"] = "authentik", ["subject"] = FriendSubject, ["label"] = FriendEmail, ["access"] = "friend"
            }, token);

        // The desktop on the data directory: paired by a code, its secret in the lab folder, the host in its hosts.json.
        var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
        var (pairing, pairingSecret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, card.Code.Reveal(), DesktopDevice, "LAB-DESKTOP", token);
        using (var lease = new SecretLease(pairingSecret))
        {
            var stored = new WindowsCredentialStore().WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
            if (stored != CredentialError.None) return Fail("Couldn't keep the lab pairing secret: " + stored);
        }
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "hosts.json"), JsonSerializer.Serialize(new
        {
            version = 1,
            hosts = new[]
            {
                new
                {
                    pairing = new
                    {
                        origin = pairing.Origin, hostId = pairing.HostId, spkiFingerprint = pairing.SpkiFingerprint, deviceId = pairing.DeviceId,
                        credentialId = pairing.CredentialId
                    },
                    method = "Agent"
                }
            }
        }), token);

        // The laptop away from home signs in with the allowed identity.
        using var laptopKey = NetworkKey.Create("lab-laptop");
        var laptop = new SignInRehearsal.LabDesktop(laptopKey, "LAB-LAPTOP");
        var invite = new NetworkInvite { HostId = host.HostId, SpkiFingerprint = host.Fingerprint, Origin = host.Origin, Label = "Lab home" };
        var (laptopPairing, laptopSecret, who) = await HostSignInClient.SignInInBrowserAsync(invite, host.Origin, "authentik", laptopKey.DeviceId,
            "LAB-LAPTOP", issuer.Browse, TimeSpan.FromSeconds(30), token);
        laptop.Keep(laptopPairing, laptopSecret);

        // A friend's computer (another identity at the same provider) signs in once: the host refuses it and lists it under
        // "Signed in but not allowed yet", where the owner can allow it as a friend in the window (or Devices › Friends).
        using var friendKey = NetworkKey.Create("lab-friend-pc");
        var friend = new SignInRehearsal.LabDesktop(friendKey, "LAB-FRIEND");
        var friendState = "not signed in";
        HostSignInIdentity? friendWho = null;
        IReadOnlyList<(string Route, string? Code)> friendRefusals = [];
        async Task FriendSignInAsync()
        {
            try
            {
                var (pairing, secret, signedIn) = await HostSignInClient.SignInInBrowserAsync(invite, host.Origin, "authentik", friendKey.DeviceId,
                    "LAB-FRIEND", url => issuer.Browse(url, FriendSubject, FriendEmail), TimeSpan.FromSeconds(30), token);
                friend.Keep(pairing, secret);
                friendWho = signedIn;
                friendState = "signed in";
                friendRefusals = await friend.RefusalsAsync(host.HostId, token);
            }
            catch (Audio2FaceHostException error) { friendState = "refused: " + error.Code; }
        }
        await FriendSignInAsync();

        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, hostId = host.HostId, laptop = laptopKey.DeviceId, signedInAs = who.ToString(),
            friend = friendKey.DeviceId, friendIdentity = $"{FriendEmail} (authentik: {FriendSubject})", friendState }));
        var events = new List<string>();
        var everMember = false;
        while (!token.IsCancellationRequested && !File.Exists(Path.Combine(dataDirectory, StopFile)))
        {
            // The laptop syncs only once the desktop has bound the host to its network (it would found its own otherwise).
            if (host.Server.NetworkState.State == "bound" && laptop.State.RemovedFrom is null)
            {
                try
                {
                    var result = await laptop.SyncAsync(token);
                    events.AddRange(result.Events.Select(e => "laptop: " + e));
                }
                catch (Exception error) when (error is not OperationCanceledException) { events.Add("laptop sync failed: " + error.Message); }
            }
            var member = laptop.State.Roster?.Trusts(laptopKey.DeviceId, laptopKey.PublicKey) == true;
            everMember |= member;
            var canUse = await laptop.CanUseAsync(host.HostId, token);
            // Once the owner allowed the friend's identity as a friend (signin.json), the friend's computer signs in again, once.
            var friendAllowed = AllowedAccess(host.SignInBytes, FriendSubject);
            if (friendWho is null && friendAllowed == "friend" && friendState.StartsWith("refused", StringComparison.Ordinal))
            {
                await FriendSignInAsync();
                events.Add($"friend: signed in again after the owner allowed {FriendEmail} ({friendState})");
            }
            var friendFailure = friendWho is null ? null : await friend.FailureCodeAsync(host.HostId, token);
            var status = new
            {
                ready = true, hostId = host.HostId, origin = host.Origin, network = host.Server.NetworkState.State,
                networkId = host.Server.NetworkState.NetworkId, laptop = laptopKey.DeviceId, signedInAs = who.ToString(),
                laptopMember = member, laptopWasMember = everMember, laptopRemoved = laptop.State.RemovedFrom is not null,
                laptopCanUseHost = canUse, waiting = laptop.State.Waiting?.CheckNumber,
                friend = new
                {
                    device = friendKey.DeviceId, identity = $"{FriendEmail} (authentik: {FriendSubject})", allowedAs = friendAllowed, state = friendState,
                    access = friendWho?.Access,
                    hostKeepsAs = host.Server.Credentials.PairedDevices().FirstOrDefault(d => d.DeviceId == friendKey.DeviceId)?.Access.ToString(),
                    canUseEngines = friendWho is not null && friendFailure is null, failure = friendFailure,
                    refusedElsewhere = friendRefusals.Count(r => r.Code == "access.friend"), otherwiseAnswered = friendRefusals.Count(r => r.Code != "access.friend"),
                    inRoster = laptop.State.Roster?.Desktop(friendKey.DeviceId) is not null
                },
                events = events.TakeLast(12).ToArray(), at = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, StatusFile), JsonSerializer.Serialize(status), CancellationToken.None);
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    internal const string FriendSubject = "lab-friend-7";
    internal const string FriendEmail = "ana@example.net";

    /// <summary>The access signin.json gives an allowed identity ("member" or "friend"), or null when it isn't allowed.</summary>
    private static string? AllowedAccess(byte[]? signIn, string subject)
    {
        if (signIn is null) return null;
        try
        {
            using var document = JsonDocument.Parse(signIn);
            if (!document.RootElement.TryGetProperty("allowed", out var allowed) || allowed.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in allowed.EnumerateArray())
                if (entry.TryGetProperty("subject", out var s) && s.GetString() == subject)
                    return entry.TryGetProperty("access", out var access) && access.GetString() == "friend" ? "friend" : "member";
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static int Fail(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ready = false, error = message }));
        return 1;
    }

    /// <summary>
    /// The lab with the desktop on <paramref name="dataDirectory"/> as a friend: a real gateway on 127.0.0.1 that belongs to a
    /// simulated owner (it paired, started its own Martlet network with the host, set up the lab OpenID Connect provider and allowed
    /// the lab identity as a friend), with a fixture Ollama Thinking route. The status carries the invite to paste in Join with an
    /// invite; the lab's simulated browser answers the desktop's sign-in through the folder named by MARTLET_LAB_BROWSER (the
    /// desktop writes the sign-in page's address there in a lab run). The status says whether the desktop signed in as a friend,
    /// whether it ever asked to join the owner's network, what the host refused it (access.friend must stay 0 in normal use) and
    /// how many Thinking requests reached the host.
    /// </summary>
    internal static async Task<int> RunFriendAsync(string dataDirectory, bool signInDesktop = false)
    {
        if (!Path.IsPathFullyQualified(dataDirectory) || !Directory.Exists(dataDirectory))
            return Fail("The data directory must be an existing absolute folder (a disposable one).");
        if (LabCredentialNative.FromEnvironment() is null)
            return Fail($"Set {LabCredentialNative.Variable} to an existing folder (Invoke-MartletMcp.ps1 -LabCredentials); the lab never writes Windows Credential Manager.");
        if (File.Exists(Path.Combine(dataDirectory, "hosts.json")))
            return Fail("hosts.json already exists there; use a fresh disposable data directory.");
        if (Environment.GetEnvironmentVariable(BrowserVariable) is not { Length: > 0 } browserFolder || !Path.IsPathFullyQualified(browserFolder))
            return Fail($"Set {BrowserVariable} to a folder (Invoke-MartletMcp.ps1 -LabCredentials sets it for the desktop and the MCP server) so the lab's simulated browser can answer the desktop's sign-in.");
        Directory.CreateDirectory(browserFolder);

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        _ = Task.Run(() =>
        {
            try { while (Console.In.ReadLine() is not null) { } }
            catch (IOException) { }
            lifetime.Cancel();
        });
        var token = lifetime.Token;
        var ollama = new ApiRehearsal.FixtureOllama();
        await using var worker = new Martlet.Gateway.Ollama.OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), "fixture-model:1b", handler: ollama);
        await using var host = await SignInRehearsal.LabHost.StartAsync("lab-shared-host", [worker]);
        using var issuer = new SignInRehearsal.LabIssuer();
        host.Server.UseSignInProviderHandler(issuer);

        // The host's owner: pairs, starts their own Martlet network with it, sets up the provider and allows the friend.
        using var ownerKey = NetworkKey.Create("lab-owner");
        var owner = new SignInRehearsal.LabDesktop(ownerKey, "LAB-OWNER");
        await owner.PairByCodeAsync(host, token);
        await owner.SyncAsync(token);
        await owner.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "provider", ["provider_config"] = new JsonObject
            {
                ["id"] = "authentik", ["kind"] = "oidc", ["name"] = "Authentik (lab)", ["issuer"] = SignInRehearsal.LabIssuer.Issuer,
                ["client_id"] = SignInRehearsal.LabIssuer.ClientId, ["client_secret"] = SignInRehearsal.LabIssuer.ClientSecret
            }
        }, token);
        await owner.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "allow", ["provider"] = "authentik", ["subject"] = "lab-user-42", ["label"] = "me@example.net", ["access"] = "friend"
        }, token);

        var invite = new NetworkInvite { HostId = host.HostId, SpkiFingerprint = host.Fingerprint, Origin = host.Origin, Label = "Ana's Martlet" }.Write();
        string? desktopDevice = null;
        if (signInDesktop)
        {
            // Without the desktop's window (a locked or headless session): the lab signs the desktop in as the friend itself,
            // under the device ID that desktop names itself by, through the same sign-in client Join with an invite uses, and keeps
            // the pairing as that window does: the secret in the lab credential folder, the host in hosts.json with access friend.
            desktopDevice = LocalLogs.ThisDeviceId();
            var parsed = NetworkInvite.Parse(invite);
            var (origin, _) = await HostSignInClient.ReadProvidersAsync(parsed, token);
            var (pairing, pairingSecret, who) = await HostSignInClient.SignInInBrowserAsync(parsed, origin, "authentik", desktopDevice,
                Environment.MachineName, issuer.Browse, TimeSpan.FromSeconds(30), token);
            if (!who.Friend) return Fail($"The host answered {who.Access} for the friend's sign-in.");
            using (var lease = new SecretLease(pairingSecret))
            {
                var stored = new WindowsCredentialStore().WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
                if (stored != CredentialError.None) return Fail("Couldn't keep the lab pairing secret: " + stored);
            }
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "hosts.json"), JsonSerializer.Serialize(new
            {
                version = 1,
                hosts = new[]
                {
                    new
                    {
                        pairing = new
                        {
                            origin = pairing.Origin, hostId = pairing.HostId, spkiFingerprint = pairing.SpkiFingerprint, deviceId = pairing.DeviceId,
                            credentialId = pairing.CredentialId
                        },
                        method = "Agent", access = "friend", signedInAs = who.ToString()
                    }
                }
            }), token);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, mode = "friend", hostId = host.HostId, invite, desktop = desktopDevice }));
        var events = new List<string>();
        var browsePage = Path.Combine(browserFolder, BrowserFile);
        var asked = false;
        var nextStatus = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested && !File.Exists(Path.Combine(dataDirectory, StopFile)))
        {
            if (File.Exists(browsePage))
            {
                try
                {
                    var url = (await File.ReadAllTextAsync(browsePage, token)).Trim();
                    File.Delete(browsePage);
                    issuer.Browse(url);
                    events.Add("browser: signed in at the lab provider as me@example.net and went back to the desktop");
                }
                catch (IOException) { }
            }
            if (DateTimeOffset.UtcNow >= nextStatus)
            {
                nextStatus = DateTimeOffset.UtcNow.AddSeconds(2);
                // The owner's computer syncs its network: a friend never asks to join it and never shows in its roster.
                try
                {
                    var synced = await owner.SyncAsync(token);
                    asked |= synced.Joins.Any(j => j.DeviceId != ownerKey.DeviceId);
                }
                catch (Exception error) when (error is not OperationCanceledException) { events.Add("owner sync failed: " + error.Message); }
                var friends = host.Server.Credentials.PairedDevices().Where(d => d.Access == GatewayAccess.Friend).ToArray();
                var status = new
                {
                    ready = true, mode = "friend", hostId = host.HostId, origin = host.Origin, invite, network = host.Server.NetworkState.State,
                    ownersNetwork = host.Server.NetworkState.NetworkId,
                    desktopSignedInAsFriend = friends.Length > 0,
                    friendComputers = friends.Select(d => new { device = d.DeviceId, name = d.DisplayName, lastSeen = d.LastSeen }).ToArray(),
                    ownersRoster = owner.State.Roster?.ActiveDesktops.Select(d => d.Id).ToArray() ?? [],
                    askedToJoin = asked,
                    refusals = host.Codes.Where(c => c.Key.StartsWith("access.", StringComparison.Ordinal) || c.Key.StartsWith("auth.", StringComparison.Ordinal) ||
                        c.Key.StartsWith("signin.", StringComparison.Ordinal) || c.Key.StartsWith("network.", StringComparison.Ordinal))
                        .ToDictionary(c => c.Key, c => c.Value),
                    accessFriendRefusals = host.Codes.GetValueOrDefault("access.friend"),
                    thinkingRequests = ollama.Chats,
                    events = events.TakeLast(12).ToArray(), at = DateTimeOffset.UtcNow
                };
                await File.WriteAllTextAsync(Path.Combine(dataDirectory, StatusFile), JsonSerializer.Serialize(status), CancellationToken.None);
            }
            try { await Task.Delay(250, token); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    /// <summary>Where the desktop hands a browser sign-in page to the lab in a lab run (Martlet.Desktop's SignInBrowser).</summary>
    internal const string BrowserVariable = "MARTLET_LAB_BROWSER";
    internal const string BrowserFile = "signin-browser.url";

    internal const string AccountPassword = "lab account passphrase";
    internal static readonly Guid SamAccount = new("5a6e0000-0000-4000-8000-00000000005a");
    internal static readonly Guid AlexAccount = new("a1e40000-0000-4000-8000-0000000000a1");

    /// <summary>
    /// Household account sign-in (docs/ACCOUNTS.md), headless: a real gateway on 127.0.0.1 with an ECDSA P-256 TLS key like
    /// every Martlet host. Its owner (lab-owner) pairs, starts a Martlet network with it and sets up an owner login (with an
    /// authenticator, its account derived from the network), Sam's login (password and authenticator), Alex's (password only),
    /// the lab OpenID Connect provider with lab-user-42 linked to Sam and lab-friend-7 as a friend. Then it signs in every way
    /// and checks each host attestation against the owner's roster with Martlet.Core's <see cref="AccountAttestation.Check"/>,
    /// and that a changed or expired one, another key and the wrong logins are refused. The data directory only takes
    /// signin-lab.json; the desktop is not involved. The lab keeps the host running until it is stopped.
    /// </summary>
    internal static async Task<int> RunAccountAsync(string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(dataDirectory) || !Directory.Exists(dataDirectory))
            return Fail("The data directory must be an existing absolute folder (a disposable one).");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        _ = Task.Run(() =>
        {
            try { while (Console.In.ReadLine() is not null) { } }
            catch (IOException) { }
            lifetime.Cancel();
        });
        var token = lifetime.Token;
        await using var host = await SignInRehearsal.LabHost.StartAsync("lab-account-host", ecdsa: true);
        using var issuer = new SignInRehearsal.LabIssuer();
        host.Server.UseSignInProviderHandler(issuer);

        using var ownerKey = NetworkKey.Create("lab-owner");
        var owner = new SignInRehearsal.LabDesktop(ownerKey, "LAB-OWNER");
        await owner.PairByCodeAsync(host, token);
        await owner.SyncAsync(token);
        var roster = owner.State.Roster ?? throw new InvalidOperationException("The lab owner started no network.");
        var now = DateTimeOffset.UtcNow;
        var ownerSecret = Totp.NewSecret();
        var samSecret = Totp.NewSecret();
        await owner.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "owner", ["user"] = "owner", ["password"] = AccountPassword, ["totp_secret"] = ownerSecret, ["code"] = Totp.Code(ownerSecret, now)
        }, token);
        var samSetup = await owner.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "account", ["account_id"] = SamAccount, ["user"] = "Sam", ["password"] = AccountPassword, ["totp_secret"] = samSecret,
            ["code"] = Totp.Code(samSecret, now)
        }, token);
        await owner.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "account", ["account_id"] = AlexAccount, ["user"] = "alex", ["password"] = AccountPassword
        }, token);
        await owner.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "provider", ["provider_config"] = new JsonObject
            {
                ["id"] = "authentik", ["kind"] = "oidc", ["name"] = "Authentik (lab)", ["issuer"] = SignInRehearsal.LabIssuer.Issuer,
                ["client_id"] = SignInRehearsal.LabIssuer.ClientId, ["client_secret"] = SignInRehearsal.LabIssuer.ClientSecret
            }
        }, token);
        await owner.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "allow", ["provider"] = "authentik", ["subject"] = "lab-user-42", ["label"] = "sam@example.net", ["account_id"] = SamAccount
        }, token);
        var settings = await owner.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "allow", ["provider"] = "authentik", ["subject"] = FriendSubject, ["label"] = FriendEmail, ["access"] = "friend"
        }, token);
        var ownerAccount = settings.OwnerAccountId;

        var checks = new List<object>();
        var attestations = new List<object>();
        var allOk = true;
        void Record(string name, bool ok, string detail)
        {
            allOk &= ok;
            checks.Add(new { name, ok, detail });
        }
        object Describe(AccountAttestation a, AccountAttestationCheck result) => new
        {
            account = a.AccountId, device = a.DeviceId, login = a.Login.ToString(), a.Algorithm, a.IssuedAt, a.ExpiresAt, check = result.ToString(),
            textLength = a.ToText().Length
        };
        async Task<HostAccountProof?> Prove(string name, Func<Audio2FaceHostConnection, Task<HostAccountProof>> proving, Guid expected, string login)
        {
            try
            {
                using var connection = owner.Connect(host.HostId);
                var proof = await proving(connection);
                var result = proof.Attestation.Check(roster, DateTimeOffset.UtcNow);
                attestations.Add(Describe(proof.Attestation, result));
                Record(name, result == AccountAttestationCheck.Valid && proof.AccountId == expected && proof.Attestation.DeviceId == ownerKey.DeviceId &&
                    proof.Attestation.Login.ToString() == login,
                    $"account {proof.AccountId:N} as {proof.Attestation.Login} on {proof.Attestation.DeviceId}: {result} ({proof.Attestation.Algorithm})");
                return proof;
            }
            catch (Audio2FaceHostException error)
            {
                Record(name, false, "refused: " + error.Code);
                return null;
            }
        }
        async Task Refused(string name, string expected, Func<Task> attempt)
        {
            try
            {
                await attempt();
                Record(name, false, "it was not refused");
            }
            catch (Audio2FaceHostException error) { Record(name, error.Code == expected, "refused: " + error.Code); }
        }

        // The authenticator steps the setups used are spent, so the first proofs use the next step (one step of drift).
        var next = DateTimeOffset.UtcNow.AddSeconds(Totp.StepSeconds);
        var owners = await Prove("owner proves the owner's account (password and authenticator)",
            c => c.ProveWithPasswordAsync("owner", AccountPassword, Totp.Code(ownerSecret, next), cancellationToken: token),
            ownerAccount ?? Guid.Empty, "martlet:martlet:owner");
        await Prove("Sam proves Sam's account (password and authenticator)",
            c => c.ProveWithPasswordAsync("sam", AccountPassword, Totp.Code(samSecret, next), cancellationToken: token), SamAccount, "martlet:martlet:sam");
        var alex = await Prove("Alex proves Alex's account (password only)",
            c => c.ProveWithPasswordAsync("ALEX", AccountPassword, null, TimeSpan.FromHours(1), token), AlexAccount, "martlet:martlet:alex");
        await Prove("Sam proves Sam's account with the identity provider", c => c.ProveInBrowserAsync("authentik", issuer.Browse, TimeSpan.FromSeconds(30),
            cancellationToken: token), SamAccount, "oidc:authentik:lab-user-42");
        await Refused("a friend's identity proves no account", "signin.no_account", async () =>
        {
            using var connection = owner.Connect(host.HostId);
            await connection.ProveInBrowserAsync("authentik", url => issuer.Browse(url, FriendSubject, FriendEmail), TimeSpan.FromSeconds(30),
                cancellationToken: token);
        });
        await Refused("a wrong password is refused", "signin.invalid", async () =>
        {
            using var connection = owner.Connect(host.HostId);
            await connection.ProveWithPasswordAsync("alex", "not the lab passphrase", null, cancellationToken: token);
        });

        // A laptop away from home adds itself as Sam (a recovery code, as the authenticator step is spent) and gets Sam's
        // attestation with its pairing; Alex, without an authenticator, can't add a computer.
        using var laptopKey = NetworkKey.Create("lab-sam-laptop");
        var invite = new NetworkInvite { HostId = host.HostId, SpkiFingerprint = host.Fingerprint, Origin = host.Origin, Label = "Lab home" };
        try
        {
            var attempt = await HostSignInClient.BeginAsync(invite, host.Origin, "martlet", cancellationToken: token);
            var (_, _, who) = await HostSignInClient.CompleteAsync(invite, attempt, laptopKey.DeviceId, "LAB-SAM-LAPTOP",
                new JsonObject { ["user"] = "sam", ["password"] = AccountPassword, ["code"] = samSetup.RecoveryCodes![0] }, token);
            var result = who.Attestation?.Check(roster, DateTimeOffset.UtcNow);
            if (who.Attestation is { } added) attestations.Add(Describe(added, result!.Value));
            Record("a laptop adds itself as Sam and gets Sam's attestation", who.AccountId == SamAccount && result == AccountAttestationCheck.Valid &&
                who.Attestation!.DeviceId == laptopKey.DeviceId, $"signed in as {who}, account {who.AccountId:N}, attestation {result}");
        }
        catch (Audio2FaceHostException error) { Record("a laptop adds itself as Sam and gets Sam's attestation", false, "refused: " + error.Code); }
        await Refused("Alex can't add a computer without an authenticator", "signin.needs_authenticator", async () =>
        {
            var attempt = await HostSignInClient.BeginAsync(invite, host.Origin, "martlet", cancellationToken: token);
            await HostSignInClient.CompleteAsync(invite, attempt, "lab-alex-laptop", "LAB-ALEX-LAPTOP",
                new JsonObject { ["user"] = "alex", ["password"] = AccountPassword }, token);
        });

        if (alex is { Attestation: var genuine })
        {
            Record("a changed attestation fails", (genuine with { AccountId = SamAccount }).Check(roster, DateTimeOffset.UtcNow) == AccountAttestationCheck.BadSignature,
                "Alex's attestation claiming Sam's account: " + (genuine with { AccountId = SamAccount }).Check(roster, DateTimeOffset.UtcNow));
            Record("an expired attestation fails", genuine.Check(roster, genuine.ExpiresAt.AddSeconds(1)) == AccountAttestationCheck.Expired,
                "checked a second after it expired: " + genuine.Check(roster, genuine.ExpiresAt.AddSeconds(1)));
            var moved = roster.AddHost(ownerKey, host.HostId, "lab-account-host", host.Origin, "sha256:" + new string('0', 64), DateTimeOffset.UtcNow);
            Record("another key than the roster's pin fails", genuine.Check(moved, DateTimeOffset.UtcNow) == AccountAttestationCheck.KeyNotPinned,
                "with the host pinned to another key: " + genuine.Check(moved, DateTimeOffset.UtcNow));
            var text = genuine.ToText();
            Record("the text form round-trips", text.Length <= AccountAttestation.MaximumTextLength &&
                AccountAttestation.FromText(text).Check(roster, DateTimeOffset.UtcNow) == AccountAttestationCheck.Valid,
                $"{text.Length} characters of base64url");
        }
        var final = await owner.ReadAsync(host.HostId, token);
        Record("the host lists the household logins", final.Accounts.Count == 2 && final.Accounts.Any(a => a is { User: "Sam", HasAuthenticator: true, RecoveryCodesLeft: 9 }) &&
            final.Accounts.Any(a => a is { User: "alex", HasAuthenticator: false }) && final.Allowed.Any(a => a.AccountId == SamAccount),
            string.Join(", ", final.Accounts.Select(a => $"{a.User} ({a.AccountId:N}, authenticator {a.HasAuthenticator}, {a.RecoveryCodesLeft} recovery codes)")));
        Record("the owner's account comes from the network", ownerAccount is not null && owners?.AccountId == ownerAccount,
            $"owner account {ownerAccount:N} in network {roster.NetworkId}");

        var status = new
        {
            ready = true, mode = "account", ok = allOk, hostId = host.HostId, origin = host.Origin, network = host.Server.NetworkState.State,
            networkId = roster.NetworkId, hostPin = roster.Host(host.HostId)?.Spki, ownerAccount,
            accounts = new { sam = SamAccount, alex = AlexAccount }, checks, attestations, at = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(status);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, StatusFile), json, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, mode = "account", ok = allOk, hostId = host.HostId, checks = checks.Count }));
        while (!token.IsCancellationRequested && !File.Exists(Path.Combine(dataDirectory, StopFile)))
        {
            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) { break; }
        }
        return allOk ? 0 : 1;
    }
}
