using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Companion › Thinking pool, on this PC (thinking-pool.json in the data folder; never shared, because which machine
/// is free depends on the computer you talk to). The pool is one shared set of Thinking models for background work: thinking
/// longer, research, screen and sound summaries, judges and helpers. Each member is one place (<see cref="DeepThinkingSettings"/>,
/// a single place without its own pool): a paired Martlet host with a Thinking model, which joins by itself
/// (<see cref="ThinkingPoolAutoJoin"/>; its Thinking pool role, or its Ollama) unless the owner took it out
/// (<see cref="LeftByOwner"/>), a model in Ollama on this PC, or an OpenAI-compatible endpoint, each with its slot count. The live conversation keeps
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

    /// <summary>Backup Thinking (Companion › Thinking pool): when the conversation's Thinking model has no first words after
    /// <see cref="BackupDelayMs"/>, the same request also goes to a member that may answer for the conversation
    /// (<see cref="AnswersForConversation"/>), and whichever answers first gives the reply. Off by default.</summary>
    public bool BackupThinking { get; init; }

    /// <summary>How long Backup Thinking waits for the first words, in milliseconds (one of <see cref="BackupDelayChoices"/>);
    /// null: automatic, from how fast recent replies began (at least 900 ms).</summary>
    public int? BackupDelayMs { get; init; }

    /// <summary>The members (by <see cref="DeepThinkingSettings.Key"/>) the owner allowed to answer for the conversation (May answer
    /// for the conversation, off for each member by default). Backup Thinking asks only these; a paid cloud provider is never
    /// asked unless it is ticked here.</summary>
    public IReadOnlyList<string> AnswersForConversation { get => answering; init => answering = value ?? []; }
    private readonly IReadOnlyList<string> answering = [];

    /// <summary>Higher priority first: when the pool has no free slot for a request, it stops a running request of lower
    /// priority to make space. The stopped request keeps its priority and goes back to the front of the line for that priority.
    /// On by default.</summary>
    public bool PreemptLowerPriority { get; init; } = true;

    /// <summary>After a request was stopped this many times for higher-priority work, its priority goes up by one, and again after
    /// each further such number of stops, until it completes (<see cref="MinRaiseAfterStops"/>-<see cref="MaxRaiseAfterStops"/>).</summary>
    public int RaisePriorityAfterStops { get; init; } = DefaultRaiseAfterStops;

    /// <summary>How many more times a Thinking request that failed (or timed out) is tried again, at the priority it had, before it
    /// fails (0-<see cref="MaxRetries"/>).</summary>
    public int RetriesOnFailure { get; init; } = DefaultRetries;

    public const int DefaultRaiseAfterStops = 3, MinRaiseAfterStops = 1, MaxRaiseAfterStops = 20;
    public const int DefaultRetries = 1, MaxRetries = 10;

    /// <summary>The fixed delays Backup Thinking offers, in milliseconds.</summary>
    public static IReadOnlyList<int> BackupDelayChoices { get; } = [500, 700, 900, 1200, 1500, 2000, 3000];

    /// <summary>Whether the member with <paramref name="key"/> may answer for the conversation.</summary>
    public bool Answers(string key) => AnswersForConversation.Contains(key, StringComparer.Ordinal);

    /// <summary>The pool with the member with <paramref name="key"/> allowed (or no longer allowed) to answer for the conversation.</summary>
    public ThinkingPoolSettings WithAnswers(string key, bool answers) => this with
    {
        AnswersForConversation = [.. AnswersForConversation.Where(k => k != key), .. answers ? new[] { key } : []]
    };

    /// <summary>The members (by <see cref="DeepThinkingSettings.Key"/>) the owner keeps from quick jobs (Quick jobs unticked): the
    /// judges and the screen and sound summaries. Empty: every member takes them. A file saved before this list existed reads as
    /// empty.</summary>
    public IReadOnlyList<string> NoQuickJobs { get => noQuickJobs; init => noQuickJobs = value ?? []; }
    private readonly IReadOnlyList<string> noQuickJobs = [];

    /// <summary>The members (by <see cref="DeepThinkingSettings.Key"/>) the owner keeps from long jobs (Long jobs unticked): thinking
    /// longer, research, a song's lyrics, touch zones, remembering, naming and check-ins. Empty: every member takes them. A file
    /// saved before this list existed reads as empty.</summary>
    public IReadOnlyList<string> NoLongJobs { get => noLongJobs; init => noLongJobs = value ?? []; }
    private readonly IReadOnlyList<string> noLongJobs = [];

    /// <summary>Whether the member with <paramref name="key"/> takes quick jobs.</summary>
    public bool TakesQuickJobs(string key) => !NoQuickJobs.Contains(key, StringComparer.Ordinal);

    /// <summary>Whether the member with <paramref name="key"/> takes long jobs.</summary>
    public bool TakesLongJobs(string key) => !NoLongJobs.Contains(key, StringComparer.Ordinal);

    /// <summary>The pool with the member with <paramref name="key"/> taking quick jobs (<paramref name="quick"/>) and long jobs
    /// (<paramref name="long"/>), or as before where null.</summary>
    public ThinkingPoolSettings WithJobs(string key, bool? quick = null, bool? @long = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return this with
        {
            NoQuickJobs = quick is { } q ? [.. NoQuickJobs.Where(k => k != key), .. q ? [] : new[] { key }] : NoQuickJobs,
            NoLongJobs = @long is { } l ? [.. NoLongJobs.Where(k => k != key), .. l ? [] : new[] { key }] : NoLongJobs
        };
    }

    /// <summary>The external members (by <see cref="DeepThinkingSettings.Key"/>) the owner allowed to receive pictures and
    /// recordings (May receive pictures and recordings, off for each external member by default). An external member is an
    /// endpoint that is not on this PC (<see cref="IsExternal"/>); this PC and paired Martlet computers always may. A file saved
    /// before this list existed reads as empty.</summary>
    public IReadOnlyList<string> MediaAllowed { get => mediaAllowed; init => mediaAllowed = value ?? []; }
    private readonly IReadOnlyList<string> mediaAllowed = [];

    /// <summary>Whether <paramref name="member"/> is outside this PC and the paired Martlet computers: an OpenAI-compatible
    /// endpoint whose address is not this PC (a computer on the home network or a cloud provider).</summary>
    public static bool IsExternal(DeepThinkingSettings member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Place == DeepThinkingPlace.Endpoint && !member.OnThisPc;
    }

    /// <summary>Whether <paramref name="member"/> may receive jobs with a picture or a recording: always on this PC and on a
    /// paired computer, and on an external member only when the owner ticked it.</summary>
    public bool MayReceiveMedia(DeepThinkingSettings member) =>
        !IsExternal(member) || MediaAllowed.Contains(member.Key, StringComparer.Ordinal);

    /// <summary>The pool with the member with <paramref name="key"/> allowed (or no longer allowed) to receive pictures and recordings.</summary>
    public ThinkingPoolSettings WithMedia(string key, bool allow)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return this with { MediaAllowed = [.. MediaAllowed.Where(k => k != key), .. allow ? new[] { key } : []] };
    }

    /// <summary>The pool as long jobs see it (thinking longer, research and a song's lyrics): only the members that take long
    /// jobs. When none does, those jobs act as with an empty pool: the conversation model, when that is allowed.</summary>
    public ThinkingPoolSettings ForLongJobs() =>
        NoLongJobs.Count == 0 ? this : this with { Members = [.. Members.Where(m => TakesLongJobs(m.Key))] };

    /// <summary>The paired computers (host IDs) the owner took out of the pool (In the Thinking pool unticked). Martlet adds a
    /// computer with a Thinking model by itself (<see cref="ThinkingPoolAutoJoin"/>), but never one in this list; ticking it
    /// again takes it off the list. A file saved before this list existed reads as empty.</summary>
    public IReadOnlyList<string> LeftByOwner { get => leftByOwner; init => leftByOwner = value ?? []; }
    private readonly IReadOnlyList<string> leftByOwner = [];

    /// <summary>The most computers kept out; keeping out one more forgets the oldest.</summary>
    public const int MaxLeftByOwner = 64;

    /// <summary>Whether the owner took <paramref name="hostId"/> out of the pool, so it never joins by itself.</summary>
    public bool Left(string hostId) => LeftByOwner.Contains(hostId, StringComparer.Ordinal);

    /// <summary>The pool with <paramref name="hostId"/> kept out (or no longer kept out). The members don't change.</summary>
    public ThinkingPoolSettings KeepOut(string hostId, bool keepOut)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostId);
        return this with
        {
            LeftByOwner = [.. LeftByOwner.Where(h => h != hostId).Concat(keepOut ? new[] { hostId } : []).TakeLast(MaxLeftByOwner)]
        };
    }

    /// <summary>The pool without <paramref name="hostId"/>'s members (one for each of its graphics cards with a Thinking pool
    /// model), with the computer kept out so it doesn't join again by itself (the owner unticked In the Thinking pool).</summary>
    public ThinkingPoolSettings TakeOut(string hostId) =>
        Members.Where(m => m.Place == DeepThinkingPlace.Host && m.HostId == hostId).Select(m => m.Key).Append(DeepThinkingSettings.KeyOf(hostId))
            .Distinct(StringComparer.Ordinal).Aggregate(this, (pool, key) => pool.Remove(key)).KeepOut(hostId, true);

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

    /// <summary>The pool without the member whose key is <paramref name="key"/>, and without its rules.</summary>
    public ThinkingPoolSettings Remove(string key) => this with
    {
        Members = [.. Members.Where(m => m.Key != key)], AnswersForConversation = [.. AnswersForConversation.Where(k => k != key)],
        NoQuickJobs = [.. NoQuickJobs.Where(k => k != key)], NoLongJobs = [.. NoLongJobs.Where(k => k != key)],
        MediaAllowed = [.. MediaAllowed.Where(k => k != key)]
    };

    /// <summary>Every place the planner considers: the members, or the conversation model while the pool is empty and that is
    /// allowed. An empty pool without it plans nothing that can run. <paramref name="offline"/> (the paired computers that don't
    /// answer now; null: all count as online) leaves those members out of what can run; when nothing else can run, thinking
    /// longer and research use the conversation model meanwhile, as with an empty pool, when that is allowed.</summary>
    public DeepThinkingPool Plan(IReadOnlyList<SetupRoute> routes, Martlet.Core.Cluster.WorkSharingSettings? sharing = null, string? device = null,
        IReadOnlyCollection<string>? offline = null)
    {
        if (Members.Count == 0 && !UseConversationModelWhenEmpty)
            return new([new DeepThinkingSpot(new(), new(false, "The Thinking pool has no member, and Use the conversation model when " +
                "the pool is empty is off. Add a member on Companion › Thinking pool."), "no member")]);
        var pool = DeepThinkingPool.For(Places, routes, sharing, device, offline);
        if (Members.Count == 0 || !UseConversationModelWhenEmpty || pool.Plan is not { Available: false, Offline: true } gone) return pool;
        // The members that would run are all offline: the conversation model stands in, as for an empty pool, until one answers.
        var conversation = DeepThinkingPool.For(new(), routes, sharing, device).Spots[0];
        if (!conversation.Plan.Available) return pool;
        return new([conversation with
        {
            Plan = conversation.Plan with { Why = $"{gone.Why} Meanwhile thinking longer and research use the conversation model. {conversation.Plan.Why}" }
        }, .. pool.Spots]);
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "The Thinking pool file is from a newer Martlet.");
        ContractRules.Require(Members is { Count: <= DeepThinkingSettings.MaxPlaces } && Members.All(m => m is { Pool: null } && m.Separate) &&
            Members.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count() == Members.Count,
            $"The Thinking pool has at most {DeepThinkingSettings.MaxPlaces} different members, each a paired computer or an endpoint.");
        ContractRules.Require(BackupDelayMs is null || BackupDelayChoices.Contains(BackupDelayMs.Value),
            $"Backup Thinking waits one of {string.Join(", ", BackupDelayChoices)} ms, or automatic.");
        ContractRules.Require(AnswersForConversation.Count <= 2 * DeepThinkingSettings.MaxPlaces &&
            AnswersForConversation.All(k => k is { Length: > 0 and <= 4096 } && !k.Any(char.IsControl)),
            "The members that may answer for the conversation are a short list of member keys.");
        ContractRules.Require(new[] { NoQuickJobs, NoLongJobs }.All(keys => keys.Count <= 2 * DeepThinkingSettings.MaxPlaces &&
            keys.All(k => k is { Length: > 0 and <= 4096 } && !k.Any(char.IsControl))),
            "The members kept from quick or long jobs are a short list of member keys.");
        ContractRules.Require(RaisePriorityAfterStops is >= MinRaiseAfterStops and <= MaxRaiseAfterStops,
            $"A stopped request's priority goes up after {MinRaiseAfterStops}-{MaxRaiseAfterStops} stops.");
        ContractRules.Require(RetriesOnFailure is >= 0 and <= MaxRetries, $"A failed request is tried again 0-{MaxRetries} times.");
        ContractRules.Require(MediaAllowed.Count <= 2 * DeepThinkingSettings.MaxPlaces &&
            MediaAllowed.All(k => k is { Length: > 0 and <= 4096 } && !k.Any(char.IsControl)),
            "The members that may receive pictures and recordings are a short list of member keys.");
        ContractRules.Require(LeftByOwner.Count <= MaxLeftByOwner &&
            LeftByOwner.All(h => h is { Length: > 0 and <= 128 } && !h.Any(char.IsControl)) &&
            LeftByOwner.Distinct(StringComparer.Ordinal).Count() == LeftByOwner.Count,
            $"The computers kept out of the Thinking pool are at most {MaxLeftByOwner} different computer names.");
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
/// graphics card; these are guidance, never a block. A member that can't run says why. With <c>gpus</c> (the graphics cards a
/// paired computer said serve a route: host ID, route ID), a member on a card of its own beside the Thinking model's card isn't
/// warned about sharing it.</summary>
public static class ThinkingPoolWarnings
{
    public static IReadOnlyList<string> For(DeepThinkingPool pool, IReadOnlyList<SetupRoute> routes,
        Func<string, string, IReadOnlyList<string>>? gpus = null)
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
            if (shared.FirstOrDefault(r => r.Role == SetupRole.Llm) is { } thinking && !OwnCard(member, thinking, gpus))
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

    /// <summary>Whether a member on a paired computer runs on graphics cards its host named, none of which serve the
    /// conversation's Thinking route there (both known; "cpu" is no card).</summary>
    public static bool OwnCard(DeepThinkingSettings member, SetupRoute thinking, Func<string, string, IReadOnlyList<string>>? gpus)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(thinking);
        if (gpus is null || member is not { Place: DeepThinkingPlace.Host, HostId: { } host } || thinking.Gateway?.HostId != host) return false;
        var mine = gpus(host, member.HostRoute);
        var theirs = gpus(host, thinking.GatewaySnapshot?.RouteId ?? SelfHostSetup.OllamaRouteId);
        return mine.Count > 0 && theirs.Count > 0 && !mine.Contains("cpu", StringComparer.Ordinal) &&
            !mine.Any(card => theirs.Contains(card, StringComparer.Ordinal));
    }
}
