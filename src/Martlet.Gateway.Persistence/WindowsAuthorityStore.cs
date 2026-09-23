using System.Runtime.Versioning;
using System.Security.Cryptography;
using Martlet.Gateway.Trust;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal enum StoreStep { FenceFlushed, PendingFlushed, Replaced, CheckpointCommitted, BeforeCleanFenceRemoval }

[SupportedOSPlatform("windows")]
internal sealed class WindowsAuthorityStore : IDurableTrust, IDisposable
{
    private readonly WindowsOwnedDirectory directory;
    private readonly Action<StoreStep>? fault;
    private readonly Guid hostId;
    private Guid generation;
    private long revision;
    private byte[] certificate;
    private byte[]? committedHash;
    private bool poisoned;
    internal TrustCheckpoint Initial { get; }

    private WindowsAuthorityStore(WindowsOwnedDirectory directory, StoreDocument document, byte[]? hash,
        Action<StoreStep>? fault)
    {
        this.directory = directory;
        this.fault = fault;
        hostId = document.HostId;
        generation = document.Generation;
        revision = document.Revision;
        certificate = document.Certificate.ToArray();
        Initial = document.Trust;
        committedHash = hash;
    }

    internal static WindowsAuthorityStore Create(string path, Guid hostId, byte[] certificate,
        DateTimeOffset now, Action<StoreStep>? fault)
    {
        var directory = WindowsOwnedDirectory.Open(path, create: true);
        WindowsAuthorityStore? store = null;
        try
        {
            store = new(directory, new(1, hostId, Guid.NewGuid(), 0, certificate, new(now, [])), null, fault);
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
        Action<StoreStep>? fault)
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
            using var validated = HostCertificate.Load(document.Certificate, document.HostId);
            store = new(directory, document, SHA256.HashData(protectedBytes), fault);
            CryptographicOperations.ZeroMemory(document.Certificate);
            document = null;
            store.Fence();
            if (resetDevices)
            {
                if (now < store.Initial.ObservedAt)
                    throw Error(GatewayPersistenceFailure.RecoveryRequired);
                if (File.Exists(directory.FilePath("pending.bin")))
                {
                    directory.Validate("pending.bin");
                    File.Delete(directory.FilePath("pending.bin"));
                }
                store.Initial.Dispose();
                store.generation = Guid.NewGuid();
                using var empty = new TrustCheckpoint(now, []);
                store.Commit(empty);
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

    internal Guid HostId => hostId;
    internal byte[] CopyCertificate() => certificate.ToArray();

    internal void ReplaceCertificate(byte[] replacement, TrustCheckpoint checkpoint)
    {
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

    public void Commit(TrustCheckpoint checkpoint)
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
            clear = StoreFormat.Write(new(1, hostId, generation, next, certificate, checkpoint));
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

    public void Complete(TrustCheckpoint checkpoint)
    {
        Commit(checkpoint);
        try
        {
            fault?.Invoke(StoreStep.BeforeCleanFenceRemoval);
            directory.Validate("running");
            File.Delete(directory.FilePath("running"));
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
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(certificate);
        Initial.Dispose();
        directory.Dispose();
    }
}
