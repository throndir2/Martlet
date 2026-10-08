using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses how the desktop connects to a paired host and reports its status, with the production code: a real gateway
/// (Kestrel, pinned TLS, signed requests) on 127.0.0.1 behind a loopback TCP forwarder that this check stops and starts at
/// the same address (a host that goes away and comes back), and a simulated desktop that checks the host as the desktop's
/// 15-second sync does (a new paired connection per check: its routes, then its copy of the plan) and logs through
/// <see cref="HostAnswers"/> as the desktop does. Checks that the checks share one kept TCP and TLS connection, that a host
/// missing one check is not reported, that a host that stops is reported once (refused, named as such) and once again when
/// it is back, and that a host flapping between single misses is never reported. Nothing leaves loopback.
/// </summary>
internal static class HostConnectionRehearsal
{
    private const string HostId = "lab-connections";

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        using var certificate = Certificate();
        var identity = GatewayHostIdentity.FromCertificate(HostId, certificate);
        var gatewayOrigin = new GatewayOrigin($"https://127.0.0.1:{FreePort()}");
        var server = new GatewayServer(identity, gatewayOrigin, [], new NoAudit());
        await using var listener = await server.StartAsync(new GatewayTlsBinding(gatewayOrigin, identity, certificate),
            new KestrelGatewayListenerFactory(), token);
        await using var forwarder = new Forwarder(FreePort(), gatewayOrigin.Port);
        forwarder.Start();
        // The desktop reaches the host at the forwarder's address; the gateway's key and the signed requests are unchanged.
        var origin = $"https://127.0.0.1:{forwarder.Port}";
        Audio2FaceHostPairing pairing = null!;
        var secret = "";
        var answers = new HostAnswers(2);
        var log = new List<string>();

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

        // One check as the desktop's sync makes it (ClusterSync.ProbeAsync): a new paired connection, the host's routes, then its
        // copy of the plan; a late plan copy doesn't count as a miss. Logged as the desktop logs it.
        async Task<(bool Answered, string Text, long Milliseconds)> CheckAsync()
        {
            var clock = Stopwatch.StartNew();
            (bool, string) outcome;
            using (var connection = new Audio2FaceHostConnection(pairing, secret))
            {
                try
                {
                    await connection.ReadRoutesAsync(token);
                    try { await connection.ReadClusterAsync(token); }
                    catch (Exception error) when (error is Audio2FaceHostException or IOException or HttpRequestException ||
                        error is OperationCanceledException && !token.IsCancellationRequested) { }
                    outcome = (true, "");
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { outcome = (false, "Didn't respond in time."); }
                catch (Exception error) when (error is Audio2FaceHostException or IOException or HttpRequestException)
                {
                    outcome = (false, error.Message);
                }
            }
            if (answers.Record(HostId, outcome.Item1, outcome.Item2) is { } change) log.Add((change.Answering ? "INFO " : "WARN ") + change.Text);
            return (outcome.Item1, outcome.Item2, clock.ElapsedMilliseconds);
        }

        long Connections() => HostRoutes.For(origin)?.Connections ?? 0;

        await Run("The desktop pairs with a real gateway on 127.0.0.1 (pinned TLS) and reaches it through the forwarder", async () =>
        {
            var card = server.Pairing.OpenWindow(new() { DeviceId = "lab-desktop", DisplayName = "LAB-DESKTOP", Roles = [GatewayRole.Voice] });
            var (paired, pairedSecret) = await Audio2FaceHostClient.PairAsync(gatewayOrigin.CanonicalOrigin, HostId, identity.SpkiFingerprint,
                "lab-desktop", card.PairingId, card.Token.Reveal(), token);
            pairing = paired with { Origin = origin };
            secret = pairedSecret;
            var first = await CheckAsync();
            return (first.Answered && log.Count == 0, $"paired; first check answered in {first.Milliseconds} ms");
        });

        await Run("Twelve regular checks, each with a new paired connection, share one kept TCP and TLS connection", async () =>
        {
            var before = Connections();
            var accepted = forwarder.Accepted;
            var times = new List<long>();
            for (var i = 0; i < 12; i++)
            {
                var check = await CheckAsync();
                if (!check.Answered) return (false, $"check {i + 1} failed: {check.Text}");
                times.Add(check.Milliseconds);
            }
            var opened = Connections() - before;
            var reached = forwarder.Accepted - accepted;
            return (opened == 0 && reached == 0 && log.Count == 0,
                $"new connections dialed by the desktop: {opened}; new TCP connections the host side accepted: {reached}; " +
                $"checks took {times.Min()}-{times.Max()} ms (connections dialed since the start: {Connections()})");
        });

        await Run("The host stops: one missed check is not a change; the second is logged once (refused), later misses add nothing", async () =>
        {
            await forwarder.StopAsync();
            var checks = new List<string>();
            var lines = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var before = log.Count;
                var check = await CheckAsync();
                checks.Add($"{(check.Answered ? "answered" : "missed")} in {check.Milliseconds} ms");
                lines.Add(log.Count > before ? log[^1] : "(no line)");
            }
            var route = HostRoutes.For(origin);
            var ok = lines[0] == "(no line)" && lines[1].StartsWith($"WARN Host {HostId} stopped answering: ", StringComparison.Ordinal) &&
                lines[2] == "(no line)" && lines[3] == "(no line)" && !answers.Answering(HostId) &&
                route is { Route: "none", Error: { } why } && why.Contains("didn't answer (refused)", StringComparison.Ordinal);
            return (ok, $"checks: {string.Join(", ", checks)}; log: {string.Join(" | ", lines)}; route status: {route?.Route}, {route?.Error}");
        });

        await Run("The host is back at the same address: the next check answers and 'answers again' is logged once", async () =>
        {
            var before = Connections();
            forwarder.Start();
            var lines = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var count = log.Count;
                var check = await CheckAsync();
                if (!check.Answered) return (false, $"check {i + 1} after the restart failed: {check.Text}");
                lines.Add(log.Count > count ? log[^1] : "(no line)");
            }
            var opened = Connections() - before;
            return (lines[0] == $"INFO Host {HostId} answers again." && lines[1] == "(no line)" && lines[2] == "(no line)" && opened == 1 &&
                answers.Answering(HostId), $"log: {string.Join(" | ", lines)}; new connections: {opened}; route {HostRoutes.For(origin)?.Route}");
        });

        await Run("A busy host that misses every other check is never reported and keeps counting as answering", async () =>
        {
            var count = log.Count;
            var outcomes = new List<string>();
            for (var i = 0; i < 6; i++)
            {
                if (i % 2 == 0) await forwarder.StopAsync();
                else forwarder.Start();
                var check = await CheckAsync();
                outcomes.Add(check.Answered ? "answered" : "missed");
            }
            forwarder.Start();
            return (log.Count == count && answers.Answering(HostId) && outcomes.Count(o => o == "missed") == 3,
                $"checks: {string.Join(", ", outcomes)}; log lines added: {log.Count - count}");
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "One real gateway on 127.0.0.1 (Kestrel, pinned TLS, signed requests) behind a loopback TCP forwarder stopped and " +
                "started at the same address, and a simulated desktop using the desktop's paired client (Audio2FaceHostConnection, " +
                "HostRoutes) and its status tracker (HostAnswers) as the desktop's 15-second sync uses them. Not covered: the desktop " +
                "window itself, real computers on a LAN, Docker's port proxy, and Windows running out of ports (NoBufferSpaceAvailable), " +
                "whose wording unit tests check.",
            log,
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
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
        var request = new CertificateRequest("CN=Martlet host connections rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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

    private sealed class NoAudit : IGatewayAuditSink
    {
        public void Record(GatewayAuditEvent gatewayEvent) { }
    }

    /// <summary>Forwards TCP connections from 127.0.0.1:<see cref="Port"/> to the gateway. Stopping it closes the port and every
    /// forwarded connection (a host that went away); starting it listens on the same port again.</summary>
    private sealed class Forwarder(int port, int target) : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly List<TcpClient> open = [];
        private TcpListener? listener;
        private CancellationTokenSource? stop;
        private Task? accepting;
        private int accepted;

        internal int Port => port;
        internal int Accepted => Volatile.Read(ref accepted);

        internal void Start()
        {
            if (listener is not null) return;
            var next = new TcpListener(IPAddress.Loopback, port);
            next.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            next.Start();
            listener = next;
            stop = new CancellationTokenSource();
            accepting = AcceptAsync(next, stop.Token);
        }

        internal async Task StopAsync()
        {
            if (listener is null) return;
            await stop!.CancelAsync();
            listener.Stop();
            listener = null;
            try { await accepting!; }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
            lock (gate)
            {
                foreach (var client in open) client.Dispose();
                open.Clear();
            }
            stop.Dispose();
        }

        private async Task AcceptAsync(TcpListener from, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var inbound = await from.AcceptTcpClientAsync(token);
                Interlocked.Increment(ref accepted);
                _ = PumpAsync(inbound, token);
            }
        }

        private async Task PumpAsync(TcpClient inbound, CancellationToken token)
        {
            var outbound = new TcpClient(AddressFamily.InterNetwork);
            lock (gate)
            {
                open.Add(inbound);
                open.Add(outbound);
            }
            try
            {
                await outbound.ConnectAsync(IPAddress.Loopback, target, token);
                var a = inbound.GetStream();
                var b = outbound.GetStream();
                await Task.WhenAny(a.CopyToAsync(b, token), b.CopyToAsync(a, token));
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                inbound.Dispose();
                outbound.Dispose();
                lock (gate)
                {
                    open.Remove(inbound);
                    open.Remove(outbound);
                }
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
