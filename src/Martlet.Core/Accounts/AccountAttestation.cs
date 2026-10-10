using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Core.Network;
using B64 = System.Buffers.Text.Base64Url;

namespace Martlet.Core.Accounts;

/// <summary>The login an <see cref="AccountAttestation"/> names: the Login contract of docs/ACCOUNTS.md. Kind is "martlet",
/// "oidc", "discord" or "steam" (hosts never attest "windows"); provider is "martlet" for a Martlet password, else the household
/// provider ID; subject is the lowercase user name for "martlet", else the provider's subject.</summary>
public sealed record AccountAttestationLogin
{
    public required string Kind { get; init; }
    public required string Provider { get; init; }
    public required string Subject { get; init; }

    public override string ToString() => $"{Kind}:{Provider}:{Subject}";
}

/// <summary>Why an <see cref="AccountAttestation"/> was or wasn't accepted (<see cref="AccountAttestation.Check"/>).</summary>
public enum AccountAttestationCheck
{
    Valid,
    /// <summary>The fields are malformed (bad IDs, an unknown login kind, times out of order or a lifetime too long).</summary>
    Malformed,
    /// <summary>It was issued in another Martlet network.</summary>
    OtherNetwork,
    /// <summary>The roster has no active host with that ID.</summary>
    UnknownHost,
    /// <summary>The key that signed it is not the one the roster pins for that host.</summary>
    KeyNotPinned,
    /// <summary>The signature does not match.</summary>
    BadSignature,
    /// <summary>Its issue time is later than the time it was checked at (beyond <see cref="AccountAttestation.ClockSkew"/>).</summary>
    NotYetValid,
    Expired
}

/// <summary>
/// A host's signed statement that an account proved itself on a device: after a Prove sign-in (a Martlet password plus the
/// authenticator code when the login has one, or an allowed provider identity) the host says "account <see cref="AccountId"/>
/// signed in with <see cref="Login"/> on device <see cref="DeviceId"/>, in network <see cref="NetworkId"/>, between
/// <see cref="IssuedAt"/> and <see cref="ExpiresAt"/>".
/// <para>The host signs with its TLS key, the key whose SHA-256 SPKI fingerprint the network roster pins for that host
/// (<see cref="NetworkMember.Spki"/>), so any member desktop or host checks it with the roster it already accepted and no new
/// key is handed out: ECDSA P-256 with SHA-256 (<c>ES256</c>, IEEE P1363 signature; every Martlet host certificate) or
/// RSA-PSS with SHA-256 (<c>PS256</c>, 2048 bits or more). <see cref="HostKey"/> carries the public key (base64url
/// SubjectPublicKeyInfo); the checker hashes it and compares the hash with the pin. The signed bytes start with
/// <see cref="Domain"/>, so they can never be taken for a TLS handshake signature.</para>
/// JSON (snake case): <c>{"schema_version":1,"network_id","host_id","account_id","device_id","login":{"kind","provider",
/// "subject"},"issued_at","expires_at","algorithm","host_key","signature"}</c>.
/// </summary>
public sealed record AccountAttestation
{
    public const string Domain = "martlet-account-attestation-v1";
    /// <summary>The largest JSON form; its <see cref="ToText"/> form stays within 4,096 characters.</summary>
    public const int MaximumBytes = 3072;
    public const int MaximumTextLength = 4096;
    public const string EcdsaP256 = "ES256";
    public const string RsaPss = "PS256";
    /// <summary>What a host gives when the caller asks for nothing else: long enough to finish one Prove action.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(30);
    /// <summary>How far a checker's clock may be behind the host's.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        MaxDepth = 4
    };

    public int SchemaVersion { get; init; } = 1;
    public required string NetworkId { get; init; }
    public required string HostId { get; init; }
    public required Guid AccountId { get; init; }
    public required string DeviceId { get; init; }
    public required AccountAttestationLogin Login { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string Algorithm { get; init; }
    public required string HostKey { get; init; }
    public required string Signature { get; init; }

    /// <summary>The exact bytes the host signs: one field per line after <see cref="Domain"/>, times as Unix milliseconds.</summary>
    public byte[] SigningBytes() => Encoding.UTF8.GetBytes(string.Join('\n',
        Domain, NetworkId, HostId, AccountId.ToString("N"), DeviceId, Login.Kind, Login.Provider, Login.Subject,
        IssuedAt.ToUnixTimeMilliseconds(), ExpiresAt.ToUnixTimeMilliseconds(), Algorithm, HostKey));

    /// <summary>The host's statement, signed with the private key of <paramref name="hostCertificate"/> (the host's TLS
    /// certificate). Times are kept to whole seconds. Throws <see cref="ArgumentException"/> when a field is malformed or the
    /// certificate has no usable private key.</summary>
    public static AccountAttestation Issue(string networkId, string hostId, Guid accountId, string deviceId, AccountAttestationLogin login,
        DateTimeOffset issuedAt, TimeSpan lifetime, X509Certificate2 hostCertificate)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(hostCertificate);
        if (lifetime < MinimumLifetime || lifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "An attestation lasts between one minute and 30 days.");
        var issued = DateTimeOffset.FromUnixTimeSeconds(issuedAt.ToUnixTimeSeconds());
        using var ecdsa = hostCertificate.GetECDsaPrivateKey();
        using var rsa = ecdsa is null ? hostCertificate.GetRSAPrivateKey() : null;
        if (ecdsa is null && rsa is null) throw new ArgumentException("The host certificate has no ECDSA or RSA private key.", nameof(hostCertificate));
        var unsigned = new AccountAttestation
        {
            NetworkId = networkId, HostId = hostId, AccountId = accountId, DeviceId = deviceId, Login = login, IssuedAt = issued,
            ExpiresAt = issued + TimeSpan.FromSeconds(Math.Floor(lifetime.TotalSeconds)), Algorithm = ecdsa is not null ? EcdsaP256 : RsaPss,
            HostKey = B64.EncodeToString(PublicKey(hostCertificate)), Signature = ""
        };
        if (!unsigned.WellFormed()) throw new ArgumentException("The attestation's fields are malformed.");
        var bytes = unsigned.SigningBytes();
        var signature = ecdsa is not null
            ? ecdsa.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            : rsa!.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return unsigned with { Signature = B64.EncodeToString(signature) };
    }

    /// <summary>Checks this statement at <paramref name="at"/> (normally now; a past time to check one kept with something written
    /// then) against <paramref name="roster"/>, which the checker already accepted: same network, an active host with this ID,
    /// a host key whose fingerprint is that host's pin, a good signature and <paramref name="at"/> within its lifetime. The
    /// caller still compares <see cref="DeviceId"/> and <see cref="AccountId"/> with what it expects.</summary>
    public AccountAttestationCheck Check(NetworkRoster roster, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (!WellFormed()) return AccountAttestationCheck.Malformed;
        if (roster.NetworkId != NetworkId) return AccountAttestationCheck.OtherNetwork;
        if (roster.Host(HostId) is not { Removed: false } host) return AccountAttestationCheck.UnknownHost;
        if (!TryDecode(HostKey, 2048, out var key) || !string.Equals(Fingerprint(key), host.Spki, StringComparison.Ordinal))
            return AccountAttestationCheck.KeyNotPinned;
        if (!SignatureMatches(key)) return AccountAttestationCheck.BadSignature;
        if (at + ClockSkew < IssuedAt) return AccountAttestationCheck.NotYetValid;
        return at >= ExpiresAt ? AccountAttestationCheck.Expired : AccountAttestationCheck.Valid;
    }

    /// <summary>The roster pin (sha256:&lt;64 hex&gt;) of a base64url SubjectPublicKeyInfo, as hosts and desktops compute it for
    /// TLS; null when <paramref name="hostKey"/> isn't base64url.</summary>
    public static string? KeyFingerprint(string? hostKey) => TryDecode(hostKey, 2048, out var key) ? Fingerprint(key) : null;

    public byte[] Write()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("The attestation is too large.");
        return bytes;
    }

    /// <summary>Reads an attestation (a JSON object). Throws <see cref="FormatException"/> when it isn't a well-formed one; the
    /// signature is checked by <see cref="Check"/>, not here.</summary>
    public static AccountAttestation Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaximumBytes) throw new FormatException("The attestation is too large.");
        try
        {
            var parsed = JsonSerializer.Deserialize<AccountAttestation>(json, Json) ?? throw new FormatException("The attestation is empty.");
            return parsed.WellFormed() ? parsed : throw new FormatException("The attestation is malformed.");
        }
        catch (JsonException error) { throw new FormatException("The attestation is not valid JSON.", error); }
    }

    public static AccountAttestation Parse(JsonElement element) => Parse(Encoding.UTF8.GetBytes(element.GetRawText()));

    /// <summary>The attestation as one line of printable ASCII: base64url of its compact JSON (at most
    /// <see cref="MaximumTextLength"/> characters), for fields that keep it as text (the account directory's device entry).</summary>
    public string ToText() => B64.EncodeToString(Write());

    /// <summary>Reads <see cref="ToText"/>'s form. Throws <see cref="FormatException"/> when it isn't a well-formed attestation.</summary>
    public static AccountAttestation FromText(string? text) =>
        text is { Length: > 0 and <= MaximumTextLength } && TryDecode(text, MaximumBytes, out var json)
            ? Parse(json)
            : throw new FormatException("The attestation text is not base64url of at most 3,072 bytes.");

    public override string ToString() => $"Account {AccountId:N} on {DeviceId} as {Login}, attested by {HostId} until {ExpiresAt:u}";

    private bool WellFormed() =>
        SchemaVersion == 1 && TryDecode(NetworkId, 16, out var network) && network.Length == 16 &&
        Contracts.ContractRules.IsIdentifier(HostId) && Contracts.ContractRules.IsIdentifier(DeviceId) && AccountId != Guid.Empty &&
        Login is { Kind: "martlet" or "oidc" or "discord" or "steam", Provider: not null, Subject: { Length: > 0 and <= 256 } } &&
        Contracts.ContractRules.IsIdentifier(Login.Provider) && !Login.Subject.Any(char.IsControl) &&
        (Login.Kind != "martlet" || Login.Provider == "martlet" && Login.Subject == Login.Subject.ToLowerInvariant()) &&
        ExpiresAt > IssuedAt && ExpiresAt - IssuedAt <= MaximumLifetime && Algorithm is EcdsaP256 or RsaPss &&
        TryDecode(HostKey, 2048, out _) && Signature is not null && (Signature.Length == 0 || TryDecode(Signature, 1024, out _));

    private bool SignatureMatches(byte[] key)
    {
        if (!TryDecode(Signature, 1024, out var signature)) return false;
        var bytes = SigningBytes();
        try
        {
            if (Algorithm == EcdsaP256)
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(key, out var read);
                return read == key.Length && ecdsa.KeySize == 256 && signature.Length == 64 &&
                    ecdsa.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value &&
                    ecdsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(key, out var length);
            return length == key.Length && rsa.KeySize >= 2048 &&
                rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException) { return false; }
    }

    // The same SubjectPublicKeyInfo bytes hosts and desktops hash for the TLS pin (the public key exported on its own).
    private static byte[] PublicKey(X509Certificate2 certificate)
    {
        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is not null) return ecdsa.ExportSubjectPublicKeyInfo();
        using var rsa = certificate.GetRSAPublicKey() ?? throw new ArgumentException("The host certificate has no ECDSA or RSA key.", nameof(certificate));
        return rsa.ExportSubjectPublicKeyInfo();
    }

    private static string Fingerprint(byte[] subjectPublicKeyInfo) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));

    private static bool TryDecode(string? text, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (text is not { Length: > 0 } || text.Length > (maximumBytes * 4 + 2) / 3 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        try
        {
            bytes = B64.DecodeFromChars(text);
            return bytes.Length > 0 && bytes.Length <= maximumBytes && B64.EncodeToString(bytes) == text;
        }
        catch (FormatException) { return false; }
    }
}
