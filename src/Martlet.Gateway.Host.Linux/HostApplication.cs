using System.Text.Json;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

internal interface IHostPlatform
{
    LinuxControlDirectory OpenControl(string path);
    IHostTerminal OpenTerminal();
    DurableGatewayHost OpenHost(string command, HostConfiguration config, ServiceApproval? approval,
        CancellationToken cancellation);
    /// <summary>Standard input for owner commands (owner-pair watches it for a "cancel" line).</summary>
    TextReader Input => Console.In;
}

internal sealed class NativeHostPlatform : IHostPlatform
{
    public LinuxControlDirectory OpenControl(string path) => new(path, PosixFileSystem.Create());
    public IHostTerminal OpenTerminal() => new LinuxTerminal();
    public DurableGatewayHost OpenHost(string command, HostConfiguration config, ServiceApproval? approval,
        CancellationToken cancellation)
    {
        var relay = config.Roles.Select(RoleWorker).ToArray();
        var host = command switch
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
        // A voice service's own reason for failing a reply goes into this host's log, which paired desktops show.
        foreach (var voice in relay.OfType<Martlet.Gateway.F5.F5RelayWorker>()) voice.Report = host.RecordActivity;
        return host;
    }

    // One relay worker per installed host role; each kind maps to exactly one gateway route, placed on the role's graphics
    // cards (host.json "gpus"; Reading always runs on the processor; unknown counts as the whole host).
    internal static IGatewayInferenceWorker RoleWorker(HostRole role)
    {
        try
        {
            IGatewayInferenceWorker worker = role.Kind switch
            {
                "audio2face" => new Martlet.Gateway.Audio2Face.Audio2FaceRelayWorker(role.Endpoint, role.Model, "nim"),
                "ollama" => new Martlet.Gateway.Ollama.OllamaRelayWorker(role.Endpoint, role.Model),
                "deep-thinking" => Martlet.Gateway.Ollama.OllamaRelayWorker.DeepThinking(role.Endpoint, role.Model, slots: role.Slots),
                "f5" => new Martlet.Gateway.F5.F5RelayWorker(role.Endpoint, role.Model),
                "xtts" => Martlet.Gateway.Xtts.XttsRelay.Create(role.Endpoint, role.Model),
                "chatterbox" => Martlet.Gateway.F5.ChatterboxRelay.Create(role.Endpoint, role.Model),
                "gpt-sovits" => Martlet.Gateway.GptSovits.GptSovitsRelay.Create(role.Endpoint, role.Model),
                "dia" => Martlet.Gateway.Dia.DiaRelay.Create(role.Endpoint, role.Model),
                "stt" => new Martlet.Gateway.Stt.SttRelayWorker(role.Endpoint, role.Model),
                "singing" => new Martlet.Gateway.Singing.SongRelayWorker(role.Endpoint, role.Model),
                "pictures" => new Martlet.Gateway.Pictures.PictureRelayWorker(role.Endpoint, role.Model),
                "ocr" => new Martlet.Gateway.Ocr.OcrRelayWorker(role.Endpoint, role.Model),
                _ => throw new HostInputException()
            };
            IReadOnlyList<string> gpus = role.Gpus ??
                (Martlet.Core.Installation.SharedGpu.ProcessorOnlyRoles.Contains(role.Kind) ? [GatewayGpus.Cpu] : []);
            if (gpus.Count > 0) worker.Route.PlaceOn(gpus);
            return worker;
        }
        catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or ArgumentException or GatewayProtocolException)
        {
            throw new HostInputException();
        }
    }
}

internal sealed class QuietAudit : IGatewayAuditSink
{
    public void Record(GatewayAuditEvent gatewayEvent) { }
}

/// <summary>Keeps the gateway's copy of the shared cluster plan in cluster.json beside host.json (0600, service owner).
/// It is not part of the approved configuration, so role changes never require re-approval.</summary>
internal sealed class ControlClusterStorage(LinuxControlDirectory directory) : IGatewayClusterStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.Cluster, Martlet.Core.Cluster.ClusterPlan.MaximumBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteCluster(bytes);
    }
}

/// <summary>Keeps the gateway's copy of the shared voice list in voices.json beside host.json (0600, service owner). Like
/// cluster.json it is not part of the approved configuration.</summary>
internal sealed class ControlVoiceStorage(LinuxControlDirectory directory) : IGatewayVoiceStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.Voices, LinuxControlDirectory.MaximumVoicesBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteVoices(bytes);
    }
}

/// <summary>Keeps the gateway's copy of the voices Martlet speaks with in speaking-voices.json and each live voice's
/// recording in speaking-voice-&lt;sha256&gt;.wav beside host.json (0600, service owner). Not part of the approved
/// configuration.</summary>
internal sealed class ControlSpeakingVoiceStorage(LinuxControlDirectory directory) : IGatewaySpeakingVoiceStorage
{
    private readonly object gate = new();

    public byte[]? LoadLibrary()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.SpeakingVoices, LinuxControlDirectory.MaximumSpeakingVoicesBytes);
    }

    public void SaveLibrary(byte[] bytes)
    {
        lock (gate) directory.WriteSpeakingVoices(bytes);
    }

    public byte[]? LoadAudio(string sha256)
    {
        lock (gate) return directory.Read(LinuxControlDirectory.SpeakingVoiceAudio(sha256), LinuxControlDirectory.MaximumSpeakingVoiceAudioBytes);
    }

    public void SaveAudio(string sha256, byte[] bytes)
    {
        lock (gate) directory.WriteSpeakingVoiceAudio(sha256, bytes);
    }

    public void RemoveAudio(string sha256)
    {
        lock (gate) directory.RemoveSpeakingVoiceAudio(sha256);
    }
}

/// <summary>Keeps the gateway's copy of the character models the owner added in character-models.json and each live model's
/// pieces in character-model-chunk-&lt;sha256&gt;.bin beside host.json (0600, service owner). Not part of the approved
/// configuration.</summary>
internal sealed class ControlCharacterModelStorage(LinuxControlDirectory directory) : IGatewayCharacterModelStorage
{
    private readonly object gate = new();

    public byte[]? LoadLibrary()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.CharacterModels, LinuxControlDirectory.MaximumCharacterModelsBytes);
    }

    public void SaveLibrary(byte[] bytes)
    {
        lock (gate) directory.WriteCharacterModels(bytes);
    }

    public bool HasChunk(string sha256)
    {
        lock (gate) return directory.HasCharacterModelChunk(sha256);
    }

    public byte[]? LoadChunk(string sha256)
    {
        lock (gate) return directory.Read(LinuxControlDirectory.CharacterModelChunk(sha256), LinuxControlDirectory.MaximumCharacterModelChunkBytes);
    }

    public void SaveChunk(string sha256, byte[] bytes)
    {
        lock (gate) directory.WriteCharacterModelChunk(sha256, bytes);
    }

    public void RemoveChunk(string sha256)
    {
        lock (gate) directory.RemoveCharacterModelChunk(sha256);
    }
}

/// <summary>Keeps the gateway's copy of Martlet's creations in creations.json and each piece of a live creation's assets in
/// creation-chunk-&lt;sha256&gt;.bin beside host.json (0600, service owner). Not part of the approved configuration.</summary>
internal sealed class ControlCreationStorage(LinuxControlDirectory directory) : IGatewayCreationStorage
{
    private readonly object gate = new();

    public byte[]? LoadLibrary()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.Creations, LinuxControlDirectory.MaximumCreationsBytes);
    }

    public void SaveLibrary(byte[] bytes)
    {
        lock (gate) directory.WriteCreations(bytes);
    }

    public bool HasChunk(string sha256)
    {
        lock (gate) return directory.HasCreationChunk(sha256);
    }

    public byte[]? LoadChunk(string sha256)
    {
        lock (gate) return directory.Read(LinuxControlDirectory.CreationChunk(sha256), LinuxControlDirectory.MaximumCreationChunkBytes);
    }

    public void SaveChunk(string sha256, byte[] bytes)
    {
        lock (gate) directory.WriteCreationChunk(sha256, bytes);
    }

    public void RemoveChunk(string sha256)
    {
        lock (gate) directory.RemoveCreationChunk(sha256);
    }
}

/// <summary>Keeps the shared Home Assistant connection in home-assistant.json beside host.json (0600, service owner).
/// Contains the HA access token and is not part of the approved configuration.</summary>
internal sealed class ControlHomeAssistantStorage(LinuxControlDirectory directory) : IGatewayHomeAssistantStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.HomeAssistant, LinuxControlDirectory.MaximumHomeAssistantBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteHomeAssistant(bytes);
    }
}

/// <summary>Keeps the gateway's log in logs.json beside host.json (0600, service owner): its own activity and every
/// computer's lines the owner's desktops share with it. Not part of the approved configuration.</summary>
internal sealed class ControlLogStorage(LinuxControlDirectory directory) : IGatewayLogStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.Logs, LinuxControlDirectory.MaximumLogsBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteLogs(bytes);
    }
}

/// <summary>Keeps the Martlet network roster this host accepted in network.json beside host.json (0600, service owner).
/// Not part of the approved configuration; martlet-host network-reset removes it.</summary>
internal sealed class ControlNetworkStorage(LinuxControlDirectory directory) : IGatewayNetworkStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.Network, LinuxControlDirectory.MaximumNetworkBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteNetwork(bytes);
    }
}

/// <summary>Keeps the commands paired computers sent through this host in commands.json beside host.json (0600, service
/// owner; never their secrets). Not part of the approved configuration.</summary>
internal sealed class ControlCommandStorage(LinuxControlDirectory directory) : IGatewayCommandStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.Commands, LinuxControlDirectory.MaximumCommandsBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteCommands(bytes);
    }
}

/// <summary>Keeps the network's API keys in api-keys.json beside host.json (0600, service owner): names, scopes and SHA-256
/// verifiers, never a usable key. Not part of the approved configuration.</summary>
internal sealed class ControlApiKeyStorage(LinuxControlDirectory directory) : IGatewayApiKeyStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.ApiKeys, LinuxControlDirectory.MaximumApiKeysBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteApiKeys(bytes);
    }
}

/// <summary>Keeps the settings the owner's computers share in shared-settings.json beside host.json (0600, service owner). It
/// contains their API keys and is not part of the approved configuration.</summary>
internal sealed class ControlSettingsStorage(LinuxControlDirectory directory) : IGatewaySettingsStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.SharedSettings, LinuxControlDirectory.MaximumSharedSettingsBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteSharedSettings(bytes);
    }
}

/// <summary>Keeps everything Martlet remembers, the same on the owner's computers, in memories.json beside host.json (0600,
/// service owner). It holds the owner's memories and is not part of the approved configuration.</summary>
internal sealed class ControlMemoryStorage(LinuxControlDirectory directory) : IGatewayMemoryStorage
{
    private readonly object gate = new();

    public byte[]? Load()
    {
        lock (gate) return directory.Read(LinuxControlDirectory.Memories, LinuxControlDirectory.MaximumMemoriesBytes);
    }

    public void Save(byte[] bytes)
    {
        lock (gate) directory.WriteMemories(bytes);
    }
}

internal static class HostApplication
{
    private static DurableGatewayHost? retainedOwner;
    private static readonly TimeSpan AuthorityCheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>Starts the command mailbox with a fresh agent token in agent.token (0600, service owner), which only the host
    /// computer itself can read: the Martlet app there presents it to take this host's commands. Without it the gateway
    /// still serves everything else.</summary>
    private static void AttachCommands(DurableGatewayHost owner, LinuxControlDirectory directory)
    {
        var token = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        try { directory.WriteAgentToken(System.Text.Encoding.ASCII.GetBytes(token)); }
        catch (GatewayPersistenceException) { return; }
        owner.AttachCommands(new ControlCommandStorage(directory), token);
    }

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
            if (MacHost.Handles(args)) return await MacHost.RunAsync(args, output, cancellation);
            var options = HostOptions.Parse(args);
            if (options is null) { output.WriteLine(HostOptions.Help); return 0; }
            health = options.Command == "health";
            platform ??= new NativeHostPlatform();
            directory = platform.OpenControl(options.ConfigPath);
            var config = HostConfiguration.Parse(directory.Read(LinuxControlDirectory.Config,
                HostConfiguration.MaximumBytes) ?? throw new HostInputException());
            config.CheckIdentity(directory);
            config.CheckPlacement(options.ConfigPath);
            if (OperatingSystem.IsMacOS() && config.Roles.Select(r => MacHost.Refusal(r.Kind)).FirstOrDefault(r => r is not null) is { } refused)
            {
                output.WriteLine($"role.unsupported: {refused}");
                return 2;
            }
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
                    runtime = "not-observed", modelReadiness = "not-probed", network = DescribeNetwork(directory, config.HostId)
                }));
                return 0;
            }
            if (options.Command == "owner-network-reset")
            {
                // The host's own account (martlet-host --yes network-reset, the service stopped) is the owner's confirmation.
                var removed = directory.RemoveNetwork();
                output.WriteLine(removed
                    ? $"Host {config.HostId} left its Martlet network. Paired desktops keep their pairings (revoke them with martlet-host console); the next desktop that pairs adds this host to its own network."
                    : $"Host {config.HostId} is not in a Martlet network.");
                return 0;
            }
            if (options.Command == "owner-exposure")
            {
                // The host's own account (martlet-host --yes) is the owner's confirmation, as for owner-network-reset.
                var current = HostExposure.Read(directory);
                var changed = options.Outside.Count > 0 || options.ClearOutside || options.AllowPairingOutsideHome is not null ||
                    options.TreatAllAsOutside is not null;
                var blocked = SignInBlocked(directory);
                if (changed)
                {
                    var next = current.With(options, DateTimeOffset.UtcNow);
                    // A host becomes reachable from outside only once sign-in is set up on it; removing and keeping never need it.
                    if (blocked is not null && GatewayOutsideAccess.Expands(current.Outside, current.AllowPairingOutsideHome, next.Outside, next.AllowPairingOutsideHome))
                    {
                        output.WriteLine($"outside.needs_signin: {GatewayOutsideAccess.Describe(blocked)} ({blocked}), so host {config.HostId} can't get new " +
                            "outside addresses or accept typed codes from outside home yet. Set it up first (martlet-host owner-signin-owner, or " +
                            "Settings › Sign-in from outside in Martlet). Nothing changed.");
                        return 5;
                    }
                    next.Write(directory);
                    current = next;
                }
                output.WriteLine($"Host {config.HostId} {(changed ? "now has" : "has")} {current.Describe()}." +
                    (changed ? " Restart the service to apply it; member desktops add the outside addresses to your network on their next sync." : ""));
                if ((current.Outside.Count > 0 || current.AllowPairingOutsideHome) && blocked is not null)
                    output.WriteLine($"Outside access paused: {GatewayOutsideAccess.Describe(blocked)} ({blocked}). Requests from outside home are refused " +
                        "until sign-in is set up again or the outside addresses are removed (they are kept).");
                return 0;
            }
            if (HostSignIn.Handles(options.Command))
                return HostSignIn.Run(options, config, approval, directory, platform.Input, output);
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
                if (owner.Enabled) owner.AttachLogs(new ControlLogStorage(directory));
                PublishMachine(owner, directory, output);
                if (owner.Enabled)
                {
                    owner.AttachCluster(new ControlClusterStorage(directory));
                    owner.AttachVoices(new ControlVoiceStorage(directory));
                    owner.AttachSpeakingVoices(new ControlSpeakingVoiceStorage(directory));
                    owner.AttachCharacterModels(new ControlCharacterModelStorage(directory));
                    owner.AttachCreations(new ControlCreationStorage(directory));
                    owner.AttachHomeAssistant(new ControlHomeAssistantStorage(directory));
                    owner.AttachSettings(new ControlSettingsStorage(directory));
                    owner.AttachMemories(new ControlMemoryStorage(directory));
                    owner.AttachApiKeys(new ControlApiKeyStorage(directory));
                    owner.AttachNetwork(new ControlNetworkStorage(directory));
                    ApplyExposure(owner, directory, output: null);
                    owner.AttachSignIn(new ControlSignInStorage(directory));
                    var (networkState, networkId) = owner.NetworkState;
                    owner.RecordActivity("INFO", networkState switch
                    {
                        "bound" => $"In Martlet network {networkId}: its member desktops pair with this host by themselves.",
                        "removed" => $"Removed from Martlet network {networkId}; a member desktop that pairs again adds it back.",
                        _ => "In no Martlet network yet: the first desktop that pairs adds this host to its network."
                    });
                    AttachCommands(owner, directory);
                    owner.RecordActivity("INFO", config.Roles.Count == 0 ? "Serving with no roles."
                        : "Serving roles: " + string.Join(", ", config.Roles.Select(r =>
                            r.Slots > 1 ? $"{r.Kind} ({r.Model}, {r.Slots} at once)" : $"{r.Kind} ({r.Model})")) + ".");
                }
                await owner.StartAsync(cancellation);
                CheckApproval(directory, config, approval);
                output.WriteLine("serving: approved gateway listener; empty worker registry; no model readiness claim.");
                // A Mac host keeps the Mac from idle sleep while it serves, as a Linux server stays up.
                using var awake = OperatingSystem.IsMacOS() ? MacNative.KeepAwake() : null;
                // A closed authority refuses every connection for good; exit so Docker or systemd reopens the same state.
                while (!owner.AuthorityClosed)
                    await Task.Delay(AuthorityCheckInterval, cancellation);
                owner.RecordActivity("ERROR", "This host's authorization closed (its clock moved back too far or its saved state " +
                    "failed), so it refused every connection. The host service restarts to reopen it; pairings are kept.");
                Report(output, "authority.closed: the authority closed while serving; exiting so the service restarts and reopens the same state.");
                var closedOwner = owner;
                owner = null;
                try { await closedOwner.DisposeAsync(); }
                catch (Exception) { retainedOwner = closedOwner; }
                exit = 4;
            }
            else if (options.Command.StartsWith("owner-", StringComparison.Ordinal))
            {
                // The host's own account running this command (martlet-host --yes, typically driven by the owner's
                // authenticated SSH session from Martlet desktop) is the owner's confirmation; no console is used.
                var init = options.Command == "owner-init";
                approval?.Check(config, directory, requireDigest: init);
                output.WriteLine($"Owner operation {options.Command} for host {config.HostId} at {config.Binding.Origin.CanonicalOrigin} (service UID/GID {config.ServiceUid}/{config.ServiceGid}).");
                config.Recheck(directory);
                if (approval is not null)
                    CheckApproval(directory, config, approval, requireDigest: init);
                owner = platform.OpenHost(init ? "init" : "admin", config, approval, cancellation);
                config.Recheck(directory);
                PublishMachine(owner, directory, output);
                output.WriteLine($"Opened host: {owner.Identity!.HostId}\nSPKI pin: {owner.Identity.SpkiFingerprint}");
                if (options.Command == "owner-pair")
                {
                    // martlet-host pair serves the owner's outside access choices too (typed codes from outside, the real source).
                    // Sign-in decides whether this public endpoint serves outside home, here as in serve (and lets people sign in).
                    if (owner.Enabled) owner.AttachSignIn(new ControlSignInStorage(directory));
                    ApplyExposure(owner, directory, output);
                    exit = await PairOnceAsync(owner, config, directory, options, platform.Input, output, cancellation);
                }
                else
                {
                    directory.WriteApproval(ServiceApproval.Create(config, owner.Identity!));
                    config.Recheck(directory);
                    output.WriteLine("Service-start approval committed for this exact configuration and identity.");
                }
            }
            else
            {
                terminal = platform.OpenTerminal();
                terminal.Check();
                // Local administration may open with an approval for an earlier config (for example after a
                // role was added) so the owner can approve-service again; serve still requires the exact config.
                var exactConfig = options.Command is not ("rebind" or "admin");
                if (options.Command == "rebind")
                {
                    if (approval is null) throw new HostApprovalException();
                    approval.Check(config, directory, requireDigest: false);
                }
                else
                    approval?.Check(config, directory, exactConfig);
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
                    CheckApproval(directory, config, approval, requireDigest: exactConfig);
                owner = platform.OpenHost(options.Command, config, approval, cancellation);
                config.Recheck(directory);
                PublishMachine(owner, directory, output);
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

    /// <summary>network.json in words for status: none, or the network ID with its member counts (no keys or addresses).</summary>
    private static object DescribeNetwork(LinuxControlDirectory directory, string hostId)
    {
        try
        {
            if (directory.Read(LinuxControlDirectory.Network, LinuxControlDirectory.MaximumNetworkBytes) is not { } bytes) return new { state = "unbound" };
            var roster = Martlet.Core.Network.NetworkRoster.Parse(bytes);
            return new
            {
                state = roster.Host(hostId) is { Removed: false } ? "bound" : "removed",
                networkId = roster.NetworkId, desktops = roster.ActiveDesktops.Count(), hosts = roster.ActiveHosts.Count()
            };
        }
        catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or GatewayPersistenceException)
        {
            return new { state = "unreadable" };
        }
    }

    // machine.json is written by martlet-host (setup, add, remove, machine) next to host.json. It is informational
    // only, so a missing, unreadable or malformed file just means paired desktops see "not reported".
    private static void PublishMachine(DurableGatewayHost owner, LinuxControlDirectory directory, TextWriter output)
    {
        if (!owner.Enabled) return;
        GatewayMachineReport? report = null;
        try
        {
            if (directory.Read(LinuxControlDirectory.Machine, GatewayMachineReport.MaximumBytes) is { } bytes)
            {
                report = GatewayMachineReport.Parse(bytes);
                if (report is null)
                {
                    Report(output, "machine.invalid: machine.json ignored; run martlet-host machine to collect it again.");
                    owner.RecordActivity("WARN", "machine.json is invalid, so this host's hardware is not reported. Run martlet-host machine to collect it again.");
                }
            }
        }
        catch (GatewayPersistenceException)
        {
            Report(output, "machine.unreadable: machine.json must be a 0600 file owned by the service user; hardware not reported.");
            owner.RecordActivity("WARN", "machine.json is unreadable (it must be a 0600 file owned by the service user), so hardware is not reported.");
        }
        owner.PublishMachine(report);
    }

    private static void CheckApproval(LinuxControlDirectory directory, HostConfiguration config,
        ServiceApproval approval, bool requireDigest = true)
    {
        approval.Check(config, directory, requireDigest);
        if (ReadApproval(directory) != approval) throw new HostApprovalException();
    }

    /// <summary>One-shot pairing for owner-pair: start the listener, show the invitation, wait until a desktop registers,
    /// then return so the caller can restart the service. A short code has no deadline: it ends when a desktop redeems it,
    /// five wrong tries close it, or it is withdrawn (a "cancel" line, or the end of stdin, so it never outlives the pipe or
    /// session that showed it; a Docker TTY whose console closed never ends, which martlet-host reports as busy). A
    /// --device-id invitation (Martlet redeems it by itself) still ends after five minutes or "cancel".</summary>
    /// <summary>Why sign-in isn't usable on this host (null: usable), from signin.json read as is: no service runs here, and
    /// sign-in's own service would tidy the file as it reads it.</summary>
    internal static string? SignInBlocked(LinuxControlDirectory directory)
    {
        try
        {
            return GatewayOutsideAccess.SignInBlockedReason(directory.Read(LinuxControlDirectory.SignIn, LinuxControlDirectory.MaximumSignInBytes) is { } bytes
                ? GatewaySignInDocument.Parse(bytes) : null);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return "signin.not_set_up"; }
    }

    /// <summary>Serves the owner's outside access choices (exposure.json) on <paramref name="owner"/>: what serve and pair apply.</summary>
    private static void ApplyExposure(DurableGatewayHost owner, LinuxControlDirectory directory, TextWriter? output)
    {
        try
        {
            var exposure = HostExposure.Read(directory);
            owner.Exposure = exposure.ToGateway();
            if (exposure.Outside.Count > 0 || exposure.AllowPairingOutsideHome || exposure.TreatAllAsOutside)
            {
                owner.RecordActivity("INFO", "Reaching this host from outside home: " + exposure.Describe() + ".");
                output?.WriteLine("Reaching this host from outside home: " + exposure.Describe() + ".");
                if ((exposure.Outside.Count > 0 || exposure.AllowPairingOutsideHome) && SignInBlocked(directory) is { } blocked)
                {
                    var paused = $"Outside access paused: {GatewayOutsideAccess.Describe(blocked)}. Requests from outside home are refused until " +
                        "sign-in is set up again or the outside addresses are removed (they are kept).";
                    owner.RecordActivity("WARN", paused);
                    output?.WriteLine(paused);
                }
            }
        }
        catch (HostInputException)
        {
            owner.RecordActivity("WARN", "exposure.json is invalid, so this host uses the defaults (no outside addresses, pairing codes only from home). " +
                "Set it again with martlet-host owner-exposure.");
        }
    }

    private static async Task<int> PairOnceAsync(DurableGatewayHost owner, HostConfiguration config,
        LinuxControlDirectory directory, HostOptions options, TextReader input, TextWriter output, CancellationToken cancellation)
    {
        var device = options.DeviceId;
        var roles = Roles(options.Roles);
        var known = owner.ListRegistrations(cancellation).Select(r => r.CredentialId).ToHashSet(StringComparer.Ordinal);
        config.Recheck(directory);
        await owner.StartAsync(cancellation);
        config.Recheck(directory);
        DateTimeOffset? expiresAt = null;
        string? codeWindow = null;
        if (device is null)
        {
            var code = owner.OpenCodePairing(new() { Roles = roles }, cancellation);
            codeWindow = code.PairingId;
            output.WriteLine(PairingCode.Describe(code));
        }
        else
        {
            var card = owner.OpenPairing(new() { DeviceId = device, DisplayName = options.Name!, Roles = roles }, cancellation);
            expiresAt = card.ExpiresAt;
            output.WriteLine($"Listener started. One-use invitation for device {device} ({string.Join(',', roles)}), host pin {owner.Identity!.SpkiFingerprint}, expires {card.ExpiresAt:O}.");
            output.WriteLine("pairing-code: " + PairingCode.Format(card));
        }
        output.Flush();
        // Console.In reads synchronously, so the watcher runs on its own thread; it is abandoned when pairing ends.
        var canceled = Task.Run(() => WatchForCancel(input, endOfInputCancels: codeWindow is not null), CancellationToken.None);
        while (true)
        {
            // Read before the registrations, so a code redeemed in between is seen as paired on the next pass.
            var open = codeWindow is null || owner.IsPairingOpen(codeWindow, cancellation);
            var registered = owner.ListRegistrations(cancellation)
                .FirstOrDefault(r => (device is null || r.DeviceId == device) && !r.Revoked && !known.Contains(r.CredentialId));
            if (registered is not null)
            {
                output.WriteLine($"Paired: {Display(registered.DeviceId)} ({Display(registered.DisplayName)}), roles {string.Join(',', registered.Roles)}. Permanent until revoked.");
                return 0;
            }
            if (canceled.IsCompletedSuccessfully && canceled.Result)
            {
                output.WriteLine("pairing.canceled: the invitation was withdrawn before a desktop redeemed it.");
                return 3;
            }
            if (!open)
            {
                output.WriteLine("pairing.closed: the code was typed wrong five times, so it no longer works. Run pair again for a new code.");
                return 3;
            }
            if (expiresAt is { } deadline && DateTimeOffset.UtcNow >= deadline)
            {
                output.WriteLine("pairing.expired: no desktop redeemed the invitation within five minutes.");
                return 3;
            }
            await Task.Delay(500, cancellation);
        }
    }

    /// <summary>True once a "cancel" line arrives; the end of input counts too when <paramref name="endOfInputCancels"/>.</summary>
    private static bool WatchForCancel(TextReader input, bool endOfInputCancels)
    {
        try
        {
            while (input.ReadLine() is { } line)
                if (line.Trim() == "cancel") return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        return endOfInputCancels;
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
