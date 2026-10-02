using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>What this PC does for one job right now: a paired host, its own choice (<see cref="HostId"/> null: its Setup
/// route, or this PC for lip-sync) or nobody (<see cref="Off"/>, lip-sync only).</summary>
internal readonly record struct LocalJob(string? HostId, bool Off);

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
/// (cluster-sync.txt) next to the other local preferences.</summary>
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
        ClusterJobs.Speaking => HostRoles.F5,
        ClusterJobs.LipSync => HostRoles.Audio2Face,
        _ => throw new ArgumentOutOfRangeException(nameof(job), job, "Unknown cluster job.")
    };

    internal static string RouteId(string job) => HostRoles.Get(RoleKind(job)).RouteId;

    internal static string Title(string job) => job == ClusterJobs.LipSync ? "Lip-sync" : char.ToUpperInvariant(job[0]) + job[1..];

    internal static string Engine(string job) => job switch
    {
        ClusterJobs.Thinking => "Ollama",
        ClusterJobs.Listening => "whisper",
        ClusterJobs.Speaking => "F5",
        _ => "Audio2Face"
    };

    internal static LocalJob Local(string job, AppSettings? settings, AvatarProfile? avatar) => job switch
    {
        ClusterJobs.LipSync => NetworkMap.LipSync(avatar) switch
        {
            LipSyncHandler.Loudness => new(null, true),
            LipSyncHandler.Host => new(avatar!.RemoteHost!.HostId, false),
            _ => new(null, false)
        },
        ClusterJobs.Thinking => new(NetworkMap.JobHost(settings, SetupRole.Llm), false),
        ClusterJobs.Listening => new(NetworkMap.JobHost(settings, SetupRole.Stt), false),
        _ => new(NetworkMap.JobHost(settings, SetupRole.Tts), false)
    };

    internal static bool Matches(ClusterAssignment assignment, LocalJob local) =>
        assignment.HostId == local.HostId && assignment.Off == local.Off;

    /// <summary>Who does a job, in words: a host, nobody (lip-sync by voice loudness) or this PC's own choice.</summary>
    internal static string Who(string job, string? hostId, bool off) =>
        off ? "nobody (voice loudness)" : hostId ?? (job == ClusterJobs.LipSync ? "this PC" : "this PC's Setup choice");

    /// <summary>The host that takes over <paramref name="job"/> from <paramref name="failed"/>: one that answered this
    /// check and runs the job's engine, preferring hosts with the fewest other jobs, then the most GPU memory, then the
    /// host ID. Deterministic, so desktops that see the same hosts choose the same one. Null when none can.</summary>
    internal static string? FailoverTarget(ClusterPlan plan, string job, string failed, IEnumerable<ClusterProbe> probes,
        IEnumerable<HostHardware> hardware)
    {
        var memory = hardware.GroupBy(h => h.HostId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(h => h.BestGpu?.MemoryMb ?? 0), StringComparer.Ordinal);
        return probes.Where(p => p.HostId != failed && p.Serves(job))
            .OrderBy(p => plan.Assignments.Count(a => a.Job != job && a.HostId == p.HostId))
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

    internal static bool LoadEnabled(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, PreferenceFile)).Trim() == "on"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
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
            });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(host.HostId, false, "Did not answer in time."); }
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
        Audio2FaceHostConnection? connection = null;
        using (var read = new WindowsCredentialStore().ReadAvatarHostSecret(host.HostId, host.CredentialId))
        {
            if (read.Error != CredentialError.None || read.Secret is null)
                throw new InvalidOperationException("This PC's pairing secret is missing; pair again.");
            read.Secret.Use(secret => connection = new Audio2FaceHostConnection(GatewayAvatarHostLink.Pairing(host), secret));
        }
        try { return await action(connection!); }
        finally { connection!.Dispose(); }
    }

    internal static bool IsHostFailure(Exception error) => error is Audio2FaceHostException or IOException or UnauthorizedAccessException or
        ContractException or InvalidOperationException or ArgumentException or JsonException or TimeoutException or HttpRequestException;
}
