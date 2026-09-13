using System.Collections.Immutable;

namespace Martlet.Host.Doctor;

public static class HostEvaluator
{
    public static HostReport Evaluate(HostSnapshot snapshot, DoctorScope scope, int port, DateTimeOffset now, bool canceled = false)
    {
        if (!Enum.IsDefined(scope) || port is < 1 or > 65535 || !Enum.IsDefined(snapshot.Provenance) ||
            snapshot.Provenance == Provenance.NotObserved || snapshot.StartedAt > now)
            throw new InvalidDataException("Invalid host evaluation input.");
        ValidateInput(snapshot);
        var probes = new Dictionary<ProbeId, ProbeResult>();
        void Add(ProbeId id, FindingCode code, Evidence? evidence = null, DateTimeOffset? observed = null, double duration = 0)
        {
            var unobserved = code is FindingCode.HOST_NOT_RUN or FindingCode.HOST_CANCELED;
            DateTimeOffset? at = unobserved ? null : observed ?? snapshot.StartedAt;
            probes[id] = new(id, HostReport.RequiredInScope(id, scope), code, unobserved ? Provenance.NotObserved : snapshot.Provenance,
                at, duration, at is null ? null : Math.Max(0, (now - at.Value).TotalMilliseconds), HostSources.Description(id), evidence);
        }
        FileResult File(LocalFile file) => snapshot.Files.TryGetValue(file, out var result)
            ? result : new(ReadStatus.NotRun, "", snapshot.StartedAt, 0);
        bool FilesReady(ProbeId id, params LocalFile[] files)
        {
            foreach (var file in files)
            {
                var read = File(file);
                if (read.Status == ReadStatus.Success) continue;
                Add(id, read.Status == ReadStatus.Missing ? FindingCode.HOST_INCOMPLETE : Parsers.Code(read.Status),
                    observed: read.StartedAt, duration: read.DurationMilliseconds);
                return false;
            }
            return true;
        }
        void FromFiles<T>(ProbeId id, Parsed<T> parsed, Func<T, Evidence> evidence, params LocalFile[] files) where T : class =>
            Add(id, parsed.Code, parsed.Value is null ? null : evidence(parsed.Value),
                files.Min(f => File(f).StartedAt), files.Sum(f => File(f).DurationMilliseconds));
        CommandResult Command(CommandKind kind) => snapshot.Commands.TryGetValue(kind, out var result)
            ? result : new(ReadStatus.NotRun, "", null, snapshot.StartedAt, 0);
        BinaryPresence Presence(params LocalFile[] files)
        {
            var states = files.Select(f => File(f).Status).ToArray();
            return states.Contains(ReadStatus.Success) ? BinaryPresence.Present
                : states.All(s => s == ReadStatus.Missing) ? BinaryPresence.Missing : BinaryPresence.Unknown;
        }

        if (canceled) Add(ProbeId.Platform, FindingCode.HOST_CANCELED);
        else if (!snapshot.Runtime.Linux || snapshot.Runtime.OsArchitecture != "X64" || snapshot.Runtime.ProcessArchitecture != "X64")
            Add(ProbeId.Platform, FindingCode.HOST_UNSUPPORTED_EXECUTION, new() { Platform = snapshot.Runtime });
        else
        {
            PlatformData platform = snapshot.Runtime;
            if (FilesReady(ProbeId.Platform, LocalFile.OsRelease, LocalFile.KernelRelease))
            {
                var parsed = Parsers.Platform(snapshot.Runtime, File(LocalFile.OsRelease).Text, File(LocalFile.KernelRelease).Text);
                FromFiles(ProbeId.Platform, parsed, p => new() { Platform = p }, LocalFile.OsRelease, LocalFile.KernelRelease);
                platform = parsed.Value ?? platform;
            }
            if (FilesReady(ProbeId.Context, LocalFile.Cgroup))
            {
                var markers = new[] { File(LocalFile.ContainerMarker), File(LocalFile.PodmanMarker) };
                var failed = markers.FirstOrDefault(m => m.Status is not (ReadStatus.Success or ReadStatus.Missing));
                if (failed is not null) Add(ProbeId.Context, Parsers.Code(failed.Status), observed: failed.StartedAt, duration: failed.DurationMilliseconds);
                else
                {
                    FromFiles(ProbeId.Context, Parsers.Context(platform.KernelFlavor, File(LocalFile.Cgroup).Text,
                        markers.Any(m => m.Status == ReadStatus.Success)), p => new() { Context = p },
                        LocalFile.Cgroup, LocalFile.ContainerMarker, LocalFile.PodmanMarker);
                }
            }
            if (FilesReady(ProbeId.Cpu, LocalFile.CpuInfo))
                FromFiles(ProbeId.Cpu, Parsers.Cpu(File(LocalFile.CpuInfo).Text), p => new() { Cpu = p }, LocalFile.CpuInfo);
            if (FilesReady(ProbeId.Memory, LocalFile.MemoryInfo))
                FromFiles(ProbeId.Memory, Parsers.Memory(File(LocalFile.MemoryInfo).Text), p => new() { Memory = p }, LocalFile.MemoryInfo);
            Add(ProbeId.Disk, Parsers.Code(snapshot.Disk.Status),
                snapshot.Disk.Data is null ? null : new() { Disk = snapshot.Disk.Data }, snapshot.Disk.StartedAt, snapshot.Disk.DurationMilliseconds);
            if (FilesReady(ProbeId.Network, LocalFile.NetworkDev, LocalFile.FibTrie, LocalFile.Inet6))
                FromFiles(ProbeId.Network, Parsers.Network(File(LocalFile.NetworkDev).Text, File(LocalFile.FibTrie).Text, File(LocalFile.Inet6).Text),
                    p => new() { Network = p }, LocalFile.NetworkDev, LocalFile.FibTrie, LocalFile.Inet6);
            LocalFile[] ports = [LocalFile.Tcp, LocalFile.Tcp6, LocalFile.Udp, LocalFile.Udp6];
            if (FilesReady(ProbeId.GatewayPort, ports))
                FromFiles(ProbeId.GatewayPort, Parsers.Port(port, ports.Select(p => File(p).Text).ToArray()), p => new() { Port = p }, ports);
            Add(ProbeId.LocalClock, FindingCode.HOST_OBSERVED, new() { Clock = new(snapshot.StartedAt) });
            var reboot = File(LocalFile.RebootMarker);
            Add(ProbeId.Reboot, reboot.Status switch
            {
                ReadStatus.Success => FindingCode.HOST_REBOOT_PENDING,
                ReadStatus.Missing => FindingCode.HOST_OBSERVED,
                _ => Parsers.Code(reboot.Status)
            }, reboot.Status is ReadStatus.Success or ReadStatus.Missing ? new() { Reboot = new(reboot.Status == ReadStatus.Success) } : null,
                reboot.StartedAt, reboot.DurationMilliseconds);
            if (scope == DoctorScope.Prerequisites)
            {
                var nvidia = Command(CommandKind.NvidiaQuery);
                var gpu = Parsers.Nvidia(nvidia);
                Add(ProbeId.Nvidia, gpu.Code, gpu.Value is null ? null : new() { Gpus = gpu.Value.Gpus }, nvidia.StartedAt, nvidia.DurationMilliseconds);
                var packageCommand = Command(CommandKind.PackageVersions);
                var packages = Parsers.Packages(packageCommand);
                void Tool(ProbeId id, ToolKind kind, string[] names, params LocalFile[] paths)
                {
                    var presence = Presence(paths);
                    var matches = packages.Value?.Versions.Where(p => names.Contains(p.Key)).ToArray();
                    var version = matches is { Length: 1 } ? matches[0].Value : null;
                    var state = matches is null ? PackagePresence.Unknown : matches.Length > 0 ? PackagePresence.Installed : PackagePresence.NotInstalled;
                    var code = packages.Code == FindingCode.HOST_MISSING ? FindingCode.HOST_INCOMPLETE
                        : packages.Code != FindingCode.HOST_OBSERVED ? packages.Code
                        : presence == BinaryPresence.Unknown || matches is { Length: > 1 } ? FindingCode.HOST_INCOMPLETE
                        : state == PackagePresence.NotInstalled && presence == BinaryPresence.Present ? FindingCode.HOST_INCOMPLETE
                        : state == PackagePresence.NotInstalled || presence == BinaryPresence.Missing ? FindingCode.HOST_MISSING
                        : kind == ToolKind.Compose && version?.Split('.')[0] != "2" ? FindingCode.HOST_UNSUPPORTED_RESPONSE
                        : FindingCode.HOST_OBSERVED;
                    var failed = paths.Select(File).FirstOrDefault(p => p.Status is ReadStatus.PermissionDenied or ReadStatus.Canceled or ReadStatus.Timeout);
                    if (failed is not null && presence != BinaryPresence.Present) code = Parsers.Code(failed.Status);
                    Add(id, code, code == FindingCode.HOST_NOT_RUN ? null : new() { Tool = new(kind, state, presence, version) },
                        packageCommand.StartedAt, packageCommand.DurationMilliseconds + paths.Sum(p => File(p).DurationMilliseconds));
                }
                Tool(ProbeId.DockerEngine, ToolKind.DockerEngine, ["docker-ce", "docker.io"], LocalFile.DockerBinary);
                Tool(ProbeId.Compose, ToolKind.Compose, ["docker-compose-plugin", "docker-compose-v2"], LocalFile.ComposeLibexec, LocalFile.ComposeLib);
                Tool(ProbeId.ContainerToolkit, ToolKind.ContainerToolkit, ["nvidia-container-toolkit"], LocalFile.ToolkitBinary);
                if (FilesReady(ProbeId.DockerAccess, LocalFile.SelfStatus, LocalFile.Groups))
                    FromFiles(ProbeId.DockerAccess, Parsers.DockerAccess(File(LocalFile.SelfStatus).Text, File(LocalFile.Groups).Text, Presence(LocalFile.DockerSocket)),
                        p => new() { DockerAccess = p }, LocalFile.SelfStatus, LocalFile.Groups, LocalFile.DockerSocket);
                Add(ProbeId.QualifiedTuple, FindingCode.HOST_UNQUALIFIED_VERSIONS);
            }
        }
        foreach (var id in Enum.GetValues<ProbeId>())
            if (!probes.ContainsKey(id)) Add(id, FindingCode.HOST_NOT_RUN);
        var report = new HostReport(1, "0.1.0", CandidateManifest.Current.Id, scope, snapshot.Provenance, now,
            Enum.GetValues<ProbeId>().Select(id => probes[id]).ToImmutableArray());
        report.Validate();
        return report;
    }

    private static void ValidateInput(HostSnapshot snapshot)
    {
        if (snapshot.Files.Count > Enum.GetValues<LocalFile>().Length || snapshot.Commands.Count > 2)
            throw new InvalidDataException("Host input exceeds the bounded registry.");
        foreach (var pair in snapshot.Files)
        {
            if (!Enum.IsDefined(pair.Key) || !Enum.IsDefined(pair.Value.Status) ||
                pair.Value.Text.Length > HostSources.Spec(pair.Key).MaximumBytes ||
                pair.Value.Status != ReadStatus.Success && pair.Value.Text.Length != 0)
                throw new InvalidDataException("Invalid bounded file input.");
        }
        foreach (var pair in snapshot.Commands)
        {
            if (!Enum.IsDefined(pair.Key) || !Enum.IsDefined(pair.Value.Status) ||
                pair.Value.Output.Length > BoundedCommandRunner.MaximumStdoutBytes ||
                pair.Value.Status != ReadStatus.Success && pair.Value.Output.Length != 0)
                throw new InvalidDataException("Invalid bounded command input.");
        }
    }
}
