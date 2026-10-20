using System.Security.Cryptography;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal enum StoreStep { FenceFlushed, PendingFlushed, Replaced, CheckpointCommitted, BeforeCleanFenceRemoval, CleanFenceRemoved, StagingFlushed,
    NonceAppending, NonceWritten, NonceAppended }

// Native mechanics adapted from PR #37, 18daa4acfa90d7c7dff52861c93800a2f63b0823.
// The v2 predecessor-bound redo protocol is deliberately not that PR's reset-on-crash policy.
internal sealed class AuthorityStore : IGatewayPersistence, IDisposable
{
    private readonly IOwnedAuthorityDirectory directory;
    private readonly IAuthorityEnvelope envelope;
    private readonly Action<StoreStep>? fault;
    private readonly Guid generation;
    private readonly Guid bootId;
    private long revision;
    private byte[] certificate;
    private byte[]? committedHash;
    private bool poisoned;
    // The nonce journal's length, or -1 when it cannot take appends until the next full save starts a new one.
    private long journalLength = -1;
    internal string HostId { get; }
    internal GatewayCheckpoint Initial { get; }
    internal bool InitialSameBoot { get; }

    private AuthorityStore(IOwnedAuthorityDirectory directory, IAuthorityEnvelope envelope,
        StoreDocument document, byte[]? hash, Guid currentBoot, Action<StoreStep>? fault)
    {
        this.directory = directory;
        this.envelope = envelope;
        this.fault = fault;
        HostId = document.HostId;
        generation = document.Generation;
        bootId = currentBoot;
        InitialSameBoot = currentBoot == document.BootId;
        revision = document.Revision;
        certificate = document.Certificate.ToArray();
        Initial = document.State;
        committedHash = hash;
    }

    internal static AuthorityStore Create(IOwnedAuthorityDirectory directory, IAuthorityEnvelope envelope,
        Guid boot, string hostId, byte[] certificate, DateTimeOffset now,
        Action<StoreStep>? fault, TimeProvider? clock = null)
    {
        AuthorityStore? store = null;
        try
        {
            var source = clock ?? TimeProvider.System;
            store = new(directory, envelope, StoreFormat.Document(hostId, Guid.NewGuid(), 0, certificate,
                new(now, [], source.GetTimestamp(), source.TimestampFrequency), boot, []), null, boot, fault);
            store.Fence();
            store.Commit(store.Initial);
            return store;
        }
        catch
        {
            if (store is not null) store.Dispose();
            else directory.Dispose();
            throw;
        }
    }

    internal static AuthorityStore Open(IOwnedAuthorityDirectory directory, IAuthorityEnvelope envelope,
        Guid bootIdentity, DateTimeOffset now, Action<StoreStep>? fault)
    {
        AuthorityStore? store = null;
        StoreDocument? document = null;
        StoreDocument? pending = null;
        byte[]? committed = null;
        byte[]? proposed = null;
        try
        {
            directory.ValidateFiles();
            if (directory.Exists("authority.bin"))
            {
                committed = directory.Read("authority.bin", StoreFormat.MaximumBytes);
                document = envelope.Decode(committed);
            }
            if (directory.Exists("pending.bin"))
            {
                if (directory.Exists("staging.bin"))
                    throw Error(GatewayPersistenceFailure.RecoveryRequired);
                proposed = directory.Read("pending.bin", StoreFormat.MaximumBytes);
                pending = envelope.Decode(proposed);
                var validSuccessor = document is null
                    ? pending.Revision == 1 && pending.PredecessorHash.Length == 0
                    : document.Revision < long.MaxValue && pending.Revision == document.Revision + 1 &&
                        pending.Generation == document.Generation && pending.HostId == document.HostId &&
                        pending.State.ObservedAt >= document.State.ObservedAt &&
                        CryptographicOperations.FixedTimeEquals(pending.PredecessorHash, SHA256.HashData(committed!));
                if (!validSuccessor)
                    throw Error(GatewayPersistenceFailure.RecoveryRequired);
                using var identity = HostCertificate.Load(pending.Certificate);
                if (document is not null)
                {
                    using var previous = HostCertificate.Load(document.Certificate);
                    if (new SystemGatewayCrypto().SpkiFingerprint(identity) !=
                        new SystemGatewayCrypto().SpkiFingerprint(previous))
                        throw Error(GatewayPersistenceFailure.RecoveryRequired);
                }
                directory.Promote(committed is not null);
                document?.Dispose();
                document = pending;
                pending = null;
                if (committed is not null) CryptographicOperations.ZeroMemory(committed);
                committed = proposed;
                proposed = null;
            }
            if (document is null || committed is null)
                throw Error(GatewayPersistenceFailure.StoreMissing);
            using var validated = HostCertificate.Load(document.Certificate);
            var journal = ReadJournal(directory, document, now);
            if (journal.Merged is { } merged)
                document = document with { State = merged };
            // Reopen within the same small step back the running store holds through; it resumes from the saved mark.
            var highWater = document.State.JournalObservedAt is { } seen && seen > document.State.ObservedAt
                ? seen : document.State.ObservedAt;
            if (highWater - now > GatewayCredentialStore.MaximumClockStepBack)
                throw Error(GatewayPersistenceFailure.ClockUnavailable);
            // Staging has never crossed the prepared-transaction boundary; it cannot have authorized a result.
            if (directory.Exists("staging.bin"))
            {
                directory.Validate("staging.bin");
                var staged = directory.Read("staging.bin", StoreFormat.MaximumBytes);
                StoreDocument? unused = null;
                try
                {
                    try { unused = envelope.Decode(staged); }
                    catch (GatewayPersistenceException error) when (error.Failure is
                        GatewayPersistenceFailure.InvalidState or GatewayPersistenceFailure.KeyProtectionFailed)
                    {
                        // Partial unprepared bytes cannot have authorized a result.
                    }
                    if (unused is not null &&
                        (unused.Generation != document.Generation || unused.HostId != document.HostId))
                        throw Error(GatewayPersistenceFailure.RecoveryRequired);
                }
                finally
                {
                    unused?.Dispose();
                    CryptographicOperations.ZeroMemory(staged);
                }
                directory.RemoveUnpreparedStage();
            }
            store = new(directory, envelope, document, SHA256.HashData(committed), bootIdentity, fault);
            store.journalLength = journal.Appendable;
            CryptographicOperations.ZeroMemory(document.Certificate);
            document = null;
            store.Fence();
            return store;
        }
        catch
        {
            document?.Dispose();
            pending?.Dispose();
            if (store is not null) store.Dispose();
            else directory.Dispose();
            throw;
        }
        finally
        {
            if (committed is not null) CryptographicOperations.ZeroMemory(committed);
            if (proposed is not null) CryptographicOperations.ZeroMemory(proposed);
        }
    }

    internal byte[] CopyCertificate() => certificate.ToArray();

    /// <summary>Reads the nonce journal and adds its nonces to the saved state. An appendable journal keeps taking appends;
    /// any other journal waits for the next full save to replace it. A damaged journal may hide nonces of admitted requests,
    /// so the state then refuses every request signed up to the latest time such a request could carry.</summary>
    private static (GatewayCheckpoint? Merged, long Appendable) ReadJournal(IOwnedAuthorityDirectory directory,
        StoreDocument document, DateTimeOffset now)
    {
        if (!directory.Exists(NonceJournal.Name))
            return (null, -1);
        var contents = NonceJournal.Read(directory.Read(NonceJournal.Name, NonceJournal.MaximumBytes), document.Generation);
        var damaged = contents.Damaged;
        var merged = NonceJournal.Merge(document.State, contents.Records, now, ref damaged);
        if (damaged)
        {
            var floor = now + GatewayCredentialStore.RequestClockSkew + GatewayCredentialStore.MaximumClockStepBack;
            if (merged.ReplayFloor is { } saved && saved > floor)
                floor = saved;
            merged = merged with { ReplayFloor = floor };
        }
        return (ReferenceEquals(merged, document.State) ? null : merged,
            contents.Appendable && !damaged ? contents.Length : -1);
    }

    internal void ReplaceCertificate(byte[] replacement, GatewayCheckpoint checkpoint)
    {
        using var old = HostCertificate.Load(certificate);
        using var next = HostCertificate.Load(replacement);
        if (new SystemGatewayCrypto().SpkiFingerprint(old) != new SystemGatewayCrypto().SpkiFingerprint(next))
            throw Error(GatewayPersistenceFailure.InvalidState);
        CryptographicOperations.ZeroMemory(certificate);
        certificate = replacement.ToArray();
        Commit(checkpoint);
    }

    private void Fence()
    {
        directory.ValidateFiles();
        if (!directory.Exists("running"))
            directory.WriteNew("running", generation.ToByteArray());
        fault?.Invoke(StoreStep.FenceFlushed);
    }

    public void Commit(GatewayCheckpoint checkpoint)
    {
        if (poisoned)
            throw Error(GatewayPersistenceFailure.RecoveryRequired);
        byte[]? wrapped = null;
        try
        {
            directory.ValidateFiles();
            if (!directory.Exists("running"))
                throw Error(GatewayPersistenceFailure.RecoveryRequired);
            if (committedHash is not null)
            {
                var existing = directory.Read("authority.bin", StoreFormat.MaximumBytes);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(committedHash, SHA256.HashData(existing)))
                        throw Error(GatewayPersistenceFailure.RecoveryRequired);
                }
                finally { CryptographicOperations.ZeroMemory(existing); }
            }
            var next = checked(revision + 1);
            wrapped = envelope.Encode(StoreFormat.Document(HostId, generation, next, certificate, checkpoint,
                bootId, committedHash ?? []));
            directory.WriteNew("staging.bin", wrapped);
            fault?.Invoke(StoreStep.StagingFlushed);
            directory.Prepare();
            fault?.Invoke(StoreStep.PendingFlushed);
            directory.Promote(committedHash is not null, () => fault?.Invoke(StoreStep.Replaced));
            committedHash = SHA256.HashData(wrapped);
            revision = next;
            // The saved state holds every live nonce, so a new, empty journal replaces the old one.
            journalLength = -1;
            if (directory.Exists(NonceJournal.Name))
                directory.RemoveJournal();
            directory.WriteNew(NonceJournal.Name, NonceJournal.Header(generation));
            journalLength = NonceJournal.HeaderBytes;
            fault?.Invoke(StoreStep.CheckpointCommitted);
        }
        catch (Exception error)
        {
            throw Poison(error);
        }
        finally
        {
            if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
        }
    }

    public bool AppendNonces(IReadOnlyList<StoredGatewayNonceRecord> nonces)
    {
        if (poisoned)
            throw Error(GatewayPersistenceFailure.RecoveryRequired);
        if (nonces.Count == 0)
            return true;
        if (journalLength < 0 || journalLength + (long)nonces.Count * NonceJournal.RecordBytes > NonceJournal.MaximumBytes)
            return false;
        try
        {
            var bytes = NonceJournal.Records(generation, nonces);
            fault?.Invoke(StoreStep.NonceAppending);
            directory.Append(NonceJournal.Name, journalLength, bytes, () => fault?.Invoke(StoreStep.NonceWritten));
            journalLength += bytes.Length;
            fault?.Invoke(StoreStep.NonceAppended);
            return true;
        }
        catch (Exception error)
        {
            throw Poison(error);
        }
    }

    private Exception Poison(Exception error)
    {
        poisoned = true;
        return error switch
        {
            IOException => Error(GatewayPersistenceFailure.StorageFailed),
            UnauthorizedAccessException => Error(GatewayPersistenceFailure.InsecureStorage),
            OverflowException => Error(GatewayPersistenceFailure.InvalidState),
            _ => error
        };
    }

    public void Complete(GatewayCheckpoint checkpoint, Action validateBeforeClean)
    {
        Commit(checkpoint);
        try
        {
            fault?.Invoke(StoreStep.BeforeCleanFenceRemoval);
            directory.Validate("running");
            validateBeforeClean();
            directory.RemoveRunning();
            fault?.Invoke(StoreStep.CleanFenceRemoved);
        }
        catch
        {
            poisoned = true;
            throw;
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(certificate);
        Initial.Dispose();
        directory.Dispose();
    }
}
