using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.HostClient;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses a host whose computer got another address on the home network (docs/NETWORK.md, "When a computer gets a new
/// address") with the production code: one real gateway (Kestrel, pinned TLS) on 127.0.0.3 whose certificate also names the
/// address it was paired at (127.0.0.2, where nothing answers now), the real find answer (<see cref="HostFindResponder"/>, UDP)
/// and a desktop's real paired connection (<see cref="HostRoutes"/>). Checks that the desktop finds the host at its new address
/// by itself and keeps it, dials it there at once next time, never uses a computer with another key that claims the host's ID,
/// and finds the host back at its paired address. Loopback only (finds go to 127.0.0.x, not the home network); writes nothing.
/// </summary>
internal static class MoveRehearsal
{
    private static readonly IPAddress Paired = IPAddress.Parse("127.0.0.2");
    private static readonly IPAddress Now = IPAddress.Parse("127.0.0.3");
    private static readonly IPAddress ImpostorPaired = IPAddress.Parse("127.0.0.5");
    private static readonly IPAddress ImpostorAt = IPAddress.Parse("127.0.0.4");

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var moves = new List<HostMove>();
        void OnMoved(HostMove move) { lock (moves) moves.Add(move); }

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

        await using var host = await LabHost.StartAsync("lab-moved-host", Now, [Paired, Now]);
        using var answers = HostFindResponder.Start(() => [host.HostId], Now, 0);
        await using var impostor = await LabHost.StartAsync("lab-impostor", ImpostorAt, [ImpostorPaired, ImpostorAt]);
        using var impostorAnswers = HostFindResponder.Start(() => ["lab-moved-host"], ImpostorAt, 0);
        // Finds go to the lab's answers on loopback, never to the home network; this PC's own addresses play no part.
        HostRoutes.Find = (wanted, window, find) => HostFind.FindAsync(wanted, window, find, [Now], answers.Port);
        HostRoutes.OwnAddresses = () => [];
        HostRoutes.Moved += OnMoved;
        try
        {
            var port = host.Port;
            var pairedOrigin = $"https://{Paired}:{port}";
            HostPairing? saved = null;
            string? secret = null;

            await Run("A desktop pairs with the host over pinned TLS", async () =>
            {
                var card = host.Server.Pairing.OpenWindow(new() { DeviceId = "lab-desktop", DisplayName = "LAB-DESKTOP", Roles = [GatewayRole.Voice] });
                (saved, secret) = await HostTransport.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, "lab-desktop",
                    card.PairingId, card.Token.Reveal(), token);
                return (saved.HostId == host.HostId, $"paired {saved.DeviceId} with {saved.HostId} (pin {saved.SpkiFingerprint[..15]}...)");
            });

            await Run("The host answers a find with its host ID only: no addresses, keys or other data", async () =>
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                await udp.SendAsync(HostFind.Query("lab-query"), new IPEndPoint(Now, answers.Port), token);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(TimeSpan.FromSeconds(2));
                var answer = await udp.ReceiveAsync(wait.Token);
                using var document = JsonDocument.Parse(answer.Buffer);
                var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
                // Martlet Nearby's own find (desktops sharing hosts) is not a host find: no answer.
                await udp.SendAsync(Encoding.UTF8.GetBytes("{\"martlet\":\"find\",\"v\":1,\"q\":\"nearby\"}"), new IPEndPoint(Now, answers.Port), token);
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(token);
                quiet.CancelAfter(TimeSpan.FromMilliseconds(500));
                var nearbyAnswered = true;
                try { await udp.ReceiveAsync(quiet.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { nearbyAnswered = false; }
                return (names.SequenceEqual(["martlet", "v", "q", "hosts"]) && HostFind.ParseAnswer(answer.Buffer, "lab-query") is [var id] &&
                        id == host.HostId && !nearbyAnswered,
                    $"answer fields: {string.Join(", ", names)}; hosts: {string.Join(", ", HostFind.ParseAnswer(answer.Buffer, "lab-query") ?? [])}; " +
                    $"Nearby's find answered: {nearbyAnswered}");
            });

            await Run("The host's computer got another address: the desktop finds it there by itself, with the same key and pairing", async () =>
            {
                // As HostRegistry.Load does for every paired host: its ID, pinned key and no new address yet.
                HostRoutes.Remember(pairedOrigin, host.HostId, saved!.SpkiFingerprint, null);
                using var desktop = new HostConnection(saved with { Origin = pairedOrigin }, secret);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var routes = await desktop.ReadRoutesAsync(token);
                var ms = clock.ElapsedMilliseconds;
                var route = HostRoutes.For(pairedOrigin, saved.SpkiFingerprint);
                HostMove[] seen;
                lock (moves) seen = [.. moves];
                return (route is { Route: "home" } && route.MovedTo == $"{Now}:{port}" && route.Address == $"{Now}:{port}" &&
                        seen.Any(m => m.HostId == host.HostId && m.To == $"{Now}:{port}" && m.Origin == pairedOrigin),
                    $"paired at {Paired}:{port} (nothing answers there); read {routes.Count} route(s) in {ms} ms; route {route?.Route} at " +
                    $"{route?.Address}, moved to {route?.MovedTo}; moves raised: {string.Join("; ", seen.Select(m => $"{m.HostId} {m.From ?? "paired"} -> {m.To}"))}; " +
                    $"the host answered {answers.Answered} find(s)");
            });

            await Run("Next time the desktop dials the new address at once, never waiting on the old one", async () =>
            {
                // A new pool of kept connections (its own clock), so this dials again rather than reuse the one just made.
                using var desktop = new HostConnection(saved! with { Origin = pairedOrigin }, secret, new FreshPool());
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await desktop.ReadRoutesAsync(token);
                var ms = clock.ElapsedMilliseconds;
                var finds = answers.Answered;
                return (ms < HostRoutes.HomeConnectWindow.TotalMilliseconds && HostRoutes.For(pairedOrigin, saved!.SpkiFingerprint)?.Address == $"{Now}:{port}",
                    $"connected in {ms} ms (the old address would have waited {HostRoutes.HomeConnectWindow.TotalMilliseconds:0} ms first); finds answered so far {finds}");
            });

            await Run("The pinned-key check (MCP outside_reachability_check, Check host) reaches the host at its new address too", async () =>
            {
                var probe = await HostRoutes.ProbeAsync(pairedOrigin, saved!.SpkiFingerprint, null, TimeSpan.FromSeconds(4), token);
                return (probe is { Reachable: true, Problem: null }, $"reachable {probe.Reachable} in {probe.Milliseconds} ms{(probe.Problem is null ? "" : ", " + probe.Problem)}");
            });

            await Run("A computer with another key that answers for the host's ID is never used", async () =>
            {
                var impostorOrigin = $"https://{ImpostorPaired}:{impostor.Port}";
                HostRoutes.Find = (wanted, window, find) => HostFind.FindAsync(wanted, window, find, [ImpostorAt], impostorAnswers.Port);
                HostRoutes.Remember(impostorOrigin, host.HostId, saved!.SpkiFingerprint, null);
                using var desktop = new HostConnection(saved with { Origin = impostorOrigin }, secret);
                string? code = null, message = null;
                try { await desktop.ReadRoutesAsync(token); }
                catch (HostConnectionException error) { (code, message) = (error.Code, error.Message); }
                var route = HostRoutes.For(impostorOrigin, saved.SpkiFingerprint);
                return (code == "host.unreachable" && route?.MovedTo is null && impostorAnswers.Answered > 0,
                    $"the other computer answered {impostorAnswers.Answered} find(s); the desktop got {code} ({message}); moved to {route?.MovedTo ?? "nowhere"}");
            });

            await Run("The host is back at its paired address: the desktop finds it there and forgets the other address", async () =>
            {
                HostRoutes.Find = (wanted, window, find) => HostFind.FindAsync(wanted, window, find, [Now], answers.Port);
                // Paired at 127.0.0.3 (where it answers again) and last found at an address where nothing answers now.
                HostRoutes.Remember(host.Origin, host.HostId, saved!.SpkiFingerprint, $"127.0.0.9:{port}");
                using var desktop = new HostConnection(saved, secret, new FreshPool());
                await desktop.ReadRoutesAsync(token);
                var route = HostRoutes.For(host.Origin, saved.SpkiFingerprint);
                HostMove[] seen;
                lock (moves) seen = [.. moves];
                return (route is { Route: "home", MovedTo: null } && route.Address == $"{Now}:{port}" &&
                        seen.Any(m => m.Origin == host.Origin && m.To is null),
                    $"route {route?.Route} at {route?.Address}, moved to {route?.MovedTo ?? "(its paired address)"}; moves raised: " +
                    string.Join("; ", seen.Select(m => $"{m.HostId} {m.From ?? "paired"} -> {m.To ?? "paired"}")));
            });
        }
        finally
        {
            HostRoutes.Moved -= OnMoved;
            HostRoutes.Reset();
        }

        var passed = steps.All(s => s.Ok);
        return (passed, new
        {
            passed,
            steps = steps.Select(s => new { name = s.Name, ok = s.Ok, detail = s.Detail }),
            notRun = new[]
            {
                "A real DHCP change: the gateway following its computer to a new home address (GatewayHomeListeners) and the Docker " +
                "keeper re-publishing its port need another address on a real network adapter; covered by unit tests and the Docker " +
                "keeper check in docs/VALIDATION.md."
            }
        });
    }

    /// <summary>The system clock under its own name: a paired connection made with it has its own pool of kept connections.</summary>
    private sealed class FreshPool : TimeProvider;

    private sealed class LabHost : IAsyncDisposable, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal int Port { get; private set; }

        /// <summary>A gateway on <paramref name="at"/> whose certificate names each of <paramref name="names"/>.</summary>
        internal static async Task<LabHost> StartAsync(string hostId, IPAddress at, IPAddress[] names)
        {
            var host = new LabHost { HostId = hostId, certificate = Certificate(names) };
            host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
            try
            {
                host.Port = FreePort(at, names);
                host.Origin = $"https://{at}:{host.Port}";
                var origin = new GatewayOrigin(host.Origin);
                host.Server = new GatewayServer(host.Identity, origin, [], host);
                host.listener = await host.Server.StartAsync(new GatewayTlsBinding(origin, host.Identity, host.certificate),
                    new KestrelGatewayListenerFactory());
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            certificate?.Dispose();
        }

        /// <summary>A port free on <paramref name="at"/> and on each of <paramref name="names"/> (nothing answers at the old address).</summary>
        private static int FreePort(IPAddress at, IPAddress[] names)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var probe = new TcpListener(at, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                if (names.Where(n => !n.Equals(at)).All(n => Free(n, port))) return port;
            }
            throw new InvalidOperationException("No free port on the lab addresses.");
        }

        private static bool Free(IPAddress address, int port)
        {
            try
            {
                var probe = new TcpListener(address, port);
                probe.Start();
                probe.Stop();
                return true;
            }
            catch (SocketException) { return false; }
        }

        private static X509Certificate2 Certificate(IPAddress[] names)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Martlet move rehearsal (fixture)", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var builder = new SubjectAlternativeNameBuilder();
            foreach (var name in names) builder.AddIpAddress(name);
            request.CertificateExtensions.Add(builder.Build());
            var now = DateTimeOffset.UtcNow;
            using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pfx = ephemeral.Export(X509ContentType.Pkcs12, password);
            try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
}
