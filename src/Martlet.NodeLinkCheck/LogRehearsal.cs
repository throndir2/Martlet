using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Logs;
using Martlet.Diagnostics;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses shared logs (docs/DIAGNOSTICS.md: every computer's logs on every computer, no log host to choose) end to end on
/// this PC with the production code: two real gateways (Kestrel, pinned TLS) on 127.0.0.1 with an in-memory logs.json, and
/// three simulated desktops, each with a real logs folder (desktop.log written in Martlet's format and read with LocalLogs),
/// the desktop's paired client (HostLogPeer) and the real sharing engine (Martlet.Core.Logs.LogShare), wired the way the
/// desktop wires them. Lines are synthetic; nothing leaves loopback and the folder is deleted afterwards.
/// </summary>
internal static class LogRehearsal
{
    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "martlet-log-rehearsal-" + Guid.NewGuid().ToString("N"));
        await using var h1 = await LabHost.StartAsync("lab-logs-1");
        await using var h2 = await LabHost.StartAsync("lab-logs-2");
        LabHost[] both = [h1, h2];
        var a = new LabDesktop("lab-desktop-a", Path.Combine(root, "a"));
        var b = new LabDesktop("lab-desktop-b", Path.Combine(root, "b"));
        var c = new LabDesktop("lab-desktop-c", Path.Combine(root, "c"));
        string[] everyone = [a.DeviceId, b.DeviceId, c.DeviceId, h1.HostId, h2.HostId];

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

        static int Count(IEnumerable<LogRecord> lines, string text) => lines.Count(l => l.Message.Contains(text, StringComparison.Ordinal));

        try
        {
            await Run("Desktops A and B pair with lab-logs-1 and lab-logs-2; desktop C pairs only with lab-logs-2 (signed, pinned connections)", async () =>
            {
                foreach (var host in both)
                {
                    await a.PairAsync(host);
                    await b.PairAsync(host);
                }
                await c.PairAsync(h2);
                return (true, "A and B paired with both hosts, C with lab-logs-2 only");
            });

            await Run("A writes three lines (an error with its stack trace among them) and shares: both hosts keep them, and A now holds both hosts' own lines", async () =>
            {
                a.Write("INFO", "A: Martlet started.");
                a.Write("ERROR", "A: Unhandled UI exception\nSystem.InvalidOperationException: fixture\n   at Lab.Throw()");
                a.Write("WARN", "A: Host lab-logs-2 stopped answering.");
                var result = await a.ShareAsync(both, token);
                var onH1 = await a.ReadHostAsync(h1, token);
                var onH2 = await a.ReadHostAsync(h2, token);
                var mine = new[] { "A: Martlet started.", "A: Unhandled UI exception", "A: Host lab-logs-2 stopped" };
                var kept = mine.All(t => Count(onH1, t) == 1 && Count(onH2, t) == 1);
                var trace = onH1.Any(l => l.Level == "ERROR" && l.Message.Contains("at Lab.Throw()", StringComparison.Ordinal));
                var sources = a.Share.Logs.Snapshot().Select(l => l.Source).Distinct().Order().ToArray();
                return (result.Shared == 2 && kept && trace && sources.Contains(h1.HostId) && sources.Contains(h2.HostId),
                    $"{result.Describe()} Both hosts keep A's three lines once: {kept}; stack trace kept: {trace}; A holds lines from {string.Join(", ", sources)}");
            });

            await Run("C reaches only lab-logs-2; A's next run passes C's lines on to lab-logs-1, and each host's own lines to the other host", async () =>
            {
                c.Write("INFO", "C: Listening started.");
                var cResult = await c.ShareAsync([h2], token);
                var before = Count(await a.ReadHostAsync(h1, token), "C: Listening started.");
                await a.ShareAsync(both, token);
                var onH1 = await a.ReadHostAsync(h1, token);
                var onH2 = await a.ReadHostAsync(h2, token);
                var relayed = onH1.SingleOrDefault(l => l.Message == "C: Listening started.");
                var gatewaysCrossed = onH1.Any(l => l.Source == h2.HostId) && onH2.Any(l => l.Source == h1.HostId);
                return (cResult.Shared == 1 && before == 0 && relayed is { RelayedBy: "lab-desktop-a" } && gatewaysCrossed,
                    $"C: {cResult.Describe()} lab-logs-1 had C's line before A's run: {before > 0}; after: {relayed is not null} (passed on by " +
                    $"{relayed?.RelayedBy ?? "nobody"}); each host holds the other host's own lines: {gatewaysCrossed}");
            });

            await Run("B, a new computer, gets every computer's lines and gives its own to both hosts; C gets B's lines through lab-logs-2", async () =>
            {
                b.Write("INFO", "B: Martlet started.");
                var result = await b.ShareAsync(both, token);
                var sources = b.Share.Logs.Snapshot().Select(l => l.Source).Append(b.DeviceId).Distinct().ToHashSet();
                var onHosts = Count(await a.ReadHostAsync(h1, token), "B: Martlet started.") == 1 &&
                    Count(await a.ReadHostAsync(h2, token), "B: Martlet started.") == 1;
                await c.ShareAsync([h2], token);
                var cSources = c.Share.Logs.Snapshot().Select(l => l.Source).Append(c.DeviceId).Distinct().ToHashSet();
                return (everyone.All(sources.Contains) && onHosts && everyone.All(cSources.Contains),
                    $"{result.Describe()} B holds lines from {sources.Count} computers ({string.Join(", ", sources.Order())}); B's line on both hosts: {onHosts}; " +
                    $"C holds lines from {cSources.Count} computers");
            });

            await Run("Every line is kept once however often it is delivered: A, B and C share twice more and no host or desktop holds a duplicate", async () =>
            {
                var sent = 0;
                for (var round = 0; round < 2; round++)
                {
                    sent += (await a.ShareAsync(both, token)).Sent;
                    sent += (await b.ShareAsync(both, token)).Sent;
                    sent += (await c.ShareAsync([h2], token)).Sent;
                }
                static int Duplicates(IEnumerable<LogRecord> lines) => lines.GroupBy(l => (l.Stream, l.Seq)).Count(g => g.Count() > 1);
                var duplicates = Duplicates(await a.ReadHostAsync(h1, token)) + Duplicates(await a.ReadHostAsync(h2, token)) +
                    Duplicates(a.Share.Logs.Snapshot()) + Duplicates(b.Share.Logs.Snapshot()) + Duplicates(c.Share.Logs.Snapshot());
                var markers = new[] { "A: Martlet started.", "B: Martlet started.", "C: Listening started." };
                var once = markers.All(m => Count(b.Share.Logs.Snapshot(), m) + (m.StartsWith("B:", StringComparison.Ordinal) ? 1 : 0) == 1);
                return (duplicates == 0 && once, $"lines the hosts kept from these runs: {sent}; duplicates anywhere: {duplicates}; each marker once on B: {once}");
            });

            await Run("A host that was down catches up: lab-logs-1 stops while A and C write; A's run waits for it; it restarts with its saved log and A's next run gives it everything", async () =>
            {
                await h1.StopAsync();
                a.Write("INFO", "A: Line written while lab-logs-1 was down.");
                c.Write("INFO", "C: Line written while lab-logs-1 was down.");
                await c.ShareAsync([h2], token);
                var waiting = await a.ShareAsync(both, token);
                var unreachable = waiting.Hosts.Single(h => h.HostId == h1.HostId).State == LogShareState.Unreachable;
                await h1.StartAsync();
                await a.PairAsync(h1);
                await b.PairAsync(h1);
                var kept = Count(await a.ReadHostAsync(h1, token), "A: Martlet started.") == 1;
                var missing = Count(await a.ReadHostAsync(h1, token), "while lab-logs-1 was down") == 0;
                var result = await a.ShareAsync(both, token);
                var caught = Count(await a.ReadHostAsync(h1, token), "while lab-logs-1 was down") == 2;
                return (unreachable && waiting.Describe().Contains("Waiting for lab-logs-1", StringComparison.Ordinal) && kept && missing && caught &&
                        result.Shared == 2,
                    $"while down: \"{waiting.Describe()}\"; restarted with its saved lines: {kept}; missed lines before A's run: {missing}; " +
                    $"both after: {caught}; then: \"{result.Describe()}\"");
            });

            await Run("B shares again (taking the lines written while lab-logs-1 was down) and restarts: its copy of every other computer's lines (logs\\network-logs.json) is still there", async () =>
            {
                await b.ShareAsync(both, token);
                var before = b.Share.Logs.Count;
                var down = Count(b.Share.Logs.Snapshot(), "while lab-logs-1 was down");
                b.Reopen();
                var after = b.Share.Logs.Snapshot();
                var sources = after.Select(l => l.Source).Append(b.DeviceId).Distinct().ToHashSet();
                return (b.Share.Logs.LoadState == "loaded" && after.Count == before && down == 2 && everyone.All(sources.Contains),
                    $"lines from while lab-logs-1 was down: {down}; saved copy {b.Share.Logs.LoadState}: {after.Count} of {before} lines, from {sources.Count} computers");
            });

            await Run("Save logs to share on B: one ZIP with about.txt and every computer's lines, each once, oldest first", async () =>
            {
                var path = Path.Combine(root, "Martlet logs lab-desktop-b.zip");
                var local = LocalLogs.Read(b.LogDirectory, b.DeviceId);
                var summary = LogBundle.Save(path, local.Concat(b.Share.Logs.Snapshot()),
                    new LogBundleInfo(b.DeviceId, [b.DeviceId], "rehearsal", "loopback", b.Share.Last?.Describe(), DateTimeOffset.Now), overwrite: false);
                using var zip = ZipFile.OpenRead(path);
                var names = zip.Entries.Select(e => e.FullName).Order().ToArray();
                string Text(string name)
                {
                    using var reader = new StreamReader(zip.GetEntry(name)!.Open());
                    return reader.ReadToEnd();
                }
                var lines = Text(LogBundle.LinesEntry);
                var about = Text(LogBundle.AboutEntry);
                var every = everyone.All(id => lines.Contains(" " + id + "/", StringComparison.Ordinal) && about.Contains(id, StringComparison.Ordinal));
                var markers = new[] { "A: Martlet started.", "B: Martlet started.", "C: Listening started.", "while lab-logs-1 was down" }
                    .All(m => lines.Contains(m, StringComparison.Ordinal));
                var once = lines.Split('\n').Count(l => l.Contains("A: Martlet started.", StringComparison.Ordinal)) == 1;
                return (names.SequenceEqual([LogBundle.AboutEntry, LogBundle.LinesEntry]) && summary.Computers == everyone.Length && every && markers && once,
                    $"{string.Join(", ", names)}; {summary.Lines} lines from {summary.Computers} computers ({summary.Bytes:N0} bytes); every computer in both " +
                    $"files: {every}; markers present: {markers}; a line once: {once}");
            });

            await Run("A request for a host's logs without a paired device's signature is refused", async () =>
            {
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var client = new HttpClient(handler);
                using var response = await client.GetAsync(h2.Origin + "/martlet/v1/logs?after=0", token);
                var body = await response.Content.ReadAsStringAsync(token);
                return (response.StatusCode != HttpStatusCode.OK && !body.Contains("Martlet started", StringComparison.Ordinal),
                    $"unsigned request: HTTP {(int)response.StatusCode}, no log line in the answer");
            });
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "Two real gateways on 127.0.0.1 (Kestrel, pinned TLS, signed requests) with an in-memory logs.json, and three simulated " +
                "desktops with real logs folders using the desktop's paired client (HostLogPeer) and the real sharing engine (LogShare), " +
                "wired as the desktop wires them, and the real bundle writer (LogBundle). Synthetic lines. Not covered: the desktop " +
                "window and its 30-second runs, the Linux host's logs.json and real computers on a LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    /// <summary>A simulated desktop: a logs folder with desktop.log (Martlet's format), its pairings and the real sharing
    /// engine, run the way the desktop runs it.</summary>
    private sealed class LabDesktop
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        internal string DeviceId { get; }
        internal string LogDirectory { get; }
        internal LogShare Share { get; private set; }

        internal LabDesktop(string deviceId, string directory)
        {
            DeviceId = deviceId;
            LogDirectory = LocalLogs.Directory(directory);
            Directory.CreateDirectory(LogDirectory);
            Share = new LogShare(LogDirectory, deviceId);
        }

        /// <summary>Martlet restarting: the engine starts again from what it saved.</summary>
        internal void Reopen() => Share = new LogShare(LogDirectory, DeviceId);

        internal void Write(string level, string message) =>
            File.AppendAllText(Path.Combine(LogDirectory, "desktop.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} [1] {message}{Environment.NewLine}");

        internal async Task PairAsync(LabHost host)
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = DeviceId, DisplayName = DeviceId.ToUpperInvariant(), Roles = [GatewayRole.Voice] });
            pairings[host.HostId] = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, DeviceId,
                card.PairingId, card.Token.Reveal());
        }

        private Audio2FaceHostConnection Connect(LabHost host)
        {
            var (pairing, secret) = pairings[host.HostId];
            return new Audio2FaceHostConnection(pairing, secret);
        }

        /// <summary>What the desktop's 30-second run does: this PC's own lines from its logs folder, every paired host.</summary>
        internal async Task<LogShareResult> ShareAsync(IEnumerable<LabHost> hosts, CancellationToken token)
        {
            var peers = hosts.Where(h => pairings.ContainsKey(h.HostId)).Select(h => new HostLogPeer(h.HostId, () => Connect(h))).ToArray();
            try
            {
                var local = LocalLogs.Read(LogDirectory, DeviceId, 512 * 1024);
                return await Share.RunAsync(peers, local, send: true, DateTimeOffset.UtcNow, token);
            }
            finally
            {
                foreach (var peer in peers) peer.Dispose();
            }
        }

        /// <summary>Every line a host keeps, oldest first.</summary>
        internal async Task<IReadOnlyList<LogRecord>> ReadHostAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            var lines = new List<LogRecord>();
            var after = 0L;
            while (true)
            {
                var page = await connection.ReadLogsAsync(after, 1_000, token);
                lines.AddRange(page.Entries);
                after = page.Next;
                if (!page.More) return lines;
            }
        }
    }

    /// <summary>A real gateway on 127.0.0.1 with an in-memory logs.json that survives a restart.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayLogStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private byte[]? saved;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";

        internal static async Task<LabHost> StartAsync(string hostId)
        {
            var host = new LabHost { HostId = hostId, certificate = Certificate() };
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

        internal async Task StartAsync()
        {
            Origin = $"https://127.0.0.1:{FreePort()}";
            var origin = new GatewayOrigin(Origin);
            Server = new GatewayServer(Identity, origin, [], this);
            Server.AttachLogStorage(this);
            listener = await Server.StartAsync(new GatewayTlsBinding(origin, Identity, certificate), new KestrelGatewayListenerFactory());
        }

        internal async Task StopAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            listener = null;
        }

        public byte[]? Load() => saved;
        public void Save(byte[] bytes) => saved = (byte[])bytes.Clone();
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
            var request = new CertificateRequest("CN=Martlet log rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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
