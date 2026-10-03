using System.Net;
using Martlet.Avatar.Hosting;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

internal enum NodeKind { ThisPc, Host, Cloud, Missing, Add }
internal enum NodeHealth { Ready, Unknown, Off, Attention }
internal enum NodeAction
{
    Companion, AudioSetup, Character, ToggleCharacter, Prerequisites, HostThisPc, AddComputer, ManageHost, CheckHost, HostDashboard, Advisor,
    UseForLipSync, LipSyncThisPc, InstallRole, RemoveRole, HostStatus, UpdateHost, ForgetHost,
    PrepareHost, RebootHost, ShutdownHost, WakeHost, PrepareComputer, UseForThinking, UseForListening, UseForSpeaking
}

/// <summary>Who handles lip-sync: a paired host, this PC's own Audio2Face service, or nobody (voice loudness).</summary>
internal enum LipSyncHandler { ThisPc, Host, Loudness }

/// <summary>Something a device does. <paramref name="Component"/> (<see cref="DeviceComponent"/>) groups it with the
/// commands that configure it in the device's details.</summary>
internal sealed record HostedRole(string Chip, string Name, string Detail, string? Component = null);
internal sealed record NodeFact(string Label, string Value);
/// <summary>A node command; <paramref name="Argument"/> names the paired host or role it applies to and
/// <paramref name="Component"/> the hosted role it configures (null: it applies to the whole device).</summary>
internal sealed record NodeCommand(NodeAction Action, string Label, bool Primary = false, string? Argument = null, string? Component = null);

/// <summary>Keys that tie a device's roles to the commands that configure them.</summary>
internal static class DeviceComponent
{
    internal const string App = "app";
    internal const string LipSync = "lipsync";
    internal const string Character = "character";
    internal const string Audio = "audio";
    internal const string HostService = "host-service";
    internal const string Host = "host";
    internal const string Offer = "offer";

    /// <summary>A conversation job (thinking, listening or speaking), wherever it runs.</summary>
    internal static string Job(SetupRole role) => "job:" + role;

    /// <summary>A host role that is installed but does no job for this PC yet.</summary>
    internal static string Standby(string roleKind) => "role:" + roleKind;

    internal static SetupRole? JobRole(string? component) =>
        component is not null && component.StartsWith("job:", StringComparison.Ordinal) &&
        Enum.TryParse<SetupRole>(component[4..], out var role) ? role : null;
}
/// <summary>The last explicit connection check of a paired host; <paramref name="Offers"/> maps each role kind it runs to its model,
/// <paramref name="MartletVersion"/> is the release its gateway reported (null when it is 0.2.0 or older) and
/// <paramref name="Routes"/> holds the routes it advertised.</summary>
internal sealed record HostCheck(bool? Reachable, string Text, IReadOnlyDictionary<string, string>? Offers = null,
    string? MartletVersion = null, IReadOnlyList<Martlet.Avatar.Audio2Face.Remote.HostRoute>? Routes = null);

internal sealed record NetworkNode(string Id, NodeKind Kind, string Title, string Subtitle, string Glyph, NodeHealth Health,
    string HealthText, IReadOnlyList<HostedRole> Roles, IReadOnlyList<NodeFact> Facts, IReadOnlyList<NodeCommand> Commands,
    IReadOnlyList<string> Notes, string? PairedHostId = null);

internal sealed record NetworkInputs(MachineInfo Machine, DeviceRole Role, AppSettings? Settings, AvatarProfile? Avatar,
    bool CharacterShowing, IReadOnlyDictionary<string, HostCheck> HostChecks, IReadOnlyList<HostHardware>? HostHardware = null,
    IReadOnlyList<PairedHost>? Hosts = null, IReadOnlyDictionary<string, string>? HostUpdates = null);

/// <summary>Turns saved settings, the avatar pairing and local hardware into the Devices map: every computer and
/// cloud service, what it runs and what can be configured there. Reads nothing itself.</summary>
internal static class NetworkMap
{
    internal const string ThisPcGlyph = "\uE770";
    internal const string ComputerGlyph = "\uE7F4";
    internal const string PhoneGlyph = "\uE8EA";
    internal const string CloudGlyph = "\uE753";
    internal const string AddGlyph = "\uE710";

    private sealed class Draft(string id, NodeKind kind, string title, string subtitle, string glyph)
    {
        internal string Id { get; } = id;
        internal NodeKind Kind { get; } = kind;
        internal string Title { get; set; } = title;
        internal string Subtitle { get; set; } = subtitle;
        internal string Glyph { get; set; } = glyph;
        internal NodeHealth Health { get; set; } = NodeHealth.Ready;
        internal string HealthText { get; set; } = "Ready";
        internal List<HostedRole> Roles { get; } = [];
        internal List<NodeFact> Facts { get; } = [];
        internal List<NodeCommand> Commands { get; } = [];
        internal List<string> Notes { get; } = [];
        internal string? PairedHostId { get; set; }

        internal void Worsen(NodeHealth health, string text)
        {
            if (health <= Health) return;
            Health = health;
            HealthText = text;
        }

        internal NetworkNode Build() => new(Id, Kind, Title, Subtitle, Glyph, Health, HealthText, Roles, Facts, Commands, Notes, PairedHostId);
    }

    internal static LipSyncHandler LipSync(AvatarProfile? avatar) =>
        avatar?.LipSync == AvatarLipSync.Loudness ? LipSyncHandler.Loudness
        : avatar?.RemoteHost is not null && avatar.LipSync == AvatarLipSync.Auto ? LipSyncHandler.Host
        : LipSyncHandler.ThisPc;

    /// <summary>The paired host whose Ollama answers conversations (the LLM route is a gateway Ollama route), or null.</summary>
    internal static string? ThinkingHost(AppSettings? settings) => JobHost(settings, SetupRole.Llm);

    /// <summary>The paired host that does a job (the role's route is one of its gateway routes), or null.</summary>
    internal static string? JobHost(AppSettings? settings, SetupRole role) =>
        settings?.Setup?.Routes.FirstOrDefault(r => r.Role == role) is { Gateway: { } gateway } route &&
        SelfHostSetup.IsGateway(route.RouteType) ? gateway.HostId : null;

    /// <summary>All paired hosts, including a lip-sync pairing saved only in the avatar profile.</summary>
    internal static IReadOnlyList<PairedHost> Hosts(NetworkInputs inputs)
    {
        var hosts = (inputs.Hosts ?? []).ToList();
        if (inputs.Avatar?.RemoteHost is { } assigned && hosts.All(h => h.HostId != assigned.HostId))
            hosts.Add(new() { Pairing = assigned });
        return hosts;
    }

    internal static string RoleName(SetupRole role) => role switch
    {
        SetupRole.Stt => "Listening",
        SetupRole.Llm => "Thinking",
        _ => "Speaking"
    };

    internal static string RoleChip(SetupRole role) => role switch
    {
        SetupRole.Stt => "Listens",
        SetupRole.Llm => "Thinks",
        _ => "Speaks"
    };

    /// <summary>The job's word in Companion's tab names: thinking, listening or voice.</summary>
    internal static string JobWord(SetupRole role) => role switch
    {
        SetupRole.Stt => "listening",
        SetupRole.Llm => "thinking",
        _ => "voice"
    };

    private static string RouteDetail(SetupRoute route)
    {
        var text = route.RouteType == SetupRouteType.LocalWindowsTts ? WindowsVoices.DisplayName(route.VoiceId)
            : route.VoiceId is { } voice ? $"Voice {voice}"
            : route.Reference is { } reference ? $"Voice {reference.PresetName}" : "Ready";
        if (route.Enabled == false) text += " (turned off)";
        else if (route.Consent is null) text += " (review in Companion)";
        return text;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    internal static bool IsReady(SetupRoute? route) => route is not null && route.Consent is not null && route.Enabled != false;

    internal static string ProviderName(SetupRoute route) => route.RouteType switch
    {
        SetupRouteType.ChatCompletions => ChatCompletionsEndpointCatalog.Named(route.Origin)?.Name ??
            (Uri.TryCreate(route.Origin, UriKind.Absolute, out var endpoint)
                ? IsLoopback(endpoint.Host) ? "A local server on this PC" : endpoint.Host
                : "Your endpoint"),
        SetupRouteType.GatewayOllama => "Your Martlet host",
        SetupRouteType.GatewayF5 => "Your Martlet host",
        SetupRouteType.GatewayStt => "Your Martlet host",
        SetupRouteType.LocalWindowsStt => "Windows speech",
        SetupRouteType.LocalWindowsTts => "Windows voice",
        SetupRouteType.LocalWhisper => "Speech recognition on this PC",
        SetupRouteType.LocalParakeet => "Speech recognition on this PC",
        _ => "OpenAI"
    };

    internal static IReadOnlyList<NetworkNode> Build(NetworkInputs inputs)
    {
        var machine = inputs.Machine;
        var thisPc = new Draft("this-pc", NodeKind.ThisPc, "This PC", machine.Name, ThisPcGlyph);
        var nodes = new Dictionary<string, Draft>(StringComparer.Ordinal);
        var order = new List<Draft> { thisPc };
        Draft Node(string id, NodeKind kind, string title, string subtitle, string glyph)
        {
            if (nodes.TryGetValue(id, out var existing)) return existing;
            var created = new Draft(id, kind, title, subtitle, glyph);
            nodes[id] = created;
            order.Add(created);
            return created;
        }

        if (inputs.Role == DeviceRole.Host)
        {
            thisPc.Roles.Add(new("Host", "Martlet host service", machine.DockerRunning
                ? "Ready for paired computers"
                : machine.DockerInstalled ? "Docker Desktop is installed but not running" : "Needs Docker Desktop", DeviceComponent.HostService));
            if (!machine.DockerRunning) thisPc.Worsen(NodeHealth.Attention, machine.DockerInstalled ? "Docker not running" : "Needs Docker");
        }
        else thisPc.Roles.Add(new("App", "Martlet companion", "Conversations, your microphone and speakers", DeviceComponent.App));

        var routes = inputs.Settings?.Setup?.Routes ?? [];
        foreach (var route in routes.OrderBy(r => r.Role))
        {
            var role = new HostedRole(RoleChip(route.Role), RoleName(route.Role), RouteDetail(route), DeviceComponent.Job(route.Role));
            Draft target;
            switch (route.RouteType)
            {
                case SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts:
                    target = thisPc;
                    role = role with { Detail = (route.RouteType == SetupRouteType.LocalWindowsTts ? "Windows voice: " : "Windows speech: ") + RouteDetail(route) };
                    break;
                case SetupRouteType.LocalWhisper:
                    target = thisPc;
                    role = role with { Detail = "Speech recognition on this PC: " + RouteDetail(route) };
                    break;
                case SetupRouteType.LocalParakeet:
                    target = thisPc;
                    role = role with { Detail = "Speech recognition on this PC: " + RouteDetail(route) };
                    break;
                case SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.GatewayStt when route.Gateway is { } gateway:
                    var host = new Uri(gateway.Origin).Host;
                    target = host == machine.LanAddress || IsLoopback(host)
                        ? thisPc
                        : Node("host:" + gateway.HostId, NodeKind.Host, gateway.HostId, host, ComputerGlyph);
                    role = role with { Detail = route.RouteType switch
                    {
                        SetupRouteType.GatewayOllama => "Conversation model: ",
                        SetupRouteType.GatewayStt => "Speech recognition: ",
                        _ => "Voice: "
                    } + RouteDetail(route) };
                    if (target != thisPc && target.Facts.Count == 0)
                    {
                        target.Facts.Add(new("Address", gateway.Origin));
                        target.Facts.Add(new("Host", gateway.HostId));
                        AddHardware(target, inputs, gateway.HostId);
                    }
                    break;
                case SetupRouteType.ChatCompletions when Uri.TryCreate(route.Origin, UriKind.Absolute, out var endpoint):
                    if (IsLoopback(endpoint.Host))
                    {
                        target = thisPc;
                        role = role with { Detail = "Local server: " + RouteDetail(route) };
                    }
                    else if (IPAddress.TryParse(endpoint.Host, out var lan) && HostSetupCommands.IsPrivate(lan))
                    {
                        target = Node("lan:" + endpoint.Host, NodeKind.Host, endpoint.Host, "OpenAI-compatible server", ComputerGlyph);
                        if (target.Facts.Count == 0)
                        {
                            target.Facts.Add(new("Address", route.Origin));
                        }
                    }
                    else
                    {
                        var name = ChatCompletionsEndpointCatalog.Named(route.Origin)?.Name ?? endpoint.Host;
                        target = Cloud(Node, "cloud:" + endpoint.Host, name, endpoint.Host, route.Origin);
                    }
                    break;
                default:
                    target = Cloud(Node, "cloud:openai", "OpenAI", "api.openai.com", OpenAiSetup.Origin);
                    break;
            }
            target.Roles.Add(role);
            if (target != thisPc)
                target.Commands.Add(new(NodeAction.Companion, $"Change {JobWord(route.Role)} in Companion",
                    target.Commands.All(c => !c.Primary), route.Role.ToString(), DeviceComponent.Job(route.Role)));
            if (route.Enabled == false) target.Worsen(NodeHealth.Off, "Turned off");
            else if (route.Consent is null) target.Worsen(NodeHealth.Attention, "Needs review in Companion");
            if (target.Kind == NodeKind.Cloud)
            {
                target.Facts.Add(new(RoleChip(route.Role), route.CredentialId is null ? "No key saved" : "Key saved"));
                target.Notes.Add(route.Role switch
                {
                    SetupRole.Stt => "Your speech is sent here.",
                    SetupRole.Llm => "Your messages and recent conversation are sent here.",
                    _ => "Reply text is sent here to be spoken."
                });
            }
        }

        if (inputs.Role == DeviceRole.Companion && routes.All(r => r.Role != SetupRole.Llm))
        {
            var missing = Node("missing:brain", NodeKind.Missing, "Conversation model", "Not chosen yet", CloudGlyph);
            missing.Worsen(NodeHealth.Attention, "Choose one");
            missing.Roles.Add(new("Thinks", "Thinking", "Choose a cloud model or one on your own computers.",
                DeviceComponent.Job(SetupRole.Llm)));
            missing.Commands.Add(new(NodeAction.Companion, "Set up thinking in Companion", true, nameof(SetupRole.Llm), DeviceComponent.Job(SetupRole.Llm)));
            missing.Commands.Add(new(NodeAction.Advisor, "Get a recommendation", Component: DeviceComponent.Job(SetupRole.Llm)));
        }

        var lipSync = LipSync(inputs.Avatar);
        var companion = inputs.Role == DeviceRole.Companion;
        foreach (var paired in Hosts(inputs))
        {
            var host = paired.Address;
            var local = host == machine.LanAddress || IsLoopback(host);
            var target = local ? thisPc : Node("host:" + paired.HostId, NodeKind.Host, paired.HostId, host, ComputerGlyph);
            target.PairedHostId = paired.HostId;
            var check = inputs.HostChecks.GetValueOrDefault(paired.HostId);
            var inCharge = lipSync == LipSyncHandler.Host && inputs.Avatar!.RemoteHost!.HostId == paired.HostId;
            var thinks = ThinkingHost(inputs.Settings) == paired.HostId;
            var listens = JobHost(inputs.Settings, SetupRole.Stt) == paired.HostId;
            var speaks = JobHost(inputs.Settings, SetupRole.Tts) == paired.HostId;
            var before = target.Roles.Count;
            // The row a host role shows on: the job it does for this PC, or standing by.
            string RoleComponent(string kind) => kind switch
            {
                HostRoles.Ollama when thinks => DeviceComponent.Job(SetupRole.Llm),
                HostRoles.Stt when listens => DeviceComponent.Job(SetupRole.Stt),
                _ when kind == HostRoles.Speaking && speaks => DeviceComponent.Job(SetupRole.Tts),
                HostRoles.Audio2Face when inCharge => DeviceComponent.LipSync,
                _ => DeviceComponent.Standby(kind)
            };
            foreach (var role in HostRoles.All)
            {
                var model = check?.Offers?.GetValueOrDefault(role.Kind);
                if (role.Kind == HostRoles.Audio2Face && inCharge)
                    target.Roles.Add(new(role.Chip, role.Name, "Handles lip-sync. " +
                        (check?.Text ?? "Use Check connection to see whether it's ready."), DeviceComponent.LipSync));
                // Thinking, listening and speaking are listed with their routes when this host does them.
                else if (model is not null && !(role.Kind == HostRoles.Ollama && thinks) && !(role.Kind == HostRoles.Stt && listens) &&
                    !(role.Kind == HostRoles.Speaking && speaks))
                    target.Roles.Add(new(role.Chip, role.Name, $"Ready. Assign {role.Job} to use it.",
                        DeviceComponent.Standby(role.Kind)));
            }
            if (local) thisPc.Roles.Add(new("Host", "Martlet host service", $"Paired as {paired.HostId}",
                DeviceComponent.HostService));
            else
            {
                if (target.Roles.Count == before)
                    target.Roles.Add(new("Host", "Martlet host", check?.Text ?? "Paired. Check connection to see what it can do.",
                        DeviceComponent.Host));
                if (target.Facts.All(f => f.Label != "Address"))
                {
                    target.Facts.Insert(0, new("Address", paired.Pairing.Origin));
                }
                target.Facts.Add(new("Connection", paired.Reach));
                AddHardware(target, inputs, paired.HostId);
                if (check is null) target.Worsen(NodeHealth.Unknown, "Paired, not checked yet");
                else if (check.Reachable == false) target.Worsen(NodeHealth.Attention, "Not reachable");
                else if (check.Reachable is null) target.Worsen(NodeHealth.Unknown, "Checking...");
                else if (target.Health == NodeHealth.Ready) target.HealthText = inCharge ? "Connected, handling lip-sync" : "Connected";
            }

            var id = paired.HostId;
            var app = AppVersions.Current;
            var saved = inputs.HostHardware?.FirstOrDefault(h => h.HostId == id)?.MartletVersion;
            var reported = check?.Reachable == true ? check.MartletVersion ?? saved : saved;
            var known = check?.Reachable == true || reported is not null;
            var outdated = known && AppVersions.IsOlder(reported, app);
            target.Facts.Add(new(local ? "Host service" : "Martlet", !known ? "Version not reported yet. Use Check connection."
                : reported is null ? $"Needs update. This PC runs {app}."
                : outdated ? $"Needs update from {reported} to {app}."
                : reported == app ? $"{reported}, up to date" : $"{reported}. Update this PC to {app}."));
            if (outdated && !local) target.Worsen(NodeHealth.Attention, "Update available");
            if (inputs.HostUpdates?.GetValueOrDefault(id) is { } update) target.Notes.Insert(0, update);
            var offersFace = check?.Offers?.ContainsKey(HostRoles.Audio2Face) == true;
            var offersThinking = check?.Offers?.ContainsKey(HostRoles.Ollama) == true;
            // What this host can run at all, from the platform and hardware it reported: impossible roles are explained,
            // never offered, and phones and tablets switch their roles on themselves.
            var device = PlatformDevice.FromHost(id, inputs.HostHardware?.FirstOrDefault(h => h.HostId == id));
            var managed = PlatformCatalog.ManagesRolesRemotely(device);
            if (!local && device.Platform is DevicePlatform.Ios or DevicePlatform.Android) target.Glyph = PhoneGlyph;
            bool Can(string kind) => check?.Offers?.ContainsKey(kind) == true ||
                managed && (PlatformCatalog.EngineForHostRole(kind) is not { } engine ||
                    PlatformCatalog.Check(engine, PlatformSide.Host, device).Allowed);
            foreach (var note in PlatformCatalog.HostNotes(device)) target.Notes.Add(note);
            var hostService = local ? DeviceComponent.HostService : null;
            target.Commands.Insert(0, new(NodeAction.CheckHost, "Check connection", !local && check?.Reachable != true, id, hostService));
            if (managed)
                target.Commands.Add(new(NodeAction.UpdateHost, local ? "Update this PC's host service" : outdated ? $"Update to Martlet {app}" : "Update host",
                    outdated && check?.Reachable == true, id, hostService));
            // Handing a job to a role it already runs configures that role's row; otherwise it gives the device a new job.
            string? Ready(string kind) => check?.Offers?.ContainsKey(kind) == true ? DeviceComponent.Standby(kind) : null;
            if (companion && !inCharge && Can(HostRoles.Audio2Face))
                target.Commands.Add(new(NodeAction.UseForLipSync, local ? "Use this PC's host service for lip-sync" : "Use this computer for lip-sync",
                    offersFace, id, Ready(HostRoles.Audio2Face)));
            if (companion && !thinks && Can(HostRoles.Ollama))
                target.Commands.Add(new(NodeAction.UseForThinking, local ? "Use this PC's host service for thinking" : "Use this computer for thinking",
                    offersThinking, id, Ready(HostRoles.Ollama)));
            if (companion && !listens && Can(HostRoles.Stt))
                target.Commands.Add(new(NodeAction.UseForListening, local ? "Use this PC's host service for listening" : "Use this computer for listening",
                    check?.Offers?.ContainsKey(HostRoles.Stt) == true, id, Ready(HostRoles.Stt)));
            if (companion && !speaks && Can(HostRoles.Speaking))
                target.Commands.Add(new(NodeAction.UseForSpeaking, local ? "Use this PC's host service for speaking" : "Use this computer for speaking",
                    check?.Offers?.ContainsKey(HostRoles.Speaking) == true, id, Ready(HostRoles.Speaking)));
            foreach (var role in HostRoles.All)
            {
                var offered = check?.Offers?.ContainsKey(role.Kind) == true;
                if (!managed) continue;
                if (!offered && PlatformCatalog.EngineForHostRole(role.Kind) is { } engine &&
                    PlatformCatalog.Check(engine, PlatformSide.Host, device) is { Allowed: false } cannot)
                {
                    target.Notes.Add($"{role.Name}: {cannot.Reason}");
                    continue;
                }
                if (!offered)
                    target.Commands.Add(new(NodeAction.InstallRole, local ? $"Install {role.Name} here" : $"Install {role.Name}",
                        Argument: id + "/" + role.Kind));
                if (check is null || offered)
                    target.Commands.Add(new(NodeAction.RemoveRole, local ? $"Remove {role.Name} here" : $"Remove {role.Name}",
                        Argument: id + "/" + role.Kind, Component: offered ? RoleComponent(role.Kind) : null));
            }
            if (managed) target.Commands.Add(new(NodeAction.HostStatus, "Show its status", Argument: id, Component: hostService));
            // Home Assistant runs on the host's own network (no gateway route); the host reports it in its machine report.
            var hardware = inputs.HostHardware?.FirstOrDefault(h => h.HostId == id);
            if (HomeAssistantHosts.Runs(hardware))
            {
                target.Roles.Add(new("Home", "Home Assistant", $"Runs at {HomeAssistantHosts.Address(paired).AbsoluteUri.TrimEnd('/')}. " +
                    "Set it up and manage it in Companion > Smart home.", DeviceComponent.Standby(HomeAssistantHosts.Role)));
                target.Commands.Add(new(NodeAction.Companion, "Open Smart home", Argument: nameof(CompanionTab.SmartHome)));
                if (managed && !local)
                    target.Commands.Add(new(NodeAction.RemoveRole, "Remove Home Assistant", Argument: id + "/" + HomeAssistantHosts.Role));
            }
            else if (managed && !local && HomeAssistantHosts.CannotInstall(paired, hardware) is null)
                target.Commands.Add(new(NodeAction.InstallRole, "Install Home Assistant", Argument: id + "/" + HomeAssistantHosts.Role));
            if (!local && managed && paired.Method != HostSetupMethod.ThisPcDocker)
            {
                // Linux computers: set them up and power them from here (martlet-prepare over SSH, Wake-on-LAN).
                target.Commands.Add(new(NodeAction.PrepareHost, "Prepare this computer", Argument: id));
                if (paired.WakeMac is { } mac)
                {
                    target.Commands.Add(new(NodeAction.WakeHost, "Wake it up", check?.Reachable == false, id));
                    target.Facts.Add(new("Wake-on-LAN", mac));
                }
                if (paired.SshTarget is not null)
                {
                    target.Commands.Add(new(NodeAction.RebootHost, "Restart it", Argument: id));
                    target.Commands.Add(new(NodeAction.ShutdownHost, "Shut it down", Argument: id));
                }
            }
            target.Commands.Add(new(NodeAction.ManageHost, "Pair or change setup", Argument: id, Component: hostService));
            target.Commands.Add(new(NodeAction.ForgetHost, "Forget this host", Argument: id, Component: hostService));
            if (!paired.CanLaunch && !local && managed)
                target.Notes.Add("Set the connection method below to install or remove roles from here.");
            else if (paired.Method == HostSetupMethod.Agent && !local && managed)
                target.Notes.Add("Martlet on that computer runs what you ask here (updates, roles, status) over the paired connection.");
        }

        if (companion)
        {
            var character = inputs.Avatar is { } avatar
                ? BundledLive2DName(avatar.ModelPath) ?? System.IO.Path.GetFileNameWithoutExtension(avatar.ModelPath)
                : "Hiyori (built-in)";
            thisPc.Roles.Add(new("Character", "Character", $"{character}, {(inputs.CharacterShowing ? "on your desktop now" : "hidden")}",
                DeviceComponent.Character));
            switch (lipSync)
            {
                case LipSyncHandler.ThisPc:
                    thisPc.Roles.Add(new("Lip-sync", "Lip-sync", inputs.Avatar?.LipSync == AvatarLipSync.Audio2Face
                        ? "Using the lip-sync service from Character settings"
                        : "Using this PC's lip-sync service when available; otherwise voice loudness", DeviceComponent.LipSync));
                    break;
                case LipSyncHandler.Loudness:
                    thisPc.Roles.Add(new("Lip-sync", "Lip-sync", "The mouth follows the voice loudness.",
                        DeviceComponent.LipSync));
                    thisPc.Commands.Add(new(NodeAction.Companion, "Set up lip-sync here", Argument: "LipSync", Component: DeviceComponent.LipSync));
                    break;
                default:
                    thisPc.Commands.Add(new(NodeAction.LipSyncThisPc, "Take lip-sync back to this PC"));
                    break;
            }
            var audio = inputs.Settings?.Audio is { } devices
                ? devices.Input.Checkpoint is not null && devices.Output.Checkpoint is not null ? "Tested"
                    : devices.Input.EndpointId is null && devices.Output.EndpointId is null ? "Windows defaults" : "Chosen devices"
                : "Windows defaults";
            thisPc.Roles.Add(new("Audio", "Microphone and speakers", audio, DeviceComponent.Audio));
        }

        thisPc.Facts.Add(new("Computer", machine.Name));
        thisPc.Facts.Add(new("Windows", machine.Windows));
        if (machine.Processor is { } cpu) thisPc.Facts.Add(new("Processor", $"{cpu} ({machine.Threads} threads)"));
        if (machine.MemoryGb is { } ram) thisPc.Facts.Add(new("Memory", $"{ram:0} GB"));
        if (machine.Gpus.Count == 0) thisPc.Facts.Add(new("Graphics", "No dedicated GPU found"));
        foreach (var gpu in machine.Gpus) thisPc.Facts.Add(new("Graphics", gpu.Describe()));
        thisPc.Facts.Add(new("Network", machine.LanAddress ?? "No local network address found"));
        thisPc.Facts.Add(new("Docker Desktop", machine.DockerRunning ? "Running" : machine.DockerInstalled ? "Installed, not running" : "Not installed"));
        thisPc.Notes.AddRange(machine.Capabilities());
        if (inputs.Role == DeviceRole.Host)
        {
            thisPc.Commands.Add(new(NodeAction.HostDashboard, "Open host dashboard", true, Component: DeviceComponent.HostService));
            thisPc.Commands.Add(new(NodeAction.Prerequisites, "Prerequisites", Component: DeviceComponent.HostService));
        }
        else
        {
            thisPc.Commands.Add(new(NodeAction.AudioSetup, "Choose and test", true, Component: DeviceComponent.Audio));
            thisPc.Commands.Add(new(NodeAction.ToggleCharacter, inputs.CharacterShowing ? "Hide character" : "Show character",
                Component: DeviceComponent.Character));
            thisPc.Commands.Add(new(NodeAction.Character, "Character settings", Component: DeviceComponent.Character));
            thisPc.Commands.Add(new(NodeAction.HostThisPc, "Run host services on this PC"));
            thisPc.Commands.Add(new(NodeAction.Prerequisites, "Prerequisites"));
        }

        var add = new Draft("add", NodeKind.Add, "Add a computer", "Use another computer", AddGlyph) { Health = NodeHealth.Unknown, HealthText = "" };
        add.Roles.Add(new("Host", "Martlet host", "Another computer can help with thinking, speech or lip-sync.",
            DeviceComponent.Offer));
        add.Notes.Add("Pair once with a one-use code on your private network.");
        add.Commands.Add(new(NodeAction.AddComputer, "Add a computer", true));
        add.Commands.Add(new(NodeAction.HostThisPc, "Or run host services on this PC"));
        add.Commands.Add(new(NodeAction.PrepareComputer, "Prepare a Linux computer"));
        order.Add(add);

        return order.Select(draft => draft.Build()).ToArray();
    }

    /// <summary>Adds what the host last reported about itself (saved on pairing and on Check connection).</summary>
    private static void AddHardware(Draft target, NetworkInputs inputs, string hostId)
    {
        if (target.Facts.Any(f => f.Label is "Hardware" or "Graphics")) return;
        if (inputs.HostHardware?.FirstOrDefault(h => h.HostId == hostId) is not { } report)
        {
            target.Facts.Add(new("Hardware", "Not reported yet. Use Check connection."));
            return;
        }
        if (report.Gpus.Count == 0) target.Facts.Add(new("Graphics", "No dedicated GPU reported"));
        foreach (var gpu in report.Gpus)
        {
            var text = gpu.Driver is { } driver ? $"{gpu.Describe()}, driver {driver}" : gpu.Describe();
            target.Facts.Add(new("Graphics", gpu.DescribePower() is { } power ? $"{text}, {power}" : text));
        }
        if (report.Cuda is { } cuda) target.Facts.Add(new("CUDA", $"Up to {cuda}"));
        if (report.Processor is { } cpu)
            target.Facts.Add(new("Processor", report.ProcessorThreads is { } threads ? $"{cpu} ({threads} threads)" : cpu));
        if (report.MemoryGb is { } memory) target.Facts.Add(new("Memory", $"{memory:0} GB"));
        target.Facts.Add(new("System", report.OperatingSystem));
        target.Facts.Add(new("Runs Martlet", report.Method == "docker" ? "In Docker" : "Natively"));
        target.Facts.Add(new("Reported", report.CollectedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)));
        target.Notes.AddRange(report.Capabilities());
    }

    private static Draft Cloud(Func<string, NodeKind, string, string, string, Draft> node, string id, string title, string host, string origin)
    {
        var cloud = node(id, NodeKind.Cloud, title, host, CloudGlyph);
        if (cloud.Facts.Count == 0)
        {
            cloud.Facts.Add(new("Address", origin));
            cloud.Facts.Add(new("Cost", "Requests may cost money on your account"));
        }
        return cloud;
    }

    private static string? BundledLive2DName(string path) =>
        BundledLive2D.IsBuiltIn(path) ? path[BundledLive2D.Prefix.Length..] + " (built-in)" : null;

}
