using System.IO;
using System.Security.Cryptography;
using System.Text;

#if MARTLET_MCP
using Martlet.Mcp.Logging;

namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>
/// *Encrypt my files on this PC while locked* (docs/ACCOUNTS.md, Privacy): an account's folder (<c>&lt;data&gt;\accounts\&lt;32
/// hex&gt;\</c>) encrypted on disk while the account is locked, with a random 256-bit file key that only its PIN or its Martlet
/// password opens (<see cref="Wrap"/>: AES-256-GCM under a PBKDF2 key). Locking encrypts each file in place to
/// <c>&lt;name&gt;.mlock</c> (AES-256-GCM, the file's path inside the folder as associated data) and unlocking decrypts them.
/// One file at a time with an atomic replace: a crash leaves every file whole, either plain or encrypted, and when both are
/// there the plain one wins (the other was made from it). A file another program has open is left plain and reported.
/// </summary>
internal static class AccountVault
{
    internal const string SealedExtension = ".mlock";
    private const string PartExtension = ".mlockpart";
    internal const long MaximumFileBytes = 512L * 1024 * 1024;
    private static readonly byte[] Magic = "MLOCK1\n"u8.ToArray();
    private static readonly byte[] KeyPurpose = "martlet-account-file-key-v1"u8.ToArray();
    private const int NonceBytes = 12, TagBytes = 16;

    internal static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>The file key wrapped under <paramref name="secret"/> (a PIN or a password).</summary>
    internal static AccountKeyWrap Wrap(byte[] fileKey, string secret, int iterations = AccountProtection.Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var wrapping = AccountProtection.Key(secret, salt, iterations);
        try
        {
            var sealedKey = new byte[fileKey.Length + TagBytes];
            using var aes = new AesGcm(wrapping, TagBytes);
            aes.Encrypt(nonce, fileKey, sealedKey.AsSpan(0, fileKey.Length), sealedKey.AsSpan(fileKey.Length), KeyPurpose);
            return new()
            {
                Salt = Convert.ToBase64String(salt), Iterations = iterations, Nonce = Convert.ToBase64String(nonce),
                Data = Convert.ToBase64String(sealedKey)
            };
        }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    /// <summary>The file key <paramref name="wrap"/> holds, or null when <paramref name="secret"/> doesn't open it.</summary>
    internal static byte[]? Unwrap(AccountKeyWrap? wrap, string? secret)
    {
        if (wrap is null || secret is not { Length: > 0 and <= 256 } || wrap.Iterations is < 1 or > 10_000_000) return null;
        byte[] salt, nonce, data;
        try
        {
            salt = Convert.FromBase64String(wrap.Salt);
            nonce = Convert.FromBase64String(wrap.Nonce);
            data = Convert.FromBase64String(wrap.Data);
        }
        catch (FormatException) { return null; }
        if (nonce.Length != NonceBytes || data.Length != 32 + TagBytes) return null;
        var wrapping = AccountProtection.Key(secret, salt, wrap.Iterations);
        var key = new byte[32];
        try
        {
            using var aes = new AesGcm(wrapping, TagBytes);
            aes.Decrypt(nonce, data.AsSpan(0, 32), data.AsSpan(32), key, KeyPurpose);
            return key;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    /// <summary>Encrypts every plain file of <paramref name="folder"/> (and its subfolders) with <paramref name="fileKey"/>.</summary>
    internal static AccountSealResult Seal(string folder, byte[] fileKey) => Walk(folder, fileKey, seal: true);

    /// <summary>Decrypts every <c>.mlock</c> file of <paramref name="folder"/> with <paramref name="fileKey"/>.</summary>
    internal static AccountSealResult Unseal(string folder, byte[] fileKey) => Walk(folder, fileKey, seal: false);

    /// <summary>How many files of <paramref name="folder"/> are encrypted and how many are plain.</summary>
    internal static (int Encrypted, int Plain) Count(string folder)
    {
        if (!Directory.Exists(folder)) return (0, 0);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(PartExtension, StringComparison.Ordinal)).ToList();
        var encrypted = files.Count(f => f.EndsWith(SealedExtension, StringComparison.Ordinal));
        return (encrypted, files.Count - encrypted);
    }

    private static AccountSealResult Walk(string folder, byte[] fileKey, bool seal)
    {
        if (fileKey.Length != 32) throw new ArgumentException("The file key must be 32 bytes.", nameof(fileKey));
        if (!Directory.Exists(folder)) return new(0, []);
        var done = 0;
        var skipped = new List<string>();
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        foreach (var part in files.Where(f => f.EndsWith(PartExtension, StringComparison.Ordinal)))
            TryDelete(part);
        foreach (var file in files.Where(f => !f.EndsWith(PartExtension, StringComparison.Ordinal)))
        {
            var encrypted = file.EndsWith(SealedExtension, StringComparison.Ordinal);
            var plain = encrypted ? file[..^SealedExtension.Length] : file;
            try
            {
                if (encrypted && File.Exists(plain))
                {
                    // Both copies: a crash came between the replace and the delete. The plain one is whole and current.
                    File.Delete(file);
                    if (!seal) continue;
                    encrypted = false;
                }
                if (seal == encrypted) continue;
                var name = Relative(folder, plain);
                if (seal) Encrypt(plain, name, fileKey);
                else Decrypt(file, plain, name, fileKey);
                done++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
            {
                skipped.Add(Relative(folder, plain));
                ErrorLog.Warn($"Account files: could not {(seal ? "encrypt" : "decrypt")} {Relative(folder, plain)} ({error.GetType().Name}: {error.Message}).");
            }
        }
        return new(done, skipped);
    }

    private static void Encrypt(string plain, string name, byte[] fileKey)
    {
        byte[] content;
        // FileShare.None: a file another program has open stays plain rather than being replaced under it.
        using (var stream = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The file is larger than 512 MB.");
            content = new byte[stream.Length];
            stream.ReadExactly(content);
        }
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var output = new byte[Magic.Length + NonceBytes + TagBytes + content.Length];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        try
        {
            using var aes = new AesGcm(fileKey, TagBytes);
            aes.Encrypt(nonce, content, output.AsSpan(Magic.Length + NonceBytes + TagBytes), output.AsSpan(Magic.Length + NonceBytes, TagBytes),
                Encoding.UTF8.GetBytes(name));
        }
        finally { CryptographicOperations.ZeroMemory(content); }
        Replace(plain + SealedExtension, output);
        File.Delete(plain);
    }

    private static void Decrypt(string sealedFile, string plain, string name, byte[] fileKey)
    {
        var input = File.ReadAllBytes(sealedFile);
        var header = Magic.Length + NonceBytes + TagBytes;
        if (input.Length < header || !input.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("It is not a Martlet encrypted file.");
        var content = new byte[input.Length - header];
        try
        {
            using var aes = new AesGcm(fileKey, TagBytes);
            aes.Decrypt(input.AsSpan(Magic.Length, NonceBytes), input.AsSpan(header), input.AsSpan(Magic.Length + NonceBytes, TagBytes), content,
                Encoding.UTF8.GetBytes(name));
            Replace(plain, content);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
        File.Delete(sealedFile);
    }

    private static void Replace(string path, byte[] bytes)
    {
        var part = path + PartExtension;
        try
        {
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(part, path, overwrite: true);
        }
        finally { TryDelete(part); }
    }

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/').ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>An account's file key wrapped under a PIN or a password (base64): PBKDF2 salt and iterations, the AES-GCM nonce and
/// the encrypted key with its tag.</summary>
internal sealed record AccountKeyWrap
{
    public required string Salt { get; init; }
    public required int Iterations { get; init; }
    public required string Nonce { get; init; }
    public required string Data { get; init; }
}

/// <summary>How many files a lock or an unlock changed, and the ones it left as they were (paths inside the folder).</summary>
internal sealed record AccountSealResult(int Done, IReadOnlyList<string> Skipped);
