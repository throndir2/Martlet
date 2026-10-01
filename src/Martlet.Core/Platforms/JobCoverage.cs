using Martlet.Core.Cluster;

namespace Martlet.Core.Platforms;

/// <summary>Who does a job for the device you talk to.</summary>
public enum JobDoer
{
    /// <summary>No route chosen yet (thinking, listening, speaking).</summary>
    NotChosen,
    /// <summary>A cloud or network provider chosen in Setup.</summary>
    Provider,
    /// <summary>Something on this device (Windows speech, its own Audio2Face service).</summary>
    ThisDevice,
    /// <summary>A paired Martlet host.</summary>
    Host,
    /// <summary>Nobody on purpose: lip-sync by the voice's loudness.</summary>
    Nobody
}

/// <summary>What Martlet last learned about the host doing a job.</summary>
public sealed record JobHostState
{
    public required string HostId { get; init; }
    /// <summary>False when the job's route still points at a host this device no longer has a pairing for.</summary>
    public bool Paired { get; init; } = true;
    /// <summary>Null: not checked since Martlet started (sync off and no Check connection).</summary>
    public bool? Reachable { get; init; }
    /// <summary>False when it answered but no longer advertises the job's route (role removed, model gone).</summary>
    public bool? Serves { get; init; }
    /// <summary>The engine the job needs there, for example "Ollama".</summary>
    public string Engine { get; init; } = "";
    public bool SyncOn { get; init; }
    public bool Failover { get; init; }
    /// <summary>The host failover would move the job to now, or null when no other paired host runs the engine.</summary>
    public string? FailoverTarget { get; init; }
    public bool ForegroundOnly { get; init; }
}

/// <summary>Everything the device you talk to knows about one job, gathered without contacting anything.</summary>
public sealed record JobSituation
{
    public required string Job { get; init; }
    public required JobDoer Doer { get; init; }
    /// <summary>Who does it, in words: "OpenRouter", "gpu-1", "Windows speech".</summary>
    public string DoerName { get; init; } = "";
    public bool Enabled { get; init; } = true;
    /// <summary>The destination choice was recorded for the current selection.</summary>
    public bool Reviewed { get; init; } = true;
    public bool KeyMissing { get; init; }
    /// <summary>This app can save the route but cannot use it yet; the reason.</summary>
    public string? NotConnected { get; init; }
    public JobHostState? Host { get; init; }
    /// <summary>The choice the job can go back to (the Setup route kept aside while a host does it), or null.</summary>
    public string? Fallback { get; init; }
    public bool FallbackIsCloud { get; init; }
}

/// <summary>How well a job is covered: ready; unknown (not checked); not chosen; limited (works in a reduced way);
/// unavailable (nobody can do it right now).</summary>
public enum CoverageState { Ready, Unknown, NotChosen, Limited, Unavailable }

/// <summary>A fix the app can offer next to a coverage problem.</summary>
public enum CoverageFix { UseFallback, CheckHost, OpenSetup, OpenDevices }

public sealed record JobCoverage(string Job, CoverageState State, string Problem, string Effect, IReadOnlyList<CoverageFix> Fixes,
    string? HostId = null, string? Fallback = null)
{
    public string Title => JobCoverageRules.Title(Job);
    public bool IsProblem => State is CoverageState.Limited or CoverageState.Unavailable;
}

/// <summary>Turns what the device knows about each job into a plain statement of what works, what does not, what that
/// means for you and how to fix it; and says what forgetting a host or removing a role would take away before you do
/// it. Contacts nothing; the caller supplies the last known state.</summary>
public static class JobCoverageRules
{
    public static string Title(string job) => job == ClusterJobs.LipSync ? "Lip-sync" : char.ToUpperInvariant(job[0]) + job[1..];

    /// <summary>What you lose while nobody can do the job.</summary>
    public static string Effect(string job) => job switch
    {
        ClusterJobs.Thinking => "Martlet can't reply until thinking works again.",
        ClusterJobs.Listening => "Martlet can't hear you; you can still type.",
        ClusterJobs.Speaking => "Replies appear as text only.",
        _ => "The character's mouth follows the voice's loudness instead."
    };

    /// <summary>What a provider receives when the job goes back to it.</summary>
    public static string Sent(string job) => job switch
    {
        ClusterJobs.Thinking => "your messages and recent conversation",
        ClusterJobs.Listening => "your recorded speech",
        ClusterJobs.Speaking => "reply text",
        _ => "nothing"
    };

    public static JobCoverage Evaluate(JobSituation job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var lipSync = job.Job == ClusterJobs.LipSync;
        var down = lipSync ? CoverageState.Limited : CoverageState.Unavailable;
        var effect = Effect(job.Job);
        JobCoverage Result(CoverageState state, string problem, params CoverageFix[] fixes) =>
            new(job.Job, state, problem, state is CoverageState.Limited or CoverageState.Unavailable ? effect : "", fixes,
                job.Host?.HostId, job.Fallback);
        CoverageFix[] fallback = job.Fallback is null ? [] : [CoverageFix.UseFallback];

        switch (job.Doer)
        {
            case JobDoer.NotChosen:
                return Result(CoverageState.NotChosen, "Not chosen yet.", CoverageFix.OpenSetup);
            case JobDoer.Nobody:
                return Result(CoverageState.Ready, "Nobody on purpose: the mouth follows the voice's loudness.");
        }
        if (!job.Enabled) return Result(down, $"{job.DoerName} is turned off in Setup.", CoverageFix.OpenSetup);
        if (job.KeyMissing) return Result(down, $"The {job.DoerName} key was removed.", CoverageFix.OpenSetup);
        if (!job.Reviewed) return Result(down, $"{job.DoerName} needs its details reviewed in Setup.", CoverageFix.OpenSetup);
        if (job.NotConnected is { } why) return Result(down, $"{job.DoerName}: {why}.", [.. fallback, CoverageFix.OpenSetup]);
        if (job.Doer != JobDoer.Host || job.Host is not { } host) return Result(CoverageState.Ready, "");

        if (!host.Paired)
            return Result(down, $"{host.HostId} is no longer paired with this device.", [.. fallback, CoverageFix.OpenDevices]);
        string Failover()
        {
            if (!host.Failover) return host.SyncOn ? " Failover is off for this job." : "";
            return host.FailoverTarget is { } target
                ? $" Failover moves it to {target}, which also runs {host.Engine}, within about 30 seconds."
                : $" Failover is on, but no other paired host runs {host.Engine} to take over.";
        }
        if (host.Reachable == false)
            return Result(down, $"{host.HostId} isn't answering" +
                (host.ForegroundOnly ? " (it hosts only while Martlet is open on its screen)." : ".") + Failover(),
                [CoverageFix.CheckHost, .. fallback, CoverageFix.OpenDevices]);
        if (host.Reachable == true && host.Serves == false)
            return Result(down, $"{host.HostId} answers but no longer runs {host.Engine}." + Failover(),
                [.. fallback, CoverageFix.OpenDevices]);
        if (host.Reachable is null)
            return Result(CoverageState.Unknown, $"{host.HostId} hasn't been checked since Martlet started.", CoverageFix.CheckHost);
        return Result(CoverageState.Ready, "");
    }

    /// <summary>One sentence for the top of the app when something is wrong, or null when every chosen job works.</summary>
    public static string? Headline(IReadOnlyList<JobCoverage> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        bool Down(string job) => jobs.Any(j => j.Job == job && j.State == CoverageState.Unavailable);
        if (Down(ClusterJobs.Thinking)) return "Martlet can't reply right now";
        if (Down(ClusterJobs.Listening) && Down(ClusterJobs.Speaking)) return "Martlet can't talk out loud right now";
        if (Down(ClusterJobs.Listening)) return "Martlet can't hear you right now";
        if (Down(ClusterJobs.Speaking)) return "Martlet can't speak right now";
        return jobs.Any(j => j.IsProblem) ? "Something is working in a reduced way" : null;
    }

    /// <summary>What forgetting <paramref name="hostId"/> does to each job it does: the job goes back to its kept-aside
    /// choice (which may send data to a cloud provider again), or nobody does it. Lip-sync goes back to this device.</summary>
    public static IReadOnlyList<string> ForgetImpact(IEnumerable<JobSituation> jobs, string hostId)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var lines = new List<string>();
        var sync = false;
        foreach (var job in jobs.Where(j => j.Doer == JobDoer.Host && j.Host?.HostId == hostId))
        {
            sync |= job.Host!.SyncOn;
            lines.Add(job.Job == ClusterJobs.LipSync
                ? "Lip-sync goes back to this device (its own Audio2Face service if it runs one, otherwise the voice's loudness)."
                : BackTo(job));
        }
        if (sync && lines.Count > 0)
            lines.Add("Your other computers that keep who does what in sync go back to their own choices for these jobs.");
        return lines;
    }

    /// <summary>What removing the role behind <paramref name="job"/> from <paramref name="hostId"/> does: failover to
    /// another host, back to the kept-aside choice, or nobody. Includes the shared plan when other computers use it.</summary>
    public static IReadOnlyList<string> RemoveRoleImpact(JobSituation job, string hostId, bool sharedPlanUsesHost)
    {
        ArgumentNullException.ThrowIfNull(job);
        var lines = new List<string>();
        var here = job.Doer == JobDoer.Host && job.Host?.HostId == hostId;
        if (here)
        {
            var host = job.Host!;
            if (host.Failover && host.FailoverTarget is { } target)
                lines.Add($"{Title(job.Job)} moves to {target}, which also runs {host.Engine}, within about 30 seconds.");
            else if (job.Job == ClusterJobs.LipSync)
                lines.Add("The character's mouth follows the voice's loudness until you hand lip-sync to another computer.");
            else lines.Add(BackTo(job));
        }
        if (sharedPlanUsesHost)
            lines.Add($"Your other computers that keep who does what in sync use {hostId} for {job.Job} too; " +
                (job.Host is { Failover: true, FailoverTarget: not null } ? "failover moves them as well." : "they lose it as well."));
        return lines;
    }

    /// <summary>Whether removing the role should first hand the job back to its kept-aside choice (no failover target).</summary>
    public static bool HandBackFirst(JobSituation job, string hostId) =>
        job.Doer == JobDoer.Host && job.Host?.HostId == hostId && job.Job != ClusterJobs.LipSync && job.Fallback is not null &&
        !(job.Host.Failover && job.Host.FailoverTarget is not null);

    private static string BackTo(JobSituation job) => job.Fallback is { } fallback
        ? $"{Title(job.Job)} goes back to your Setup choice, {fallback}" +
            (job.FallbackIsCloud ? $". {char.ToUpperInvariant(Sent(job.Job)[0])}{Sent(job.Job)[1..]} go there again, and requests may cost money." : ".")
        : $"{Title(job.Job)}: nobody will do it. {Effect(job.Job)} Choose another in Setup or on the Devices page.";
}
