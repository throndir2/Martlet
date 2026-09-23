using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Martlet.LocalStt;

public enum PackageVerificationStatus
{
    Verified,
    Missing,
    Changed,
    UnsafePath,
    AccessDenied,
    Invalid,
    UnsupportedHost,
    Canceled,
    IoFailure
}

public sealed record PackageVerificationResult(
    PackageVerificationStatus Status,
    VerifiedLocalSttPackage? Package = null);

public interface ILocalSttPackageVerifier
{
    Task<PackageVerificationResult> VerifyForLaunchAsync(CancellationToken cancellationToken);
}

public sealed class VerifiedLocalSttPackage : IAsyncDisposable
{
    private IReadOnlyList<Stream>? locks;

    public string PackageId { get; }
    public string ManifestSha256 { get; }
    public string ExecutablePath { get; }
    public string ModelPath { get; }
    public string RuntimeDirectory { get; }
    public string ModelId { get; }
    public string ModelSha256 { get; }
    public string ExecutableArchiveSha256 { get; }
    public string Language { get; }
    public LocalSttNetworkPolicy NetworkPolicy { get; }

    internal VerifiedLocalSttPackage(
        LocalSttPackageManifest manifest,
        string executablePath,
        string modelPath,
        string runtimeDirectory,
        IReadOnlyList<Stream>? locks = null)
    {
        PackageId = manifest.Id;
        ManifestSha256 = manifest.DocumentSha256;
        ExecutablePath = executablePath;
        ModelPath = modelPath;
        RuntimeDirectory = runtimeDirectory;
        ModelId = manifest.ModelId;
        ModelSha256 = manifest.ModelSha256;
        ExecutableArchiveSha256 = manifest.Document.Runtime.ArchiveSha256;
        Language = manifest.Language;
        NetworkPolicy = manifest.NetworkPolicy;
        this.locks = locks;
    }

    public ValueTask DisposeAsync()
    {
        var owned = Interlocked.Exchange(ref locks, null);
        if (owned is not null)
            foreach (var stream in owned)
                stream.Dispose();
        return ValueTask.CompletedTask;
    }

    public override string ToString() => nameof(VerifiedLocalSttPackage);
}

public sealed class PhysicalLocalSttPackageVerifier : ILocalSttPackageVerifier
{
    private readonly string packageRoot;
    private readonly LocalSttPackageManifest manifest;
    private readonly ILocalPathInspector pathInspector;
    private readonly Func<bool> hostSupported;

    public PhysicalLocalSttPackageVerifier(string packageRoot)
        : this(
            packageRoot,
            LocalSttPackageManifest.Current,
            new PhysicalLocalPathInspector(),
            () => OperatingSystem.IsWindows() &&
                RuntimeInformation.ProcessArchitecture == Architecture.X64)
    {
    }

    internal PhysicalLocalSttPackageVerifier(
        string packageRoot,
        LocalSttPackageManifest manifest,
        ILocalPathInspector pathInspector,
        Func<bool>? hostSupported = null)
    {
        ArgumentNullException.ThrowIfNull(packageRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pathInspector);
        this.packageRoot = LocalPathRules.NormalizeRoot(packageRoot);
        this.manifest = manifest;
        this.pathInspector = pathInspector;
        this.hostSupported = hostSupported ?? (() => true);
    }

    public async Task<PackageVerificationResult> VerifyForLaunchAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(PackageVerificationStatus.Canceled);
        if (!hostSupported())
            return new(PackageVerificationStatus.UnsupportedHost);

        var held = new List<Stream>();
        try
        {
            pathInspector.AssertSafeExisting(packageRoot, directory: true);
            var downloads = LocalPathRules.Combine(packageRoot, "downloads");
            var runtime = LocalPathRules.Combine(packageRoot, "runtime");
            var models = LocalPathRules.Combine(packageRoot, "models");
            pathInspector.AssertSafeExisting(downloads, directory: true);
            pathInspector.AssertSafeExisting(runtime, directory: true);
            pathInspector.AssertSafeExisting(models, directory: true);

            var runtimeDocument = manifest.Document.Runtime;
            var modelDocument = manifest.Document.Model;
            var archivePath = LocalPathRules.Combine(downloads, runtimeDocument.ArchiveFileName);
            var modelPath = LocalPathRules.Combine(models, modelDocument.FileName);
            pathInspector.AssertSafeExisting(archivePath, directory: false);
            pathInspector.AssertSafeExisting(modelPath, directory: false);

            RequireExactEntries(downloads, [runtimeDocument.ArchiveFileName]);
            RequireExactEntries(models, [modelDocument.FileName]);
            RequireExactEntries(runtime, runtimeDocument.Files.Select(file => file.InstalledName));

            var archive = OpenLockedRead(archivePath);
            held.Add(archive);
            if (archive.Length != runtimeDocument.ArchiveBytes ||
                !await HashMatchesAsync(archive, runtimeDocument.ArchiveSha256, cancellationToken).ConfigureAwait(false))
                return Failure(PackageVerificationStatus.Changed, held);

            var model = OpenLockedRead(modelPath);
            held.Add(model);
            if (model.Length != modelDocument.Bytes ||
                !await HashMatchesAsync(model, modelDocument.Sha256, cancellationToken).ConfigureAwait(false))
                return Failure(PackageVerificationStatus.Changed, held);

            archive.Position = 0;
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count == 0 ||
                    zip.Entries.Count > manifest.Document.Provisioning.MaximumArchiveEntries)
                    return Failure(PackageVerificationStatus.Invalid, held);
                long expanded = 0;
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                {
                    ManifestRules.RelativeArchivePath(entry.FullName.TrimEnd('/'));
                    if (entry.Length < 0 || entry.CompressedLength < 0 ||
                        IsArchiveLink(entry) ||
                        !entries.TryAdd(entry.FullName, entry))
                        return Failure(PackageVerificationStatus.Invalid, held);
                    expanded = checked(expanded + entry.Length);
                    if (expanded > manifest.Document.Provisioning.MaximumExpandedRuntimeBytes)
                        return Failure(PackageVerificationStatus.Invalid, held);
                }

                foreach (var file in runtimeDocument.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entries.TryGetValue(file.ArchiveEntry, out var entry) || entry.Length <= 0)
                        return Failure(PackageVerificationStatus.Invalid, held);
                    var installedPath = LocalPathRules.Combine(runtime, file.InstalledName);
                    pathInspector.AssertSafeExisting(installedPath, directory: false);
                    var installed = OpenLockedRead(installedPath);
                    held.Add(installed);
                    if (installed.Length != entry.Length ||
                        !await ContentsEqualAsync(entry, installed, cancellationToken).ConfigureAwait(false))
                        return Failure(PackageVerificationStatus.Changed, held);
                }
            }

            var executableName = runtimeDocument.Files.Single(file =>
                file.Purpose == RuntimeFilePurpose.Executable).InstalledName;
            return new(PackageVerificationStatus.Verified,
                new VerifiedLocalSttPackage(
                    manifest,
                    LocalPathRules.Combine(runtime, executableName),
                    modelPath,
                    runtime,
                    held.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Dispose(held);
            return new(PackageVerificationStatus.Canceled);
        }
        catch (LocalPathException)
        {
            Dispose(held);
            return new(PackageVerificationStatus.UnsafePath);
        }
        catch (FileNotFoundException)
        {
            Dispose(held);
            return new(PackageVerificationStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            Dispose(held);
            return new(PackageVerificationStatus.Missing);
        }
        catch (UnauthorizedAccessException)
        {
            Dispose(held);
            return new(PackageVerificationStatus.AccessDenied);
        }
        catch (InvalidDataException)
        {
            Dispose(held);
            return new(PackageVerificationStatus.Invalid);
        }
        catch (InvalidOperationException)
        {
            Dispose(held);
            return new(PackageVerificationStatus.Invalid);
        }
        catch (IOException)
        {
            Dispose(held);
            return new(PackageVerificationStatus.IoFailure);
        }
        catch (OverflowException)
        {
            Dispose(held);
            return new(PackageVerificationStatus.Invalid);
        }
    }

    private void RequireExactEntries(string directory, IEnumerable<string> expected)
    {
        var required = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observed = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (observed.Length != required.Count)
            throw new InvalidDataException("The package directory contains undeclared entries.");
        foreach (var path in observed)
        {
            pathInspector.AssertSafeExisting(path, directory: false);
            if (!required.Remove(Path.GetFileName(path)))
                throw new InvalidDataException("The package directory contains undeclared entries.");
        }
        if (required.Count != 0)
            throw new FileNotFoundException("A package entry is missing.");
    }

    private static FileStream OpenLockedRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<bool> HashMatchesAsync(
        FileStream stream,
        string expected,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return CryptographicOperations.FixedTimeEquals(
            hash,
            Convert.FromHexString(expected));
    }

    private static async Task<bool> ContentsEqualAsync(
        ZipArchiveEntry entry,
        FileStream installed,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        installed.Position = 0;
        byte[]? first = null;
        byte[]? second = null;
        try
        {
            first = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
            second = await SHA256.HashDataAsync(installed, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(first, second);
        }
        finally
        {
            if (first is not null)
                CryptographicOperations.ZeroMemory(first);
            if (second is not null)
                CryptographicOperations.ZeroMemory(second);
            installed.Position = 0;
        }
    }

    private static bool IsArchiveLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink;
    }

    private static PackageVerificationResult Failure(
        PackageVerificationStatus status,
        List<Stream> held)
    {
        Dispose(held);
        return new(status);
    }

    private static void Dispose(List<Stream> streams)
    {
        foreach (var stream in streams)
            stream.Dispose();
        streams.Clear();
    }
}

internal interface ILocalPathInspector
{
    void AssertSafeExisting(string path, bool directory);
}

internal sealed class PhysicalLocalPathInspector : ILocalPathInspector
{
    public void AssertSafeExisting(string path, bool directory)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) ||
            !IsAcceptedDriveType(new DriveInfo(root).DriveType))
            throw new LocalPathException();
        FileSystemInfo? current = directory ? new DirectoryInfo(path) : new FileInfo(path);
        while (current is not null)
        {
            current.Refresh();
            if (!current.Exists)
                throw directory && current.FullName == path
                    ? new DirectoryNotFoundException()
                    : new FileNotFoundException();
            if (IsUnsafe(current.Attributes))
                throw new LocalPathException();
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        if (directory != Directory.Exists(path))
            throw new LocalPathException();
    }

    internal static bool IsUnsafe(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0;

    internal static bool IsAcceptedDriveType(DriveType type) =>
        type is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
}

internal static class LocalPathRules
{
    internal static string NormalizeRoot(string path)
    {
        try
        {
            if (path.Length is < 4 or > 1024 || !Path.IsPathFullyQualified(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) ||
                path.Contains('\0'))
                throw new LocalPathException();
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!string.Equals(full, path, StringComparison.Ordinal) ||
                full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!))
                throw new LocalPathException();
            if (OperatingSystem.IsWindows() &&
                (full[1] != ':' || full[2..].Contains(':')))
                throw new LocalPathException();
            return full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new LocalPathException();
        }
    }

    internal static string Combine(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal) ||
            path.IndexOf(':', OperatingSystem.IsWindows() ? 2 : 0) >= 0)
            throw new LocalPathException();
        return path;
    }
}

internal sealed class LocalPathException : Exception;
