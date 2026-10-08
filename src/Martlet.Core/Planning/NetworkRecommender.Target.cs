using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

public static partial class NetworkRecommender
{
    private sealed partial class Planner
    {
        private static int JobOrder(string? job) => job is null ? int.MaxValue
            : ClusterJobs.All.Contains(job) ? ClusterJobs.All.ToList().IndexOf(job) : ClusterJobs.All.Count;

        private NetworkSetup BuildCurrent()
        {
            var jobs = (request.CurrentJobs ?? []).Where(j => j?.Job is not null).GroupBy(j => j.Job, StringComparer.Ordinal)
                .Select(g => g.First()).OrderBy(j => JobOrder(j.Job)).ThenBy(j => j.Job, StringComparer.Ordinal).ToArray();
            var machines = nodes.Select(n => new MachinePlan(n.Id, n.Machine.Kind, n.Machine.Roles)
            {
                Why = AwayWhy(n),
                Usage = n.Presence == Presence.Gone ? null : Usage(n, n.Today, InApp(n, jobs))
            }).ToArray();
            var pool = request.CurrentThinkingPool is { Count: > 0 } chosen ? chosen.ToArray()
                : nodes.Where(n => !optOut.Contains(n.Id) && n.Machine.Roles.Any(r => r.Kind == DeepThinkingRole)).Select(n => n.Id).ToArray();
            return new(machines, jobs) { ThinkingPool = pool };
        }

        private static string AwayWhy(Node node) => node.Presence == Presence.Gone
            ? $"{(node.Machine.OfflineFor is { TotalMinutes: >= 1 } away ? $"Hasn't answered for {Minutes(away)}" : "Isn't answering")}, " +
              "so Martlet plans without it."
            : "";

        private NetworkSetup BuildTarget()
        {
            var jobs = ClusterJobs.All.Select(TargetJob).OfType<JobPlan>()
                .Concat((request.CurrentJobs ?? []).Where(j => j?.Job is not null && !ClusterJobs.All.Contains(j.Job))
                    .GroupBy(j => j.Job, StringComparer.Ordinal).Select(g => g.First()))
                .ToArray();
            var machines = nodes.Select(n => n.Presence == Presence.Here
                ? new MachinePlan(n.Id, n.Machine.Kind, Placements(n)) { Why = MachineWhy(n), Usage = Usage(n, n.Roles, InApp(n, jobs)) }
                : new MachinePlan(n.Id, n.Machine.Kind, n.Machine.Roles) { Why = AwayWhy(n) }).ToArray();
            var pool = nodes.Where(n => !optOut.Contains(n.Id) && n.Presence == Presence.Here &&
                    n.Roles.Any(r => r.Kind == DeepThinkingRole && (r.Purpose == ThinkingPoolPlace || r.Fixed)))
                .Select(n => n.Id).ToArray();
            return new(machines, jobs) { ThinkingPool = pool };
        }

        private JobPlan? TargetJob(string job)
        {
            var today = TodayJob(job);
            if (!decisions.TryGetValue(job, out var decision)) return today;
            if (today is null && decision is { HostId: null, Off: false, OptionId: null }) return null;
            return new JobPlan(job, decision.HostId, decision.Off, decision.OptionId) { Pool = decision.Pool.ToArray(), Why = decision.Why };
        }

        /// <summary>The roles a computer runs in the recommended setup; a role is pinned to its card (GpuIndex) only on a computer
        /// with two or more NVIDIA cards.</summary>
        private static IReadOnlyList<HostedRolePlacement> Placements(Node node) => node.Roles.Where(r => !r.Native)
            .OrderBy(r => r.Kind, StringComparer.Ordinal)
            .Select(r => r.Fixed && r.Was is not null ? r.Was
                : new HostedRolePlacement(r.Kind, r.Model, node.Pinnable && r.Option is { UsesGpu: true } ? r.Card : null))
            .ToArray();

        /// <summary>What runs inside Martlet on a companion PC: the character and the jobs no host does there.</summary>
        private IEnumerable<ComponentOption> InApp(Node node, IEnumerable<JobPlan> jobs)
        {
            if (!node.Companion) yield break;
            if (Wants(PlanComponent.Character) && catalog.For(PlanComponent.Character).FirstOrDefault(o => o.RunsInApp) is { } character)
                yield return character;
            foreach (var job in jobs.Where(j => j.HostId is null && ClusterJobs.All.Contains(j.Job)))
                if ((job.Off ? catalog.Find("loudness-lipsync") : Find(job.OptionId)) is { RunsInApp: true } option)
                    yield return option;
        }

        private static MachineUsage Usage(Node node, IEnumerable<Role> roles, IEnumerable<ComponentOption> inApp)
        {
            var items = new List<UsageItem>();
            foreach (var role in roles.Where(r => r.Option is { IsLocal: true }))
            {
                var option = role.Option!;
                var onCard = role.Card is not null || !option.UsesGpu;
                items.Add(new(option.Component, option.Id, role.Card, onCard ? option.Reserve : option.Reserve with { VramGb = 0 })
                {
                    Usual = onCard ? option.Usual : option.Usual with { VramGb = 0 }
                });
            }
            items.AddRange(inApp.Select(o => new UsageItem(o.Component, o.Id, null, o.Reserve) { Usual = o.Usual }));
            var gpus = node.Spec.Gpus.Select((g, i) => new GpuUsage(i, g.Name, g.Vendor, g.VramGb,
                new ResourceGauge(node.Capacity[i], Math.Round(items.Where(x => x.GpuIndex == i).Sum(x => x.Use.VramGb), 2)), g.UnifiedMemory)).ToArray();
            var ram = items.Sum(x => x.Use.RamGb + (x.GpuIndex is { } c && node.Spec.Gpus[c].UnifiedMemory ? x.Use.VramGb : 0));
            return new(node.Id, node.Name, node.Spec.Platform, node.Spec.IsPrimary, gpus,
                new(node.RamCapacity, Math.Round(ram, 2)), new(node.CpuCapacity, Math.Round(items.Sum(x => x.Use.CpuThreads), 2)),
                new(node.DiskCapacity, Math.Round(items.Sum(x => x.Use.DiskGb), 2)), items);
        }

        private string MachineWhy(Node node)
        {
            var parts = node.Roles.Where(r => r.Option is not null || !r.Fixed).Select(Describe).Distinct(StringComparer.Ordinal).ToList();
            if (node.Companion)
                return parts.Count == 0
                    ? "A companion PC: it runs only what must run inside Martlet, so games keep its graphics card."
                    : $"A companion PC that {List(parts)}" + (singlePc ? "." : ", because no host can.");
            return parts.Count == 0 ? "A host with nothing to do in this setup." : $"It {List(parts)}.";
        }

        private static string Describe(Role role)
        {
            var name = role.Option is null ? role.Kind : Plain(role.Option);
            return role.Purpose switch
            {
                ClusterJobs.Thinking => $"thinks for your replies with {name}",
                ClusterJobs.Speaking => $"speaks with {name}",
                ClusterJobs.Listening => $"hears with {name}",
                ClusterJobs.LipSync => "moves the character's face",
                SpeakingPool => "speaks for a companion PC when the voice computer is busy",
                ListeningPool => "hears for a companion PC when the listening computer is busy",
                ThinkingPoolPlace => $"takes Thinking pool jobs with {name}",
                _ => role.Kind == ThinkingRole ? $"keeps {name} ready for Thinking" : $"runs {name}"
            };
        }
    }
}
