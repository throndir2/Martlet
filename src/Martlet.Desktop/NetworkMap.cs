using System.Net;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

internal enum NodeKind { ThisPc, Host, Computer, Cloud, Missing, Add }
/// <summary>A device's status. <see cref="Configuring"/>: Martlet is changing it right now (the recommended setup, a host role,
/// following a plan change); it shows over every other status until the change ends.</summary>
internal enum NodeHealth { Ready, Unknown, Off, Attention, Configuring }
internal enum NodeAction
{
    Companion, AudioSetup, Character, ToggleCharacter, Prerequisites, HostThisPc, AddComputer, ManageHost, CheckHost, HostDashboard, Advisor,
    UseForLipSync, LipSyncThisPc, InstallRole, ChangeRole, RemoveRole, HostStatus, UpdateHost, ForgetHost,
    PrepareHost, RebootHost, ShutdownHost, WakeHost, PrepareComputer, UseForThinking, UseForListening, UseForSpeaking,
    MakeHostPc, MakeCompanionPc, OutsideAccess
}

/// <summary>Who handles lip-sync: a paired host, this PC's own Audio2Face service, or nobody (voice loudness).</summary>
internal enum LipSyncHandler { ThisPc, Host, Loudness }

/// <summary>Something a device does. <paramref name="Component"/> (<see cref="DeviceComponent"/>) groups it with the
/// commands that configure it in the device's details.</summary>
internal sealed record HostedRole(string Chip, string Name, string Detail, string? Component = null);
/// <summary>A line in a device's details; <paramref name="AutomationId"/> exposes its value to UI Automation (MCP).</summary>
internal sealed record NodeFact(string Label, string Value, string? AutomationId = null);
/// <summary>A node command; <paramref name="Argument"/> names the paired host or role it applies to and
/// <paramref name="Component"/> the hosted role it configures (null: it applies to the whole device). A command with a
/// <paramref name="Detail"/> is shown as a whole clickable card that says what it does (Add a computer's choices).</summary>
internal sealed record NodeCommand(NodeAction Action, string Label, bool Primary = false, string? Argument = null, string? Component = null,
    string? Detail = null);

/// <summary>Keys that tie a device's roles to the commands that configure them.</summary>
internal static class DeviceComponent
{
    internal const string App = "app";
    internal const string LipSync = "lipsync";
    internal const string Character = "character";
    internal const string Audio = "audio";
    internal const string HostService = "host-service";
    internal const string Host = "host";
    /// <summary>The computers that use a host (paired with it), as the host reports them.</summary>
    internal const string Users = "users";
    /// <summary>Martlet itself on another of your computers (a member of your Martlet network, or one that uses your hosts).</summary>
    internal const string Member = "member";

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
    string? MartletVersion = null, IReadOnlyList<Martlet.Avatar.Audio2Face.Remote.HostRoute>? Routes = null)
{
    /// <summary>How many thinks its Deep thinking role runs at once (its slots: several on one graphics card), or null when it
    /// has no Deep thinking role.</summary>
    public int? DeepThinkingSlots => Routes?.FirstOrDefault(r => r.RouteId == Martlet.Avatar.Audio2Face.Remote.HostRoute.DeepThinkingRouteId)
        ?.MaximumConcurrency;

    /// <summary>The host's refusal code when it answered with one (for example <c>auth.revoked</c> once a friend's host stopped
    /// sharing with this PC), else null.</summary>
    public string? Code { get; init; }
}

/// <summary>A device on the map. <paramref name="HealthCommand"/> is what clicking its status runs, when the status names
/// something one click fixes ("Update available" updates the host). <paramref name="SharedGpu"/> warns that its voice engine
/// shares a Windows computer's graphics card with other work (<see cref="Martlet.Core.Installation.SharedGpu.Warning"/>).</summary>
internal sealed record NetworkNode(string Id, NodeKind Kind, string Title, string Subtitle, string Glyph, NodeHealth Health,
    string HealthText, IReadOnlyList<HostedRole> Roles, IReadOnlyList<NodeFact> Facts, IReadOnlyList<NodeCommand> Commands,
    IReadOnlyList<string> Notes, string? PairedHostId = null, NodeCommand? HealthCommand = null, string? SharedGpu = null);

internal sealed record NetworkInputs(MachineInfo Machine, DeviceRole Role, AppSettings? Settings, AvatarProfile? Avatar,
    bool CharacterShowing, IReadOnlyDictionary<string, HostCheck> HostChecks, IReadOnlyList<HostHardware>? HostHardware = null,
    IReadOnlyList<PairedHost>? Hosts = null, IReadOnlyDictionary<string, string>? HostUpdates = null,
    IReadOnlyDictionary<string, IReadOnlyList<HostUser>>? HostUsers = null, ClusterPlan? Plan = null,
    IReadOnlyList<MartletComputer>? Computers = null, IReadOnlyCollection<string>? DeepThinkingHosts = null,
    IReadOnlyDictionary<string, string>? HostOutside = null, string? OwnHostTrouble = null,
    IReadOnlyCollection<string>? ThinkingPoolLeft = null, IReadOnlyDictionary<string, TimeSpan>? HostAway = null,
    IReadOnlyDictionary<string, string>? Configuring = null, IReadOnlyList<PairedHost>? SharedHosts = null);

/// <summary>A computer paired with a host, in words ("IMOUTO (desktop-imouto), active now"); <paramref name="ThisPc"/> marks
/// this PC itself.</summary>
internal sealed record HostUser(string Text, bool ThisPc);

/// <summary>Where another Martlet computer stands with your Martlet network. <see cref="Friend"/>: a friend's computer that
/// signed in to one of your hosts as a friend; it uses that host's engines only and never joins.</summary>
internal enum ComputerStanding { Member, Asking, Outside, Friend }

/// <summary>Another computer that runs Martlet: a member of your Martlet network, one asking to join it (with its check number
/// and the host it asked through) or one that uses your hosts outside it. <paramref name="Activity"/> is where it was last
/// active ("Active now on diva-host."), as your hosts report it; <paramref name="Role"/> and <paramref name="HostId"/> are what
/// it says it is (companion or host PC) and the host service Martlet runs on it, null until it shares them.
/// <paramref name="Asked"/> is what one of your computers (<paramref name="AskedBy"/>, at <paramref name="AskedAt"/>) asked it
/// to become, while it hasn't switched yet.</summary>
internal sealed record MartletComputer(string DeviceId, string Name, ComputerStanding Standing, string? Activity, bool Active,
    string? CheckNumber = null, string? Through = null, DeviceRole? Role = null, string? HostId = null,
    DeviceRole? Asked = null, string? AskedBy = null, DateTimeOffset? AskedAt = null);

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
        internal NodeCommand? HealthCommand { get; set; }
        internal string? SharedGpu { get; set; }

        /// <summary>Shows a worse status; returns whether <paramref name="text"/> is now the status shown.</summary>
        internal bool Worsen(NodeHealth health, string text)
        {
            if (health <= Health) return false;
            Health = health;
            HealthText = text;
            return true;
        }

        /// <summary>Shows that Martlet changes this device now, with the step it works on.</summary>
        internal void Configure(string step)
        {
            Health = NodeHealth.Configuring;
            HealthText = "Configuring: " + step;
        }

        internal NetworkNode Build() =>
            new(Id, Kind, Title, Subtitle, Glyph, Health, HealthText, Roles, Facts, Commands, Notes, PairedHostId, HealthCommand, SharedGpu);
    }

    /// <summary>" Asked by DESK-A at 1:05 AM to become a host PC: ..." while another computer's ask waits, else "".</summary>
    private static string Asked(MartletComputer computer) => computer.Asked is not { } asked ? ""
        : $" Asked by {computer.AskedBy}{(computer.AskedAt is { } at ? $" at {at.ToLocalTime():g}" : "")} to become a " +
          $"{(asked == DeviceRole.Host ? "host" : "companion")} PC: it switches the next time Martlet there syncs its settings (an older " +
          "Martlet there needs updating first).";

    /// <summary>What another computer says when it is a host PC without a host service of its own yet.</summary>
    internal const string NoHostServiceYet = "Its host service isn't set up yet, so it does no work for your other computers until someone " +
        "at it chooses Set up host service on its Home (once; Windows may ask to allow it).";

    /// <summary>The command that switches another of your computers between companion and host PC (any of your computers can
    /// switch any other; it follows on its next settings sync), or, while an ask waits, the opposite one, which withdraws it.
    /// Null for a computer asking to join the network and for one that hasn't said what it is (an older Martlet).</summary>
    internal static NodeCommand? RoleCommand(MartletComputer computer) =>
        computer.Standing is ComputerStanding.Asking or ComputerStanding.Friend || computer.Role is not { } role ? null
        : (computer.Asked ?? role) == DeviceRole.Companion
            ? new(NodeAction.MakeHostPc, "Make it a host PC", Argument: computer.DeviceId, Component: DeviceComponent.Member)
            : new(NodeAction.MakeCompanionPc, role == DeviceRole.Companion ? "Keep it a companion PC" : "Make it a companion PC",
                Argument: computer.DeviceId, Component: DeviceComponent.Member);

    /// <summary>What another computer is, for Settings › What this PC is for: "Companion PC.", "Host PC, runs diva-host.", a host
    /// PC without its host service yet, or that it hasn't said (an older Martlet); then a waiting ask and where it was last active.
    /// <paramref name="pairedHost"/> is the host service of that computer this PC is paired with, if any (a computer on an
    /// older Martlet may not name it).</summary>
    internal static string RoleLine(MartletComputer computer, string? pairedHost)
    {
        var host = computer.HostId ?? pairedHost;
        var what = computer.Role switch
        {
            DeviceRole.Companion => "Companion PC" + (host is not null ? $" that also runs a host service ({host})." : "."),
            DeviceRole.Host => host is not null ? $"Host PC, runs {host}." : "Host PC. " + NoHostServiceYet,
            _ => "It hasn't said whether it is a companion or a host PC: it runs an older Martlet. Update it to switch it from here."
        };
        return what + Asked(computer) + (computer.Activity is { } activity ? " " + activity : "");
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

    /// <summary>All paired hosts, including a lip-sync pairing saved only in the avatar profile; never a host a friend shares
    /// with this PC (<see cref="NetworkInputs.SharedHosts"/>), even when it does this PC's lip-sync.</summary>
    internal static IReadOnlyList<PairedHost> Hosts(NetworkInputs inputs)
    {
        var hosts = (inputs.Hosts ?? []).Where(h => !h.Shared).ToList();
        if (inputs.Avatar?.RemoteHost is { } assigned && hosts.All(h => h.HostId != assigned.HostId) &&
            inputs.SharedHosts?.Any(h => h.HostId == assigned.HostId) != true)
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
            : route.ClonedVoice is { } cloned ? $"Voice {cloned.Name}, cloned"
            : route.VoiceId is { } voice ? $"Voice {voice}"
            : route.Reference is { } reference ? $"Voice {reference.PresetName}"
            // The model is what tells two setups of one provider apart (the cloud model every computer uses, say).
            : route.RouteType is SetupRouteType.ChatCompletions or SetupRouteType.OpenAi or SetupRouteType.GatewayOllama or
                SetupRouteType.GatewayStt or SetupRouteType.LocalWhisper or SetupRouteType.LocalParakeet &&
                route.ModelId is { Length: > 0 } model ? model
            : "Ready";
        if (route.Enabled == false) text += " (turned off)";
        else if (route.Consent is null) text += " (review in Companion)";
        return text;
    }

    /// <summary>The cluster job a conversation role is.</summary>
    internal static string ClusterJob(SetupRole role) => role switch
    {
        SetupRole.Llm => ClusterJobs.Thinking,
        SetupRole.Stt => ClusterJobs.Listening,
        _ => ClusterJobs.Speaking
    };

    private static bool IsGatewayRoute(SetupRouteType? type) =>
        type is SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.GatewayStt;

    /// <summary>A shared route that runs on each companion PC itself (a Windows voice, Parakeet, a local server), in words for
    /// another computer's row; null for a cloud or host route.</summary>
    private static string? OnEachPc(SetupRoute route) => route.RouteType switch
    {
        SetupRouteType.LocalWindowsStt => "Windows speech recognition on that PC",
        SetupRouteType.LocalWindowsTts => "Windows voice on that PC: " + RouteDetail(route),
        SetupRouteType.LocalWhisper or SetupRouteType.LocalParakeet => "Speech recognition on that PC: " + RouteDetail(route),
        SetupRouteType.ChatCompletions when Uri.TryCreate(route.Origin, UriKind.Absolute, out var endpoint) && IsLoopback(endpoint.Host) =>
            "A local server on that PC: " + RouteDetail(route),
        _ => null
    };

    /// <summary>A job the shared plan gives a host, in words, with the model that host last reported running for it.</summary>
    private static string PlannedDetail(ClusterPlan plan, string hostId, SetupRole role)
    {
        var roles = plan.Node(hostId)?.Roles ?? [];
        var runs = role switch
        {
            SetupRole.Llm => roles.FirstOrDefault(r => r.Kind == HostRoles.Ollama),
            SetupRole.Stt => roles.FirstOrDefault(r => r.Kind == HostRoles.Stt),
            _ => roles.FirstOrDefault(r => HostRoles.Speaks(r.Kind))
        };
        var what = role switch
        {
            SetupRole.Llm => "Conversation model",
            SetupRole.Stt => "Speech recognition",
            _ => runs is not null ? SpeechEngines.ForRoleKind(runs.Kind)?.Name ?? "Voice" : "Voice"
        };
        return $"{what}{(runs?.Model is { Length: > 0 } model ? ": " + model : "")}, for your companion PCs";
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    internal static bool IsReady(SetupRoute? route) => route is not null && route.Consent is not null && route.Enabled != false;

    internal static string ProviderName(SetupRoute route) => route.RouteType switch
    {
        SetupRouteType.ChatCompletions => ChatCompletionsEndpointCatalog.Named(route.Origin)?.Name ??
            (Uri.TryCreate(route.Origin, UriKind.Absolute, out var endpoint)
                ? IsLoopback(endpoint.Host) ? Martlet.Providers.LocalModelServers.AppAt(route.Origin)?.Name ?? "A model app on this PC" : endpoint.Host
                : "Your endpoint"),
        SetupRouteType.GatewayOllama => "Your Martlet host",
        SetupRouteType.GatewayF5 => "Your Martlet host",
        SetupRouteType.GatewayStt => "Your Martlet host",
        SetupRouteType.LocalWindowsStt => "Windows speech",
        SetupRouteType.LocalWindowsTts => "Windows voice",
        SetupRouteType.LocalWhisper => "Speech recognition on this PC",
        SetupRouteType.LocalParakeet => "Speech recognition on this PC",
        SetupRouteType.ElevenLabs => "ElevenLabs",
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

        // Every computer draws the same picture from what your computers share. A host PC uses no jobs itself, so each job shows
        // where the shared plan puts it for your companion PCs, not the Setup choice this PC kept from before it became a host.
        var shared = inputs.Plan;
        var plan = inputs.Role == DeviceRole.Host ? shared : null;
        string? Planned(string job) => plan?.For(job) is { HostId: { } id, Off: false } ? id : null;
        // Jobs that run on each companion PC itself (a Windows voice, Parakeet): shown on your other companion PCs too.
        var onEachPc = new List<HostedRole>();
        Draft HostDraft(string hostId)
        {
            var paired = Hosts(inputs).FirstOrDefault(h => h.HostId == hostId);
            var address = paired?.Address ?? (shared?.Node(hostId)?.Origin is { } origin && Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : null);
            if (address is not null && (address == machine.LanAddress || IsLoopback(address))) return thisPc;
            var draft = Node("host:" + hostId, NodeKind.Host, hostId, address ?? hostId, ComputerGlyph);
            if (paired is null && draft.Facts.Count == 0)
            {
                if (shared?.Node(hostId)?.Origin is { } planned) draft.Facts.Add(new("Address", planned));
                draft.Facts.Add(new("Host", hostId));
                AddHardware(draft, inputs, hostId);
                draft.Worsen(NodeHealth.Unknown, "Not paired with this PC");
                draft.Notes.Add("Your other computers use this host. It pairs with this PC by itself once it's in your Martlet network.");
            }
            return draft;
        }

        var routes = inputs.Settings?.Setup?.Routes ?? [];
        foreach (var route in routes.OrderBy(r => r.Role))
        {
            var job = ClusterJob(route.Role);
            if (plan?.For(job) is { } assignment)
            {
                // A host does it (shown on that host below), or this PC's own route is a host's from before it became a host.
                if (assignment.HostId is not null || IsGatewayRoute(route.RouteType)) continue;
                if (OnEachPc(route) is { } each)
                {
                    onEachPc.Add(new(RoleChip(route.Role), RoleName(route.Role), each, DeviceComponent.Job(route.Role)));
                    continue;
                }
            }
            else if (shared?.For(job) is { HostId: null } && OnEachPc(route) is { } same)
                onEachPc.Add(new(RoleChip(route.Role), RoleName(route.Role), same, DeviceComponent.Job(route.Role)));
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
            missing.Roles.Add(new("Thinks", "Thinking", "Martlet needs this to answer you. Run a free model on your own computers (a graphics card, or the processor if there is none), or choose an online one.",
                DeviceComponent.Job(SetupRole.Llm)));
            missing.Commands.Add(new(NodeAction.Companion, "Set up thinking in Companion", true, nameof(SetupRole.Llm), DeviceComponent.Job(SetupRole.Llm)));
            missing.Commands.Add(new(NodeAction.Advisor, "Get a recommendation", Component: DeviceComponent.Job(SetupRole.Llm)));
        }

        if (plan is not null)
        {
            foreach (var setupRole in new[] { SetupRole.Llm, SetupRole.Stt, SetupRole.Tts })
                if (Planned(ClusterJob(setupRole)) is { } hostId)
                    HostDraft(hostId).Roles.Add(new(RoleChip(setupRole), RoleName(setupRole), PlannedDetail(plan, hostId, setupRole),
                        DeviceComponent.Job(setupRole)));
            // Lip-sync on a paired host shows with that host's roles below.
            if (Planned(ClusterJobs.LipSync) is { } face && Hosts(inputs).All(h => h.HostId != face))
                HostDraft(face).Roles.Add(new("Lip-sync", "Lip-sync", "Handles lip-sync for your companion PCs.", DeviceComponent.LipSync));
        }

        var lipSync = LipSync(inputs.Avatar);
        var companion = inputs.Role == DeviceRole.Companion;
        string? ownHostId = null;
        foreach (var paired in Hosts(inputs))
        {
            var host = paired.Address;
            var local = host == machine.LanAddress || IsLoopback(host);
            if (local) ownHostId = paired.HostId;
            var target = local ? thisPc : Node("host:" + paired.HostId, NodeKind.Host, paired.HostId, host, ComputerGlyph);
            target.PairedHostId = paired.HostId;
            var check = inputs.HostChecks.GetValueOrDefault(paired.HostId);
            // A host PC reads who does what from the shared plan; a companion PC from what it uses itself.
            var inCharge = plan is not null ? Planned(ClusterJobs.LipSync) == paired.HostId
                : lipSync == LipSyncHandler.Host && inputs.Avatar!.RemoteHost!.HostId == paired.HostId;
            var thinks = plan is not null ? Planned(ClusterJobs.Thinking) == paired.HostId : ThinkingHost(inputs.Settings) == paired.HostId;
            var listens = plan is not null ? Planned(ClusterJobs.Listening) == paired.HostId : JobHost(inputs.Settings, SetupRole.Stt) == paired.HostId;
            var speaks = plan is not null ? Planned(ClusterJobs.Speaking) == paired.HostId : JobHost(inputs.Settings, SetupRole.Tts) == paired.HostId;
            // The voice engine that speaks: on a host PC the one this host runs (it runs one at a time), else this PC's choice.
            var speaking = plan is not null
                ? plan.Node(paired.HostId)?.Roles.FirstOrDefault(r => HostRoles.Speaks(r.Kind))?.Kind ??
                    check?.Offers?.Keys.FirstOrDefault(HostRoles.Speaks) ?? HostRoles.Speaking
                : HostRoles.Speaking;
            var before = target.Roles.Count;
            // The row a host role shows on: the job it does for this PC, or standing by.
            string RoleComponent(string kind) => kind switch
            {
                HostRoles.Ollama when thinks => DeviceComponent.Job(SetupRole.Llm),
                HostRoles.Stt when listens => DeviceComponent.Job(SetupRole.Stt),
                _ when kind == speaking && speaks => DeviceComponent.Job(SetupRole.Tts),
                HostRoles.Audio2Face when inCharge => DeviceComponent.LipSync,
                _ => DeviceComponent.Standby(kind)
            };
            foreach (var role in HostRoles.All)
            {
                var model = check?.Offers?.GetValueOrDefault(role.Kind);
                if (role.Kind == HostRoles.Audio2Face && inCharge)
                    target.Roles.Add(new(role.Chip, role.Name, (plan is not null ? "Handles lip-sync for your companion PCs. " : "Handles lip-sync. ") +
                        (check?.Text ?? "Use Check connection to see whether it's ready."), DeviceComponent.LipSync));
                // The Thinking pool is this PC's own: a computer with its role joins by itself unless the owner keeps it out.
                else if (role.Kind == HostRoles.DeepThinking && model is not null)
                    target.Roles.Add(new(role.Chip, role.Name, companion && inputs.DeepThinkingHosts?.Contains(paired.HostId) == true
                        ? $"Thinks things over in the background for this PC ({model})."
                        : inputs.ThinkingPoolLeft?.Contains(paired.HostId) == true
                        ? $"Ready ({model}). You keep it out of the Thinking pool: tick it in Companion > Thinking pool to add it again."
                        : $"Ready ({model}). It joins the Thinking pool by itself unless you keep it out in Companion > Thinking pool.",
                        DeviceComponent.Standby(role.Kind)));
                // Thinking, listening and speaking are listed with their routes when this host does them.
                else if (model is not null && !(role.Kind == HostRoles.Ollama && thinks) && !(role.Kind == HostRoles.Stt && listens) &&
                    !(role.Kind == speaking && speaks))
                    target.Roles.Add(new(role.Chip, role.Name, role.Kind == HostRoles.Singing
                            ? "Ready. Martlet makes its songs here when you ask it to sing (Companion > Singing)."
                            : $"Ready. Assign {role.Job} to use it.",
                        DeviceComponent.Standby(role.Kind)));
            }
            var users = inputs.HostUsers?.GetValueOrDefault(paired.HostId);
            if (local)
            {
                var others = users?.Where(u => !u.ThisPc).Select(u => u.Text).ToArray();
                var detail = $"Paired as {paired.HostId}" + (others is null ? "."
                    : others.Length == 0 ? ". No other computer uses it yet." : ". Used by " + string.Join("; ", others) + ".");
                // A host PC already lists its host service: one row says what it is and who uses it.
                var listed = thisPc.Roles.FindIndex(r => r.Component == DeviceComponent.HostService);
                // This PC reads its own host service from Docker; a companion PC that also hosts shows when it can't work.
                if (inputs.OwnHostTrouble is { } trouble)
                {
                    detail = $"Not working: {trouble}. " + detail;
                    thisPc.Worsen(NodeHealth.Attention, "Host service not working");
                }
                else if (check?.Reachable == false)
                {
                    detail = "Not answering. " + detail;
                    thisPc.Worsen(NodeHealth.Attention, "Host service not answering");
                }
                var row = new HostedRole("Host", "Martlet host service",
                    listed >= 0 && !machine.DockerRunning ? $"{thisPc.Roles[listed].Detail}. {detail}" : detail, DeviceComponent.HostService);
                if (listed >= 0) thisPc.Roles[listed] = row;
                else thisPc.Roles.Add(row);
            }
            else
            {
                if (target.Roles.Count == before)
                    target.Roles.Add(new("Host", "Martlet host", check?.Text ?? "Paired. Check connection to see what it can do.",
                        DeviceComponent.Host));
                if (users is { Count: > 0 })
                    target.Roles.Add(new("Users", "Computers using it", string.Join("; ", users.Select(u => u.Text)) + ".", DeviceComponent.Users));
                if (target.Facts.All(f => f.Label != "Address"))
                {
                    target.Facts.Insert(0, new("Address", paired.Pairing.Origin));
                }
                target.Facts.Add(new("Connection", paired.Reach));
                AddHardware(target, inputs, paired.HostId);
                if (check is null) target.Worsen(NodeHealth.Unknown, "Paired, not checked yet");
                else if (check.Reachable == false)
                    target.Worsen(NodeHealth.Attention, inputs.HostAway?.TryGetValue(paired.HostId, out var away) == true &&
                        NodePresenceNotices.MapText(away) is { } silent ? silent : "Not reachable");
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
                : reported == app ? $"{reported}, up to date" : $"{reported}, newer than this PC ({app}). Update this PC to {reported}.",
                "SelectedDeviceRelease"));
            var updateShown = outdated && !local && target.Worsen(NodeHealth.Attention, "Update available");
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
            {
                var updateHost = new NodeCommand(NodeAction.UpdateHost, local ? "Update this PC's host service" : outdated ? $"Update to Martlet {app}" : "Update host",
                    outdated && check?.Reachable == true, id, hostService);
                target.Commands.Add(updateHost);
                // Clicking "Update available" runs the same update.
                if (updateShown) target.HealthCommand = updateHost;
            }
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
            if (companion && inputs.DeepThinkingHosts?.Contains(id) != true && check?.Offers?.ContainsKey(HostRoles.DeepThinking) == true)
                target.Commands.Add(new(NodeAction.Companion, inputs.ThinkingPoolLeft?.Contains(id) == true ? "Add it to the Thinking pool" : "Show the Thinking pool",
                    Argument: nameof(CompanionTab.DeepThinking), Component: Ready(HostRoles.DeepThinking)));
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
                // A role it runs: change its settings (model, GPU or CPU, graphics card...) the way it was added.
                if (offered)
                    target.Commands.Add(new(NodeAction.ChangeRole, $"Change {role.Name} settings",
                        Argument: id + "/" + role.Kind, Component: RoleComponent(role.Kind)));
                if (check is null || offered)
                    target.Commands.Add(new(NodeAction.RemoveRole, local ? $"Remove {role.Name} here" : $"Remove {role.Name}",
                        Argument: id + "/" + role.Kind, Component: offered ? RoleComponent(role.Kind) : null));
            }
            if (managed) target.Commands.Add(new(NodeAction.HostStatus, "Show its status", Argument: id, Component: hostService));
            // How it is reached from outside home (martlet-host exposure): this PC's host service, a host Martlet manages over SSH,
            // or one whose own Martlet runs it (the host.exposure command between computers).
            if (local || paired.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative || paired.Method == HostSetupMethod.Agent && managed)
                target.Commands.Add(new(NodeAction.OutsideAccess, local ? "Outside access for this PC's host service" : "Outside access", Argument: id,
                    Component: hostService));
            if (inputs.HostOutside?.GetValueOrDefault(id) is { } outside) target.Facts.Add(new("Outside home", outside, "SelectedDeviceOutside"));
            var hardware = inputs.HostHardware?.FirstOrDefault(h => h.HostId == id);
            // A voice engine on a Windows computer whose graphics card also does other work falls behind when the card fills.
            if (check?.Offers?.Keys.FirstOrDefault(HostRoles.Speaks) is { } voiceKind &&
                SharedGpu.Warning(local ? "This PC" : id, SharedGpu.OnWindows(local, hardware), SpeechEngines.ForRoleKind(voiceKind)?.Name,
                    SharedGpu.Neighbours(check.Offers.Keys, local && MainWindow.IsLocalOllama(
                        inputs.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm)))) is { } sharedGpu)
                target.SharedGpu = sharedGpu;
            // Home Assistant runs on the host's own network (no gateway route); the host reports it in its machine report.
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

        // Your other Martlet computers, so every one of them shows the same computers: one device per computer. One that runs a
        // host service is that host's device too (the host it names, or the one Martlet names after the PC: DIVA runs diva-host).
        foreach (var computer in inputs.Computers ?? [])
        {
            var friend = computer.Standing == ComputerStanding.Friend;
            var hostId = computer.HostId ?? HostSetupCommands.SuggestedHostId(computer.Name);
            // A friend's computer is never one of your hosts, whatever it is called.
            var hosting = friend ? null : nodes.GetValueOrDefault("host:" + hostId);
            var node = hosting ?? Node("pc:" + computer.DeviceId, NodeKind.Computer, computer.Name, computer.DeviceId, ThisPcGlyph);
            var where = computer.Activity is { } activity ? " " + activity : "";
            var standing = computer.Standing switch
            {
                ComputerStanding.Asking => $"Asks to join your Martlet network through {computer.Through} (check number {computer.CheckNumber}). " +
                    "Allow it under Your Martlet network below.",
                ComputerStanding.Outside => "Uses your hosts but isn't in your Martlet network." + where,
                ComputerStanding.Friend => $"A friend's computer: it signed in to {computer.Through ?? "your host"} as a friend and uses only its " +
                    "engines (thinking, listening, speaking, lip-sync, reading). It never joins your Martlet network; your own work comes " +
                    "first. Stop sharing under Friends below." + where,
                _ => "In your Martlet network." + where
            };
            // What it is, as it said itself; a computer on an older Martlet hasn't said, so it shows as the Martlet app. A host PC
            // whose host service isn't set up yet does no work for the others until someone at it sets one up.
            var (chip, name, does) = computer.Role switch
            {
                DeviceRole.Host when computer.HostId is null && hosting is null => ("Host PC", "Martlet host PC", NoHostServiceYet + " "),
                DeviceRole.Host => ("Host PC", "Martlet host PC",
                    $"Runs Martlet's host service ({computer.HostId ?? hostId}) for your companion PCs and uses no jobs itself. "),
                DeviceRole.Companion => ("Companion", "Martlet companion", "Conversations, its microphone and speakers" +
                    (hosting is not null ? $"; it runs a host service too ({hostId}). " : ". ")),
                _ => ("Martlet", "Martlet app", hosting is not null ? $"Martlet on this computer runs its host service ({hostId}). " : "")
            };
            node.Roles.Insert(0, new(chip, name, $"{computer.DeviceId}. {does}{standing}{Asked(computer)}", DeviceComponent.Member));
            if (RoleCommand(computer) is { } switching) node.Commands.Add(switching);
            // The jobs every companion PC does itself (a Windows voice, Parakeet) show on each companion PC; a friend's has its own.
            if (computer.Standing is not (ComputerStanding.Asking or ComputerStanding.Friend) &&
                (computer.Role == DeviceRole.Companion || computer.Role is null && hosting is null))
                node.Roles.AddRange(onEachPc);
            if (hosting is not null)
            {
                hosting.Title = computer.Name;
                hosting.Subtitle = $"{computer.DeviceId} \u00b7 {hostId}";
                hosting.Facts.Insert(0, new("Computer", $"{computer.Name} ({computer.DeviceId})"));
                if (computer.Standing == ComputerStanding.Asking) hosting.Worsen(NodeHealth.Attention, "Asks to join");
                continue;
            }
            node.Facts.Add(new("Device", computer.DeviceId));
            node.Facts.Add(new("Martlet network", computer.Standing switch
            {
                ComputerStanding.Member => "Member",
                ComputerStanding.Asking => $"Asks to join (check number {computer.CheckNumber})",
                ComputerStanding.Friend => "Never: a friend's computer",
                _ => "Not a member"
            }));
            if (computer.Standing == ComputerStanding.Asking) node.Worsen(NodeHealth.Attention, "Asks to join");
            else if (computer.Standing == ComputerStanding.Outside) node.Worsen(NodeHealth.Unknown, "Not in your network");
            else if (computer.Standing == ComputerStanding.Friend) node.HealthText = computer.Active ? "A friend's, active now" : "A friend's";
            else if (computer.Active) node.HealthText = "Active now";
            else node.Worsen(NodeHealth.Unknown, "Not active now");
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
                    // Lip-sync by this PC's own host service is already on this PC: the way back is without the host service.
                    thisPc.Commands.Add(new(NodeAction.LipSyncThisPc, inputs.Avatar!.RemoteHost!.HostId == ownHostId
                        ? "Do lip-sync without the host service" : "Take lip-sync back to this PC"));
                    // A host a friend shares isn't one of your hosts (drawn above): it shows with the lip-sync it does for this PC.
                    if (inputs.SharedHosts?.FirstOrDefault(h => h.HostId == inputs.Avatar.RemoteHost.HostId) is { } friendsLipSync)
                    {
                        var face = Node("host:" + friendsLipSync.HostId, NodeKind.Host, friendsLipSync.HostId, friendsLipSync.Address, ComputerGlyph);
                        face.Roles.Add(new("Lip-sync", "Lip-sync", "Handles lip-sync for this PC.", DeviceComponent.LipSync));
                    }
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
        thisPc.Facts.Add(new("Processor type", machine.ProcessorType.Describe()));
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

        // Computers Martlet changes right now (by host ID, device ID or "this-pc") show Configuring with their step.
        foreach (var (id, step) in inputs.Configuring ?? new Dictionary<string, string>())
            (id == "this-pc" || id == ownHostId ? thisPc : nodes.GetValueOrDefault("host:" + id) ?? nodes.GetValueOrDefault("pc:" + id))?.Configure(step);

        // A host a friend shares with this PC shows where one of this PC's jobs uses it, marked as theirs.
        foreach (var friendsHost in inputs.SharedHosts ?? [])
            if (nodes.GetValueOrDefault("host:" + friendsHost.HostId) is { } draft && draft.Kind == NodeKind.Host)
            {
                draft.Subtitle = "Shared by a friend";
                draft.Facts.Add(new("Shared with this PC", "By a friend: its engines only, for this PC only"));
                draft.Notes.Add("A friend shares this host with this PC. It never joins your Martlet network, your other computers don't " +
                    "use it, and its owner's own work comes first. Devices › Hosts shared with this PC lists it.");
            }

        // Its details are just the ways to add one, each a whole clickable card (nothing there only looks like a button).
        var add = new Draft("add", NodeKind.Add, "Add a computer", "Use another computer", AddGlyph) { Health = NodeHealth.Unknown, HealthText = "" };
        add.Commands.Add(new(NodeAction.AddComputer, "Add a computer", true,
            Detail: "Another computer can help with thinking, speech or lip-sync. Pair it once with a one-use code on your private network."));
        add.Commands.Add(new(NodeAction.PrepareComputer, "Prepare a Linux computer",
            Detail: "Get a Linux PC ready for host jobs over SSH first. Martlet checks it, then changes only what you select."));
        if (inputs.Role != DeviceRole.Host)
            add.Commands.Add(new(NodeAction.HostThisPc, "Run host services on this PC",
                Detail: "No other computer? Let this PC do thinking, speech or lip-sync itself."));
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
