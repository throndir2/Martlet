using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Hardware;

/// <summary>Sends raw TPM 2.0 commands and returns the raw response.</summary>
public interface ITpmTransport : IDisposable
{
    byte[] Submit(byte[] command);
}

/// <summary>A TPM 2.0 key that the chip makes again from the same template after an OS reinstall: a primary key in the
/// endorsement hierarchy (whose seed survives even a TPM clear), or else in the owner hierarchy (survives a reinstall,
/// not a clear). The private key never leaves the chip. Raw commands keep it free of tpm2-tools and TSS libraries, so it
/// works on any Linux with /dev/tpmrm0 and on Windows through TBS.</summary>
public static class Tpm2
{
    public const uint Endorsement = 0x4000000B, Owner = 0x40000001;
    const ushort SessionsTag = 0x8002, NoSessionsTag = 0x8001;
    const uint CreatePrimaryCode = 0x131, SignCode = 0x15D, FlushContextCode = 0x165, PasswordSession = 0x40000009;

    /// <summary>The template's unique field: makes this key Martlet's own, apart from the chip's standard keys.</summary>
    static readonly byte[] Label = SHA256.HashData(Encoding.ASCII.GetBytes("martlet.host.hardware-key.v1"));

    /// <summary>The key's public half (SubjectPublicKeyInfo, DER) for the hierarchy, or a TPM response code error.</summary>
    public static byte[] PublicKey(ITpmTransport tpm, uint hierarchy)
    {
        var (handle, spki) = CreatePrimary(tpm, hierarchy);
        Flush(tpm, handle);
        return spki;
    }

    /// <summary>Signs a SHA-256 digest with the key; returns the IEEE P1363 signature (r||s) and the key's public half.</summary>
    public static (byte[] Signature, byte[] PublicKey) Sign(ITpmTransport tpm, uint hierarchy, ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 32) throw new ArgumentException("A SHA-256 digest is 32 bytes.", nameof(digest));
        var (handle, spki) = CreatePrimary(tpm, hierarchy);
        try
        {
            var w = new Writer();
            w.U16((ushort)digest.Length); w.Bytes(digest);
            w.U16(0x0018); w.U16(0x000B);            // TPMT_SIG_SCHEME: ECDSA, SHA-256
            w.U16(0x8024); w.U32(0x40000007); w.U16(0); // TPMT_TK_HASHCHECK: null ticket (the key isn't restricted)
            var r = new Reader(Run(tpm, SignCode, [handle], w.ToArray()));
            r.U32();                                   // parameterSize
            if (r.U16() != 0x0018) throw new TpmException("The TPM signed with another scheme.", 0);
            r.U16();
            var sig = new byte[64];
            Fixed(r.Sized()).CopyTo(sig, 0);
            Fixed(r.Sized()).CopyTo(sig, 32);
            return (sig, spki);
        }
        finally { Flush(tpm, handle); }
    }

    static (uint Handle, byte[] Spki) CreatePrimary(ITpmTransport tpm, uint hierarchy)
    {
        var w = new Writer();
        w.U16(4); w.U16(0); w.U16(0);                 // TPM2B_SENSITIVE_CREATE: no auth, no data
        var pub = new Writer();
        pub.U16(0x0023); pub.U16(0x000B);             // ECC, name SHA-256
        pub.U32(0x00040072);                          // fixedTPM, fixedParent, sensitiveDataOrigin, userWithAuth, sign
        pub.U16(0);                                   // no policy
        pub.U16(0x0010);                              // symmetric: null
        pub.U16(0x0018); pub.U16(0x000B);             // scheme: ECDSA SHA-256
        pub.U16(0x0003);                              // NIST P-256
        pub.U16(0x0010);                              // kdf: null
        pub.U16((ushort)Label.Length); pub.Bytes(Label); pub.U16(0);
        var area = pub.ToArray();
        w.U16((ushort)area.Length); w.Bytes(area);
        w.U16(0);                                     // outsideInfo
        w.U32(0);                                     // creationPCR: none
        var r = new Reader(Run(tpm, CreatePrimaryCode, [hierarchy], w.ToArray()));
        var handle = r.U32();
        r.U32();                                      // parameterSize
        var outPublic = new Reader(r.Sized());
        if (outPublic.U16() != 0x0023) throw new TpmException("The TPM made another kind of key.", 0);
        outPublic.U16(); outPublic.U32(); outPublic.Sized();
        if (outPublic.U16() != 0x0010) throw new TpmException("Unexpected key parameters.", 0);
        outPublic.U16(); outPublic.U16(); outPublic.U16(); outPublic.U16();
        var x = Fixed(outPublic.Sized()); var y = Fixed(outPublic.Sized());
        using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
        return (handle, ec.ExportSubjectPublicKeyInfo());
    }

    static void Flush(ITpmTransport tpm, uint handle)
    {
        try
        {
            var w = new Writer(); w.U16(NoSessionsTag); w.U32(14); w.U32(FlushContextCode); w.U32(handle);
            tpm.Submit(w.ToArray());
        }
        catch { /* The resource manager flushes transient objects when the connection closes. */ }
    }

    static byte[] Run(ITpmTransport tpm, uint code, uint[] handles, byte[] parameters)
    {
        var w = new Writer();
        w.U16(SessionsTag); w.U32(0); w.U32(code);
        foreach (var h in handles) w.U32(h);
        w.U32(9);                                     // authorization area: one password session with an empty password
        w.U32(PasswordSession); w.U16(0); w.Byte(0); w.U16(0);
        w.Bytes(parameters);
        var command = w.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(2), (uint)command.Length);
        var response = tpm.Submit(command);
        if (response.Length < 10) throw new TpmException("The TPM sent a short answer.", 0);
        var rc = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(6));
        if (rc != 0) throw new TpmException($"The TPM refused the command (0x{rc:X}).", rc);
        return response[10..];
    }

    static byte[] Fixed(byte[] value) => value.Length switch
    {
        32 => value,
        < 32 => [.. new byte[32 - value.Length], .. value],
        _ => throw new TpmException("A key value is too long.", 0),
    };

    sealed class Writer
    {
        readonly List<byte> b = [];
        public void Byte(byte v) => b.Add(v);
        public void U16(ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        public void Bytes(ReadOnlySpan<byte> v) { foreach (var x in v) b.Add(x); }
        public byte[] ToArray() => [.. b];
    }

    sealed class Reader(byte[] data)
    {
        int at;
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)); at += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)); at += 4; return v; }
        public byte[] Sized() { var n = U16(); Need(n); var v = data[at..(at + n)]; at += n; return v; }
        void Need(int n) { if (at + n > data.Length) throw new TpmException("The TPM's answer is cut short.", 0); }
    }
}

public sealed class TpmException(string message, uint responseCode) : Exception(message)
{
    public uint ResponseCode { get; } = responseCode;
}

/// <summary>/dev/tpmrm0: the kernel's resource manager, so several programs can use the TPM at once.</summary>
public sealed class LinuxTpmTransport(string path = LinuxTpmTransport.DefaultPath) : ITpmTransport
{
    public const string DefaultPath = "/dev/tpmrm0";
    readonly FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0);

    public byte[] Submit(byte[] command)
    {
        stream.Write(command);
        stream.Flush();
        var buffer = new byte[4096];
        var n = stream.Read(buffer);
        return buffer[..n];
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>Windows' TPM Base Services.</summary>
public sealed class WindowsTpmTransport : ITpmTransport
{
    readonly IntPtr context;

    public WindowsTpmTransport()
    {
        var p = new ContextParams2 { Version = 2, Flags = 1u << 2 }; // includeTpm20
        var rc = Tbsi_Context_Create(ref p, out context);
        if (rc != 0) throw new TpmException($"Windows' TPM service isn't available (0x{rc:X}).", rc);
    }

    public byte[] Submit(byte[] command)
    {
        var buffer = new byte[4096];
        var length = (uint)buffer.Length;
        var rc = Tbsip_Submit_Command(context, 0, 200, command, (uint)command.Length, buffer, ref length);
        if (rc != 0) throw new TpmException($"Windows' TPM service refused the command (0x{rc:X}).", rc);
        return buffer[..(int)length];
    }

    public void Dispose() => Tbsip_Context_Close(context);

    [StructLayout(LayoutKind.Sequential)]
    struct ContextParams2 { public uint Version; public uint Flags; }

    [DllImport("tbs.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint Tbsi_Context_Create(ref ContextParams2 p, out IntPtr context);

    [DllImport("tbs.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint Tbsip_Context_Close(IntPtr context);

    [DllImport("tbs.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint Tbsip_Submit_Command(IntPtr context, uint locality, uint priority,
        byte[] command, uint commandLength, byte[] result, ref uint resultLength);
}
