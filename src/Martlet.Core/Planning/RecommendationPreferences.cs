using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

/// <summary>What matters more in a conversation (the first-run wizard's third question; docs/RECOMMENDATION_DESIGN.md). It sets
/// live Thinking's first-word target (<see cref="RecommendationPreferences.TargetMs"/>): Balanced 0.4 s, Quick replies 0.25 s
/// (and the smaller of two models within one quality step), Smarter replies 1.0 s (and the smarter one).</summary>
public enum ReplyQuality { Balanced, Quick, Smarter }

/// <summary>May Martlet use free online services? Backup (the default): only as a backup, so no live job (Thinking, the voice,
/// listening, lip-sync) goes online in the normal plan, but backup Thinking and Deep thinking may. Never: nothing online. Yes,
/// when they're faster or smarter: online options compete with local ones.</summary>
public enum OnlineServices { Backup, Never, Yes }

/// <summary>One computer's answer to "Do you play games or use heavy apps on this PC?", by its Martlet device ID.</summary>
public sealed record GamesAnswer(string Device, bool Plays);

/// <summary>A job's lock in Recommended setup (docs/RECOMMENDATION_DESIGN.md, "Owners who run their own models").
/// <see cref="Locked"/> false: the owner lets Martlet choose the job's model. <see cref="Chose"/>: the choice (an option ID) that
/// ran when they unlocked it, or that Martlet last set up for it; when the job runs another choice later, the owner changed it by
/// hand, so it counts as locked again.</summary>
public sealed record JobLock(string Job, bool Locked)
{
    public string? Chose { get; init; }
}

/// <summary>Kept: the owner never chose, so Martlet keeps what runs today (the same as locked). Locked: the owner locked it.
/// Unlocked: the owner lets Martlet choose. ChangedByHand: unlocked, but the owner changed it by hand since, so it counts as
/// locked.</summary>
public enum JobLockState { Kept, Locked, Unlocked, ChangedByHand }

/// <summary>The owner's recommendation preferences (docs/RECOMMENDATION_DESIGN.md, "Recommendation preferences"):
/// recommendation-preferences.json in the data directory and the <c>recommendation-preferences</c> shared setting, so they are
/// the same on all the owner's computers. The first-run wizard saves the reply quality, the online services and this PC's
/// games answer; Recommended setup and Settings show and change all of them. The games answers are one per computer
/// (<see cref="Games"/>, by device ID); the rest is shared. Nonsecret: choices and device IDs only.</summary>
public sealed record RecommendationPreferences
{
    public const string FileName = "recommendation-preferences.json";
    public const string SharedKey = "recommendation-preferences";
    /// <summary>The host graphics card shares the owner can choose, in percent (the default first).</summary>
    public static IReadOnlyList<int> HostGpuShares { get; } = [90, 75, 50];
    private const int MaximumComputers = 64;
    /// <summary>recommended-setup.json, where Use models your apps already run and Prefer models your hosts already have were
    /// saved on each PC before these preferences.</summary>
    private const string LegacyFile = "recommended-setup.json";

    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        RespectNullableAnnotations = true,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };
    private static readonly JsonSerializerOptions Indented = new(Canonical) { WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;
    /// <summary>Reply quality: live Thinking's first-word target.</summary>
    public ReplyQuality Quality { get; init; }
    /// <summary>Online services: where hosted options may run.</summary>
    public OnlineServices Online { get; init; }
    /// <summary>Prefer models that hear you: a Thinking model that hears counts one quality step up. Off: the model gets the
    /// transcript.</summary>
    public bool PreferHearing { get; init; } = true;
    /// <summary>Host graphics card share: how much of each host's graphics card Martlet may plan with, in percent (90, 75 or 50),
    /// for hosts that also do other work.</summary>
    public int HostGpuShare { get; init; } = 90;
    /// <summary>Use models your apps already run (docs/RECOMMENDED_SETUPS.md).</summary>
    public bool UseServedModels { get; init; } = true;
    /// <summary>Prefer models your hosts already have (docs/RECOMMENDED_SETUPS.md).</summary>
    public bool PreferHostModels { get; init; }
    /// <summary>Each computer's games answer, by its Martlet device ID. A computer without an answer uses Martlet's guess
    /// (a game library found there) on that computer, and No elsewhere.</summary>
    public IReadOnlyList<GamesAnswer> Games { get; init; } = [];
    /// <summary>The jobs' locks in Recommended setup (<see cref="LockableJobs"/>). A job without one is
    /// <see cref="JobLockState.Kept"/>: Martlet keeps what runs today and only suggests a better choice.</summary>
    public IReadOnlyList<JobLock> Locks { get; init; } = [];

    /// <summary>The jobs with a lock (ClusterJobs names): Thinking, Listening and Lip-sync. The voice engine always stays the
    /// owner's choice (their voices are made for it).</summary>
    public static IReadOnlyList<string> LockableJobs { get; } = [ClusterJobs.Thinking, ClusterJobs.Listening, ClusterJobs.LipSync];

    /// <summary>The lock of <paramref name="job"/>, with <paramref name="today"/> the option that does it today (null: not known).</summary>
    public JobLockState LockState(string job, string? today) => Locks.FirstOrDefault(l => l.Job == job) switch
    {
        null => JobLockState.Kept,
        { Locked: true } => JobLockState.Locked,
        { Chose: { } chose } when today is not null && !string.Equals(chose, today, StringComparison.Ordinal) => JobLockState.ChangedByHand,
        _ => JobLockState.Unlocked
    };

    /// <summary>Whether Martlet keeps <paramref name="job"/>'s choice (every state but <see cref="JobLockState.Unlocked"/>).</summary>
    public bool IsLocked(string job, string? today) => LockState(job, today) != JobLockState.Unlocked;

    /// <summary>The same preferences with <paramref name="job"/> locked or unlocked; unlocking remembers <paramref name="today"/>,
    /// so a later change by hand locks it again.</summary>
    public RecommendationPreferences WithLock(string job, bool locked, string? today) =>
        Normalized(this with { Locks = [.. Locks.Where(l => l.Job != job), new JobLock(job, locked) { Chose = locked ? null : today }] });

    /// <summary>After Martlet set up <paramref name="jobs"/> (Reconfigure): each unlocked job remembers the choice Martlet set up,
    /// so it stays unlocked until the owner changes it by hand.</summary>
    public RecommendationPreferences SetUp(IEnumerable<JobPlan> jobs)
    {
        var next = this;
        foreach (var job in jobs)
            if (job?.OptionId is { Length: > 0 and <= 256 } option && Locks.FirstOrDefault(l => l.Job == job.Job) is { Locked: false })
                next = next.WithLock(job.Job, false, option);
        return next;
    }

    /// <summary>Live Thinking's first-word target in milliseconds for <paramref name="quality"/>.</summary>
    public static int TargetMs(ReplyQuality quality) => quality switch
    {
        ReplyQuality.Quick => 250,
        ReplyQuality.Smarter => 1000,
        _ => 400
    };

    /// <summary>Live Thinking's first-word target in milliseconds.</summary>
    [JsonIgnore] public int FirstWordTargetMs => TargetMs(Quality);

    /// <summary>The planners' hosting preference for <see cref="Online"/>.</summary>
    [JsonIgnore] public HostingPreference Hosting => Online switch
    {
        OnlineServices.Never => HostingPreference.PreferLocal,
        OnlineServices.Yes => HostingPreference.Balanced,
        _ => HostingPreference.Backup
    };

    /// <summary>The host graphics card share as a fraction (0.9, 0.75 or 0.5).</summary>
    [JsonIgnore] public double HostGpuFraction => (HostGpuShares.Contains(HostGpuShare) ? HostGpuShare : 90) / 100.0;

    /// <summary>The games answer of the computer with Martlet device ID <paramref name="device"/>; null: not answered.</summary>
    public bool? PlaysGames(string? device) =>
        device is null ? null : Games.FirstOrDefault(g => string.Equals(g.Device, device, StringComparison.Ordinal))?.Plays;

    /// <summary>The same preferences with <paramref name="device"/>'s games answer.</summary>
    public RecommendationPreferences WithGames(string device, bool plays) =>
        Normalized(this with { Games = [.. Games.Where(g => g.Device != device), new GamesAnswer(device, plays)] });

    [JsonIgnore] public bool IsDefault => Share() == new RecommendationPreferences().Share();

    // Lists without repeats, sorted: every computer writes the same JSON for the same choices.
    private static RecommendationPreferences Normalized(RecommendationPreferences preferences) => preferences with
    {
        Games = [.. preferences.Games.Where(g => g is not null).GroupBy(g => g.Device, StringComparer.Ordinal).Select(g => g.Last())
            .OrderBy(g => g.Device, StringComparer.Ordinal)],
        Locks = [.. (preferences.Locks ?? []).Where(l => l is not null).GroupBy(l => l.Job, StringComparer.Ordinal).Select(g => g.Last())
            .OrderBy(l => l.Job, StringComparer.Ordinal)]
    };

    private static bool Name(string? text) => text is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(text[0]) &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private bool Valid() => SchemaVersion == 1 && Enum.IsDefined(Quality) && Enum.IsDefined(Online) && HostGpuShares.Contains(HostGpuShare) &&
        Games is { Count: <= MaximumComputers } && Games.All(g => g is not null && Name(g.Device)) &&
        (Locks ?? []) is { Count: <= 8 } locks && locks.All(l => l is not null && LockableJobs.Contains(l.Job) &&
            l.Chose is null or { Length: > 0 and <= 256 } && (l.Chose ?? "").All(c => c is >= ' ' and <= '~'));

    /// <summary>The canonical JSON every computer writes for the same choices, for sharing.</summary>
    public string Share() => JsonSerializer.Serialize(Normalized(this), Canonical);

    /// <summary>Preferences another computer shared (<see cref="Share"/>); null when they aren't ones this Martlet reads (a newer
    /// Martlet's choice).</summary>
    public static RecommendationPreferences? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<RecommendationPreferences>(json, Canonical);
            return parsed is not null && parsed.Valid() ? Normalized(parsed) : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>Whether the owner's preferences are saved in <paramref name="directory"/>.</summary>
    public static bool Saved(string? directory) => directory is not null && File.Exists(Path.Combine(directory, FileName));

    /// <summary>The saved preferences, or the defaults. Before the owner saved any, Use models your apps already run and Prefer
    /// models your hosts already have come from recommended-setup.json, where each PC kept them before.</summary>
    public static RecommendationPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path)) return new FileInfo(path).Length <= 65_536 ? Parse(File.ReadAllText(path)) ?? new() : new();
            var legacy = Path.Combine(directory, LegacyFile);
            if (!File.Exists(legacy) || new FileInfo(legacy).Length > 65_536) return new();
            var old = JsonNode.Parse(File.ReadAllText(legacy)) as JsonObject;
            return new()
            {
                UseServedModels = old?["UseServedModels"] is JsonValue served && served.TryGetValue<bool>(out var use) ? use : true,
                PreferHostModels = old?["PreferHostModels"] is JsonValue host && host.TryGetValue<bool>(out var prefer) && prefer
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// <summary>Saves recommendation-preferences.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null || !Valid()) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"recommendation-preferences.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(Normalized(this), Indented));
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

    /// <summary>The words for <paramref name="quality"/> as the wizard and Recommended setup show it.</summary>
    public static string Words(ReplyQuality quality) => quality switch
    {
        ReplyQuality.Quick => "Quick replies",
        ReplyQuality.Smarter => "Smarter replies",
        _ => "Balanced"
    };

    /// <summary>The words for <paramref name="online"/> as the wizard and Recommended setup show it.</summary>
    public static string Words(OnlineServices online) => online switch
    {
        OnlineServices.Never => "Never",
        OnlineServices.Yes => "Yes, when they're faster or smarter",
        _ => "Only as a backup"
    };

    /// <summary>One line for logs and MCP: "Balanced (first word 0.4 s), online only as a backup, hearing preferred, hosts 90%".</summary>
    public string Describe() =>
        $"{Words(Quality)} (first word {PlacementEngine.Seconds(FirstWordTargetMs)}), online: {Words(Online).ToLowerInvariant()}, " +
        $"{(PreferHearing ? "models that hear preferred" : "no hearing preference")}, hosts up to {HostGpuShare}%, " +
        $"{(UseServedModels ? "uses" : "doesn't use")} models your apps run, {(PreferHostModels ? "prefers" : "doesn't prefer")} models your hosts have" +
        (Locks.Any(l => !l.Locked) ? $", Martlet may choose {string.Join(" and ", Locks.Where(l => !l.Locked).Select(l => l.Job))}" : "");
}

/// <summary>Live Thinking's choice among local models (docs/RECOMMENDATION_DESIGN.md, "Live Thinking"): the smartest model whose
/// first word meets the reply quality's target, a model that hears counting one quality step up while hearing is preferred,
/// and the fastest one that works when none meets it.</summary>
public static class LiveThinking
{
    /// <summary><paramref name="options"/> in the order a planner tries them for a new live Thinking model: on a graphics card
    /// first. Within each group, the ones that meet <paramref name="quality"/>'s target come first, smartest first (Quick
    /// replies: the smallest within one quality step of the best, then the rest smartest first), then the others fastest first.
    /// <paramref name="quality"/> null: the fastest model that hears first (the planners before preferences).</summary>
    public static IEnumerable<ComponentOption> Order(IEnumerable<ComponentOption> options, ReplyQuality? quality, bool preferHearing = true)
    {
        var list = options.ToList();
        if (quality is not { } wanted)
            return list.OrderBy(o => o.UsesGpu ? 0 : 1).ThenByDescending(o => preferHearing && o.HearsAudio).ThenBy(o => o.FirstWordMs ?? int.MaxValue)
                .ThenBy(o => o.Id, StringComparer.Ordinal);
        var target = RecommendationPreferences.TargetMs(wanted);
        int Score(ComponentOption o) => o.QualityTier + (preferHearing && o.HearsAudio ? 1 : 0);
        IEnumerable<ComponentOption> Ranked(IReadOnlyList<ComponentOption> group)
        {
            var meets = group.Where(o => Meets(o, target)).ToList();
            var best = meets.Count == 0 ? 0 : meets.Max(Score);
            var smartest = meets.OrderByDescending(Score).ThenByDescending(o => preferHearing && o.HearsAudio)
                .ThenBy(o => o.FirstWordMs ?? int.MaxValue).ThenBy(o => o.GpuGb).ThenBy(o => o.Id, StringComparer.Ordinal);
            var first = wanted == ReplyQuality.Quick
                ? meets.Where(o => Score(o) >= best - 1).OrderBy(o => o.GpuGb).ThenByDescending(Score).ThenBy(o => o.FirstWordMs ?? int.MaxValue)
                    .ThenBy(o => o.Id, StringComparer.Ordinal).Concat(smartest.Where(o => Score(o) < best - 1))
                : smartest;
            var rest = group.Where(o => !Meets(o, target)).OrderBy(o => o.FirstWordMs ?? int.MaxValue)
                .ThenByDescending(o => preferHearing && o.HearsAudio).ThenBy(o => o.Id, StringComparer.Ordinal);
            return first.Concat(rest);
        }
        return Ranked([.. list.Where(o => o.UsesGpu)]).Concat(Ranked([.. list.Where(o => !o.UsesGpu)]));
    }

    /// <summary>Whether <paramref name="option"/>'s first word comes within <paramref name="targetMs"/>.</summary>
    public static bool Meets(ComponentOption option, int targetMs) => option.FirstWordMs is { } ms && ms <= targetMs;
}
