using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Cluster;

/// <summary>The jobs a cluster plan assigns. Hosts store any job name, so a newer desktop's jobs pass through older hosts.</summary>
public static class ClusterJobs
{
    public const string Thinking = "thinking";
    public const string Listening = "listening";
    public const string Speaking = "speaking";
    public const string LipSync = "lip-sync";
    public static readonly IReadOnlyList<string> All = [Thinking, Listening, Speaking, LipSync];

    /// <summary>Not a role: the paired host that desktops older than shared logs send every computer's logs to (the old
    /// "log host"). Newer desktops share every computer's logs with every host and never set or use it; the entry stays in the
    /// plan only so older desktops keep sending their logs somewhere newer ones read them. It never moves or fails over.</summary>
    public const string Logs = "logs";
}

/// <summary>Who does one job. <see cref="HostId"/> null means each desktop uses its own choice (its Setup route, or this
/// PC for lip-sync); <see cref="Off"/> is lip-sync by nobody (voice loudness). <see cref="Failover"/> lets a desktop move
/// the job to another host that runs the same engine when the host in charge stops answering; <see cref="MovedFrom"/>
/// names the host a failover moved it away from.</summary>
public sealed record ClusterAssignment
{
    public required string Job { get; init; }
    public string? HostId { get; init; }
    public bool Off { get; init; }
    public bool Failover { get; init; }
    public string? MovedFrom { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    /// <summary>Same owner, failover choice and failover origin, whatever the stamp.</summary>
    public bool SameAs(ClusterAssignment? other) => other is not null && Job == other.Job && HostId == other.HostId &&
        Off == other.Off && Failover == other.Failover && MovedFrom == other.MovedFrom;

    internal string Content => $"{HostId}|{Off}|{Failover}|{MovedFrom}|{UpdatedAt.UtcTicks}";
}

public sealed record ClusterNodeRole
{
    public required string Kind { get; init; }
    public required string Model { get; init; }
}

/// <summary>One Martlet host in the cluster and the roles (with models) it was last seen running. <see cref="Removed"/> is
/// a tombstone, so a forgotten host does not come back from an older copy.</summary>
public sealed record ClusterNode
{
    public required string HostId { get; init; }
    public string? Origin { get; init; }
    public IReadOnlyList<ClusterNodeRole> Roles { get; init; } = [];
    public bool Removed { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    /// <summary>Same address, roles and removal, whatever the stamp.</summary>
    public bool SameAs(ClusterNode? other) => other is not null && HostId == other.HostId && Origin == other.Origin &&
        Removed == other.Removed && Roles.SequenceEqual(other.Roles);

    internal string Content => $"{Origin}|{Removed}|{string.Join(',', Roles.Select(r => r.Kind + "=" + r.Model))}|{UpdatedAt.UtcTicks}";
}

/// <summary>Who does what across the owner's computers, shared by every paired Martlet host and every desktop that keeps
/// them in sync. It holds one last-writer-wins entry per job and per host; <see cref="Merge"/> is commutative,
/// associative and idempotent, so every copy converges whatever order the changes arrive in. Revisions are hybrid
/// clocks (<see cref="NextRevision"/>): later changes win even between copies that were apart for a while, and a
/// computer with a wrong clock cannot make later changes lose. Nonsecret: host IDs, origins and model names only.</summary>
public sealed record ClusterPlan
{
    public const int MaximumBytes = 16_384;
    public const int MaximumAssignments = 8;
    public const int MaximumNodes = 32;
    public const int MaximumRoles = 8;
    private const long MaximumRevision = long.MaxValue / 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 6
    };

    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<ClusterAssignment> Assignments { get; init; }
    public required IReadOnlyList<ClusterNode> Nodes { get; init; }

    public static ClusterPlan Empty { get; } = new() { SchemaVersion = 1, Assignments = [], Nodes = [] };

    /// <summary>The newest stamp in the plan (0 when empty).</summary>
    [JsonIgnore]
    public long Revision => Math.Max(Assignments.Select(a => a.Revision).DefaultIfEmpty(0).Max(),
        Nodes.Select(n => n.Revision).DefaultIfEmpty(0).Max());

    public ClusterAssignment? For(string job) => Assignments.FirstOrDefault(a => a.Job == job);

    public ClusterNode? Node(string hostId) => Nodes.FirstOrDefault(n => n.HostId == hostId);

    /// <summary>A revision newer than everything in this plan and, normally, than anything written before now.</summary>
    public long NextRevision(DateTimeOffset now) => Math.Max(Revision + 1, now.ToUnixTimeMilliseconds());

    /// <summary>Records a new owner (or failover choice) for a job, stamped by <paramref name="by"/>.</summary>
    public ClusterPlan Assign(string job, string? hostId, bool off, bool failover, string? movedFrom, string by, DateTimeOffset now)
    {
        var assignment = new ClusterAssignment
        {
            Job = job, HostId = off ? null : hostId, Off = off, Failover = failover,
            MovedFrom = off || hostId is null ? null : movedFrom,
            Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by
        };
        return this with { Assignments = Sorted(Assignments.Where(a => a.Job != job).Append(assignment)) };
    }

    /// <summary>Records what a host runs (or that it was forgotten), stamped by <paramref name="by"/>.</summary>
    public ClusterPlan Observe(string hostId, string? origin, IEnumerable<ClusterNodeRole> roles, bool removed, string by, DateTimeOffset now)
    {
        var node = new ClusterNode
        {
            HostId = hostId, Origin = removed ? null : origin,
            Roles = removed ? [] : roles.OrderBy(r => r.Kind, StringComparer.Ordinal).ToArray(),
            Removed = removed, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by
        };
        return this with { Nodes = Sorted(Nodes.Where(n => n.HostId != hostId).Append(node)) };
    }

    /// <summary>Joins two copies: per job and per host the entry with the newest (revision, writer, content) wins.</summary>
    public static ClusterPlan Merge(ClusterPlan left, ClusterPlan right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var assignments = left.Assignments.Concat(right.Assignments)
            .GroupBy(a => a.Job, StringComparer.Ordinal)
            .Select(group => group.Aggregate((a, b) => Newer(a.Revision, a.UpdatedBy, a.Content, b.Revision, b.UpdatedBy, b.Content) ? a : b))
            .OrderBy(a => a.Job, StringComparer.Ordinal).Take(MaximumAssignments).ToArray();
        var nodes = left.Nodes.Concat(right.Nodes)
            .GroupBy(n => n.HostId, StringComparer.Ordinal)
            .Select(group => group.Aggregate((a, b) => Newer(a.Revision, a.UpdatedBy, a.Content, b.Revision, b.UpdatedBy, b.Content) ? a : b))
            .OrderBy(n => n.Removed).ThenByDescending(n => n.Revision).ThenBy(n => n.HostId, StringComparer.Ordinal)
            .Take(MaximumNodes).OrderBy(n => n.HostId, StringComparer.Ordinal).ToArray();
        return new() { SchemaVersion = 1, Assignments = assignments, Nodes = nodes };
    }

    private static bool Newer(long revision, string by, string content, long otherRevision, string otherBy, string otherContent) =>
        revision != otherRevision ? revision > otherRevision
        : by != otherBy ? string.CompareOrdinal(by, otherBy) > 0
        : string.CompareOrdinal(content, otherContent) >= 0;

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "This cluster plan was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Assignments is { Count: <= MaximumAssignments } && Nodes is { Count: <= MaximumNodes },
            "The cluster plan lists too many jobs or hosts.");
        ContractRules.Require(Assignments.All(a => a is not null) && Nodes.All(n => n is not null), "A cluster entry is missing.");
        ContractRules.Require(Assignments.Select(a => a.Job).Distinct(StringComparer.Ordinal).Count() == Assignments.Count &&
            Nodes.Select(n => n.HostId).Distinct(StringComparer.Ordinal).Count() == Nodes.Count,
            "The cluster plan lists a job or host twice.");
        foreach (var assignment in Assignments)
        {
            ContractRules.Require(Name(assignment.Job), "A cluster job name is invalid.");
            Stamp(assignment.Revision, assignment.UpdatedBy);
            if (assignment.HostId is not null) ContractRules.Identifier(assignment.HostId);
            if (assignment.MovedFrom is not null) ContractRules.Identifier(assignment.MovedFrom);
            ContractRules.Require(!assignment.Off || assignment.HostId is null && assignment.MovedFrom is null,
                "A job that nobody does cannot name a host.");
        }
        foreach (var node in Nodes)
        {
            ContractRules.Identifier(node.HostId);
            Stamp(node.Revision, node.UpdatedBy);
            ContractRules.Require(node.Origin is null || node.Origin.Length <= 256 &&
                Uri.TryCreate(node.Origin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps,
                "A cluster host address is invalid.");
            ContractRules.Require(node.Roles is { Count: <= MaximumRoles } &&
                node.Roles.All(r => r is not null && Name(r.Kind) && IsModelName(r.Model)) &&
                node.Roles.Select(r => r.Kind).Distinct(StringComparer.Ordinal).Count() == node.Roles.Count,
                "A cluster host's roles are invalid.");
        }
    }

    private static void Stamp(long revision, string? by)
    {
        ContractRules.Require(revision is > 0 and <= MaximumRevision, "A cluster revision is out of range.");
        ContractRules.Identifier(by);
    }

    private static bool Name(string? text) => text is { Length: > 0 and <= 32 } && char.IsAsciiLetterLower(text[0]) &&
        text.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>Whether <paramref name="text"/> can be recorded as a host role's model.</summary>
    public static bool IsModelName(string? text) => text is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(text[0]) &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/');

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { Assignments = Sorted(Assignments), Nodes = Sorted(Nodes) }, Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The cluster plan is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static ClusterPlan Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The cluster plan is empty or too large.", ErrorCode.PayloadTooLarge);
        ClusterPlan? plan;
        try { plan = JsonSerializer.Deserialize<ClusterPlan>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The cluster plan is malformed.");
        }
        ContractRules.Require(plan is not null, "The cluster plan is empty.");
        plan!.Validate();
        return plan with { Assignments = Sorted(plan.Assignments), Nodes = Sorted(plan.Nodes) };
    }

    /// <summary>Identifies the plan's content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    private static ClusterAssignment[] Sorted(IEnumerable<ClusterAssignment> items) =>
        items.OrderBy(a => a.Job, StringComparer.Ordinal).ToArray();

    private static ClusterNode[] Sorted(IEnumerable<ClusterNode> items) =>
        items.OrderBy(n => n.HostId, StringComparer.Ordinal).ToArray();

    public override string ToString() => $"Cluster plan r{Revision} ({Assignments.Count} jobs, {Nodes.Count} hosts)";
}
