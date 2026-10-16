using System.IO;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Martlet.Desktop;

/// <summary>What this Windows user's own OpenSSH setup (%USERPROFILE%\.ssh) already has for a computer, so Martlet's first
/// connection asks nothing that is set up already: a host key that known_hosts trusts, and private keys without a passphrase
/// that may sign in there (only to add Martlet's own key; Martlet never copies or keeps them).</summary>
internal static class OwnerSsh
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    private static readonly string[] KeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    /// <summary>Whether the owner's known_hosts lists <paramref name="target"/> (its host, with [host]:port for another port;
    /// plain or hashed names) with exactly this host key.</summary>
    internal static bool Trusts(HostShellTarget target, byte[] hostKey)
    {
        string text;
        try { text = File.ReadAllText(Path.Combine(Folder, "known_hosts")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        return Trusts(text, target, hostKey);
    }

    internal static bool Trusts(string knownHosts, HostShellTarget target, byte[] hostKey)
    {
        var host = target.Host.ToLowerInvariant();
        var name = target.Port == 22 ? host : $"[{host}]:{target.Port}";
        foreach (var raw in knownHosts.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '@') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !Names(parts[0], name)) continue;
            byte[] key;
            try { key = Convert.FromBase64String(parts[2]); }
            catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(key, hostKey)) return true;
        }
        return false;
    }

    private static bool Names(string field, string name)
    {
        if (!field.StartsWith("|1|", StringComparison.Ordinal))
            return field.Split(',').Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        var parts = field.Split('|');
        if (parts.Length != 4) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(HMACSHA1.HashData(Convert.FromBase64String(parts[2]), Encoding.ASCII.GetBytes(name)),
                Convert.FromBase64String(parts[3]));
        }
        catch (FormatException) { return false; }
    }

    /// <summary>The owner's default private keys that open without a passphrase, with their file names. The caller disposes them.</summary>
    internal static IReadOnlyList<(string Name, PrivateKeyFile Key)> Keys(string? folder = null)
    {
        var keys = new List<(string, PrivateKeyFile)>();
        foreach (var name in KeyNames)
        {
            var path = Path.Combine(folder ?? Folder, name);
            if (!File.Exists(path)) continue;
            try { keys.Add((name, new PrivateKeyFile(path))); }
            catch (Exception error) when (error is SshException or SshPassPhraseNullOrEmptyException or IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or NotSupportedException or FormatException or CryptographicException) { }
        }
        return keys;
    }
}
