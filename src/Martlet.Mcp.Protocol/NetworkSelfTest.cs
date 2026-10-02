using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.Mcp;

/// <summary>
/// Rehearses the Martlet network end to end on this PC with the production code: three real gateways (Kestrel, pinned TLS,
/// volatile credentials) on 127.0.0.1 and two simulated desktops that drive the desktop's own client and sync engine. It
/// founds a network, binds hosts, joins a second desktop with a check number, pairs every member with every host by
/// itself, refuses forged keys and rosters, and removes a desktop and a host (revoking their access). Nothing leaves
/// loopback, nothing is written to disk or the credential vault, and every key is thrown away at the end.
/// </summary>
internal static class NetworkSelfTest
{
    internal static async Task<object> RunAsync(CancellationToken token)
    {
        var steps = new List<Step>();
        var started = DateTimeOffset.UtcNow;
        await using var h1 = await LabHost.StartAsync("lab-host-1");
        await using var h2 = await LabHost.StartAsync("lab-host-2");
        await using var h3 = await LabHost.StartAsync("lab-host-3");
        using var keyA = NetworkKey.Create("lab-desktop-a");
        using var keyB = NetworkKey.Create("lab-desktop-b");
        var a = new LabDesktop(keyA, "LAB-A");
        var b = new LabDesktop(keyB, "LAB-B");

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

        await Run("A pairs with lab-host-1 by a typed code and founds a network that binds it", async () =>
        {
            await a.PairByCodeAsync(h1, token);
            var result = await a.SyncAsync(token);
            return (a.State.Roster is { } r && r.Host(h1.HostId) is { Removed: false } && h1.State == "bound" && h1.Saves > 0,
                $"network {a.State.Roster?.NetworkId}; lab-host-1 {h1.State}; {string.Join(" ", result.Events)}");
        });
        await Run("A adds lab-host-2 (as Add this computer over SSH does) and it joins the same network", async () =>
        {
            await a.PairByCodeAsync(h2, token);
            await a.SyncAsync(token);
            return (h2.State == "bound" && h2.NetworkId == a.State.Roster?.NetworkId, $"lab-host-2 {h2.State} in {h2.NetworkId}");
        });
        string? checkB = null;
        await Run("B pairs with lab-host-1 only and asks to join, showing a check number", async () =>
        {
            await b.PairByCodeAsync(h1, token);
            var result = await b.SyncAsync(token);
            checkB = b.State.Waiting?.CheckNumber;
            return (b.State.Roster is null && checkB is { Length: 7 }, $"waiting on {b.State.Waiting?.HostId} with check {checkB}");
        });
        HostJoinRequest? join = null;
        await Run("A sees B's request with the same check number", async () =>
        {
            var result = await a.SyncAsync(token);
            join = result.Joins.FirstOrDefault(j => j.DeviceId == keyB.DeviceId);
            return (join is not null && join.CheckNumber == checkB, $"A sees {join?.DeviceId} with check {join?.CheckNumber}");
        });
        await Run("A allows B; B becomes a member and pairs with lab-host-2 by itself (no code)", async () =>
        {
            a.Approve(join ?? throw new InvalidOperationException("No join request to allow."));
            await a.SyncAsync(token);
            var result = await b.SyncAsync(token);
            var works = await b.CanUseAsync(h2.HostId, token);
            return (b.State.Roster?.Trusts(keyB.DeviceId, keyB.PublicKey) == true && b.Has(h2.HostId) && works,
                $"B member: {b.State.Roster is not null}; paired: {string.Join(", ", b.HostIds)}; signed request to lab-host-2: {(works ? "accepted" : "refused")}. {string.Join(" ", result.Events)}");
        });
        await Run("A sets up a new Linux host (lab-host-3); B is paired with it on its next sync", async () =>
        {
            await a.PairByCodeAsync(h3, token);
            await a.SyncAsync(token);
            await b.SyncAsync(token);
            var works = await b.CanUseAsync(h3.HostId, token);
            return (h3.State == "bound" && b.Has(h3.HostId) && works, $"lab-host-3 {h3.State}; B paired with it: {b.Has(h3.HostId)}; request {(works ? "accepted" : "refused")}");
        });
        await Run("A desktop outside the network can't pair by itself", async () =>
        {
            using var intruder = NetworkKey.Create("lab-intruder");
            var code = await FailureAsync(() => HostNetworkPairing.PairAsMemberAsync(a.State.Roster!.Host(h2.HostId)!, a.State.Roster.NetworkId,
                intruder, "INTRUDER", token));
            return (code == "network.denied", $"refused with {code}");
        });
        await Run("A member's device ID with the wrong key can't pair", async () =>
        {
            using var impostor = NetworkKey.Create(keyA.DeviceId);
            var code = await FailureAsync(() => HostNetworkPairing.PairAsMemberAsync(a.State.Roster!.Host(h3.HostId)!, a.State.Roster.NetworkId,
                impostor, "IMPOSTOR", token));
            return (code == "pairing.invalid", $"refused with {code}");
        });
        await Run("A roster entry not signed by a member is refused by the host", async () =>
        {
            using var intruder = NetworkKey.Create("lab-intruder");
            var foreign = NetworkRoster.Found(intruder, "INTRUDER", DateTimeOffset.UtcNow).Members[0];
            var forged = a.State.Roster! with { Members = a.State.Roster.Members.Append(foreign).ToArray() };
            var merge = NetworkRoster.Accept(a.State.Roster, forged);
            var view = await a.MergeAsync(h1.HostId, forged, token);
            return (merge.Rejected == 1 && view.Roster?.Desktop("lab-intruder") is null && h1.Roster?.Desktop("lab-intruder") is null,
                $"rejected locally: {merge.Rejected}; on lab-host-1: {(h1.Roster?.Desktop("lab-intruder") is null ? "absent" : "PRESENT")}");
        });
        await Run("A removes lab-host-3: it stops trusting the network's desktops, and A and B both forget it", async () =>
        {
            a.Remove(NetworkKinds.Host, h3.HostId);
            var pairing = a.Pairing(h3.HostId);
            await a.SyncAsync(token);
            var result = await b.SyncAsync(token);
            var code = await FailureAsync(async () =>
            {
                using var connection = new Audio2FaceHostConnection(pairing.Pairing, pairing.Secret);
                return await connection.ReadNetworkAsync(token);
            });
            return (h3.State == "removed" && !a.Has(h3.HostId) && !b.Has(h3.HostId) && code is "auth.revoked" or "auth.invalid",
                $"lab-host-3 {h3.State}; A still paired: {a.Has(h3.HostId)}; B still paired: {b.Has(h3.HostId)}; A's old pairing: {code}. {string.Join(" ", result.Events)}");
        });
        await Run("A pairs lab-host-3 again by a code: it rejoins the network and B is paired with it again by itself", async () =>
        {
            await a.PairByCodeAsync(h3, token);
            await a.SyncAsync(token);
            await b.SyncAsync(token);
            var works = await b.CanUseAsync(h3.HostId, token);
            return (h3.State == "bound" && a.State.Roster?.Host(h3.HostId) is { Removed: false } && works,
                $"lab-host-3 {h3.State}; B paired with it: {b.Has(h3.HostId)}; request {(works ? "accepted" : "refused")}");
        });
        await Run("A removes B: every host revokes B, and B leaves the network and forgets its hosts", async () =>
        {
            a.Remove(NetworkKinds.Desktop, keyB.DeviceId);
            await a.SyncAsync(token);
            var revoked = await b.FailureCodeAsync(h2.HostId, token);
            var result = await b.SyncAsync(token);
            return (revoked is "auth.revoked" or "auth.invalid" && b.State.Roster is null && result.RetireKey && b.HostIds.Count == 0,
                $"B on lab-host-2: {revoked}; B in network: {b.State.Roster is not null}; new key needed: {result.RetireKey}; B still paired: {b.HostIds.Count}");
        });

        return new
        {
            ok = steps.All(s => s.Ok),
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "In-process: three real gateways on 127.0.0.1 (Kestrel, pinned TLS, volatile credentials) and two simulated desktops " +
                "using the desktop's network client and sync engine. Not covered: the desktop window, Windows Credential Manager, " +
                "network.json on a Linux host, martlet-host, SSH and a real LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        };
    }

    private static async Task<string?> FailureAsync<T>(Func<Task<T>> action)
    {
        try { await action(); return null; }
        catch (Audio2FaceHostException error) { return error.Code; }
    }

    private sealed record Step(string Name, bool Ok, string Detail);

    /// <summary>A simulated desktop: its network key, its pairings (secrets in memory) and its network.json state.</summary>
    private sealed class LabDesktop(NetworkKey key, string name)
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly NetworkSyncEngine engine = new(key, name);
        internal NetworkLocalState State { get; private set; } = NetworkLocalState.Empty;
        internal IReadOnlyCollection<string> HostIds => pairings.Keys;
        internal bool Has(string hostId) => pairings.ContainsKey(hostId);
        internal (Audio2FaceHostPairing Pairing, string Secret) Pairing(string hostId) => pairings[hostId];

        internal async Task PairByCodeAsync(LabHost host, CancellationToken token)
        {
            var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, card.Code.Reveal(), key.DeviceId, name, token);
            pairings[pairing.HostId] = (pairing, secret);
            State = State.WithAdopted(pairing.HostId);
        }

        internal async Task<NetworkSyncResult> SyncAsync(CancellationToken token)
        {
            var result = await engine.SyncAsync(State, pairings.Values.Select(p => p.Pairing).ToArray(),
                pairing => new Audio2FaceHostConnection(pairing, pairings[pairing.HostId].Secret), token);
            foreach (var (pairing, secret) in result.Paired) pairings[pairing.HostId] = (pairing, secret);
            foreach (var id in result.Forget) pairings.Remove(id);
            // Through network.json's format, as the desktop keeps it between syncs.
            State = NetworkLocalState.Parse(result.State.Write());
            return result;
        }

        internal void Approve(HostJoinRequest join) => State = engine.Approve(State, join);
        internal void Remove(string kind, string id) => State = engine.Remove(State, kind, id);

        internal async Task<HostNetworkView> MergeAsync(string hostId, NetworkRoster roster, CancellationToken token)
        {
            using var connection = new Audio2FaceHostConnection(pairings[hostId].Pairing, pairings[hostId].Secret);
            return await connection.MergeNetworkAsync(roster, token);
        }

        internal async Task<bool> CanUseAsync(string hostId, CancellationToken token) =>
            pairings.ContainsKey(hostId) && await FailureCodeAsync(hostId, token) is null;

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

    /// <summary>A real gateway on 127.0.0.1 with a throwaway certificate and an in-memory network.json.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayNetworkStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private byte[]? saved;
        internal GatewayServer Server { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal int Saves { get; private set; }
        internal string State => Server.NetworkState.State;
        internal string? NetworkId => Server.NetworkState.NetworkId;
        internal NetworkRoster? Roster => saved is null ? null : NetworkRoster.Parse(saved);

        internal static async Task<LabHost> StartAsync(string hostId)
        {
            var host = new LabHost { HostId = hostId };
            try
            {
                host.certificate = Certificate();
                host.Origin = $"https://127.0.0.1:{FreePort()}";
                var origin = new GatewayOrigin(host.Origin);
                var identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
                host.Server = new GatewayServer(identity, origin, [], host);
                host.Server.AttachNetworkStorage(host);
                host.listener = await host.Server.StartAsync(new GatewayTlsBinding(origin, identity, host.certificate), new KestrelGatewayListenerFactory());
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public byte[]? Load() => null;
        public void Save(byte[] bytes)
        {
            saved = bytes;
            Saves++;
        }
        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
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
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Martlet network rehearsal", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var now = DateTimeOffset.UtcNow;
            using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            // Windows TLS needs the private key in a key container; it is deleted when the certificate is disposed.
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pfx = ephemeral.Export(X509ContentType.Pkcs12, password);
            try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
}
