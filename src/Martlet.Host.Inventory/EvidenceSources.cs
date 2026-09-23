namespace Martlet.Host.Inventory;
public static class EvidenceSources
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
        ProbeId.QualifiedTuple or ProbeId.ContainerGpu or ProbeId.ModelInference or ProbeId.ClockAccuracy or ProbeId.PairingFirewall =>
            "not executed: future selected/consented qualification",
        _ => throw new InvalidDataException("Unknown host probe.")
    };
}
