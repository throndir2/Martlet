using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Martlet.Gateway;

/// <summary>
/// Short pairing codes a person can read on the host and type on a desktop (for example <c>K7QM-4XPA</c>): 8 characters
/// from 32 unambiguous symbols (40 bits), one use, five minutes. The desktop connects to the address the host shows
/// without a pin, then proves it knows the code with an HMAC keyed by PBKDF2(code, the host key it was shown and a fresh
/// client nonce); the host answers with its own proof under the same key before the desktop pins that key. A host key
/// swapped in by someone on the network changes the derived key, so the real host rejects the proof and the impostor
/// cannot answer without the code. Observing one attempt leaves only an offline guess per code at 100,000 PBKDF2
/// iterations each, far slower than the five-minute window. Desktops implement the same derivation
/// (<c>Audio2FaceHostClient.PairWithCodeAsync</c>); keep both in step.
/// </summary>
public static class GatewayPairingCode
{
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int Length = 8;
    public const int Iterations = 100_000;
    internal const string Path = "/martlet/v1/pair/code";
    private const string Label = "martlet-pair-code-v1";

    /// <summary>The code as people read it: two groups of four separated by a dash.</summary>
    public static string Format(string canonical) => canonical[..4] + "-" + canonical[4..];

    /// <summary>Uppercases and drops spaces and dashes; false unless exactly <see cref="Length"/> symbols of
    /// <see cref="Alphabet"/> remain.</summary>
    public static bool TryNormalize(string? text, out string canonical)
    {
        var builder = new StringBuilder(Length);
        foreach (var character in text ?? "")
        {
            if (character is ' ' or '-' or '\t') continue;
            var upper = char.ToUpperInvariant(character);
            if (!Alphabet.Contains(upper) || builder.Length == Length)
            {
                canonical = "";
                return false;
            }
            builder.Append(upper);
        }
        canonical = builder.ToString();
        return canonical.Length == Length;
    }

    internal static string Generate(IGatewayCrypto crypto)
    {
        var bytes = crypto.RandomBytes(5);
        try
        {
            ulong bits = 0;
            foreach (var value in bytes) bits = bits << 8 | value;
            var code = new char[Length];
            for (var i = Length - 1; i >= 0; i--, bits >>= 5) code[i] = Alphabet[(int)(bits & 31)];
            return new string(code);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>PBKDF2-SHA256 over the canonical code, salted with the host's SPKI fingerprint (as the desktop saw it)
    /// and the desktop's nonce.</summary>
    public static byte[] DeriveKey(string canonical, string spkiFingerprint, ReadOnlySpan<byte> clientNonce)
    {
        var salt = Concat(Encoding.UTF8.GetBytes($"{Label}\n{spkiFingerprint}\n"), clientNonce);
        var password = Encoding.ASCII.GetBytes(canonical);
        try { return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    public static byte[] ClientProof(ReadOnlySpan<byte> key, string deviceId, string displayName, ReadOnlySpan<byte> clientNonce) =>
        HMACSHA256.HashData(key, Concat(Encoding.UTF8.GetBytes($"{Label} client\n{deviceId}\n{displayName}\n"), clientNonce));

    public static byte[] HostProof(ReadOnlySpan<byte> key, string hostId, string deviceId, string credentialId, string credentialSecret,
        ReadOnlySpan<byte> clientNonce)
    {
        var message = Concat(Encoding.UTF8.GetBytes($"{Label} host\n{hostId}\n{deviceId}\n{credentialId}\n{credentialSecret}\n"), clientNonce);
        try { return HMACSHA256.HashData(key, message); }
        finally { CryptographicOperations.ZeroMemory(message); }
    }

    private static byte[] Concat(byte[] head, ReadOnlySpan<byte> tail)
    {
        var bytes = new byte[head.Length + tail.Length];
        head.CopyTo(bytes, 0);
        tail.CopyTo(bytes.AsSpan(head.Length));
        return bytes;
    }
}

/// <summary>The owner's approval for one short-code pairing: which roles the desktop that types the code receives. The
/// desktop names itself when it redeems the code.</summary>
public sealed record GatewayCodePairingApproval
{
    public required IReadOnlyList<GatewayRole> Roles { get; init; }

    internal GatewayRole[] ValidateAndCopy()
    {
        GatewayRules.Require(Roles is { Count: > 0 and <= 3 }, "request.invalid");
        var roles = Roles.ToArray();
        foreach (var role in roles)
            GatewayRules.Defined(role);
        GatewayRules.Require(roles.Distinct().Count() == roles.Length, "request.invalid");
        Array.Sort(roles);
        return roles;
    }
}

/// <summary>What the host shows for a short-code pairing: its address and the code (formatted XXXX-XXXX).</summary>
public sealed record GatewayCodePairingCard
{
    public required string HostId { get; init; }
    public required string Origin { get; init; }
    public required string SpkiFingerprint { get; init; }
    public required GatewaySecret Code { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => nameof(GatewayCodePairingCard);
}

internal sealed record GatewayCodePairingResult(IssuedDeviceCredential Credential, string HostProof);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GatewayCodePairingProof
{
    public required GatewayProtocolVersion ProtocolVersion { get; init; }
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string ClientNonce { get; init; }
    public required string Proof { get; init; }

    internal void Validate()
    {
        GatewayRules.Require(ProtocolVersion is not null, "request.invalid");
        ProtocolVersion!.Validate();
        GatewayRules.Identifier(DeviceId);
        GatewayRules.Token(DisplayName, 64);
        GatewayRules.Require(Base64Url.TryDecode(ClientNonce, 16, out _), "request.invalid");
        GatewayRules.Require(Base64Url.TryDecode(Proof, 32, out _), "request.invalid");
    }
}
