using System.Runtime.Versioning;
using System.Security.Cryptography;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal enum StoreStep { FenceFlushed, PendingFlushed, Replaced, CheckpointCommitted, BeforeCleanFenceRemoval, CleanFenceRemoved }

// Atomic storage mechanics adapted from PR #37, 18daa4acfa90d7c7dff52861c93800a2f63b0823.
[SupportedOSPlatform("windows")]
internal sealed class WindowsAuthorityStore : IGatewayPersistence, IDisposable
{
    private readonly WindowsOwnedDirectory directory;
    private readonly Action<StoreStep>? fault;
    private Guid generation;
    private Guid bootId;
    private long revision;
    private byte[] certificate;
    private byte[]? committedHash;
    private bool poisoned;
    internal string HostId { get; private set; }
    internal GatewayCheckpoint Initial { get; private set; }

    private WindowsAuthorityStore(WindowsOwnedDirectory directory, StoreDocument document, byte[]? hash,
        Action<StoreStep>? fault)
    {
        this.directory = directory;
        this.fault = fault;
        HostId = document.HostId;
        generation = document.Generation;
        bootId = document.BootId;
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
            store = new(directory, StoreFormat.Document(hostId, Guid.NewGuid(), 0, certificate,
                new(now, [], source.GetTimestamp(), source.TimestampFrequency), WindowsBootIdentity.Read()), null, fault);
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

    internal static WindowsAuthorityStore Open(string path, bool resetDevices, DateTimeOffset now,
        Action<StoreStep>? fault, TimeProvider? clock = null, Guid? bootIdentity = null)
    {
        var directory = WindowsOwnedDirectory.Open(path, create: false);
        WindowsAuthorityStore? store = null;
        StoreDocument? document = null;
        try
        {
            directory.ValidateFiles();
            if (!resetDevices && (File.Exists(directory.FilePath("running")) || File.Exists(directory.FilePath("pending.bin"))))
                throw Error(GatewayPersistenceFailure.RecoveryRequired);
            if (!File.Exists(directory.FilePath("authority.bin")))
                throw Error(GatewayPersistenceFailure.StoreMissing);
            var protectedBytes = directory.Read("authority.bin", StoreFormat.MaximumBytes);
            var cipher = StoreFormat.Unwrap(protectedBytes);
            byte[]? clear = null;
            try
            {
                clear = WindowsProtection.Unprotect(cipher);
                document = StoreFormat.Read(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cipher);
                if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            }
            using var validated = HostCertificate.Load(document.Certificate);
            if (now < document.State.ObservedAt)
                throw Error(GatewayPersistenceFailure.RecoveryRequired);
            var currentBoot = bootIdentity ?? WindowsBootIdentity.Read();
            if (!resetDevices && currentBoot != document.BootId)
                throw Error(GatewayPersistenceFailure.RecoveryRequired);
            store = new(directory, document, SHA256.HashData(protectedBytes), fault);
            CryptographicOperations.ZeroMemory(document.Certificate);
            document = null;
            store.Fence();
            if (resetDevices)
            {
                if (File.Exists(directory.FilePath("pending.bin")))
                {
                    directory.Validate("pending.bin");
                    File.Delete(directory.FilePath("pending.bin"));
                }
                store.Initial.Dispose();
                var source = clock ?? TimeProvider.System;
                store.Initial = new(now, [], source.GetTimestamp(), source.TimestampFrequency);
                store.bootId = currentBoot;
                store.generation = Guid.NewGuid();
                store.Commit(store.Initial);
            }
            return store;
        }
        catch
        {
            document?.Dispose();
            if (store is not null) store.Dispose();
            else directory.Dispose();
            throw;
        }
    }

    internal byte[] CopyCertificate() => certificate.ToArray();

    internal void ReplaceCertificate(byte[] replacement, string? newHostId = null)
    {
        CryptographicOperations.ZeroMemory(certificate);
        certificate = replacement.ToArray();
        if (newHostId is not null)
        {
            HostId = newHostId;
            generation = Guid.NewGuid();
        }
        Commit(Initial);
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
            clear = StoreFormat.Write(StoreFormat.Document(HostId, generation, next, certificate, checkpoint, bootId));
            var cipher = WindowsProtection.Protect(clear);
            byte[] wrapped;
            try { wrapped = StoreFormat.Wrap(cipher); }
            finally { CryptographicOperations.ZeroMemory(cipher); }
            directory.WriteNew("pending.bin", wrapped);
            fault?.Invoke(StoreStep.PendingFlushed);
            if (committedHash is null)
                File.Move(directory.FilePath("pending.bin"), directory.FilePath("authority.bin"), overwrite: false);
            else
                File.Replace(directory.FilePath("pending.bin"), directory.FilePath("authority.bin"), null);
            fault?.Invoke(StoreStep.Replaced);
            directory.Validate("authority.bin");
            using (var file = new FileStream(directory.FilePath("authority.bin"), FileMode.Open,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                file.Flush(flushToDisk: true);
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
