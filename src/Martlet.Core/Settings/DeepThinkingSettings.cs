using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Where Deep thinking runs: with the Thinking model, on an OpenAI-compatible endpoint (a cloud provider, another
/// server, or Ollama on this PC with its own model) or on a paired Martlet host's Ollama.</summary>
public enum DeepThinkingPlace { SameAsThinking, Endpoint, Host }

/// <summary>Companion › Deep thinking › Where it thinks, on this PC (deep-thinking.json in the data folder; never part of the
/// settings your computers share, because which machine is free to think depends on the computer you talk to). Thinking
/// answers you; Deep thinking works out what Martlet hands it in the background (think_longer), always in parallel with the
/// conversation, so it needs a model of its own (<see cref="DeepThinkingPlan"/>): another computer, a cloud provider, a second
/// model on this PC, or Thinking's own model when its provider answers several requests at once. An endpoint's own key is in
/// Windows Credential Manager (<see cref="CredentialId"/>, a reference); a paired computer is reached with this PC's pairing,
/// whose secret stays where pairing saved it.</summary>
public sealed record DeepThinkingSettings
{
    public const string FileName = "deep-thinking.json";
    private const int MaxFileBytes = 64 * 1024;

    [JsonConverter(typeof(JsonStringEnumConverter<DeepThinkingPlace>))]
    public DeepThinkingPlace Place { get; init; }
    /// <summary>The endpoint's API base URL (Place Endpoint).</summary>
    public string? Origin { get; init; }
    /// <summary>The model: the endpoint's model ID, or the one the paired computer's Ollama route serves.</summary>
    public string? ModelId { get; init; }
    /// <summary>The endpoint's own key in Windows Credential Manager; null uses Thinking's key for the same base URL, or none.</summary>
    public Guid? CredentialId { get; init; }
    /// <summary>The paired computer (Place Host): its ID, pinned gateway origin and key, and this PC's pairing with it.</summary>
    public string? HostId { get; init; }
    public string? HostOrigin { get; init; }
    public string? HostSpkiFingerprint { get; init; }
    public string? HostDeviceId { get; init; }
    public Guid? HostCredentialId { get; init; }
    /// <summary>Which of the paired computer's routes thinks: its Deep thinking role's own Ollama
    /// (<see cref="SelfHostSetup.DeepThinkingRouteId"/>), or its Ollama role's (null, as saved before that role existed).</summary>
    public string? HostRouteId { get; init; }
    public DateTimeOffset? ChosenAt { get; init; }

    /// <summary>How many thinks this place runs at once, when its computer says (a Deep thinking role with several models or
    /// parallel slots on its graphics card); null: <see cref="ThinksAtOnce"/>'s default.</summary>
    public int? Slots { get; init; }

    /// <summary>How many thinks run here at once: <see cref="Slots"/>, else one on a computer of yours (its graphics card) and
    /// <see cref="CloudThinksAtOnce"/> on a cloud provider or another server (Same as Thinking is offered only for one that answers
    /// several requests at once).</summary>
    [JsonIgnore]
    public int ThinksAtOnce => Slots ?? (Place == DeepThinkingPlace.Host || OnThisPc ? 1 : CloudThinksAtOnce);

    public const int CloudThinksAtOnce = 4;

    /// <summary>The most places Deep thinking thinks on at once: this one and up to seven more.</summary>
    public const int MaxPlaces = 8;

    /// <summary>The other places it thinks on at the same time (Companion › Deep thinking › Think here too): paired computers
    /// (or endpoints), each a place of its own like this one, so several thinks run at once, one on each. Null (as saved before
    /// it existed) or empty: it thinks in this one place.</summary>
    public IReadOnlyList<DeepThinkingSettings>? Pool { get; init; }

    /// <summary>This place alone, without <see cref="Pool"/>.</summary>
    [JsonIgnore] public DeepThinkingSettings Single => Pool is null ? this : this with { Pool = null };

    /// <summary>Every place it thinks on, this one first.</summary>
    [JsonIgnore] public IReadOnlyList<DeepThinkingSettings> Places => [Single, .. Pool ?? []];

    /// <summary>A place's key: one per computer (<c>host:diva</c>), endpoint and model, or <c>thinking</c>.</summary>
    [JsonIgnore]
    public string Key => Place switch
    {
        DeepThinkingPlace.Host => "host:" + HostId,
        DeepThinkingPlace.Endpoint => $"endpoint:{Origin}|{ModelId}",
        _ => "thinking"
    };

    /// <summary>This place first, then <paramref name="pool"/>'s other places (none twice, at most <see cref="MaxPlaces"/>).</summary>
    public DeepThinkingSettings WithPool(IEnumerable<DeepThinkingSettings> pool)
    {
        var self = Single;
        var others = pool.Select(p => p.Single).Where(p => p.Place != DeepThinkingPlace.SameAsThinking && p.Key != self.Key)
            .DistinctBy(p => p.Key).Take(MaxPlaces - 1).ToArray();
        return self with { Pool = others.Length == 0 ? null : others };
    }

    /// <summary>The computer a place thinks on, by name only: the paired computer ("diva"), "this PC", the provider's host
    /// ("openrouter.ai"), or for Same as Thinking where <paramref name="thinking"/> runs.</summary>
    public string Computer(SetupRoute? thinking)
    {
        static string? Named(string? origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            ? uri.IsLoopback ? "this PC" : uri.IdnHost : null;
        return Place switch
        {
            DeepThinkingPlace.Host => HostId!,
            DeepThinkingPlace.Endpoint => Named(Origin) ?? "its endpoint",
            _ when thinking?.Gateway is { } gateway => Uri.TryCreate(gateway.Origin, UriKind.Absolute, out var g) && g.IsLoopback ? "this PC" : gateway.HostId,
            _ when thinking?.RouteType == SetupRouteType.ChatCompletions => Named(thinking.Origin) ?? "the Thinking model",
            _ when thinking is not null => "OpenAI",
            _ => "the Thinking model"
        };
    }

    /// <summary>Every place, in words: "diva's Deep thinking (qwen3-8b) and ripley's Deep thinking (gemma4:27b)".</summary>
    public string DescribeAll()
    {
        var all = Places.Select(p => p.Describe()).ToArray();
        return all.Length == 1 ? all[0] : string.Join(", ", all[..^1]) + " and " + all[^1];
    }

    [JsonIgnore] public bool Separate => Place != DeepThinkingPlace.SameAsThinking;

    /// <summary>Whether it thinks with a paired computer's Deep thinking role, a model of its own beside that computer's Thinking.</summary>
    [JsonIgnore] public bool OnHostRole => Place == DeepThinkingPlace.Host && HostRouteId == SelfHostSetup.DeepThinkingRouteId;

    /// <summary>The paired computer's route a think goes to (Place Host).</summary>
    [JsonIgnore] public string HostRoute => HostRouteId ?? SelfHostSetup.OllamaRouteId;

    /// <summary>Whether the endpoint runs on this PC (Ollama, LM Studio or another server on loopback).</summary>
    [JsonIgnore]
    public bool OnThisPc => Place == DeepThinkingPlace.Endpoint && Uri.TryCreate(Origin, UriKind.Absolute, out var origin) && origin.IsLoopback;

    /// <summary>Where it thinks, in words: "the Thinking model", "Ollama on this PC (gemma4:12b)", "diva (gemma4:27b)",
    /// "diva's Deep thinking (qwen3-8b)", "openrouter.ai (x-ai/grok-4.3)".</summary>
    public string Describe() => Place switch
    {
        DeepThinkingPlace.Host when OnHostRole => $"{HostId}'s Thinking pool ({ModelId})",
        DeepThinkingPlace.Host => $"{HostId} ({ModelId})",
        DeepThinkingPlace.Endpoint when string.Equals(Origin, GenerationSupport.LocalOllamaChatBaseUrl, StringComparison.Ordinal) =>
            $"Ollama on this PC ({ModelId})",
        DeepThinkingPlace.Endpoint => $"{(Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ? uri.IdnHost : Origin)} ({ModelId})",
        _ => "the Thinking model"
    };

    /// <summary>Throws when the saved destination isn't usable as it is.</summary>
    public void Validate()
    {
        ContractRules.Defined(Place);
        ContractRules.Require(Slots is null or >= 1 and <= MaxPlaces, $"A place runs 1-{MaxPlaces} thinks at once.");
        switch (Place)
        {
            case DeepThinkingPlace.Endpoint:
                _ = ChatCompletionsSetup.BaseUri(Origin ?? "");
                ChatCompletionsSetup.ModelId(ModelId ?? "");
                ContractRules.Require(CredentialId != Guid.Empty && HostId is null && HostRouteId is null, "An endpoint's key reference is invalid.");
                break;
            case DeepThinkingPlace.Host:
                ContractRules.Require(HostId is { Length: > 0 and <= 128 } && !HostId.Any(char.IsControl) &&
                    Uri.TryCreate(HostOrigin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps &&
                    HostSpkiFingerprint is { Length: > 0 and <= 200 } && HostDeviceId is { Length: > 0 and <= 128 } &&
                    HostCredentialId is { } pairing && pairing != Guid.Empty && CredentialId is null && Origin is null &&
                    HostRouteId is null or SelfHostSetup.OllamaRouteId or SelfHostSetup.DeepThinkingRouteId,
                    "The paired computer in the Thinking pool is incomplete. Add it again.");
                ChatCompletionsSetup.ModelId(ModelId ?? "");
                break;
            default:
                ContractRules.Require(Origin is null && ModelId is null && CredentialId is null && HostId is null && HostRouteId is null,
                    "Same as Thinking keeps no destination of its own.");
                break;
        }
        if (Pool is null) return;
        ContractRules.Require(Pool.Count < MaxPlaces && Pool.All(p => p is { Pool: null, Place: DeepThinkingPlace.Host or DeepThinkingPlace.Endpoint }) &&
            Pool.Select(p => p.Key).Append(Key).Distinct(StringComparer.Ordinal).Count() == Pool.Count + 1,
            $"The Thinking pool has at most {MaxPlaces} different members, each a paired computer or an endpoint.");
        foreach (var place in Pool) place.Validate();
    }

    public static DeepThinkingSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved destination and the file's state: none, loaded or unreadable (which reads as Same as Thinking).</summary>
    public static (DeepThinkingSettings Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > MaxFileBytes) return (new(), "unreadable");
            var loaded = JsonSerializer.Deserialize<DeepThinkingSettings>(File.ReadAllText(path));
            if (loaded is null) return (new(), "unreadable");
            loaded.Validate();
            return (loaded, "loaded");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            ContractException or ArgumentException)
        {
            return (new(), "unreadable");
        }
    }

    /// <summary>Saves deep-thinking.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null) return false;
        Validate();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"deep-thinking.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The endpoint's own key scope, like a Chat Completions Thinking key bound to its exact base URL.</summary>
    public CredentialBinding Binding(Guid profileId, Guid credentialId) =>
        new(profileId, credentialId, SetupRole.Llm, SetupRouteType.ChatCompletions, ChatCompletionsSetup.Alias, Origin!);

    /// <summary>Whether it borrows the Thinking route's key: no key of its own, and the same base URL as a Chat Completions
    /// Thinking route that has one.</summary>
    public bool UsesThinkingKey(SetupRoute? thinking) =>
        Place == DeepThinkingPlace.Endpoint && CredentialId is null &&
        thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == Origin;

    /// <summary>Whether it is exactly the Thinking route already (same endpoint and model).</summary>
    public bool SameAs(SetupRoute? thinking) =>
        Place == DeepThinkingPlace.SameAsThinking ||
        Place == DeepThinkingPlace.Endpoint && thinking?.RouteType == SetupRouteType.ChatCompletions && thinking.Origin == Origin &&
        thinking.ModelId == ModelId;
}

/// <summary>Whether Deep thinking can work where it is set to think, and why. Deep thinking is parallel thinking: a think always
/// runs alongside the conversation, never in turns with it, so it needs a model of its own. The Thinking model itself can't
/// think something over while it answers you when it runs on this PC or a paired computer (such a server answers one request
/// at a time and keeps one conversation in its prompt cache), so there it isn't <see cref="Available"/> and think_longer isn't
/// offered. A second model in the same Ollama on this PC runs in a process of its own and answers at the same time, but only
/// while both fit on the graphics card (Ollama unloads one or makes a request wait otherwise): <see cref="ChecksFit"/> says
/// Martlet checks that before each think.</summary>
public sealed record DeepThinkingPlan(bool Available, string Why, bool ChecksFit = false, int Rank = 0)
{
    /// <summary>Whether the place would run but its computer doesn't answer now (<see cref="DeepThinkingPool.For"/>'s offline
    /// hosts): its slots come back when it answers again.</summary>
    public bool Offline { get; init; }

    /// <summary>The plan of a place on <paramref name="host"/>, which would run but doesn't answer now.</summary>
    public static DeepThinkingPlan Away(string host) =>
        new(false, $"{host} is offline; its slots come back when it answers again.") { Offline = true };

    public static DeepThinkingPlan For(DeepThinkingSettings deep, IReadOnlyList<SetupRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(deep);
        ArgumentNullException.ThrowIfNull(routes);
        var thinking = routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        if (deep.SameAs(thinking))
            return thinking is null ? new(false, "Set up Thinking first.")
                : IsThisPc(thinking)
                ? new(false, $"Thinking's model ({thinking.ModelId}) runs on this PC and can't think something over while it answers you. " +
                    "Add a member to the Thinking pool: another model in Ollama on this PC, another of your computers or a cloud provider.")
                : thinking.RouteType == SetupRouteType.GatewayOllama
                    ? new(false, $"Thinking's model runs on {thinking.Gateway?.HostId ?? "a paired computer"} and can't think something over " +
                        "while it answers you. Add the Thinking pool role there, or add another member to the Thinking pool.")
                    : new(true, "Thinking's provider answers several requests at once, so a think runs alongside the conversation.", Rank: 2);
        if (deep.OnThisPc)
        {
            if (thinking is not null && IsThisPc(thinking) && SameServer(deep, thinking))
                return new(true, $"It runs as a second model beside Thinking's {thinking.ModelId} on this PC, so a think runs alongside the " +
                    "conversation. Before each think Martlet checks both fit on the graphics card together, and replies may start a " +
                    "little later while it thinks.", ContextBudget.IsLocalOllama(SetupRouteType.ChatCompletions, deep.Origin) &&
                    ContextBudget.IsLocalOllama(thinking.RouteType, thinking.Origin), Rank: 3);
            var shared = routes.Where(r => r.Enabled != false && IsThisPc(r) && r.Role is SetupRole.Llm or SetupRole.Tts).ToArray();
            return shared.Length > 0
                ? new(true, $"It runs on this PC alongside the conversation and shares the graphics card with {Jobs(shared)}, so replies " +
                    "may start a little later while it thinks.", Rank: shared.Any(r => r.Role == SetupRole.Llm) ? 2 : 1)
                : new(true, "It runs on this PC while the conversation's models run elsewhere, so a think runs alongside the conversation.", Rank: 1);
        }
        if (deep.Place == DeepThinkingPlace.Host)
        {
            var shared = routes.Where(r => r.Enabled != false && SelfHostSetup.IsGateway(r.RouteType) && r.Gateway?.HostId == deep.HostId).ToArray();
            // Its Deep thinking role is an Ollama server of its own, so it thinks beside the computer's Thinking model.
            if (deep.OnHostRole)
                return shared.Length > 0
                    ? new(true, $"{deep.HostId}'s Thinking pool role runs a model of its own beside {Jobs(shared)} there, so a think runs " +
                        "alongside the conversation and shares its graphics card.", Rank: shared.Any(r => r.Role == SetupRole.Llm) ? 2 : 1)
                    : new(true, $"{deep.HostId}'s Thinking pool role does none of the conversation's jobs, so a think runs there alongside the conversation.");
            if (shared.Any(r => r.Role == SetupRole.Llm))
                return new(false, $"{deep.HostId} also does Thinking for the conversation, and its model can't think something over while " +
                    "it answers you. Add the Thinking pool role there, or add another member to the Thinking pool.");
            return shared.Length > 0
                ? new(true, $"{deep.HostId} also does {Jobs(shared)} for the conversation; a think runs there alongside it and shares its graphics card.", Rank: 1)
                : new(true, $"{deep.HostId} does none of the conversation's jobs, so a think runs there alongside the conversation.");
        }
        return new(true, "It runs on its own provider, so a think runs alongside the conversation.", Rank: 1);
    }

    // A route served on this PC: a loopback endpoint (Ollama, LM Studio...) or this PC's own host service.
    private static bool IsThisPc(SetupRoute route) =>
        route.RouteType == SetupRouteType.ChatCompletions && Uri.TryCreate(route.Origin, UriKind.Absolute, out var origin) && origin.IsLoopback ||
        SelfHostSetup.IsGateway(route.RouteType) && Uri.TryCreate(route.Gateway?.Origin, UriKind.Absolute, out var gateway) && gateway.IsLoopback;

    private static bool SameServer(DeepThinkingSettings deep, SetupRoute? thinking) =>
        thinking?.RouteType == SetupRouteType.ChatCompletions && Uri.TryCreate(thinking.Origin, UriKind.Absolute, out var a) &&
        Uri.TryCreate(deep.Origin, UriKind.Absolute, out var b) && a.Authority == b.Authority;

    private static string Jobs(IEnumerable<SetupRoute> routes) => string.Join(" and ", routes.Select(r => r.Role switch
    {
        SetupRole.Llm => "Thinking",
        SetupRole.Tts => "the voice",
        _ => "listening"
    }).Distinct());
}

/// <summary>One place Deep thinking can think on: its settings (a single place), whether a think can run there and how
/// it shares the conversation's hardware (<see cref="DeepThinkingPlan.Rank"/>), and the computer's name.</summary>
public sealed record DeepThinkingSpot(DeepThinkingSettings Settings, DeepThinkingPlan Plan, string Computer)
{
    public string Key => Settings.Key;
}

/// <summary>Every place Deep thinking is set to think on (Companion › Deep thinking: the place chosen and the computers ticked
/// Think here too), each with its own <see cref="DeepThinkingPlan"/>. Several thinks run at once, one on each usable place, the
/// one sharing least with the conversation first; one place that can't run (a computer that also does Thinking without its
/// Deep thinking role) doesn't stop the others.</summary>
public sealed record DeepThinkingPool(IReadOnlyList<DeepThinkingSpot> Spots)
{
    /// <param name="sharing">Devices › Sharing work: a paired computer Deep thinking never uses, or one kept for other companion
    /// PCs than <paramref name="device"/>, can't run a think from this PC.</param>
    /// <param name="offline">The paired computers that don't answer now (by host ID; null: every computer counts as online). A
    /// place on one of them that would run gets an <see cref="DeepThinkingPlan.Offline"/> plan instead, so <see cref="Usable"/>
    /// and the slots leave it out until it answers again.</param>
    public static DeepThinkingPool For(DeepThinkingSettings deep, IReadOnlyList<SetupRoute> routes,
        Martlet.Core.Cluster.WorkSharingSettings? sharing = null, string? device = null, IReadOnlyCollection<string>? offline = null)
    {
        ArgumentNullException.ThrowIfNull(deep);
        ArgumentNullException.ThrowIfNull(routes);
        var thinking = routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        return new([.. deep.Places.Select(place => new DeepThinkingSpot(place, Kept(place, sharing, device) ??
            Present(place, DeepThinkingPlan.For(place, routes), offline), place.Computer(thinking)))]);
    }

    // A place that would run, on a computer that doesn't answer now: offline until it answers again. A place that can't run
    // anyway keeps its own reason, which is the one the owner can act on.
    private static DeepThinkingPlan Present(DeepThinkingSettings place, DeepThinkingPlan plan, IReadOnlyCollection<string>? offline) =>
        plan.Available && place.Place == DeepThinkingPlace.Host && place.HostId is { } host && offline?.Contains(host, StringComparer.Ordinal) == true
            ? DeepThinkingPlan.Away(host) : plan;

    private static DeepThinkingPlan? Kept(DeepThinkingSettings place, Martlet.Core.Cluster.WorkSharingSettings? sharing, string? device)
    {
        if (sharing is null || place.Place != DeepThinkingPlace.Host || place.HostId is not { } host) return null;
        if (sharing.Job(Martlet.Core.Cluster.WorkSharingJobs.DeepThinking).Never.Contains(host, StringComparer.Ordinal))
            return new(false, $"Devices › Sharing work says the Thinking pool never uses {host}.");
        return device is null || sharing.Allows(host, device) ? null
            : new(false, $"{host} is kept for {string.Join(" and ", sharing.OnlyFor(host))} (Devices › Sharing work).");
    }

    /// <summary>The places a think can run on now, in the order they were chosen.</summary>
    public IReadOnlyList<DeepThinkingSpot> Usable => [.. Spots.Where(spot => spot.Plan.Available)];

    public DeepThinkingSpot? Find(string key) => Spots.FirstOrDefault(spot => spot.Key == key);

    /// <summary>How many thinks run at once on places with <paramref name="slots"/> slots in all: one fewer than the slots when
    /// there are two or more, because the Thinking pool keeps its last free slot for quick jobs (judges and summaries; the rule
    /// is BackgroundPlaces' in Martlet.Conversation), else the one slot; at most <see cref="DeepThinkingSettings.MaxPlaces"/>,
    /// the most thinks that run or wait at once.</summary>
    public static int AtOnce(int slots) => Math.Min(slots >= 2 ? slots - 1 : Math.Max(slots, 0), DeepThinkingSettings.MaxPlaces);

    /// <summary>Whether Deep thinking can run anywhere and why: the first place's plan with one place, else how many think at once.
    /// When nothing can run because computers that would run are offline, it says so (<see cref="DeepThinkingPlan.Offline"/>).</summary>
    public DeepThinkingPlan Plan
    {
        get
        {
            var usable = Usable;
            if (usable.Count == 0 && Spots.Where(spot => spot.Plan.Offline).ToArray() is { Length: > 0 } away) return Gone(away);
            if (Spots.Count == 1 || usable.Count == 0) return Spots[0].Plan;
            if (usable.Count == 1) return usable[0].Plan;
            var names = usable.Select(spot => spot.Computer).Distinct(StringComparer.Ordinal).ToArray();
            var atOnce = AtOnce(usable.Sum(spot => spot.Settings.ThinksAtOnce));
            return new(true, (atOnce == 1 ? "One think runs at a time" : $"Up to {atOnce} thinks run at once") +
                " alongside the conversation on its places (" + Names(names) +
                "); each new one goes to a free place that shares least with the conversation and isn't kept for other work, " +
                "and waits in line when every place is busy. The last free slot stays free for quick jobs (judges and summaries).",
                Rank: usable.Min(spot => spot.Plan.Rank));
        }
    }

    // Nothing can run, and these places would but their computers are offline.
    private DeepThinkingPlan Gone(IReadOnlyList<DeepThinkingSpot> away)
    {
        if (away.Count == 1) return away[0].Plan;
        var names = Names([.. away.Select(spot => spot.Computer).Distinct(StringComparer.Ordinal)]);
        return new(false, (away.Count == Spots.Count(spot => spot.Settings.Separate) ? $"Every Thinking pool computer is offline ({names})"
            : $"{names} are offline") + "; their slots come back when they answer again.") { Offline = true };
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}