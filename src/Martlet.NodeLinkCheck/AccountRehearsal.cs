using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses the household's account directory (docs/ACCOUNTS.md) end to end on this PC with the production code: two real
/// gateways (Kestrel, pinned TLS, signed requests) on 127.0.0.1 with an in-memory network.json and accounts.json, bound to one
/// lab network, and two simulated member desktops with real network keys, the desktop's paired client (HostAccounts.cs) and
/// the directory's own merge and checks (Martlet.Core.Accounts). Accounts are synthetic; nothing leaves loopback.
/// </summary>
internal static class AccountRehearsal
{
    private const string SidA = "S-1-5-21-1004336348-1177238915-682003330-1001";

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        await using var h1 = await LabHost.StartAsync("lab-accounts-1");
        await using var h2 = await LabHost.StartAsync("lab-accounts-2");
        LabHost[] hosts = [h1, h2];
        using var keyA = NetworkKey.Create("lab-desktop-a");
        using var keyB = NetworkKey.Create("lab-desktop-b");
        using var keyX = NetworkKey.Create("lab-intruder");
        var a = new LabDesktop(keyA);
        var b = new LabDesktop(keyB);
        var x = new LabDesktop(keyX);
        Guid ownerId = default, alexId = default;

        async Task Run(string name, Func<Task<(bool Ok, string Detail)>> action)
        {
            try
            {
                var (ok, detail) = await action();
                steps.Add((name, ok, detail));
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                steps.Add((name, false, $"{error.GetType().Name}: {error.Message}"));
            }
        }

        await Run("A founds a household network with lab-accounts-1 and lab-accounts-2 and lets B in; both hosts are bound to it", async () =>
        {
            foreach (var desktop in new[] { a, b })
            foreach (var host in hosts)
                await desktop.PairAsync(host);
            var now = DateTimeOffset.UtcNow;
            var roster = NetworkRoster.Found(keyA, "LAB-A", now)
                .AddHost(keyA, h1.HostId, "LAB-ACCOUNTS-1", h1.Origin, h1.Identity.SpkiFingerprint, now)
                .AddHost(keyA, h2.HostId, "LAB-ACCOUNTS-2", h2.Origin, h2.Identity.SpkiFingerprint, now)
                .AddDesktop(keyA, keyB.DeviceId, "LAB-B", keyB.PublicKey, now);
            foreach (var host in hosts) await a.MergeNetworkAsync(host, roster, token);
            a.Roster = b.Roster = roster;
            return (h1.State == "bound" && h2.State == "bound", $"network {roster.NetworkId}; lab-accounts-1 {h1.State}, lab-accounts-2 {h2.State}");
        });

        await Run("A migrates to accounts: it adds the household owner (the ID every computer derives from the network, its Windows " +
            "login, signed in on A) and syncs; both hosts keep it, signed by A, and their digests match A's copy", async () =>
        {
            var windows = AccountLoginKey.ForWindows(keyA.DeviceId, SidA);
            var owner = OwnerAccount.Create(a.Roster!, "Owner").WithLogin(AccountLogin.For(windows, "owner@example.net", DateTimeOffset.UtcNow))
                .WithDevice(AccountDevice.For(keyA.DeviceId, windows, DateTimeOffset.UtcNow))
                .WithEmailHint(a.Roster!.NetworkId, "owner@example.net");
            ownerId = owner.Id;
            a.Put(owner);
            var sync = await a.SyncAsync(hosts, token);
            var d1 = await a.DigestAsync(h1, token);
            var d2 = await a.DigestAsync(h2, token);
            var onHost = (await a.ReadAsync(h1, token)).Find(ownerId);
            var signed = onHost is not null && AccountDirectory.Verify(onHost, a.Roster!);
            return (sync.RefusedByHosts == 0 && signed && d1 == a.Digest && d2 == a.Digest && ownerId == OwnerAccount.IdFor(a.Roster!.NetworkId) &&
                    onHost!.CreatedBy == keyA.DeviceId,
                $"owner {ownerId:N} (from the network ID: {ownerId == OwnerAccount.IdFor(a.Roster!.NetworkId)}); signed by {onHost?.UpdatedBy}, " +
                $"created by {onHost?.CreatedBy}: {signed}; host digests match A: {d1 == a.Digest && d2 == a.Digest}; refused by hosts: {sync.RefusedByHosts}");
        });

        await Run("B syncs, checks every entry against its own roster and takes the owner account; A's Windows login finds it", async () =>
        {
            var sync = await b.SyncAsync(hosts, token);
            var found = b.Local.FindByLogin(AccountLoginKey.ForWindows(keyA.DeviceId, SidA));
            return (sync.RefusedHere == 0 && found?.Id == ownerId && b.Digest == a.Digest,
                $"B took {b.Local.Live.Count()} account(s), refused {sync.RefusedHere}; the Windows login finds {found?.Name}; same as A: {b.Digest == a.Digest}");
        });

        await Run("B adds a person (Alex, a member with a Martlet password and a voice) and syncs with lab-accounts-2 only; A syncs with " +
            "both hosts and every copy ends the same", async () =>
        {
            var alex = Account.Create("Alex", AccountRoles.Member)
                .WithLogin(AccountLogin.For(AccountLoginKey.ForPassword("alex"), "alex", DateTimeOffset.UtcNow))
                .WithVoice("5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7");
            alexId = alex.Id;
            b.Put(alex);
            await b.SyncAsync([h2], token);
            var before = (await a.ReadAsync(h1, token)).Find(alexId) is not null;
            await a.SyncAsync(hosts, token);
            await b.SyncAsync(hosts, token);
            var d1 = await a.DigestAsync(h1, token);
            var d2 = await a.DigestAsync(h2, token);
            var same = d1 == d2 && d1 == a.Digest && a.Digest == b.Digest;
            return (!before && same && a.Local.Find(alexId) is { CreatedBy: "lab-desktop-b", Role: AccountRoles.Member },
                $"lab-accounts-1 had Alex before A synced: {before}; every copy the same: {same} ({a.Local.Live.Count()} accounts)");
        });

        await Run("A and B rename the owner account while apart; the later change wins on every computer", async () =>
        {
            a.Put(a.Local.Find(ownerId)! with { Name = "Owner (from A)" });
            await Task.Delay(20, token);
            b.Put(b.Local.Find(ownerId)! with { Name = "Owner (from B)" });
            await a.SyncAsync(hosts, token);
            await b.SyncAsync(hosts, token);
            await a.SyncAsync(hosts, token);
            var names = new[] { a.Local, b.Local, await a.ReadAsync(h1, token), await a.ReadAsync(h2, token) }.Select(d => d.Find(ownerId)!.Name).ToArray();
            return (names.All(n => n == "Owner (from B)") && a.Local.Find(ownerId)!.CreatedBy == keyA.DeviceId,
                $"names: {string.Join(", ", names)}; the creator stays {a.Local.Find(ownerId)!.CreatedBy}");
        });

        await Run("While apart, A renames Alex after B removed Alex: the removal wins everywhere and Alex never comes back", async () =>
        {
            b.Remove(alexId);
            await Task.Delay(20, token);
            a.Put(a.Local.Find(alexId)! with { Name = "Alex B." });
            await b.SyncAsync(hosts, token);
            await a.SyncAsync(hosts, token);
            await b.SyncAsync(hosts, token);
            var removed = new[] { a.Local, b.Local, await a.ReadAsync(h1, token), await a.ReadAsync(h2, token) }
                .All(d => d.Find(alexId) is { Removed: true, Logins.Count: 0 });
            return (removed && a.Local.FindByLogin(AccountLoginKey.ForPassword("alex")) is null,
                $"removed on A, B and both hosts: {removed}; the password login finds nobody: {a.Local.FindByLogin(AccountLoginKey.ForPassword("alex")) is null}");
        });

        await Run("A computer outside the network (paired with lab-accounts-1, not a member) posts its own account and a forged change " +
            "of the owner account: the host refuses both and keeps its copy", async () =>
        {
            await x.PairAsync(h1);
            var before = await a.DigestAsync(h1, token);
            var owner = (await a.ReadAsync(h1, token)).Find(ownerId)!;
            var forged = AccountDirectory.Empty with { Accounts = [owner with { Role = AccountRoles.Member, Revision = owner.Revision + 1 }] };
            x.Put(Account.Create("Eve", AccountRoles.Owner));
            var result = await x.MergeAsync(h1, AccountDirectory.Merge(x.Local, forged), token);
            var after = await a.DigestAsync(h1, token);
            return (result.Rejected == 2 && before == after && result.Directory.Find(ownerId)!.Role == AccountRoles.Owner,
                $"refused {result.Rejected} of 2 entries; host copy unchanged: {before == after}; the owner is still an owner: {result.Directory.Find(ownerId)!.Role}");
        });

        await Run("lab-accounts-1 restarts with its saved accounts.json and network.json and serves the same directory", async () =>
        {
            var before = await a.DigestAsync(h1, token);
            await h1.StopAsync();
            await h1.StartAsync();
            await a.PairAsync(h1);
            var after = await a.DigestAsync(h1, token);
            var saved = h1.SavedAccounts is { } bytes && AccountDirectory.Parse(bytes).Digest() == before;
            return (before == after && saved && h1.State == "bound", $"digest kept across the restart: {before == after}; accounts.json matches: {saved}; {h1.State}");
        });

        await Run("Only paired devices may read the directory: an unsigned request is refused", async () =>
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync(h1.Origin + "/martlet/v1/accounts", token);
            var body = await response.Content.ReadAsStringAsync(token);
            return (response.StatusCode != HttpStatusCode.OK && !body.Contains("Owner", StringComparison.Ordinal),
                $"unsigned request: HTTP {(int)response.StatusCode}, no account in the answer");
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "Two real gateways on 127.0.0.1 (Kestrel, pinned TLS, signed requests) with an in-memory network.json and accounts.json, " +
                "bound to one lab network, two member desktops and one outside computer with real network keys and the desktop's paired " +
                "client, merging with the production account directory. Synthetic accounts. Not covered: the desktop's account sync and " +
                "picker (later work), a friend's credential (Martlet.Gateway.Tests), the Linux host's file and two real computers on a LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    private sealed record SyncResult(int RefusedHere, int RefusedByHosts);

    /// <summary>A simulated desktop: its network key, its pairings, the roster it accepted and its own copy of the directory,
    /// kept through accounts.json's format between syncs.</summary>
    private sealed class LabDesktop(NetworkKey key)
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        internal NetworkRoster? Roster { get; set; }
        internal AccountDirectory Local { get; private set; } = AccountDirectory.Empty;
        internal string Digest => Local.Digest();

        internal async Task PairAsync(LabHost host)
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = key.DeviceId, DisplayName = key.DeviceId.ToUpperInvariant(), Roles = [GatewayRole.Voice] });
            pairings[host.HostId] = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, key.DeviceId,
                card.PairingId, card.Token.Reveal());
        }

        private Audio2FaceHostConnection Connect(LabHost host) => new(pairings[host.HostId].Pairing, pairings[host.HostId].Secret);

        internal async Task MergeNetworkAsync(LabHost host, NetworkRoster roster, CancellationToken token)
        {
            using var connection = Connect(host);
            await connection.MergeNetworkAsync(roster, token);
        }

        internal void Put(Account account) => Local = AccountDirectory.Parse(Local.Put(key, account, DateTimeOffset.UtcNow).Write());

        internal void Remove(Guid id) => Local = AccountDirectory.Parse(Local.Remove(key, id, DateTimeOffset.UtcNow).Write());

        internal async Task<AccountDirectory> ReadAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadAccountsAsync(token);
        }

        internal async Task<string> DigestAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadAccountsDigestAsync(token);
        }

        internal async Task<AccountDirectoryMerge> MergeAsync(LabHost host, AccountDirectory directory, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeAccountsAsync(directory, token);
        }

        /// <summary>What desktop directory sync does: read each host's copy and take the entries this PC's roster vouches for,
        /// then give each host the merged copy and take what it answers.</summary>
        internal async Task<SyncResult> SyncAsync(IEnumerable<LabHost> hosts, CancellationToken token)
        {
            int here = 0, there = 0;
            var reachable = hosts.Where(h => h.Running && pairings.ContainsKey(h.HostId)).ToArray();
            foreach (var host in reachable)
            {
                var accepted = AccountDirectory.Accept(Local, await ReadAsync(host, token), Roster);
                Local = accepted.Directory;
                here += accepted.Rejected;
            }
            foreach (var host in reachable)
            {
                var merged = await MergeAsync(host, Local, token);
                there += merged.Rejected;
                var accepted = AccountDirectory.Accept(Local, merged.Directory, Roster);
                Local = accepted.Directory;
                here += accepted.Rejected;
            }
            Local = AccountDirectory.Parse(Local.Write());
            return new(here, there);
        }
    }

    /// <summary>A real gateway on 127.0.0.1 with an in-memory network.json and accounts.json that survive a restart.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayAuditSink
    {
        private sealed class Memory : IGatewayAccountStorage, IGatewayNetworkStorage
        {
            internal byte[]? Bytes;
            public byte[]? Load() => Bytes;
            public void Save(byte[] bytes) => Bytes = (byte[])bytes.Clone();
        }

        private readonly Memory accounts = new();
        private readonly Memory network = new();
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private int port;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin => $"https://127.0.0.1:{port}";
        internal bool Running => listener is not null;
        internal string State => Server.NetworkState.State;
        internal byte[]? SavedAccounts => accounts.Bytes;

        internal static async Task<LabHost> StartAsync(string hostId)
        {
            var host = new LabHost { HostId = hostId, certificate = Certificate(), port = FreePort() };
            host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
            try
            {
                await host.StartAsync();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        /// <summary>Starts the gateway on the same address, so the roster's entry for it stays true across a restart.</summary>
        internal async Task StartAsync()
        {
            var origin = new GatewayOrigin(Origin);
            Server = new GatewayServer(Identity, origin, [], this);
            Server.AttachAccountStorage(accounts);
            Server.AttachNetworkStorage(network);
            listener = await Server.StartAsync(new GatewayTlsBinding(origin, Identity, certificate), new KestrelGatewayListenerFactory());
        }

        internal async Task StopAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            listener = null;
        }

        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            certificate?.Dispose();
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
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Martlet account rehearsal (fixture)", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
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
