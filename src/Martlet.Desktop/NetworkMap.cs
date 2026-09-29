using System.Net;
using Martlet.Avatar.Hosting;
using Martlet.Core.Installation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

internal enum NodeKind { ThisPc, Host, Cloud, Missing, Add }
internal enum NodeHealth { Ready, Unknown, Off, Attention }
internal enum NodeAction
{
    Setup, AudioSetup, Character, ToggleCharacter, Prerequisites, HostThisPc, AddComputer, ManageHost, CheckHost, HostDashboard, Advisor,
    UseForLipSync, LipSyncThisPc, InstallRole, RemoveRole, HostStatus, UpdateHost, ForgetHost,
    PrepareHost, RebootHost, ShutdownHost, WakeHost, PrepareComputer, UseForThinking, UseForListening
}

/// <summary>Who handles lip-sync: a paired host, this PC's own Audio2Face service, or nobody (voice loudness).</summary>
internal enum LipSyncHandler { ThisPc, Host, Loudness }

internal sealed record HostedRole(string Chip, string Name, string Detail);
internal sealed record NodeFact(string Label, string Value);
/// <summary>A node command; <paramref name="Argument"/> names the paired host or role it applies to.</summary>
internal sealed record NodeCommand(NodeAction Action, string Label, bool Primary = false, string? Argument = null);
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
    internal const string CloudGlyph = "\uE753";
    internal const string AddGlyph = "\uE710";

    private sealed class Draft(string id, NodeKind kind, string title, string subtitle, string glyph)
    {
        internal string Id { get; } = id;
        internal NodeKind Kind { get; } = kind;
        internal string Title { get; set; } = title;
        internal string Subtitle { get; set; } = subtitle;
        internal string Glyph { get; } = glyph;
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
        SetupRole.Stt => "Listening (speech-to-text)",
        SetupRole.Llm => "Thinking (conversation model)",
        _ => "Speaking (voice)"
    };

    internal static string RoleChip(SetupRole role) => role switch
    {
        SetupRole.Stt => "Listens",
        SetupRole.Llm => "Thinks",
        _ => "Speaks"
    };

    private static string RouteDetail(SetupRoute route)
    {
        var text = route.VoiceId is { } voice && route.RouteType != SetupRouteType.LocalWindowsTts
            ? $"{route.ModelId}, voice {voice}" : route.VoiceId ?? route.ModelId;
        if (route.Enabled == false) text += " (turned off)";
        else if (route.Consent is null) text += " (review in Setup)";
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
        SetupRouteType.GatewayOllama => "Ollama on your Martlet host",
        SetupRouteType.GatewayF5 => "F5 on your Martlet host",
        SetupRouteType.GatewayStt => "whisper on your Martlet host",
        SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts => "Windows speech",
        SetupRouteType.LocalWhisper => "whisper.cpp on this PC",
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
                ? "Docker Desktop is running; desktops connect on port 9443"
                : machine.DockerInstalled ? "Docker Desktop is installed but not running" : "Needs Docker Desktop"));
            if (!machine.DockerRunning) thisPc.Worsen(NodeHealth.Attention, machine.DockerInstalled ? "Docker not running" : "Needs Docker");
        }
        else thisPc.Roles.Add(new("App", "Martlet companion", "Conversations, your microphone and speakers"));

        var routes = inputs.Settings?.Setup?.Routes ?? [];
        foreach (var route in routes.OrderBy(r => r.Role))
        {
            var role = new HostedRole(RoleChip(route.Role), RoleName(route.Role), RouteDetail(route));
            Draft target;
            switch (route.RouteType)
            {
                case SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts:
                    target = thisPc;
                    role = role with { Detail = "Windows speech: " + RouteDetail(route) };
                    break;
                case SetupRouteType.LocalWhisper:
                    target = thisPc;
                    role = role with { Detail = "whisper.cpp on this PC: " + RouteDetail(route) };
                    break;
                case SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.GatewayStt when route.Gateway is { } gateway:
                    var host = new Uri(gateway.Origin).Host;
                    target = host == machine.LanAddress || IsLoopback(host)
                        ? thisPc
                        : Node("host:" + gateway.HostId, NodeKind.Host, gateway.HostId, host, ComputerGlyph);
                    role = role with { Detail = route.RouteType switch
                    {
                        SetupRouteType.GatewayOllama => "Ollama: ",
                        SetupRouteType.GatewayStt => "whisper: ",
                        _ => "F5 voice: "
                    } + RouteDetail(route) };
                    if (target != thisPc && target.Facts.Count == 0)
                    {
                        target.Facts.Add(new("Address", gateway.Origin));
                        target.Facts.Add(new("Host ID", gateway.HostId));
                        target.Facts.Add(new("Identity", "Pinned TLS " + Short(gateway.SpkiFingerprint)));
                        AddHardware(target, inputs, gateway.HostId);
                        target.Commands.Add(new(NodeAction.Setup, "Change its routes in Setup", true));
                    }
                    break;
                case SetupRouteType.ChatCompletions when Uri.TryCreate(route.Origin, UriKind.Absolute, out var endpoint):
                    if (IsLoopback(endpoint.Host))
                    {
                        target = thisPc;
                        role = role with { Detail = $"Local server {endpoint.Authority}: " + RouteDetail(route) };
                    }
                    else if (IPAddress.TryParse(endpoint.Host, out var lan) && HostSetupCommands.IsPrivate(lan))
                    {
                        target = Node("lan:" + endpoint.Host, NodeKind.Host, endpoint.Host, "OpenAI-compatible server", ComputerGlyph);
                        if (target.Facts.Count == 0)
                        {
                            target.Facts.Add(new("Address", route.Origin));
                            target.Commands.Add(new(NodeAction.Setup, "Change in Setup", true));
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
            if (route.Enabled == false) target.Worsen(NodeHealth.Off, "Turned off");
            else if (route.Consent is null) target.Worsen(NodeHealth.Attention, "Needs review in Setup");
            if (target.Kind == NodeKind.Cloud)
            {
                target.Facts.Add(new(RoleChip(route.Role), route.CredentialId is null ? "No key saved" : "Key saved in Windows Credential Manager"));
                target.Notes.Add(route.Role switch
                {
                    SetupRole.Stt => "Your recorded push-to-talk audio is sent here.",
                    SetupRole.Llm => "Your messages and recent conversation are sent here.",
                    _ => "Reply text is sent here to be spoken."
                });
            }
        }

        if (inputs.Role == DeviceRole.Companion && routes.All(r => r.Role != SetupRole.Llm))
        {
            var missing = Node("missing:brain", NodeKind.Missing, "Conversation model", "Not chosen yet", CloudGlyph);
            missing.Worsen(NodeHealth.Attention, "Choose one");
            missing.Roles.Add(new("Thinks", "Thinking (conversation model)", "Pick a cloud model (OpenRouter, NVIDIA Build, OpenAI) or one on your own computers."));
            missing.Commands.Add(new(NodeAction.Setup, "Choose how Martlet thinks", true));
            missing.Commands.Add(new(NodeAction.Advisor, "Get a recommendation"));
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
            var before = target.Roles.Count;
            foreach (var role in HostRoles.All)
            {
                var model = check?.Offers?.GetValueOrDefault(role.Kind);
                if (role.Kind == HostRoles.Audio2Face && inCharge)
                    target.Roles.Add(new(role.Chip, role.Name, "In charge of lip-sync. " +
                        (check?.Text ?? "Use Check connection to see whether it runs Audio2Face.")));
                // Thinking and listening are listed with their routes when this host does them.
                else if (model is not null && !(role.Kind == HostRoles.Ollama && thinks) && !(role.Kind == HostRoles.Stt && listens))
                    target.Roles.Add(new(role.Chip, role.Name, $"Installed (model {model}), standing by. Hand it {role.Job} to use it."));
            }
            if (local) thisPc.Roles.Add(new("Host", "Martlet host service (Docker)", $"Paired as {paired.HostId} on {paired.Pairing.Origin}"));
            else
            {
                if (target.Roles.Count == before)
                    target.Roles.Add(new("Host", "Martlet host", check?.Text ?? "Paired. Nothing is handed to it yet; use Check connection to see what it runs."));
                if (target.Facts.All(f => f.Label != "Address"))
                {
                    target.Facts.Insert(0, new("Address", paired.Pairing.Origin));
                    target.Facts.Add(new("Identity", "Pinned TLS " + Short(paired.Pairing.SpkiFingerprint)));
                }
                target.Facts.Add(new("This PC as", paired.Pairing.DeviceId));
                target.Facts.Add(new("Reached via", paired.Reach));
                AddHardware(target, inputs, paired.HostId);
                if (check is null) target.Worsen(NodeHealth.Unknown, "Paired, not checked yet");
                else if (check.Reachable == false) target.Worsen(NodeHealth.Attention, "Not reachable");
                else if (check.Reachable is null) target.Worsen(NodeHealth.Unknown, "Checking...");
                else if (target.Health == NodeHealth.Ready) target.HealthText = inCharge ? "Connected, in charge of lip-sync" : "Connected";
            }

            var id = paired.HostId;
            var app = AppVersions.Current;
            var saved = inputs.HostHardware?.FirstOrDefault(h => h.HostId == id)?.MartletVersion;
            var reported = check?.Reachable == true ? check.MartletVersion ?? saved : saved;
            var known = check?.Reachable == true || reported is not null;
            var outdated = known && AppVersions.IsOlder(reported, app);
            target.Facts.Add(new(local ? "Host service" : "Martlet", !known ? "Version not reported yet. Use Check connection."
                : reported is null ? $"0.2.0 or older; this PC runs {app}. Update it."
                : outdated ? $"{reported}; this PC runs {app}. Update it."
                : reported == app ? $"{reported}, same as this PC" : $"{reported}, newer than this PC ({app}). Update this PC."));
            if (outdated && !local) target.Worsen(NodeHealth.Attention, "Update available");
            if (inputs.HostUpdates?.GetValueOrDefault(id) is { } update) target.Notes.Insert(0, update);
            var offersFace = check?.Offers?.ContainsKey(HostRoles.Audio2Face) == true;
            var offersThinking = check?.Offers?.ContainsKey(HostRoles.Ollama) == true;
            target.Commands.Insert(0, new(NodeAction.CheckHost, "Check connection", !local && check?.Reachable != true, id));
            target.Commands.Add(new(NodeAction.UpdateHost, local ? "Update this PC's host service" : outdated ? $"Update it to Martlet {app}" : "Update host",
                outdated && check?.Reachable == true, id));
            if (companion && !inCharge)
                target.Commands.Add(new(NodeAction.UseForLipSync, local ? "Hand lip-sync to this PC's host service" : "Hand lip-sync to this computer",
                    offersFace, id));
            if (companion && !thinks)
                target.Commands.Add(new(NodeAction.UseForThinking, local ? "Hand thinking to this PC's host service" : "Hand thinking to this computer",
                    offersThinking, id));
            if (companion && !listens)
                target.Commands.Add(new(NodeAction.UseForListening, local ? "Hand listening to this PC's host service" : "Hand listening to this computer",
                    check?.Offers?.ContainsKey(HostRoles.Stt) == true, id));
            foreach (var role in HostRoles.All)
            {
                var offered = check?.Offers?.ContainsKey(role.Kind) == true;
                if (!offered)
                    target.Commands.Add(new(NodeAction.InstallRole, local ? $"Install {role.Name} in this PC's host service" : $"Install {role.Name} there",
                        Argument: id + "/" + role.Kind));
                if (check is null || offered)
                    target.Commands.Add(new(NodeAction.RemoveRole, local ? $"Remove {role.Name} from this PC's host service" : $"Remove {role.Name} from it",
                        Argument: id + "/" + role.Kind));
            }
            target.Commands.Add(new(NodeAction.HostStatus, "Show its status", Argument: id));
            if (!local && paired.Method != HostSetupMethod.ThisPcDocker)
            {
                // Linux computers: set them up and power them from here (martlet-prepare over SSH, Wake-on-LAN).
                target.Commands.Add(new(NodeAction.PrepareHost, "Prepare this computer (drivers, Docker, GPU, tools)", Argument: id));
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
            target.Commands.Add(new(NodeAction.ManageHost, "Pair again or change its setup", Argument: id));
            target.Commands.Add(new(NodeAction.ForgetHost, "Forget this host", Argument: id));
            if (!paired.CanLaunch && !local)
                target.Notes.Add("Tell Martlet how to reach it (below) to install or remove roles from here; otherwise it shows the command to run there.");
        }

        if (companion)
        {
            var character = inputs.Avatar is { } avatar
                ? BundledLive2DName(avatar.ModelPath) ?? System.IO.Path.GetFileNameWithoutExtension(avatar.ModelPath)
                : "Hiyori (built-in)";
            thisPc.Roles.Add(new("Character", "Character", $"{character}, {(inputs.CharacterShowing ? "on your desktop now" : "hidden")}"));
            var endpoint = new Uri(inputs.Avatar?.Endpoint ?? AvatarProfile.DefaultEndpoint).Authority;
            switch (lipSync)
            {
                case LipSyncHandler.ThisPc:
                    thisPc.Roles.Add(new("Lip-sync", "Lip-sync", inputs.Avatar?.LipSync == AvatarLipSync.Audio2Face
                        ? "In charge: explicit Audio2Face activation from Character settings"
                        : $"In charge: this PC's Audio2Face service at {endpoint} when it runs, otherwise voice loudness"));
                    break;
                case LipSyncHandler.Loudness:
                    thisPc.Roles.Add(new("Lip-sync", "Lip-sync", "In charge: the mouth follows the voice's loudness (Audio2Face is off)"));
                    thisPc.Commands.Add(new(NodeAction.LipSyncThisPc, "Turn on Audio2Face lip-sync here"));
                    break;
                default:
                    thisPc.Commands.Add(new(NodeAction.LipSyncThisPc, "Take lip-sync back to this PC"));
                    break;
            }
            var audio = inputs.Settings?.Audio is { } devices
                ? devices.Input.Checkpoint is not null && devices.Output.Checkpoint is not null ? "Tested" : "Chosen, not tested yet"
                : "Windows defaults, not tested";
            thisPc.Roles.Add(new("Audio", "Microphone and speakers", audio));
        }

        thisPc.Facts.Add(new("Computer", machine.Name));
        thisPc.Facts.Add(new("Windows", machine.Windows));
        if (machine.Processor is { } cpu) thisPc.Facts.Add(new("Processor", $"{cpu} ({machine.Threads} threads)"));
        if (machine.MemoryGb is { } ram) thisPc.Facts.Add(new("Memory", $"{ram:0} GB"));
        if (machine.Gpus.Count == 0) thisPc.Facts.Add(new("Graphics", "No dedicated GPU found"));
        foreach (var gpu in machine.Gpus) thisPc.Facts.Add(new("Graphics", gpu.Describe()));
        thisPc.Facts.Add(new("Network", machine.LanAddress ?? "No private network address found"));
        thisPc.Facts.Add(new("Docker Desktop", machine.DockerRunning ? "Running" : machine.DockerInstalled ? "Installed, not running" : "Not installed"));
        thisPc.Notes.AddRange(machine.Capabilities());
        if (inputs.Role == DeviceRole.Host)
        {
            thisPc.Commands.Add(new(NodeAction.HostDashboard, "Open host dashboard", true));
            thisPc.Commands.Add(new(NodeAction.Prerequisites, "Prerequisites"));
        }
        else
        {
            thisPc.Commands.Add(new(NodeAction.AudioSetup, "Microphone and speakers", true));
            thisPc.Commands.Add(new(NodeAction.ToggleCharacter, inputs.CharacterShowing ? "Hide character" : "Show character"));
            thisPc.Commands.Add(new(NodeAction.Character, "Character settings"));
            thisPc.Commands.Add(new(NodeAction.HostThisPc, "Run host services on this PC"));
            thisPc.Commands.Add(new(NodeAction.Prerequisites, "Prerequisites"));
        }

        var add = new Draft("add", NodeKind.Add, "Add a computer", "Lend a GPU PC to Martlet", AddGlyph) { Health = NodeHealth.Unknown, HealthText = "" };
        add.Roles.Add(new("Host", "Martlet host", "A spare or gaming PC runs heavy parts, such as thinking, listening or lip-sync, for this PC."));
        add.Notes.Add("Hosts listen only on your private network and are paired once with a one-use code.");
        add.Commands.Add(new(NodeAction.AddComputer, "Add a computer", true));
        add.Commands.Add(new(NodeAction.HostThisPc, "Or run host services on this PC"));
        add.Commands.Add(new(NodeAction.PrepareComputer, "Prepare a Linux computer over SSH"));
        order.Add(add);

        return order.Select(draft => draft.Build()).ToArray();
    }

    /// <summary>Adds what the host last reported about itself (saved on pairing and on Check connection).</summary>
    private static void AddHardware(Draft target, NetworkInputs inputs, string hostId)
    {
        if (target.Facts.Any(f => f.Label is "Hardware" or "Graphics")) return;
        if (inputs.HostHardware?.FirstOrDefault(h => h.HostId == hostId) is not { } report)
        {
            target.Facts.Add(new("Hardware", "Not reported yet. Use Check connection; older hosts need updating first."));
            return;
        }
        if (report.Gpus.Count == 0) target.Facts.Add(new("Graphics", "No dedicated GPU reported"));
        foreach (var gpu in report.Gpus)
        {
            var text = gpu.Driver is { } driver ? $"{gpu.Describe()}, driver {driver}" : gpu.Describe();
            target.Facts.Add(new("Graphics", gpu.DescribePower() is { } power ? $"{text}, {power}" : text));
        }
        if (report.Cuda is { } cuda) target.Facts.Add(new("CUDA", $"up to {cuda} (driver)"));
        if (report.Processor is { } cpu)
            target.Facts.Add(new("Processor", report.ProcessorThreads is { } threads ? $"{cpu} ({threads} threads)" : cpu));
        if (report.MemoryGb is { } memory) target.Facts.Add(new("Memory", $"{memory:0} GB"));
        target.Facts.Add(new("System", report.Kernel is { } kernel ? $"{report.OperatingSystem} (kernel {kernel})" : report.OperatingSystem));
        target.Facts.Add(new("Runs Martlet", report.Method == "docker"
            ? $"In Docker ({report.ContainerRuntime ?? "Docker"})" : "Natively (systemd user service)"));
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
            cloud.Commands.Add(new(NodeAction.Setup, "Change in Setup", true));
        }
        return cloud;
    }

    private static string? BundledLive2DName(string path) =>
        BundledLive2D.IsBuiltIn(path) ? path[BundledLive2D.Prefix.Length..] + " (built-in)" : null;

    private static string Short(string fingerprint) =>
        fingerprint.Length > 19 ? fingerprint[..19] + "\u2026" : fingerprint;
}
