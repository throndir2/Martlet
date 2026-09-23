using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway.Persistence.Tests;

public sealed class OwnerTests : NativeTest
{
    [Fact]
    public async Task Default_No_has_no_storage_key_or_listener_side_effects()
    {
        await using var host = DurableGatewayHost.CreateNew(Store, "fixture-host", Origin(), [], new Audit());
        Assert.False(host.Enabled);
        Assert.Null(host.Identity);
        Assert.False(Directory.Exists(Store));
        Assert.Equal(GatewayPersistenceFailure.Disabled,
            (await Assert.ThrowsAsync<GatewayPersistenceException>(() => host.StartAsync().AsTask())).Failure);
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task Actual_pinned_loopback_pairing_and_exact_replay_survive_clean_restart()
    {
        var origin = Origin();
        IssuedDeviceCredential credential;
        HttpRequestMessage original;
        GatewayHostIdentity identity;
        await using (var host = DurableGatewayHost.CreateNew(Store, "fixture-host", origin, [], new Audit(),
            LocalGatewayDecision.Enable))
        {
            identity = host.Identity!;
            await host.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            credential = await Pair(host, client, origin);
            original = Signed(origin, identity, credential);
            using var response = await client.SendAsync(Clone(original));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await host.CloseCleanlyAsync();
        }
        var child = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { typeof(OwnerTests).Assembly.Location, "--clean-probe", Store })
            child.ArgumentList.Add(argument);
        using (var process = Process.Start(child)!)
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(0, process.ExitCode);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        using (original)
        await using (var host = DurableGatewayHost.OpenExisting(Store, origin, [], new Audit(), LocalGatewayDecision.Enable))
        {
            Assert.Equal(identity, host.Identity);
            await host.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            using var replay = await client.SendAsync(Clone(original));
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Equal("auth.replay", await Failure(replay));
            using var request = Signed(origin, identity, credential);
            using var fresh = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            await host.CloseCleanlyAsync();
        }
    }

    [Fact]
    public async Task Same_key_renewal_retains_replay_and_replacement_requires_new_pairing()
    {
        var origin = Origin();
        GatewayHostIdentity identity;
        IssuedDeviceCredential credential;
        HttpRequestMessage first;
        await using (var host = DurableGatewayHost.CreateNew(Store, "fixture-host", origin, [], new Audit(),
            LocalGatewayDecision.Enable))
        {
            identity = host.Identity!;
            await host.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            credential = await Pair(host, client, origin);
            first = Signed(origin, identity, credential);
            using var response = await client.SendAsync(Clone(first));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await host.CloseCleanlyAsync();
        }
        using (first)
        await using (var renewed = DurableGatewayHost.RenewCertificateForLocalHost(Store, origin, [], new Audit(),
            LocalGatewayDecision.Enable))
        {
            Assert.Equal(identity, renewed.Identity);
            await renewed.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            using var replay = await client.SendAsync(Clone(first));
            Assert.Equal("auth.replay", await Failure(replay));
            using var request = Signed(origin, identity, credential);
            using var fresh = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            await renewed.CloseCleanlyAsync();
        }
        await using var replacement = DurableGatewayHost.CreateNew(
            Path.Combine(Root, "new-identity"), "replacement-host", origin, [], new Audit(), LocalGatewayDecision.Enable);
        Assert.NotEqual(identity.SpkiFingerprint, replacement.Identity!.SpkiFingerprint);
        Assert.Empty(replacement.ListRegistrations());
        await replacement.StartAsync();
        using (var oldClient = PinnedGatewayClient.Create(origin, identity))
        using (var request = Signed(origin, identity, credential))
            await Assert.ThrowsAsync<GatewayClientException>(() => oldClient.SendAsync(request).AsTask());
        using var newClient = PinnedGatewayClient.Create(origin, replacement.Identity);
        using var invalid = Signed(origin, replacement.Identity, credential);
        using var denied = await newClient.SendAsync(invalid);
        Assert.Equal("auth.invalid", await Failure(denied));
        await Pair(replacement, newClient, origin);
        await replacement.CloseCleanlyAsync();
    }

    [Fact]
    public async Task Unclean_owner_reopens_without_losing_pairing_or_pin()
    {
        var origin = Origin();
        GatewayHostIdentity identity;
        await using (var host = DurableGatewayHost.CreateNew(Store, "fixture-host", origin, [], new Audit(),
            LocalGatewayDecision.Enable))
        {
            identity = host.Identity!;
            await host.StartAsync();
            using var client = PinnedGatewayClient.Create(origin, identity);
            await Pair(host, client, origin);
        }
        await using var recovered = DurableGatewayHost.OpenExisting(
            Store, origin, [], new Audit(), LocalGatewayDecision.Enable);
        Assert.Equal(identity, recovered.Identity);
        Assert.Single(recovered.ListRegistrations());
        await recovered.CloseCleanlyAsync();
    }

    [Fact]
    public void Public_owner_never_exposes_mutable_authority_certificate_or_snapshot_import()
    {
        var properties = typeof(DurableGatewayHost).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(["Enabled", "Identity"], properties.OrderBy(value => value).ToArray());
        Assert.DoesNotContain(typeof(DurableGatewayHost).GetMethods(), method =>
            method.Name.Contains("Import", StringComparison.Ordinal) || method.Name.Contains("Export", StringComparison.Ordinal));
        Assert.Equal(["Authenticate"], typeof(IGatewayRequestCredentials).GetMethods().Select(method => method.Name).ToArray());
        Assert.Equal(["Exchange"], typeof(IGatewayPairingExchange).GetMethods().Select(method => method.Name).ToArray());
        Assert.DoesNotContain(typeof(DurableGatewayHost).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "Martlet.Gateway.Trust");
    }

    [Fact]
    public async Task Uncertain_listener_stop_retains_ownership_and_cannot_be_marked_clean()
    {
        var origin = Origin();
        await using var host = DurableGatewayHost.CreateNew(Store, "fixture-host", origin, [], new Audit(),
            LocalGatewayDecision.Enable);
        var factory = new ControlledListenerFactory();
        await host.StartAsync(factory);
        using var cancellation = new CancellationTokenSource();
        var closing = host.CloseCleanlyAsync(cancellation.Token).AsTask();
        await factory.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing);
        Assert.True(File.Exists(Path.Combine(Store, "running")));
        Assert.Equal(GatewayPersistenceFailure.StoreBusy, Assert.Throws<GatewayPersistenceException>(() =>
            DurableGatewayHost.OpenExisting(Store, origin, [], new Audit(), LocalGatewayDecision.Enable)).Failure);
        factory.Stopped.SetResult();
        await Assert.ThrowsAsync<GatewayProtocolException>(() => host.CloseCleanlyAsync().AsTask());
        Assert.True(File.Exists(Path.Combine(Store, "running")));
    }

    private sealed class ControlledListenerFactory : IGatewayListenerFactory
    {
        internal TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<GatewayListenerHandle> StartAsync(GatewayTlsBinding binding, RequestDelegate application,
            CancellationToken cancellationToken) => ValueTask.FromResult<GatewayListenerHandle>(new Handle(binding.Origin, this));

        private sealed class Handle(GatewayOrigin origin, ControlledListenerFactory owner) : GatewayListenerHandle
        {
            public override GatewayOrigin Origin => origin;
            public override ValueTask DisposeAsync()
            {
                owner.Stopping.TrySetResult();
                return new(owner.Stopped.Task);
            }
        }
    }

    private static async Task<IssuedDeviceCredential> Pair(DurableGatewayHost host, PinnedGatewayClient client,
        GatewayOrigin origin)
    {
        var card = host.OpenPairing(new()
        {
            DeviceId = "fixture-device", DisplayName = "Fixture device", Roles = [GatewayRole.Voice]
        });
        var json = JsonSerializer.Serialize(new
        {
            protocol_version = new { major = 2, minor = 0 }, pairing_id = card.PairingId,
            pairing_token = card.Token.Reveal(), host_id = card.HostId,
            spki_fingerprint = card.SpkiFingerprint, device_id = "fixture-device"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, origin.CanonicalOrigin + "/martlet/v1/pair")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement;
        Assert.Equal(2, result.GetProperty("protocol_version").GetProperty("major").GetInt32());
        var lifetime = result.GetProperty("lifetime").Deserialize<GatewayCredentialLifetime>();
        Assert.IsType<PairedDeviceLifetime>(lifetime);
        Assert.False(result.TryGetProperty("expires_at", out _));
        return new()
        {
            CredentialId = result.GetProperty("credential_id").GetString()!,
            Secret = new(result.GetProperty("credential_secret").GetString()!),
            DeviceId = result.GetProperty("device_id").GetString()!,
            Roles = [GatewayRole.Voice],
            Lifetime = lifetime!
        };
    }

    private static HttpRequestMessage Signed(GatewayOrigin origin, GatewayHostIdentity identity,
        IssuedDeviceCredential credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, origin.CanonicalOrigin + "/martlet/v1/version");
        new GatewayRequestSigner(identity, credential).Sign(request, GatewayRole.Voice);
        return request;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return copy;
    }

    private static async Task<string> Failure(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString()!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Automatic_same_key_renewal_preserves_pairing_at_open_and_live_handshake(bool atOpen)
    {
        var clock = new Clock();
        var elapsed = TimeSpan.FromDays(atOpen ? 100 : 61);
        clock.Utc -= elapsed;
        IssuedDeviceCredential credential;
        GatewayHostIdentity identity;
        using (var initial = new NativeAuthority(Store, clock, create: true))
        {
            identity = initial.Identity;
            credential = initial.Issue();
            initial.Clean();
        }
        if (atOpen) clock.Advance(elapsed);
        var origin = Origin();
        await using var host = DurableGatewayHost.Open(Store, null, origin, [], new Audit(),
            LocalGatewayDecision.Enable, HostOpenMode.Open, clock);
        await host.StartAsync();
        if (!atOpen) clock.Advance(elapsed);
        using var client = PinnedGatewayClient.Create(origin, identity);
        using var request = Signed(origin, identity, credential);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(identity, host.Identity);
        Assert.Single(host.ListRegistrations());
        await host.CloseCleanlyAsync();
        using var reopened = new NativeAuthority(Store, clock);
        var bytes = reopened.Storage.CopyCertificate();
        try
        {
            using var certificate = HostCertificate.Load(bytes);
            Assert.True(certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(80));
            Assert.Equal(identity.SpkiFingerprint, new SystemGatewayCrypto().SpkiFingerprint(certificate));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}
