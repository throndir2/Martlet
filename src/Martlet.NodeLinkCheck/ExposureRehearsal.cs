using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses a host reachable from outside home (docs/NETWORK.md, "Reaching your network from outside home") with the
/// production code: one real gateway (Kestrel, pinned TLS) on 127.0.0.1, a desktop that pairs while "at home", then the host
/// told to treat every connection as coming from outside (as for a host behind a port proxy), and a stranger's pinned HTTPS
/// client guessing. Checks that pairing from outside is refused until the owner allows it, guessing is locked out with a
/// doubling Retry-After, routes anyone may call have a request budget, and the paired desktop reads the audit and the
/// host log lines over its signed connection. Nothing leaves loopback; nothing is written to disk.
/// </summary>
internal static class ExposureRehearsal
{
    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        await using var host = await LabHost.StartAsync("lab-exposure");
        using var stranger = PinnedGatewayClient.Create(new GatewayOrigin(host.Origin), host.Identity);
        Audio2FaceHostConnection? desktop = null;
        Audio2FaceHostPairing? saved = null;
        string? savedSecret = null;

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

        async Task<(int Status, string? Code, TimeSpan? RetryAfter)> Call(HttpMethod method, string path, string? json = null)
        {
            using var request = new HttpRequestMessage(method, host.Origin + path);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await stranger.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            string? code = null;
            try { code = JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null; }
            catch (JsonException) { }
            return ((int)response.StatusCode, code, response.Headers.RetryAfter?.Delta);
        }

        string PairingProof(GatewayPairingCard card) => JsonSerializer.Serialize(new
        {
            protocol_version = new { major = GatewayProtocolVersion.Current.Major, minor = GatewayProtocolVersion.Current.Minor },
            pairing_id = card.PairingId, pairing_token = card.Token.Reveal(), host_id = card.HostId,
            spki_fingerprint = card.SpkiFingerprint, device_id = "lab-laptop"
        });

        await Run("At home (default exposure) a desktop pairs with the host over pinned TLS", async () =>
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = "lab-desktop", DisplayName = "LAB-DESKTOP", Roles = [GatewayRole.Voice] });
            var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, "lab-desktop",
                card.PairingId, card.Token.Reveal(), token);
            desktop = new Audio2FaceHostConnection(pairing, secret);
            (saved, savedSecret) = (pairing, secret);
            var audit = await desktop.ReadSecurityAuditAsync(token);
            return (audit.Events.Any(e => e is { Outcome: "success", Subject: "lab-desktop", SourceKind: "loopback" }) && !audit.InternetReachable,
                $"paired; audit: {audit.Successes} success(es), source kind {audit.Events.LastOrDefault()?.SourceKind}");
        });

        host.Server.Exposure = new() { TreatAllAsOutside = true, InternetReachable = true };
        GatewayPairingCard? laptopCard = null;
        GatewayCodePairingCard? code = null;
        await Run("Marked reachable from outside: a typed pairing code used from outside home is refused and stays open", async () =>
        {
            code = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var refused = await Call(HttpMethod.Post, "/martlet/v1/pair/code", "{}");
            var stillOpen = host.Server.Pairing.IsOpen(code.PairingId);
            string? desktopSees = null;
            try { await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, code.Code.Reveal(), "lab-phone", "LAB-PHONE", token); }
            catch (Audio2FaceHostException error) { desktopSees = error.Code; }
            return (refused is { Status: 403, Code: "pair.outside_home" } && stillOpen && desktopSees == "pair.outside_home" &&
                    host.Server.Pairing.IsOpen(code.PairingId),
                $"HTTP {refused.Status} {refused.Code}; window still open: {stillOpen}; the desktop's code pairing gets {desktopSees}");
        });

        await Run("A card opened for one named device (as Martlet pairs with its own host service) pairs from outside without the opt-in", async () =>
        {
            laptopCard = host.Server.Pairing.OpenWindow(new() { DeviceId = "lab-laptop", DisplayName = "LAB-LAPTOP", Roles = [GatewayRole.Voice] });
            var paired = await Call(HttpMethod.Post, "/martlet/v1/pair", PairingProof(laptopCard));
            var audit = await desktop!.ReadSecurityAuditAsync(token);
            return (paired.Status == 201 && audit.Events.Any(e => e is { Outcome: "success", Subject: "lab-laptop", SourceKind: "outside" }),
                $"HTTP {paired.Status}; audit records the outside pairing: {audit.Events.Any(e => e.Subject == "lab-laptop" && e.Outcome == "success")}");
        });

        await Run("Allowing typed codes from outside without sign-in pauses outside access; the owner sets up sign-in from outside", async () =>
        {
            host.Server.Exposure = host.Server.Exposure with { AllowPairingOutsideHome = true };
            string? codeSees = null, auditSees = null;
            try { await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, code!.Code.Reveal(), "lab-phone", "LAB-PHONE", token); }
            catch (Audio2FaceHostException error) { codeSees = error.Code; }
            try { await desktop!.ReadSecurityAuditAsync(token); }
            catch (Audio2FaceHostException error) { auditSees = error.Code; }
            var reason = host.Server.Guard.OutsideAccessBlockedReason;
            // Sign-in's own settings stay reachable for the paired owner, so it can be fixed from outside.
            var secret = Totp.NewSecret();
            var settings = await desktop!.ChangeSignInSettingsAsync(new System.Text.Json.Nodes.JsonObject
            {
                ["action"] = "owner", ["user"] = "owner", ["password"] = "a long owner passphrase", ["totp_secret"] = secret,
                ["code"] = Totp.Code(secret, DateTimeOffset.UtcNow)
            }, token);
            await Task.Delay(TimeSpan.FromSeconds(5.5), token); // the guard reads sign-in at most every five seconds
            var after = await desktop.ReadSecurityAuditAsync(token);
            return (codeSees == "outside.paused" && auditSees == "outside.paused" && reason == "signin.not_set_up" && settings.Usable &&
                    !after.OutsideAccessPaused && after.OutsideAccessBlockedReason is null &&
                    after.Events.Any(e => e is { Outcome: "refused", Code: "outside.paused" }),
                $"without sign-in ({reason}): the typed code gets {codeSees}, the desktop's audit read gets {auditSees}; the owner sets up an " +
                $"owner account over the sign-in settings route (usable: {settings.Usable}); then paused: {after.OutsideAccessPaused}");
        });

        await Run("The owner allows pairing codes from outside home: the same typed code then pairs", async () =>
        {
            var (pairing, _) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, code!.Code.Reveal(), "lab-phone", "LAB-PHONE", token);
            return (pairing.DeviceId == "lab-phone", $"paired {pairing.DeviceId} with {pairing.HostId}");
        });        // The refusal above counted once against this address's pairing; wait out anything left before guessing.
        await Run("A stranger guessing credentials is locked out after five failures, with Retry-After", async () =>
        {
            var codes = new List<string?>();
            for (var i = 0; i < GatewayRequestGuard.FreeFailures; i++) codes.Add((await Call(HttpMethod.Get, "/martlet/v1/status")).Code);
            var locked = await Call(HttpMethod.Get, "/martlet/v1/status");
            return (codes.All(c => c == "auth.missing") && locked is { Status: 429, Code: "auth.throttled" } && locked.RetryAfter >= TimeSpan.FromSeconds(1),
                $"{string.Join(", ", codes)}; then HTTP {locked.Status} {locked.Code}, Retry-After {locked.RetryAfter?.TotalSeconds} s");
        });

        await Run("Each further failure doubles the wait; after Retry-After the paired desktop's signed requests work again", async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.2), token);
            var again = await Call(HttpMethod.Get, "/martlet/v1/status");
            var locked = await Call(HttpMethod.Get, "/martlet/v1/status");
            await Task.Delay(locked.RetryAfter ?? TimeSpan.FromSeconds(2), token);
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
            var routes = await desktop!.ReadRoutesAsync(token);
            return (again.Code == "auth.missing" && locked is { Status: 429 } && locked.RetryAfter >= TimeSpan.FromSeconds(2),
                $"sixth failure {again.Code}, then Retry-After {locked.RetryAfter?.TotalSeconds} s; desktop read {routes.Count} route(s) afterwards");
        });

        await Run($"Routes anyone may call allow {GatewayRequestGuard.UnauthenticatedRequestsPerMinute} requests a minute per outside address", async () =>
        {
            var statuses = new List<int>();
            for (var i = 0; i <= GatewayRequestGuard.UnauthenticatedRequestsPerMinute; i++)
                statuses.Add((await Call(HttpMethod.Get, "/health/live")).Status);
            var accepted = statuses.Count(s => s == 200);
            return (accepted <= GatewayRequestGuard.UnauthenticatedRequestsPerMinute && statuses[^1] == 429,
                $"{accepted} answered, then HTTP {statuses[^1]}");
        });

        await Run("The paired desktop reads the audit and the host log names each decision with its source", async () =>
        {
            var audit = await desktop!.ReadSecurityAuditAsync(token);
            var log = await desktop.ReadOwnLogsAsync(0, 500, token);
            var lines = log.Entries.Select(e => e.Message).ToArray();
            var outcomes = audit.Events.GroupBy(e => e.Outcome).ToDictionary(g => g.Key, g => g.Count());
            var ok = audit is { InternetReachable: true, AllowPairingOutsideHome: true, TreatAllAsOutside: true } &&
                outcomes.ContainsKey("refused") && outcomes.ContainsKey("throttled") && outcomes.ContainsKey("failure") &&
                lines.Any(l => l.StartsWith("Refused a pairing code from 127.0.0.1", StringComparison.Ordinal)) &&
                lines.Any(l => l.StartsWith("Locked out credential requests from 127.0.0.1 (outside home)", StringComparison.Ordinal)) &&
                lines.Any(l => l.StartsWith("Throttled health requests from 127.0.0.1", StringComparison.Ordinal));
            return (ok, $"audit: {string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}"))}; totals {audit.Successes}/{audit.Failures}/" +
                $"{audit.Throttled} (success/failure/throttled), locked out {audit.LockedOut}; host log: " +
                string.Join(" | ", lines.Where(l => l.StartsWith("Refused") || l.StartsWith("Locked out") || l.StartsWith("Throttled")).Take(4)));
        });
        desktop?.Dispose();

        // Outside addresses: the desktop keeps the home origin (pinned key, signatures) and only the TCP connection goes elsewhere.
        host.Server.Exposure = new();
        var port = int.Parse(host.Origin[(host.Origin.LastIndexOf(':') + 1)..]);
        var deadHome = $"https://127.0.0.1:{LabHost.FreePort()}";
        await Run("Home address doesn't answer: the desktop reaches the host at its outside address, pinned to the same key", async () =>
        {
            HostRoutes.Set(deadHome, host.HostId, [$"127.0.0.1:{port}"]);
            using var away = new Audio2FaceHostConnection(saved! with { Origin = deadHome }, savedSecret);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = await away.ReadRoutesAsync(token);
            var firstMs = clock.ElapsedMilliseconds;
            var route = HostRoutes.For(deadHome);
            using var again = new Audio2FaceHostConnection(saved! with { Origin = deadHome }, savedSecret);
            clock.Restart();
            await again.ReadRoutesAsync(token);
            return (route is { Route: "outside" } && route.Address == $"127.0.0.1:{port}",
                $"route {route?.Route} via {route?.Address}; first connection {firstMs} ms, next (last good route first) {clock.ElapsedMilliseconds} ms");
        });
        await Run("Home address answers: it is used at once, never waiting on an outside one", async () =>
        {
            HostRoutes.Set(host.Origin, host.HostId, [$"127.0.0.1:{LabHost.FreePort()}"]);
            using var home = new Audio2FaceHostConnection(saved!, savedSecret);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await home.ReadRoutesAsync(token);
            var route = HostRoutes.For(host.Origin);
            return (route is { Route: "home" } && clock.ElapsedMilliseconds < HostRoutes.HomeConnectWindow.TotalMilliseconds,
                $"route {route?.Route} in {clock.ElapsedMilliseconds} ms");
        });
        await Run("Nothing answers: the desktop says which addresses it tried and what to check", async () =>
        {
            var nowhere = $"https://127.0.0.1:{LabHost.FreePort()}";
            HostRoutes.Set(nowhere, host.HostId, [$"127.0.0.1:{LabHost.FreePort()}"]);
            using var lost = new Audio2FaceHostConnection(saved! with { Origin = nowhere }, savedSecret);
            try
            {
                await lost.ReadRoutesAsync(token);
                return (false, "unexpectedly connected");
            }
            catch (Audio2FaceHostException error)
            {
                return (error.Code == "host.unreachable" && error.Message.Contains("Couldn't reach the host at home or outside", StringComparison.Ordinal) &&
                    HostRoutes.For(nowhere) is { Route: "none" }, $"{error.Code}: {error.Message}");
            }
        });
        await Run("The reachability probe (MCP outside_reachability_check) tells answering, closed and wrong-key addresses apart", async () =>
        {
            await using var stranger2 = await LabHost.StartAsync("lab-stranger");
            var spki = host.Identity.SpkiFingerprint;
            var home = await HostRoutes.ProbeAsync(host.Origin, spki, null, TimeSpan.FromSeconds(4), token);
            var outside = await HostRoutes.ProbeAsync(deadHome, spki, $"127.0.0.1:{port}", TimeSpan.FromSeconds(4), token);
            var closed = await HostRoutes.ProbeAsync(deadHome, spki, null, TimeSpan.FromSeconds(4), token);
            var other = await HostRoutes.ProbeAsync(stranger2.Origin, spki, null, TimeSpan.FromSeconds(4), token);
            return (home is { Reachable: true, Problem: null } && outside is { Reachable: true, Problem: null } && !closed.Reachable && closed.Problem == "refused" && !other.Reachable && other.Problem == "another key",
                $"home: {home.Reachable} ({home.Milliseconds} ms{(home.Problem is null ? "" : ", " + home.Problem)}); outside: {outside.Reachable} ({outside.Milliseconds} ms{(outside.Problem is null ? "" : ", " + outside.Problem)}); closed: {closed.Problem}; " +
                $"another host's key at the address: {other.Problem}");
        });
        await Run("Outside addresses set on the host itself (martlet-host owner-exposure) are signed into the network by a member desktop", async () =>
        {
            host.Server.Exposure = new() { OutsideAddresses = ["gpu-box.tailnet.ts.net:9443"], OutsideAddressesSetAt = DateTimeOffset.UtcNow };
            using var key = Martlet.Core.Network.NetworkKey.Create("lab-desktop");
            var engine = new NetworkSyncEngine(key, "LAB-DESKTOP");
            var first = await engine.SyncAsync(NetworkLocalState.Empty, [saved!], p => new Audio2FaceHostConnection(p, savedSecret), token);
            var second = await engine.SyncAsync(first.State, [saved!], p => new Audio2FaceHostConnection(p, savedSecret), token);
            var listed = second.State.Roster?.Host(host.HostId)?.Addresses;
            var reachable = host.Server.Guard.InternetReachable;
            host.Server.Exposure = new();
            return (listed is ["gpu-box.tailnet.ts.net:9443"] && reachable && second.Views[host.HostId].Roster?.Host(host.HostId)?.Addresses is { Count: 1 },
                $"roster lists {string.Join(", ", listed ?? [])}; host has it too and counts as reachable from outside: {reachable}; " +
                string.Join(" ", first.Events.Concat(second.Events)));
        });
        await Run("Another computer with the home address (another network's key): skipped, the outside address is used", async () =>
        {
            await using var other = await LabHost.StartAsync("lab-other");
            HostRoutes.Set(other.Origin, host.HostId, [$"127.0.0.1:{port}"]);
            using var collide = new Audio2FaceHostConnection(saved! with { Origin = other.Origin }, savedSecret);
            string firstOutcome;
            try { await collide.ReadRoutesAsync(token); firstOutcome = "connected"; }
            catch (Audio2FaceHostException error) { firstOutcome = error.Code; }
            using var retry = new Audio2FaceHostConnection(saved! with { Origin = other.Origin }, savedSecret);
            var routes = await retry.ReadRoutesAsync(token);
            var route = HostRoutes.For(other.Origin);
            return (firstOutcome == "host.unreachable" && route is { Route: "outside" },
                $"first request {firstOutcome} (wrong key at the home address); then route {route?.Route} via {route?.Address}, {routes.Count} route(s) read");
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "One real gateway on 127.0.0.1 (Kestrel, pinned TLS) told to treat every connection as outside home, a desktop " +
                "using the desktop's paired client and network sync, a stranger's pinned HTTPS client, and outside addresses played by " +
                "other loopback ports. Not covered: a real internet source address, a router port forward, an overlay network and the " +
                "desktop window.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    private sealed class LabHost : IAsyncDisposable, IGatewayAuditSink, IGatewaySignInStorage
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
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
                host.Origin = $"https://127.0.0.1:{FreePort()}";
                var origin = new GatewayOrigin(host.Origin);
                host.Server = new GatewayServer(host.Identity, origin, [], host);
                host.Server.AttachSignInStorage(host);
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

        // signin.json in memory.
        private byte[]? signIn;
        public byte[]? Load() => signIn;
        public void Save(byte[] bytes) => signIn = bytes;

        public async ValueTask DisposeAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            certificate?.Dispose();
        }

        internal static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        private static X509Certificate2 Certificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Martlet exposure rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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
