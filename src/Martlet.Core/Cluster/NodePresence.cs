using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Settings;

namespace Martlet.Core.Cluster;

/// <summary>What happened to one of your other computers, as the companion PC's Home tells it.</summary>
public enum PresenceChangeKind
{
    /// <summary>It stopped answering and stayed silent for <see cref="PresenceWatch.MissingAfter"/> (one missed check is not enough).</summary>
    WentMissing,
    /// <summary>It is still missing after the time chosen in Settings (<see cref="NodePresenceSettings"/>). Raised once per absence.</summary>
    StayedAway,
    /// <summary>It went missing and now answered again for <see cref="PresenceWatch.BackAfter"/>.</summary>
    CameBack
}

/// <summary>One change of a computer's presence: its host ID, its name in words (the computer's name when known, else the host
/// ID), what happened and when (for <see cref="PresenceChangeKind.WentMissing"/> the time it stopped answering, for
/// <see cref="PresenceChangeKind.CameBack"/> the time it answered again).</summary>
public sealed record PresenceChange(string HostId, string Name, PresenceChangeKind Kind, DateTimeOffset At);

/// <summary>Where one computer stands for the notices.</summary>
public enum NodePresenceState
{
    /// <summary>It answers (or the notices know nothing else about it).</summary>
    Answering,
    /// <summary>It missed a check, but not for long enough to be missing: maybe only a flaky moment.</summary>
    NotAnswering,
    /// <summary>It stopped answering: Home says Martlet works with less.</summary>
    Missing,
    /// <summary>Missing for longer than the time chosen in Settings.</summary>
    Away,
    /// <summary>It was missing and answers again, but not yet for <see cref="PresenceWatch.BackAfter"/>.</summary>
    Returning,
    /// <summary>It came back; Home says so for <see cref="PresenceWatch.BackShownFor"/> or until dismissed.</summary>
    Back
}

/// <summary>One computer the notices know about. <paramref name="Since"/> is when it stopped answering (the start of the absence;
/// a short answer while it flaps does not end it), <paramref name="BackAt"/> when it came back, and <paramref name="AwayFor"/>
/// how long the absence lasted (so far, or in all when it is back).</summary>
public sealed record NodePresenceHost(string HostId, string Name, NodePresenceState State, DateTimeOffset? Since, DateTimeOffset? BackAt,
    TimeSpan? AwayFor);

/// <summary>Turns which computers answer now into the notices on the companion PC's Home and the events the recommended setup
/// listens to. It keeps no record of checks itself: each call gives it when a computer stopped answering, as the desktop's one
/// presence record (HostPresence) knows it, and it adds patience on top. A computer is missing only after it stayed silent
/// for <see cref="MissingAfter"/> (two of device sync's 15-second checks), so one flaky miss says nothing; it is back only after
/// it answered for <see cref="BackAfter"/>, so a computer that flaps stays one absence; and it stayed away once it is missing
/// for <see cref="AwayAfter"/>. Each event is raised once per absence. Not thread-safe: the desktop uses it on its UI thread.</summary>
public sealed class PresenceWatch
{
    public static readonly TimeSpan MissingAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BackAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BackShownFor = TimeSpan.FromMinutes(10);

    private sealed class Absence(string name, DateTimeOffset since)
    {
        public string Name { get; set; } = name;
        public DateTimeOffset Since { get; } = since;
        public bool Missing { get; set; }
        public bool Away { get; set; }
        public DateTimeOffset? AnsweringSince { get; set; }
    }

    private sealed record Return(string Name, DateTimeOffset At, TimeSpan AwayFor);

    private readonly Dictionary<string, Absence> absences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Return> returns = new(StringComparer.Ordinal);

    /// <summary>How long a missing computer stays away before <see cref="PresenceChangeKind.StayedAway"/>.</summary>
    public TimeSpan AwayAfter { get; set; } = TimeSpan.FromMinutes(NodePresenceSettings.DefaultAwayMinutes);

    /// <summary>The computers it knows something about: missing, not answering, returning or just back.</summary>
    public IReadOnlyCollection<string> Tracked => [.. absences.Keys.Union(returns.Keys, StringComparer.Ordinal)];

    /// <summary>Notes where <paramref name="hostId"/> stands at <paramref name="now"/>: <paramref name="offlineSince"/> is when
    /// it stopped answering, or null while it answers. Returns the changes this makes, in order (usually none).</summary>
    public IReadOnlyList<PresenceChange> Observe(string hostId, string name, DateTimeOffset? offlineSince, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        var changes = new List<PresenceChange>();
        if (offlineSince is { } off)
        {
            if (!absences.TryGetValue(hostId, out var absence))
                absences[hostId] = absence = new(name, off <= now ? off : now);
            absence.Name = name;
            absence.AnsweringSince = null;
            if (!absence.Missing && now - absence.Since >= MissingAfter)
            {
                absence.Missing = true;
                returns.Remove(hostId);
                changes.Add(new(hostId, name, PresenceChangeKind.WentMissing, absence.Since));
            }
            if (absence.Missing && !absence.Away && now - absence.Since >= AwayAfter)
            {
                absence.Away = true;
                changes.Add(new(hostId, name, PresenceChangeKind.StayedAway, now));
            }
            return changes;
        }
        if (!absences.TryGetValue(hostId, out var gone)) return changes;
        // A computer that answers before it was missing only missed a check or two: nothing to say.
        if (!gone.Missing)
        {
            absences.Remove(hostId);
            return changes;
        }
        gone.Name = name;
        gone.AnsweringSince ??= now;
        if (now - gone.AnsweringSince.Value < BackAfter) return changes;
        absences.Remove(hostId);
        returns[hostId] = new(name, gone.AnsweringSince.Value, gone.AnsweringSince.Value - gone.Since);
        changes.Add(new(hostId, name, PresenceChangeKind.CameBack, gone.AnsweringSince.Value));
        return changes;
    }

    /// <summary>Forgets a computer that is no longer paired.</summary>
    public void Forget(string hostId)
    {
        absences.Remove(hostId);
        returns.Remove(hostId);
    }

    /// <summary>Hides the "is back" notice of <paramref name="hostId"/>; returns whether there was one.</summary>
    public bool DismissBack(string hostId) => returns.Remove(hostId);

    /// <summary>When <paramref name="hostId"/> stopped answering, while it is not back yet (a computer that missed one check
    /// counts from that check); null while it answers.</summary>
    public DateTimeOffset? Since(string hostId) => absences.TryGetValue(hostId, out var absence) ? absence.Since : null;

    /// <summary>Where each computer it knows about stands at <paramref name="now"/>, sorted by host ID. An "is back" notice
    /// older than <see cref="BackShownFor"/> is dropped here.</summary>
    public IReadOnlyList<NodePresenceHost> Hosts(DateTimeOffset now)
    {
        foreach (var old in returns.Where(r => now - r.Value.At >= BackShownFor).Select(r => r.Key).ToList()) returns.Remove(old);
        var hosts = absences.Select(pair => new NodePresenceHost(pair.Key, pair.Value.Name,
                pair.Value.AnsweringSince is not null ? NodePresenceState.Returning
                : pair.Value.Away ? NodePresenceState.Away
                : pair.Value.Missing ? NodePresenceState.Missing : NodePresenceState.NotAnswering,
                pair.Value.Since, null, now - pair.Value.Since))
            .Concat(returns.Select(pair => new NodePresenceHost(pair.Key, pair.Value.Name, NodePresenceState.Back, null, pair.Value.At,
                pair.Value.AwayFor)));
        return [.. hosts.OrderBy(h => h.HostId, StringComparer.Ordinal)];
    }
}

/// <summary>The per-PC choice "Look for a better setup when a computer is away for N minutes" (node-presence.txt in the data
/// directory: the number of minutes). Not shared: each PC keeps its own.</summary>
public static class NodePresenceSettings
{
    public const string FileName = "node-presence.txt";
    public const int DefaultAwayMinutes = 10;
    public const int MinimumAwayMinutes = 1;
    public const int MaximumAwayMinutes = 240;
    /// <summary>The choices Settings offers.</summary>
    public static readonly IReadOnlyList<int> Choices = [2, 5, 10, 15, 30, 60];

    /// <summary>The saved minutes, or <see cref="DefaultAwayMinutes"/> when nothing usable is saved.</summary>
    public static int AwayMinutes(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) ?? DefaultAwayMinutes : DefaultAwayMinutes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return DefaultAwayMinutes; }
    }

    /// <summary>The minutes in <paramref name="text"/>, or null when it is not a whole number from 1 to 240.</summary>
    public static int? Parse(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) &&
        minutes is >= MinimumAwayMinutes and <= MaximumAwayMinutes ? minutes : null;

    public static void SaveAwayMinutes(string dataDirectory, int minutes)
    {
        if (minutes is < MinimumAwayMinutes or > MaximumAwayMinutes)
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, $"Choose {MinimumAwayMinutes} to {MaximumAwayMinutes} minutes.");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, FileName), minutes.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>The words of the presence notices on the companion PC's Home, from the shared plan of who does what.</summary>
public static class NodePresenceNotices
{
    /// <summary>The Home item ID of a missing computer's warning.</summary>
    public static string MissingId(string hostId) => "presence-missing-" + hostId;

    /// <summary>The Home item ID of a computer's "is back" notice.</summary>
    public static string BackId(string hostId) => "presence-back-" + hostId;

    public static string MissingTitle(string name) => $"Working with less: {name} isn't answering";

    public static string BackTitle(string name) => $"{name} is back";

    /// <summary>What a missing computer did for you and what Martlet does without it: the jobs a failover moved, the jobs that
    /// stay with it, and the pools (Speaking, Listening, Thinking pool) and other roles it was part of.
    /// <paramref name="plan"/> is null while device sync is off; <paramref name="elsewhereAway"/> are the other computers that
    /// don't answer either, which don't count as taking over.</summary>
    public static string MissingDetail(string hostId, ClusterPlan? plan, TimeSpan offlineFor, IReadOnlyCollection<string>? elsewhereAway = null)
    {
        var parts = new List<string>
        {
            offlineFor < TimeSpan.FromMinutes(1) ? "It stopped answering less than a minute ago." : $"It hasn't answered for {Duration(offlineFor)}."
        };
        if (plan is null)
        {
            parts.Add("Turn on Keep Martlet the same on all my computers to see what it did for you.");
            return string.Join(" ", parts);
        }
        var jobs = plan.Assignments.Where(a => ClusterJobs.All.Contains(a.Job)).ToList();
        var moved = jobs.Where(a => a.MovedFrom == hostId && a.HostId is { } to && to != hostId).ToList();
        if (moved.Count > 0)
            parts.Add("Martlet moved " + Join(moved.Select(a => $"{ClusterTitle(a.Job)} to {a.HostId}")) + ".");
        var stuck = jobs.Where(a => a.HostId == hostId && !a.Off).Select(a => ClusterTitle(a.Job)).ToList();
        if (stuck.Count > 0)
            parts.Add($"{Join(stuck)} stay{(stuck.Count == 1 ? "s" : "")} with it until it is back or you move {(stuck.Count == 1 ? "it" : "them")} on the Devices page.");

        var away = elsewhereAway ?? [];
        var others = plan.Nodes.Where(n => !n.Removed && n.HostId != hostId && !away.Contains(n.HostId)).ToList();
        var groups = (plan.Node(hostId) is { Removed: false } node ? node.Roles : []).Select(r => Group(r.Kind)).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
        var shared = groups.Where(g => g.EndsWith(" pool", StringComparison.Ordinal)).ToList();
        bool ElsewhereToo(string group) => others.Any(n => n.Roles.Any(r => Group(r.Kind) == group));
        var goOn = shared.Where(ElsewhereToo).ToList();
        if (goOn.Count > 0) parts.Add($"Your {Join(goOn)} go{(goOn.Count == 1 ? "es" : "")} on with your other computers.");
        var alone = shared.Where(g => !ElsewhereToo(g)).ToList();
        if (alone.Count > 0) parts.Add($"Your {Join(alone)} {(alone.Count == 1 ? "has" : "have")} no other computer now.");
        // Roles outside the pools that no other computer runs, and that no job above names already.
        var only = groups.Except(shared).Where(g => !ElsewhereToo(g) && !stuck.Contains(g) && moved.All(a => ClusterTitle(a.Job) != g)).ToList();
        if (only.Count > 0) parts.Add($"No other computer does {Join(only)}.");
        if (moved.Count + stuck.Count + groups.Count == 0) parts.Add("Nothing depended on it.");
        return string.Join(" ", parts);
    }

    /// <summary>"It answers again after 12 minutes away." and the jobs a failover moved away from it, which stay where they are.</summary>
    public static string BackDetail(string hostId, ClusterPlan? plan, TimeSpan awayFor)
    {
        var parts = new List<string>
        {
            awayFor < TimeSpan.FromMinutes(1) ? "It answers again after less than a minute away." : $"It answers again after {Duration(awayFor)} away."
        };
        var moved = plan?.Assignments.Where(a => ClusterJobs.All.Contains(a.Job) && a.MovedFrom == hostId && a.HostId is { } to && to != hostId)
            .ToList() ?? [];
        if (moved.Count > 0)
            parts.Add($"{Join(moved.Select(a => $"{ClusterTitle(a.Job)} is still on {a.HostId}"))}. Move {(moved.Count == 1 ? "it" : "them")} " +
                "back on the Devices page if you want.");
        return string.Join(" ", parts);
    }

    /// <summary>"12 minutes", "1 minute", "3 hours".</summary>
    public static string Duration(TimeSpan span)
    {
        var minutes = (int)Math.Max(1, Math.Floor(span.TotalMinutes));
        if (minutes < 120) return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
        var hours = minutes / 60;
        return $"{hours} hours";
    }

    /// <summary>"Not answering for 3 min" on the Devices map, or null under a minute.</summary>
    public static string? MapText(TimeSpan offlineFor) =>
        offlineFor < TimeSpan.FromMinutes(1) ? null : $"Not answering for {(int)Math.Floor(offlineFor.TotalMinutes)} min";

    // What a host role is part of: a pool your computers share, or a job of its own. Thinking (Ollama) counts only through the
    // Thinking job above: a computer can run it without anyone using it.
    private static string? Group(string kind) => kind switch
    {
        "stt" => "Listening pool",
        "deep-thinking" or "deep-thinking-2" or "deep-thinking-3" or "deep-thinking-4" => "Thinking pool",
        "audio2face" => "Lip-sync",
        "singing" => "Singing",
        "pictures" => "Pictures",
        "ocr" => "Reading",
        _ => SpeechEngines.ForRoleKind(kind) is not null ? "Speaking pool" : null
    };

    private static string ClusterTitle(string job) => job == ClusterJobs.LipSync ? "Lip-sync" : char.ToUpperInvariant(job[0]) + job[1..];

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list[..^1]) + " and " + list[^1];
    }
}

/// <summary>A notice as Home shows it, for the presence report.</summary>
public sealed record NodePresenceNotice(string Id, string Level, string Title, string Detail);

/// <summary>What the presence notices know now (node-presence.json in the data directory), written by the desktop when a
/// computer's state changes so node_presence_status can read it without the desktop. Nonsecret: host IDs, computer names
/// and times only. It describes this run of Martlet only: a new run starts with every computer answering.</summary>
public sealed record NodePresenceReport
{
    public const string FileName = "node-presence.json";
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
    public required int AwayMinutes { get; init; }
    public required bool Companion { get; init; }
    public IReadOnlyList<NodePresenceHost> Hosts { get; init; } = [];
    public IReadOnlyList<NodePresenceNotice> Notices { get; init; } = [];

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The saved report, or null when there is none or it can't be read.</summary>
    public static NodePresenceReport? Load(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            return JsonSerializer.Deserialize<NodePresenceReport>(File.ReadAllBytes(path), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
}
