using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Settings;

namespace Martlet.Credentials.Windows;

/// <summary>
/// For MCP verification on a disposable data directory only: keeps secrets as files in one folder instead of Windows
/// Credential Manager, so a lab host's pairing secret never enters the real vault. Used only when the
/// <see cref="Variable"/> environment variable names an existing absolute folder (scripts\Invoke-MartletMcp.ps1
/// -LabCredentials sets it to a folder inside the disposable data directory for the desktop and MCP processes it starts).
/// The files are plaintext; the folder is thrown away with the data directory.
/// </summary>
public sealed class LabCredentialNative(string directory) : ICredentialNative
{
    public const string Variable = "MARTLET_LAB_CREDENTIALS";

    /// <summary>The lab store when <see cref="Variable"/> names an existing absolute folder; null otherwise.</summary>
    public static LabCredentialNative? FromEnvironment() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path && Path.IsPathFullyQualified(path) && Directory.Exists(path)
            ? new(path) : null;

    public bool IsSupported => true;

    private string File(string target) =>
        Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target))) + ".secret");

    public int Write(string target, ReadOnlySpan<char> secret)
    {
        System.IO.File.WriteAllText(File(target), new string(secret), new UTF8Encoding(false));
        return 0;
    }

    public int Read(string target, out SecretLease? secret)
    {
        secret = null;
        var path = File(target);
        if (!System.IO.File.Exists(path)) return 1168;
        secret = new SecretLease(System.IO.File.ReadAllText(path, Encoding.UTF8));
        return 0;
    }

    public int Delete(string target)
    {
        var path = File(target);
        if (!System.IO.File.Exists(path)) return 1168;
        System.IO.File.Delete(path);
        return 0;
    }

    public DateTimeOffset? WrittenAt(string target) =>
        System.IO.File.Exists(File(target)) ? System.IO.File.GetLastWriteTimeUtc(File(target)) : null;
}
