using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Credentials.Windows;

// The only advapi32 boundary. No enumeration, redirects, network or global configuration APIs.
internal sealed class WindowsCredentialNative : ICredentialNative
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public int Write(string target, ReadOnlySpan<char> secret)
    {
        if (!OperatingSystem.IsWindows()) return 50;
        var blob = Marshal.AllocHGlobal(secret.Length * 2);
        try
        {
            for (var i = 0; i < secret.Length; i++) Marshal.WriteInt16(blob, i * 2, (short)secret[i]);
            var credential = new NativeCredential
            {
                Type = 1, TargetName = target, CredentialBlobSize = (uint)(secret.Length * 2),
                CredentialBlob = blob, Persist = 2, UserName = "Martlet.OpenAI"
            };
            return CredWrite(ref credential, 0) ? 0 : Marshal.GetLastPInvokeError();
        }
        finally
        {
            Zero(blob, secret.Length * 2);
            Marshal.FreeHGlobal(blob);
        }
    }

    public int Read(string target, out SecretLease? secret)
    {
        secret = null;
        if (!OperatingSystem.IsWindows()) return 50;
        if (!CredRead(target, 1, 0, out var pointer)) return Marshal.GetLastPInvokeError();
        var credential = new NativeCredential();
        try
        {
            credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.Type != 1 || credential.TargetName != target || credential.CredentialBlob == IntPtr.Zero ||
                credential.CredentialBlobSize is < 2 or > SecretLease.MaximumLength * 2 || credential.CredentialBlobSize % 2 != 0)
                return 13;
            var chars = new char[credential.CredentialBlobSize / 2];
            try
            {
                Marshal.Copy(credential.CredentialBlob, chars, 0, chars.Length);
                secret = new SecretLease(chars);
                return 0;
            }
            catch (ContractException) { return 13; }
            finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan())); }
        }
        finally
        {
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize <= 2560)
                Zero(credential.CredentialBlob, (int)credential.CredentialBlobSize);
            CredFree(pointer);
        }
    }

    public int Delete(string target)
    {
        if (!OperatingSystem.IsWindows()) return 50;
        return CredDelete(target, 1, 0) ? 0 : Marshal.GetLastPInvokeError();
    }

    public DateTimeOffset? WrittenAt(string target)
    {
        if (!OperatingSystem.IsWindows() || !CredRead(target, 1, 0, out var pointer)) return null;
        var credential = new NativeCredential();
        try
        {
            credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.Type != 1 || credential.TargetName != target) return null;
            var ticks = (long)(uint)credential.LastWritten.dwHighDateTime << 32 | (uint)credential.LastWritten.dwLowDateTime;
            return ticks <= 0 ? null : DateTimeOffset.FromFileTime(ticks).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException) { return null; }
        finally
        {
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize <= 2560)
                Zero(credential.CredentialBlob, (int)credential.CredentialBlobSize);
            CredFree(pointer);
        }
    }

    private static void Zero(IntPtr pointer, int length)
    {
        for (var i = 0; i < length; i++) Marshal.WriteByte(pointer, i, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
