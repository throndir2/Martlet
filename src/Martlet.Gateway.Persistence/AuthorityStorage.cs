using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

public enum GatewayStorageBackend { WindowsCurrentUserDpapi, LinuxServicePermissions }

internal interface IOwnedAuthorityDirectory : IDisposable
{
    void ValidateFiles();
    bool Exists(string name);
    void Validate(string name);
    byte[] Read(string name, int maximum);
    void WriteNew(string name, ReadOnlySpan<byte> bytes);
    void Prepare();
    void Promote(bool replace, Action? replaced = null);
    void RemoveUnpreparedStage();
    void RemoveRunning();
}

internal interface IAuthorityEnvelope
{
    byte[] Encode(StoreDocument document);
    StoreDocument Decode(byte[] bytes);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiEnvelope : IAuthorityEnvelope
{
    public byte[] Encode(StoreDocument document)
    {
        var clear = StoreFormat.Write(document);
        byte[]? cipher = null;
        try
        {
            cipher = WindowsProtection.Protect(clear);
            return StoreFormat.Wrap(cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
        }
    }

    public StoreDocument Decode(byte[] bytes)
    {
        var cipher = StoreFormat.Unwrap(bytes);
        byte[]? clear = null;
        try
        {
            clear = WindowsProtection.Unprotect(cipher);
            return StoreFormat.Read(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }
}

// The checksum detects accidental damage, not tampering by a writer of this plaintext state.
internal sealed class LinuxPermissionEnvelope : IAuthorityEnvelope
{
    internal static ReadOnlySpan<byte> Magic => "MRTLUP02"u8;
    public byte[] Encode(StoreDocument document)
    {
        var clear = StoreFormat.Write(document);
        try
        {
            var result = new byte[clear.Length + 48];
            Magic.CopyTo(result);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), 2);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), clear.Length);
            clear.CopyTo(result, 16);
            SHA256.HashData(result.AsSpan(0, result.Length - 32), result.AsSpan(result.Length - 32));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public StoreDocument Decode(byte[] bytes)
    {
        if (bytes.Length >= 8)
        {
            if (bytes.AsSpan(0, 8).SequenceEqual("MRTLHM02"u8))
                throw Error(GatewayPersistenceFailure.StorageBackendMismatch);
            if (bytes.AsSpan(0, 8).SequenceEqual("MRTLHM01"u8) ||
                bytes.AsSpan(0, 8).SequenceEqual("MARTLET1"u8))
                throw Error(GatewayPersistenceFailure.MigrationRequired);
        }
        if (bytes.Length is <= 48 or > StoreFormat.MaximumBytes ||
            !bytes.AsSpan(0, 8).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != 2 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) != bytes.Length - 48 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)),
                bytes.AsSpan(bytes.Length - 32)))
            throw Error(GatewayPersistenceFailure.InvalidState);
        var clear = bytes.AsSpan(16, bytes.Length - 48).ToArray();
        try { return StoreFormat.Read(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
