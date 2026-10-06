using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Access;

namespace Martlet.Gateway;

/// <summary>
/// The owner's sign-in account on a host: a password kept only as a PBKDF2-SHA256 verifier (600,000 iterations, 16-byte
/// salt, OWASP's current figure; .NET has no built-in Argon2id), a TOTP authenticator secret (RFC 6238, see
/// <see cref="Totp"/>) that is mandatory, and ten one-use recovery codes kept as SHA-256 verifiers. Signing in needs the
/// password and either a current authenticator code (each works once) or an unused recovery code.
/// </summary>
internal static class GatewayAccounts
{
    internal const int PasswordIterations = 600_000;
    internal const int MinimumPasswordLength = 12;
    internal const int MaximumPasswordLength = 256;
    internal const int RecoveryCodeCount = 10;
    private const string RecoveryAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    internal static GatewayPasswordVerifier HashPassword(string password, int iterations = PasswordIterations)
    {
        RequirePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        return new() { Salt = Base64Url.Encode(salt), Iterations = iterations, Hash = Base64Url.Encode(Derive(password, salt, iterations)) };
    }

    internal static bool VerifyPassword(string? password, GatewayPasswordVerifier verifier)
    {
        if (password is not { Length: > 0 and <= MaximumPasswordLength}) return false;
        var salt = System.Buffers.Text.Base64Url.DecodeFromChars(verifier.Salt);
        var expected = System.Buffers.Text.Base64Url.DecodeFromChars(verifier.Hash);
        var actual = Derive(password, salt, verifier.Iterations);
        try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    internal static void RequirePassword(string? password) =>
        GatewayRules.Require(password is { Length: >= MinimumPasswordLength and <= MaximumPasswordLength } && password.All(c => !char.IsControl(c)),
            "signin.weak_password");

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Ten fresh recovery codes as people read them (XXXXX-XXXXX, 50 bits each) and their verifiers.</summary>
    internal static (IReadOnlyList<string> Codes, List<string> Verifiers) NewRecoveryCodes()
    {
        var codes = new List<string>();
        var verifiers = new List<string>();
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(10);
            var code = new string(bytes.Select(b => RecoveryAlphabet[b & 31]).ToArray());
            codes.Add(code[..5] + "-" + code[5..]);
            verifiers.Add(RecoveryVerifier(code)!);
        }
        return (codes, verifiers);
    }

    /// <summary>The verifier of a typed recovery code (case, spaces and dashes ignored); null when it can't be one.</summary>
    internal static string? RecoveryVerifier(string? typed)
    {
        var code = new string((typed ?? "").Where(c => c is not (' ' or '-')).Select(char.ToUpperInvariant).ToArray());
        if (code.Length != 10 || !code.All(RecoveryAlphabet.Contains)) return null;
        return Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes("martlet-recovery-v1\n" + code)));
    }

    internal static void RequireTotpSecret(string? secret)
    {
        try { GatewayRules.Require(secret is { Length: >= 16 and <= 128 } && Base32.Decode(secret).Length >= 10, "request.invalid"); }
        catch (FormatException) { throw new GatewayProtocolException("request.invalid"); }
    }
}

internal sealed record GatewayPasswordVerifier
{
    public required string Salt { get; init; }
    public required int Iterations { get; init; }
    public required string Hash { get; init; }
}
