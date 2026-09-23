using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Readiness;

namespace Martlet.Launcher;

internal enum LauncherAttemptKind
{
    ActivationReadiness,
    DesktopLaunch
}

internal enum LauncherEvidencePoint
{
    AfterIntent,
    AfterProcessStarted,
    AfterReadiness,
    BeforeTerminal
}

internal sealed class LauncherEvidenceStore
{
    private const int MaximumRootChildren = 64;
    private const int MaximumAttemptsPerProfile = 64;
    private const int MaximumDocumentBytes = 16384;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 4
    };
    private readonly string root;

    internal Action<LauncherEvidencePoint, Guid, int?>? Io { get; set; }
    internal string Root => root;

    internal LauncherEvidenceStore(string existingPrivateRoot)
    {
        root = Canonical(existingPrivateRoot);
        if (!Directory.Exists(root) ||
            root == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!) ||
            root.Length > 180)
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
        RequireNoReparse(root);
    }

    internal void RequireSeparateFrom(LaunchTarget target)
    {
        if (Overlaps(root, target.StageDirectory) ||
            Overlaps(root, target.PayloadDirectory) ||
            Overlaps(root, target.ExecutablePath) ||
            Overlaps(root, target.LeaseDirectory))
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
    }

    internal LauncherLease Acquire(Guid profileId, string authoritativeLeaseDirectory)
    {
        if (profileId == Guid.Empty)
            throw new LauncherException(LauncherFailure.InvalidActivation);
        authoritativeLeaseDirectory = Canonical(authoritativeLeaseDirectory);
        if (!Directory.Exists(authoritativeLeaseDirectory))
            throw new LauncherException(LauncherFailure.InvalidActivation);
        RequireNoReparse(authoritativeLeaseDirectory);
        RequireNoReparse(root);
        var lockPath = Canonical(Path.Combine(
            authoritativeLeaseDirectory,
            ".martlet-launcher-" + profileId.ToString("N") + ".lock"));
        FileStream owner;
        try
        {
            owner = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
        {
            throw new LauncherException(LauncherFailure.Busy);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
        }
        try
        {
            var rootChildren = Directory.EnumerateFileSystemEntries(root)
                .Take(MaximumRootChildren + 1).ToArray();
            if (rootChildren.Length > MaximumRootChildren)
                throw new LauncherException(LauncherFailure.EvidenceUnavailable);
            var profileDirectory = Canonical(Path.Combine(
                root, "profile-" + profileId.ToString("N")));
            if (!Directory.Exists(profileDirectory))
                Directory.CreateDirectory(profileDirectory);
            RequireNoReparse(profileDirectory);
            return new(owner, profileId, profileDirectory);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    internal LauncherAttempt Begin(
        LauncherLease lease,
        LauncherAttemptKind kind,
        LaunchTarget target,
        DateTimeOffset deadlineUtc)
    {
        var attemptId = Guid.NewGuid();
        try
        {
            RequireNoReparse(root);
            RequireNoReparse(lease.ProfileDirectory);
            var children = Directory.EnumerateFileSystemEntries(lease.ProfileDirectory)
                .Take(MaximumAttemptsPerProfile + 1).ToArray();
            if (children.Length >= MaximumAttemptsPerProfile ||
                children.Any(path => !ValidAttemptName(Path.GetFileName(path))))
                throw new LauncherException(LauncherFailure.EvidenceUnavailable);
            var directory = Canonical(Path.Combine(
                lease.ProfileDirectory, "attempt-" + attemptId.ToString("N")));
            Directory.CreateDirectory(directory);
            RequireNoReparse(directory);
            var runtime = Canonical(Path.Combine(directory, "runtime"));
            Directory.CreateDirectory(runtime);
            RequireNoReparse(runtime);
            var intent = new LauncherIntentDocument(
                1,
                attemptId,
                kind.ToString(),
                target.ActivationRevision,
                target.TransitionId,
                target.TransitionKind?.ToString(),
                target.ProfileId,
                target.Version,
                target.Rid,
                target.SettingsRevision,
                target.SettingsSchemaVersion,
                target.PayloadSha256,
                target.ManifestSha256,
                target.ExecutableSha256,
                target.SelectionRevision,
                DateTimeOffset.UtcNow,
                deadlineUtc);
            WriteNew(Path.Combine(directory, "intent-v1.json"), intent);
            Io?.Invoke(LauncherEvidencePoint.AfterIntent, attemptId, null);
            return new(this, attemptId, directory, runtime);
        }
        catch (LauncherException error) when (error.AttemptId is null)
        {
            throw new LauncherException(error.Failure, attemptId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(LauncherFailure.EvidenceUnavailable, attemptId);
        }
    }

    private void WriteNew<T>(string path, T value)
    {
        path = Canonical(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is 0 or > MaximumDocumentBytes)
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
        using (var stream = new FileStream(path, FileMode.CreateNew,
                   FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (read.Length != bytes.Length || !Martlet.Updates.BoundedIo.Read(
                read, MaximumDocumentBytes, CancellationToken.None).AsSpan().SequenceEqual(bytes))
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
    }

    private static bool ValidAttemptName(string name) =>
        name.Length == 40 &&
        name.StartsWith("attempt-", StringComparison.Ordinal) &&
        Guid.TryParseExact(name[8..], "N", out var id) &&
        name == "attempt-" + id.ToString("N");

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
        string full;
        try
        {
            full = Martlet.Updates.LocalPaths.Canonical(path);
            Martlet.Updates.LocalPaths.NoReparse(full);
        }
        catch (Martlet.Updates.StagingException)
        {
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
        }
        if (path != full ||
            full.Split(Path.DirectorySeparatorChar).Any(part =>
                part.EndsWith('.') || part.EndsWith(' ')))
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
        return full;
    }

    private static void RequireNoReparse(string path)
    {
        try { Martlet.Updates.LocalPaths.NoReparse(path); }
        catch (Martlet.Updates.StagingException)
        {
            throw new LauncherException(LauncherFailure.EvidenceUnavailable);
        }
    }

    private static bool Overlaps(string left, string right) =>
        Same(left, right) ||
        LauncherSupport.Within(left, right) ||
        LauncherSupport.Within(right, left);

    private static bool Same(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    internal sealed class LauncherAttempt(
        LauncherEvidenceStore owner,
        Guid id,
        string directory,
        string runtimeDirectory)
    {
        private int readinessRecorded;
        private int terminalRecorded;
        internal bool HasTerminal => Volatile.Read(ref terminalRecorded) != 0;

        internal Guid Id { get; } = id;
        internal string RuntimeDirectory { get; } = runtimeDirectory;

        internal void ProcessStarted(int processId) =>
            owner.Io?.Invoke(
                LauncherEvidencePoint.AfterProcessStarted, Id, processId);

        internal void RecordReadiness(
            PrivateReadinessResult result,
            int processId)
        {
            if (Interlocked.Exchange(ref readinessRecorded, 1) != 0)
                throw new LauncherException(
                    LauncherFailure.EvidenceUnavailable, Id);
            try
            {
                owner.WriteNew(Path.Combine(directory, "readiness-v1.json"),
                    new LauncherReadinessDocument(
                        1,
                        Id,
                        processId,
                        result.Failure.ToString(),
                        result.State?.ToString(),
                        result.ExitCode,
                        DateTimeOffset.UtcNow));
                owner.Io?.Invoke(
                    LauncherEvidencePoint.AfterReadiness, Id, processId);
            }
            catch (LauncherException error) when (error.AttemptId is null)
            {
                throw new LauncherException(error.Failure, Id);
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    LauncherFailure.EvidenceUnavailable, Id);
            }
        }

        internal void RecordTerminal(
            LauncherFailure? failure,
            int? processId,
            int? exitCode,
            bool cleanupConfirmed)
        {
            if (Interlocked.Exchange(ref terminalRecorded, 1) != 0)
                throw new LauncherException(
                    LauncherFailure.EvidenceUnavailable, Id);
            try
            {
                owner.Io?.Invoke(
                    LauncherEvidencePoint.BeforeTerminal, Id, processId);
                owner.WriteNew(Path.Combine(directory, "terminal-v1.json"),
                    new LauncherTerminalDocument(
                        1,
                        Id,
                        processId,
                        failure?.ToString() ?? "Completed",
                        exitCode,
                        cleanupConfirmed,
                        DateTimeOffset.UtcNow));
            }
            catch (LauncherException error) when (error.AttemptId is null)
            {
                throw new LauncherException(error.Failure, Id);
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    LauncherFailure.EvidenceUnavailable, Id);
            }
        }
    }

    internal sealed class LauncherLease(
        FileStream owner,
        Guid profileId,
        string profileDirectory) : IDisposable
    {
        internal Guid ProfileId { get; } = profileId;
        internal string ProfileDirectory { get; } = profileDirectory;
        public void Dispose() => owner.Dispose();
    }

    private sealed record LauncherIntentDocument(
        int FormatVersion,
        Guid AttemptId,
        string Kind,
        long? ActivationRevision,
        Guid TransitionId,
        string? TransitionKind,
        Guid ProfileId,
        string Version,
        string Rid,
        string SettingsRevision,
        int SettingsSchemaVersion,
        string PayloadSha256,
        string ManifestSha256,
        string ExecutableSha256,
        long SelectionRevision,
        DateTimeOffset StartedUtc,
        DateTimeOffset DeadlineUtc);

    private sealed record LauncherReadinessDocument(
        int FormatVersion,
        Guid AttemptId,
        int ProcessId,
        string Failure,
        string? State,
        int? ExitCode,
        DateTimeOffset CompletedUtc);

    private sealed record LauncherTerminalDocument(
        int FormatVersion,
        Guid AttemptId,
        int? ProcessId,
        string Outcome,
        int? ExitCode,
        bool CleanupConfirmed,
        DateTimeOffset CompletedUtc);
}
