using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Network;

namespace Martlet.Mcp;

/// <summary>
/// outside_path_check: reaching a host from outside home on real sockets, with this checkout's real Linux gateway
/// (docs/NETWORK.md, "Reaching your network from outside home"). It builds Martlet.Gateway.Host.Linux in the .NET SDK
/// image, runs it as the service account in disposable aspnet containers (an ext4 named volume holds host.json and its
/// state) on a Docker network numbered from TEST-NET-3 (203.0.113.0/24), and publishes its port on 127.0.0.1 only. Docker
/// hands connections to the published port in from that network's gateway address, so the gateway sees every one of them
/// coming from 203.0.113.1: a real outside source, not a flag. The host's home address is a private address nothing
/// answers on; its outside address is the published port. The desktop's real pairing client, paired connection, network
/// sync and HostRoutes then have to fall back to the outside address, and the gateway's guard acts on that source.
/// Never pulls; the containers, data volume, gateway volume and network are removed afterwards (the NuGet cache volume
/// martlet-outside-check-nuget is kept, so later runs restore offline).
/// </summary>
internal static class OutsidePathCheck
{
    internal const string SdkImage = "mcr.microsoft.com/dotnet/sdk:10.0.401";
    internal const string RuntimeImage = "mcr.microsoft.com/dotnet/aspnet:10.0.12";
    private const string NuGetVolume = "martlet-outside-check-nuget";
    private const string Subnet = "203.0.113.0/24";
    private const string NetworkGateway = "203.0.113.1";
    private const string HostId = "outside-check";
    private const string HomeOrigin = "https://192.168.77.20:9443";
    private const string DeviceId = "lab-laptop";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(20);

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        foreach (var image in new[] { SdkImage, RuntimeImage })
        {
            var (found, _) = await HostSupplyCheck.DockerAsync(["image", "inspect", "--format", "{{.Id}}", image], null,
                TimeSpan.FromSeconds(30), cancellation);
            if (found != 0)
                return new
                {
                    exitCode = 2,
                    notRun = $"Docker isn't running here or the {image} image isn't on this PC (this check never pulls it; run: docker pull {image})."
                };
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Limit);
        var token = limit.Token;
        var steps = new List<object>();
        var passed = true;
        void Step(string name, bool ok, string detail)
        {
            steps.Add(new { name, ok, detail });
            passed &= ok;
        }

        var name = "martlet-outside-check-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var data = name + "-data";
        var gateway = name + "-gw";
        var port = FreePort();
        var outside = $"127.0.0.1:{port}";
        var containers = new List<string>();
        try
        {
            var (created, createdText) = await Docker(["network", "create", "--subnet", Subnet, "--gateway", NetworkGateway, name], token);
            if (created != 0) throw new InvalidOperationException("docker network create failed: " + createdText.Trim());

            // This checkout's Linux gateway, built as the host image builds it (framework-dependent, no app host).
            var root = HostSupplyCheck.FindCheckout();
            var archive = Path.Combine(Path.GetTempPath(), name + ".tar.gz");
            try
            {
                await HostSupplyCheck.ArchiveCheckoutAsync(root, archive, token);
                var clock = Stopwatch.StartNew();
                var (built, buildText) = await HostSupplyCheck.ProcessAsync("docker",
                    ["run", "--rm", "-i", "--pull", "never", "-v", gateway + ":/out", "-v", NuGetVolume + ":/root/.nuget/packages",
                        "-e", "CI=true", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1", SdkImage, "bash", "-c",
                        "mkdir -p /s && tar -xz -C /s && cd /s/Martlet && dotnet publish src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj " +
                        "-c Release -o /out -p:UseAppHost=false -nologo -v q"],
                    async (stream, ct) => { await using var file = File.OpenRead(archive); await file.CopyToAsync(stream, ct); },
                    TimeSpan.FromMinutes(12), token);
                Step("gateway-built", built == 0, built == 0
                    ? $"Martlet.Gateway.Host.Linux published from this checkout in the SDK image ({clock.Elapsed.TotalSeconds:0} s)"
                    : "dotnet publish failed: " + Tail(buildText));
                if (built != 0) return Report(passed, steps, port);
            }
            finally { File.Delete(archive); }

            // The service account's private, ext4 control directory, as setup leaves it: host.json 0600 in a 0700 directory.
            var (owned, ownedText) = await Docker(["run", "--rm", "--pull", "never", "-u", "0", "-v", data + ":/srv/martlet", RuntimeImage,
                "sh", "-c", "chown 1000:1000 /srv/martlet && chmod 700 /srv/martlet"], token);
            if (owned != 0) throw new InvalidOperationException("Preparing the data volume failed: " + ownedText.Trim());
            var config = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, hostId = HostId, stateDirectory = "/srv/martlet/store", storageBackend = "linuxServicePermissions",
                binding = new { mode = "published", origin = HomeOrigin }, serviceUid = 1000, serviceGid = 1000
            });
            var (wrote, wroteText) = await HostSupplyCheck.DockerAsync(["run", "--rm", "-i", "--pull", "never", "-u", "1000:1000", "-v",
                data + ":/srv/martlet", RuntimeImage, "sh", "-c", "umask 077; cat > /srv/martlet/host.json"], config, TimeSpan.FromSeconds(60), token);
            if (wrote != 0) throw new InvalidOperationException("Writing host.json failed: " + wroteText.Trim());

            IReadOnlyList<string> Gateway(string command, params string[] options) =>
                ["dotnet", "/opt/gw/Martlet.Gateway.Host.Linux.dll", command, "--config", "/srv/martlet/host.json", .. options];
            IReadOnlyList<string> Run(string? container, bool publish, IReadOnlyList<string> command) =>
            [
                "run", "--rm", "-i", "--pull", "never", "-u", "1000:1000", "-v", data + ":/srv/martlet", "-v", gateway + ":/opt/gw:ro",
                .. container is null ? Array.Empty<string>() : ["--name", container],
                .. publish ? new[] { "--network", name, "-p", $"127.0.0.1:{port}:9443" } : [],
                RuntimeImage, .. command
            ];

            var (valid, validText) = await Docker(Run(null, false, Gateway("validate")), token);
            Step("config-valid", valid == 0 && validText.Contains("configuration.valid", StringComparison.Ordinal),
                $"validate: exit {valid}; {LastLine(validText)}");
            var (init, initText) = await Docker(Run(null, false, Gateway("owner-init")), token);
            var spki = initText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("SPKI pin: ", StringComparison.Ordinal))?[10..];
            Step("identity-created", init == 0 && NetworkRoster.IsFingerprint(spki),
                $"owner-init: exit {init}; host {HostId} at {HomeOrigin} (published on {outside}), pin {spki ?? "(none)"}");
            if (spki is null) return Report(passed, steps, port);

            // A host becomes a public endpoint only once sign-in is set up on it.
            var (early, earlyText) = await Docker(Run(null, false, Gateway("owner-exposure", "--outside", outside)), token);
            string? ownerProblem = null;
            {
                var container = name + "-owner";
                containers.Add(container);
                await using var owner = Attached.Start(Run(container, false, Gateway("owner-signin-owner", "--user", "owner")));
                await owner.WriteLineAsync("a long owner passphrase");
                var secretLine = await owner.WaitForAsync(l => l.StartsWith("secret: ", StringComparison.Ordinal), TimeSpan.FromSeconds(60), token);
                if (secretLine is null) ownerProblem = "no authenticator secret shown";
                else await owner.WriteLineAsync(Martlet.Core.Access.Totp.Code(secretLine["secret: ".Length..].Trim(), DateTimeOffset.UtcNow));
                var ownerExit = await owner.WaitForExitAsync(TimeSpan.FromSeconds(60), token);
                if (ownerExit != 0) ownerProblem ??= $"owner-signin-owner exit {ownerExit}: {Tail(owner.Text)}";
            }
            Step("exposure-needs-signin-first", early == 5 && earlyText.Contains("outside.needs_signin", StringComparison.Ordinal) && ownerProblem is null,
                $"before sign-in: owner-exposure --outside exit {early}, {LastLine(earlyText)}; then owner-signin-owner set an owner account " +
                $"with an authenticator: {ownerProblem ?? "ok"}");

            var (refused, refusedText) = await Docker(Run(null, false, Gateway("owner-exposure", "--outside", "evil;reboot:1")), token);
            var (exposed, exposedText) = await Docker(Run(null, false, Gateway("owner-exposure", "--outside", outside)), token);
            Step("owner-exposure", refused == 2 && exposed == 0 && exposedText.Contains(
                    $"now has outside addresses: {outside}; pairing codes from outside home: refused; treat every connection as outside home: no",
                    StringComparison.Ordinal),
                $"an address with a shell character: exit {refused}; --outside {outside}: exit {exposed}, {LastLine(exposedText)}");

            // What the desktop knows before the network does (from the sign-in invite or the owner): the outside address.
            HostRoutes.Set(HomeOrigin, HostId, [outside]);

            // A typed pairing code from outside home is refused by default, the desktop says so, and the code stays open.
            {
                var container = name + "-code";
                containers.Add(container);
                await using var pair = Attached.Start(Run(container, true, Gateway("owner-pair")));
                var codeLine = await pair.WaitForAsync(l => l.TrimStart().StartsWith("Code:", StringComparison.Ordinal), TimeSpan.FromSeconds(60), token);
                var code = codeLine?.Split(':', 2)[1].Trim();
                string? seen = null;
                if (code is not null)
                {
                    await WaitListeningAsync(spki, outside, token);
                    try { await Audio2FaceHostClient.PairWithCodeAsync(HomeOrigin, code, DeviceId, "LAB-LAPTOP", token); seen = "paired"; }
                    catch (Audio2FaceHostException error) { seen = error.Code; }
                }
                var route = HostRoutes.For(HomeOrigin);
                await pair.WriteLineAsync("cancel");
                var exit = await pair.WaitForExitAsync(TimeSpan.FromSeconds(30), token);
                Step("typed-code-refused-from-outside", seen == "pair.outside_home" && route is { Route: "outside" } && exit == 3 &&
                        pair.Text.Contains("pairing.canceled", StringComparison.Ordinal),
                    $"code shown: {code is not null}; the desktop's code pairing (home {HomeOrigin} dead, so {route?.Route} via {route?.Address}): {seen}; " +
                    $"the code stayed open until cancel (owner-pair exit {exit})");
            }

            // A one-use card opened for this device can't be guessed, so it pairs from outside home.
            Audio2FaceHostPairing? pairing = null;
            string? secret = null;
            {
                var container = name + "-card";
                containers.Add(container);
                await using var pair = Attached.Start(Run(container, true, Gateway("owner-pair", "--device-id", DeviceId, "--name", "LAB-LAPTOP")));
                var cardLine = await pair.WaitForAsync(l => l.StartsWith("pairing-code: ", StringComparison.Ordinal), TimeSpan.FromSeconds(60), token);
                string? problem = null;
                if (cardLine is not null)
                {
                    await WaitListeningAsync(spki, outside, token);
                    try { (pairing, secret) = await HostPairingCode.Parse(cardLine["pairing-code: ".Length..].Trim()).PairAsync(DeviceId, token); }
                    catch (Audio2FaceHostException error) { problem = error.Code; }
                }
                var exit = await pair.WaitForExitAsync(TimeSpan.FromSeconds(30), token);
                Step("card-pairs-from-outside", pairing is not null && exit == 0 && pair.Text.Contains($"Paired: {DeviceId}", StringComparison.Ordinal),
                    pairing is null ? $"not paired: {problem ?? "no card shown"}; owner-pair exit {exit}; {Tail(pair.Text)}"
                        : $"paired {pairing.DeviceId} with {pairing.HostId}, pinned {pairing.SpkiFingerprint == spki}; owner-pair exit {exit}");
            }
            if (pairing is null || secret is null) return Report(passed, steps, port);

            var serve = name + "-serve";
            containers.Add(serve);
            var (started, startedText) = await Docker(["run", "-d", "--pull", "never", "--name", serve, "-u", "1000:1000", "-v", data + ":/srv/martlet",
                "-v", gateway + ":/opt/gw:ro", "--network", name, "-p", $"127.0.0.1:{port}:9443", RuntimeImage, .. Gateway("serve")], token);
            if (started != 0) throw new InvalidOperationException("Starting serve failed: " + startedText.Trim());
            var healthy = await WaitListeningAsync(spki, outside, token);
            Step("serve-healthy", healthy, healthy ? $"serve answers GET /health/live on {outside} with pin {spki}"
                : "serve did not answer: " + Tail((await Docker(["logs", serve], token)).Output));
            if (!healthy) return Report(passed, steps, port);

            // The network: the desktop founds it with this host and signs in the outside address the host advertises (set with
            // owner-exposure); then the route comes from the signed roster alone.
            using var key = NetworkKey.Create(DeviceId);
            var engine = new NetworkSyncEngine(key, "LAB-LAPTOP");
            Audio2FaceHostConnection Connect(Audio2FaceHostPairing p) => new(p, secret);
            var first = await engine.SyncAsync(NetworkLocalState.Empty, [pairing], Connect, token);
            var second = await engine.SyncAsync(first.State, [pairing], Connect, token);
            var listed = second.State.Roster?.Host(HostId)?.Addresses;
            Step("roster-signs-outside-address", listed is { Count: 1 } && listed[0] == outside,
                $"roster lists {string.Join(", ", listed ?? [])}; {string.Join(" ", first.Events.Concat(second.Events))}");

            HostRoutes.Set(HomeOrigin, HostId, []);
            string? homeOnly = null;
            try { using var c = Connect(pairing); await c.ReadRoutesAsync(token); homeOnly = "connected"; }
            catch (Audio2FaceHostException error) { homeOnly = error.Code; }
            // A host with no outside address whose home address doesn't answer: the connect timeout ends the request.
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { homeOnly = "timed out"; }
            HostRoutes.Update(second.State.Roster);
            var clock2 = Stopwatch.StartNew();
            using (var c = Connect(pairing)) await c.ReadRoutesAsync(token);
            var firstMs = clock2.ElapsedMilliseconds;
            clock2.Restart();
            using (var c = Connect(pairing)) await c.ReadRoutesAsync(token);
            var nextMs = clock2.ElapsedMilliseconds;
            var route2 = HostRoutes.For(HomeOrigin);
            Step("home-fails-outside-succeeds", homeOnly is "host.unreachable" or "timed out" && route2 is { Route: "outside" } && route2.Address == outside,
                $"home only: {homeOnly}; with the roster's outside address: {route2?.Route} via {route2?.Address}, first {firstMs} ms, " +
                $"next (last good route first) {nextMs} ms");

            // The guard on the real outside source: guessing locks the address out with a doubling wait.
            var codes = new List<string>();
            for (var i = 0; i < 6; i++) codes.Add(await StrangerAsync(spki, outside, token));
            Step("guard-locks-out-outside-source", codes.Take(5).All(c => c == "401 auth.missing") && codes[5].StartsWith("429 auth.throttled", StringComparison.Ordinal),
                $"a stranger without a credential: {string.Join(", ", codes)}");

            await Task.Delay(TimeSpan.FromSeconds(2.2), token);
            using (var c = Connect(pairing))
            {
                var audit = await c.ReadSecurityAuditAsync(token);
                var log = await c.ReadOwnLogsAsync(0, 500, token);
                var sources = audit.Events.Where(e => e.Outcome is "failure" or "throttled").Select(e => $"{e.Source} ({e.SourceKind})").Distinct().ToArray();
                var locked = log.Entries.Select(e => e.Message).FirstOrDefault(m => m.StartsWith("Locked out credential requests from", StringComparison.Ordinal));
                Step("audit-names-outside-source", audit.InternetReachable && !audit.TreatAllAsOutside && sources.SequenceEqual([$"{NetworkGateway} (outside)"]) &&
                        locked?.Contains($"{NetworkGateway} (outside home)", StringComparison.Ordinal) == true,
                    $"internet reachable: {audit.InternetReachable} (from the roster), treat every connection as outside: {audit.TreatAllAsOutside}; " +
                    $"failures and throttles from {string.Join(", ", sources)}; host log: {locked ?? "(no lockout line)"}");
            }

            // Sign-in removed later (signin.json gone): the addresses stay, requests from outside home are refused, and the paired
            // owner still reaches sign-in's settings to set it up again.
            var (removed, removedText) = await Docker(["exec", "-u", "1000:1000", serve, "rm", "/srv/martlet/signin.json"], token);
            await Task.Delay(TimeSpan.FromSeconds(5.5), token);
            string? pausedSees = null;
            try { using var c = Connect(pairing); await c.ReadRoutesAsync(token); pausedSees = "served"; }
            catch (Audio2FaceHostException error) { pausedSees = error.Code; }
            var strangerSees = await StrangerAsync(spki, outside, token);
            string? settingsSees;
            try { using var c = Connect(pairing); settingsSees = (await c.ReadSignInSettingsAsync(token)).BlockedReason ?? "usable"; }
            catch (Audio2FaceHostException error) { settingsSees = error.Code; }
            Step("outside-paused-without-signin", removed == 0 && pausedSees == "outside.paused" && strangerSees.StartsWith("403 outside.paused", StringComparison.Ordinal) &&
                    settingsSees == "signin.not_set_up",
                $"signin.json removed (exit {removed}{(removed == 0 ? "" : ", " + removedText.Trim())}): the paired desktop gets {pausedSees}, " +
                $"a stranger {strangerSees}; sign-in's settings still answer the owner ({settingsSees})");

            var home = await HostRoutes.ProbeAsync(HomeOrigin, spki, null, TimeSpan.FromSeconds(3), token);
            var reached = await HostRoutes.ProbeAsync(HomeOrigin, spki, outside, TimeSpan.FromSeconds(3), token);
            Step("probe-tells-home-and-outside-apart", !home.Reachable && reached is { Reachable: true, Problem: "answered 403 Forbidden" },
                $"home {HomeOrigin[8..]}: {home.Problem} after {home.Milliseconds} ms; outside {outside}: reachable in {reached.Milliseconds} ms" +
                (reached.Problem is { } note ? $" ({note})" : ""));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            Step("unexpected", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            HostRoutes.Set(HomeOrigin, HostId, []);
            foreach (var container in containers)
                await HostSupplyCheck.DockerAsync(["rm", "-f", container], null, TimeSpan.FromSeconds(60), CancellationToken.None);
            await HostSupplyCheck.DockerAsync(["volume", "rm", "-f", data, gateway], null, TimeSpan.FromSeconds(60), CancellationToken.None);
            await HostSupplyCheck.DockerAsync(["network", "rm", name], null, TimeSpan.FromSeconds(60), CancellationToken.None);
        }
        return Report(passed, steps, port);
    }

    private static object Report(bool passed, List<object> steps, int port) => new
    {
        exitCode = passed ? 0 : 1,
        report = new
        {
            passed, total = steps.Count, network = Subnet, outsideSource = NetworkGateway, home = HomeOrigin, published = $"127.0.0.1:{port}",
            steps
        },
        notCovered = new[]
        {
            "a real router port forward, overlay network (Tailscale, ZeroTier, WireGuard) or internet path: Docker's published port stands in",
            "the desktop window (its Outside access dialog is checked through ui_* tools)"
        }
    };

    private static Task<(int Exit, string Output)> Docker(IReadOnlyList<string> arguments, CancellationToken token) =>
        HostSupplyCheck.DockerAsync(arguments, null, TimeSpan.FromMinutes(2), token);

    /// <summary>Waits until the gateway answers on its outside address with the pinned key.</summary>
    private static async Task<bool> WaitListeningAsync(string spki, string outside, CancellationToken token)
    {
        for (var i = 0; i < 60; i++)
        {
            var probe = await HostRoutes.ProbeAsync(HomeOrigin, spki, outside, TimeSpan.FromSeconds(2), token);
            if (probe.Reachable) return true;
            await Task.Delay(500, token);
        }
        return false;
    }

    /// <summary>Someone without a credential asking for status over the outside address (pinned TLS, so only the guard
    /// stands between): "HTTP-status code".</summary>
    private static async Task<string> StrangerAsync(string spki, string outside, CancellationToken token)
    {
        var colon = outside.LastIndexOf(':');
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new DnsEndPoint(outside[..colon], int.Parse(outside[(colon + 1)..])), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && Fingerprint(certificate) == spki;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await http.GetAsync(HomeOrigin + "/martlet/v1/status", token);
        var body = await response.Content.ReadAsStringAsync(token);
        string? code = null;
        try { code = JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null; }
        catch (JsonException) { }
        var retry = response.Headers.RetryAfter?.Delta is { } wait ? $" (Retry-After {wait.TotalSeconds:0} s)" : "";
        return $"{(int)response.StatusCode} {code}{retry}";
    }

    private static string Fingerprint(X509Certificate certificate)
    {
        using var certificate2 = new X509Certificate2(certificate);
        using var key = (AsymmetricAlgorithm?)certificate2.GetECDsaPublicKey() ?? certificate2.GetRSAPublicKey();
        var info = key switch
        {
            ECDsa ecdsa => ecdsa.ExportSubjectPublicKeyInfo(),
            RSA rsa => rsa.ExportSubjectPublicKeyInfo(),
            _ => []
        };
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(info));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static string LastLine(string text) => text.Replace("\r", "").Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no output)";

    private static string Tail(string text) => text.Length <= 700 ? text.Trim() : "..." + text[^700..].Trim();

    /// <summary>A docker run whose output is read line by line while it runs, with stdin kept open.</summary>
    private sealed class Attached : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        private readonly StringBuilder text = new();

        private Attached(Process process) => this.process = process;

        internal string Text { get { lock (text) return text.ToString(); } }

        internal static Attached Start(IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo("docker")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var attached = new Attached(Process.Start(start) ?? throw new InvalidOperationException("docker did not start."));
            attached.process.OutputDataReceived += (_, e) => attached.Add(e.Data);
            attached.process.ErrorDataReceived += (_, e) => attached.Add(e.Data);
            attached.process.BeginOutputReadLine();
            attached.process.BeginErrorReadLine();
            return attached;
        }

        private void Add(string? line)
        {
            if (line is null) return;
            lock (text) text.AppendLine(line);
            lines.Writer.TryWrite(line);
        }

        internal async Task<string?> WaitForAsync(Func<string, bool> match, TimeSpan limit, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(limit);
            try
            {
                while (await lines.Reader.WaitToReadAsync(timeout.Token))
                    while (lines.Reader.TryRead(out var line))
                        if (match(line)) return line;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            return null;
        }

        internal async Task WriteLineAsync(string line)
        {
            try
            {
                await process.StandardInput.WriteLineAsync(line);
                await process.StandardInput.FlushAsync();
            }
            catch (IOException) { }
        }

        internal async Task<int> WaitForExitAsync(TimeSpan limit, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(limit);
            try { await process.WaitForExitAsync(timeout.Token); return process.ExitCode; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return -1; }
        }

        public ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
