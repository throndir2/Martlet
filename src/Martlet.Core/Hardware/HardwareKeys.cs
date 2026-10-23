using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Hardware;

/// <summary>One key that a computer's TPM makes again after an OS reinstall. <see cref="Key"/> is base64url
/// SubjectPublicKeyInfo (ECDSA P-256).</summary>
public sealed record HardwareKey(string Kind, string Key)
{
    /// <summary>A key in the endorsement hierarchy: it stays the same after an OS reinstall and after a TPM clear.</summary>
    public const string Endorsement = "tpm-endorsement";
    /// <summary>A key in the owner hierarchy: it stays the same after an OS reinstall, not after a TPM clear.</summary>
    public const string Owner = "tpm-owner";
}

public sealed record HardwareSignature(string Kind, string Signature);

/// <summary>This computer's hardware keys (see <see cref="Tpm2"/>), or why it has none. Making them reads the TPM once.</summary>
public sealed class HardwareKeys
{
    private readonly Func<ITpmTransport>? open;
    private readonly object gate = new();

    private HardwareKeys(Func<ITpmTransport>? open, IReadOnlyList<HardwareKey> keys, string? unavailable)
    {
        this.open = open;
        Keys = keys;
        Unavailable = unavailable;
    }

    /// <summary>The keys this computer's TPM makes (endorsement first). Empty when <see cref="Unavailable"/> says why.</summary>
    public IReadOnlyList<HardwareKey> Keys { get; }

    /// <summary>Why this computer has no hardware key (no TPM, no access to it, or the TPM refused); null when it has one.</summary>
    public string? Unavailable { get; }

    public static HardwareKeys None(string reason) => new(null, [], reason);

    /// <summary>Reads this computer's TPM: /dev/tpmrm0 on Linux, TPM Base Services on Windows. Other systems have none.</summary>
    public static HardwareKeys ForThisComputer()
    {
        if (OperatingSystem.IsLinux())
        {
            var path = LinuxTpmTransport.DefaultPath;
            if (!File.Exists(path))
                return None(Directory.Exists("/sys/class/tpm") && Directory.EnumerateDirectories("/sys/class/tpm").Any()
                    ? "This computer's TPM has no resource manager (/dev/tpmrm0); the Linux kernel is older than 4.12."
                    : "This computer has no TPM 2.0 (or it is turned off in the firmware settings).");
            return From(() => new LinuxTpmTransport(path));
        }
        if (OperatingSystem.IsWindows()) return From(() => new WindowsTpmTransport());
        return None("Martlet reads a TPM only on Linux and Windows.");
    }

    /// <summary>Makes the keys through <paramref name="open"/> (a real TPM or a test double).</summary>
    public static HardwareKeys From(Func<ITpmTransport> open)
    {
        var keys = new List<HardwareKey>();
        var problems = new List<string>();
        foreach (var (hierarchy, kind) in Hierarchies)
        {
            try
            {
                using var tpm = open();
                keys.Add(new(kind, Base64Url.EncodeToString(Tpm2.PublicKey(tpm, hierarchy))));
            }
            catch (UnauthorizedAccessException)
            {
                return None("Martlet can't use this computer's TPM: its account has no access to /dev/tpmrm0 " +
                    "(set up the host again, or add the account to the tss group).");
            }
            catch (Exception e) when (e is TpmException or IOException or DllNotFoundException or EntryPointNotFoundException)
            {
                problems.Add($"{kind}: {e.Message}");
            }
        }
        return keys.Count > 0 ? new(open, keys, null) : None("The TPM refused to make Martlet's key (" + string.Join("; ", problems) + ").");
    }

    /// <summary>Signs <paramref name="digest"/> (SHA-256) with each key. A key the TPM no longer makes is left out.</summary>
    public IReadOnlyList<HardwareSignature> Sign(ReadOnlySpan<byte> digest)
    {
        if (open is null || Keys.Count == 0) return [];
        var signatures = new List<HardwareSignature>();
        var copy = digest.ToArray();
        lock (gate)
        {
            foreach (var (hierarchy, kind) in Hierarchies)
            {
                if (!Keys.Any(k => k.Kind == kind)) continue;
                try
                {
                    using var tpm = open();
                    var (signature, _) = Tpm2.Sign(tpm, hierarchy, copy);
                    signatures.Add(new(kind, Base64Url.EncodeToString(signature)));
                }
                catch (Exception e) when (e is TpmException or IOException or UnauthorizedAccessException)
                {
                    // The other key can still prove it.
                }
            }
        }
        return signatures;
    }

    private static readonly (uint Hierarchy, string Kind)[] Hierarchies =
        [(Tpm2.Endorsement, HardwareKey.Endorsement), (Tpm2.Owner, HardwareKey.Owner)];
}

/// <summary>What a host shows anyone who asks at <see cref="HardwareProof.Path"/>: its hardware keys and their
/// signatures over its host ID, its TLS key and the asker's nonce. It holds no secret.</summary>
public sealed record HardwareProofDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required string HostId { get; init; }
    /// <summary>The host's TLS key fingerprint (sha256:&lt;hex&gt;) as the host knows it.</summary>
    public required string Spki { get; init; }
    public required string Nonce { get; init; }
    public IReadOnlyList<HardwareKey> Keys { get; init; } = [];
    public IReadOnlyList<HardwareSignature> Signatures { get; init; } = [];
    /// <summary>Why the host has no hardware key; null when it has one.</summary>
    public string? Unavailable { get; init; }
}

public enum HardwareCheck
{
    /// <summary>A key this PC saved for the host signed the host's current TLS key: it is the same computer.</summary>
    Proven,
    /// <summary>The proof is valid but signed by none of the saved keys: another computer, or its TPM was reset.</summary>
    OtherKeys,
    /// <summary>The host has no hardware key.</summary>
    NoKeys,
    /// <summary>The proof is malformed, for another host or TLS key, or a signature doesn't verify.</summary>
    Invalid,
}

public static class HardwareProof
{
    public const string Path = "/martlet/v1/hardware";
    public const int NonceBytes = 16;

    /// <summary>The route's JSON: snake_case like the gateway's other routes.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8,
    };

    /// <summary>The request target that asks a host for a proof.</summary>
    public static string Target(string nonce) => Path + "?nonce=" + nonce;

    /// <summary>A fresh nonce (base64url, 16 random bytes).</summary>
    public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(NonceBytes));

    public static bool IsNonce(string? nonce) =>
        nonce is { Length: 22 } && Base64Url.IsValid(nonce, out var bytes) && bytes == NonceBytes;

    /// <summary>The digest each key signs: the host ID, its TLS key and the nonce, so a proof can't be moved to another
    /// host, another TLS key or another question.</summary>
    public static byte[] Digest(string hostId, string spki, string nonce) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"martlet-hardware-proof-v1\n{hostId}\n{spki}\n{nonce}"));

    public static HardwareProofDocument Make(HardwareKeys keys, string hostId, string spki, string nonce) => new()
    {
        HostId = hostId,
        Spki = spki,
        Nonce = nonce,
        Keys = keys.Keys,
        Signatures = keys.Sign(Digest(hostId, spki, nonce)),
        Unavailable = keys.Unavailable,
    };

    /// <summary>The keys in <paramref name="proof"/> whose signatures verify for this host, TLS key and nonce.</summary>
    public static IReadOnlyList<HardwareKey> Verified(HardwareProofDocument proof, string hostId, string spki, string nonce)
    {
        if (proof.HostId != hostId || proof.Spki != spki || proof.Nonce != nonce) return [];
        var digest = Digest(hostId, spki, nonce);
        var verified = new List<HardwareKey>();
        foreach (var key in proof.Keys.DistinctBy(k => k.Kind).Take(4))
        {
            var signature = proof.Signatures.FirstOrDefault(s => s.Kind == key.Kind);
            if (signature is null || !Verifies(key.Key, signature.Signature, digest)) continue;
            verified.Add(key);
        }
        return verified;
    }

    /// <summary>Checks <paramref name="proof"/>, read over a connection whose TLS key is <paramref name="spki"/>, against
    /// the keys this PC saved for the host (<paramref name="saved"/>).</summary>
    public static HardwareCheck Check(HardwareProofDocument proof, string hostId, string spki, string nonce,
        IReadOnlyList<HardwareKey>? saved)
    {
        if (proof.HostId != hostId || proof.Spki != spki || proof.Nonce != nonce) return HardwareCheck.Invalid;
        if (proof.Keys.Count == 0) return HardwareCheck.NoKeys;
        var verified = Verified(proof, hostId, spki, nonce);
        if (verified.Count == 0) return proof.Signatures.Count == 0 ? HardwareCheck.NoKeys : HardwareCheck.Invalid;
        return saved is { Count: > 0 } && verified.Any(v => saved.Any(s => s.Kind == v.Kind && s.Key == v.Key))
            ? HardwareCheck.Proven
            : HardwareCheck.OtherKeys;
    }

    private static bool Verifies(string key, string signature, byte[] digest)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Base64Url.DecodeFromChars(key), out _);
            return ec.KeySize == 256 && ec.VerifyHash(digest, Base64Url.DecodeFromChars(signature));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
