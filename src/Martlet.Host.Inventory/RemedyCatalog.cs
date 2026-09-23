namespace Martlet.Host.Inventory;

public sealed record Remedy(ActionId Action, string Instruction, string OfficialUrl, string Privilege,
    string Download, string Reboot);
public sealed record Finding(FindingState State, string Summary);

public static class RemedyCatalog
{
    public static Finding Get(FindingCode code) => code switch
    {
        FindingCode.HOST_OBSERVED => new(FindingState.Observed, "Local metadata observed; this is not deployment readiness."),
        FindingCode.HOST_MISSING => new(FindingState.Missing, "A requested prerequisite is missing at the checked standard location."),
        FindingCode.HOST_PERMISSION_DENIED => new(FindingState.Unknown, "Permission denied; the prerequisite has not been verified."),
        FindingCode.HOST_UNSUPPORTED_EXECUTION => new(FindingState.Unsupported, "Live inventory requires Linux x86_64. Use help or an authored fixture here."),
        FindingCode.HOST_UNQUALIFIED_PLATFORM => new(FindingState.Unknown, "This distribution/version/context is not the initial Ubuntu 24.04 x86_64 target."),
        FindingCode.HOST_INCOMPLETE => new(FindingState.Unknown, "Local metadata is insufficient or installation metadata and file presence disagree."),
        FindingCode.HOST_UNSUPPORTED_RESPONSE => new(FindingState.Unknown, "The installed tool or driver cannot provide the approved query in the expected format."),
        FindingCode.HOST_TIMEOUT => new(FindingState.Unknown, "The bounded read-only operation exceeded its deadline."),
        FindingCode.HOST_CANCELED => new(FindingState.Unknown, "The operation was canceled; no successful observation was accepted."),
        FindingCode.HOST_MALFORMED => new(FindingState.Unknown, "The response is malformed or contains fields outside the safe report contract."),
        FindingCode.HOST_OUTPUT_LIMIT => new(FindingState.Unknown, "The response exceeded its byte/item limit; partial evidence was not accepted."),
        FindingCode.HOST_IO_ERROR => new(FindingState.Unknown, "Local data could not be read; native error text is intentionally omitted."),
        FindingCode.HOST_COMMAND_FAILED => new(FindingState.Unknown, "The approved local query failed; native error text is intentionally omitted."),
        FindingCode.HOST_PORT_IN_USE => new(FindingState.Missing, "The proposed gateway port is occupied in this network namespace."),
        FindingCode.HOST_NOT_RUN => new(FindingState.NotRun, "Not run. This check needs a later scope, selected artifacts, or separate consent."),
        FindingCode.HOST_UNQUALIFIED_VERSIONS => new(FindingState.Unknown, "No GPU/driver/image/framework/model tuple is Martlet-qualified yet."),
        FindingCode.HOST_DOCKER_PRIVILEGE => new(FindingState.Warning, "Root identity or Docker group membership observed: Docker access can be root-equivalent."),
        FindingCode.HOST_DRIVER_UNAVAILABLE => new(FindingState.Unknown, "NVIDIA reports that its driver or management library is unavailable."),
        FindingCode.HOST_GPU_NOT_VISIBLE => new(FindingState.Unknown, "No NVIDIA GPU is visible to the approved driver query."),
        FindingCode.HOST_REBOOT_PENDING => new(FindingState.Warning, "Ubuntu's reboot-required marker is present; no reboot was performed."),
        _ => throw new InvalidDataException("Unknown diagnostic code.")
    };

    public static Remedy For(ProbeId probe, FindingCode code)
    {
        if (!Enum.IsDefined(probe)) throw new InvalidDataException("Unknown host probe.");
        _ = Get(code);
        var url = probe switch
        {
            ProbeId.Nvidia => "https://documentation.ubuntu.com/server/how-to/graphics/install-nvidia-drivers/",
            ProbeId.DockerEngine or ProbeId.DockerAccess => "https://docs.docker.com/engine/install/ubuntu/",
            ProbeId.Compose => "https://docs.docker.com/compose/install/linux/",
            ProbeId.ContainerToolkit or ProbeId.ContainerGpu => "https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html",
            ProbeId.ClockAccuracy or ProbeId.LocalClock => "https://documentation.ubuntu.com/server/how-to/networking/timedatectl-and-timesyncd/",
            ProbeId.Network or ProbeId.GatewayPort or ProbeId.PairingFirewall => "https://documentation.ubuntu.com/server/how-to/security/firewalls/",
            ProbeId.ModelInference or ProbeId.QualifiedTuple => "https://docs.nvidia.com/deploy/cuda-compatibility/minor-version-compatibility.html",
            _ => "https://documentation.ubuntu.com/server/"
        };
        var instruction = probe switch
        {
            ProbeId.Platform or ProbeId.Context => "Review the Ubuntu 24.04 LTS x86_64 target. Other releases and container/WSL observations do not qualify a physical host. Do not reinstall or upgrade just for this report.",
            ProbeId.Nvidia => "On Ubuntu 24.04, review Ubuntu's distro-packaged NVIDIA driver guide with the administrator. Match the actual GPU and future H02 image; no driver floor is selected. Preserve Secure Boot and existing driver policy.",
            ProbeId.DockerEngine => "Review Docker's Ubuntu 24.04 Engine instructions and existing package conflicts with the administrator. Do not remove an existing engine/team configuration. This checks package metadata, not a running daemon.",
            ProbeId.Compose => "Review the Compose v2 system plugin instructions for the existing Engine installation. A user-local plugin is deliberately not executed or inspected. Exact tested version awaits H02.",
            ProbeId.ContainerToolkit => "Review NVIDIA's Ubuntu/Debian toolkit instructions with the administrator after H02 selects a compatible tuple. Do not run nvidia-ctk configure or restart Docker as part of preflight.",
            ProbeId.DockerAccess => "Daemon access is UNKNOWN: no socket connection or Docker context command was made. Review existing access policy with the administrator; never automatically add Docker groups or grant socket permissions.",
            ProbeId.Cpu or ProbeId.Memory => "These are current CPU and byte-count observations, not model fit. The plan's 4 cores / 32 GiB are recommendations, not enforced minima. H02 must select workloads before capacity decisions.",
            ProbeId.Disk => "This is available space on / only, not a selected model directory or download budget. Inodes and per-model staging/rollback needs are UNKNOWN. Review storage after H02 selects exact artifacts.",
            ProbeId.Network => "Only interface/address-shape counts in the current namespace are reported. No addresses, names, DNS requests, LAN scans, or reachability checks are exported. Confirm the intended private LAN manually later.",
            ProbeId.GatewayPort => "Review the chosen port with the administrator, or rerun with --port 7444 (or another agreed unused port). Do not stop unrelated services or change firewall rules. No observed collision is NOT gateway availability.",
            ProbeId.LocalClock or ProbeId.ClockAccuracy => "The local UTC clock is observed, not externally verified. Review timedatectl/time synchronization with the administrator later; this tool contacts no time or TLS service.",
            ProbeId.Reboot => "Review Ubuntu's pending reboot notice and schedule any reboot with the owner after saving work. Absence of the marker does not prove driver readiness or a successful reboot.",
            _ => "Await H02's selected, rights-reviewed artifact tuple and H03/H06 consented hardware/network evidence. GPU visibility does not prove CUDA kernels, PyTorch, container GPU access, model fit, inference, pairing, or firewall isolation."
        };
        var action = code switch
        {
            FindingCode.HOST_OBSERVED => ActionId.ReviewObservation,
            FindingCode.HOST_UNSUPPORTED_EXECUTION or FindingCode.HOST_UNQUALIFIED_PLATFORM => ActionId.ReviewTarget,
            FindingCode.HOST_PERMISSION_DENIED => ActionId.ReviewAccess,
            FindingCode.HOST_TIMEOUT or FindingCode.HOST_CANCELED or FindingCode.HOST_OUTPUT_LIMIT or FindingCode.HOST_MALFORMED or FindingCode.HOST_IO_ERROR or FindingCode.HOST_COMMAND_FAILED => ActionId.RetryReadOnly,
            FindingCode.HOST_PORT_IN_USE => ActionId.ReviewPort,
            FindingCode.HOST_NOT_RUN or FindingCode.HOST_UNQUALIFIED_VERSIONS => ActionId.AwaitQualification,
            FindingCode.HOST_DOCKER_PRIVILEGE => ActionId.ReviewDockerPrivilege,
            FindingCode.HOST_REBOOT_PENDING => ActionId.ReviewReboot,
            _ => ActionId.ReviewPrerequisite
        };
        if (action == ActionId.RetryReadOnly)
            instruction = "Rerun the same read-only command once. If it persists, share the redacted code and consult the administrator; do not paste raw native output. " + instruction;
        if (action == ActionId.ReviewAccess)
            instruction = "Do not rerun Martlet with sudo to hide a permission failure. Ask the administrator to review the denied prerequisite. " + instruction;
        if (action == ActionId.ReviewDockerPrivilege)
            url = "https://docs.docker.com/engine/install/linux-postinstall/";
        return new(action, instruction, url,
            "None for this read-only command. Manual package/configuration changes require separate administrator approval.",
            "None performed. Future package/image/model download sizes are unknown until selected; review before consent.",
            "None performed. Driver/package changes may require an owner-scheduled reboot.");
    }
}
