using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// The sign-in lab's household mode (signin_lab mode "account" with household true; docs/MCP.md): sign-in providers for the
/// whole household and provider logins linked to household accounts, with the desktop that runs on <c>dataDirectory</c>. Two
/// real gateways on 127.0.0.1 (<see cref="HostA"/> and <see cref="HostB"/>, ECDSA keys like every Martlet host), both routed to
/// the lab's OpenID Connect issuer, are paired with that desktop (hosts.json written there, the pairing secrets in the lab
/// credential folder named by MARTLET_LAB_CREDENTIALS). Before the desktop binds them to its network, the lab's own admin desktop
/// sets the household provider <c>authentik</c> up on host A only, as a public client: the desktop adds it to host B by itself
/// when it reads its hosts' sign-in settings, and Save provider in Sign-in from outside saves it on both.
/// <para>Once the desktop has bound both hosts, the lab acts for that desktop (its own pairings): it gives Sam
/// (<see cref="SignInLab.SamAccount"/>) a Martlet password with an authenticator on both hosts, proves Sam with it on host A and
/// links <c>lab-user-42</c> at the provider to Sam on every host (<see cref="HouseholdSignIn.LinkInBrowserAsync"/>, the code the
/// Account page uses). When host B has the provider, it proves Sam on host B with that provider login (a sign-in by it), and a
/// new computer (<c>lab-new-pc</c>) joins through host B by signing in with it: it gets Sam's attestation, which the lab checks
/// against the roster, asks to join, and the desktop lets it in by itself. The status (signin-lab.json) never holds a secret.
/// It stops when its standard input closes, signin-lab.stop appears in the data directory, or after 20 minutes.</para>
/// </summary>
internal static class SignInAccountLab
{
    internal const string HostA = "lab-host-a";
    internal const string HostB = "lab-host-b";
    internal const string Provider = "authentik";
    internal const string NewDevice = "lab-new-pc";
    internal const string SecondDevice = "lab-sam-pc";
    /// <summary>The desktop's own person at the lab provider: the identity its Account page links.</summary>
    internal const string DesktopSubject = "lab-desktop-7";
    internal const string DesktopEmail = "owner@example.net";

    internal static async Task<int> RunAsync(string dataDirectory)
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
        using var issuer = new SignInRehearsal.LabIssuer();
        await using var a = await SignInRehearsal.LabHost.StartAsync(HostA, ecdsa: true);
        await using var b = await SignInRehearsal.LabHost.StartAsync(HostB, ecdsa: true);
        LabHosts hosts = [a, b];
        foreach (var host in hosts) host.Server.UseSignInProviderHandler(issuer);

        // The lab's admin desktop pairs with both hosts while they are in no network and sets the household provider up on host A
        // only, as a public client: the desktop adds it to host B by itself.
        using var adminKey = NetworkKey.Create("lab-admin");
        var admin = new SignInRehearsal.LabDesktop(adminKey, "LAB-ADMIN");
        foreach (var host in hosts) await admin.PairByCodeAsync(host, token);
        await admin.ChangeAsync(HostA, new HouseholdProvider(Provider, "oidc", "Authentik (lab)", SignInRehearsal.LabIssuer.Issuer,
            SignInRehearsal.LabIssuer.ClientId, null, null).Change(null), token);

        // The desktop on the data directory: paired with both hosts by a code under the device ID it names itself by (device.json),
        // its secrets in the lab folder, both in its hosts.json.
        var desktopDevice = DeviceIds.Ensure(dataDirectory).Id;
        var desktop = new Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)>(StringComparer.Ordinal);
        var entries = new List<object>();
        foreach (var host in hosts)
        {
            var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, card.Code.Reveal(), desktopDevice, "LAB-DESKTOP", token);
            using (var lease = new SecretLease(secret))
            {
                var stored = new WindowsCredentialStore().WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
                if (stored != CredentialError.None) return Fail("Couldn't keep the lab pairing secret: " + stored);
            }
            desktop[host.HostId] = (pairing, secret);
            entries.Add(new
            {
                pairing = new
                {
                    origin = pairing.Origin, hostId = pairing.HostId, spkiFingerprint = pairing.SpkiFingerprint, deviceId = pairing.DeviceId,
                    credentialId = pairing.CredentialId
                },
                method = "Agent"
            });
        }
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "hosts.json"), JsonSerializer.Serialize(new { version = 1, hosts = entries }), token);
        Audio2FaceHostConnection AsDesktop(string hostId) => new(desktop[hostId].Pairing, desktop[hostId].Secret);

        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, mode = "account", household = true, hosts = hosts.Select(h => h.HostId).ToArray(),
            provider = Provider, providerOnHosts = hosts.OnHosts(Provider) }));
        var events = new List<string>();
        string[] seen = [];
        var samSecret = Totp.NewSecret();
        object? link = null, providerProve = null, accountSignIn = null;
        string? phase = "waiting for the desktop to bind both hosts", failure = null;
        using var newKey = NetworkKey.Create(NewDevice);
        var newPc = new SignInRehearsal.LabDesktop(newKey, "LAB-NEW-PC");
        var newPcPaired = false;
        var newPcWasMember = false;
        object? joinAttested = null;
        using var secondKey = NetworkKey.Create(SecondDevice);
        var secondPc = new SignInRehearsal.LabDesktop(secondKey, "LAB-SAM-PC");
        var secondPcPaired = false;
        string? recoveryOnB = null;
        string[]? joinChoices = null;
        object? passwordSignIn = null;
        // In a run with the desktop's window, the desktop hands its browser sign-in pages here (MARTLET_LAB_BROWSER): the first
        // one signs in as the desktop's own person (Link on the Account page), the next ones as Sam (Sign in as someone else).
        var browserFolder = Environment.GetEnvironmentVariable(SignInLab.BrowserVariable) is { Length: > 0 } folder && Path.IsPathFullyQualified(folder)
            ? folder : null;
        if (browserFolder is not null) Directory.CreateDirectory(browserFolder);
        var browsed = new List<string>();
        while (!token.IsCancellationRequested && !File.Exists(Path.Combine(dataDirectory, SignInLab.StopFile)))
        {
            var on = hosts.OnHosts(Provider);
            if (!on.SequenceEqual(seen))
            {
                events.Add($"{Provider} is on {(on.Length == 0 ? "no host" : string.Join(" and ", on))}");
                seen = on;
            }
            var roster = Roster(dataDirectory);
            if (browserFolder is not null && File.Exists(Path.Combine(browserFolder, SignInLab.BrowserFile)))
            {
                try
                {
                    var page = Path.Combine(browserFolder, SignInLab.BrowserFile);
                    var url = (await File.ReadAllTextAsync(page, token)).Trim();
                    File.Delete(page);
                    var (subject, email) = browsed.Count == 0 ? (DesktopSubject, DesktopEmail) : ("lab-user-42", "me@example.net");
                    issuer.Browse(url, subject, email);
                    browsed.Add(email);
                    events.Add($"browser: signed in at the lab provider as {email} for the desktop");
                }
                catch (Exception error) when (error is IOException or KeyNotFoundException or UriFormatException) { events.Add("browser: " + error.Message); }
            }
            try
            {
                if (link is null && roster is not null && hosts.All(h => h.Server.NetworkState.State == "bound" && roster.Host(h.HostId) is { Removed: false }))
                {
                    // Sam's Martlet password on every host, a Prove with it on host A, then the provider login linked to Sam everywhere.
                    phase = "linking the provider login to Sam";
                    var now = DateTimeOffset.UtcNow;
                    foreach (var host in hosts)
                    {
                        using var connection = AsDesktop(host.HostId);
                        var set = await connection.ChangeSignInSettingsAsync(new JsonObject
                        {
                            ["action"] = "account", ["account_id"] = SignInLab.SamAccount.ToString(), ["user"] = "Sam", ["password"] = SignInLab.AccountPassword,
                            ["totp_secret"] = samSecret, ["code"] = Totp.Code(samSecret, now)
                        }, token);
                        if (host.HostId == HostB) recoveryOnB = set.RecoveryCodes?.FirstOrDefault();
                    }
                    using var atA = AsDesktop(HostA);
                    var proved = await atA.ProveWithPasswordAsync("sam", SignInLab.AccountPassword, Totp.Code(samSecret, now.AddSeconds(Totp.StepSeconds)),
                        cancellationToken: token);
                    var linked = await HouseholdSignIn.LinkInBrowserAsync(atA, SignInLab.SamAccount, proved.Attestation, roster, Provider, issuer.Browse, TimeSpan.FromSeconds(30),
                        hosts.Select(h => h.HostId), AsDesktop, cancellationToken: token);
                    link = new
                    {
                        account = SignInLab.SamAccount, provedWith = proved.Attestation.Login.ToString(), identity = linked.Identity.ToString(),
                        savedOn = linked.Results.Where(r => r.Saved).Select(r => r.HostId).ToArray(),
                        problems = linked.Results.Where(r => !r.Saved).Select(r => $"{r.HostId}: {r.Problem}").ToArray()
                    };
                    events.Add($"linked {linked.Identity} to Sam on {string.Join(" and ", linked.Results.Where(r => r.Saved).Select(r => r.HostId))}");
                    phase = $"waiting for the desktop to add {Provider} to {HostB} (Devices › Friends' read)";
                }
                if (link is not null && roster is not null && providerProve is null && on.Contains(HostB))
                {
                    // A sign-in by the linked provider login on host B, where the link arrived only by the push: Sam's attestation.
                    using var atB = AsDesktop(HostB);
                    var proof = await atB.ProveInBrowserAsync(Provider, issuer.Browse, TimeSpan.FromSeconds(30), cancellationToken: token);
                    var check = proof.Attestation.Check(roster, DateTimeOffset.UtcNow);
                    providerProve = new
                    {
                        host = HostB, account = proof.AccountId, login = proof.Attestation.Login.ToString(), check = check.ToString(),
                        verified = check == AccountAttestationCheck.Valid && proof.AccountId == SignInLab.SamAccount
                    };
                    events.Add($"proved Sam on {HostB} with {proof.Identity}: {check}");

                    // A new computer joins through host B by signing in to Sam's account with that provider login.
                    phase = "a new computer signs in to Sam's account through lab-host-b";
                    var invite = new NetworkInvite { HostId = HostB, SpkiFingerprint = b.Fingerprint, Origin = b.Origin, Label = "Lab home" };
                    var (pairing, secret, who) = await HostSignInClient.SignInInBrowserAsync(invite, b.Origin, Provider, newKey.DeviceId, "LAB-NEW-PC",
                        issuer.Browse, TimeSpan.FromSeconds(30), token);
                    newPc.Keep(pairing, secret);
                    newPcPaired = true;
                    var attested = who.Attestation?.Check(roster, DateTimeOffset.UtcNow);
                    accountSignIn = new
                    {
                        host = HostB, device = newKey.DeviceId, signedInAs = who.ToString(), account = who.AccountId, attestation = attested?.ToString(),
                        verified = attested == AccountAttestationCheck.Valid && who.AccountId == SignInLab.SamAccount && who.Attestation!.DeviceId == newKey.DeviceId
                    };
                    events.Add($"{NewDevice} signed in to Sam's account through {HostB}: attestation {attested}");

                    // What Join with an invite offers there, and a second computer that joins with Sam's Martlet password (a recovery
                    // code: the authenticator steps the setup and the Prove used are spent).
                    var (_, choices) = await HostSignInClient.ReadProvidersAsync(invite, token);
                    joinChoices = choices.Select(c => c.Id).ToArray();
                    var (pairing2, secret2, who2) = await HostSignInClient.SignInWithPasswordAsync(invite, b.Origin, HostSignInProvider.Martlet, "sam",
                        SignInLab.AccountPassword, recoveryOnB ?? "", secondKey.DeviceId, "LAB-SAM-PC", token);
                    secondPc.Keep(pairing2, secret2);
                    secondPcPaired = true;
                    var attested2 = who2.Attestation?.Check(roster, DateTimeOffset.UtcNow);
                    passwordSignIn = new
                    {
                        host = HostB, device = secondKey.DeviceId, signedInAs = who2.ToString(), account = who2.AccountId, attestation = attested2?.ToString(),
                        verified = attested2 == AccountAttestationCheck.Valid && who2.AccountId == SignInLab.SamAccount
                    };
                    events.Add($"{SecondDevice} signed in to Sam's account with Sam's Martlet password through {HostB}: attestation {attested2}");
                    phase = "waiting for the desktop to let the new computers in";
                }
                if (secondPcPaired && secondPc.State.RemovedFrom is null)
                    events.AddRange((await secondPc.SyncAsync(token)).Events.Select(e => "second computer: " + e));
                if (newPcPaired && newPc.State.RemovedFrom is null)
                {
                    var result = await newPc.SyncAsync(token);
                    events.AddRange(result.Events.Select(e => "new computer: " + e));
                    if (joinAttested is null)
                    {
                        using var atB = AsDesktop(HostB);
                        var view = await atB.ReadNetworkAsync(token);
                        if (view.Joins.FirstOrDefault(j => j.DeviceId == newKey.DeviceId)?.SignIn is { } signIn)
                            joinAttested = new { provider = signIn.Provider, label = signIn.Label, account = signIn.AccountId };
                    }
                }
            }
            catch (Exception error) when (error is Audio2FaceHostException or InvalidOperationException or IOException or HttpRequestException or JsonException)
            {
                failure = $"{phase}: {error.Message}";
                events.Add("failed: " + failure);
            }
            var member = newPc.State.Roster?.Trusts(newKey.DeviceId, newKey.PublicKey) == true;
            newPcWasMember |= member;
            var secondMember = secondPc.State.Roster?.Trusts(secondKey.DeviceId, secondKey.PublicKey) == true;
            if (member && secondMember && phase != "done") phase = "done";
            var status = new
            {
                ready = true, mode = "account", household = true, hosts = hosts.Select(h => h.HostId).ToArray(), provider = Provider, phase, failure,
                network = hosts.ToDictionary(h => h.HostId, h => h.Server.NetworkState.State),
                networkId = a.Server.NetworkState.NetworkId,
                providers = hosts.ToDictionary(h => h.HostId, h => Providers(h.SignInBytes)),
                providerOnHosts = on,
                clientSecretOnHosts = hosts.Where(h => Providers(h.SignInBytes).Any(p => p.Id == Provider && p.HasClientSecret)).Select(h => h.HostId).ToArray(),
                linkedLogins = hosts.ToDictionary(h => h.HostId, h => LinkedLogins(h.SignInBytes)),
                link, providerProve, accountSignIn, joinAttested, joinChoices, passwordSignIn, browsedAs = browsed,
                newDeviceMember = member, newDeviceWasMember = newPcWasMember, newDeviceWaiting = newPc.State.Waiting?.CheckNumber,
                secondDeviceMember = secondMember,
                events = events.TakeLast(12).ToArray(), at = DateTimeOffset.UtcNow
            };
            var path = Path.Combine(dataDirectory, SignInLab.StatusFile);
            try
            {
                await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(status), CancellationToken.None);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    internal const string JoinHost = "lab-home-host";

    /// <summary>
    /// The desktop on <paramref name="dataDirectory"/> as a new computer that joins a household by signing in to Sam's account
    /// (signin_lab mode "account" with joinDesktop true; docs/MCP.md). A real gateway on 127.0.0.1 (<see cref="JoinHost"/>, an
    /// ECDSA key) belongs to a simulated owner (<c>lab-owner</c>), who started the network with it, set up the lab provider with a
    /// client secret, gave Sam a Martlet password with an authenticator and linked <c>lab-user-42</c> (<c>sam@example.net</c>) to
    /// Sam. The lab then does what Join with an invite does for the desktop, under the device ID that desktop names itself by
    /// (device.json): it signs in through the lab provider as Sam, keeps the pairing (hosts.json, the secret in the lab credential
    /// folder) and the account the host vouched for (joined-account.json). Once the desktop reads hosts.json (RefreshDevices), it
    /// asks to join; the owner's computer lets it in by the host's attestation (ApproveSignedIn), and the desktop signs Sam in and
    /// switches to Sam. The status names the account, whether the desktop asked, was let in, and whether joined-account.json is
    /// still waiting. Never a secret.
    /// </summary>
    internal static async Task<int> RunJoinAsync(string dataDirectory)
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
        using var issuer = new SignInRehearsal.LabIssuer();
        await using var host = await SignInRehearsal.LabHost.StartAsync(JoinHost, ecdsa: true);
        host.Server.UseSignInProviderHandler(issuer);

        using var ownerKey = NetworkKey.Create("lab-owner");
        var owner = new SignInRehearsal.LabDesktop(ownerKey, "LAB-OWNER");
        await owner.PairByCodeAsync(host, token);
        await owner.SyncAsync(token);
        var samSecret = Totp.NewSecret();
        await owner.ChangeAsync(JoinHost, new JsonObject
        {
            ["action"] = "account", ["account_id"] = SignInLab.SamAccount.ToString(), ["user"] = "Sam", ["password"] = SignInLab.AccountPassword,
            ["totp_secret"] = samSecret, ["code"] = Totp.Code(samSecret, DateTimeOffset.UtcNow)
        }, token);
        await owner.ChangeAsync(JoinHost, new HouseholdProvider(Provider, "oidc", "Authentik (lab)", SignInRehearsal.LabIssuer.Issuer,
            SignInRehearsal.LabIssuer.ClientId, null, null).Change(SignInRehearsal.LabIssuer.ClientSecret), token);
        await owner.ChangeAsync(JoinHost, HouseholdSignIn.LinkChange(Provider, "lab-user-42", "sam@example.net", SignInLab.SamAccount), token);
        // Sam in the household's account directory, with both logins, as the owner's computer writes it.
        var now = DateTimeOffset.UtcNow;
        var directory = AccountDirectory.Empty.Put(ownerKey, Account.Create("Sam", AccountRoles.Member, SignInLab.SamAccount)
            .WithLogin(AccountLogin.For(AccountLoginKey.ForPassword("sam"), "Sam", now))
            .WithLogin(AccountLogin.For(AccountLoginKey.ForProvider("oidc", Provider, "lab-user-42"), "sam@example.net", now)), now);
        using (var connection = owner.Connect(JoinHost)) await connection.MergeAccountsAsync(directory, token);

        // Join with an invite, as the window does it, for the desktop of the data directory.
        var device = DeviceIds.Ensure(dataDirectory).Id;
        var invite = new NetworkInvite { HostId = JoinHost, SpkiFingerprint = host.Fingerprint, Origin = host.Origin, Label = "Sam's household" };
        var (origin, choices) = await HostSignInClient.ReadProvidersAsync(invite, token);
        var (pairing, secret, who) = await HostSignInClient.SignInInBrowserAsync(invite, origin, Provider, device, Environment.MachineName,
            issuer.Browse, TimeSpan.FromSeconds(30), token);
        if (who.AccountId != SignInLab.SamAccount || who.Attestation is null) return Fail($"The host answered account {who.AccountId} for Sam's login.");
        using (var lease = new SecretLease(secret))
        {
            var stored = new WindowsCredentialStore().WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
            if (stored != CredentialError.None) return Fail("Couldn't keep the lab pairing secret: " + stored);
        }
        // joined-account.json in the form Martlet.Desktop's JoinedAccount writes.
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "joined-account.json"), JsonSerializer.Serialize(new
        {
            hostId = pairing.HostId, accountId = who.AccountId, login = who.Attestation.Login.ToString(), attestation = who.Attestation.ToText(),
            at = DateTimeOffset.UtcNow
        }), token);
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
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, mode = "account", joinDesktop = true, hostId = JoinHost, desktop = device,
            account = who.AccountId, signedInAs = who.ToString(), choices = choices.Select(c => c.Id).ToArray() }));

        var events = new List<string>();
        var asked = false;
        object? joinRequest = null;
        while (!token.IsCancellationRequested && !File.Exists(Path.Combine(dataDirectory, SignInLab.StopFile)))
        {
            try
            {
                var synced = await owner.SyncAsync(token);
                if (synced.Joins.FirstOrDefault(j => j.DeviceId == device) is { } join)
                {
                    asked = true;
                    joinRequest = new { provider = join.SignIn?.Provider, label = join.SignIn?.Label, account = join.SignIn?.AccountId };
                    var approved = owner.ApproveSignedIn(synced.Joins);
                    if (approved.Count > 0)
                    {
                        events.Add("owner: let in " + string.Join(", ", approved.Select(a => $"{a.DisplayName} ({a.SignIn?.Label}, account {a.SignIn?.AccountId})")));
                        await owner.SyncAsync(token);
                    }
                }
            }
            catch (Exception error) when (error is Audio2FaceHostException or InvalidOperationException or IOException or HttpRequestException)
            {
                events.Add("owner sync failed: " + error.Message);
            }
            var status = new
            {
                ready = true, mode = "account", joinDesktop = true, hostId = JoinHost, network = host.Server.NetworkState.State,
                networkId = host.Server.NetworkState.NetworkId, desktop = device, account = who.AccountId, signedInAs = who.ToString(),
                choices = choices.Select(c => c.Id).ToArray(), askedToJoin = asked, joinRequest,
                desktopMember = owner.State.Roster?.Desktop(device) is { Removed: false },
                joinedAccountWaiting = File.Exists(Path.Combine(dataDirectory, "joined-account.json")),
                events = events.TakeLast(12).ToArray(), at = DateTimeOffset.UtcNow
            };
            var path = Path.Combine(dataDirectory, SignInLab.StatusFile);
            try
            {
                await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(status), CancellationToken.None);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    /// <summary>The roster the desktop on the data directory accepted (its network.json), or null.</summary>
    private static NetworkRoster? Roster(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, NetworkLocalState.FileName);
            return File.Exists(path) ? NetworkLocalState.Parse(File.ReadAllBytes(path)).Roster : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return null; }
    }

    private sealed class LabHosts : List<SignInRehearsal.LabHost>
    {
        internal string[] OnHosts(string provider) => this.Where(h => Providers(h.SignInBytes).Any(p => p.Id == provider)).Select(h => h.HostId).ToArray();
    }

    internal sealed record LabProvider(string Id, string Kind, string Name, bool HasClientSecret);

    /// <summary>The providers in a host's signin.json: IDs, kinds, names and whether a client secret is kept (never the secret).</summary>
    internal static LabProvider[] Providers(byte[]? signIn)
    {
        if (signIn is null) return [];
        try
        {
            var root = JsonNode.Parse(signIn);
            return root?["providers"]?.AsArray().Select(p => new LabProvider((string?)p?["id"] ?? "", (string?)p?["kind"] ?? "", (string?)p?["name"] ?? "",
                p?["client_secret"] is not null)).ToArray() ?? [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The allowed identities in a host's signin.json that are linked to an account: provider, subject and account.</summary>
    internal static object[] LinkedLogins(byte[]? signIn)
    {
        if (signIn is null) return [];
        try
        {
            var root = JsonNode.Parse(signIn);
            return root?["allowed"]?.AsArray().Where(x => x?["account_id"] is not null)
                .Select(x => (object)new { provider = (string?)x?["provider"], subject = (string?)x?["subject"], account = (string?)x?["account_id"] })
                .ToArray() ?? [];
        }
        catch (JsonException) { return []; }
    }

    private static int Fail(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ready = false, error = message }));
        return 1;
    }
}
