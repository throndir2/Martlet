using System.Security.Cryptography;
using System.Text;
using B64 = System.Buffers.Text.Base64Url;

namespace Martlet.Core.Network;

/// <summary>Signs Martlet network roster entries and member pairing requests with a desktop's own network key.</summary>
public interface INetworkSigner
{
    /// <summary>The desktop's device ID: the same ID its host pairings use.</summary>
    string DeviceId { get; }

    /// <summary>The public key as base64url SubjectPublicKeyInfo (ECDSA P-256).</summary>
    string PublicKey { get; }

    /// <summary>An ECDSA P-256 / SHA-256 signature over <paramref name="data"/> in IEEE P1363 form (64 bytes).</summary>
    byte[] Sign(byte[] data);
}

/// <summary>A desktop's network key (ECDSA P-256) held in memory, plus verification of other members' signatures. The
/// desktop keeps its private key in its data directory (readable by its Windows user only); hosts only ever see public keys.</summary>
public sealed class NetworkKey : INetworkSigner, IDisposable
{
    private readonly ECDsa key;

    public NetworkKey(string deviceId, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Contracts.ContractRules.Identifier(deviceId);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new ArgumentException("A Martlet network key must be ECDSA P-256.", nameof(key));
        DeviceId = deviceId;
        this.key = key;
        PublicKey = B64.EncodeToString(key.ExportSubjectPublicKeyInfo());
    }

    public static NetworkKey Create(string deviceId) => new(deviceId, ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public string DeviceId { get; }
    public string PublicKey { get; }

    public byte[] Sign(byte[] data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>The private key as PEM, to keep it in a private file.</summary>
    public string ExportPem() => key.ExportECPrivateKeyPem();

    public static NetworkKey FromPem(string deviceId, string pem)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            return new(deviceId, ecdsa);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    public void Dispose() => key.Dispose();

    public override string ToString() => $"Network key of {DeviceId}";

    /// <summary>Whether <paramref name="text"/> is a base64url SubjectPublicKeyInfo of an ECDSA P-256 key.</summary>
    public static bool IsPublicKey(string? text)
    {
        if (!TryDecode(text, 512, out var bytes)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && ecdsa.KeySize == 256 &&
                ecdsa.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value &&
                B64.EncodeToString(bytes) == text;
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Whether <paramref name="signature"/> is <paramref name="publicKey"/>'s signature over <paramref name="data"/>.</summary>
    public static bool Verify(string publicKey, byte[] data, string? signature)
    {
        if (!TryDecode(publicKey, 512, out var spki) || !TryDecode(signature, 64, out var raw) || raw.Length != 64) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa.KeySize == 256 &&
                ecdsa.VerifyData(data, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>The ID of the network founded with <paramref name="publicKey"/>: base64url of the first 16 bytes of its
    /// SHA-256, so anyone can tell which member founded a network.</summary>
    public static string NetworkIdFor(string publicKey) =>
        B64.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(publicKey)).AsSpan(0, 16));

    /// <summary>Six digits (shown as "482 913") a joining desktop and the member approving it both show, so the owner can
    /// tell the request is from the computer in front of them.</summary>
    public static string CheckNumber(string networkId, string deviceId, string publicKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"martlet-network-check-v1\n{networkId}\n{deviceId}\n{publicKey}"));
        var value = (uint)(hash[0] << 24 | hash[1] << 16 | hash[2] << 8 | hash[3]) % 1_000_000;
        var digits = value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return digits[..3] + " " + digits[3..];
    }

    internal static bool TryDecode(string? text, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (text is null || text.Length == 0 || text.Length > (maximumBytes * 4 + 2) / 3 ||
            !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        try
        {
            bytes = B64.DecodeFromChars(text);
            return bytes.Length is > 0 && bytes.Length <= maximumBytes;
        }
        catch (FormatException) { return false; }
    }
}
