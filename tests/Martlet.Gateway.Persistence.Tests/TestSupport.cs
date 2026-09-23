using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;

[assembly: SupportedOSPlatform("windows")]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Martlet.Gateway.Persistence.Tests;

internal sealed class Clock : TimeProvider
{
    internal DateTimeOffset Utc = DateTimeOffset.UtcNow.AddSeconds(-1);
    internal long Ticks;
    internal long Frequency = TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => Utc;
    public override long GetTimestamp() => Ticks;
    public override long TimestampFrequency => Frequency;
    internal void Advance(TimeSpan delta, bool utc = true)
    {
        if (utc) Utc += delta;
        Ticks += delta.Ticks;
    }
}

internal sealed class Audit : IGatewayAuditSink
{
    public void Record(GatewayAuditEvent gatewayEvent) { }
}

internal sealed class NativeAuthority : IDisposable
{
    internal WindowsAuthorityStore Storage { get; }
    internal GatewayCredentialStore Credentials { get; }
    internal GatewayPairingService Pairing { get; }
    internal GatewayHostIdentity Identity { get; }
    internal TimeProvider Clock { get; }

    internal NativeAuthority(string path, TimeProvider clock, bool create = false,
        Action<StoreStep>? fault = null, Guid? boot = null)
    {
        Clock = clock;
        if (create)
        {
            using var certificate = HostCertificate.Create(clock.GetUtcNow());
            var bytes = certificate.Export(X509ContentType.Pkcs12);
            try { Storage = WindowsAuthorityStore.Create(path, "fixture-host", bytes, clock.GetUtcNow(), fault, clock); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else
            Storage = WindowsAuthorityStore.Open(path, clock.GetUtcNow(), fault, boot);
        var encoded = Storage.CopyCertificate();
        try
        {
            using var certificate = HostCertificate.Load(encoded);
            Identity = GatewayHostIdentity.FromCertificate(Storage.HostId, certificate);
            Credentials = new(Identity, clock, Storage.Initial, Storage, Storage.InitialSameBoot);
            Pairing = new(Identity, new("https://127.0.0.1:9443"), Credentials, clock);
        }
        catch
        {
            Storage.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    internal IssuedDeviceCredential Issue(string device = "fixture-device") =>
        Credentials.Issue(device, "Fixture device", [GatewayRole.Voice]);

    internal GatewaySignedRequest Signed(IssuedDeviceCredential credential,
        CancellationToken cancellationToken = default, DateTimeOffset? timestamp = null)
    {
        Assert.True(Base64Url.TryDecode(credential.Secret.Reveal(), 32, out var secret));
        var key = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
        var time = DateTimeOffset.FromUnixTimeSeconds((timestamp ?? Clock.GetUtcNow()).ToUnixTimeSeconds());
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(24));
        var bytes = GatewayRequestSigner.Canonical(Identity.HostId, credential.CredentialId,
            "GET", "/martlet/v1/version", "voice", time.ToUnixTimeSeconds(), nonce, SHA256.HashData([]));
        var signature = HMACSHA256.HashData(key, bytes);
        CryptographicOperations.ZeroMemory(key);
        return new()
        {
            CredentialId = credential.CredentialId,
            Signature = signature,
            Nonce = nonce,
            Timestamp = time,
            Role = GatewayRole.Voice,
            CanonicalBytes = bytes,
            CancellationToken = cancellationToken
        };
    }

    internal void Clean()
    {
        Pairing.Close();
        Credentials.Complete();
    }

    public void Dispose()
    {
        Pairing.Close();
        Credentials.Close();
        Storage.Dispose();
    }
}

public abstract class NativeTest : IDisposable
{
    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "Martlet.Hmac." + Guid.NewGuid().ToString("N"));
    protected string Store => Path.Combine(Root, "store");
    protected NativeTest()
    {
        Assert.True(OperatingSystem.IsWindows(), "Actual Windows DPAPI tests cannot be replaced with a platform skip.");
        Assert.Equal("NTFS", new DriveInfo(Path.GetPathRoot(Root)!).DriveFormat);
        Directory.CreateDirectory(Root);
    }

    internal static GatewayOrigin Origin()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new($"https://127.0.0.1:{port}");
    }

    protected static void Code(string expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<GatewayProtocolException>(action).Failure.Code);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
