using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>Nonsecret identity of a paired Martlet host that relays Audio2Face. The device secret lives in the OS vault.</summary>
public sealed record Audio2FaceHostPairing
{
    public required string Origin { get; init; }
    public required string HostId { get; init; }
    public required string SpkiFingerprint { get; init; }
    public required string DeviceId { get; init; }
    public required string CredentialId { get; init; }

    public void Validate()
    {
        Audio2FaceHostClient.CanonicalOrigin(Origin);
        Audio2FaceHostClient.RequireIdentifier(HostId, "host ID");
        Audio2FaceHostClient.RequireIdentifier(DeviceId, "device ID");
        Audio2FaceHostClient.RequireFingerprint(SpkiFingerprint);
        if (!Audio2FaceHostClient.TryBase64Url(CredentialId, 16, out _))
            throw new Audio2FaceHostException("pairing.invalid", "The saved host credential reference is invalid; pair again.");
    }
}

public sealed class Audio2FaceHostException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record Audio2FaceHostRoute(
    string Path, string RouteId, string ContractId, string ContractVersion, string DestinationId, string WorkerId,
    string AdapterVersion, string ModelId, string ModelRevision, string ModelSha256, string ArtifactIdentitySha256,
    int MaximumInputBytes, int MaximumEventBytes, int MaximumStreamBytes, TimeSpan MaximumDuration);

/// <summary>The single-line invitation a Martlet host shows during "pair" (martlet-pair-v1.&lt;base64url JSON&gt;).</summary>
public sealed record HostPairingCode(string Origin, string HostId, string SpkiFingerprint, string PairingId, string Token)
{
    public const string Prefix = "martlet-pair-v1.";

    public static HostPairingCode Parse(string? text)
    {
        var value = text?.Trim() ?? "";
        try
        {
            if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length > 2048) throw new FormatException();
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(value.AsSpan(Prefix.Length)));
            var root = document.RootElement;
            if (root.EnumerateObject().Count() != 5) throw new FormatException();
            string Field(string name) => root.GetProperty(name).GetString() ?? throw new FormatException();
            var code = new HostPairingCode(Field("o"), Field("h"), Field("s"), Field("i"), Field("t"));
            Audio2FaceHostClient.CanonicalOrigin(code.Origin);
            Audio2FaceHostClient.RequireIdentifier(code.HostId, "host ID");
            Audio2FaceHostClient.RequireFingerprint(code.SpkiFingerprint);
            if (!Audio2FaceHostClient.TryBase64Url(code.PairingId, 16, out _) || code.Token.Length != 43) throw new FormatException();
            return code;
        }
        catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new Audio2FaceHostException("pairing.invalid",
                "Paste the whole pairing code the host shows (it starts with martlet-pair-v1.).");
        }
    }

    public Task<(Audio2FaceHostPairing Pairing, string Secret)> PairAsync(string deviceId, CancellationToken token = default) =>
        Audio2FaceHostClient.PairAsync(Origin, HostId, SpkiFingerprint, deviceId, PairingId, Token, token);

    public override string ToString() => $"Pairing code for {HostId} at {Origin} (token omitted)";
}

/// <summary>One facial frame; SampleOffset is relative to the first sample of the submitted chunk.</summary>
public sealed record RemoteFaceFrame(long SampleOffset, IReadOnlyDictionary<string, double> Blendshapes);

/// <summary>
/// Minimal client for the Martlet gateway protocol 2 used by the Audio2Face relay: pinned TLS,
/// one-use pairing and HMAC-signed requests (same wire format as <c>Martlet.Gateway</c>).
/// </summary>
public static class Audio2FaceHostClient
{
    public const string RouteId = "martlet.gateway.audio2face.v1";
    internal const string ContractId = "martlet.audio2face-relay";

    public static async Task<(Audio2FaceHostPairing Pairing, string Secret)> PairAsync(
        string origin, string hostId, string spkiFingerprint, string deviceId, string pairingId, string pairingToken,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalOrigin(origin);
        RequireIdentifier(hostId, "host ID");
        RequireIdentifier(deviceId, "device ID");
        RequireFingerprint(spkiFingerprint);
        if (!TryBase64Url(pairingId, 16, out _) || pairingToken is not { Length: 43 })
            throw new Audio2FaceHostException("pairing.invalid", "Copy the pairing ID and token exactly as the host shows them.");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol_version = new { major = 2, minor = 0 },
            pairing_id = pairingId, pairing_token = pairingToken, host_id = hostId,
            spki_fingerprint = spkiFingerprint, device_id = deviceId
        });
        try
        {
            using var http = CreateHttpClient(canonical, spkiFingerprint);
            using var request = new HttpRequestMessage(HttpMethod.Post, canonical + "/martlet/v1/pair") { Content = JsonContent(body) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Send(http, request, timeout.Token).ConfigureAwait(false);
            using var document = await ReadJson(response, 16 * 1024, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created) throw Remote(document.RootElement);
            var root = document.RootElement;
            if (root.GetProperty("protocol_version").GetProperty("major").GetInt32() != 2 ||
                root.GetProperty("host_id").GetString() != hostId || root.GetProperty("device_id").GetString() != deviceId ||
                root.GetProperty("lifetime").GetProperty("kind").GetString() != "paired" ||
                !root.GetProperty("roles").EnumerateArray().Any(role => role.GetString() == "voice"))
                throw new Audio2FaceHostException("pairing.invalid", "The host returned an unexpected pairing; pair again with the voice role.");
            var credentialId = root.GetProperty("credential_id").GetString()!;
            var secret = root.GetProperty("credential_secret").GetString()!;
            if (!TryBase64Url(credentialId, 16, out _) || !TryBase64Url(secret, 32, out var raw))
                throw new Audio2FaceHostException("pairing.invalid", "The host returned an invalid credential.");
            CryptographicOperations.ZeroMemory(raw);
            var pairing = new Audio2FaceHostPairing
            {
                Origin = canonical, HostId = hostId, SpkiFingerprint = spkiFingerprint,
                DeviceId = deviceId, CredentialId = credentialId
            };
            return (pairing, secret);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's pairing response was invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    internal static string CanonicalOrigin(string? value)
    {
        var text = value?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
            !IsPrivate(address) || !HasExplicitPort(text))
            throw new Audio2FaceHostException("origin.invalid",
                "Enter the host address as https://<private IP>:<port>, for example https://192.168.1.20:9443.");
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"https://[{address}]:{uri.Port}" : $"https://{address}:{uri.Port}";
    }

    private static bool HasExplicitPort(string text)
    {
        var authority = text["https://".Length..];
        var colon = authority.LastIndexOf(':');
        return colon > authority.LastIndexOf(']') && colon < authority.Length - 1 && authority[(colon + 1)..].All(char.IsAsciiDigit);
    }

    internal static void RequireIdentifier(string? value, string label)
    {
        if (value is not { Length: > 0 and <= 64 } || !char.IsAsciiLetterOrDigit(value[0]) ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new Audio2FaceHostException("identity.invalid", $"Enter the exact {label} (letters, digits, '.', '_' or '-').");
    }

    internal static void RequireFingerprint(string? value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            !value[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new Audio2FaceHostException("identity.invalid", "Enter the host fingerprint exactly as shown (sha256: followed by 64 lowercase hex digits).");
    }

    internal static bool TryBase64Url(string? value, int bytes, out byte[] decoded)
    {
        decoded = [];
        var length = (bytes * 4 + 2) / 3;
        if (value is null || value.Length != length || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        var buffer = new byte[bytes];
        try
        {
            if (Base64Url.DecodeFromChars(value, buffer) != bytes || Base64Url.EncodeToString(buffer) != value) return false;
        }
        catch (FormatException) { return false; }
        decoded = buffer;
        return true;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    internal static HttpClient CreateHttpClient(string origin, string spkiFingerprint, TimeProvider? clock = null)
    {
        var now = clock ?? TimeProvider.System;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        handler.SslOptions.CertificateChainPolicy = new()
        {
            RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            ValidateCertificate(certificate, chain, errors, spkiFingerprint, now);
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(origin + "/"), Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static bool ValidateCertificate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors,
        string spkiFingerprint, TimeProvider clock)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;
        X509Certificate2? owned = null;
        var certificate2 = certificate as X509Certificate2;
        if (certificate2 is null)
        {
            owned = new X509Certificate2(certificate);
            certificate2 = owned;
        }
        try
        {
            var now = clock.GetUtcNow();
            if (now < certificate2.NotBefore.ToUniversalTime() || now > certificate2.NotAfter.ToUniversalTime() ||
                Fingerprint(certificate2) != spkiFingerprint)
                return false;
            if (!errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) return errors == SslPolicyErrors.None;
            const X509ChainStatusFlags allowed = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
            return chain is not null && chain.ChainStatus.Length > 0 && chain.ChainStatus.All(s => (s.Status & ~allowed) == 0);
        }
        finally { owned?.Dispose(); }
    }

    internal static string Fingerprint(X509Certificate2 certificate)
    {
        byte[] info;
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is not null) info = rsa.ExportSubjectPublicKeyInfo();
            else
            {
                using var ecdsa = certificate.GetECDsaPublicKey() ?? throw new CryptographicException("Unsupported host key.");
                info = ecdsa.ExportSubjectPublicKeyInfo();
            }
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(info));
    }

    internal static ByteArrayContent JsonContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    internal static async Task<HttpResponseMessage> Send(HttpClient http, HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                response.Dispose();
                throw new Audio2FaceHostException("host.redirect", "The host redirected the request; check the host address.");
            }
            return response;
        }
        catch (HttpRequestException)
        {
            throw new Audio2FaceHostException("host.unreachable",
                "Could not reach the Martlet host over pinned TLS. Check the address, that the gateway is running, and the fingerprint.");
        }
    }

    internal static async Task<JsonDocument> ReadJson(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (bytes.Length is 0 || bytes.Length > maximum || response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new Audio2FaceHostException("response.invalid", "The host returned an invalid response.");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new Audio2FaceHostException("response.invalid", "The host returned an invalid response."); }
    }

    internal static Audio2FaceHostException Remote(JsonElement failure)
    {
        var code = failure.TryGetProperty("code", out var value) ? value.GetString() ?? "host.failed" : "host.failed";
        var remedy = failure.TryGetProperty("remedy", out var detail) ? " " + detail.GetString() : "";
        return new Audio2FaceHostException(code, $"The Martlet host refused the request ({code}).{remedy}");
    }
}

/// <summary>A paired connection that relays generated-speech chunks to the host's Audio2Face service.</summary>
public sealed class Audio2FaceHostConnection : IDisposable
{
    private const string Role = "voice";
    private readonly Audio2FaceHostPairing pairing;
    private readonly HttpClient http;
    private readonly byte[] key;
    private readonly TimeProvider clock;
    private int disposed;

    public Audio2FaceHostConnection(Audio2FaceHostPairing pairing, ReadOnlySpan<char> secret, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        pairing.Validate();
        if (!Audio2FaceHostClient.TryBase64Url(new string(secret), 32, out var raw))
            throw new Audio2FaceHostException("auth.invalid", "The saved host credential is invalid; pair again.");
        key = SHA256.HashData(raw);
        CryptographicOperations.ZeroMemory(raw);
        this.pairing = pairing;
        this.clock = clock ?? TimeProvider.System;
        http = Audio2FaceHostClient.CreateHttpClient(pairing.Origin, pairing.SpkiFingerprint, this.clock);
    }

    public Audio2FaceHostPairing Pairing => pairing;

    /// <summary>Reads the host's advertised Audio2Face relay route, or null when the host has none.</summary>
    public async Task<Audio2FaceHostRoute?> ReadRouteAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + "/martlet/v1/capabilities");
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            foreach (var route in root.GetProperty("routes").EnumerateArray())
            {
                if (route.GetProperty("route_id").GetString() != Audio2FaceHostClient.RouteId) continue;
                string Text(string name) => route.GetProperty(name).GetString()!;
                var path = Text("path");
                if (path != "/martlet/v1/inference/audio2face" || Text("contract_id") != Audio2FaceHostClient.ContractId)
                    return null;
                return new(path, Text("route_id"), Text("contract_id"), Text("contract_version"), Text("destination_id"),
                    Text("worker_id"), Text("adapter_version"), Text("model_id"), Text("model_revision"), Text("model_sha256"),
                    Text("artifact_identity_sha256"), route.GetProperty("maximum_input_bytes").GetInt32(),
                    route.GetProperty("maximum_event_bytes").GetInt32(), route.GetProperty("maximum_stream_bytes").GetInt32(),
                    TimeSpan.FromMilliseconds(route.GetProperty("maximum_duration_milliseconds").GetInt64()));
            }
            return null;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's capability response was invalid.");
        }
    }

    /// <summary>Reads what the host reported about its hardware (GPUs, CPU, memory, OS), or null when the host has not
    /// collected it yet. Hosts older than this endpoint throw <see cref="Audio2FaceHostException"/>.</summary>
    public async Task<HostHardware?> ReadMachineAsync(CancellationToken cancellationToken = default) =>
        (await ReadMachineReportAsync(cancellationToken).ConfigureAwait(false)).Hardware;

    /// <summary>Reads the host's hardware report (null when not collected yet) and the Martlet release its gateway runs
    /// (null for hosts from 0.2.0 and earlier, which do not report it).</summary>
    public async Task<(HostHardware? Hardware, string? MartletVersion)> ReadMachineReportAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + "/martlet/v1/machine");
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            var version = root.TryGetProperty("martlet_version", out var reported) && reported.ValueKind == JsonValueKind.String &&
                Version.TryParse(reported.GetString(), out var parsed) ? parsed.ToString(3) : null;
            if (!root.TryGetProperty("machine", out var machine) || machine.ValueKind == JsonValueKind.Null) return (null, version);
            string? Text(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? Clean(value.GetString()) : null;
            int? Integer(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                    ? number : null;
            var gpus = machine.GetProperty("gpus").EnumerateArray().Take(16)
                .Select(gpu => new HostGpu(Text(gpu, "name") ?? "GPU", Text(gpu, "vendor") ?? "other", Integer(gpu, "memory_mb"), Text(gpu, "driver")))
                .ToArray();
            return (new HostHardware(pairing.HostId, pairing.Origin, machine.GetProperty("collected_at").GetDateTimeOffset(),
                clock.GetUtcNow(), Text(machine, "method") ?? "unknown", Text(machine, "operating_system") ?? "Unknown",
                Text(machine, "kernel"), Text(machine, "processor"), Integer(machine, "processor_threads"),
                machine.TryGetProperty("memory_gb", out var memory) && memory.ValueKind == JsonValueKind.Number ? memory.GetDouble() : null,
                Text(machine, "container_runtime"), Text(machine, "nvidia_containers"), gpus) { MartletVersion = version }, version);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's machine report was invalid.");
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new string(value.Trim().Take(128).Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    /// <summary>Animates one chunk of mono signed 16-bit generated-speech PCM through the host relay.</summary>
    public async IAsyncEnumerable<RemoteFaceFrame> AnimateAsync(Audio2FaceHostRoute route, CorrelationIds ids, long epoch,
        int sampleRate, ReadOnlyMemory<byte> pcm, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(ids);
        ids.Validate();
        if (pcm.Length is 0 || pcm.Length % 2 != 0 || pcm.Length > route.MaximumInputBytes ||
            sampleRate is not (16_000 or 24_000 or 44_100 or 48_000))
            throw new ArgumentException("PCM chunk exceeds the host route bounds.");
        var deadline = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(15, route.MaximumDuration.TotalSeconds));
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["protocol_version"] = new { major = 2, minor = 0 },
            ["route_id"] = route.RouteId, ["contract_id"] = route.ContractId, ["contract_version"] = route.ContractVersion,
            ["destination_id"] = route.DestinationId, ["worker_id"] = route.WorkerId, ["adapter_version"] = route.AdapterVersion,
            ["model_id"] = route.ModelId, ["model_revision"] = route.ModelRevision, ["model_sha256"] = route.ModelSha256,
            ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
            ["session_id"] = ids.SessionId, ["turn_id"] = ids.TurnId, ["request_id"] = ids.RequestId, ["epoch"] = epoch,
            ["deadline_utc"] = deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = new { sample_rate = sampleRate, pcm_base64 = Convert.ToBase64String(pcm.Span) }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + route.Path)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline - clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var failure = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
            throw Audio2FaceHostClient.Remote(failure.RootElement);
        }
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new Audio2FaceHostException("response.invalid", "The host returned an invalid animation stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var total = 0L;
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            total += line.Length + 1;
            if (line.Length > route.MaximumEventBytes * 2 || total > route.MaximumStreamBytes)
                throw new Audio2FaceHostException("stream.limit", "The host's animation stream exceeded its bounds.");
            var (frame, terminal) = ParseEvent(line, ids);
            if (frame is not null) yield return frame;
            if (terminal) yield break;
        }
        throw new Audio2FaceHostException("stream.truncated", "The host's animation stream ended early.");
    }

    private static (RemoteFaceFrame? Frame, bool Terminal) ParseEvent(string line, CorrelationIds ids)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("request_id").GetGuid() != ids.RequestId)
                throw new FormatException();
            switch (root.GetProperty("type").GetString())
            {
                case "started": return (null, false);
                case "completed": return (null, true);
                case "canceled": throw new Audio2FaceHostException("job.canceled", "The host canceled the animation request.");
                case "failed": throw Audio2FaceHostClient.Remote(root);
                case "face_frame":
                    var payload = Convert.FromBase64String(root.GetProperty("data_base64").GetString()!);
                    using (var face = JsonDocument.Parse(payload))
                    {
                        var values = new Dictionary<string, double>(StringComparer.Ordinal);
                        foreach (var item in face.RootElement.GetProperty("blendshapes").EnumerateObject())
                        {
                            var value = item.Value.GetDouble();
                            if (!double.IsFinite(value) || value is < 0 or > 1 || values.Count >= 64)
                                throw new FormatException();
                            values[item.Name] = value;
                        }
                        return (new RemoteFaceFrame(root.GetProperty("sample_offset").GetInt64(), values), false);
                    }
                default: throw new FormatException();
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("stream.invalid", "The host's animation stream was invalid.");
        }
    }

    private void Sign(HttpRequestMessage request, ReadOnlySpan<byte> body)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        var canonical = Encoding.ASCII.GetBytes(string.Join('\n',
            "martlet-request-v1", pairing.HostId, pairing.CredentialId, request.Method.Method,
            request.RequestUri!.PathAndQuery, Role, timestamp.ToString(CultureInfo.InvariantCulture), nonce,
            Convert.ToHexStringLower(SHA256.HashData(body))));
        var signature = Base64Url.EncodeToString(HMACSHA256.HashData(key, canonical));
        request.Headers.TryAddWithoutValidation("Authorization", $"Martlet-HMAC {pairing.CredentialId}.{signature}");
        request.Headers.TryAddWithoutValidation("X-Martlet-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Martlet-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Martlet-Role", Role);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CryptographicOperations.ZeroMemory(key);
        http.Dispose();
    }
}
