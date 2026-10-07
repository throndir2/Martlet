using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>One thing the live conversation runs on (docs/CONVERSATION.md, Live floor): its <paramref name="Job"/> (thinking, voice
/// or listening), the computer (<paramref name="Machine"/>: <see cref="LiveResources.ThisPc"/>, a computer on the home network
/// such as "lan:192.168.1.20", or null for a cloud provider, which shares no hardware with anything of yours) and the graphics
/// cards that serve it when known (<paramref name="Gpus"/>; empty: the whole computer).</summary>
public sealed record LiveResource(string Job, string? Machine, IReadOnlyList<string> Gpus)
{
    /// <summary>The paired host whose gateway serves it, for a hold on its graphics cards (<see cref="Martlet.Core.Cluster.ILiveGpuHold"/>).</summary>
    public string? HostId { get; init; }
    /// <summary>That host's route for it.</summary>
    public string? RouteId { get; init; }

    public override string ToString() => $"{nameof(LiveResource)} {Job} on {Machine ?? "a cloud provider"}";
}

/// <summary>What the live conversation runs on now: its Thinking route, its voice and its listening, each on a computer and
/// graphics cards (<see cref="LiveResource"/>). A background place shares them (<see cref="Shares"/>) when it is the
/// conversation's own Thinking route, or runs on the same computer and the same graphics card (when either side doesn't know
/// its graphics cards, the same computer is enough).</summary>
public sealed record LiveResources(IReadOnlyList<LiveResource> Items)
{
    /// <summary>The computer Martlet runs on.</summary>
    public const string ThisPc = "this-pc";
    /// <summary>The place ID of the conversation's own Thinking route when it takes pool work (the pool is empty and Use the
    /// conversation model when the pool is empty is on): it always shares the conversation's hardware.</summary>
    public const string ConversationModel = "thinking";

    public static LiveResources None { get; } = new([]);

    /// <summary>Whether <paramref name="place"/> shares hardware with the live conversation.</summary>
    public bool Shares(BackgroundPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (place.Id == ConversationModel) return true;
        if (place.Machine is not { } machine) return false;
        return Items.Any(item => item.Machine == machine &&
            (item.Gpus.Count == 0 || place.Gpus.Count == 0 || item.Gpus.Any(gpu => place.Gpus.Contains(gpu, StringComparer.Ordinal))));
    }

    /// <summary>The paired hosts that serve a live route, each with those routes, for holds on their graphics cards.</summary>
    public IReadOnlyList<(string HostId, IReadOnlyList<string> Routes)> Hosts =>
        [.. Items.Where(item => item.HostId is not null && item.RouteId is not null).GroupBy(item => item.HostId!, StringComparer.Ordinal)
            .Select(group => (group.Key, (IReadOnlyList<string>)[.. group.Select(item => item.RouteId!).Distinct(StringComparer.Ordinal)]))];

    /// <summary>The live resources of <paramref name="routes"/> (Thinking, voice and listening, the ones turned on).
    /// <paramref name="gpus"/> gives the graphics cards a paired host said serve one of its routes (host ID, route ID), when known.</summary>
    public static LiveResources For(IReadOnlyList<SetupRoute> routes, Func<string, string, IReadOnlyList<string>>? gpus = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        List<LiveResource> items = [];
        foreach (var route in routes.Where(r => r.Enabled != false && r.Role is SetupRole.Llm or SetupRole.Tts or SetupRole.Stt))
        {
            var job = route.Role switch { SetupRole.Llm => "thinking", SetupRole.Tts => "voice", _ => "listening" };
            if (SelfHostSetup.IsGateway(route.RouteType) && route.Gateway is { } gateway)
            {
                var routeId = route.GatewaySnapshot?.RouteId ?? SelfHostSetup.Gateway(route.RouteType!.Value).RouteId;
                items.Add(new(job, MachineOf(gateway.Origin) ?? "host:" + gateway.HostId, gpus?.Invoke(gateway.HostId, routeId) ?? [])
                {
                    HostId = gateway.HostId, RouteId = routeId
                });
            }
            else if (route.RouteType is SetupRouteType.LocalWhisper or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts or
                SetupRouteType.LocalParakeet)
                items.Add(new(job, ThisPc, []));
            else if (route.RouteType == SetupRouteType.ChatCompletions)
                items.Add(new(job, MachineOf(route.Origin), []));
            else
                items.Add(new(job, null, []));
        }
        return new(items);
    }

    /// <summary>The computer a Thinking pool member runs on: its paired computer, the endpoint's computer on the home network or
    /// this PC, or null for a cloud provider and for the conversation model (whose place always shares).</summary>
    public static string? MachineOf(DeepThinkingSettings member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Place switch
        {
            DeepThinkingPlace.Host => MachineOf(member.HostOrigin) ?? "host:" + member.HostId,
            DeepThinkingPlace.Endpoint => MachineOf(member.Origin),
            _ => null
        };
    }

    /// <summary>The computer at <paramref name="origin"/>: <see cref="ThisPc"/> for a loopback address or one of this PC's own,
    /// "lan:" and the address or name for one on the home network, null for anything else (a cloud provider).</summary>
    public static string? MachineOf(string? origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return null;
        if (uri.IsLoopback) return ThisPc;
        var host = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var address) && Own.Value.Contains(address)) return ThisPc;
        return ContextBudget.IsInNetwork(SetupRouteType.ChatCompletions, origin) ? "lan:" + host : null;
    }

    // This PC's own addresses, read once: a route to one of them runs on this PC.
    private static readonly Lazy<HashSet<IPAddress>> Own = new(() =>
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))];
        }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException) { return []; }
    });

    public override string ToString() => $"{nameof(LiveResources)} ({Items.Count})";
}

/// <summary>What the live floor did to background work, by job kind name (think-longer, digest...): jobs it held back (they wait
/// for the conversation) and jobs it stopped (they go on later or were dropped).</summary>
public sealed record LiveFloorCounts(IReadOnlyDictionary<string, int> Held, IReadOnlyDictionary<string, int> Stopped)
{
    public static LiveFloorCounts Empty { get; } = new(new Dictionary<string, int>(), new Dictionary<string, int>());

    public int HeldTotal => Held.Values.Sum();
    public int StoppedTotal => Stopped.Values.Sum();

    /// <summary>The reply latency line's part, such as "held 2 pool jobs, stopped 1 (think longer)"; null when it did nothing.</summary>
    public string? Describe()
    {
        if (HeldTotal == 0 && StoppedTotal == 0) return null;
        List<string> parts = [];
        if (HeldTotal > 0) parts.Add($"held {HeldTotal} pool job{(HeldTotal == 1 ? "" : "s")}");
        if (StoppedTotal > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"stopped {StoppedTotal} ") + "(" +
                string.Join(", ", Stopped.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key.Replace('-', ' ') + (p.Value > 1 ? $" x{p.Value}" : ""))) + ")");
        return string.Join(", ", parts);
    }
}

/// <summary>The live floor's rules for the Thinking pool and background work (docs/CONVERSATION.md, Live floor), on the places
/// that share the live conversation's hardware (<see cref="LiveResources.Shares"/>); places that share nothing never change.
/// Listening: no new work starts there (nothing is stopped). Live: the barge-in and end-of-turn judges still start (on a place
/// that shares nothing first); summaries (digest), remembering and naming, thinking longer and research don't start, and when
/// they run there they stop (a summary is dropped, remembering and naming wait in line again, a think or research goes on later
/// from what it wrote); finding touch zones doesn't start but isn't stopped. On every place, places that share go last while
/// the conversation isn't idle. <see cref="Attach"/> makes a broker follow the floor. Thread-safe.</summary>
public sealed class LiveFloorRules : IPlaceRules, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> heldPeriod = new(StringComparer.Ordinal), stoppedPeriod = new(StringComparer.Ordinal),
        heldTotal = new(StringComparer.Ordinal), stoppedTotal = new(StringComparer.Ordinal);
    private readonly HashSet<string> heldOnce = new(StringComparer.Ordinal);
    private LiveResources resources;
    private BackgroundPlaces? places;

    public LiveFloorRules(LiveFloor floor, LiveResources? resources = null)
    {
        Floor = floor ?? throw new ArgumentNullException(nameof(floor));
        this.resources = resources ?? LiveResources.None;
        floor.Changed += Follow;
    }

    public LiveFloor Floor { get; }

    /// <summary>What the conversation runs on now; setting it (a settings change) applies the rules again at once.</summary>
    public LiveResources Resources
    {
        get => Volatile.Read(ref resources);
        set
        {
            Volatile.Write(ref resources, value ?? LiveResources.None);
            Volatile.Read(ref places)?.Reconsider();
        }
    }

    /// <summary>Raised (off the broker's lock) for each piece of work the rules stopped.</summary>
    public event Action<BackgroundPlaceLease>? StoppedWork;

    /// <summary>Makes <paramref name="broker"/> follow the floor: these become its rules, applied again on every change of the
    /// floor and of <see cref="Resources"/>.</summary>
    public LiveFloorRules Attach(BackgroundPlaces broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        Volatile.Write(ref places, broker);
        broker.Rules = this;
        return this;
    }

    /// <summary>The kinds stopped while the floor is Live on a place that shares: summaries, remembering and naming, thinking
    /// longer and research.</summary>
    public static bool Stops(ThinkingJobKind kind) => kind is ThinkingJobKind.Digest or ThinkingJobKind.Memory or ThinkingJobKind.Naming or
        ThinkingJobKind.ThinkLonger or ThinkingJobKind.Research;

    /// <summary>The kinds that serve the live turn itself and always start (the judges).</summary>
    public static bool ServesTheTurn(ThinkingJobKind kind) => kind is ThinkingJobKind.BargeInJudge or ThinkingJobKind.EndOfTurnJudge;

    public bool Shares(BackgroundPlace place) => Resources.Shares(place);

    public bool MayStart(BackgroundPlace place, ThinkingJobKind? kind) =>
        kind is not { } pooled || Floor.Level == LiveFloorLevel.Idle || ServesTheTurn(pooled) || !Shares(place);

    public bool MustStop(BackgroundPlace place, ThinkingJobKind? kind) =>
        kind is { } pooled && Stops(pooled) && Floor.Level == LiveFloorLevel.Live && Shares(place);

    public bool Avoid(BackgroundPlace place) => Floor.Level != LiveFloorLevel.Idle && Shares(place);

    public void Held(string holder, ThinkingJobKind kind)
    {
        var name = ThinkingJobKinds.Name(kind);
        lock (gate)
        {
            if (!heldOnce.Add(holder + "|" + name)) return;
            heldPeriod[name] = heldPeriod.GetValueOrDefault(name) + 1;
            heldTotal[name] = heldTotal.GetValueOrDefault(name) + 1;
        }
    }

    public void Stopped(BackgroundPlaceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var name = lease.Kind is { } kind ? ThinkingJobKinds.Name(kind) : "background";
        lock (gate)
        {
            stoppedPeriod[name] = stoppedPeriod.GetValueOrDefault(name) + 1;
            stoppedTotal[name] = stoppedTotal.GetValueOrDefault(name) + 1;
        }
        try { StoppedWork?.Invoke(lease); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    /// <summary>What the rules did since the floor last left Idle (the turn now, or the last one).</summary>
    public LiveFloorCounts Period { get { lock (gate) return new(new Dictionary<string, int>(heldPeriod), new Dictionary<string, int>(stoppedPeriod)); } }

    /// <summary>What the rules did since they were made.</summary>
    public LiveFloorCounts Total { get { lock (gate) return new(new Dictionary<string, int>(heldTotal), new Dictionary<string, int>(stoppedTotal)); } }

    // A new turn begins when the floor leaves Idle; then the broker applies the rules for the new level.
    private void Follow(LiveFloorChange change)
    {
        if (change.From == LiveFloorLevel.Idle)
            lock (gate)
            {
                heldPeriod.Clear();
                stoppedPeriod.Clear();
                heldOnce.Clear();
            }
        Volatile.Read(ref places)?.Reconsider();
    }

    public void Dispose() => Floor.Changed -= Follow;

    public override string ToString() => $"{nameof(LiveFloorRules)} ({Floor.Level})";
}
