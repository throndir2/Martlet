using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Gateway.Trust;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal sealed record StoreDocument(int Version, Guid HostId, Guid Generation, long Revision,
    byte[] Certificate, TrustCheckpoint Trust) : IDisposable
{
    public void Dispose()
    {
        if (Certificate is not null)
            CryptographicOperations.ZeroMemory(Certificate);
        Trust?.Dispose();
    }
}

internal static class StoreFormat
{
    internal const int MaximumBytes = 262144;
    private static readonly byte[] Magic = "MARTLET1"u8.ToArray();
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    internal static byte[] Wrap(byte[] ciphertext)
    {
        if (ciphertext.Length > MaximumBytes - 16)
            throw Error(GatewayPersistenceFailure.InvalidState);
        var bytes = new byte[ciphertext.Length + 16];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), ciphertext.Length);
        ciphertext.CopyTo(bytes, 16);
        return bytes;
    }

    internal static byte[] Unwrap(byte[] bytes)
    {
        if (bytes.Length is <= 16 or > MaximumBytes || !bytes.AsSpan(0, 8).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) != bytes.Length - 16)
            throw Error(GatewayPersistenceFailure.InvalidState);
        return bytes.AsSpan(16).ToArray();
    }

    internal static byte[] Write(StoreDocument document)
    {
        Validate(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (bytes.Length > MaximumBytes - 4096)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw Error(GatewayPersistenceFailure.InvalidState);
        }
        return bytes;
    }

    internal static StoreDocument Read(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes - 4096)
            throw Error(GatewayPersistenceFailure.InvalidState);
        StoreDocument? result = null;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            Inspect(json.RootElement);
            result = json.Deserialize<StoreDocument>(Options) ?? throw Error(GatewayPersistenceFailure.InvalidState);
            Validate(result);
            return result;
        }
        catch (JsonException)
        {
            result?.Dispose();
            throw Error(GatewayPersistenceFailure.InvalidState);
        }
        catch (InvalidOperationException)
        {
            result?.Dispose();
            throw Error(GatewayPersistenceFailure.InvalidState);
        }
        catch
        {
            result?.Dispose();
            throw;
        }
    }

    private static void Inspect(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Error(GatewayPersistenceFailure.InvalidState);
                Inspect(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > GatewayTrustLimits.MaximumDevices)
                throw Error(GatewayPersistenceFailure.InvalidState);
            foreach (var item in element.EnumerateArray())
                Inspect(item);
        }
    }

    private static void Validate(StoreDocument document)
    {
        Require(document.Version == 1 && document.HostId != Guid.Empty && document.Generation != Guid.Empty &&
            document.Revision >= 1 && document.Certificate is { Length: > 0 and <= 16384 } &&
            document.Trust is not null);
        var trust = document.Trust;
        Require(trust.ObservedAt.Offset == TimeSpan.Zero && trust.ObservedAt > DateTimeOffset.MinValue &&
            trust.Devices is { Length: <= GatewayTrustLimits.MaximumDevices });
        var ids = new HashSet<Guid>();
        foreach (var device in trust.Devices)
        {
            Require(device is not null && device.DeviceId != Guid.Empty && ids.Add(device.DeviceId) &&
                device.Scopes != GatewayScope.None && ((int)device.Scopes & ~127) == 0 &&
                device.Current is not null && (device.Previous is null) == (device.Overlap is null));
            Validate(device.Current, trust.ObservedAt);
            if (device.Previous is { } previous)
            {
                Validate(previous, trust.ObservedAt);
                Validate(device.Overlap!, trust.ObservedAt, GatewayTrustLimits.RotationOverlap);
            }
        }
    }

    private static void Validate(StoredCredential credential, DateTimeOffset observed)
    {
        Require(credential.Verifier is { Length: GatewaySecret.ByteLength } && credential.Life is not null);
        Validate(credential.Life, observed, GatewayTrustLimits.CredentialLifetime);
    }

    private static void Validate(StoredLifetime life, DateTimeOffset observed, TimeSpan maximum)
    {
        Require(life.ExpiresAt.Offset == TimeSpan.Zero && life.RemainingTicks >= 0 &&
            life.RemainingTicks <= maximum.Ticks && life.ExpiresAt >= observed &&
            life.ExpiresAt - observed <= maximum && life.RemainingTicks <= (life.ExpiresAt - observed).Ticks);
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid)
    {
        if (!valid)
            throw Error(GatewayPersistenceFailure.InvalidState);
    }
}
