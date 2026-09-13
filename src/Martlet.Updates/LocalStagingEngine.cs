namespace Martlet.Updates;

internal enum StagingIoPoint
{
    BeforeCreateDirectory, BeforeCreateFile, BeforeWrite, BeforeFlush, AfterCandidateCopy, BeforeFinalize,
    BeforeRename, BeforeCleanup
}

// Synchronous, bounded local IO. A UI must offload the entire operation, retaining this owner
// until it returns AND any pending cleanup completes. Cancellation never disposes active inputs.
public sealed class LocalStagingEngine
{
    private readonly string root;
    private readonly UpdateTrustPolicy trust;
    private readonly StagingLimits limits;
    private readonly Func<InstalledVersionFacts> readInstalledFacts;
    private readonly CandidateVerifier verifier;
    private int busy;
    private long generation;
    private OwnedStage? pending;
    internal Action<StagingIoPoint, CancellationToken>? Io { get; init; }
    internal Func<long>? AvailableBytes { get; init; }
    public string? PendingCleanupDirectory => pending?.Directory;

    public LocalStagingEngine(string existingPrivateStagingRoot, UpdateTrustPolicy trust,
        Func<InstalledVersionFacts> readInstalledFacts, StagingLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(readInstalledFacts);
        root = LocalPaths.Canonical(existingPrivateStagingRoot);
        if (root == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!) || !Directory.Exists(root))
            throw new StagingException(StagingFailure.AccessDenied);
        LocalPaths.NoReparse(root);
        this.trust = trust; this.readInstalledFacts = readInstalledFacts;
        this.limits = limits ?? new();
        this.limits.Validate();
        verifier = new(trust, this.limits);
    }

    public StagingPlan Preview(string archivePath, string envelopePath, string destination,
        CancellationToken token = default) => Run(() =>
    {
        generation++;
        trust.RequireConfigured();
        token.ThrowIfCancellationRequested();
        var installed = Current();
        archivePath = Source(archivePath);
        envelopePath = Source(envelopePath);
        destination = Destination(destination);
        RequireAbsent(destination);
        using var envelope = BoundedIo.OpenRead(envelopePath);
        var candidate = verifier.ReadEnvelope(BoundedIo.Read(envelope, Wire.MaximumEnvelopeBytes, token), installed);
        using var archive = BoundedIo.OpenRead(archivePath);
        verifier.VerifyArchive(archive, candidate, token);
        var plan = new StagingPlan(this, generation, archivePath, envelopePath, destination, installed, candidate);
        RequireCurrent(plan.Installed);
        RequireSpace(plan.RequiredFreeBytes);
        token.ThrowIfCancellationRequested();
        return plan;
    }, token);

    public StagedReceipt Stage(StagingPlan plan, StagingApproval approval, CancellationToken token = default) => Run(() =>
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        approval.Consume(plan);
        if (!ReferenceEquals(plan.Owner, this) || plan.Generation != generation)
            throw new StagingException(StagingFailure.Conflict);
        token.ThrowIfCancellationRequested();
        using var rootLock = LockRoot();
        Destination(plan.Destination);
        RequireAbsent(plan.Destination);
        RequireCurrent(plan.Installed);
        Source(plan.ArchivePath);
        Source(plan.EnvelopePath);
        using var envelope = BoundedIo.OpenRead(plan.EnvelopePath);
        using var archive = BoundedIo.OpenRead(plan.ArchivePath);
        ValidateSources(plan, archive, envelope, token);
        RequireSpace(plan.RequiredFreeBytes);
        var working = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N"));
        RequireAbsent(working);
        Point(StagingIoPoint.BeforeCreateDirectory, token);
        LocalPaths.NoReparse(root);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(working);
        pending = new(working);
        var copyPath = Path.Combine(working, "candidate.zip");
        using (var output = CreateFile(copyPath, token))
        {
            archive.Position = 0;
            BoundedIo.CopyAndHash(archive, output, plan.ArchiveBytes, plan.ArchiveSha256, token,
                () => Point(StagingIoPoint.BeforeWrite, token));
            Flush(output, token);
        }
        Point(StagingIoPoint.AfterCandidateCopy, token);
        WriteFile(Path.Combine(working, "candidate.json"), plan.Candidate.EnvelopeBytes, token);
        var payloadRoot = Path.Combine(working, "payload");
        CreateDirectory(payloadRoot, token);
        using (var copy = BoundedIo.OpenRead(copyPath))
        {
            // The private verified copy is the only extraction source, including on platforms
            // where sharing modes cannot prevent a source path from being replaced.
            BoundedIo.CopyAndHash(copy, null, plan.ArchiveBytes, plan.ArchiveSha256, token);
            RestrictedZip.VerifyContent(copy, plan.Candidate, limits, token, (name, input, file, crc) =>
            {
                var path = Path.Combine(payloadRoot, name.Replace('/', Path.DirectorySeparatorChar));
                EnsureParents(path, payloadRoot, token);
                using var output = CreateFile(path, token);
                BoundedIo.CopyAndHash(input, output, file.Bytes, file.Sha256, token,
                    () => Point(StagingIoPoint.BeforeWrite, token), crc);
                Flush(output, token);
            });
        }
        var receiptBytes = Wire.Write(Document(plan.Destination, plan.Installed, plan.Candidate));
        if (receiptBytes.Length > Wire.MaximumReceiptBytes) throw new StagingException(StagingFailure.CapacityExceeded);
        WriteFile(Path.Combine(working, "staged.json"), receiptBytes, token);
        // Verify the actual written files too, not just bytes passed to FileStream.Write.
        VerifyExtracted(working, plan.Candidate, receiptBytes, token);
        Point(StagingIoPoint.BeforeFinalize, token);
        RequireCurrent(plan.Installed);
        ValidateSources(plan, archive, envelope, token);
        Destination(plan.Destination);
        RequireAbsent(plan.Destination);
        LocalPaths.NoReparse(working);
        Point(StagingIoPoint.BeforeRename, token);
        token.ThrowIfCancellationRequested();
        Directory.Move(working, plan.Destination);
        pending = null;
        return new StagedReceipt(plan.Destination, receiptBytes, plan.Candidate, plan.Installed);
    }, token);

    // Persisted JSON is not a trust token. Re-enter the real signature/archive/file verifier
    // before producing a receipt object, and require the current owner-provided facts again.
    public StagedReceipt InspectStaged(string destination, CancellationToken token = default) => Run(() =>
    {
        token.ThrowIfCancellationRequested();
        destination = Destination(destination);
        var installed = Current();
        using var rootLock = LockRoot();
        using var envelope = BoundedIo.OpenRead(Path.Combine(destination, "candidate.json"));
        var candidate = verifier.ReadEnvelope(BoundedIo.Read(envelope, Wire.MaximumEnvelopeBytes, token), installed);
        using var archive = BoundedIo.OpenRead(Path.Combine(destination, "candidate.zip"));
        verifier.VerifyArchive(archive, candidate, token);
        using var receipt = BoundedIo.OpenRead(Path.Combine(destination, "staged.json"));
        var bytes = BoundedIo.Read(receipt, Wire.MaximumReceiptBytes, token);
        Wire.Read<ReceiptDocument>(bytes, Wire.MaximumReceiptBytes, canonical: true);
        if (!bytes.AsSpan().SequenceEqual(Wire.Write(Document(destination, installed, candidate))))
            throw new StagingException(StagingFailure.InvalidReceipt);
        VerifyExtracted(destination, candidate, bytes, token);
        RequireCurrent(installed);
        token.ThrowIfCancellationRequested();
        return new StagedReceipt(destination, bytes, candidate, installed);
    }, token, inspecting: true);

    public void RetryCleanup()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new StagingException(StagingFailure.Busy);
        try
        {
            if (pending is null) throw new StagingException(StagingFailure.Conflict);
            using var rootLock = LockRoot();
            Cleanup();
        }
        catch (Exception ex) when (Handled(ex))
        {
            if (pending is not null)
                throw new StagingException(StagingFailure.CleanupPending, pending.Directory, Failure(ex, CancellationToken.None));
            throw Translate(ex, CancellationToken.None);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private T Run<T>(Func<T> operation, CancellationToken token, bool inspecting = false)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new StagingException(StagingFailure.Busy);
        try
        {
            if (pending is not null) throw new StagingException(StagingFailure.Busy);
            return operation();
        }
        catch (Exception ex) when (Handled(ex))
        {
            var failure = Failure(ex, token);
            if (pending is not null && failure != StagingFailure.Busy)
            {
                try { Cleanup(); }
                catch (Exception cleanup) when (Handled(cleanup))
                { throw new StagingException(StagingFailure.CleanupPending, pending!.Directory, failure); }
            }
            if (inspecting && failure is StagingFailure.InvalidManifest or StagingFailure.CorruptArchive or
                StagingFailure.UnsafeEntry or StagingFailure.CapacityExceeded)
                throw new StagingException(StagingFailure.InvalidReceipt);
            if (ex is StagingException) throw;
            throw Translate(ex, token);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private static bool Handled(Exception ex) => ex is StagingException or IOException or UnauthorizedAccessException or
        OperationCanceledException or ArgumentException or NotSupportedException;
    private static StagingException Translate(Exception ex, CancellationToken token) =>
        ex as StagingException ?? new(Failure(ex, token));
    private static StagingFailure Failure(Exception ex, CancellationToken token) => ex switch
    {
        StagingException staging => staging.Failure,
        OperationCanceledException when token.IsCancellationRequested => StagingFailure.Cancelled,
        UnauthorizedAccessException => StagingFailure.AccessDenied,
        InvalidDataException or EndOfStreamException => StagingFailure.CorruptArchive,
        IOException io when (io.HResult & 0xffff) is 112 or 39 or 28 => StagingFailure.InsufficientDisk,
        ArgumentException or NotSupportedException => StagingFailure.InvalidManifest,
        _ => StagingFailure.Unavailable
    };

    private InstalledVersionFacts Current()
    {
        var installed = readInstalledFacts();
        if (installed is null) throw new StagingException(StagingFailure.Conflict);
        installed.Validate();
        var installation = LocalPaths.Canonical(installed.InstallationDirectory);
        if (LocalPaths.Within(root, installation) || LocalPaths.Within(installation, root))
            throw new StagingException(StagingFailure.Conflict);
        return installed;
    }

    private void RequireCurrent(InstalledVersionFacts expected)
    {
        if (Current() != expected) throw new StagingException(StagingFailure.Conflict);
    }

    private string Source(string path)
    {
        path = LocalPaths.Canonical(path);
        LocalPaths.NoReparse(path);
        if (LocalPaths.Within(path, root)) throw new StagingException(StagingFailure.Conflict);
        return path;
    }

    private string Destination(string path)
    {
        path = LocalPaths.Canonical(path);
        LocalPaths.NoReparse(path);
        var name = Path.GetFileName(path);
        if (Path.GetDirectoryName(path) != root || name.Length is 0 or > 100 || name.StartsWith('.') ||
            name.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new StagingException(StagingFailure.Conflict);
        // Reuse the exact same Windows-safe segment rules used by payload inventory.
        LocalPaths.Entry("help/" + name);
        return path;
    }

    private static void RequireAbsent(string path)
    {
        if (LocalPaths.Exists(path)) throw new StagingException(StagingFailure.Conflict);
    }

    private void RequireSpace(long required)
    {
        LocalPaths.NoReparse(root);
        if ((AvailableBytes?.Invoke() ?? new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace) < required)
            throw new StagingException(StagingFailure.InsufficientDisk);
    }

    private FileStream LockRoot()
    {
        LocalPaths.NoReparse(root);
        var path = Path.Combine(root, ".martlet-staging.lock");
        LocalPaths.NoReparse(path);
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
        { throw new StagingException(StagingFailure.Busy); }
    }

    private void Point(StagingIoPoint point, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Io?.Invoke(point, token);
        token.ThrowIfCancellationRequested();
    }

    private static void ValidateSources(StagingPlan plan, FileStream archive, FileStream envelope, CancellationToken token)
    {
        void Envelope(Stream stream)
        {
            stream.Position = 0;
            if (!BoundedIo.Read(stream, Wire.MaximumEnvelopeBytes, token).AsSpan().SequenceEqual(plan.Candidate.EnvelopeBytes))
                throw new StagingException(StagingFailure.Conflict);
        }
        void Archive(Stream stream)
        {
            stream.Position = 0;
            BoundedIo.CopyAndHash(stream, null, plan.ArchiveBytes, plan.ArchiveSha256, token);
        }
        try
        {
            Envelope(envelope);
            Archive(archive);
            // Reopen the names as well as checking pinned handles: detects Unix rename replacement.
            using var namedEnvelope = BoundedIo.OpenRead(plan.EnvelopePath);
            using var namedArchive = BoundedIo.OpenRead(plan.ArchivePath);
            Envelope(namedEnvelope);
            Archive(namedArchive);
        }
        catch (StagingException ex) when (ex.Failure is StagingFailure.CorruptArchive or StagingFailure.CapacityExceeded)
        { throw new StagingException(StagingFailure.Conflict); }
    }

    private FileStream CreateFile(string path, CancellationToken token)
    {
        Point(StagingIoPoint.BeforeCreateFile, token);
        LocalPaths.NoReparse(path);
        token.ThrowIfCancellationRequested();
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        pending!.Files.Add(path);
        return output;
    }

    private void WriteFile(string path, byte[] bytes, CancellationToken token)
    {
        using var output = CreateFile(path, token);
        Point(StagingIoPoint.BeforeWrite, token);
        output.Write(bytes);
        Flush(output, token);
    }

    private void Flush(FileStream output, CancellationToken token)
    {
        Point(StagingIoPoint.BeforeFlush, token);
        output.Flush(flushToDisk: true);
        token.ThrowIfCancellationRequested();
    }

    private void CreateDirectory(string path, CancellationToken token)
    {
        Point(StagingIoPoint.BeforeCreateDirectory, token);
        LocalPaths.NoReparse(path);
        RequireAbsent(path);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        pending!.Directories.Add(path);
    }

    private void EnsureParents(string path, string payloadRoot, CancellationToken token)
    {
        var parents = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path)!; parent != payloadRoot; parent = Path.GetDirectoryName(parent)!)
            parents.Push(parent);
        while (parents.TryPop(out var parent))
            if (!pending!.Directories.Contains(parent, StringComparer.Ordinal)) CreateDirectory(parent, token);
    }

    private void Cleanup()
    {
        var owned = pending!;
        Io?.Invoke(StagingIoPoint.BeforeCleanup, CancellationToken.None);
        LocalPaths.NoReparse(owned.Directory);
        while (owned.Files.Count > 0)
        {
            var file = owned.Files[^1];
            LocalPaths.NoReparse(file);
            if (LocalPaths.Exists(file)) File.Delete(file);
            owned.Files.RemoveAt(owned.Files.Count - 1);
        }
        while (owned.Directories.Count > 0)
        {
            var directory = owned.Directories[^1];
            LocalPaths.NoReparse(directory);
            if (LocalPaths.Exists(directory)) Directory.Delete(directory, recursive: false);
            owned.Directories.RemoveAt(owned.Directories.Count - 1);
        }
        pending = null;
    }

    private static ReceiptDocument Document(string destination, InstalledVersionFacts installed, VerifiedCandidate candidate) => new()
    {
        FormatVersion = 1, Destination = destination, Installed = installed,
        EnvelopeSha256 = Wire.Hash(candidate.EnvelopeBytes), ManifestSha256 = Wire.Hash(candidate.ManifestBytes),
        Candidate = candidate.Manifest, NextSteps = StagedReceipt.NextSteps
    };

    private static void VerifyExtracted(string directory, VerifiedCandidate candidate, byte[] receiptBytes, CancellationToken token)
    {
        var expected = candidate.Manifest.Files.ToDictionary(f => "payload/" + f.Path, StringComparer.Ordinal);
        expected.Add("candidate.zip", new PayloadFile
        {
            Path = "candidate.zip", Bytes = candidate.Manifest.ArchiveBytes, Sha256 = candidate.Manifest.ArchiveSha256
        });
        expected.Add("candidate.json", new PayloadFile
        {
            Path = "candidate.json", Bytes = candidate.EnvelopeBytes.Length, Sha256 = Wire.Hash(candidate.EnvelopeBytes)
        });
        expected.Add("staged.json", new PayloadFile
        {
            Path = "staged.json", Bytes = receiptBytes.Length, Sha256 = Wire.Hash(receiptBytes)
        });
        var remaining = expected.Keys.ToHashSet(StringComparer.Ordinal);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal) { "payload" };
        foreach (var path in expected.Keys)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                expectedDirectories.Add(path[..slash]);
        var directories = new Stack<string>();
        directories.Push(directory);
        while (directories.TryPop(out var current))
        {
            LocalPaths.NoReparse(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                LocalPaths.NoReparse(path);
                var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                {
                    if (!expectedDirectories.Remove(relative)) throw new StagingException(StagingFailure.InvalidReceipt);
                    directories.Push(path);
                }
                else
                {
                    if (!remaining.Remove(relative)) throw new StagingException(StagingFailure.InvalidReceipt);
                    if (expected.TryGetValue(relative, out var file))
                    {
                        using var input = BoundedIo.OpenRead(path);
                        BoundedIo.CopyAndHash(input, null, file.Bytes, file.Sha256, token);
                    }
                }
            }
        }
        if (remaining.Count != 0 || expectedDirectories.Count != 0)
            throw new StagingException(StagingFailure.InvalidReceipt);
    }

    private sealed class OwnedStage(string directory)
    {
        internal string Directory { get; } = directory;
        internal List<string> Files { get; } = [];
        internal List<string> Directories { get; } = [directory];
    }
}
