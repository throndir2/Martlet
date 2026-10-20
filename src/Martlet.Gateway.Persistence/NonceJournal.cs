using System.Buffers.Binary;
using System.Security.Cryptography;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

/// <summary>The nonce journal (<c>nonces.log</c>): request nonces appended with one flush each, so a signed request does
/// not wait for a full authority save. A full save holds every live nonce and starts a new, empty journal. It is not
/// encrypted: nonces and credential IDs travel in clear request headers. Each record has a checksum bound to the store's
/// generation, which detects damage (a torn write), not tampering by a writer of the store directory.</summary>
internal static class NonceJournal
{
    internal const string Name = "nonces.log";
    internal const int HeaderBytes = 32;
    internal const int RecordBytes = 64;
    internal const int MaximumRecords = 8192;
    internal const int MaximumBytes = HeaderBytes + RecordBytes * MaximumRecords;
    private static ReadOnlySpan<byte> Magic => "MRTLNJ01"u8;

    internal readonly record struct Contents(StoredGatewayNonceRecord[] Records, int Length, bool Damaged)
    {
        /// <summary>True when new records can follow: a complete header and only complete, valid records.</summary>
        internal bool Appendable => !Damaged && Length >= HeaderBytes;
    }

    internal static byte[] Header(Guid generation)
    {
        var bytes = new byte[HeaderBytes];
        Magic.CopyTo(bytes);
        generation.TryWriteBytes(bytes.AsSpan(8));
        SHA256.HashData(bytes.AsSpan(0, 24)).AsSpan(0, 8).CopyTo(bytes.AsSpan(24));
        return bytes;
    }

    internal static byte[] Records(Guid generation, IReadOnlyList<StoredGatewayNonceRecord> nonces)
    {
        var bytes = new byte[checked(nonces.Count * RecordBytes)];
        Span<byte> hashed = stackalloc byte[16 + 56];
        generation.TryWriteBytes(hashed);
        for (var i = 0; i < nonces.Count; i++)
        {
            var record = bytes.AsSpan(i * RecordBytes, RecordBytes);
            var nonce = nonces[i];
            if (!Base64Url.TryDecode(nonce.CredentialId, 16, out var credential) ||
                !Base64Url.TryDecode(nonce.Nonce, 24, out var value) || nonce.ExpiresAt.Offset != TimeSpan.Zero ||
                nonce.ObservedAt.Offset != TimeSpan.Zero)
                throw Error(GatewayPersistenceFailure.InvalidState);
            credential.CopyTo(record);
            value.CopyTo(record[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(record[40..], nonce.ExpiresAt.UtcTicks);
            BinaryPrimitives.WriteInt64LittleEndian(record[48..], nonce.ObservedAt.UtcTicks);
            record[..56].CopyTo(hashed[16..]);
            SHA256.HashData(hashed).AsSpan(0, 8).CopyTo(record[56..]);
        }
        return bytes;
    }

    /// <summary>Reads a journal. A short header is a journal whose creation was interrupted: no request could append to it,
    /// so it holds nothing. A damaged header or record, or a partial last record, may hide nonces of admitted requests, so
    /// it is reported as damaged and the store refuses requests signed before it opened.</summary>
    internal static Contents Read(byte[] bytes, Guid generation)
    {
        if (bytes.Length < HeaderBytes)
            return new([], bytes.Length, false);
        var header = Header(generation);
        if (!bytes.AsSpan(0, 8).SequenceEqual(Magic) ||
            !SHA256.HashData(bytes.AsSpan(0, 24)).AsSpan(0, 8).SequenceEqual(bytes.AsSpan(24, 8)))
            return new([], bytes.Length, true);
        if (!bytes.AsSpan(0, HeaderBytes).SequenceEqual(header))
            throw Error(GatewayPersistenceFailure.RecoveryRequired);
        var records = new List<StoredGatewayNonceRecord>();
        Span<byte> hashed = stackalloc byte[16 + 56];
        generation.TryWriteBytes(hashed);
        var offset = HeaderBytes;
        for (; offset + RecordBytes <= bytes.Length; offset += RecordBytes)
        {
            var record = bytes.AsSpan(offset, RecordBytes);
            record[..56].CopyTo(hashed[16..]);
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
            var observed = BinaryPrimitives.ReadInt64LittleEndian(record[48..]);
            if (!SHA256.HashData(hashed).AsSpan(0, 8).SequenceEqual(record[56..]) ||
                ticks <= DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks ||
                observed <= DateTimeOffset.MinValue.UtcTicks || observed > (DateTimeOffset.MaxValue - TimeSpan.FromDays(90)).UtcTicks)
                return new([.. records], bytes.Length, true);
            records.Add(new(Base64Url.Encode(record[..16].ToArray()), Base64Url.Encode(record[16..40].ToArray()),
                new DateTimeOffset(ticks, TimeSpan.Zero), new DateTimeOffset(observed, TimeSpan.Zero)));
        }
        return new([.. records], bytes.Length, offset != bytes.Length);
    }

    /// <summary>Adds the journal's nonces to the saved state. A record for a credential the state no longer has (revoked
    /// later) is dropped. A record the clock rules cannot have made marks the journal damaged.</summary>
    internal static GatewayCheckpoint Merge(GatewayCheckpoint state, StoredGatewayNonceRecord[] records,
        DateTimeOffset now, ref bool damaged)
    {
        if (records.Length == 0)
            return state;
        var latest = now + GatewayCredentialStore.RequestClockSkew + GatewayCredentialStore.RequestClockSkew +
            GatewayCredentialStore.MaximumClockStepBack + TimeSpan.FromTicks(1);
        var byCredential = state.Credentials.ToDictionary(credential => credential.CredentialId,
            credential => credential.Nonces.ToDictionary(nonce => nonce.Nonce, nonce => nonce.ExpiresAt, StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.ExpiresAt > latest || record.ExpiresAt - record.ObservedAt > GatewayCredentialStore.RequestClockSkew +
                GatewayCredentialStore.RequestClockSkew + TimeSpan.FromTicks(1))
            {
                damaged = true;
                continue;
            }
            if (record.ExpiresAt > now && byCredential.TryGetValue(record.CredentialId, out var nonces))
                nonces[record.Nonce] = nonces.TryGetValue(record.Nonce, out var known) && known > record.ExpiresAt
                    ? known : record.ExpiresAt;
        }
        var credentials = new StoredGatewayCredential[state.Credentials.Length];
        for (var i = 0; i < credentials.Length; i++)
        {
            var credential = state.Credentials[i];
            var nonces = byCredential[credential.CredentialId].Where(item => item.Value > now)
                .OrderByDescending(item => item.Value).ToArray();
            if (nonces.Length > GatewayCredentialStore.MaximumNoncesPerCredential)
            {
                damaged = true;
                nonces = nonces[..GatewayCredentialStore.MaximumNoncesPerCredential];
            }
            credentials[i] = credential with { Nonces = nonces.Select(item => new StoredGatewayNonce(item.Key, item.Value)).ToArray() };
        }
        return state with
        {
            Credentials = credentials,
            JournalObservedAt = records.Where(record => record.ExpiresAt <= latest).Select(record => (DateTimeOffset?)record.ObservedAt).Max()
        };
    }
}
