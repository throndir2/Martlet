using System.Security.Cryptography;

namespace Martlet.Memory;

public enum MemoryExportDecision
{
    No,
    Export
}

public sealed class MemoryExportAuthorization
{
    internal MemoryExportAuthorization(Guid previewId, Guid storeId, long storeRevision, string digest, string destination)
    {
        PreviewId = previewId;
        StoreId = storeId;
        StoreRevision = storeRevision;
        Digest = digest;
        Destination = destination;
    }

    internal Guid PreviewId { get; }
    internal Guid StoreId { get; }
    internal long StoreRevision { get; }
    internal string Digest { get; }
    internal string Destination { get; }
    internal int Used;

    public override string ToString() => "Explicit local memory export approval (destination and content omitted)";
}

public sealed record MemoryExportReceipt(Guid PreviewId, long StoreRevision, string Sha256, long Bytes)
{
    public override string ToString() =>
        $"MemoryExportReceipt {{ PreviewId = {PreviewId}, StoreRevision = {StoreRevision}, Sha256 = {Sha256}, Bytes = {Bytes} }}";
}

public sealed class MemoryExportPreview : IDisposable
{
    private readonly object gate = new();
    private byte[]? bytes;

    internal MemoryExportPreview(Guid id, Guid storeId, long storeRevision, DateTimeOffset frozenAtUtc,
        int factCount, byte[] bytes)
    {
        Id = id;
        StoreId = storeId;
        StoreRevision = storeRevision;
        FrozenAtUtc = frozenAtUtc;
        FactCount = factCount;
        this.bytes = bytes;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public Guid Id { get; }
    internal Guid StoreId { get; }
    public long StoreRevision { get; }
    public DateTimeOffset FrozenAtUtc { get; }
    public int FactCount { get; }
    public int Bytes
    {
        get
        {
            lock (gate)
            {
                MemoryGuard.Require(bytes is not null, MemoryFailure.Closed);
                return bytes!.Length;
            }
        }
    }
    public string Sha256 { get; }
    public bool ExportApprovedByDefault => false;

    public byte[] Preview()
    {
        lock (gate)
        {
            MemoryGuard.Require(bytes is not null, MemoryFailure.Closed);
            return bytes!.ToArray();
        }
    }

    public MemoryExportAuthorization Authorize(string absoluteDestination,
        MemoryExportDecision decision = MemoryExportDecision.No)
    {
        lock (gate)
        {
            MemoryGuard.Require(bytes is not null, MemoryFailure.Closed);
            MemoryGuard.Defined(decision);
            MemoryGuard.Require(decision == MemoryExportDecision.Export, MemoryFailure.ConsentRequired);
            var destination = MemoryPaths.ExportDestination(absoluteDestination);
            MemoryGuard.Require(!File.Exists(destination) && !Directory.Exists(destination),
                MemoryFailure.DestinationExists);
            return new(Id, StoreId, StoreRevision, Sha256, destination);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (bytes is null)
                return;
            CryptographicOperations.ZeroMemory(bytes);
            bytes = null;
        }
    }

    public override string ToString() =>
        $"MemoryExportPreview {{ Id = {Id}, StoreRevision = {StoreRevision}, FactCount = {FactCount}, Content = [redacted] }}";
}
