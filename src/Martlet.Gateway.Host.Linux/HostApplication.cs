using System.Text.Json;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

internal interface IHostPlatform
{
    LinuxControlDirectory OpenControl(string path);
    IHostTerminal OpenTerminal();
    DurableGatewayHost OpenHost(string command, HostConfiguration config, ServiceApproval? approval,
        CancellationToken cancellation);
}

internal sealed class NativeHostPlatform : IHostPlatform
{
    public LinuxControlDirectory OpenControl(string path) => new(path, new LinuxFileSystem());
    public IHostTerminal OpenTerminal() => new LinuxTerminal();
    public DurableGatewayHost OpenHost(string command, HostConfiguration config, ServiceApproval? approval,
        CancellationToken cancellation)
    {
        var relay = config.Roles.Select(RoleWorker).ToArray();
        return command switch
        {
            "init" => DurableGatewayHost.CreateNewForBinding(config.StateDirectory, config.HostId, config.Binding,
                GatewayStorageBackend.LinuxServicePermissions, [], new QuietAudit(), LocalGatewayDecision.Enable, cancellation, relay),
            "rebind" => DurableGatewayHost.RebindForLocalHost(config.StateDirectory, config.Binding,
                GatewayStorageBackend.LinuxServicePermissions, approval!.Identity, [], new QuietAudit(),
                LocalGatewayDecision.Enable, cancellation),
            _ when approval is not null => DurableGatewayHost.OpenExistingForBinding(config.StateDirectory, config.Binding,
                GatewayStorageBackend.LinuxServicePermissions, approval.Identity, [], new QuietAudit(),
                LocalGatewayDecision.Enable, cancellation, relay),
            _ => DurableGatewayHost.OpenForLocalAdministration(config.StateDirectory, config.HostId, config.Binding,
                GatewayStorageBackend.LinuxServicePermissions, [], new QuietAudit(), LocalGatewayDecision.Enable, cancellation, relay)
        };
    }

    // One relay worker per installed host role; each kind maps to exactly one gateway route.
    internal static IGatewayInferenceWorker RoleWorker(HostRole role) => role.Kind switch
    {
        "audio2face" => new Martlet.Gateway.Audio2Face.Audio2FaceRelayWorker(role.Endpoint, role.Model, "nim"),
        _ => throw new HostInputException()
    };
}

internal sealed class QuietAudit : IGatewayAuditSink
{
    public void Record(GatewayAuditEvent gatewayEvent) { }
}

internal static class HostApplication
{
    private static DurableGatewayHost? retainedOwner;

    internal static async Task<int> RunAsync(string[] args, TextWriter output,
        CancellationToken cancellation = default, IHostPlatform? platform = null)
    {
        DurableGatewayHost? owner = null;
        IHostTerminal? terminal = null;
        LinuxControlDirectory? directory = null;
        var exit = 0;
        var health = false;
        try
        {
            var options = HostOptions.Parse(args);
            if (options is null) { output.WriteLine(HostOptions.Help); return 0; }
            health = options.Command == "health";
            platform ??= new NativeHostPlatform();
            directory = platform.OpenControl(options.ConfigPath);
            var config = HostConfiguration.Parse(directory.Read(LinuxControlDirectory.Config,
                HostConfiguration.MaximumBytes) ?? throw new HostInputException());
            config.CheckIdentity(directory);
            config.CheckPlacement(options.ConfigPath);
            if (options.Command == "validate")
            {
                output.WriteLine("configuration.valid: syntax and selected native config custody admitted; authority/runtime/network not observed.");
                return 0;
            }
            var approval = ReadApproval(directory);
            if (options.Command == "status")
            {
                approval?.Check(config, directory);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, serviceApproval = approval is null ? "absent" : "matching",
                    runtime = "not-observed", modelReadiness = "not-probed"
                }));
                return 0;
            }
            if (options.Command is "serve" or "health")
            {
                if (approval is null) throw new HostApprovalException();
                approval.Check(config, directory);
                if (health)
                {
                    var ready = await HostHealth.ProbeAsync(config, approval, cancellation);
                    CheckApproval(directory, config, approval);
                    output.WriteLine(ready ? "ready: listener and auth admission; model readiness not probed." : "health.unavailable");
                    return ready ? 0 : 6;
                }
                owner = platform.OpenHost("serve", config, approval, cancellation);
                CheckApproval(directory, config, approval);
                await owner.StartAsync(cancellation);
                CheckApproval(directory, config, approval);
                output.WriteLine("serving: approved gateway listener; empty worker registry; no model readiness claim.");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            else
            {
                terminal = platform.OpenTerminal();
                terminal.Check();
                if (options.Command == "rebind")
                {
                    if (approval is null) throw new HostApprovalException();
                    approval.Check(config, directory, requireDigest: false);
                }
                else
                    approval?.Check(config, directory);
                output.WriteLine($"Review state: {config.StateDirectory}\nHost: {config.HostId}\nOrigin: {config.Binding.Origin.CanonicalOrigin}\nService UID/GID: {config.ServiceUid}/{config.ServiceGid}");
                output.WriteLine("LinuxServicePermissions is plaintext at rest. OS session is not proof of physical presence.");
                if (!await Confirm(terminal, options.Command == "init"
                    ? "Create this new permanent host identity?"
                    : options.Command == "rebind"
                        ? $"Rebind certificate for the SAME host key {approval!.SpkiFingerprint}, without changing pairings?"
                        : "Open this existing identity for local administration?", cancellation))
                    return 3;
                config.Recheck(directory);
                if (approval is not null)
                    CheckApproval(directory, config, approval, requireDigest: options.Command != "rebind");
                owner = platform.OpenHost(options.Command, config, approval, cancellation);
                config.Recheck(directory);
                output.WriteLine($"Opened host: {owner.Identity!.HostId}\nSPKI pin: {owner.Identity.SpkiFingerprint}\nListener stopped. No engines/models/inference available.");
                if (options.Command == "rebind")
                {
                    try { directory.RemoveApproval(); }
                    catch
                    {
                        Report(output, "rebind.approval_cleanup_failed: same-key binding may be committed; new-config serve remains blocked. Preserve state and reconcile the protected receipt locally.");
                        throw;
                    }
                    output.WriteLine("Same-key rebind committed. Old service approval removed; explicitly approve-service for this configuration.");
                }
                exit = await AdminAsync(owner, config, directory, terminal, output, cancellation);
            }
        }
        catch (HostEofException) { exit = owner is null ? 3 : 0; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Report(output, "Canceled; closing owned resources without unpairing.");
            exit = 130;
        }
        catch (OperationCanceledException)
        {
            Report(output, health ? "health.deadline" : "host.canceled");
            exit = health ? 6 : 4;
        }
        catch (HostInputException)
        {
            Report(output, health ? "health.invalid_or_configuration_invalid" : "input.invalid: exact bounded nonsecret configuration required; no defaults substituted.");
            exit = health ? 6 : 2;
        }
        catch (HostApprovalException)
        {
            Report(output, "approval.required: use the same service UID/GID, identity and locally approved exact config; no approval was inferred.");
            exit = 3;
        }
        catch (HostTerminalException)
        {
            Report(output, "terminal.required: supported foreground unredirected local Linux TTY required; no secret-output fallback.");
            exit = 3;
        }
        catch (GatewayPersistenceException error)
        {
            Report(output, $"{error.Failure}: {error.Message} Preserve the same state; do not reset devices.");
            exit = 4;
        }
        catch (GatewayProtocolException error)
        {
            Report(output, $"{error.Failure.Code}: {error.Failure.Summary}");
            exit = health ? 6 : 4;
        }
        catch (GatewayClientException error)
        {
            Report(output, error.Failure.Code);
            exit = health ? 6 : 4;
        }
        catch (Exception)
        {
            Report(output, "host.failed: sanitized operational failure; preserve original state. No replacement identity or automatic retry.");
            exit = health ? 6 : 4;
        }
        finally
        {
            if (owner is not null)
            {
                var clean = false;
                try { await owner.CloseCleanlyAsync(); clean = true; }
                catch (Exception)
                {
                    exit = 5;
                    try { await owner.DisposeAsync(); }
                    catch (Exception) { retainedOwner = owner; }
                }
                if (!Report(output, clean
                    ? "Stopped and closed cleanly. Permanent pairings retained."
                    : "host.cleanup_failed: owner retained until real drain or process exit; clean closure NOT confirmed.") && exit == 0)
                    exit = 4;
            }
            terminal?.Dispose();
            directory?.Dispose();
        }
        return exit;
    }

    private static ServiceApproval? ReadApproval(LinuxControlDirectory directory) =>
        directory.Read(LinuxControlDirectory.Approval, HostConfiguration.MaximumBytes) is { } bytes
            ? ServiceApproval.Parse(bytes) : null;

    private static void CheckApproval(LinuxControlDirectory directory, HostConfiguration config,
        ServiceApproval approval, bool requireDigest = true)
    {
        approval.Check(config, directory, requireDigest);
        if (ReadApproval(directory) != approval) throw new HostApprovalException();
    }

    private static async Task<int> AdminAsync(DurableGatewayHost owner, HostConfiguration config,
        LinuxControlDirectory directory, IHostTerminal terminal, TextWriter output, CancellationToken cancellation)
    {
        var started = false;
        output.WriteLine("Commands: start, pair, list, revoke, approve-service, disable-service, stop, help. EOF closes; daemon must be stopped for admin.");
        while (true)
        {
            var command = await Read(terminal, "host> ", 32, cancellation);
            if (command is null) return 0;
            config.Recheck(directory);
            switch (command)
            {
                case "help":
                    output.WriteLine(HostOptions.Help);
                    break;
                case "start":
                    if (started) output.WriteLine("Already started.");
                    else if (await Confirm(terminal, "Start exactly the selected TLS listener (no engines)?", cancellation))
                    {
                        config.Recheck(directory);
                        await owner.StartAsync(cancellation);
                        started = true;
                        output.WriteLine("Listener started. Keep this session open during invitation redemption.");
                    }
                    break;
                case "pair":
                    if (!started) { output.WriteLine("Explicitly start the listener first."); break; }
                    terminal.CheckDisclosure();
                    var device = await Read(terminal, "Exact device ID: ", 64, cancellation) ?? throw new HostEofException();
                    var name = await Read(terminal, "Display name: ", 64, cancellation) ?? throw new HostEofException();
                    var roles = Roles(await Read(terminal, "Roles (voice,perception,memory): ", 32, cancellation) ?? throw new HostEofException());
                    if (!HostConfiguration.Identifier(device) || string.IsNullOrEmpty(name)) throw new HostInputException();
                    output.WriteLine($"Review device: {device}\nName: {name}\nRoles: {string.Join(',', roles)}\nHost pin: {owner.Identity!.SpkiFingerprint}");
                    if (await Confirm(terminal, "Approve exact device and PRIVATE invitation display? Exclude observers/recorders; terminal erasure is not forensic protection.", cancellation))
                    {
                        terminal.CheckDisclosure();
                        config.Recheck(directory);
                        var card = owner.OpenPairing(new() { DeviceId = device, DisplayName = name, Roles = roles }, cancellation);
                        await terminal.DiscloseAsync(card, cancellation);
                        output.WriteLine("Invitation screen closed; one use/five minutes. Listener remains active; use list to confirm permanent registration.");
                    }
                    break;
                case "list":
                    var registrations = owner.ListRegistrations(cancellation);
                    output.WriteLine("Permanent registrations, NOT connected sessions:");
                    foreach (var registration in registrations)
                        output.WriteLine($"Device: {Display(registration.DeviceId)} | Name: {Display(registration.DisplayName)} | Roles: {string.Join(',', registration.Roles)} | {(registration.Lifetime is PairedDeviceLifetime ? "paired; permanent" : "retiring replaced credential")}");
                    if (registrations.Count == 0) output.WriteLine("No registrations.");
                    break;
                case "revoke":
                    var target = await Read(terminal, "Exact device ID to revoke: ", 64, cancellation) ?? throw new HostEofException();
                    if (!HostConfiguration.Identifier(target)) throw new HostInputException();
                    if (await Confirm(terminal, $"Permanently revoke device {target}, including invitations?", cancellation))
                    {
                        config.Recheck(directory);
                        output.WriteLine($"Revocation committed: {owner.RevokeDevice(target, cancellation)} credential(s).");
                    }
                    break;
                case "approve-service":
                    if (await Confirm(terminal, $"Permit unattended listener restart for exactly this config and pin {owner.Identity!.SpkiFingerprint}? No device/model/action authority.", cancellation))
                    {
                        config.Recheck(directory);
                        directory.WriteApproval(ServiceApproval.Create(config, owner.Identity!));
                        config.Recheck(directory);
                        output.WriteLine("Service-start approval committed for this exact configuration and identity.");
                    }
                    break;
                case "disable-service":
                    if (await Confirm(terminal, "Remove unattended-start approval (retain all device pairings)?", cancellation))
                    {
                        config.Recheck(directory);
                        directory.RemoveApproval();
                        output.WriteLine("Service-start approval removed.");
                    }
                    break;
                case "stop":
                    if (await Confirm(terminal, "Stop and close, retaining permanent pairings?", cancellation)) return 0;
                    break;
                case "":
                    break;
                default:
                    throw new HostInputException();
            }
        }
    }

    internal static GatewayRole[] Roles(string input)
    {
        var roles = input.Split(',').Select(role => role switch
        {
            "voice" => GatewayRole.Voice,
            "perception" => GatewayRole.Perception,
            "memory" => GatewayRole.Memory,
            _ => throw new HostInputException()
        }).ToArray();
        if (roles.Length is < 1 or > 3 || roles.Distinct().Count() != roles.Length) throw new HostInputException();
        return roles;
    }

    private static string Display(string value) =>
        new(value.Take(64).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    private static async ValueTask<string?> Read(IHostTerminal terminal, string prompt, int maximum, CancellationToken cancellation)
    {
        terminal.Check();
        cancellation.ThrowIfCancellationRequested();
        var value = await terminal.ReadAsync(prompt, maximum, cancellation);
        terminal.Check();
        cancellation.ThrowIfCancellationRequested();
        if (value is not null && (value.Length > maximum || value.Any(c => c is < ' ' or > '~')))
            throw new HostInputException();
        return value;
    }

    private static async ValueTask<bool> Confirm(IHostTerminal terminal, string question, CancellationToken cancellation) =>
        (await Read(terminal, question + " [yes/No]: ", 3, cancellation) ?? throw new HostEofException()) == "yes";

    private static bool Report(TextWriter output, string message)
    {
        try { output.WriteLine(message); return true; }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
