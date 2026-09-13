namespace Martlet.Support;

// Synchronous, bounded IO. No queue, worker, timer or collection starts on construction.
public sealed class DiagnosticJournal : IDisposable
{
    internal const string ManifestName = ".martlet-support.v1.json";
    internal const string LockName = ".martlet-support.v1.lock";
    private readonly string directory;
    private readonly SupportFileSystem fs;
    private readonly TimeProvider clock;
    private readonly FileStream lease;
    private readonly object gate = new();
    private JournalManifest manifest;
    private bool closed;
    private bool faulted;
    public JournalRecovery Recovery { get; private set; } = new(0, 0);

    private DiagnosticJournal(string directory, JournalManifest manifest, FileStream lease,
        SupportFileSystem fs, TimeProvider clock)
    {
        this.directory = directory; this.manifest = manifest; this.lease = lease; this.fs = fs; this.clock = clock;
    }

    public static DiagnosticJournal Start(string absoluteDirectory, JournalOptions? options = null,
        TimeProvider? clock = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
        Start(absoluteDirectory, options ?? new(), clock ?? TimeProvider.System, new SupportFileSystem(), cancellationToken, timeout);

    internal static DiagnosticJournal Start(string absoluteDirectory, JournalOptions options, TimeProvider clock,
        SupportFileSystem fs, CancellationToken token = default, TimeSpan? timeout = null) => Storage.Run(() =>
    {
        options.Validate();
        var operation = new Operation(clock, token, timeout);
        operation.Check();
        var directory = Storage.LocalPath(absoluteDirectory, true);
        Directory.CreateDirectory(directory);
        Storage.CheckAncestors(directory);
        var lease = Acquire(directory, fs, writer: true);
        try
        {
            var path = Path.Combine(directory, ManifestName);
            JournalManifest manifest;
            if (File.Exists(path)) manifest = LoadManifest(directory, fs);
            else
            {
                manifest = new JournalManifest
                {
                    SchemaVersion = 1, Owner = Guid.NewGuid(), Options = options, NextSequence = 1, Segments = [], Sha256 = ""
                }.Seal();
                Storage.WriteFile(fs, path, Storage.Compact(manifest, 16_384), FileMode.CreateNew, operation);
            }
            Guard.Require(manifest.Options == options);
            var journal = new DiagnosticJournal(directory, manifest, lease, fs, clock);
            journal.Recover(operation);
            journal.Prune(operation);
            operation.Check();
            return journal;
        }
        catch
        {
            // Ownership must be released on failed acquisition, without retaining raw exception details.
            try { fs.Close(lease); }
            finally { lease.Dispose(); }
            throw;
        }
    });

    internal static FileStream Acquire(string directory, SupportFileSystem fs, bool writer)
    {
        var path = Path.Combine(directory, LockName);
        Storage.CheckFile(path);
        FileStream lease;
        try { lease = fs.Open(path, writer ? FileMode.OpenOrCreate : FileMode.Open,
            writer ? FileAccess.ReadWrite : FileAccess.Read, writer ? FileShare.None : FileShare.Read); }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
        { throw new SupportException(SupportFailure.Busy); }
        if (lease.Length != 0)
        {
            try { fs.Close(lease); }
            finally { lease.Dispose(); }
            throw new SupportException(SupportFailure.CorruptJournal);
        }
        return lease;
    }

    private string SegmentPath(SegmentSlot slot, bool active) =>
        Path.Combine(directory, $"{manifest.Owner:N}.{slot.Slot:D2}.{(active ? "open" : "jsonl")}");
    private string PendingPath => Path.Combine(directory, $"{manifest.Owner:N}.control.pending");

    internal static JournalManifest LoadManifest(string directory, SupportFileSystem fs)
    {
        try { return SupportJson.Read<JournalManifest>(Storage.ReadBounded(fs, Path.Combine(directory, ManifestName), 16_384), 16_384); }
        catch (SupportException error) when (error.Failure is SupportFailure.InvalidData or SupportFailure.LimitExceeded)
        { throw new SupportException(SupportFailure.CorruptJournal); }
    }

    private void Save(Operation operation)
    {
        manifest = manifest.Seal();
        Storage.WriteFile(fs, PendingPath, Storage.Compact(manifest, 16_384), FileMode.CreateNew, operation);
        operation.Check();
        Storage.CheckFile(Path.Combine(directory, ManifestName));
        fs.Move(PendingPath, Path.Combine(directory, ManifestName), overwrite: true);
        operation.Check();
    }

    private string? ExistingPath(SegmentSlot slot)
    {
        var active = SegmentPath(slot, true);
        var final = SegmentPath(slot, false);
        Storage.CheckFile(active); Storage.CheckFile(final);
        Guard.Require(!File.Exists(active) || !File.Exists(final), SupportFailure.CorruptJournal);
        return File.Exists(active) ? active : File.Exists(final) ? final : null;
    }

    private void CheckLayout()
    {
        Storage.CheckAncestors(directory);
        for (var slot = 0; slot < 32; slot++)
        {
            if (manifest.Segments.Any(s => s.Slot == slot)) continue;
            var unused = new SegmentSlot { Slot = slot, FirstSequence = 1, CreatedUtc = DateTimeOffset.UnixEpoch };
            foreach (var active in new[] { true, false })
            {
                var path = SegmentPath(unused, active);
                Storage.CheckFile(path);
                Guard.Require(!File.Exists(path), SupportFailure.CorruptJournal);
            }
        }
    }

    private void Recover(Operation operation)
    {
        CheckLayout();
        Storage.CheckFile(PendingPath);
        if (File.Exists(PendingPath))
        {
            JournalManifest pending;
            try { pending = SupportJson.Read<JournalManifest>(Storage.ReadBounded(fs, PendingPath, 16_384), 16_384); }
            catch (SupportException error) when (error.Failure is SupportFailure.InvalidData or SupportFailure.LimitExceeded)
            { throw new SupportException(SupportFailure.CorruptJournal); }
            Guard.Require(pending.Owner == manifest.Owner && pending.Options == manifest.Options,
                SupportFailure.CorruptJournal);
            operation.Check();
            fs.Delete(PendingPath);
        }
        var abandoned = 0;
        var truncated = 0;
        foreach (var slot in manifest.Segments)
        {
            operation.Check();
            var path = ExistingPath(slot);
            if (path is null) { abandoned++; continue; }
            var active = path == SegmentPath(slot, true);
            var tail = ReadSegment(slot, path, operation, _ => { }, allowTail: active);
            if (tail != 0)
            {
                using var stream = fs.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
                operation.Check();
                fs.Truncate(stream, stream.Length - tail);
                fs.Flush(stream);
                operation.Check();
                truncated += tail;
            }
            if (active) FinalizeSegment(slot, operation);
        }
        if (abandoned != 0)
        {
            manifest = manifest with { Segments = manifest.Segments.Where(s => ExistingPath(s) is not null).ToArray() };
            Save(operation);
        }
        Guard.Require(DataBytes() <= manifest.Options.MaximumTotalBytes - 32_768, SupportFailure.LimitExceeded);
        Recovery = new(truncated, abandoned);
    }

    private int ReadSegment(SegmentSlot slot, string path, Operation operation, Action<JournalRecord> consume, bool allowTail)
    {
        Storage.CheckFile(path);
        using var stream = fs.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Guard.Require(stream.Length <= manifest.Options.MaximumSegmentBytes, SupportFailure.CorruptJournal);
        var buffer = new byte[manifest.Options.MaximumRecordBytes];
        var length = 0;
        var previous = slot.FirstSequence - 1;
        int next;
        while ((next = stream.ReadByte()) != -1)
        {
            if (length == 0) operation.Check();
            Guard.Require(length < buffer.Length, SupportFailure.CorruptJournal);
            buffer[length++] = (byte)next;
            if (next != '\n') continue;
            JournalRecord record;
            try { record = SupportJson.Read<JournalFrame>(buffer.AsMemory(0, length), buffer.Length).Record; }
            catch (SupportException) { throw new SupportException(SupportFailure.CorruptJournal); }
            Guard.Require(record.JournalId == manifest.Owner && record.Sequence > previous &&
                record.Sequence >= slot.FirstSequence && record.Sequence < manifest.NextSequence,
                SupportFailure.CorruptJournal);
            var nextSlot = manifest.Segments.FirstOrDefault(s => s.FirstSequence > slot.FirstSequence);
            Guard.Require(nextSlot is null || record.Sequence < nextSlot.FirstSequence, SupportFailure.CorruptJournal);
            previous = record.Sequence;
            consume(record);
            length = 0;
        }
        Guard.Require(length == 0 || allowTail, SupportFailure.CorruptJournal);
        return length;
    }

    private void FinalizeSegment(SegmentSlot slot, Operation operation)
    {
        var active = SegmentPath(slot, true);
        if (!File.Exists(active)) return;
        operation.Check();
        Storage.CheckFile(active); Storage.CheckFile(SegmentPath(slot, false));
        fs.Move(active, SegmentPath(slot, false), overwrite: false);
        operation.Check();
    }

    private long DataBytes() => manifest.Segments.Sum(slot =>
        ExistingPath(slot) is { } path ? new FileInfo(path).Length : 0);

    private void DeleteSegment(SegmentSlot slot, Operation operation)
    {
        var path = ExistingPath(slot);
        if (path is not null)
        {
            // Validate before removal: corrupt source evidence is preserved, not silently repaired.
            ReadSegment(slot, path, operation, _ => { }, allowTail: false);
            operation.Check();
            Storage.CheckFile(path);
            Guard.Require((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0, SupportFailure.AccessDenied);
            fs.Delete(path);
        }
        manifest = manifest with { Segments = manifest.Segments.Where(s => s.Slot != slot.Slot).ToArray() };
        Save(operation);
    }

    private void Prune(Operation operation)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(manifest.Options.RetentionDays);
        foreach (var slot in manifest.Segments.Where(s => s.CreatedUtc <= cutoff).ToArray())
            DeleteSegment(slot, operation);
    }

    public AppendReceipt Append(DiagnosticEvent value, CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
        Exclusive(() =>
        {
            var operation = new Operation(clock, cancellationToken, timeout);
            operation.Check();
            Guard.Require(value is not null);
            value!.Validate();
            _ = SupportJson.WriteEvent(value);
            var now = clock.GetUtcNow();
            Guard.Utc(now);
            var sequence = manifest.NextSequence;
            Guard.Require(sequence < long.MaxValue, SupportFailure.LimitExceeded);
            var record = new JournalRecord { SchemaVersion = 1, JournalId = manifest.Owner, Sequence = sequence, AcceptedUtc = now, Event = value };
            var bytes = Storage.Compact(JournalFrame.Create(record), manifest.Options.MaximumRecordBytes);
            Prune(operation);
            var slot = manifest.Segments.LastOrDefault();
            var activePath = slot is null ? null : SegmentPath(slot, true);
            if (slot is not null && File.Exists(activePath) && new FileInfo(activePath).Length + bytes.Length > manifest.Options.MaximumSegmentBytes)
                FinalizeSegment(slot, operation);
            while (DataBytes() + bytes.Length > manifest.Options.MaximumTotalBytes - 32_768 || manifest.Segments.Length == 32 &&
                (activePath is null || !File.Exists(activePath)))
                DeleteSegment(manifest.Segments[0], operation);
            if (slot is null || !manifest.Segments.Contains(slot) || !File.Exists(activePath))
            {
                slot = new SegmentSlot
                {
                    Slot = Enumerable.Range(0, 32).First(i => !manifest.Segments.Any(s => s.Slot == i)),
                    FirstSequence = sequence, CreatedUtc = now
                };
                manifest = manifest with { Segments = [.. manifest.Segments, slot] };
            }
            manifest = manifest with { NextSequence = sequence + 1 };
            Save(operation); // Reserve ownership/order before writing; interruptions may leave explicit gaps.
            var path = SegmentPath(slot, true);
            Storage.WriteFile(fs, path, bytes, File.Exists(path) ? FileMode.Append : FileMode.CreateNew, operation);
            return new AppendReceipt(sequence, bytes.Length);
        });

    public JournalSelection Select(LogRange range, CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
        Exclusive(() => SelectCore(range, new Operation(clock, cancellationToken, timeout)));

    private JournalSelection SelectCore(LogRange range, Operation operation)
    {
        range.Validate(); operation.Check();
        var records = new List<JournalRecord>();
        var bytes = 0;
        foreach (var slot in manifest.Segments)
        {
            var path = ExistingPath(slot) ?? throw new SupportException(SupportFailure.CorruptJournal);
            ReadSegment(slot, path, operation, record =>
            {
                // Range is receipt UTC, not caller-controlled event time.
                if (record.AcceptedUtc < range.FromUtc || record.AcceptedUtc > range.ThroughUtc) return;
                bytes += Storage.Compact(JournalFrame.Create(record), manifest.Options.MaximumRecordBytes).Length;
                Guard.Require(records.Count < range.MaximumRecords && bytes <= range.MaximumBytes, SupportFailure.LimitExceeded);
                records.Add(record);
            }, allowTail: false);
        }
        operation.Check();
        return new(range, records.ToArray());
    }

    public static JournalSelection ReadClosed(string absoluteDirectory, LogRange range, TimeProvider? clock = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null) => Storage.Run(() =>
    {
        var time = clock ?? TimeProvider.System;
        var operation = new Operation(time, cancellationToken, timeout);
        operation.Check();
        var directory = Storage.LocalPath(absoluteDirectory, true);
        var fs = new SupportFileSystem();
        operation.Check();
        using var lease = Acquire(directory, fs, writer: false);
        var journal = new DiagnosticJournal(directory, LoadManifest(directory, fs), lease, fs, time);
        journal.CheckLayout();
        Storage.CheckFile(journal.PendingPath);
        Guard.Require(!File.Exists(journal.PendingPath) &&
            !journal.manifest.Segments.Any(s => File.Exists(journal.SegmentPath(s, true))), SupportFailure.CorruptJournal);
        return journal.SelectCore(range, operation);
    });

    private T Exclusive<T>(Func<T> action)
    {
        Guard.Require(Monitor.TryEnter(gate), SupportFailure.Busy);
        try
        {
            Guard.Require(!closed && !faulted, SupportFailure.Closed);
            return Storage.Run(action);
        }
        catch (SupportException error) when (error.Failure is not (SupportFailure.InvalidData or SupportFailure.UnsupportedVersion or SupportFailure.LimitExceeded))
        {
            faulted = true;
            throw;
        }
        finally { Monitor.Exit(gate); }
    }

    public void Close(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        Guard.Require(Monitor.TryEnter(gate), SupportFailure.Busy);
        try
        {
            if (closed) return;
            Storage.Run(() =>
            {
                try
                {
                    var operation = new Operation(clock, cancellationToken, timeout);
                    operation.Check();
                    if (!faulted)
                        foreach (var slot in manifest.Segments) FinalizeSegment(slot, operation);
                }
                finally
                {
                    // A synchronous IO call has returned before we get here. Never release a live operation's lease.
                    fs.Close(lease);
                    closed = true;
                }
            });
        }
        finally { Monitor.Exit(gate); }
    }

    public void Dispose() => Close();
}
