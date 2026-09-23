using System.Runtime.Versioning;
using System.Security.Cryptography;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal enum StoreStep { FenceFlushed, PendingFlushed, Replaced, CheckpointCommitted, BeforeCleanFenceRemoval, CleanFenceRemoved, StagingFlushed }

// Native mechanics adapted from PR #37, 18daa4acfa90d7c7dff52861c93800a2f63b0823.
// The v2 predecessor-bound redo protocol is deliberately not that PR's reset-on-crash policy.
[SupportedOSPlatform("windows")]
internal sealed class WindowsAuthorityStore : IGatewayPersistence, IDisposable
{
    private readonly WindowsOwnedDirectory directory;
    private readonly Action<StoreStep>? fault;
    private readonly Guid generation;
    private readonly Guid bootId;
    private long revision;
    private byte[] certificate;
    private byte[]? committedHash;
    private bool poisoned;
    internal string HostId { get; }
    internal GatewayCheckpoint Initial { get; }
    internal bool InitialSameBoot { get; }

    private WindowsAuthorityStore(WindowsOwnedDirectory directory, StoreDocument document, byte[]? hash,
        Guid currentBoot, Action<StoreStep>? fault)
    {
        this.directory = directory;
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

    internal static WindowsAuthorityStore Create(string path, string hostId, byte[] certificate,
        DateTimeOffset now, Action<StoreStep>? fault, TimeProvider? clock = null)
    {
        var directory = WindowsOwnedDirectory.Open(path, create: true);
        WindowsAuthorityStore? store = null;
        try
        {
            var source = clock ?? TimeProvider.System;
            var boot = WindowsBootIdentity.Read();
            store = new(directory, StoreFormat.Document(hostId, Guid.NewGuid(), 0, certificate,
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

    internal static WindowsAuthorityStore Open(string path, DateTimeOffset now,
        Action<StoreStep>? fault, Guid? bootIdentity = null)
    {
        var directory = WindowsOwnedDirectory.Open(path, create: false);
        WindowsAuthorityStore? store = null;
        StoreDocument? document = null;
        StoreDocument? pending = null;
        try
        {
            directory.ValidateFiles();
            byte[]? committed = null;
            if (File.Exists(directory.FilePath("authority.bin")))
            {
                committed = directory.Read("authority.bin", StoreFormat.MaximumBytes);
                document = Decode(committed);
            }
            if (File.Exists(directory.FilePath("pending.bin")))
            {
                if (File.Exists(directory.FilePath("staging.bin")))
                    throw Error(GatewayPersistenceFailure.RecoveryRequired);
                var proposed = directory.Read("pending.bin", StoreFormat.MaximumBytes);
                pending = Decode(proposed);
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
                Promote(directory, committed is not null);
                document?.Dispose();
                document = pending;
                pending = null;
                committed = proposed;
            }
            if (document is null || committed is null)
                throw Error(GatewayPersistenceFailure.StoreMissing);
            using var validated = HostCertificate.Load(document.Certificate);
            if (now < document.State.ObservedAt)
                throw Error(GatewayPersistenceFailure.ClockUnavailable);
            // Staging has never crossed the prepared-transaction boundary; it cannot have authorized a result.
            if (File.Exists(directory.FilePath("staging.bin")))
            {
                directory.Validate("staging.bin");
                File.Delete(directory.FilePath("staging.bin"));
            }
            store = new(directory, document, SHA256.HashData(committed),
                bootIdentity ?? WindowsBootIdentity.Read(), fault);
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
    }

    private static StoreDocument Decode(byte[] envelope)
    {
        var cipher = StoreFormat.Unwrap(envelope);
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

    internal byte[] CopyCertificate() => certificate.ToArray();

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
        if (!File.Exists(directory.FilePath("running")))
            directory.WriteNew("running", generation.ToByteArray());
        fault?.Invoke(StoreStep.FenceFlushed);
    }

    public void Commit(GatewayCheckpoint checkpoint)
    {
        if (poisoned)
            throw Error(GatewayPersistenceFailure.RecoveryRequired);
        byte[]? clear = null;
        try
        {
            directory.ValidateFiles();
            if (!File.Exists(directory.FilePath("running")))
                throw Error(GatewayPersistenceFailure.RecoveryRequired);
            if (committedHash is not null)
            {
                var existing = directory.Read("authority.bin", StoreFormat.MaximumBytes);
                if (!CryptographicOperations.FixedTimeEquals(committedHash, SHA256.HashData(existing)))
                    throw Error(GatewayPersistenceFailure.RecoveryRequired);
            }
            var next = checked(revision + 1);
            clear = StoreFormat.Write(StoreFormat.Document(HostId, generation, next, certificate, checkpoint,
                bootId, committedHash ?? []));
            var cipher = WindowsProtection.Protect(clear);
            byte[] wrapped;
            try { wrapped = StoreFormat.Wrap(cipher); }
            finally { CryptographicOperations.ZeroMemory(cipher); }
            directory.WriteNew("staging.bin", wrapped);
            fault?.Invoke(StoreStep.StagingFlushed);
            File.Move(directory.FilePath("staging.bin"), directory.FilePath("pending.bin"), overwrite: false);
            using (var prepared = new FileStream(directory.FilePath("pending.bin"), FileMode.Open,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                prepared.Flush(flushToDisk: true);
            fault?.Invoke(StoreStep.PendingFlushed);
            Promote(directory, committedHash is not null, () => fault?.Invoke(StoreStep.Replaced));
            committedHash = SHA256.HashData(wrapped);
            revision = next;
            fault?.Invoke(StoreStep.CheckpointCommitted);
        }
        catch (IOException)
        {
            poisoned = true;
            throw Error(GatewayPersistenceFailure.StorageFailed);
        }
        catch (UnauthorizedAccessException)
        {
            poisoned = true;
            throw Error(GatewayPersistenceFailure.InsecureStorage);
        }
        catch (OverflowException)
        {
            poisoned = true;
            throw Error(GatewayPersistenceFailure.InvalidState);
        }
        catch
        {
            poisoned = true;
            throw;
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static void Promote(WindowsOwnedDirectory directory, bool replace, Action? replaced = null)
    {
        if (replace)
            File.Replace(directory.FilePath("pending.bin"), directory.FilePath("authority.bin"), null);
        else
            File.Move(directory.FilePath("pending.bin"), directory.FilePath("authority.bin"), overwrite: false);
        replaced?.Invoke();
        directory.Validate("authority.bin");
        using var file = new FileStream(directory.FilePath("authority.bin"), FileMode.Open,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Flush(flushToDisk: true);
    }

    public void Complete(GatewayCheckpoint checkpoint, Action validateBeforeClean)
    {
        Commit(checkpoint);
        try
        {
            fault?.Invoke(StoreStep.BeforeCleanFenceRemoval);
            directory.Validate("running");
            validateBeforeClean();
            File.Delete(directory.FilePath("running"));
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
