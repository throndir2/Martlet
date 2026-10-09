using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

public static partial class NetworkRecommender
{
    private sealed partial class Planner
    {
        /// <summary>Every part of Martlet in the priority list's order (<see cref="ComponentRanking"/>), with where it runs in
        /// <paramref name="target"/> or that it is off, and why.</summary>
        private IReadOnlyList<ComponentStatus> Components(NetworkSetup target) =>
        [
            .. ComponentRanking.All.Select(info =>
            {
                var (on, where, why) = info.Component switch
                {
                    PlanComponent.Thinking => JobStatus(target, ClusterJobs.Thinking),
                    PlanComponent.Voice => JobStatus(target, ClusterJobs.Speaking),
                    PlanComponent.Listening => JobStatus(target, ClusterJobs.Listening),
                    PlanComponent.Character => Wants(PlanComponent.Character)
                        ? (true, $"Inside Martlet on {OwnPcs()}", "It draws on the screen you talk at and needs very little.")
                        : (false, "Not planned.", ""),
                    PlanComponent.LipSync => LipSyncStatus(target),
                    PlanComponent.DeepThinking => DeepThinkingStatus(target),
                    _ => ExtraStatus(info.Component)
                };
                return new ComponentStatus(info.Component, info.Rank, info.Necessity, on, where, WithoutAway(why)) { OwnerOff = IsOff(info.Component) };
            })
        ];

        /// <summary><paramref name="why"/> without the sentences about computers that aren't answering: the review says those once.</summary>
        private string WithoutAway(string why)
        {
            foreach (var sentence in awayWords.OrderByDescending(s => s.Length))
                why = why.Replace(sentence, "", StringComparison.Ordinal);
            return why.Replace("  ", " ", StringComparison.Ordinal).Trim();
        }

        private static string OffWhere(PlanComponent component) => $"Off: {ComponentRanking.OffMeans(component)}.";

        /// <summary>Where a job runs: a computer's card or processor, a hosted provider, Ollama or the app on the companion PCs.</summary>
        private (bool On, string Where, string Why) JobStatus(NetworkSetup target, string job)
        {
            if (target.Job(job) is not { } plan) return (false, "Not set up.", "");
            if (plan.HostId is { } id)
            {
                var node = NodeOf(id);
                var role = node?.Roles.FirstOrDefault(r => !r.Native && r.Purpose == job)
                    ?? node?.Roles.FirstOrDefault(r => !r.Native && MatchesJob(job, r.Kind));
                var option = role?.Option ?? Find(plan.OptionId);
                var name = option is null ? Title(job) : Plain(option);
                return node is null ? (true, $"{name} on {id}", plan.Why)
                    : node.Presence == Presence.Gone ? (true, $"{name} on {node.Name}, which isn't answering", plan.Why)
                    : (true, $"{name} on {CardText(node, role?.Card)}", plan.Why);
            }
            if (Find(plan.OptionId) is not { } chosen) return (false, "Nobody does it.", plan.Why);
            if (!chosen.IsLocal) return (true, $"{chosen.DisplayName}, online", plan.Why);
            if (chosen.RunsInApp) return (true, $"{Plain(chosen)} inside Martlet on {OwnPcs()}'s processor", plan.Why);
            // Thinking in Ollama on the companion PCs themselves: name the card when one companion PC does it.
            var natives = nodes.Where(n => n.Presence == Presence.Here)
                .SelectMany(n => n.Roles.Where(r => r.Native && MatchesJob(job, r.Kind)).Select(r => (Node: n, Role: r))).ToList();
            var where = natives.Count == 1 ? CardText(natives[0].Node, natives[0].Role.Card) : OwnPcs();
            return (true, $"{Plain(chosen)} in {chosen.ServedBy ?? "Ollama"} on {where}", plan.Why);
        }

        private (bool On, string Where, string Why) LipSyncStatus(NetworkSetup target)
        {
            const string job = ClusterJobs.LipSync;
            if (IsOff(PlanComponent.LipSync)) return (false, OffWhere(PlanComponent.LipSync), "You turned advanced lip-sync off.");
            if (target.Job(job) is not { } plan)
                return (false, OffWhere(PlanComponent.LipSync), Wants(PlanComponent.LipSync)
                    ? "No host has an NVIDIA graphics card with room for advanced lip-sync, and a companion PC's card is kept for Thinking and the voice."
                    : "Not planned.");
            if (plan.Off) return (false, OffWhere(PlanComponent.LipSync), plan.Why);
            if (plan.HostId is not null) return JobStatus(target, job);
            return (true, $"{OwnPcs(capital: true)} moves the character's face itself", plan.Why);
        }

        private (bool On, string Where, string Why) DeepThinkingStatus(NetworkSetup target)
        {
            const PlanComponent part = PlanComponent.DeepThinking;
            if (IsOff(part)) return (false, OffWhere(part), "You turned Deep thinking off.");
            var places = nodes.Where(n => n.Presence == Presence.Here && target.ThinkingPool.Contains(n.Id))
                .SelectMany(n => n.Roles.Where(r => r.Kind == DeepThinkingRole).Select(r => $"{Label(r.Option, r.Kind)} on {CardText(n, r.Card)}"))
                .ToList();
            if (places.Count > 0) return (true, $"Thinking pool: {List(places)}", "It thinks things over in the background, never on the reply path.");
            if (!Wants(part)) return (false, OffWhere(part), "Not planned.");
            var hosts = nodes.Any(n => n.Presence == Presence.Here && !n.Companion && n.CanHost);
            var card = nodes.Any(n => n.Presence == Presence.Here && n.Companion && n.Spec.Gpus.Count > 0);
            return (false, OffWhere(part), hosts
                ? "No host has a graphics card with room for a Deep thinking model beside the jobs that come first."
                : card ? "It needs a host with a free graphics card: a companion PC's card is kept for Thinking and the voice."
                : "It needs a host with a graphics card.");
        }

        /// <summary>Singing and pictures: where they run, or why they are off (you turned them off, no room left beside the jobs
        /// that come first, or never set up).</summary>
        private (bool On, string Where, string Why) ExtraStatus(PlanComponent part)
        {
            var kind = part == PlanComponent.Singing ? SingingRole : PicturesRole;
            if (IsOff(part)) return (false, OffWhere(part), $"You turned {ComponentRanking.Name(part).ToLowerInvariant()} off.");
            var places = nodes.Where(n => n.Presence == Presence.Here)
                .SelectMany(n => n.Roles.Where(r => r.Kind == kind).Select(r => CardText(n, r.Card))).ToList();
            if (places.Count > 0) return (true, $"On {List(places)}", "Only with the room the jobs that come first leave.");
            if (nodes.FirstOrDefault(n => n.Presence == Presence.Gone && n.Machine.Roles.Any(r => r.Kind == kind)) is { } away)
                return (false, OffWhere(part), $"It runs on {away.Name}, which isn't answering.");
            var leaving = nodes.SelectMany(n => n.Today).FirstOrDefault(r => r.Kind == kind && r.Leave is not null);
            return (false, OffWhere(part), leaving?.Leave?.Why ?? "It isn't set up on any of your computers.");
        }
    }
}
