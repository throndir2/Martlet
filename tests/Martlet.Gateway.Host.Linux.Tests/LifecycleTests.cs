using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Gateway.Host.Linux;
using Martlet.Gateway.Persistence;
using Martlet.Gateway.Persistence.Portable.Tests;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Martlet.Gateway.Host.Linux.Tests;

internal sealed class FixtureTerminal(params string?[] input) : IHostTerminal
{
    internal readonly Queue<string?> Input = new(input);
    internal bool Interactive = true, DisclosureSupported = true;
    internal Func<GatewayPairingCard, Task>? Disclose;
    internal Action<string>? BeforeRead;
    internal int Disclosures;
    public void Check() { if (!Interactive) throw new HostTerminalException(); }
    public void CheckDisclosure() { Check(); if (!DisclosureSupported) throw new HostTerminalException(); }
    public ValueTask<string?> ReadAsync(string prompt, int maximum, CancellationToken cancellation)
    {
        BeforeRead?.Invoke(prompt);
        return ValueTask.FromResult(Input.Count == 0 ? null : Input.Dequeue());
    }
    public async ValueTask DiscloseAsync(GatewayPairingCard card, CancellationToken cancellation)
    {
        CheckDisclosure();
        Disclosures++;
        if (Disclose is not null) await Disclose(card);
    }
    public void Dispose() { }
}

internal sealed class FixturePlatform : IHostPlatform, IDisposable
{
    internal FakeLinuxFileSystem Fs = new();
    internal FixtureTerminal Terminal = new();
    internal int Opens;
    internal DurableGatewayHost? Owner;
    internal Action<StoreStep>? Fault { get; set; }
    internal GatewayOrigin Origin = FreeOrigin();
    internal FixturePlatform() => ConfigurationTests.WriteConfig(Fs, ConfigurationTests.Config(Origin.CanonicalOrigin));
    public LinuxControlDirectory OpenControl(string path) => new(path, Fs);
    public IHostTerminal OpenTerminal() => Terminal;
    public DurableGatewayHost OpenHost(string command, HostConfiguration config, ServiceApproval? approval,
        CancellationToken cancellation)
    {
        Opens++;
        return Owner = DurableGatewayHost.Open(config.StateDirectory, config.HostId, config.Binding.Origin,
            [], new QuietAudit(), LocalGatewayDecision.Enable,
            command switch { "init" => HostOpenMode.Create, "rebind" => HostOpenMode.Rebind, _ => HostOpenMode.Open },
            TimeProvider.System, Fault, cancellation, storageBackend: GatewayStorageBackend.LinuxServicePermissions,
            linuxFileSystem: Fs, explicitBinding: true, expectedIdentity: approval?.Identity);
    }
    internal Task<int> Run(string command, StringWriter output, CancellationToken cancellation = default) =>
        HostApplication.RunAsync([command, "--config", "/srv/martlet/host.json"], output, cancellation, this);
    internal static GatewayOrigin FreeOrigin()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var origin = new GatewayOrigin($"https://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}");
        socket.Stop();
        return origin;
    }
    public void Dispose() => Fs.Dispose();
}

public sealed class LifecycleTests
{
    [Fact]
    public async Task Help_validation_status_and_refusal_do_not_open_authority()
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--help" }, new[] { "help" }, new[] { "-h" } })
            Assert.Equal(0, await HostApplication.RunAsync(args, output, platform: platform));
        Assert.Equal(0, await platform.Run("validate", output));
        Assert.Equal(0, await platform.Run("status", output));
        Assert.Contains("not-observed", output.ToString());
        Assert.Equal(3, await platform.Run("serve", output));
        platform.Terminal = new("");
        Assert.Equal(3, await platform.Run("init", output));
        platform.Terminal = new("yes") { Interactive = false };
        Assert.Equal(3, await platform.Run("init", output));
        Assert.Equal(0, platform.Opens);
        Assert.False(platform.Fs.Parent.Children.ContainsKey("store"));
    }

    [Fact]
    public async Task Actual_admin_pairing_then_approved_daemon_keeps_same_identity_and_replay()
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        IssuedDeviceCredential? credential = null;
        GatewayHostIdentity? identity = null;
        HttpRequestMessage? signed = null;
        platform.Terminal = new("yes", "start", "yes", "pair", "fixture-device", "Fixture",
            "voice", "yes", "list", "approve-service", "yes", "stop", "yes")
        {
            Disclose = async card =>
            {
                identity = platform.Owner!.Identity!;
                using var client = PinnedGatewayClient.Create(platform.Origin, identity);
                credential = await PairCard(client, platform.Origin, card);
                using var reused = await Exchange(client, platform.Origin, card);
                Assert.NotEqual(HttpStatusCode.Created, reused.StatusCode);
                signed = OwnerScenario.Sign(platform.Origin, identity, credential);
                using var request = Clone(signed);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        };
        Assert.Equal(0, await platform.Run("init", output));
        Assert.Equal(1, platform.Terminal.Disclosures);
        Assert.Contains("fixture-device", output.ToString());
        Assert.DoesNotContain(credential!.Secret.Reveal(), output.ToString());
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var servingOutput = new WatchingWriter("serving:");
        var serving = platform.Run("serve", servingOutput, cancel.Token);
        await servingOutput.Seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (signed!)
        using (var client = PinnedGatewayClient.Create(platform.Origin, identity!))
        {
            Assert.Equal(identity, platform.Owner!.Identity);
            Assert.IsType<PairedDeviceLifetime>(Assert.Single(platform.Owner.ListRegistrations()).Lifetime);
            using var replay = Clone(signed!);
            using var replayed = await client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);
            using var caps = new HttpRequestMessage(HttpMethod.Get, platform.Origin.CanonicalOrigin + "/martlet/v1/capabilities");
            new GatewayRequestSigner(identity!, credential).Sign(caps, GatewayRole.Voice);
            using var capabilities = await client.SendAsync(caps);
            using var json = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
            Assert.Equal(0, json.RootElement.GetProperty("workers").GetArrayLength());
            Assert.Equal(0, json.RootElement.GetProperty("routes").GetArrayLength());
            using var healthOutput = new StringWriter();
            Assert.Equal(0, await platform.Run("health", healthOutput));
            Assert.DoesNotContain("model-ready", healthOutput.ToString());
        }
        cancel.Cancel();
        Assert.Equal(130, await serving);
        Assert.DoesNotContain("running", platform.Fs.Store.Children.Keys);
        platform.Terminal = new("yes", "revoke", "fixture-device", "yes", "stop", "yes");
        Assert.Equal(0, await platform.Run("admin", output));
        platform.Terminal = new("yes", "list");
        Assert.Equal(0, await platform.Run("admin", output));
        Assert.Contains("No registrations.", output.ToString());
    }

    [Fact]
    public async Task Changed_config_and_receipt_cannot_start_or_repair_missing_state()
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        platform.Terminal = new("yes", "approve-service", "yes");
        Assert.Equal(0, await platform.Run("init", output));
        ConfigurationTests.WriteConfig(platform.Fs,
            platform.Fs.Parent.Children["host.json"].Bytes.Concat(new byte[] { 32 }).ToArray());
        var opened = platform.Opens;
        Assert.Equal(3, await platform.Run("serve", output));
        Assert.Equal(opened, platform.Opens);
        ConfigurationTests.WriteConfig(platform.Fs, ConfigurationTests.Config(platform.Origin.CanonicalOrigin));
        var authority = platform.Fs.Store.Children["authority.bin"];
        var original = authority.Bytes.ToArray();
        authority.Bytes = [1, 2, 3];
        Assert.Equal(4, await platform.Run("serve", output));
        Assert.Equal(new byte[] { 1, 2, 3 }, authority.Bytes);
        authority.Bytes = original;
    }

    [Fact]
    public async Task Changed_config_is_reapproved_in_admin_before_serve()
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        platform.Terminal = new("yes", "approve-service", "yes");
        Assert.Equal(0, await platform.Run("init", output));
        ConfigurationTests.WriteConfig(platform.Fs,
            platform.Fs.Parent.Children["host.json"].Bytes.Concat(new byte[] { 32 }).ToArray());
        Assert.Equal(3, await platform.Run("serve", output));
        platform.Terminal = new("yes", "approve-service", "yes", "stop", "yes");
        Assert.Equal(0, await platform.Run("admin", output));
        using var status = new StringWriter();
        Assert.Equal(0, await platform.Run("status", status));
        Assert.Contains("\"matching\"", status.ToString());
    }

    [Fact]
    public async Task Cleanup_failure_is_not_clean_success_and_preserves_dirty_fence()
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        platform.Terminal = new("yes", "start", "yes");
        platform.Terminal.BeforeRead = prompt =>
        {
            if (platform.Terminal.Input.Count == 0)
                platform.Fs.Fault = call =>
                {
                    if (call == "unlink:running") throw new IOException("SENTINEL-SECRET");
                };
        };
        Assert.Equal(5, await platform.Run("init", output));
        Assert.Contains("cleanup_failed", output.ToString());
        Assert.DoesNotContain("Stopped and closed cleanly", output.ToString());
        Assert.DoesNotContain("SENTINEL-SECRET", output.ToString());
        Assert.True(platform.Fs.Store.Children.ContainsKey("running"));
        platform.Fs.Fault = null;
        platform.Terminal = new("yes");
        Assert.Equal(0, await platform.Run("admin", output));
    }

    [Fact]
    public async Task Unsupported_disclosure_and_EOF_do_not_issue_invitation()
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        platform.Terminal = new("yes", "start", "yes", "pair") { DisclosureSupported = false };
        Assert.Equal(3, await platform.Run("init", output));
        Assert.Equal(0, platform.Terminal.Disclosures);
        platform.Terminal = new("yes", "start", "yes", "pair", null);
        Assert.Equal(0, await platform.Run("admin", output));
        Assert.Equal(0, platform.Terminal.Disclosures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rebind_requires_fresh_approval_and_receipt_removal_failure_blocks_new_serve(bool failRemoval)
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        platform.Terminal = new("yes", "approve-service", "yes");
        Assert.Equal(0, await platform.Run("init", output));
        var identity = platform.Owner!.Identity;
        var oldReceipt = platform.Fs.Parent.Children["service-approval.json"].Bytes.ToArray();
        ConfigurationTests.WriteConfig(platform.Fs, ConfigurationTests.Config("https://192.168.20.4:9443", "privateIp"));
        Assert.Equal(3, await platform.Run("serve", output));
        platform.Terminal = new("no");
        Assert.Equal(3, await platform.Run("rebind", output));
        platform.Terminal = new("yes");
        if (failRemoval)
            platform.Fs.Fault = call => { if (call == "unlink:service-approval.json") throw new IOException("SECRET"); };
        Assert.Equal(failRemoval ? 4 : 0, await platform.Run("rebind", output));
        Assert.Equal(identity, platform.Owner!.Identity);
        if (failRemoval) Assert.Contains("rebind.approval_cleanup_failed", output.ToString());
        Assert.DoesNotContain("SECRET", output.ToString());
        platform.Fs.Fault = null;
        Assert.Equal(3, await platform.Run("serve", output));
        using var directory = new LinuxControlDirectory("/srv/martlet/host.json", platform.Fs);
        directory.WriteApproval(oldReceipt);
        Assert.Equal(3, await platform.Run("serve", output));
    }

    internal static async Task<IssuedDeviceCredential> PairCard(PinnedGatewayClient client, GatewayOrigin origin, GatewayPairingCard card)
    {
        using var response = await Exchange(client, origin, card);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new()
        {
            CredentialId = json.RootElement.GetProperty("credential_id").GetString()!,
            DeviceId = "fixture-device", Roles = [GatewayRole.Voice], Lifetime = new PairedDeviceLifetime(),
            Secret = new(json.RootElement.GetProperty("credential_secret").GetString()!)
        };
    }

    private static async Task<HttpResponseMessage> Exchange(PinnedGatewayClient client, GatewayOrigin origin, GatewayPairingCard card)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, origin.CanonicalOrigin + "/martlet/v1/pair")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                protocol_version = new { major = 2, minor = 0 }, host_id = card.HostId,
                spki_fingerprint = card.SpkiFingerprint, device_id = "fixture-device",
                pairing_id = card.PairingId, pairing_token = card.Token.Reveal()
            }), Encoding.UTF8, "application/json")
        };
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}

internal sealed class WatchingWriter(string prefix) : StringWriter
{
    internal readonly TaskCompletionSource Seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override void WriteLine(string? value)
    {
        base.WriteLine(value);
        if (value?.StartsWith(prefix, StringComparison.Ordinal) == true) Seen.TrySetResult();
    }
}
