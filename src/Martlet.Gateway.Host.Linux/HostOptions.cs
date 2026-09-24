using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

internal sealed record HostOptions(string Command, string ConfigPath)
{
    internal const string Help = """
        Martlet Linux gateway candidate (native x86_64/glibc/ext4; default No)
        No arguments, help, --help, -h: passive help; no file/key/listener access.
        Commands: validate, status, init, admin, rebind, serve, health
        Syntax: <command> --config <absolute-private-directory>/host.json
        validate/status read only the selected nonsecret configuration/approval.
        status does not observe a live process. health explicitly probes pinned HTTPS.
        init/admin/rebind require the supported foreground local TTY and default-No approval.
        Stop the daemon before admin. Admin start/pair keeps its listener alive for redemption.
        approve-service permits unattended serve of exactly this identity/configuration.
        Config alone grants no authority. Never put secrets in arguments, environment or logs.
        Permanent pairing is not connectivity or an inference/action permission.
        Empty worker registry: no models, inference, downloads, service or firewall installation.
        Linux native, LAN and Docker deployment remain unqualified.
        """;

    internal static HostOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args is ["help" or "--help" or "-h"]) return null;
        if (args.Length != 3 || args[0] is not
            ("validate" or "status" or "init" or "admin" or "rebind" or "serve" or "health") ||
            args[1] != "--config" || !LinuxControlDirectory.ValidPath(args[2]) ||
            !args[2].EndsWith("/host.json", StringComparison.Ordinal))
            throw new HostInputException();
        return new(args[0], args[2]);
    }
}
