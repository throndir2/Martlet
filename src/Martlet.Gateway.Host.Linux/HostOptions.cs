using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

internal sealed record HostOptions(string Command, string ConfigPath)
{
    internal const string Help = """
        Martlet Linux gateway candidate (native x86_64/glibc/ext4; default No)
        No arguments, help, --help, -h: passive help; no file/key/listener access.
        Commands: validate, status, init, admin, rebind, serve, health, owner-init, owner-approve, owner-pair, owner-network-reset
        Syntax: <command> --config <absolute-private-directory>/host.json
        validate/status read only the selected nonsecret configuration/approval.
        status does not observe a live process. health explicitly probes pinned HTTPS.
        init/admin/rebind require the supported foreground local TTY and default-No approval.
        Stop the daemon before admin. Admin start/pair keeps its listener alive for redemption.
        approve-service permits unattended serve of exactly this identity/configuration.
        Owner commands run without a console for the host's own account (martlet-host --yes, for example driven
        by Martlet desktop over the owner's SSH session, whose explicit click is the confirmation):
          owner-init     create this new host identity and approve unattended serve of this configuration
          owner-approve  open the existing identity and approve unattended serve of this configuration
          owner-pair     ... [--roles voice]: start the listener, show this host's address and a one-use XXXX-XXXX code
                         to type on a desktop (Devices > Add a computer > Pair), wait for one desktop to redeem it,
                         then close. The code has no deadline: a "cancel" line or the end of stdin withdraws it, and
                         five wrong tries close it.
          owner-pair     ... --device-id <id> --name <display name> [--roles voice]: for exactly that device, printing
                         one machine-readable "pairing-code: martlet-pair-v1..." line instead; it waits at most 5
                         minutes, and a "cancel" line on stdin stops waiting.
          owner-network-reset  leave this host's Martlet network (removes network.json; pairings stay). Stop the
                         service first; the next desktop that pairs adds the host to its own network.
        Config alone grants no authority. Never put secrets in arguments, environment or logs.
        Permanent pairing is not connectivity or an inference/action permission.
        Empty worker registry: no models, inference, downloads, service or firewall installation.
        Linux native, LAN and Docker deployment remain unqualified.
        """;

    internal string? DeviceId { get; init; }
    internal string? Name { get; init; }
    internal string Roles { get; init; } = "voice";

    internal static HostOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args is ["help" or "--help" or "-h"]) return null;
        if (args.Length < 3 || args[0] is not
            ("validate" or "status" or "init" or "admin" or "rebind" or "serve" or "health" or
             "owner-init" or "owner-approve" or "owner-pair" or "owner-network-reset") ||
            args[1] != "--config" || !LinuxControlDirectory.ValidPath(args[2]) ||
            !args[2].EndsWith("/host.json", StringComparison.Ordinal))
            throw new HostInputException();
        if (args[0] != "owner-pair")
            return args.Length == 3 ? new(args[0], args[2]) : throw new HostInputException();
        var options = new HostOptions(args[0], args[2]);
        for (var i = 3; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new HostInputException();
            var value = args[i + 1];
            options = args[i] switch
            {
                "--device-id" when options.DeviceId is null && HostConfiguration.Identifier(value) => options with { DeviceId = value },
                "--name" when options.Name is null && DisplayName(value) => options with { Name = value },
                "--roles" => options with { Roles = value },
                _ => throw new HostInputException()
            };
        }
        // Neither --device-id nor --name: a short typed code that any desktop can redeem once.
        if ((options.DeviceId is null) != (options.Name is null)) throw new HostInputException();
        _ = HostApplication.Roles(options.Roles);
        return options;
    }

    private static bool DisplayName(string value) =>
        value.Length is > 0 and <= 64 && value[0] != '-' && value.All(c => c is >= ' ' and <= '~');
}
