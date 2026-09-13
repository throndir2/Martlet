using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Host.Doctor;

public enum LocalFile
{
    OsRelease, KernelRelease, CpuInfo, MemoryInfo, Cgroup, ContainerMarker, PodmanMarker,
    DockerBinary, ComposeLibexec, ComposeLib, ToolkitBinary, DockerSocket, SelfStatus, Groups,
    NetworkDev, FibTrie, Inet6, Tcp, Tcp6, Udp, Udp6, RebootMarker
}
public sealed record FileResult(ReadStatus Status, string Text, DateTimeOffset StartedAt, double DurationMilliseconds);
public sealed record DiskResult(ReadStatus Status, DiskData? Data, DateTimeOffset StartedAt, double DurationMilliseconds);
public sealed record HostSnapshot(Provenance Provenance, DateTimeOffset StartedAt, PlatformData Runtime,
    ImmutableDictionary<LocalFile, FileResult> Files, ImmutableDictionary<CommandKind, CommandResult> Commands, DiskResult Disk);

public interface IHostSource
{
    Task<HostSnapshot> CaptureAsync(DoctorScope scope, bool queryNvidia, CancellationToken cancellationToken);
}

public static class HostSources
{
    public static string Description(ProbeId id) => id switch
    {
        ProbeId.Platform => "runtime architecture; /etc/os-release; /proc/sys/kernel/osrelease",
        ProbeId.Context => "kernel; /proc/1/cgroup; /.dockerenv; /run/.containerenv (indicators only)",
        ProbeId.Cpu => "/proc/cpuinfo (selected flags and logical count)",
        ProbeId.Memory => "/proc/meminfo (selected KiB counters converted to bytes)",
        ProbeId.Disk => "filesystem API: / (available to caller)",
        ProbeId.Nvidia => "/usr/bin/nvidia-smi (fixed name, driver_version, memory.total query)",
        ProbeId.DockerEngine or ProbeId.Compose or ProbeId.ContainerToolkit => "fixed dpkg-query package metadata and system executable-file metadata; no tool/plugin execution",
        ProbeId.DockerAccess => "/proc/self/status; /etc/group docker GID; standard socket metadata; no daemon connection",
        ProbeId.Network => "/proc/net/dev; /proc/net/fib_trie; /proc/net/if_inet6 (counts only)",
        ProbeId.GatewayPort => "/proc/net/tcp,tcp6,udp,udp6 (current namespace, no bind)",
        ProbeId.LocalClock => "local runtime UTC clock only",
        ProbeId.Reboot => "/run/reboot-required marker only",
        _ => "not executed: future selected/consented qualification"
    };

    internal static (string Path, int MaximumBytes, bool Metadata) Spec(LocalFile file) => file switch
    {
        LocalFile.OsRelease => ("/etc/os-release", 16384, false),
        LocalFile.KernelRelease => ("/proc/sys/kernel/osrelease", 512, false),
        LocalFile.CpuInfo => ("/proc/cpuinfo", 2 * 1024 * 1024, false),
        LocalFile.MemoryInfo => ("/proc/meminfo", 32768, false),
        LocalFile.Cgroup => ("/proc/1/cgroup", 32768, false),
        LocalFile.ContainerMarker => ("/.dockerenv", 0, true),
        LocalFile.PodmanMarker => ("/run/.containerenv", 0, true),
        LocalFile.DockerBinary => ("/usr/bin/docker", 0, true),
        LocalFile.ComposeLibexec => ("/usr/libexec/docker/cli-plugins/docker-compose", 0, true),
        LocalFile.ComposeLib => ("/usr/lib/docker/cli-plugins/docker-compose", 0, true),
        LocalFile.ToolkitBinary => ("/usr/bin/nvidia-ctk", 0, true),
        LocalFile.DockerSocket => ("/var/run/docker.sock", 0, true),
        LocalFile.SelfStatus => ("/proc/self/status", 32768, false),
        LocalFile.Groups => ("/etc/group", 65536, false),
        LocalFile.NetworkDev => ("/proc/net/dev", 32768, false),
        LocalFile.FibTrie => ("/proc/net/fib_trie", 262144, false),
        LocalFile.Inet6 => ("/proc/net/if_inet6", 32768, false),
        LocalFile.Tcp => ("/proc/net/tcp", 262144, false),
        LocalFile.Tcp6 => ("/proc/net/tcp6", 262144, false),
        LocalFile.Udp => ("/proc/net/udp", 262144, false),
        LocalFile.Udp6 => ("/proc/net/udp6", 262144, false),
        LocalFile.RebootMarker => ("/run/reboot-required", 0, true),
        _ => throw new ArgumentOutOfRangeException(nameof(file))
    };
}

public sealed class LocalHostSource : IHostSource
{
    private readonly ICommandRunner commands;
    public LocalHostSource(ICommandRunner? commands = null) => this.commands = commands ?? new BoundedCommandRunner();

    public async Task<HostSnapshot> CaptureAsync(DoctorScope scope, bool queryNvidia, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        var start = DateTimeOffset.UtcNow;
        var runtime = new PlatformData(OperatingSystem.IsLinux() ? Distro.Unknown : Distro.NonLinux,
            null, null, KernelFlavor.Unknown, RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(), OperatingSystem.IsLinux());
        var files = ImmutableDictionary.CreateBuilder<LocalFile, FileResult>();
        var results = ImmutableDictionary.CreateBuilder<CommandKind, CommandResult>();
        var disk = new DiskResult(ReadStatus.NotRun, null, start, 0);
        if (!runtime.Linux || runtime.OsArchitecture != "X64" || runtime.ProcessArchitecture != "X64")
            return new(Provenance.LiveLocal, start, runtime, files.ToImmutable(), results.ToImmutable(), disk);
        foreach (var file in Enum.GetValues<LocalFile>())
        {
            if (scope == DoctorScope.Inventory && file is LocalFile.DockerBinary or LocalFile.ComposeLibexec or
                LocalFile.ComposeLib or LocalFile.ToolkitBinary or LocalFile.DockerSocket or LocalFile.SelfStatus or LocalFile.Groups)
                continue;
            files.Add(file, await ReadAsync(file, cancellationToken).ConfigureAwait(false));
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            var diskStart = DateTimeOffset.UtcNow;
            var watch = Stopwatch.StartNew();
            try
            {
                var drive = new DriveInfo("/");
                var data = new DiskData(drive.TotalSize, drive.AvailableFreeSpace);
                disk = new(cancellationToken.IsCancellationRequested ? ReadStatus.Canceled : ReadStatus.Success,
                    cancellationToken.IsCancellationRequested ? null : data, diskStart, watch.Elapsed.TotalMilliseconds);
            }
            catch (UnauthorizedAccessException) { disk = new(ReadStatus.PermissionDenied, null, diskStart, watch.Elapsed.TotalMilliseconds); }
            catch (IOException) { disk = new(ReadStatus.IoError, null, diskStart, watch.Elapsed.TotalMilliseconds); }
        }
        if (scope == DoctorScope.Prerequisites)
        {
            results.Add(CommandKind.PackageVersions, await commands.RunAsync(CommandKind.PackageVersions, cancellationToken).ConfigureAwait(false));
            if (queryNvidia)
                results.Add(CommandKind.NvidiaQuery, await commands.RunAsync(CommandKind.NvidiaQuery, cancellationToken).ConfigureAwait(false));
        }
        return new(Provenance.LiveLocal, start, runtime, files.ToImmutable(), results.ToImmutable(), disk);
    }

    private static async Task<FileResult> ReadAsync(LocalFile file, CancellationToken original)
    {
        var (path, maximum, metadata) = HostSources.Spec(file);
        var start = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        FileResult Result(ReadStatus status, string text = "") => new(status, text, start, watch.Elapsed.TotalMilliseconds);
        if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
        try
        {
            if (metadata)
            {
                var attributes = File.GetAttributes(path);
                if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
                return Result((attributes & FileAttributes.Directory) != 0 ? ReadStatus.Malformed : ReadStatus.Success);
            }
            // O_NONBLOCK prevents a misconfigured known path pointing at a FIFO from blocking open().
            // Files are never sourced; only bounded UTF-8 text from this fixed registry is parsed.
            if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
            using var handle = OpenReadOnly(path);
            using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(original, timeout.Token);
            var bytes = new byte[maximum + 1];
            var count = 0;
            while (count <= maximum)
            {
                if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
                if (timeout.IsCancellationRequested || watch.Elapsed >= TimeSpan.FromSeconds(2)) return Result(ReadStatus.Timeout);
                var read = await stream.ReadAsync(bytes.AsMemory(count), stop.Token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
            if (timeout.IsCancellationRequested || watch.Elapsed >= TimeSpan.FromSeconds(2)) return Result(ReadStatus.Timeout);
            if (count > maximum) return Result(ReadStatus.OutputLimit);
            return Result(ReadStatus.Success, new UTF8Encoding(false, true).GetString(bytes, 0, count));
        }
        catch (OperationCanceledException) { return Result(original.IsCancellationRequested ? ReadStatus.Canceled : ReadStatus.Timeout); }
        catch (UnauthorizedAccessException) { return Result(ReadStatus.PermissionDenied); }
        catch (FileNotFoundException) { return Result(ReadStatus.Missing); }
        catch (DirectoryNotFoundException) { return Result(ReadStatus.Missing); }
        catch (DecoderFallbackException) { return Result(ReadStatus.Malformed); }
        catch (IOException) { return Result(ReadStatus.IoError); }
    }

    private static SafeFileHandle OpenReadOnly(string path)
    {
        var fd = Open(path, 0x800 | 0x80000); // O_RDONLY | O_NONBLOCK | O_CLOEXEC on the Linux x64 target.
        if (fd >= 0) return new SafeFileHandle(fd, ownsHandle: true);
        throw Marshal.GetLastPInvokeError() switch
        {
            2 => new FileNotFoundException("Known local input missing."),
            13 => new UnauthorizedAccessException("Known local input denied."),
            _ => new IOException("Known local input cannot be opened.")
        };
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
