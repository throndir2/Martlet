using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

/// <summary>Why a suggestion is clearly better (docs/RECOMMENDATION_DESIGN.md, "Staying current"): Smarter, with its first word
/// still within the reply quality's target or no later than today's; Lighter, as smart with clearly less graphics memory.</summary>
public enum BetterChoice { Smarter, Lighter }

/// <summary>A clearly better choice for <see cref="Job"/> (a ClusterJobs name) than the one the recommendation keeps:
/// <see cref="OptionId"/> on <see cref="HostId"/> (null: each companion PC itself, or a hosted provider) instead of
/// <see cref="TodayOptionId"/> on <see cref="TodayHostId"/>. <see cref="Locked"/>: the owner keeps the job's choice, so Recommended
/// setup only shows it; <see cref="Applied"/>: the job is unlocked, so the recommendation already makes the change.
/// <see cref="Name"/> and <see cref="TodayName"/> are the choices in words ("Gemma 4 E4B on gpu-box").</summary>
public sealed record JobSuggestion(string Job, string? OptionId, string? HostId, BetterChoice Kind, string Why)
{
    public string? TodayOptionId { get; init; }
    public string? TodayHostId { get; init; }
    public bool Locked { get; init; }
    public bool Applied { get; init; }
    public string Name { get; init; } = "";
    public string TodayName { get; init; } = "";

    /// <summary>"Thinking: Gemma 4 E4B on gpu-box instead of Gemma 4 E2B on gpu-box: smarter, and its first word comes in about 0.21 s."</summary>
    public string Text => $"{NetworkRecommender.JobTitle(Job)}: {Name} instead of {TodayName}: {Why}";
}

public static partial class NetworkRecommender
{
    /// <summary>"Thinking", "Speaking", "Listening" or "Lip-sync".</summary>
    public static string JobTitle(string job) => Title(job);

    /// <summary>The recommendation with each job's suggestion (<see cref="NetworkRecommendation.Suggestions"/>): every job with a
    /// lock (<see cref="RecommendationPreferences.LockableJobs"/>) is planned once more as if it were new
    /// (<see cref="NetworkSetupRequest.Fresh"/>), and a choice that is clearly better than the one the recommendation keeps is a
    /// suggestion. A locked job keeps its choice; the jobs the owner unlocked (<see cref="NetworkSetupRequest.Unlocked"/>) get
    /// theirs in the recommendation, so Reconfigure makes them. Pure: the planner runs a few times, off the reply path.</summary>
    private static NetworkRecommendation Suggest(NetworkSetupRequest request, FootprintCatalog catalog)
    {
        var plain = new Planner(request with { Fresh = [] }, catalog).Run();
        var suggestions = new List<JobSuggestion>();
        foreach (var job in RecommendationPreferences.LockableJobs)
        {
            if (plain.Target.Job(job) is not { } kept) continue;
            var fresh = new Planner(request with { Fresh = [job] }, catalog).Run();
            if (fresh.CannotReply || fresh.Target.Job(job) is not { } next) continue;
            if (Better(job, kept, next, catalog, request) is not { } better) continue;
            suggestions.Add(new JobSuggestion(job, next.OptionId, next.HostId, better.Kind, better.Why)
            {
                TodayOptionId = kept.OptionId, TodayHostId = kept.HostId, Locked = !(request.Unlocked ?? []).Contains(job),
                Name = Choice(next, catalog, request), TodayName = Choice(kept, catalog, request)
            });
        }
        if (suggestions.Count == 0) return plain;
        var unlocked = suggestions.Where(s => !s.Locked).Select(s => s.Job).ToArray();
        var final = unlocked.Length == 0 ? plain : new Planner(request with { Fresh = unlocked }, catalog).Run();
        return final with
        {
            Suggestions = [.. suggestions.Select(s => s with { Applied = !s.Locked && final.Target.Job(s.Job) is { } made &&
                made.OptionId == s.OptionId && made.HostId == s.HostId })]
        };
    }

    /// <summary>Whether <paramref name="next"/> is clearly better than <paramref name="kept"/> for <paramref name="job"/>, and why
    /// in words; null when it isn't (another place for the same choice isn't a suggestion: the recommendation decides places).</summary>
    private static (BetterChoice Kind, string Why)? Better(string job, JobPlan kept, JobPlan next, FootprintCatalog catalog, NetworkSetupRequest request)
    {
        if (kept.OptionId == next.OptionId || catalog.Find(kept.OptionId ?? "") is not { } now || catalog.Find(next.OptionId ?? "") is not { } better)
            return null;
        var hearing = job == ClusterJobs.Thinking && request.PreferHearing;
        int Score(ComponentOption option) => option.QualityTier + (hearing && option.HearsAudio ? 1 : 0);
        int? target = job == ClusterJobs.Thinking && request.Quality is { } quality ? RecommendationPreferences.TargetMs(quality) : null;
        bool AsSoon()
        {
            if (better.FirstWordMs is not { } after) return now.FirstWordMs is null;
            if (now.FirstWordMs is not { } before) return target is null || after <= target;
            return after <= Math.Max(before, target ?? before);
        }
        if (!AsSoon()) return null;
        string When() => better.FirstWordMs is { } ms
            ? $", and its first word comes in about {Seconds(ms)}" + (now.FirstWordMs is { } before && before != ms ? $" (today about {Seconds(before)})" : "")
            : "";
        if (Score(better) > Score(now))
        {
            var word = job switch
            {
                ClusterJobs.Thinking => "smarter" + (hearing && better.HearsAudio && !now.HearsAudio ? " and it hears you" : ""),
                ClusterJobs.Listening => "it hears your words more accurately",
                ClusterJobs.LipSync => "the character's face moves with the words, not only the loudness",
                _ => "better"
            };
            return (BetterChoice.Smarter, $"{word}{When()}.");
        }
        if (better.IsLocal && now.IsLocal && better.UsesGpu && now.UsesGpu && Score(better) >= Score(now) &&
            better.GpuGb <= now.GpuGb * 0.85 && now.GpuGb - better.GpuGb >= 0.5)
            return (BetterChoice.Lighter, $"as smart with about {Gb(now.GpuGb - better.GpuGb)} GB less graphics memory{When()}.");
        return null;
    }

    /// <summary>A job's choice in words: "Gemma 4 E4B on gpu-box", "Gemma 4 E2B on each companion PC", "NVIDIA Build (free endpoint)".</summary>
    private static string Choice(JobPlan job, FootprintCatalog catalog, NetworkSetupRequest request)
    {
        var option = job.OptionId is { } id ? catalog.Find(id) : null;
        var name = job.Off ? "the voice's loudness" : option is null ? job.OptionId ?? "nothing" : Plain(option);
        if (job.HostId is { } host)
        {
            var named = (request.Machines ?? []).FirstOrDefault(m => m?.Specs?.Id == host)?.Specs.Name;
            return $"{name} on {(string.IsNullOrWhiteSpace(named) ? host : named)}";
        }
        return option is { IsLocal: true } && !job.Off ? $"{name} on each companion PC" : name;
    }

    private sealed partial class Planner
    {
        /// <summary>The planner plans <paramref name="job"/> as if it were new (<see cref="NetworkSetupRequest.Fresh"/>): its model
        /// runs on the owner's computers today and isn't one their own model app serves.</summary>
        private bool Fresh(string job) => (request.Fresh ?? []).Contains(job) && TodayOption(job) is { IsLocal: true, ServedBy: null };

        /// <summary>Lip-sync by the voice's loudness today, but planned as new (<see cref="NetworkSetupRequest.Fresh"/>): advanced
        /// lip-sync takes a host's card when one has room, unless the owner turned it off in Recommended setup.</summary>
        private bool FreshFace() => (request.Fresh ?? []).Contains(ClusterJobs.LipSync) && TodayJob(ClusterJobs.LipSync) is { Off: true } &&
            !IsOff(PlanComponent.LipSync);
    }
}
