using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

internal sealed record HostOptions(string Command, string ConfigPath)
{
    internal const string Help = """
        Martlet Linux gateway candidate (native x86_64/glibc/ext4; default No)
        No arguments, help, --help, -h: passive help; no file/key/listener access.
        Commands: validate, status, init, admin, rebind, serve, health, owner-init, owner-approve, owner-pair, owner-network-reset,
                  owner-exposure
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
          owner-exposure ... [--outside <name:port>]... [--clear-outside] [--allow-pairing-outside-home yes|no] (typed codes)
                         [--treat-all-as-outside yes|no]: how this host is reached from outside home (exposure.json
                         beside host.json; docs/NETWORK.md). Outside addresses (an overlay address such as Tailscale's, or
                         a router port forward) are advertised to member desktops, which sign them into the network; with
                         any set, the gateway's limits apply to every source. No options prints the current choices.
                         Restart the service to apply.
        Sign-in from outside home (signin.json; the running service picks changes up by itself):
          owner-signin-status    show the owner account, providers, allowed identities and computers that signed in
                                 (never a secret)
          owner-signin-owner     ... --user <name>: set the owner account. Reads the password (12+ characters) from
                                 the first stdin line, prints an authenticator secret and otpauth link, reads a current
                                 authenticator code from the next line, then prints ten one-use recovery codes once
          owner-signin-allow     ... --provider <id> --subject <subject> [--label <text>]: allow an identity
          owner-signin-disallow  ... --provider <id> --subject <subject>: remove it (its computers lose access)
          owner-invite           ... [--address <name:port>]... [--label <text>]: print a martlet-invite-v1 line (this
                                 host's ID, TLS pin, home address and the outside addresses; no secret)
        Config alone grants no authority. Never put secrets in arguments, environment or logs.
        Permanent pairing is not connectivity or an inference/action permission.
        Empty worker registry: no models, inference, downloads, service or firewall installation.
        Linux native, LAN and Docker deployment remain unqualified.
        """;

    internal string? DeviceId { get; init; }
    internal string? Name { get; init; }
    internal string Roles { get; init; } = "voice";
    internal IReadOnlyList<string> Outside { get; init; } = [];
    internal bool ClearOutside { get; init; }
    internal bool? AllowPairingOutsideHome { get; init; }
    internal bool? TreatAllAsOutside { get; init; }
    internal string? User { get; init; }
    internal string? Provider { get; init; }
    internal string? Subject { get; init; }
    internal string? Label { get; init; }
    internal IReadOnlyList<string> Addresses { get; init; } = [];

    internal static HostOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args is ["help" or "--help" or "-h"]) return null;
        if (args.Length < 3 || args[0] is not
            ("validate" or "status" or "init" or "admin" or "rebind" or "serve" or "health" or
             "owner-init" or "owner-approve" or "owner-pair" or "owner-network-reset" or "owner-exposure" or "owner-signin-status" or
             "owner-signin-owner" or "owner-signin-allow" or "owner-signin-disallow" or "owner-invite") ||
            args[1] != "--config" || !LinuxControlDirectory.ValidPath(args[2]) ||
            !args[2].EndsWith("/host.json", StringComparison.Ordinal))
            throw new HostInputException();
        if (args[0] == "owner-exposure") return ParseExposure(new HostOptions(args[0], args[2]), args);
        var command = args[0];
        if (command is not ("owner-pair" or "owner-signin-owner" or "owner-signin-allow" or "owner-signin-disallow" or "owner-invite"))
            return args.Length == 3 ? new(command, args[2]) : throw new HostInputException();
        var options = new HostOptions(command, args[2]);
        var pair = command == "owner-pair";
        var identity = command is "owner-signin-allow" or "owner-signin-disallow";
        for (var i = 3; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new HostInputException();
            var value = args[i + 1];
            options = args[i] switch
            {
                "--device-id" when pair && options.DeviceId is null && HostConfiguration.Identifier(value) => options with { DeviceId = value },
                "--name" when pair && options.Name is null && DisplayName(value) => options with { Name = value },
                "--roles" when pair => options with { Roles = value },
                "--user" when command == "owner-signin-owner" && options.User is null && DisplayName(value) => options with { User = value },
                "--provider" when identity && options.Provider is null && DisplayName(value) => options with { Provider = value },
                "--subject" when identity && options.Subject is null && DisplayName(value) => options with { Subject = value },
                "--label" when (identity && command != "owner-signin-disallow" || command == "owner-invite") && options.Label is null &&
                    DisplayName(value) => options with { Label = value },
                "--address" when command == "owner-invite" && options.Addresses.Count < Martlet.Core.Network.NetworkInvite.MaximumAddresses &&
                    Martlet.Core.Network.NetworkInvite.NormalizeAddress(value) is { } address => options with { Addresses = [.. options.Addresses, address] },
                _ => throw new HostInputException()
            };
        }
        if (command == "owner-signin-owner" && options.User is null || identity && (options.Provider is null || options.Subject is null))
            throw new HostInputException();
        if (!pair) return options;
        // Neither --device-id nor --name: a short typed code that any desktop can redeem once.
        if ((options.DeviceId is null) != (options.Name is null)) throw new HostInputException();
        _ = HostApplication.Roles(options.Roles);
        return options;
    }

    private static HostOptions ParseExposure(HostOptions options, string[] args)
    {
        static bool YesNo(string value) => value switch { "yes" => true, "no" => false, _ => throw new HostInputException() };
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] == "--clear-outside") { options = options with { ClearOutside = true }; continue; }
            if (i + 1 >= args.Length) throw new HostInputException();
            var value = args[++i];
            options = args[i - 1] switch
            {
                "--outside" when Martlet.Core.Network.NetworkRoster.NormalizeAddress(value) is { } address &&
                    options.Outside.Count < Martlet.Core.Network.NetworkRoster.MaximumAddresses =>
                    options with { Outside = [.. options.Outside, address] },
                "--allow-pairing-outside-home" when options.AllowPairingOutsideHome is null => options with { AllowPairingOutsideHome = YesNo(value) },
                "--treat-all-as-outside" when options.TreatAllAsOutside is null => options with { TreatAllAsOutside = YesNo(value) },
                _ => throw new HostInputException()
            };
        }
        if (options.ClearOutside && options.Outside.Count > 0) throw new HostInputException();
        return options;
    }

    private static bool DisplayName(string value) =>
        value.Length is > 0 and <= 64 && value[0] != '-' && value.All(c => c is >= ' ' and <= '~');
}
