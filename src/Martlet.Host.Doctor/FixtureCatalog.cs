using System.Collections.Immutable;

namespace Martlet.Host.Doctor;

public static class FixtureCatalog
{
    public static ImmutableArray<string> Names { get; } =
    [
        "inventory", "prerequisites", "missing-tools", "denied", "unsupported-response", "timeout", "canceled",
        "malformed", "oversized", "io-error", "command-failed", "driver-unavailable", "gpu-not-visible",
        "wrong-os", "windows", "container", "port-in-use", "reboot", "docker-root", "incomplete", "not-run"
    ];
    public static DateTimeOffset Timestamp { get; } = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

    public static HostSnapshot Create(string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Unknown authored fixture.", nameof(name));
        FileResult Text(string value) => new(ReadStatus.Success, value, Timestamp, 1);
        FileResult Missing() => new(ReadStatus.Missing, "", Timestamp, 1);
        var files = Enum.GetValues<LocalFile>().ToImmutableDictionary(f => f, _ => Missing()).ToBuilder();
        files[LocalFile.OsRelease] = Text("NAME=\"Ubuntu\"\nID=ubuntu\nVERSION_ID=\"24.04\"\n");
        files[LocalFile.KernelRelease] = Text("6.8.0-99-generic\n");
        files[LocalFile.Cgroup] = Text("0::/\n");
        files[LocalFile.CpuInfo] = Text("processor : 0\nflags : sse4_2 avx avx2\nprocessor : 1\nflags : sse4_2 avx avx2\n");
        files[LocalFile.MemoryInfo] = Text("MemTotal: 8388608 kB\nMemAvailable: 4194304 kB\nSwapTotal: 1048576 kB\nSwapFree: 1048576 kB\n");
        files[LocalFile.NetworkDev] = Text("Inter-| Receive | Transmit\n face |bytes packets|bytes packets\n lo: 0 0\n eth0: 0 0\n");
        files[LocalFile.FibTrie] = Text("Main:\n |-- 127.0.0.1\n /32 host LOCAL\n |-- 192.168.20.30\n /32 host LOCAL\n");
        files[LocalFile.Inet6] = Text("00000000000000000000000000000001 01 80 10 80 lo\n");
        foreach (var id in new[] { LocalFile.Tcp, LocalFile.Tcp6, LocalFile.Udp, LocalFile.Udp6 })
            files[id] = Text("  sl  local_address rem_address st\n");
        files[LocalFile.SelfStatus] = Text("Uid:\t1000\t1000\t1000\t1000\nGid:\t1000\t1000\t1000\t1000\nGroups:\t1000\n");
        files[LocalFile.Groups] = Text("docker:x:999:\n");
        foreach (var id in new[] { LocalFile.DockerBinary, LocalFile.ComposeLibexec, LocalFile.ToolkitBinary, LocalFile.DockerSocket })
            files[id] = Text("");
        var commands = ImmutableDictionary.CreateBuilder<CommandKind, CommandResult>();
        commands[CommandKind.PackageVersions] = new(ReadStatus.Success,
            "docker-ce\t5:28.0.4-1~ubuntu.24.04~noble\tinstalled\ndocker-compose-plugin\t2.39.4-1~ubuntu.24.04~noble\tinstalled\nnvidia-container-toolkit\t1.17.8-1\tinstalled\n",
            1, Timestamp, 2);
        commands[CommandKind.NvidiaQuery] = new(ReadStatus.Success, "NVIDIA GeForce RTX 4090, 570.124.06, 24564\n", 0, Timestamp, 2);
        var runtime = new PlatformData(Distro.Unknown, null, null, KernelFlavor.Unknown, "X64", "X64", "10.0.9", true);
        switch (name)
        {
            case "inventory": commands.Clear(); break;
            case "missing-tools":
                commands[CommandKind.PackageVersions] = new(ReadStatus.Success, "", 1, Timestamp, 2);
                commands[CommandKind.NvidiaQuery] = new(ReadStatus.Missing, "", null, Timestamp, 2);
                foreach (var id in new[] { LocalFile.DockerBinary, LocalFile.ComposeLibexec, LocalFile.ToolkitBinary }) files[id] = Missing();
                break;
            case "denied": commands[CommandKind.NvidiaQuery] = new(ReadStatus.Success, "", 4, Timestamp, 2); break;
            case "unsupported-response": commands[CommandKind.NvidiaQuery] = new(ReadStatus.Success, "NVIDIA RTX 4090, 570.124.06, [Not Supported]\n", 0, Timestamp, 2); break;
            case "timeout": commands[CommandKind.NvidiaQuery] = new(ReadStatus.Timeout, "", null, Timestamp, 5000); break;
            case "canceled": commands[CommandKind.NvidiaQuery] = new(ReadStatus.Canceled, "", null, Timestamp, 2); break;
            case "malformed": commands[CommandKind.NvidiaQuery] = new(ReadStatus.Success, "$(secret), 570.124.06, 24564\n", 0, Timestamp, 2); break;
            case "oversized": commands[CommandKind.NvidiaQuery] = new(ReadStatus.OutputLimit, "", null, Timestamp, 2); break;
            case "io-error": files[LocalFile.MemoryInfo] = new(ReadStatus.IoError, "", Timestamp, 1); break;
            case "command-failed": commands[CommandKind.NvidiaQuery] = new(ReadStatus.Success, "", 255, Timestamp, 2); break;
            case "driver-unavailable": commands[CommandKind.NvidiaQuery] = new(ReadStatus.Success, "", 9, Timestamp, 2); break;
            case "gpu-not-visible": commands[CommandKind.NvidiaQuery] = new(ReadStatus.Success, "", 6, Timestamp, 2); break;
            case "wrong-os": files[LocalFile.OsRelease] = Text("ID=ubuntu\nVERSION_ID=\"26.04\"\n"); break;
            case "windows": runtime = runtime with { Linux = false, Distribution = Distro.NonLinux }; break;
            case "container": files[LocalFile.ContainerMarker] = Text(""); break;
            case "port-in-use": files[LocalFile.Tcp] = Text("  sl  local_address rem_address st\n0: 00000000:1D13 00000000:0000 0A\n"); break;
            case "reboot": files[LocalFile.RebootMarker] = Text(""); break;
            case "docker-root": files[LocalFile.SelfStatus] = Text("Uid:\t0\t0\t0\t0\nGid:\t0\t0\t0\t0\nGroups:\t0 999\n"); break;
            case "incomplete": files[LocalFile.DockerBinary] = Missing(); commands[CommandKind.PackageVersions] = new(ReadStatus.Success, "", 1, Timestamp, 2); break;
            case "not-run": commands.Remove(CommandKind.NvidiaQuery); break;
        }
        return new(Provenance.AuthoredFixture, Timestamp, runtime, files.ToImmutable(), commands.ToImmutable(),
            new(ReadStatus.Success, new(128L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024), Timestamp, 1));
    }
}
