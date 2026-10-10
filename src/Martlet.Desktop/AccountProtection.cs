using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>
/// How an account's unlock secrets are kept on this PC (docs/ACCOUNTS.md, Unlock): a PIN or a Martlet password only as a
/// PBKDF2-SHA256 verifier (600,000 iterations, 16-byte salt, as hosts keep passwords), and keys from them for the account's
/// file key. The verifiers are then protected with DPAPI for this Windows user (CurrentUser scope, with Martlet's own entropy),
/// so other Windows users of the PC can't read them. People who share this Windows login can; the PBKDF2 cost is what keeps
/// them from guessing.
/// </summary>
internal static class AccountProtection
{
    internal const int Iterations = 600_000;
    private static readonly byte[] Entropy = "martlet-account-lock-v1"u8.ToArray();

    internal static AccountSecretVerifier Verifier(string secret, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(secret, salt, iterations, 32);
        try { return new() { Salt = Convert.ToBase64String(salt), Iterations = iterations, Hash = Convert.ToBase64String(hash) }; }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    internal static bool Matches(string? secret, AccountSecretVerifier? verifier)
    {
        if (secret is not { Length: > 0 and <= 256 } || verifier is null || verifier.Iterations is < 1 or > 10_000_000) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(verifier.Salt);
            expected = Convert.FromBase64String(verifier.Hash);
        }
        catch (FormatException) { return false; }
        var actual = Derive(secret, salt, verifier.Iterations, expected.Length is > 0 and <= 64 ? expected.Length : 32);
        try { return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    /// <summary>A 32-byte key from <paramref name="secret"/> (for wrapping the account's file key).</summary>
    internal static byte[] Key(string secret, byte[] salt, int iterations) => Derive(secret, salt, iterations, 32);

    private static byte[] Derive(string secret, byte[] salt, int iterations, int length)
    {
        var bytes = Encoding.UTF8.GetBytes(secret.Normalize(NormalizationForm.FormKC));
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, length); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>DPAPI for this Windows user. Throws <see cref="CryptographicException"/> when Windows refuses.</summary>
    internal static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

    /// <summary>The bytes <see cref="Protect"/> protected. Throws <see cref="CryptographicException"/> when they were protected
    /// by another Windows user or are damaged.</summary>
    internal static byte[] Unprotect(byte[] protectedBytes) => Transform(protectedBytes, protect: false);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (bytes.Length is <= 0 or > 1024 * 1024) throw new CryptographicException("The account lock data has a wrong size.");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var entropy = new Blob { Length = Entropy.Length, Data = Marshal.AllocHGlobal(Entropy.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);
            var success = protect
                ? CryptProtectData(ref input, null, ref entropy, 0, 0, UiForbidden, out output)
                : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, UiForbidden, out output);
            if (!success || output.Data == 0 || output.Length <= 0)
                throw new CryptographicException($"Windows could not {(protect ? "protect" : "open")} the account lock data ({Marshal.GetLastPInvokeError()}).");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Zero(input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != 0)
            {
                Zero(output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }

    private static void Zero(nint pointer, int length)
    {
        for (var offset = 0; offset < length; offset++) Marshal.WriteByte(pointer, offset, 0);
    }

    private const int UiForbidden = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        internal int Length;
        internal nint Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, nint reserved, nint prompt, int flags,
        out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, int flags,
        out Blob output);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);
}

/// <summary>A PBKDF2-SHA256 verifier of a PIN or password (base64 salt and hash).</summary>
internal sealed record AccountSecretVerifier
{
    public required string Salt { get; init; }
    public required int Iterations { get; init; }
    public required string Hash { get; init; }
}
