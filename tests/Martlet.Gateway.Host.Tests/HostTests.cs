using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Gateway.Host;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Tests;

public sealed class HostTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Martlet.Host." + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(root, "state");
    public HostTests()
    {
        Assert.True(OperatingSystem.IsWindows(), "Native Windows tests must run on Windows; not silently skipped.");
        Assert.Equal("NTFS", new DriveInfo(Path.GetPathRoot(root)!).DriveFormat);
        Directory.CreateDirectory(root);
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
    private string[] Args(string mode = "init", GatewayOrigin? origin = null) =>
        [mode, "--state", Store, "--host-id", "fixture-host", "--origin", (origin ?? new("https://127.0.0.1:9443")).CanonicalOrigin];

    [Fact]
    public async Task Passive_help_noargs_and_parse_errors_never_open_state()
    {
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--help" }, new[] { "-h" }, new[] { "help" } })
        {
            var console = new TestConsole { IsInteractive = false };
            Assert.Equal(0, await HostApplication.RunAsync(args, console));
            Assert.Contains("default No", console.Text);
            Assert.False(Directory.Exists(Store));
        }
        var invalid = new TestConsole();
        Assert.Equal(2, await HostApplication.RunAsync(["--token", "SECRET-SHOULD-NOT-ECHO"], invalid));
        Assert.DoesNotContain("SECRET-SHOULD-NOT-ECHO", invalid.Text);
        Assert.False(Directory.Exists(Store));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no")]
    [InlineData("YES")]
    [InlineData(null)]
    public async Task Initial_default_No_refuses_without_creating_state(string? answer)
    {
        var console = new TestConsole(answer);
        Assert.Equal(3, await HostApplication.RunAsync(Args(), console));
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task Redirected_or_invalid_approval_never_creates_state()
    {
        var console = new TestConsole("yes") { IsInteractive = false };
        Assert.Equal(3, await HostApplication.RunAsync(Args(), console));
        console = new("yes\nSECRET");
        Assert.Equal(2, await HostApplication.RunAsync(Args(), console));
        Assert.DoesNotContain("SECRET", console.Text);
        console = new("yes") { BeforeRead = (_, _) => { console.IsInteractive = false; return Task.CompletedTask; } };
        Assert.Equal(3, await HostApplication.RunAsync(Args(), console));
        Assert.False(Directory.Exists(Store));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9443")]
    [InlineData("https://127.0.0.1:9443/")]
    [InlineData("https://localhost:9443")]
    [InlineData("https://0.0.0.0:9443")]
    [InlineData("https://192.168.1.1:9443")]
    [InlineData("https://127.0.0.1:9443/?token=SECRET")]
    public void Noncanonical_or_nonloopback_origin_is_passively_refused(string origin)
    {
        var args = Args();
        args[6] = origin;
        Assert.Throws<HostInputException>(() => HostOptions.Parse(args));
        Assert.False(Directory.Exists(Store));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\parent\..\store")]
    [InlineData(@"\\server\share\store")]
    [InlineData(@"C:\store:secret")]
    [InlineData(@"C:\store\")]
    [InlineData(@"C:\store?")]
    public void Invalid_state_path_is_passively_refused(string path)
    {
        var args = Args();
        args[2] = path;
        Assert.Throws<HostInputException>(() => HostOptions.Parse(args));
    }

    [Theory]
    [InlineData("voice,voice")]
    [InlineData("admin")]
    [InlineData("")]
    [InlineData("Voice")]
    public void Invalid_roles_are_not_coerced(string value) =>
        Assert.Throws<HostInputException>(() => HostApplication.ParseRoles(value));

    [Fact]
    public async Task Init_open_and_start_are_separate_and_missing_state_is_not_created()
    {
        var console = new TestConsole("yes");
        Assert.Equal(4, await HostApplication.RunAsync(Args("open"), console));
        Assert.Contains("StoreMissing", console.Text);
        Assert.False(Directory.Exists(Store));
        var origin = FreeOrigin();
        console = new("yes", "start", "", "pair", "list", "stop", "no");
        Assert.Equal(0, await HostApplication.RunAsync(Args(origin: origin), console));
        Assert.DoesNotContain("Loopback listener started.", console.Text);
        Assert.Contains("No paired registrations.", console.Text);
        Assert.False(File.Exists(Path.Combine(Store, "running")));
        await using var owner = DurableGatewayHost.OpenExisting(Store, origin, [], new Audit(), LocalGatewayDecision.Enable);
        await owner.CloseCleanlyAsync();
    }

    [Fact]
    public async Task Pairing_default_No_and_invalid_fields_never_disclose_or_issue()
    {
        var origin = FreeOrigin();
        var console = new TestConsole("yes", "start", "yes", "pair", "fixture-device", "Fixture",
            "voice", "", "list", "stop", "yes");
        Assert.Equal(0, await HostApplication.RunAsync(Args(origin: origin), console));
        Assert.Contains("No paired registrations.", console.Text);
        console = new("yes", "start", "yes", "pair", "fixture-device", "Fixture", "admin");
        Assert.Equal(2, await HostApplication.RunAsync(Args("open", origin), console));
        console = new("yes", "start", "yes", "pair", "fixture-device", "Fixture", "voice", "yes")
        {
            Disclosure = (_, _) => throw new HostConsoleException()
        };
        Assert.Equal(3, await HostApplication.RunAsync(Args("open", origin), console));
        Assert.False(File.Exists(Path.Combine(Store, "running")));
        await using var owner = DurableGatewayHost.OpenExisting(Store, origin, [], new Audit(), LocalGatewayDecision.Enable);
        Assert.Empty(owner.ListRegistrations());
        await owner.CloseCleanlyAsync();
    }

    [Fact]
    public async Task Actual_CLI_listener_pairing_permanent_v2_replay_reopen_list_revoke()
    {
        var origin = FreeOrigin();
        GatewayHostIdentity? identity = null;
        FixtureCredential? credential = null;
        HttpRequestMessage? original = null;
        var console = new TestConsole("yes", "start", "yes", "start", "pair", "fixture-device", "Fixture device",
            "voice,perception", "yes", "list", "stop", "yes");
        console.Disclosure = async (card, cancellation) =>
        {
            Assert.InRange(card.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5));
            identity = new() { HostId = card.HostId, SpkiFingerprint = card.SpkiFingerprint };
            using var client = PinnedGatewayClient.Create(origin, identity);
            var body = PairingBody(card);
            using (var request = PairRequest(origin, body))
            using (var response = await client.SendAsync(request, cancellation))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                var value = json.RootElement;
                Assert.Equal(2, value.GetProperty("protocol_version").GetProperty("major").GetInt32());
                var lifetime = value.GetProperty("lifetime");
                Assert.Equal("paired", lifetime.GetProperty("kind").GetString());
                Assert.Single(lifetime.EnumerateObject());
                Assert.False(value.TryGetProperty("expires_at", out _));
                credential = new(value.GetProperty("credential_id").GetString()!, value.GetProperty("credential_secret").GetString()!);
            }
            using (var request = PairRequest(origin, body))
            using (var response = await client.SendAsync(request, cancellation))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            foreach (var route in new[] { "/martlet/v1/capabilities", "/martlet/v1/status" })
            {
                using var request = Signed(origin, identity, credential!, route);
                using var response = await client.SendAsync(request, cancellation);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                Assert.Empty(json.RootElement.GetProperty("workers").EnumerateArray());
            }
            original = Signed(origin, identity, credential!, "/martlet/v1/version");
            using var accepted = await client.SendAsync(Clone(original), cancellation);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.DoesNotContain(card.Token.Reveal(), console.Text);
            Assert.DoesNotContain(credential!.Secret, console.Text);
        };
        Assert.Equal(0, await HostApplication.RunAsync(Args(origin: origin), console));
        Assert.Contains("Name: Fixture device | Roles: Voice,Perception | paired; permanent until revoked", console.Text);
        Assert.Contains("Listener already started", console.Text);
        Assert.DoesNotContain("90-day", console.Text);
        Assert.False(File.Exists(Path.Combine(Store, "running")));
        using (original)
        {
            var check = false;
            var reopened = new TestConsole("yes", "start", "yes", "list", "revoke", "fixture-device", "",
                "list", "revoke", "fixture-device", "yes", "list", "stop", "yes");
            reopened.BeforeRead = async (prompt, cancellation) =>
            {
                if (check || prompt != "host> " || !reopened.Text.Contains("Loopback listener started.", StringComparison.Ordinal))
                    return;
                check = true;
                Assert.Contains(identity!.SpkiFingerprint, reopened.Text);
                using var client = PinnedGatewayClient.Create(origin, identity);
                using var response = await client.SendAsync(Clone(original!), cancellation);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains("auth.replay", await response.Content.ReadAsStringAsync(cancellation));
                using var fresh = Signed(origin, identity, credential!, "/martlet/v1/version");
                using var accepted = await client.SendAsync(fresh, cancellation);
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            };
            Assert.Equal(0, await HostApplication.RunAsync(Args("open", origin), reopened));
            Assert.True(check);
            Assert.Contains("Revocation committed: 1 credential(s)", reopened.Text);
            Assert.Contains("No paired registrations.", reopened.Text);
        }
        var revoked = new TestConsole("yes", "start", "yes", "stop", "yes");
        var verified = false;
        revoked.BeforeRead = async (prompt, cancellation) =>
        {
            if (verified || prompt != "host> " || !revoked.Text.Contains("Loopback listener started.", StringComparison.Ordinal)) return;
            verified = true;
            using var client = PinnedGatewayClient.Create(origin, identity!);
            using var request = Signed(origin, identity!, credential!, "/martlet/v1/version");
            using var response = await client.SendAsync(request, cancellation);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        };
        Assert.Equal(0, await HostApplication.RunAsync(Args("open", origin), revoked));
        Assert.True(verified);
    }

    [Fact]
    public async Task Cancel_exception_and_EOF_drain_and_release_the_owner()
    {
        var origin = FreeOrigin();
        Assert.Equal(0, await HostApplication.RunAsync(Args(origin: origin), new TestConsole("yes")));
        foreach (var fail in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var console = new TestConsole("yes", "start", "yes");
            console.BeforeRead = (_, _) =>
            {
                if (console.Text.Contains("Loopback listener started.", StringComparison.Ordinal))
                {
                    if (fail) throw new InvalidOperationException("SECRET-FAILURE");
                    cancellation.Cancel();
                }
                return Task.CompletedTask;
            };
            Assert.Equal(fail ? 4 : 130, await HostApplication.RunAsync(Args("open", origin), console, cancellation.Token));
            Assert.DoesNotContain("SECRET-FAILURE", console.Text);
            Assert.False(File.Exists(Path.Combine(Store, "running")));
            Assert.Equal(0, await HostApplication.RunAsync(Args("open", origin), new TestConsole("yes", "start", "yes")));
        }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("revoke")]
    [InlineData("pair")]
    public async Task EOF_at_confirmation_closes_without_another_input_read(string command)
    {
        var origin = FreeOrigin();
        var inputs = new List<string?> { "yes", "start", "yes", command };
        if (command is "revoke" or "pair") inputs.Add("fixture-device");
        if (command == "pair") inputs.AddRange(["Fixture", "voice"]);
        inputs.Add(null);
        var reads = 0;
        var console = new TestConsole(inputs.ToArray())
        {
            BeforeRead = (_, _) =>
            {
                Assert.True(++reads <= inputs.Count, "EOF must not request any subsequent input.");
                return Task.CompletedTask;
            }
        };
        Assert.Equal(0, await HostApplication.RunAsync(Args(origin: origin), console));
        Assert.Equal(inputs.Count, reads);
        Assert.False(File.Exists(Path.Combine(Store, "running")));
        Assert.Equal(0, await HostApplication.RunAsync(Args("open", origin), new TestConsole("yes", "start", "yes")));
    }

    [Fact]
    public async Task Cleanup_failure_and_broken_console_do_not_skip_disposal_or_claim_clean_success()
    {
        var origin = FreeOrigin();
        FileStream? blockFenceDeletion = null;
        var console = new TestConsole("yes", "start", "yes", "stop", "yes");
        console.BeforeRead = (prompt, _) =>
        {
            if (prompt.StartsWith("Stop the listener", StringComparison.Ordinal))
                blockFenceDeletion = new(Path.Combine(Store, "running"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Task.CompletedTask;
        };
        console.OnWrite = text =>
        {
            if (text.StartsWith("host.cleanup_failed:", StringComparison.Ordinal))
                throw new IOException("OUTPUT-FAILURE");
        };
        try
        {
            Assert.Equal(5, await HostApplication.RunAsync(Args(origin: origin), console));
            Assert.NotNull(blockFenceDeletion);
            Assert.True(File.Exists(Path.Combine(Store, "running")));
            Assert.DoesNotContain("Stopped and closed cleanly.", console.Text);
        }
        finally { blockFenceDeletion?.Dispose(); }
        Assert.Equal(0, await HostApplication.RunAsync(Args("open", origin), new TestConsole("yes", "start", "yes")));
    }

    [Fact]
    public async Task Output_failure_after_clean_close_is_not_misreported_as_failed_drain()
    {
        var console = new TestConsole("yes")
        {
            OnWrite = text =>
            {
                if (text.StartsWith("Stopped and closed cleanly.", StringComparison.Ordinal))
                    throw new IOException("OUTPUT-FAILURE");
            }
        };
        Assert.Equal(4, await HostApplication.RunAsync(Args(), console));
        Assert.DoesNotContain("host.cleanup_failed", console.Text);
        Assert.False(File.Exists(Path.Combine(Store, "running")));
        Assert.Equal(0, await HostApplication.RunAsync(Args("open"), new TestConsole("yes")));
    }

    [Fact]
    public async Task Malformed_and_old_state_is_preserved_with_sanitized_recovery()
    {
        Assert.Equal(0, await HostApplication.RunAsync(Args(), new TestConsole("yes")));
        var authority = Path.Combine(Store, "authority.bin");
        foreach (var bytes in new[] { Encoding.ASCII.GetBytes("SECRET malformed"), Encoding.ASCII.GetBytes("MRTLHM01" + new string('x', 256)) })
        {
            File.WriteAllBytes(authority, bytes);
            var console = new TestConsole("yes");
            Assert.Equal(4, await HostApplication.RunAsync(Args("open"), console));
            Assert.Equal(bytes, File.ReadAllBytes(authority));
            Assert.DoesNotContain("SECRET", console.Text);
            Assert.Contains("Do not delete", console.Text);
            if (bytes[0] == 'M')
                Assert.Contains("MigrationRequired", console.Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_executable_help_and_noargs_exit_passively(bool help)
    {
        var result = await RunProcess(typeof(HostOptions).Assembly.Location, help ? ["--help"] : []);
        Assert.Equal(0, result.Exit);
        Assert.Contains("default No", result.Output);
        Assert.Empty(result.Error);
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task Actual_executable_rejects_redirected_yes_and_unattended_flags()
    {
        var result = await RunProcess(typeof(HostOptions).Assembly.Location, Args(), "yes\n");
        Assert.Equal(3, result.Exit);
        Assert.Contains("console.required", result.Output);
        result = await RunProcess(typeof(HostOptions).Assembly.Location, [.. Args(), "--approve", "SECRET"]);
        Assert.Equal(2, result.Exit);
        Assert.DoesNotContain("SECRET", result.Output + result.Error);
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task Owned_subprocess_interruption_preserves_identity_and_releases_listener_after_PID_exit()
    {
        var origin = FreeOrigin();
        GatewayHostIdentity? identity = null;
        FixtureCredential? credential = null;
        var initial = new TestConsole("yes", "start", "yes", "pair", "fixture-device", "Fixture", "voice", "yes");
        initial.Disclosure = async (card, cancellation) =>
        {
            identity = new() { HostId = card.HostId, SpkiFingerprint = card.SpkiFingerprint };
            using var client = PinnedGatewayClient.Create(origin, identity);
            using var request = PairRequest(origin, PairingBody(card));
            using var response = await client.SendAsync(request, cancellation);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            credential = new(json.RootElement.GetProperty("credential_id").GetString()!,
                json.RootElement.GetProperty("credential_secret").GetString()!);
        };
        Assert.Equal(0, await HostApplication.RunAsync(Args(origin: origin), initial));
        using var original = Signed(origin, identity!, credential!, "/martlet/v1/version");
        using var process = Process.Start(StartInfo(typeof(HostTests).Assembly.Location,
            ["--owned-probe", Store, origin.CanonicalOrigin]))!;
        try
        {
            Assert.Equal("OWNED-READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.False(process.HasExited);
            Assert.True(File.Exists(Path.Combine(Store, "running")));
            using var client = PinnedGatewayClient.Create(origin, identity!);
            using var request = Clone(original);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var busy = new TestConsole("yes");
            Assert.Equal(4, await HostApplication.RunAsync(Args("open", origin), busy));
            Assert.Contains("StoreBusy", busy.Text);
        }
        finally { await StopOwnedProcess(process); }
        var reopened = new TestConsole("yes", "start", "yes", "list");
        var checkedReplay = false;
        reopened.BeforeRead = async (prompt, cancellation) =>
        {
            if (checkedReplay || prompt != "host> " || !reopened.Text.Contains("Loopback listener started.", StringComparison.Ordinal)) return;
            checkedReplay = true;
            using var client = PinnedGatewayClient.Create(origin, identity!);
            using var request = Clone(original);
            using var response = await client.SendAsync(request, cancellation);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("auth.replay", await response.Content.ReadAsStringAsync(cancellation));
        };
        Assert.Equal(0, await HostApplication.RunAsync(Args("open", origin), reopened));
        Assert.True(checkedReplay);
        Assert.Contains("paired; permanent until revoked", reopened.Text);
        Assert.Equal(initial.Output.Single(line => line.StartsWith("Opened host:", StringComparison.Ordinal)),
            reopened.Output.Single(line => line.StartsWith("Opened host:", StringComparison.Ordinal)));
        Assert.False(File.Exists(Path.Combine(Store, "running")));
    }

    [Fact]
    public async Task Private_native_console_discloses_erases_restores_and_bounds_input()
    {
        var result = await RunProcess(typeof(HostTests).Assembly.Location, ["--console-probe", Store, FreeOrigin().CanonicalOrigin]);
        Assert.True(result.Exit == 0, result.Output);
        Assert.Equal("CONSOLE-PASS", result.Output.Trim());
        Assert.Empty(result.Error);
        Assert.False(File.Exists(Path.Combine(Store, "running")));
    }

    private static GatewayOrigin FreeOrigin()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new($"https://127.0.0.1:{port}");
    }
    private static string PairingBody(GatewayPairingCard card) => JsonSerializer.Serialize(new
    {
        protocol_version = new { major = 2, minor = 0 }, pairing_id = card.PairingId,
        pairing_token = card.Token.Reveal(), host_id = card.HostId,
        spki_fingerprint = card.SpkiFingerprint, device_id = "fixture-device"
    });
    private static HttpRequestMessage PairRequest(GatewayOrigin origin, string body) =>
        new(HttpMethod.Post, origin.CanonicalOrigin + "/martlet/v1/pair") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed record FixtureCredential(string Id, string Secret);
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpRequestMessage Signed(GatewayOrigin origin, GatewayHostIdentity identity, FixtureCredential credential, string route)
    {
        var nonce = Base64(RandomNumberGenerator.GetBytes(24));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var canonical = string.Join('\n', "martlet-request-v1", identity.HostId, credential.Id, "GET", route, "voice", timestamp, nonce,
            Convert.ToHexStringLower(SHA256.HashData([])));
        var secret = Convert.FromBase64String(credential.Secret.Replace('-', '+').Replace('_', '/') + "=");
        var key = SHA256.HashData(secret);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, origin.CanonicalOrigin + route);
            request.Headers.TryAddWithoutValidation("Authorization", $"Martlet-HMAC {credential.Id}.{Base64(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(canonical)))}");
            request.Headers.TryAddWithoutValidation("X-Martlet-Nonce", nonce);
            request.Headers.TryAddWithoutValidation("X-Martlet-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-Martlet-Role", "voice");
            return request;
        }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(key); }
    }
    private static HttpRequestMessage Clone(HttpRequestMessage original)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return copy;
    }
    private static ProcessStartInfo StartInfo(string assembly, string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(assembly)!
        };
        start.ArgumentList.Add(assembly);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    private static async Task<(int Exit, string Output, string Error)> RunProcess(string assembly, string[] args, string? input = null)
    {
        using var process = Process.Start(StartInfo(assembly, args))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return (process.ExitCode, await output, await error);
        }
        finally { await StopOwnedProcess(process); }
    }
    private static async Task StopOwnedProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    private sealed class Audit : IGatewayAuditSink { public void Record(GatewayAuditEvent gatewayEvent) { } }
}
