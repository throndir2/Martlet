using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host;

internal static class HostApplication
{
    // A failed drain must retain the owner until process exit, not release the store under a live listener.
    private static DurableGatewayHost? retainedOwner;

    internal static async Task<int> RunAsync(string[] args, ILocalConsole console, CancellationToken cancellation = default)
    {
        DurableGatewayHost? owner = null;
        var exit = 0;
        try
        {
            var options = HostOptions.Parse(args);
            if (options is null)
            {
                console.Write(HostOptions.Help);
                return 0;
            }
            RequireInteractive(console);
            if (!OperatingSystem.IsWindows())
            {
                console.Write("UnsupportedPlatform: This executable requires Windows and local NTFS. No state was opened.");
                return 4;
            }
            console.Write($"State: {options.State}\nHost ID: {options.HostId}\nOrigin: {options.Origin.CanonicalOrigin}");
            console.Write("Local OS-session authority is not proof of physical presence. No listener starts on open.");
            if (!await Confirm(console, options.Create ? "Create this new permanent host identity?" : "Open this existing host identity?", cancellation))
                return 3;
            owner = options.Create
                ? DurableGatewayHost.CreateNew(options.State, options.HostId, options.Origin, [], new Audit(),
                    LocalGatewayDecision.Enable, cancellation)
                : DurableGatewayHost.OpenExisting(options.State, options.Origin, [], new Audit(),
                    LocalGatewayDecision.Enable, cancellation);
            if (owner.Identity!.HostId != options.HostId)
            {
                console.Write("host.id_mismatch: The stored host ID differs from the selected ID. Reopen with the verified original ID; do not replace or reset the store.");
                exit = 4;
            }
            else
            {
                console.Write($"Opened host: {owner.Identity.HostId}\nSPKI pin: {owner.Identity.SpkiFingerprint}");
                console.Write("Listener stopped. Paired does not mean connected. No engines/models/inference are available.");
                exit = await ControlAsync(owner, console, cancellation);
            }
        }
        catch (HostEndOfInputException)
        {
            exit = owner is null ? 3 : 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Report(console, "Canceled. Closing owned resources without unpairing devices.");
            exit = 130;
        }
        catch (HostInputException)
        {
            Report(console, "input.invalid: Use help and exact bounded non-secret fields. No defaults were substituted; argument/input contents are not echoed.");
            exit = 2;
        }
        catch (HostConsoleException)
        {
            Report(console, "console.required: Use a local interactive Windows console with input, output and error unredirected. Approval and invitation disclosure cannot be piped or logged.");
            exit = 3;
        }
        catch (GatewayPersistenceException error)
        {
            Report(console, $"{error.Failure}: {error.Message}\nResolve the reported condition and reopen the same protected state. Do not delete it, reset pairings or restore a stale backup.");
            exit = 4;
        }
        catch (GatewayProtocolException error)
        {
            Report(console, $"{error.Failure.Code}: {error.Failure.Summary}\n{error.Failure.Remedy}");
            exit = 4;
        }
        catch (Exception)
        {
            // Exception messages may contain paths, request data or secrets. Never print them.
            Report(console, "host.failed: The local operation failed. Outcome may be uncertain; reopen the same protected state for recovery. No automatic retry or identity replacement was attempted.");
            exit = 4;
        }
        finally
        {
            if (owner is not null)
            {
                var clean = false;
                try
                {
                    await owner.CloseCleanlyAsync();
                    clean = true;
                }
                catch (Exception)
                {
                    exit = 5;
                    try { await owner.DisposeAsync(); }
                    catch (Exception) { retainedOwner = owner; }
                }
                var reported = Report(console, clean
                    ? "Stopped and closed cleanly. Permanent pairing records remain; connectivity is stopped."
                    : "host.cleanup_failed: Clean closure could not be confirmed. Final drain attempted; any uncertain owner is retained until process exit. Reopen the same state afterward. Do not reset devices.");
                if (!reported && exit == 0)
                    exit = 4;
            }
        }
        return exit;
    }

    private static async Task<int> ControlAsync(DurableGatewayHost owner, ILocalConsole console, CancellationToken cancellation)
    {
        var started = false;
        console.Write("Commands: start, pair, list, revoke, stop, help. EOF or Ctrl+C closes without unpairing.");
        while (true)
        {
            var command = await Read(console, "host> ", 16, cancellation);
            if (command is null)
                return 0;
            switch (command)
            {
                case "help":
                    console.Write(HostOptions.Help);
                    break;
                case "start":
                    if (started)
                        console.Write("Listener already started; no additional listener created.");
                    else if (await Confirm(console, "Start the selected loopback TLS listener (no AI engines)?", cancellation))
                    {
                        await owner.StartAsync(cancellation);
                        started = true;
                        console.Write("Loopback listener started. Connection state is not tracked; paired registrations are not connected sessions.");
                    }
                    break;
                case "list":
                    console.Write("Permanent paired registrations (not connectivity; no pairing expiry):");
                    var registrations = owner.ListRegistrations(cancellation);
                    if (registrations.Count == 0)
                        console.Write("No paired registrations.");
                    foreach (var record in registrations)
                    {
                        var lifetime = record.Lifetime is PairedDeviceLifetime
                            ? "paired; permanent until revoked"
                            : "retiring replaced credential; not pairing expiry";
                        console.Write($"Device: {Fields.Display(record.DeviceId, 64)} | Name: {Fields.Display(record.DisplayName, 64)} | Roles: {string.Join(',', record.Roles)} | {lifetime}");
                    }
                    break;
                case "pair":
                    if (!started)
                    {
                        console.Write("Listener stopped: explicitly start before opening an invitation.");
                        break;
                    }
                    var device = await Read(console, "Device ID (1-64 ASCII identifier): ", 64, cancellation);
                    if (device is null) return 0;
                    var name = await Read(console, "Display name (1-64 printable ASCII): ", 64, cancellation);
                    if (name is null) return 0;
                    var rolesText = await Read(console, "Roles (comma-separated: voice,perception,memory): ", 32, cancellation);
                    if (rolesText is null) return 0;
                    if (!Fields.Identifier(device) || !Fields.Printable(name, 64))
                        throw new HostInputException();
                    var roles = ParseRoles(rolesText);
                    console.Write($"Review device: {device}\nName: {name}\nRoles: {string.Join(',', roles)}\nHost: {owner.Identity!.HostId}\nSPKI pin: {owner.Identity.SpkiFingerprint}");
                    console.Write("Invitation: one use, five minutes. Device pairing: permanent until deliberately revoked.");
                    if (await Confirm(console, "Approve this exact device and disclose its invitation on a temporary private screen? Ensure no screen recording/sharing.", cancellation))
                    {
                        var card = owner.OpenPairing(new() { DeviceId = device, DisplayName = name, Roles = roles }, cancellation);
                        RequireInteractive(console);
                        await console.DiscloseAsync(card, cancellation);
                        console.Write("Invitation screen closed. Invitation remains one-use until its five-minute deadline; revoke this device to cancel it. Pairing is confirmed only by a registration in list.");
                    }
                    break;
                case "revoke":
                    var target = await Read(console, "Exact device ID to revoke (also cancels its invitations): ", 64, cancellation);
                    if (target is null) return 0;
                    if (!Fields.Identifier(target))
                        throw new HostInputException();
                    if (await Confirm(console, $"Permanently revoke all credentials and invitations for device {target}?", cancellation))
                    {
                        var count = owner.RevokeDevice(target, cancellation);
                        console.Write($"Revocation committed: {count} credential(s); matching invitations closed. A new local pairing approval is required for future access.");
                    }
                    break;
                case "stop":
                    if (await Confirm(console, "Stop the listener and close this owner, retaining permanent pairings?", cancellation))
                        return 0;
                    break;
                case "":
                    break;
                default:
                    throw new HostInputException();
            }
        }
    }

    internal static GatewayRole[] ParseRoles(string input)
    {
        var roles = input.Split(',').Select(value => value switch
        {
            "voice" => GatewayRole.Voice,
            "perception" => GatewayRole.Perception,
            "memory" => GatewayRole.Memory,
            _ => throw new HostInputException()
        }).ToArray();
        if (roles.Length is < 1 or > 3 || roles.Distinct().Count() != roles.Length)
            throw new HostInputException();
        return roles;
    }

    private static void RequireInteractive(ILocalConsole console)
    {
        if (!console.IsInteractive)
            throw new HostConsoleException();
    }

    private static async ValueTask<string?> Read(ILocalConsole console, string prompt, int maximum, CancellationToken cancellation)
    {
        RequireInteractive(console);
        cancellation.ThrowIfCancellationRequested();
        var value = await console.ReadAsync(prompt, maximum, cancellation);
        RequireInteractive(console);
        cancellation.ThrowIfCancellationRequested();
        if (value is not null && value != "" && !Fields.Printable(value, maximum))
            throw new HostInputException();
        return value;
    }

    private static async ValueTask<bool> Confirm(ILocalConsole console, string question, CancellationToken cancellation)
    {
        var response = await Read(console, question + " [yes/No]: ", 3, cancellation);
        if (response is null)
            throw new HostEndOfInputException();
        if (response == "yes")
            return true;
        console.Write("Not approved; no requested action taken.");
        return false;
    }

    private static bool Report(ILocalConsole console, string text)
    {
        try { console.Write(text); return true; }
        // Output failure cannot interrupt resource cleanup. The caller still returns a nonzero exit code.
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private sealed class Audit : IGatewayAuditSink
    {
        // No request logging or secret-bearing diagnostic sinks in this bounded console host.
        public void Record(GatewayAuditEvent gatewayEvent) { }
    }
}
