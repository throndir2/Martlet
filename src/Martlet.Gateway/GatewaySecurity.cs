using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Gateway;

public interface IGatewayCrypto
{
    byte[] RandomBytes(int count);
    byte[] Sha256(ReadOnlyMemory<byte> value);
    byte[] HmacSha256(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value);
    bool FixedTimeEquals(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right);
    string SpkiFingerprint(X509Certificate2 certificate);
}

public sealed class SystemGatewayCrypto : IGatewayCrypto
{
    public byte[] RandomBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 1024);
        return RandomNumberGenerator.GetBytes(count);
    }

    public byte[] Sha256(ReadOnlyMemory<byte> value) => SHA256.HashData(value.Span);

    public byte[] HmacSha256(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value) =>
        HMACSHA256.HashData(key.Span, value.Span);

    public bool FixedTimeEquals(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left.Span, right.Span);

    public string SpkiFingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        byte[] subjectPublicKeyInfo;
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is not null)
                subjectPublicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
            else
            {
                using var ecdsa = certificate.GetECDsaPublicKey();
                GatewayRules.Require(ecdsa is not null, "host.identity_invalid");
                subjectPublicKeyInfo = ecdsa!.ExportSubjectPublicKeyInfo();
            }
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));
    }
}

public sealed class GatewayOrigin
{
    public string CanonicalOrigin { get; }
    public Uri Uri { get; }
    public IPAddress Address { get; }
    public int Port { get; }

    public GatewayOrigin(string canonicalOrigin)
    {
        Uri? parsedUri = null;
        IPAddress? parsedAddress = null;
        var valid = canonicalOrigin is { Length: > 0 and <= 96 } &&
            System.Uri.TryCreate(canonicalOrigin, UriKind.Absolute, out parsedUri) &&
            IPAddress.TryParse(parsedUri.IdnHost, out parsedAddress);
        GatewayRules.Require(valid, "binding.unsafe");
        var uri = parsedUri!;
        var address = parsedAddress!;
        GatewayRules.Require(
            uri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            uri.AbsolutePath == "/" &&
            !canonicalOrigin.EndsWith("/", StringComparison.Ordinal) &&
            uri.Port is >= 1 and <= 65535, "binding.unsafe");
        GatewayRules.Require(IsPrivateOrLoopback(address), "binding.unsafe");
        var expected = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"https://[{address}]:{uri!.Port}"
            : $"https://{address}:{uri!.Port}";
        GatewayRules.Require(canonicalOrigin == expected, "binding.unsafe");
        CanonicalOrigin = expected;
        Uri = uri;
        Address = address;
        Port = uri.Port;
    }

    internal static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return false;
        if (IPAddress.IsLoopback(address))
            return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xfe) == 0xfc;
    }

    public override string ToString() => nameof(GatewayOrigin);
}

public sealed partial record GatewayHostIdentity
{
    public required string HostId { get; init; }
    public required string SpkiFingerprint { get; init; }

    public static GatewayHostIdentity FromCertificate(
        string hostId,
        X509Certificate2 certificate,
        IGatewayCrypto? crypto = null)
    {
        GatewayRules.Identifier(hostId);
        return new()
        {
            HostId = hostId,
            SpkiFingerprint = (crypto ?? new SystemGatewayCrypto()).SpkiFingerprint(certificate)
        };
    }

    internal void Validate()
    {
        GatewayRules.Identifier(HostId);
        GatewayRules.Require(IsFingerprint(SpkiFingerprint), "host.identity_invalid");
    }

    internal static bool IsFingerprint(string? value) =>
        value is not null && SpkiPattern().IsMatch(value);

    [GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SpkiPattern();
}

public sealed class GatewayTlsBinding
{
    internal Func<X509Certificate2>? CertificateSelector { get; init; }
    public GatewayOrigin Origin { get; }
    public GatewayHostIdentity Identity { get; }
    public X509Certificate2 Certificate { get; }
    /// <summary>Wildcard listen address inside a container whose runtime publishes the origin; null listens on the origin.</summary>
    public IPAddress? ListenAddress
    {
        get => listenAddress;
        init
        {
            GatewayRules.Require(value is null || value.Equals(IPAddress.Any) || value.Equals(IPAddress.IPv6Any), "binding.unsafe");
            listenAddress = value;
        }
    }
    private readonly IPAddress? listenAddress;

    public GatewayTlsBinding(
        GatewayOrigin origin,
        GatewayHostIdentity identity,
        X509Certificate2 certificate,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(certificate);
        identity.Validate();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var basicConstraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>().ToArray();
        var keyUsage = certificate.Extensions
            .OfType<X509KeyUsageExtension>().ToArray();
        var enhancedUsage = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>().ToArray();
        GatewayRules.Require(certificate.HasPrivateKey &&
            now >= certificate.NotBefore.ToUniversalTime() &&
            now <= certificate.NotAfter.ToUniversalTime() &&
            HasIpSubjectAlternativeName(certificate, origin.Address) &&
            basicConstraints is [{ CertificateAuthority: false }] &&
            keyUsage is [var usage] &&
            usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature) &&
            enhancedUsage is [var enhanced] &&
            enhanced.EnhancedKeyUsages.Cast<Oid>()
                .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"),
            "binding.certificate_invalid");
        GatewayRules.Require(string.Equals(identity.SpkiFingerprint,
            (crypto ?? new SystemGatewayCrypto()).SpkiFingerprint(certificate),
            StringComparison.Ordinal), "host.pin_mismatch");
        Origin = origin;
        Identity = identity;
        Certificate = certificate;
    }

    private static bool HasIpSubjectAlternativeName(
        X509Certificate2 certificate,
        IPAddress address)
    {
        var alternatives = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>().ToArray();
        if (alternatives.Length != 1)
            return false;
        try
        {
            return alternatives[0].EnumerateIPAddresses()
                .Any(candidate => candidate.Equals(address));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

public sealed record GatewayAuditEvent
{
    public required Guid TraceId { get; init; }
    public required string Code { get; init; }
    public required int HttpStatus { get; init; }
}

public interface IGatewayAuditSink
{
    // The request boundary can call concurrently. Implementations must not throw.
    void Record(GatewayAuditEvent gatewayEvent);
}

public sealed class GatewayFailure
{
    public string Code { get; }
    public string Summary { get; }
    public string Remedy { get; }
    public int HttpStatus { get; }

    internal GatewayFailure(string code, string summary, string remedy, int status)
    {
        GatewayRules.Identifier(code);
        GatewayRules.Token(summary, 512);
        GatewayRules.Token(remedy, 512);
        Code = code;
        Summary = summary;
        Remedy = remedy;
        HttpStatus = status;
    }
}

public sealed class GatewayProtocolException : Exception
{
    public GatewayFailure Failure { get; }

    internal GatewayProtocolException(string code) : base(GatewayFailures.Get(code).Summary)
    {
        Failure = GatewayFailures.Get(code);
    }
}

internal static class GatewayFailures
{
    internal static GatewayFailure Get(string code) => code switch
    {
        "request.invalid" => new(code,
            "The request does not match the bounded gateway contract.",
            "Use the exact documented method, media type, headers and protocol version; do not put credentials in a URL.",
            400),
        "request.too_large" => new(code,
            "The request exceeds the gateway byte limit.",
            "Send a complete request within the documented limit; the oversized body was not interpreted.",
            413),
        "protocol.unsupported" => new(code,
            "The gateway protocol major version is unsupported.",
            "Use a compatible signed Martlet client and host pair; do not treat this as an authentication failure.",
            426),
        "binding.unsafe" => new(code,
            "The gateway origin is not an explicit private or loopback HTTPS address.",
            "Choose one canonical private or loopback IP address and explicit port; never use HTTP, a wildcard, public address or hostname alias.",
            400),
        "binding.certificate_invalid" => new(code,
            "The supplied gateway certificate is not valid for this TLS binding.",
            "Supply a current non-CA server certificate with its private key, server-auth usage and an IP subject alternative name for the selected address; the gateway does not generate or install one.",
            400),
        "host.identity_invalid" => new(code,
            "The pinned host identity is invalid.",
            "Use the exact bounded host ID and SHA-256 SPKI fingerprint shown by the local host utility.",
            400),
        "host.pin_mismatch" => new(code,
            "The presented host key does not match the pinned identity.",
            "Stop and compare the SHA-256 fingerprint out of band; re-pair only after verifying an intended host-key change.",
            401),
        "pairing.closed" => new(code,
            "The pairing window is unavailable, consumed or closed.",
            "On the host, explicitly approve a new Pair device window and use only its new out-of-band card.",
            401),
        "pairing.expired" => new(code,
            "The one-use pairing window expired.",
            "On the host, open a new Pair device window and use the replacement card before its displayed expiry.",
            401),
        "pairing.invalid" => new(code,
            "The pairing proof does not match the locally approved device.",
            "Compare the host ID and fingerprint out of band, then enter the exact one-use token and approved device ID.",
            401),
        "auth.missing" => new(code,
            "A signed device credential is required.",
            "Pair this device explicitly, then sign a fresh request with its scoped credential.",
            401),
        "auth.invalid" => new(code,
            "The signed device credential is invalid.",
            "Check the paired host and device credential; if it was lost or rotated, revoke it and pair again.",
            401),
        "auth.expired" => new(code,
            "The replaced credential's rotation overlap ended.",
            "Use the replacement credential; the device pairing itself does not expire.",
            401),
        "auth.revoked" => new(code,
            "The device credential was revoked.",
            "Keep the credential disabled; explicitly pair a new device credential if access is still intended.",
            401),
        "auth.clock" => new(code,
            "The signed request timestamp is outside the accepted clock window.",
            "Correct the client and host clocks, then create a fresh nonce and signature.",
            401),
        "auth.clock_invalid" => new(code,
            "The host authorization clock moved backwards and this authority is closed.",
            "Correct the host clock and reopen the existing protected authority. Pairing records remain preserved; do not reset paired devices.",
            503),
        "auth.closed" => new(code,
            "The gateway authority is closed.",
            "Reopen the existing host after resolving its local recovery status; pairing data remains preserved. Never restore stale backups.",
            503),
        "auth.storage" => new(code,
            "The gateway could not durably commit authorization state.",
            "Resolve protected-storage access and reopen the same store for authenticated transaction recovery. Pairing data is preserved; do not reset devices.",
            503),
        "auth.capacity" => new(code,
            "The host reached its bounded device credential capacity.",
            "Locally revoke unused credentials or wait for old-key rotation overlap to end, then retry; permanent pairings and live replay records are never evicted.",
            429),
        "auth.replay" => new(code,
            "The signed request nonce was already used.",
            "Create a fresh nonce and signature; never retry by replaying an authenticated request.",
            409),
        "auth.rate" => new(code,
            "The credential reached its bounded authenticated-request rate.",
            "Reduce the authenticated request rate and wait for the four-minute replay window to drain; no nonce was evicted.",
            429),
        "auth.role" => new(code,
            "The device credential does not grant the requested role.",
            "Use a locally approved credential scoped to this role; pairing never grants host administration.",
            403),
        "worker.invalid" => new(code,
            "A private worker returned invalid bounded metadata.",
            "Keep the worker unavailable and repair its configured adapter/status contract; do not expose its raw service.",
            503),
        "response.invalid" => new(code,
            "The pinned gateway returned an invalid bounded response.",
            "Keep this action unavailable and check protocol compatibility. Do not discard the saved pairing, retry automatically or accept an unpinned destination.",
            502),
        "worker.unavailable" => new(code,
            "A private worker did not provide status before its bounded operation completed.",
            "Check only the named private worker and retry deliberately after it reports a valid local status.",
            503),
        "worker.identity" => new(code,
            "A private worker response did not match the configured route identity.",
            "Keep this route unavailable and verify the exact destination, worker, model and artifact configuration; do not accept drift or fall back.",
            502),
        "worker.failed" => new(code,
            "The selected private worker failed the bounded operation.",
            "Inspect only the configured private worker using this trace ID; do not retry automatically or select another model.",
            502),
        "worker.quarantined" => new(code,
            "The private worker route is quarantined because bounded ownership cleanup was not proven.",
            "Keep the route disabled until the private worker is explicitly restarted and its ownership boundary is re-established.",
            503),
        "action.denied" => new(code,
            "The selected inference action is not currently permitted.",
            "Obtain the exact per-action provider permission; device pairing is not action approval.",
            403),
        "job.replay" => new(code,
            "This inference request identifier has already been admitted.",
            "Use a new request and a new explicitly authorized action; do not resubmit a batch.",
            409),
        "job.busy" => new(code,
            "The configured role and private worker already own one bounded job.",
            "Wait for that exact job to complete or cancel it; no pending inference queue was created.",
            429),
        "job.not_found" => new(code,
            "No matching active job is owned by this credential and role.",
            "Use the exact active route and request ID; completed or differently owned jobs cannot be canceled.",
            404),
        "job.deadline" => new(code,
            "The bounded inference job deadline expired.",
            "Treat all partial output as incomplete; retry only as a new explicitly authorized request after checking the private worker.",
            504),
        "job.canceled" => new(code,
            "The bounded inference job was canceled and local output was discarded.",
            "Start a new explicitly authorized request if work is still intended; cancellation does not claim that private compute stopped.",
            409),
        "stream.invalid" => new(code,
            "The private worker stream violated the exact event contract.",
            "Keep this route unavailable and repair the configured private worker adapter; do not expose or reinterpret the malformed output.",
            502),
        "stream.limit" => new(code,
            "The private worker stream exceeded its event or byte bound.",
            "Discard the incomplete output and repair the configured worker or request limits; no unbounded stream was accepted.",
            502),
        "stream.truncated" => new(code,
            "The private worker stream ended without one valid terminal event.",
            "Discard the incomplete output and inspect the configured private worker using this trace ID.",
            502),
        "stream.late" => new(code,
            "The private worker emitted output after its terminal event.",
            "Discard the late output and repair the configured worker; it cannot be attached to another job.",
            502),
        "stream.cleanup" => new(code,
            "The private worker stream did not prove bounded ownership cleanup.",
            "Keep the worker route quarantined until it is explicitly restarted; do not admit replacement work.",
            503),
        "context.unavailable" => new(code,
            "The optional perception context worker is unavailable.",
            "Continue without optional context or repair host 2 deliberately; voice inference remains independent.",
            503),
        "gateway.redirect_rejected" => new(code,
            "The gateway returned a redirect and it was not followed.",
            "Use the exact paired private HTTPS origin; verify the host identity instead of following another destination.",
            502),
        "gateway.connection_failed" => new(code,
            "The pinned gateway connection could not be established.",
            "Verify the exact private address and port, then compare the host fingerprint out of band; never bypass certificate validation.",
            502),
        "gateway.deadline" => new(code,
            "The pinned gateway request exceeded its bounded deadline.",
            "Check host-local readiness and the private network path, then retry deliberately with a fresh signed request.",
            504),
        "gateway.internal" => new(code,
            "The gateway could not complete the request.",
            "Use the trace ID with local redacted diagnostics; no automatic retry, fallback or raw worker access was attempted.",
            500),
        "network.unbound" => new(code,
            "This host is not in a Martlet network yet.",
            "Pair a member desktop with this host once (Add a computer); it adds the host to its network by itself.",
            409),
        "network.other" => new(code,
            "This host belongs to another Martlet network.",
            "Use a desktop of that network, or run martlet-host network-reset on the host to let it join another network.",
            409),
        "network.denied" => new(code,
            "This device is not an active member of the host's Martlet network.",
            "Ask to join from Martlet on this PC and allow it on one of your other computers (Devices).",
            403),
        "network.invalid" => new(code,
            "The Martlet network roster is invalid for this host.",
            "Sync again from a member desktop of this host's network; entries must be signed by members and list this host with its own key.",
            400),
        _ => throw new ArgumentException("Unknown gateway failure code.", nameof(code))
    };
}

internal static class Base64Url
{
    internal static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryDecode(string? value, int expectedBytes, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != EncodedLength(expectedBytes) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        var text = value.Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - text.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(text);
            return bytes.Length == expectedBytes && value == Encode(bytes);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static int EncodedLength(int bytes) => (bytes * 8 + 5) / 6;
}
