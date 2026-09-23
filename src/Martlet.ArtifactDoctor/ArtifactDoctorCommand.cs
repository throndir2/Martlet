using Martlet.HostArtifacts;

namespace Martlet.ArtifactDoctor;

public static class ArtifactDoctorCommand
{
    public const string Usage = """
        Usage: Martlet.ArtifactDoctor inspect --manifest ABSOLUTE_LOCAL_JSON_PATH [--role ROLE_ID] [--target TARGET_ID] [--platform OS/ARCH[/VARIANT]] [--json]
               Martlet.ArtifactDoctor --help | --version
        Reads only the selected bounded local JSON document. No settings, directory/artifact/cache discovery,
        network/URL resolution, Docker, OS/GPU probes, model loading, execution, downloads or writes.
        Target is supplied metadata, not an observed host. All candidates remain disabled and unqualified.
        This standalone developer tool is not part of the shipped Windows installer.
        Platform compares v2 image metadata only. Formats 1 and 2 are supported.
        Exit 0: help/version only; 1: declared target/platform mismatch; 2: disabled/incomplete/canceled inspection;
        3: invalid invocation/document or inaccessible/disallowed input. No inspection result is host-ready.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        if (args is ["--version"])
        {
            await output.WriteLineAsync("Martlet.ArtifactDoctor 0.1.0 (metadata-only formats 1 and 2)");
            return 0;
        }
        var json = args.Contains("--json", StringComparer.Ordinal);
        InspectionReport report;
        try
        {
            var selection = Parse(args);
            cancellationToken.ThrowIfCancellationRequested();
            var input = await ReadDocumentAsync(selection.Path, cancellationToken);
            report = ArtifactInspector.Inspect(ArtifactManifestReader.Read(input), selection.Role, selection.Target, selection.Platform);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (ArtifactManifestException error) { report = InspectionReport.Failure(error.DiagnosticCode); }
        catch (InputException error) { report = InspectionReport.Failure(error.Code); }
        catch (OperationCanceledException) { report = InspectionReport.Failure("inspection.canceled"); }
        catch (UnauthorizedAccessException) { report = InspectionReport.Failure("inspection.input_unreadable"); }
        catch (IOException) { report = InspectionReport.Failure("inspection.input_unreadable"); }
        catch (System.Security.SecurityException) { report = InspectionReport.Failure("inspection.input_unreadable"); }
        catch (ArgumentException) { report = InspectionReport.Failure("inspection.invalid_invocation"); }
        catch (NotSupportedException) { report = InspectionReport.Failure("inspection.input_disallowed"); }
        await output.WriteLineAsync(json ? report.ToJson() : report.ToHuman());
        return report.ExitCode;
    }

    private static Selection Parse(string[] args)
    {
        if (args.Length is < 3 or > 10 || args[0] != "inspect" ||
            args.Any(a => a is null || a.Length > 4096))
            throw new InputException("inspection.invalid_invocation");
        string? path = null, role = null, target = null, platform = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw new InputException("inspection.invalid_invocation");
            if (option == "--json") continue;
            if (i + 1 == args.Length) throw new InputException("inspection.invalid_invocation");
            switch (option)
            {
                case "--manifest": path = args[++i]; break;
                case "--role": role = args[++i]; break;
                case "--target": target = args[++i]; break;
                case "--platform": platform = args[++i]; break;
                default: throw new InputException("inspection.invalid_invocation");
            }
        }
        if (path is null) throw new InputException("inspection.invalid_invocation");
        return new(path, role, target, platform);
    }

    private static async Task<byte[]> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            Path.GetFullPath(path) != path || Path.GetExtension(path) != ".json" ||
            path.IndexOf(':', OperatingSystem.IsWindows() ? 2 : 0) >= 0)
            throw new InputException("inspection.input_disallowed");
        var file = new FileInfo(path);
        for (FileSystemInfo? current = file; current is not null;
            current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
        {
            current.Refresh();
            if (!current.Exists) throw new InputException("inspection.input_unreadable");
            if ((current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InputException("inspection.input_disallowed");
        }
        if ((file.Attributes & FileAttributes.Directory) != 0 || file.Length == 0)
            throw new InputException("inspection.input_disallowed");
        if (file.Length > ArtifactManifestReader.MaximumBytes)
            throw new InputException("manifest.too_large");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[ArtifactManifestReader.MaximumBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > ArtifactManifestReader.MaximumBytes) throw new InputException("manifest.too_large");
            return buffer[..length];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InputException("inspection.input_timeout");
        }
    }

    private sealed record Selection(string Path, string? Role, string? Target, string? Platform);
    private sealed class InputException(string code) : Exception
    {
        internal string Code { get; } = code;
    }
}
