using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>What this PC does for one job right now: a paired host, its own choice (<see cref="HostId"/> null: its Setup
/// route, or this PC for lip-sync) or nobody (<see cref="Off"/>, lip-sync only). <see cref="Here"/>: this PC does the job itself
/// (Thinking with its own Ollama) and <see cref="HostId"/> is its own host service, through which your other computers use this
/// PC for it; this PC keeps its direct route, so its own replies never take the extra hop. <see cref="Shared"/>: <see cref="HostId"/>
/// is a host a friend shares with this PC, this PC's own choice that your shared plan never records, seeds or moves.</summary>
internal readonly record struct LocalJob(string? HostId, bool Off, bool Here = false, bool Shared = false);

/// <summary>One background check of a paired host: whether it answered, the routes it offers, its copy of the cluster
/// plan and whether it can share one at all (<see cref="Shares"/> is false for hosts older than cluster sync).</summary>
internal sealed record ClusterProbe(string HostId, bool Reachable, string Text, IReadOnlyList<HostRoute>? Routes = null,
    ClusterPlan? Plan = null, bool Shares = false)
{
    /// <summary>The host answered and advertises the job's route (its role is installed and its model ready).</summary>
    internal bool Serves(string job) => Reachable && Routes?.Any(r => r.RouteId == ClusterSync.RouteId(job)) == true;
}

/// <summary>The rules behind shared "who does what": which host role each job needs, what this PC does now, which host
/// takes over a job whose host stopped answering, and this PC's copy of the plan (cluster.json) and on/off choice
/// (cluster-sync.txt, on unless the owner turned it off) next to the other local preferences.</summary>
internal static class ClusterSync
{
    internal const string PlanFile = "cluster.json";
    internal const string PreferenceFile = "cluster-sync.txt";
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    /// <summary>Consecutive checks a host must miss before its jobs fail over (about 30 seconds).</summary>
    internal const int FailAfter = 2;

    internal static string RoleKind(string job) => job switch
    {
        ClusterJobs.Thinking => HostRoles.Ollama,
        ClusterJobs.Listening => HostRoles.Stt,
        ClusterJobs.Speaking => HostRoles.Speaking,
        ClusterJobs.LipSync => HostRoles.Audio2Face,
        _ => throw new ArgumentOutOfRangeException(nameof(job), job, "Unknown cluster job.")
    };

    internal static string RouteId(string job) => HostRoles.Get(RoleKind(job)).RouteId;

    internal static string Title(string job) => job == ClusterJobs.LipSync ? "Lip-sync" : char.ToUpperInvariant(job[0]) + job[1..];

    /// <summary>Stands in for this PC's own host service where only whether this PC does a job itself matters.</summary>
    internal const string ThisPcMarker = "this PC";

    /// <param name="ownHost">The host service Martlet runs on this PC, when known: a job this PC does itself (Thinking with its
    /// own Ollama) is then done by this PC for the whole network, through that host service.</param>
    /// <param name="shared">Hosts friends share with this PC: a job on one is marked <see cref="LocalJob.Shared"/>.</param>
    internal static LocalJob Local(string job, AppSettings? settings, AvatarProfile? avatar, string? ownHost = null,
        IReadOnlyCollection<string>? shared = null)
    {
        LocalJob local = job switch
        {
            ClusterJobs.LipSync => NetworkMap.LipSync(avatar) switch
            {
                LipSyncHandler.Loudness => new(null, true),
                LipSyncHandler.Host => new(avatar!.RemoteHost!.HostId, false),
                _ => new(null, false)
            },
            ClusterJobs.Thinking when ownHost is not null && OwnOllama(settings) is not null => new(ownHost, false, true),
            ClusterJobs.Thinking => new(NetworkMap.JobHost(settings, SetupRole.Llm), false),
            ClusterJobs.Listening => new(NetworkMap.JobHost(settings, SetupRole.Stt), false),
            _ => new(NetworkMap.JobHost(settings, SetupRole.Tts), false)
        };
        return local.HostId is { } id && !local.Here && shared?.Contains(id) == true ? local with { Shared = true } : local;
    }

    /// <summary>This PC's Thinking route when it is Ollama on this PC itself (enabled), else null.</summary>
    internal static SetupRoute? OwnOllama(AppSettings? settings) =>
        settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm) is { } route && MainWindow.IsLocalOllama(route) &&
        route.Enabled != false ? route : null;

    /// <summary>Whether this PC does what the plan says. A job this PC does itself (<see cref="LocalJob.Here"/>) also matches
    /// "each computer's own choice": the shared settings, not the plan, then move it if the owner's computers chose another route.
    /// A job on a host a friend shares (<see cref="LocalJob.Shared"/>) is this PC's own choice: the plan never moves it.</summary>
    internal static bool Matches(ClusterAssignment assignment, LocalJob local) =>
        local.Shared ||
        assignment.HostId == local.HostId && assignment.Off == local.Off ||
        local.Here && assignment.HostId is null && !assignment.Off;

    /// <summary>Whether this PC records what it does for a job nobody has recorded yet. Only a real choice is recorded (a host
    /// does it, or lip-sync is off): Martlet's default on a computer that just joined never overrides what your other computers
    /// chose, even when the plan reaches it a check later. A host a friend shares with this PC is never recorded: your other
    /// computers can't use it.</summary>
    internal static bool Seeds(LocalJob local) => !local.Shared && (local.HostId is not null || local.Off);

    /// <summary>Whether what this PC does for a job may go into your shared plan: its own choice (no host) or a host of yours
    /// (<paramref name="own"/>: the hosts paired here that no friend shares, and this PC's own host service). A host a friend
    /// shares, or one this PC no longer has (a forgotten shared host a job still names), never goes in.</summary>
    internal static bool Recordable(LocalJob local, IReadOnlyCollection<string> own) =>
        !local.Shared && (local.HostId is null || own.Contains(local.HostId));

    /// <summary>Whether this PC takes <paramref name="job"/> on for every computer, through its own host service: the owner set
    /// the job up on this PC to run here (the shared route, chosen on <paramref name="device"/>, is the one this PC uses and runs
    /// on the computer itself, like Ollama), and the plan leaves it to each computer's own choice. Then the computer it was set
    /// up on does it, whether it is a companion or a host PC, rather than every other computer needing its own copy. A plan
    /// entry newer than this PC's last look at the shared settings waits a check, so a change made on another computer
    /// (choosing a cloud provider, say) is seen first.</summary>
    internal static bool Claims(string job, ClusterAssignment? current, LocalJob local, SharedSetting? shared, SetupRoute? mine,
        string device, DateTimeOffset? settingsCheckedAt)
    {
        if (job != ClusterJobs.Thinking || !local.Here || local.HostId is null || mine is null) return false;
        if (current is not null && (current.HostId is not null || current.Off)) return false;
        if (shared is null || shared.UpdatedBy != device || settingsCheckedAt is not { } seen) return false;
        if (current is not null && current.UpdatedAt > seen - TimeSpan.FromMinutes(1)) return false;
        try { return SharedRoute.From(mine) is { } route && SharedRoute.Parse(shared.Value) == route; }
        catch (ContractException) { return false; }
    }

    /// <summary>Who does a job, in words: a host, nobody (lip-sync by voice loudness) or this PC's own choice.</summary>
    internal static string Who(string job, string? hostId, bool off) =>
        off ? "voice loudness" : hostId ?? (job == ClusterJobs.LipSync ? "this PC" : "the Setup choice");

    /// <summary>The host that takes over <paramref name="job"/> from <paramref name="failed"/>: one that answered this
    /// check and runs the job's engine, preferring hosts with the fewest other jobs, then the most GPU memory, then the
    /// host ID. Deterministic, so desktops that see the same hosts choose the same one. Null when none can.</summary>
    internal static string? FailoverTarget(ClusterPlan plan, string job, string failed, IEnumerable<ClusterProbe> probes,
        IEnumerable<HostHardware> hardware)
    {
        var memory = hardware.GroupBy(h => h.HostId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(h => h.BestGpu?.MemoryMb ?? 0), StringComparer.Ordinal);
        return probes.Where(p => p.HostId != failed && p.Serves(job))
            .OrderBy(p => plan.Assignments.Count(a => a.Job != job && ClusterJobs.All.Contains(a.Job) && a.HostId == p.HostId))
            .ThenByDescending(p => memory.GetValueOrDefault(p.HostId))
            .ThenBy(p => p.HostId, StringComparer.Ordinal)
            .Select(p => p.HostId)
            .FirstOrDefault();
    }

    /// <summary>The Martlet roles (with models) a host's routes show it runs, as the plan records them.</summary>
    internal static IReadOnlyList<ClusterNodeRole> Roles(IEnumerable<HostRoute> routes) => routes
        .Select(route => (Role: HostRoles.ForRoute(route.RouteId), route.ModelId))
        .Where(item => item.Role is not null && ClusterPlan.IsModelName(item.ModelId))
        .Select(item => new ClusterNodeRole { Kind = item.Role!.Kind, Model = item.ModelId })
        .DistinctBy(role => role.Kind)
        .OrderBy(role => role.Kind, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Whether sync is on: on by default, off only after the owner turned it off on this PC.</summary>
    internal static bool LoadEnabled(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, PreferenceFile)).Trim() != "off"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static void SaveEnabled(string directory, bool enabled)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PreferenceFile), enabled ? "on" : "off");
    }

    /// <summary>This PC's copy of the plan; an unreadable copy starts empty (the hosts' copies restore it).</summary>
    internal static ClusterPlan LoadPlan(string directory)
    {
        try { return ClusterPlan.Parse(File.ReadAllBytes(Path.Combine(directory, PlanFile))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return ClusterPlan.Empty; }
    }

    internal static void SavePlan(string directory, ClusterPlan plan)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PlanFile);
        var temporary = Path.Combine(directory, $"cluster.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, plan.Write());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Reads a paired host's routes and its copy of the plan over one pinned connection. Never throws for an
    /// unreachable host; the probe says why.</summary>
    internal static async Task<ClusterProbe> ProbeAsync(AvatarRemoteHost host, CancellationToken token)
    {
        try
        {
            return await WithConnectionAsync(host, async connection =>
            {
                var routes = await connection.ReadRoutesAsync(token);
                try { return new ClusterProbe(host.HostId, true, "", routes, await connection.ReadClusterAsync(token), true); }
                catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "response.invalid")
                {
                    return new ClusterProbe(host.HostId, true, "", routes);
                }
                // It answered with its routes: a late or failed read of its plan copy doesn't make it a host that stopped
                // answering. Its copy is read again on the next check.
                catch (Exception error) when (error is OperationCanceledException && !token.IsCancellationRequested || IsHostFailure(error))
                {
                    return new ClusterProbe(host.HostId, true, "", routes, null, true);
                }
            });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(host.HostId, false, "Didn't respond in time."); }
        catch (Exception error) when (IsHostFailure(error)) { return new(host.HostId, false, error.Message); }
    }

    /// <summary>Merges this PC's plan into a host's copy and returns the host's merged copy, or null when that failed.</summary>
    internal static async Task<ClusterPlan?> MergeAsync(AvatarRemoteHost host, ClusterPlan plan, CancellationToken token)
    {
        try { return await WithConnectionAsync(host, connection => connection.MergeClusterAsync(plan, token)); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (IsHostFailure(error)) { return null; }
    }

    internal static async Task<T> WithConnectionAsync<T>(AvatarRemoteHost host, Func<Audio2FaceHostConnection, Task<T>> action)
    {
        var connection = Connect(host);
        try { return await action(connection); }
        finally { connection.Dispose(); }
    }

    /// <summary>A paired connection to <paramref name="host"/> for several requests in a row; the caller disposes it.</summary>
    internal static Audio2FaceHostConnection Connect(AvatarRemoteHost host)
    {
        Audio2FaceHostConnection? connection = null;
        using (var read = new WindowsCredentialStore().ReadAvatarHostSecret(host.HostId, host.CredentialId))
        {
            if (read.Error != CredentialError.None || read.Secret is null)
                throw new InvalidOperationException("Pair this host again.");
            read.Secret.Use(secret => connection = new Audio2FaceHostConnection(GatewayAvatarHostLink.Pairing(host), secret));
        }
        return connection!;
    }

    internal static bool IsHostFailure(Exception error) => error is Audio2FaceHostException or IOException or UnauthorizedAccessException or
        ContractException or InvalidOperationException or ArgumentException or JsonException or TimeoutException or HttpRequestException;
}
