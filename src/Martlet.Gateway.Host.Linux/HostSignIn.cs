using System.Text.Json;
using Martlet.Core.Access;
using Martlet.Core.Network;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

/// <summary>
/// martlet-host commands for sign-in from outside home, run on the host by its own account (the owner at the host's console
/// or over SSH). They edit signin.json beside host.json with the same rules the gateway applies to a member desktop's
/// changes; the running service reads it again on the next sign-in, and revokes the computers of an identity removed here.
/// </summary>
internal static class HostSignIn
{
    internal static bool Handles(string command) =>
        command is "owner-signin-status" or "owner-signin-owner" or "owner-signin-allow" or "owner-signin-disallow" or "owner-invite";

    internal static int Run(HostOptions options, HostConfiguration config, ServiceApproval? approval, LinuxControlDirectory directory,
        TextReader input, TextWriter output)
    {
        if (options.Command == "owner-invite")
        {
            if (approval is null) throw new HostApprovalException();
            string? network = null;
            try
            {
                if (directory.Read(LinuxControlDirectory.Network, LinuxControlDirectory.MaximumNetworkBytes) is { } bytes &&
                    NetworkRoster.Parse(bytes) is { } roster && roster.Host(config.HostId) is { Removed: false })
                    network = roster.NetworkId;
            }
            catch (Martlet.Core.Contracts.ContractException) { }
            // Without --address, the outside addresses set with owner-exposure (the ones the network advertises).
            IReadOnlyList<string> addresses = options.Addresses;
            if (addresses.Count == 0)
                try { addresses = HostExposure.Read(directory).Outside; }
                catch (HostInputException) { }
            var invite = new NetworkInvite
            {
                HostId = config.HostId, SpkiFingerprint = approval.SpkiFingerprint, Origin = config.Binding.Origin.CanonicalOrigin,
                Addresses = addresses, NetworkId = network, Label = options.Label
            };
            if (addresses.Count == 0)
                output.WriteLine("This host has no outside address (owner-exposure --outside) and none was given (--address): this " +
                    "invite only works where the home address is reachable.");
            output.WriteLine(invite.Write());
            return 0;
        }

        var document = Load(directory);
        var now = DateTimeOffset.UtcNow;
        switch (options.Command)
        {
            case "owner-signin-status":
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    usable = GatewaySignInSettings.BlockedReason(document) is null,
                    blockedReason = GatewaySignInSettings.BlockedReason(document),
                    owner = document.Owner is { } owner ? new { user = owner.User, recoveryCodesLeft = owner.RecoveryCodes.Count } : null,
                    providers = document.Providers.Select(p => new { p.Id, p.Kind, p.Name, p.Issuer, p.ClientId, hasClientSecret = p.ClientSecret is not null }),
                    allowed = document.Allowed.Select(a => new { a.Provider, a.Subject, a.Label }),
                    enrolled = document.Enrolled.Select(e => new { e.DeviceId, e.Provider, e.Subject, e.Label, e.EnrolledAt }),
                    // Computers of sign-ins no longer allowed: the service revokes them here and member desktops remove them from
                    // the network on their next sync (removedFromNetwork until the roster shows it; pendingRemoval until the
                    // service has read the change).
                    removedFromNetwork = document.Removed.Select(r => new { r.DeviceId, r.Provider, r.Subject, r.Label, r.At }),
                    pendingRemoval = document.WouldSweep().Select(e => new { e.DeviceId, e.Provider, e.Subject, e.Label })
                }));
                return 0;
            case "owner-signin-owner":
            {
                var password = input.ReadLine() ?? throw new HostEofException();
                var secret = Totp.NewSecret();
                output.WriteLine($"Add this to your authenticator app (scan or open the link, or type the secret):");
                output.WriteLine($"otpauth: {Totp.Uri("Martlet " + config.HostId, options.User!, secret)}");
                output.WriteLine($"secret: {secret}");
                output.WriteLine("Then type the 6-digit code it shows:");
                var code = input.ReadLine() ?? throw new HostEofException();
                var codes = GatewaySignInSettings.Apply(document, new()
                {
                    Action = "owner", User = options.User, Password = password, TotpSecret = secret, Code = code
                }, now);
                Save(directory, document);
                output.WriteLine($"Owner account {options.User} set. Recovery codes (each works once; keep them somewhere safe, they are not shown again):");
                foreach (var recovery in codes!) output.WriteLine(recovery);
                return 0;
            }
            case "owner-signin-allow":
                GatewaySignInSettings.Apply(document, new()
                {
                    Action = "allow", Provider = options.Provider, Subject = options.Subject, Label = options.Label
                }, now);
                Save(directory, document);
                output.WriteLine($"Allowed {options.Label ?? options.Subject} ({options.Provider}) to sign in to {config.HostId}.");
                return 0;
            case "owner-signin-disallow":
                GatewaySignInSettings.Apply(document, new() { Action = "disallow", Provider = options.Provider, Subject = options.Subject }, now);
                var computers = document.WouldSweep().Select(e => e.DeviceId).ToArray();
                Save(directory, document);
                output.WriteLine($"Removed {options.Subject} ({options.Provider}) and its computers from the network: " + (computers.Length == 0
                    ? "it signed in no computer here."
                    : $"the service revokes {string.Join(", ", computers)} and your member computers remove them from the Martlet network on their next sync."));
                return 0;
            default:
                throw new HostInputException();
        }
    }

    private static GatewaySignInDocument Load(LinuxControlDirectory directory) =>
        directory.Read(LinuxControlDirectory.SignIn, LinuxControlDirectory.MaximumSignInBytes) is { } bytes
            ? GatewaySignInDocument.Parse(bytes) : new();

    private static void Save(LinuxControlDirectory directory, GatewaySignInDocument document) => directory.WriteSignIn(document.Write());
}

/// <summary>Keeps the host's sign-in settings in signin.json beside host.json (0600, service owner). It holds the owner
/// account's authenticator secret and verifiers and provider client secrets; not part of the approved configuration.</summary>
internal sealed class ControlSignInStorage(LinuxControlDirectory directory) : IGatewaySignInStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.SignIn, LinuxControlDirectory.MaximumSignInBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteSignIn(bytes);
    }
}
