using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

// Adapted from PR #37, 18daa4acfa90d7c7dff52861c93800a2f63b0823.
[SupportedOSPlatform("windows")]
internal static class WindowsProtection
{
    internal static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    internal static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (bytes.Length is <= 0 or > StoreFormat.MaximumBytes)
            throw Error(GatewayPersistenceFailure.InvalidState);
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success || output.Length is <= 0 or > StoreFormat.MaximumBytes || output.Data == IntPtr.Zero)
                throw Error(GatewayPersistenceFailure.KeyProtectionFailed);
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Zero(input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                if (output.Length is > 0 and <= StoreFormat.MaximumBytes)
                    Zero(output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }

    private static void Zero(IntPtr pointer, int length)
    {
        var zeros = new byte[Math.Min(length, 4096)];
        for (var offset = 0; offset < length; offset += zeros.Length)
            Marshal.Copy(zeros, 0, pointer + offset, Math.Min(zeros.Length, length - offset));
        CryptographicOperations.ZeroMemory(zeros);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        internal int Length;
        internal IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
