using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal sealed record StoreDocument(
    int Version, string Protocol, string Algorithm, string HostId, Guid Generation,
    long Revision, byte[] Certificate, GatewayCheckpoint State, Guid BootId, byte[] PredecessorHash) : IDisposable
{
    public void Dispose()
    {
        if (Certificate is not null)
            CryptographicOperations.ZeroMemory(Certificate);
        State?.Dispose();
    }
}

internal static class StoreFormat
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal const string Protocol = "martlet-paired-v2";
    internal const string Algorithm = "hmac-sha256-key-sha256";
    private static readonly byte[] Magic = "MRTLHM02"u8.ToArray();
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters =
        {
            new JsonStringEnumConverter<GatewayRole>(JsonNamingPolicy.SnakeCaseLower, false),
            new JsonStringEnumConverter<GatewayAccess>(JsonNamingPolicy.SnakeCaseLower, false)
        }
    };

    internal static StoreDocument Document(string hostId, Guid generation, long revision,
        byte[] certificate, GatewayCheckpoint state, Guid bootId, byte[] predecessorHash) =>
        new(2, Protocol, Algorithm, hostId, generation, revision, certificate, state, bootId, predecessorHash);

    internal static byte[] Wrap(byte[] ciphertext)
    {
        Require(ciphertext.Length is > 0 and <= MaximumBytes - 16);
        var bytes = new byte[ciphertext.Length + 16];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), ciphertext.Length);
        ciphertext.CopyTo(bytes, 16);
        return bytes;
    }

    internal static byte[] Unwrap(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(LinuxPermissionEnvelope.Magic))
            throw Error(GatewayPersistenceFailure.StorageBackendMismatch);
        if (bytes.Length >= 8 && (bytes.AsSpan(0, 8).SequenceEqual("MRTLHM01"u8) ||
            bytes.AsSpan(0, 8).SequenceEqual("MARTLET1"u8)))
            throw Error(GatewayPersistenceFailure.MigrationRequired);
        Require(bytes.Length is > 16 and <= MaximumBytes &&
            bytes.AsSpan(0, 8).SequenceEqual(Magic) &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) == 2 &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) == bytes.Length - 16);
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
        Require(bytes.Length is > 0 and <= MaximumBytes - 4096);
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
                Require(names.Add(property.Name));
                Inspect(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            Require(element.GetArrayLength() <= GatewayCredentialStore.MaximumNoncesPerCredential);
            foreach (var item in element.EnumerateArray())
                Inspect(item);
        }
    }

    private static void Validate(StoreDocument document)
    {
        try
        {
            Require(document.Version == 2 && document.Protocol == Protocol && document.Algorithm == Algorithm &&
                document.Generation != Guid.Empty && document.Revision >= 1 &&
                document.PredecessorHash is not null &&
                document.PredecessorHash.Length == (document.Revision == 1 ? 0 : 32) &&
                document.BootId != Guid.Empty &&
                document.Certificate is { Length: > 0 and <= 16_384 } && document.State is not null);
            GatewayRules.Identifier(document.HostId);
            var state = document.State;
            Require(state.ObservedAt.Offset == TimeSpan.Zero &&
                state.ObservedAt > DateTimeOffset.MinValue &&
                state.ObservedAt <= DateTimeOffset.MaxValue - TimeSpan.FromDays(90) &&
                state.Timestamp >= 0 && state.Frequency > 0 &&
                state.Credentials is { Length: <= GatewayCredentialStore.MaximumRegistrations });
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var credential in state.Credentials)
            {
                Require(credential is not null &&
                    Base64Url.TryDecode(credential.CredentialId, 16, out _) &&
                    ids.Add(credential.CredentialId) &&
                    credential.SigningKey is { Length: 32 } &&
                    credential.Roles is { Length: > 0 and <= 3 } &&
                    credential.Roles.Distinct().Count() == credential.Roles.Length &&
                    credential.Access is null or GatewayAccess.Friend &&
                    credential.Nonces is { Length: <= GatewayCredentialStore.MaximumNoncesPerCredential });
                GatewayRules.Identifier(credential.DeviceId);
                GatewayRules.Token(credential.DisplayName, 64);
                foreach (var role in credential.Roles)
                    GatewayRules.Defined(role);
                Require(credential.IssuedAt.Offset == TimeSpan.Zero &&
                    credential.IssuedAt > DateTimeOffset.MinValue &&
                    credential.IssuedAt <= state.ObservedAt &&
                    (credential.RotatedToCredentialId is null ||
                        Base64Url.TryDecode(credential.RotatedToCredentialId, 16, out _) &&
                        credential.RotatedToCredentialId != credential.CredentialId));
                switch (credential.Lifetime)
                {
                    case PairedDeviceLifetime:
                        Require(credential.RemainingTicks == 0 && credential.RotatedToCredentialId is null);
                        break;
                    case RetiringCredentialLifetime retiring:
                        Require(credential.RotatedToCredentialId is not null &&
                            retiring.ExpiresAt.Offset == TimeSpan.Zero && retiring.ExpiresAt > state.ObservedAt &&
                            retiring.ExpiresAt - state.ObservedAt <= GatewayCredentialStore.MaximumRotationOverlap &&
                            credential.RemainingTicks > 0 &&
                            credential.RemainingTicks <= (retiring.ExpiresAt - state.ObservedAt).Ticks);
                        break;
                    default:
                        throw Error(GatewayPersistenceFailure.InvalidState);
                }
                var nonces = new HashSet<string>(StringComparer.Ordinal);
                foreach (var nonce in credential.Nonces)
                    Require(nonce is not null && Base64Url.TryDecode(nonce.Nonce, 24, out _) &&
                        nonces.Add(nonce.Nonce) && nonce.ExpiresAt.Offset == TimeSpan.Zero &&
                        nonce.ExpiresAt > state.ObservedAt &&
                        nonce.ExpiresAt <= state.ObservedAt + GatewayCredentialStore.RequestClockSkew +
                            GatewayCredentialStore.RequestClockSkew + TimeSpan.FromTicks(1));
            }
        }
        catch (GatewayProtocolException)
        {
            throw Error(GatewayPersistenceFailure.InvalidState);
        }
    }

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition)
            throw Error(GatewayPersistenceFailure.InvalidState);
    }
}
