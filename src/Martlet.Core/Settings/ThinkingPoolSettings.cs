using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Companion › Thinking pool, on this PC (thinking-pool.json in the data folder; never shared, because which machine
/// is free depends on the computer you talk to). The pool is one shared set of Thinking models for background work: thinking
/// longer, research, screen and sound summaries, judges and helpers. Each member is one place (<see cref="DeepThinkingSettings"/>,
/// a single place without its own pool): a paired Martlet host whose owner turned Join the Thinking pool on (its Thinking pool
/// role), a model in Ollama on this PC, or an OpenAI-compatible endpoint, each with its slot count. The live conversation keeps
/// its own Thinking route; pool work never uses it, except that thinking longer and research use the conversation model while
/// the pool is empty when <see cref="UseConversationModelWhenEmpty"/> is on (the default). Martlet writes this file from the
/// older deep-thinking.json once, the first time it reads the pool.</summary>
public sealed record ThinkingPoolSettings
{
    public const string FileName = "thinking-pool.json";
    private const int MaxFileBytes = 128 * 1024;

    public int SchemaVersion { get; init; } = 1;
    /// <summary>The members, in the order they were added; at most <see cref="DeepThinkingSettings.MaxPlaces"/>.</summary>
    public IReadOnlyList<DeepThinkingSettings> Members { get; init; } = [];
    /// <summary>Use the conversation model when the pool is empty: thinking longer and research then run on the conversation's own
    /// Thinking route (as Deep thinking's Same as Thinking did). Other job kinds get "no member" and use their own fallback.</summary>
    public bool UseConversationModelWhenEmpty { get; init; } = true;
    /// <summary>When the file was made from deep-thinking.json, else null.</summary>
    public DateTimeOffset? MigratedAt { get; init; }

    /// <summary>The members as one place with its pool (the first member and the others), as Martlet's planner reads them. An
    /// empty pool reads as the conversation model (Same as Thinking).</summary>
    [JsonIgnore]
    public DeepThinkingSettings Places => Members.Count == 0 ? new() : Members[0].Single.WithPool(Members.Skip(1));

    /// <summary>The pool with <paramref name="places"/>' places as members (the conversation model is never a member).</summary>
    public ThinkingPoolSettings With(DeepThinkingSettings places) =>
        this with { Members = [.. places.Places.Where(p => p.Separate).Select(p => p.Single).DistinctBy(p => p.Key).Take(DeepThinkingSettings.MaxPlaces)] };

    /// <summary>The pool with <paramref name="member"/> added (or replaced: the same computer, or the same endpoint and model).</summary>
    public ThinkingPoolSettings Add(DeepThinkingSettings member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var single = member.Single;
        ContractRules.Require(single.Separate, "The conversation model isn't a pool member.");
        var index = Members.ToList().FindIndex(m => m.Key == single.Key);
        if (index >= 0) return this with { Members = [.. Members.Select((m, i) => i == index ? single : m)] };
        ContractRules.Require(Members.Count < DeepThinkingSettings.MaxPlaces, $"The Thinking pool has at most {DeepThinkingSettings.MaxPlaces} members.");
        return this with { Members = [.. Members, single] };
    }

    /// <summary>The pool without the member whose key is <paramref name="key"/>.</summary>
    public ThinkingPoolSettings Remove(string key) => this with { Members = [.. Members.Where(m => m.Key != key)] };

    /// <summary>Every place the planner considers: the members, or the conversation model while the pool is empty and that is
    /// allowed. An empty pool without it plans nothing that can run.</summary>
    public DeepThinkingPool Plan(IReadOnlyList<SetupRoute> routes, Martlet.Core.Cluster.WorkSharingSettings? sharing = null, string? device = null)
    {
        if (Members.Count == 0 && !UseConversationModelWhenEmpty)
            return new([new DeepThinkingSpot(new(), new(false, "The Thinking pool has no member, and Use the conversation model when " +
                "the pool is empty is off. Add a member on Companion › Thinking pool."), "no member")]);
        return DeepThinkingPool.For(Places, routes, sharing, device);
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "The Thinking pool file is from a newer Martlet.");
        ContractRules.Require(Members is { Count: <= DeepThinkingSettings.MaxPlaces } && Members.All(m => m is { Pool: null } && m.Separate) &&
            Members.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count() == Members.Count,
            $"The Thinking pool has at most {DeepThinkingSettings.MaxPlaces} different members, each a paired computer or an endpoint.");
        foreach (var member in Members) member.Validate();
    }

    public static ThinkingPoolSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>Saves <paramref name="places"/>' places as the members, keeping the other choices; false when the folder can't
    /// be written.</summary>
    public static bool SavePlaces(string? directory, DeepThinkingSettings places) => Load(directory).With(places).Save(directory);

    /// <summary>The saved pool and the file's state: none, loaded, migrated (made from deep-thinking.json just now; saved unless
    /// <paramref name="save"/> is false, for read-only callers) or unreadable (an empty pool).</summary>
    public static (ThinkingPoolSettings Settings, string State) Read(string? directory, bool save = true)
    {
        if (directory is null) return (new(), "none");
        var path = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                var (deep, state) = DeepThinkingSettings.Read(directory);
                if (state != "loaded") return (new(), "none");
                var migrated = new ThinkingPoolSettings { MigratedAt = DateTimeOffset.UtcNow }.With(deep);
                // Saved once; a folder that can't be written reads the old file again next time.
                if (save) migrated.Save(directory);
                return (migrated, "migrated");
            }
            if (new FileInfo(path).Length > MaxFileBytes) return (new(), "unreadable");
            var loaded = JsonSerializer.Deserialize<ThinkingPoolSettings>(File.ReadAllText(path));
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

    /// <summary>Saves thinking-pool.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null) return false;
        Validate();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"thinking-pool.{Guid.NewGuid():N}.tmp");
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
}

/// <summary>Plain-words warnings about likely slowdowns in the Thinking pool: a member on the same computer, graphics card or
/// Ollama server as the conversation's Thinking model, or on the same computer as the voice. Pool jobs may run on every
/// graphics card; these are guidance, never a block. A member that can't run says why.</summary>
public static class ThinkingPoolWarnings
{
    public static IReadOnlyList<string> For(DeepThinkingPool pool, IReadOnlyList<SetupRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(routes);
        List<string> warnings = [];
        var live = routes.Where(r => r.Enabled != false).ToArray();
        foreach (var spot in pool.Spots.Where(s => s.Settings.Separate))
        {
            var member = spot.Settings;
            var name = member.Describe();
            if (!spot.Plan.Available)
            {
                warnings.Add($"{name} can't run pool jobs: {spot.Plan.Why}");
                continue;
            }
            SetupRoute[] shared = member.Place == DeepThinkingPlace.Host
                ? [.. live.Where(r => SelfHostSetup.IsGateway(r.RouteType) && r.Gateway?.HostId == member.HostId)]
                : member.OnThisPc ? [.. live.Where(IsThisPc)] : [];
            if (shared.FirstOrDefault(r => r.Role == SetupRole.Llm) is { } thinking)
                warnings.Add(SameServer(member, thinking)
                    ? $"{name} runs in the same Ollama server as the conversation's Thinking model ({thinking.ModelId}): replies may start later while it works, and Martlet checks both fit first."
                    : $"{name} shares {(member.Place == DeepThinkingPlace.Host ? member.HostId : "this PC")} and its graphics card with the conversation's Thinking model: replies may start later while it works.");
            if (shared.Any(r => r.Role == SetupRole.Tts))
                warnings.Add($"{name} runs on the same computer as the voice: speech may slow down while it works.");
        }
        return warnings;
    }

    private static bool IsThisPc(SetupRoute route) =>
        route.RouteType == SetupRouteType.ChatCompletions && Uri.TryCreate(route.Origin, UriKind.Absolute, out var origin) && origin.IsLoopback ||
        SelfHostSetup.IsGateway(route.RouteType) && Uri.TryCreate(route.Gateway?.Origin, UriKind.Absolute, out var gateway) && gateway.IsLoopback;

    private static bool SameServer(DeepThinkingSettings member, SetupRoute thinking) =>
        member.Place == DeepThinkingPlace.Endpoint && thinking.RouteType == SetupRouteType.ChatCompletions &&
        Uri.TryCreate(thinking.Origin, UriKind.Absolute, out var a) && Uri.TryCreate(member.Origin, UriKind.Absolute, out var b) &&
        a.Authority == b.Authority;
}
