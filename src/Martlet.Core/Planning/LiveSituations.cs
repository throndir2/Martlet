using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Planning;

// Situations (docs/RECOMMENDATION_DESIGN.md, stage 2): the plans a companion PC changes between by itself while it runs.
// Normal: the usual setup. While gaming: a game runs on a companion PC whose owner said it is used for games, so live Thinking
// leaves its graphics card. Host away: the host that runs live Thinking stopped answering. The decision is pure and cheap, so
// the desktop makes it again at every change; it never runs on a reply's path.

/// <summary>The plan a companion PC is in now: <see cref="Normal"/>, <see cref="Gaming"/> (While gaming) or
/// <see cref="HostAway"/> (Host away).</summary>
public enum Situation { Normal, Gaming, HostAway }

/// <summary>Where live Thinking runs normally on this PC: not set up, this PC's graphics card (Ollama on this PC, or this PC's
/// own host service), a paired host, or online (a hosted provider or a server on another address).</summary>
public enum LiveHome { None, ThisPc, Host, Online }

/// <summary>A place live Thinking can go in a situation: another paired host, the hosted backup (Companion › Thinking › If
/// Thinking fails), or this PC's own model in Ollama.</summary>
public enum LivePlaceKind { Host, Hosted, ThisPc }

/// <summary>One place live Thinking can go. <see cref="HostId"/> is the paired host for <see cref="LivePlaceKind.Host"/>.
/// <see cref="CardKept"/>: this PC's card is kept for a game, so the model runs on the processor whenever the game needs the
/// card's memory.</summary>
public sealed record LivePlace(LivePlaceKind Kind, string Name, string Model)
{
    public string? HostId { get; init; }
    public bool CardKept { get; init; }

    /// <summary>"gpu-box (qwen3:8b)", "NVIDIA Build (nvidia/nemotron...)" or "this PC's gemma4:e2b".</summary>
    public string Text => Kind switch
    {
        LivePlaceKind.ThisPc => CardKept ? $"this PC's {Model} (on the processor when the game needs the graphics card's memory)"
            : $"this PC's {Model}",
        _ => $"{Name} ({Model})"
    };
}

/// <summary>A paired host that runs a Thinking chat model (its Ollama role) and that this PC can use: its ID, its name and
/// the model.</summary>
public sealed record LiveHost(string HostId, string Name, string Model);

/// <summary>What the situation decision reads, all known on this PC without a request: where live Thinking runs normally,
/// whether the owner said this PC is used for games and the game running now, the hosts that went missing, the other hosts
/// that run a Thinking model (in the order to try), the hosted backup and whether the online services preference allows it,
/// and the model Ollama on this PC has for Thinking.</summary>
public sealed record SituationFacts
{
    public LiveHome Home { get; init; }
    /// <summary><see cref="LiveHome.Host"/>: the host that runs live Thinking, and its name.</summary>
    public string? HomeHost { get; init; }
    public string? HomeName { get; init; }
    /// <summary>The model live Thinking uses normally.</summary>
    public string? HomeModel { get; init; }
    /// <summary>The owner said this PC is used for games (or Martlet guessed it from a game library).</summary>
    public bool PlaysGames { get; init; }
    /// <summary>The game running on this PC now, or null.</summary>
    public string? Game { get; init; }
    /// <summary>Paired hosts that went missing (no answer for about 30 seconds).</summary>
    public IReadOnlyCollection<string> Away { get; init; } = [];
    /// <summary>Other paired hosts with a Thinking chat model, in the order to try them.</summary>
    public IReadOnlyList<LiveHost> Hosts { get; init; } = [];
    /// <summary>The hosted backup's name ("NVIDIA Build") and model, when one is set up (Companion › Thinking › If Thinking
    /// fails, not on this PC).</summary>
    public string? Backup { get; init; }
    public string? BackupModel { get; init; }
    /// <summary>The online services preference allows the hosted backup (Only as a backup or Yes; not Never).</summary>
    public bool BackupAllowed { get; init; } = true;
    /// <summary>A Thinking model Ollama on this PC has, or null.</summary>
    public string? LocalModel { get; init; }
}

/// <summary>The situation now and where live Thinking goes: <see cref="Place"/> (null: nowhere else can take it, so it stays
/// where it is), the whole <see cref="Order"/> and why, in words.</summary>
public sealed record SituationDecision(Situation Situation, LivePlace? Place, IReadOnlyList<LivePlace> Order, string Why)
{
    /// <summary>Live Thinking leaves its normal route: a host or the hosted backup, or this PC's own model while a host is away.
    /// While gaming, this PC's own model is the normal route itself.</summary>
    public bool Moves => Place is { Kind: not LivePlaceKind.ThisPc } || Place is not null && Situation == Situation.HostAway;

    /// <summary>The same for the same situation and place, so a change is noticed once.</summary>
    public string Key => $"{Situation}|{Place?.Kind}|{Place?.HostId}|{Place?.Name}|{Place?.Model}|{Place?.CardKept}";

    /// <summary>"While gaming", "Host away" or "Normal".</summary>
    public string Title => LiveSituations.Title(Situation);
}

/// <summary>The production situation rules. While gaming, live Thinking moves from this PC's graphics card to a host that
/// answers, then to the hosted backup when the online services preference allows it, then stays on this PC's own model (the
/// card's model stays loaded while the game leaves room; Martlet unloads it only when the card is short of memory, and Ollama
/// then runs it on the processor). Host away: when the host that runs live Thinking stops answering, live Thinking uses another
/// host, the allowed hosted backup, then this PC's own model; it comes back when the host answers again.</summary>
public static class LiveSituations
{
    /// <summary>How long a game counts as running after Martlet last saw it (a short switch to another window doesn't end it).</summary>
    public static TimeSpan GameHold { get; } = TimeSpan.FromSeconds(90);

    /// <summary>A graphics card with less free memory than this is short of memory: the game needs it.</summary>
    public const long ShortBytes = 1L << 30;

    /// <summary>Whether a card with <paramref name="totalBytes"/> of memory, <paramref name="usedBytes"/> of it in use, is short
    /// of memory (less than <see cref="ShortBytes"/> free).</summary>
    public static bool ShortOfMemory(long totalBytes, long usedBytes) => totalBytes > 0 && totalBytes - usedBytes < ShortBytes;

    public static string Title(Situation situation) => situation switch
    {
        Situation.Gaming => "While gaming",
        Situation.HostAway => "Host away",
        _ => "Normal"
    };

    /// <summary>The situation and where live Thinking goes now. Pure.</summary>
    public static SituationDecision Decide(SituationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var game = facts.Game is { Length: > 0 } running ? running : null;
        var away = facts.Away.ToHashSet(StringComparer.Ordinal);
        var homeName = facts.HomeName ?? facts.HomeHost ?? "the host";
        var backupNote = facts.Backup is not null && !facts.BackupAllowed
            ? $" Online services are set to Never, so {facts.Backup} isn't used." : "";

        if (facts.Home == LiveHome.ThisPc && game is not null && facts.PlaysGames)
        {
            var order = Others(facts, away, null).ToList();
            order.Add(new(LivePlaceKind.ThisPc, "This PC", facts.HomeModel ?? facts.LocalModel ?? "its model") { CardKept = true });
            var place = order[0];
            var why = place.Kind == LivePlaceKind.ThisPc
                ? $"A game ({game}) runs on this PC, and no other place can do live Thinking, so it stays on this PC's {place.Model}. " +
                  "Martlet unloads it only when the game needs the graphics card's memory; Ollama then runs it on the processor." + backupNote
                : $"A game ({game}) runs on this PC, so live Thinking leaves its graphics card for {place.Text}. The model stays loaded " +
                  "while the game leaves room, and live Thinking comes back when the game ends." + backupNote;
            return new(Situation.Gaming, place, order, why);
        }

        if (facts.Home == LiveHome.Host && facts.HomeHost is { } home && away.Contains(home))
        {
            var order = Others(facts, away, home).ToList();
            if (facts.LocalModel is { } local)
                order.Add(new(LivePlaceKind.ThisPc, "This PC", local) { CardKept = game is not null && facts.PlaysGames });
            if (order.Count == 0)
                return new(Situation.HostAway, null, order,
                    $"{homeName} doesn't answer, and no other place can do live Thinking, so Martlet waits for it." + backupNote);
            return new(Situation.HostAway, order[0], order,
                $"{homeName} doesn't answer, so live Thinking uses {order[0].Text} until {homeName} answers again." + backupNote);
        }

        var normal = facts.Home switch
        {
            LiveHome.None => "Thinking isn't set up yet.",
            LiveHome.ThisPc when game is not null =>
                $"A game ({game}) runs, but you said this PC isn't used for games, so live Thinking stays on this PC's graphics card.",
            LiveHome.ThisPc => "Live Thinking runs on this PC's graphics card.",
            LiveHome.Host => $"Live Thinking runs on {homeName}.",
            _ => "Live Thinking runs online, so games and hosts that go away don't move it."
        };
        return new(Situation.Normal, null, [], normal);
    }

    // Other hosts that answer (in order), then the hosted backup when allowed.
    private static IEnumerable<LivePlace> Others(SituationFacts facts, HashSet<string> away, string? except)
    {
        foreach (var host in facts.Hosts)
            if (host.HostId != except && !away.Contains(host.HostId) && host.HostId != facts.HomeHost)
                yield return new(LivePlaceKind.Host, host.Name, host.Model) { HostId = host.HostId };
        if (facts.Backup is { } backup && facts.BackupAllowed)
            yield return new(LivePlaceKind.Hosted, backup, facts.BackupModel ?? "its model");
    }
}

/// <summary>A situation's Thinking route for the conversation, never saved: <see cref="Route"/> replaces the saved Thinking
/// route whose configuration revision is <see cref="ForRoute"/> (a stale override after the owner changed Thinking does
/// nothing). <see cref="Text"/> says where it goes, in words.</summary>
public sealed record SituationOverride(string Key, Guid ForRoute, SetupRoute Route, string Text);

/// <summary>Builds the Thinking route a situation moves live Thinking to, from what this PC already has: a paired host's
/// pinned gateway and pairing, the If Thinking fails endpoint with its own key, or Ollama on this PC. Each is enabled with the
/// owner's recorded selection, since the owner chose these places (the pairing, the backup with the online services
/// preference, this PC). Deterministic: the same place gives the same route.</summary>
public static class SituationRoutes
{
    /// <summary>A paired host's Ollama chat route.</summary>
    public static SetupRoute Host(GatewayEndpointSettings endpoint, Guid credentialId, string deviceId, GatewayRouteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(snapshot);
        return Consented(new()
        {
            RouteSchemaVersion = 1, RouteType = SetupRouteType.GatewayOllama, Enabled = true, Role = SetupRole.Llm,
            ProviderAlias = SelfHostSetup.GatewayOllamaAlias, Origin = endpoint.Origin, ModelId = snapshot.ModelId,
            CredentialId = credentialId, Gateway = endpoint, GatewayDeviceId = deviceId, GatewaySnapshot = snapshot,
            ConfigurationRevision = Revision($"host|{endpoint.HostId}|{snapshot.ModelId}|{snapshot.ProbeRevision}")
        });
    }

    /// <summary>The hosted backup (If Thinking fails) as the Thinking route, with its own key: the key is scoped to that exact
    /// endpoint the same way (<see cref="ThinkingFallbackSettings.Binding"/>).</summary>
    public static SetupRoute Hosted(ThinkingFallbackSettings fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return Chat(fallback.Origin, fallback.ModelId, fallback.CredentialId);
    }

    /// <summary>A Chat Completions server on this PC (Ollama) with <paramref name="model"/>, no key.</summary>
    public static SetupRoute Local(string baseUrl, string model) => Chat(baseUrl, model, null);

    private static SetupRoute Chat(string origin, string model, Guid? credential) => Consented(new()
    {
        RouteSchemaVersion = 1, RouteType = SetupRouteType.ChatCompletions, Enabled = true, Role = SetupRole.Llm,
        ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin, ModelId = model, CredentialId = credential,
        ConfigurationRevision = Revision($"chat|{origin}|{model}|{credential}")
    });

    private static SetupRoute Consented(SetupRoute route) => route with { Consent = route.Selection() };

    private static Guid Revision(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("martlet-situation|" + text)).AsSpan(0, 16).ToArray();
        bytes[7] = (byte)(bytes[7] & 0x0F | 0x50);
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes);
    }

    /// <summary><paramref name="settings"/> with <paramref name="situation"/>'s Thinking route in place of the saved one, for the
    /// conversation only (never saved). Unchanged when there is no override, the saved Thinking route changed since the decision,
    /// or the result isn't valid. A host pairing kept aside (retained) or listed for removal is in use again for the
    /// conversation, so the copy leaves it out of those lists.</summary>
    public static AppSettings Apply(AppSettings settings, SituationOverride? situation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (situation is null || settings.Setup is not { } setup ||
            setup.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm) is not { } saved || saved.ConfigurationRevision != situation.ForRoute)
            return settings;
        var id = situation.Route.CredentialId;
        var changed = settings with
        {
            Setup = setup with
            {
                Routes = [.. setup.Routes.Select(r => r.Role == SetupRole.Llm ? situation.Route : r)],
                PendingRemovals = [.. setup.PendingRemovals.Where(p => id is null || p.CredentialId != id)],
                RetainedGatewayCredentials = setup.RetainedGatewayCredentials is { } retained
                    ? [.. retained.Where(r => id is null || r.CredentialId != id)] : null
            }
        };
        try
        {
            changed.Validate();
            return changed;
        }
        catch (ContractException) { return settings; }
    }
}

/// <summary>situation.json in the data directory: what the desktop decided last (for MCP situation_status). Host IDs, model and
/// provider names, a game's name and times only; never what was said.</summary>
public sealed record SituationReport
{
    public const string FileName = "situation.json";
    public const int MaximumBytes = 65_536;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
        MaxDepth = 8
    };

    public int SchemaVersion { get; init; } = 1;
    public required DateTimeOffset UpdatedAt { get; init; }
    public required Situation Situation { get; init; }
    /// <summary>Where live Thinking runs normally, in words.</summary>
    public required string Normal { get; init; }
    /// <summary>Where it runs now (null: where it normally runs).</summary>
    public string? Now { get; init; }
    public LivePlaceKind? NowKind { get; init; }
    public string? NowHost { get; init; }
    public IReadOnlyList<string> Order { get; init; } = [];
    public required string Why { get; init; }
    /// <summary>The conversation uses the moved route (an open conversation switches between replies).</summary>
    public bool Applied { get; init; }
    public bool PlaysGames { get; init; }
    public string? Game { get; init; }
    public IReadOnlyList<string> Away { get; init; } = [];
    public bool BackupAllowed { get; init; }
    public string? Online { get; init; }
    /// <summary>The graphics card note while gaming: "12.0 GB, 2.4 GB free: gemma4:e2b stays loaded." or that it was unloaded.</summary>
    public string? Card { get; init; }
    /// <summary>FIXTURE (MARTLET_SIMULATE_SITUATION): the game and the hosts away are simulated.</summary>
    public bool Simulated { get; init; }

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The saved report, or null when there is none or it can't be read.</summary>
    public static SituationReport? Load(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            return JsonSerializer.Deserialize<SituationReport>(File.ReadAllBytes(path), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
}
