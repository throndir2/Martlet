using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

internal class ManualGatewayClock(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return utcNow;
    }

    internal void Advance(TimeSpan duration)
    {
        lock (gate)
            utcNow += duration;
    }
}

internal sealed class SteppingGatewayClock(DateTimeOffset utcNow) : ManualGatewayClock(utcNow)
{
    private int readsUntilAdvance = -1;
    private TimeSpan advance;
    internal void AdvanceOnRead(int reads, TimeSpan duration)
    {
        readsUntilAdvance = reads;
        advance = duration;
    }
    public override DateTimeOffset GetUtcNow()
    {
        if (Interlocked.Decrement(ref readsUntilAdvance) == 0)
            Advance(advance);
        return base.GetUtcNow();
    }
}

internal sealed class RecordingAuditSink : IGatewayAuditSink
{
    private readonly ConcurrentQueue<GatewayAuditEvent> events = new();
    internal IReadOnlyList<GatewayAuditEvent> Events => events.ToArray();
    public void Record(GatewayAuditEvent gatewayEvent) => events.Enqueue(gatewayEvent);
}

internal sealed class SyntheticWorker(
    GatewayWorkerCapabilities capabilities,
    Func<GatewayWorkerStatus> status) : IGatewayWorker
{
    public GatewayWorkerCapabilities Capabilities { get; } = capabilities;
    public ValueTask<GatewayWorkerStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(status());
    }
}

internal sealed class UnavailableWorker(GatewayWorkerCapabilities capabilities) : IGatewayWorker
{
    public GatewayWorkerCapabilities Capabilities { get; } = capabilities;
    public ValueTask<GatewayWorkerStatus> ReadStatusAsync(CancellationToken cancellationToken) =>
        throw new GatewayWorkerUnavailableException();
}

internal sealed class GatewayTestHost : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = CreateJson();
    internal ManualGatewayClock Clock { get; }
    internal X509Certificate2 Certificate { get; }
    internal GatewayOrigin Origin { get; }
    internal GatewayHostIdentity Identity { get; }
    internal GatewayServer Server { get; }
    internal RecordingAuditSink Audit { get; }
    internal GatewayListenerHandle Listener { get; private set; } = null!;
    internal PinnedGatewayClient Client { get; private set; } = null!;

    private GatewayTestHost(
        IEnumerable<IGatewayWorker>? workers,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers,
        ManualGatewayClock? clock,
        string hostId)
    {
        Clock = clock ?? new(new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero));
        Certificate = CreateCertificate(Clock.GetUtcNow());
        Origin = new($"https://127.0.0.1:{ReserveLoopbackPort()}");
        Identity = GatewayHostIdentity.FromCertificate(hostId, Certificate);
        Audit = new();
        Server = new(Identity, Origin, workers ?? DefaultWorkers(Clock), Audit, Clock,
            inferenceWorkers: inferenceWorkers);
    }

    internal static async ValueTask<GatewayTestHost> StartAsync(
        IEnumerable<IGatewayWorker>? workers = null,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null,
        ManualGatewayClock? clock = null,
        string hostId = "fixture-host")
    {
        var host = new GatewayTestHost(workers, inferenceWorkers, clock, hostId);
        try
        {
            var binding = new GatewayTlsBinding(
                host.Origin, host.Identity, host.Certificate, host.Clock);
            host.Listener = await host.Server.StartAsync(
                binding, new KestrelGatewayListenerFactory());
            host.Client = PinnedGatewayClient.Create(
                host.Origin, host.Identity, host.Clock);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    internal GatewayPairingCard OpenPairing(
        GatewayRole role = GatewayRole.Voice,
        string deviceId = "fixture-device") =>
        Server.Pairing.OpenWindow(new()
        {
            DeviceId = deviceId,
            DisplayName = "Fixture device",
            Roles = [role]
        });

    internal async ValueTask<IssuedDeviceCredential> PairAsync(
        GatewayRole role = GatewayRole.Voice,
        string deviceId = "fixture-device")
    {
        var card = OpenPairing(role, deviceId);
        using var response = await SendPairingAsync(card, card.SpkiFingerprint, deviceId: deviceId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("protocol_version").GetProperty("major").GetInt32());
        var lifetime = root.GetProperty("lifetime").Deserialize<GatewayCredentialLifetime>(Json);
        Assert.IsType<PairedDeviceLifetime>(lifetime);
        return new()
        {
            CredentialId = root.GetProperty("credential_id").GetString()!,
            DeviceId = root.GetProperty("device_id").GetString()!,
            Roles = root.GetProperty("roles").EnumerateArray()
                .Select(item => ParseRole(item.GetString()!)).ToArray(),
            Secret = new(root.GetProperty("credential_secret").GetString()!),
            Lifetime = lifetime!
        };
    }

    internal ValueTask<HttpResponseMessage> SendPairingAsync(
        GatewayPairingCard card,
        string fingerprint,
        string? token = null,
        string deviceId = "fixture-device")
    {
        var proof = new GatewayPairingProof
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            PairingId = card.PairingId,
            PairingToken = token ?? card.Token.Reveal(),
            HostId = card.HostId,
            SpkiFingerprint = fingerprint,
            DeviceId = deviceId
        };
        var request = new HttpRequestMessage(
            HttpMethod.Post, Origin.CanonicalOrigin + "/martlet/v1/pair")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(proof, Json), Encoding.UTF8, "application/json")
        };
        return Client.SendAsync(request);
    }

    internal HttpRequestMessage SignedGet(
        string path,
        GatewayRole role,
        GatewayRequestSigner signer)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, Origin.CanonicalOrigin + path);
        signer.Sign(request, role);
        return request;
    }

    internal HttpRequestMessage SignedPost(
        string path, GatewayRole role, GatewayRequestSigner signer, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Origin.CanonicalOrigin + path)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        signer.Sign(request, role, body);
        return request;
    }

    internal static HttpRequestMessage ClonePost(HttpRequestMessage source, byte[] body)
    {
        var clone = Clone(source);
        clone.Content = new ByteArrayContent(body);
        clone.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        return clone;
    }

    internal static HttpRequestMessage Clone(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    internal static async ValueTask<string> FailureCode(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (Listener is not null)
            await Listener.DisposeAsync();
        Certificate.Dispose();
    }

    internal static GatewayWorkerCapabilities Capabilities(
        string id,
        GatewayWorkerKind kind,
        GatewayRole role) => new()
    {
        WorkerId = id,
        Kind = kind,
        RequiredRole = role,
        AdapterVersion = "fixture-v1",
        ModelId = "fixture-model-v1",
        ModelRevision = "fixture-revision",
        MaxInputBytes = 16_384,
        MaxConcurrency = 1,
        TextDeltas = kind == GatewayWorkerKind.OllamaLlm,
        AudioTransport = kind == GatewayWorkerKind.F5Tts,
        Cancellation = GatewayCancellationCapability.RequestAbort
    };

    private static IEnumerable<IGatewayWorker> DefaultWorkers(ManualGatewayClock clock) =>
    [
        Worker("ollama-private", GatewayWorkerKind.OllamaLlm, GatewayRole.Voice, clock),
        Worker("f5-private", GatewayWorkerKind.F5Tts, GatewayRole.Voice, clock),
        Worker("vision-private", GatewayWorkerKind.Vision, GatewayRole.Perception, clock),
        Worker("memory-private", GatewayWorkerKind.Memory, GatewayRole.Memory, clock)
    ];

    private static IGatewayWorker Worker(
        string id,
        GatewayWorkerKind kind,
        GatewayRole role,
        ManualGatewayClock clock) =>
        new SyntheticWorker(Capabilities(id, kind, role), () => new()
        {
            State = GatewayWorkerState.Ready,
            QueueDepth = 0,
            ObservedAt = clock.GetUtcNow()
        });

    internal static X509Certificate2 CreateCertificate(
        DateTimeOffset manualNow,
        bool includeIpSan = true)
    {
        var systemNow = TimeProvider.System.GetUtcNow();
        var notBefore = (manualNow <= systemNow ? manualNow : systemNow).AddDays(-1);
        var notAfter = (manualNow >= systemNow ? manualNow : systemNow).AddYears(1);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Martlet gateway fixture",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        if (includeIpSan)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var ephemeral = request.CreateSelfSigned(notBefore, notAfter);
        const string password = "martlet-fixture-only";
        var pfx = ephemeral.Export(X509ContentType.Pkcs12, password);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static GatewayRole ParseRole(string role) => role switch
    {
        "voice" => GatewayRole.Voice,
        "perception" => GatewayRole.Perception,
        "memory" => GatewayRole.Memory,
        _ => throw new InvalidOperationException("Unexpected role.")
    };

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}
