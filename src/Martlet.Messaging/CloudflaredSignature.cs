using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Martlet.Messaging;

/// <summary>Checks that a cloudflared.exe carries a valid Windows Authenticode signature from Cloudflare before Martlet runs it,
/// also a copy found on PATH or installed by the owner. The check uses only what Windows has cached (no revocation download),
/// and its result is kept while the file's size and time stay the same.</summary>
public static class CloudflaredSignature
{
    /// <summary>The signer's name (certificate common name) on Cloudflare's official Windows builds.</summary>
    public const string Signer = "Cloudflare, Inc.";

    private static readonly Dictionary<string, (long Length, DateTime Written, string? Problem)> checkedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why Martlet doesn't run <paramref name="path"/> ("it has no valid Windows signature ..."), or null when it is signed by Cloudflare (or this is not Windows,
    /// where Authenticode doesn't apply).</summary>
    public static string? Problem(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists) return "it isn't there";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return error.Message; }
        lock (checkedFiles)
            if (checkedFiles.TryGetValue(path, out var seen) && seen.Length == file.Length && seen.Written == file.LastWriteTimeUtc)
                return seen.Problem;
        var problem = Check(path);
        lock (checkedFiles) checkedFiles[path] = (file.Length, file.LastWriteTimeUtc, problem);
        return problem;
    }

    private static string? Check(string path)
    {
        var status = Verify(path);
        if (status != 0) return $"it has no valid Windows signature (0x{status:X8})";
        try
        {
#pragma warning disable SYSLIB0057 // The signer of a signed file has no X509CertificateLoader equivalent.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.Equals(name, Signer, StringComparison.Ordinal) ? null
                : $"it is signed by {name}, not {Signer}";
        }
        catch (CryptographicException error) { return $"its signer can't be read ({error.Message})"; }
    }

    // ---------- WinVerifyTrust (wintrust.dll) ----------

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2, RevokeNone = 0, ChoiceFile = 1, StateVerify = 1, StateClose = 2, CacheOnlyUrlRetrieval = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoData
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

#pragma warning disable SYSLIB1054 // DllImport keeps the struct marshalling simple here.
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
#pragma warning restore SYSLIB1054

    private static int Verify(string path)
    {
        var info = new FileInfoData { Size = (uint)Marshal.SizeOf<FileInfoData>(), FilePath = path };
        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
        try
        {
            Marshal.StructureToPtr(info, infoPointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = UiNone, RevocationChecks = RevokeNone, UnionChoice = ChoiceFile,
                FileInfo = infoPointer, StateAction = StateVerify, ProviderFlags = CacheOnlyUrlRetrieval
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.StateAction = StateClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<FileInfoData>(infoPointer);
            Marshal.FreeHGlobal(infoPointer);
        }
    }
}
