using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Diagnostics;

namespace Martlet.Support;

public sealed record PreviewFile(string Name, int Bytes, string Sha256, string Source);
public sealed record ExportReceipt(Guid SnapshotId, string Digest, long ZipBytes);

public sealed class ExportConsent
{
    internal readonly Guid SnapshotId;
    internal readonly string Digest;
    internal readonly string Destination;
    internal int Used;
    internal ExportConsent(Guid snapshotId, string digest, string destination)
    { SnapshotId = snapshotId; Digest = digest; Destination = destination; }
    public override string ToString() => "Explicit local support export approval (destination omitted)";
}

internal sealed record BundleManifest(int SchemaVersion, Guid SnapshotId, DateTimeOffset FrozenAtUtc,
    LogRange SelectedReceiptRange, DateTimeOffset? FirstIncludedReceiptUtc, DateTimeOffset? LastIncludedReceiptUtc,
    string IntegrityMeaning, string Omissions, PreviewFile[] Files);

public sealed class SupportSnapshot : IDisposable
{
    public const int MaximumFrozenBytes = 4 * 1024 * 1024;
    public const string OmissionSummary = "No raw settings, secrets, provider bodies, summaries supplied by callers, " +
        "transcripts, audio, screenshots, history, memory, voice/model IDs, device/host/user identities, paths, environment " +
        "or credential contents. Trace, turn and journal UUIDs use one fresh per-bundle pseudonym map. " +
        "Catalog guidance only. Timestamps and counts remain. Allowlisting reduces disclosure; it is not a universal privacy guarantee.";
    private readonly object gate = new();
    private readonly (PreviewFile Info, byte[] Bytes)[] files;
    private readonly TimeProvider clock;
    private bool disposed;
    private PendingExport? pending;
    private sealed class PendingExport(SupportFileSystem fs, string path, FileStream stream)
    {
        internal readonly SupportFileSystem FileSystem = fs;
        internal readonly string Path = path;
        internal FileStream? Stream = stream;
    }
    public Guid Id { get; }
    public string Digest { get; }
    public DateTimeOffset FrozenAtUtc { get; }
    public LogRange SelectedRange { get; }
    public IReadOnlyList<PreviewFile> Files { get; }
    public int TotalBytes { get; }
    public bool HasPendingCleanup => pending is not null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    private SupportSnapshot(Guid id, DateTimeOffset frozenAt, LogRange range,
        (PreviewFile Info, byte[] Bytes)[] files, TimeProvider clock)
    {
        Id = id; FrozenAtUtc = frozenAt; SelectedRange = range; this.files = files; this.clock = clock;
        Files = Array.AsReadOnly(files.Select(f => f.Info).ToArray());
        TotalBytes = files.Sum(f => f.Bytes.Length);
        Guard.Require(TotalBytes <= MaximumFrozenBytes, SupportFailure.LimitExceeded);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.ASCII.GetBytes($"{file.Info.Name}\0{file.Info.Bytes}\0"));
            hash.AppendData(file.Bytes);
        }
        Digest = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static SupportSnapshot Freeze(SettingsSummary settings, DoctorReport report, BuildMetadata build,
        JournalSelection logs, TimeProvider? clock = null, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var time = clock ?? TimeProvider.System;
        var operation = new Operation(time, cancellationToken, timeout);
        operation.Check();
        Guard.Require(settings is not null && report is not null && build is not null && logs is not null);
        settings!.Validate();
        logs!.Range.Validate();
        Guard.Require(logs.Records.Count <= logs.Range.MaximumRecords);
        var id = Guid.NewGuid();
        var frozenAt = time.GetUtcNow();
        Guard.Utc(frozenAt);
        var key = RandomNumberGenerator.GetBytes(32);
        var map = new Dictionary<Guid, Guid>();
        var files = new List<(PreviewFile Info, byte[] Bytes)>();
        var total = 0;
        Guid Pseudonym(Guid raw)
        {
            if (map.TryGetValue(raw, out var alias)) return alias;
            Guard.Require(map.Count < 8192, SupportFailure.LimitExceeded);
            Span<byte> digest = stackalloc byte[32];
            HMACSHA256.HashData(key, raw.ToByteArray(), digest);
            alias = new Guid(digest[..16]);
            CryptographicOperations.ZeroMemory(digest);
            map.Add(raw, alias);
            return alias;
        }
        void Add<T>(string name, T value, string source)
        {
            operation.Check();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            total += bytes.Length;
            if (total > MaximumFrozenBytes)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new SupportException(SupportFailure.LimitExceeded);
            }
            files.Add((new(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), source), bytes));
        }
        try
        {
            Add("build.json", build!, "Assembly metadata or explicitly supplied packaging manifest; not signature verification");
            Add("settings.json", settings, "Explicit typed configuration summary; no credential or config read");
            Add("doctor.json", ReportProjection.Freeze(report!, Pseudonym), "Shared DoctorReport; recorded provenance/freshness, not a new probe");
            var selected = logs.Records.Select(record =>
            {
                operation.Check();
                record.Validate();
                return record with
                {
                    JournalId = Pseudonym(record.JournalId),
                    Event = record.Event with
                    {
                        TraceId = record.Event.TraceId is { } trace ? Pseudonym(trace) : null,
                        TurnId = record.Event.TurnId is { } turn ? Pseudonym(turn) : null
                    }
                };
            }).ToArray();
            Add("events.json", selected, "Explicit journal receipt-UTC range; fixture/live/not-run labels retained");
            Add("manifest.json", new BundleManifest(1, id, frozenAt, logs.Range,
                selected.Length == 0 ? null : selected.Min(r => r.AcceptedUtc),
                selected.Length == 0 ? null : selected.Max(r => r.AcceptedUtc),
                "SHA-256 detects changed bytes; it is not a publisher signature or privacy certification.",
                OmissionSummary, files.Select(f => f.Info).ToArray()), "Fixed bundle inventory; manifest digest appears in the preview, not recursively in itself");
            operation.Check();
            return new(id, frozenAt, logs.Range, files.ToArray(), time);
        }
        catch
        {
            foreach (var file in files) CryptographicOperations.ZeroMemory(file.Bytes);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            map.Clear();
        }
    }

    // Returns a copy: the caller cannot mutate approved bytes. The caller owns disposal of its copy.
    public byte[] Preview(string filename) => Exclusive(() =>
    {
        var file = files.SingleOrDefault(f => f.Info.Name == filename);
        Guard.Require(file.Info is not null);
        return file.Bytes.ToArray();
    });

    public ExportConsent Approve(Guid snapshotId, string digest, string absoluteDestination,
        CancellationToken cancellationToken = default) => Exclusive(() => Storage.Run(() =>
    {
        var operation = new Operation(clock, cancellationToken, null);
        operation.Check();
        Guard.Require(snapshotId == Id && digest == Digest, SupportFailure.ConsentMismatch);
        var destination = Destination(absoluteDestination);
        Guard.Require(!File.Exists(destination) && !Directory.Exists(destination), SupportFailure.DestinationExists);
        operation.Check();
        return new ExportConsent(Id, Digest, destination);
    }));

    public ExportReceipt Export(ExportConsent consent, string absoluteDestination,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
        Export(consent, absoluteDestination, new SupportFileSystem(), cancellationToken, timeout);

    internal ExportReceipt Export(ExportConsent consent, string absoluteDestination, SupportFileSystem fs,
        CancellationToken token = default, TimeSpan? timeout = null) => Exclusive(() => Storage.Run(() =>
    {
        var operation = new Operation(clock, token, timeout);
        operation.Check();
        Guard.Require(pending is null, SupportFailure.CleanupPending);
        var destination = Destination(absoluteDestination);
        Guard.Require(consent is not null && consent.SnapshotId == Id && consent.Digest == Digest &&
            string.Equals(consent.Destination, destination, OperatingSystem.IsWindows() ?
                StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), SupportFailure.ConsentMismatch);
        Guard.Require(Interlocked.CompareExchange(ref consent!.Used, 1, 0) == 0, SupportFailure.ConsentConsumed);
        Guard.Require(!File.Exists(destination) && !Directory.Exists(destination), SupportFailure.DestinationExists);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".martlet-support-{Guid.NewGuid():N}.partial");
        var closeFailed = false;
        try
        {
            operation.Check();
            var stream = fs.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            pending = new(fs, temporary, stream);
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                foreach (var file in files)
                {
                    operation.Check();
                    var entry = archive.CreateEntry(file.Info.Name, CompressionLevel.NoCompression);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var output = entry.Open();
                    fs.Write(output, file.Bytes);
                    operation.Check();
                }
            }
            operation.Check();
            fs.Flush(stream);
            var length = stream.Length;
            Guard.Require(length <= MaximumFrozenBytes + 4096, SupportFailure.LimitExceeded);
            try { fs.Close(stream); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                closeFailed = true;
                throw;
            }
            pending.Stream = null;
            operation.Check();
            Storage.CheckAncestors(Path.GetDirectoryName(destination)!);
            Storage.CheckFile(destination);
            Guard.Require(!File.Exists(destination) && !Directory.Exists(destination), SupportFailure.DestinationExists);
            operation.Check();
            // Commit boundary: a synchronous atomic move cannot be canceled halfway through.
            fs.Move(temporary, destination, overwrite: false);
            pending = null;
            return new ExportReceipt(Id, Digest, length);
        }
        finally
        {
            // Do not delete a still-open file when close itself fails. The exact owned partial remains evidence.
            if (!closeFailed) CleanupCore();
        }
    }));

    public void RetryCleanup(CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
        Exclusive(() => Storage.Run(() =>
        {
            var operation = new Operation(clock, cancellationToken, timeout);
            operation.Check();
            CleanupCore(operation);
            operation.Check();
            return true;
        }));

    private void CleanupCore(Operation? operation = null)
    {
        if (pending is null) return;
        if (pending.Stream is { } stream)
        {
            pending.FileSystem.Close(stream);
            pending.Stream = null;
        }
        operation?.Check();
        Storage.CheckAncestors(Path.GetDirectoryName(pending.Path)!);
        Storage.CheckFile(pending.Path);
        pending.FileSystem.Delete(pending.Path);
        pending = null;
    }

    private static string Destination(string path)
    {
        var full = Storage.LocalPath(path, false);
        Guard.Require(Path.GetExtension(full).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.GetDirectoryName(full)), SupportFailure.InvalidPath);
        return full;
    }

    private T Exclusive<T>(Func<T> action)
    {
        Guard.Require(Monitor.TryEnter(gate), SupportFailure.Busy);
        try { Guard.Require(!disposed, SupportFailure.Closed); return action(); }
        finally { Monitor.Exit(gate); }
    }

    public void Dispose()
    {
        Guard.Require(Monitor.TryEnter(gate), SupportFailure.Busy);
        try
        {
            if (disposed) return;
            Storage.Run(() => CleanupCore());
            foreach (var file in files) CryptographicOperations.ZeroMemory(file.Bytes);
            disposed = true;
        }
        finally { Monitor.Exit(gate); }
    }
}
