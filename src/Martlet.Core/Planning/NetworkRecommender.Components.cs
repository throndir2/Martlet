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
                    PlanComponent.Vision or PlanComponent.Hearing => SenseStatus(target, info.Component),
                    PlanComponent.Reading => ReadingStatus(),
                    PlanComponent.DeepThinking => DeepThinkingStatus(target),
                    PlanComponent.SmartHome => SmartHomeStatus(),
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
                .Concat((request.OnlineThinkingPool ?? []).Select(m => $"{m}, online"))
                .ToList();
            var suggestion = onlineDeep is { } deep
                ? $" {deep.DisplayName} is smarter: add it in Companion › Thinking pool. What it thinks about goes to that service." : "";
            if (places.Count > 0) return (true, $"Thinking pool: {List(places)}", "It thinks things over in the background, never on the reply path." + suggestion);
            if (!Wants(part)) return (false, OffWhere(part), "Not planned.");
            var hosts = nodes.Any(n => n.Presence == Presence.Here && !n.Companion && n.CanHost);
            var card = nodes.Any(n => n.Presence == Presence.Here && n.Companion && n.Spec.Gpus.Count > 0);
            return (false, OffWhere(part), (hosts
                ? "No host has a graphics card with room for a Deep thinking model beside the jobs that come first."
                : card ? "It needs a host with a free graphics card: a companion PC's card is kept for Thinking and the voice."
                : "It needs a host with a graphics card.") + suggestion);
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

        /// <summary>The option that does Thinking in <paramref name="target"/>: the role its host runs, else the job's option.</summary>
        private ComponentOption? ThinkingOption(NetworkSetup target)
        {
            var plan = target.Job(ClusterJobs.Thinking);
            if (plan is null || plan.Off) return null;
            if (NodeOf(plan.HostId)?.Roles.FirstOrDefault(r => !r.Native && r.Kind == ThinkingRole) is { Option: { } running }) return running;
            return Find(plan.OptionId);
        }

        /// <summary>Vision (the image model) and Hearing (the audio model): this PC's choice on its page (sense-models.json). Thinking's
        /// own model by default, when it sees or hears; else a model of its own, which puts each picture or recording into words
        /// for Thinking. An image or audio model uses a companion PC's graphics card only as Thinking itself.</summary>
        private (bool On, string Where, string Why) SenseStatus(NetworkSetup target, PlanComponent part)
        {
            var vision = part == PlanComponent.Vision;
            var page = ComponentRanking.Page(part);
            var (kind, input, verb) = vision ? ("image", "picture", "sees") : ("audio", "recording", "hears");
            if (!Wants(part)) return (false, OffWhere(part), "Not planned.");
            var choice = Choice(part);
            if (choice is { On: false })
                return (false, OffWhere(part), vision
                    ? $"Vision is off on this PC. Turn it on in {page}."
                    : $"No model hears your voice on this PC: Thinking gets the transcript. Turn it on in {page}.");
            var own = Find(choice?.OptionId) is { UsesThinking: false } chosen ? chosen : null;
            if (own is null && choice?.Where is null && choice?.Model is null)
            {
                var thinking = ThinkingOption(target);
                bool? takes = thinking is null ? null : vision ? thinking.SeesImages : thinking.HearsAudio;
                if (takes == false)
                    return (false, OffWhere(part), $"{Plain(thinking!)} doesn't {(vision ? "see pictures" : "hear recordings")}. Choose an {kind} " +
                        $"model of its own in {page}, or a Thinking model that {verb}.");
                return (true, thinking is null ? "Thinking's own model" : $"{Plain(thinking)}, Thinking's own model",
                    $"Thinking takes the {input}s in its own request, so nothing more runs.");
            }
            var where = choice?.Where
                ?? (own is { IsLocal: false } ? $"{own.DisplayName}, online"
                    : $"{(own is null ? choice?.Model : Plain(own))} in Ollama on {(choice?.HostId is { } host ? NameOf(host) : "this PC")}");
            var why = $"An {kind} model of its own puts each {input} into words for Thinking, and a reply never waits for it. Change it in {page}.";
            if (own is { UsesGpu: true } && choice?.HostId is null)
                why += " A companion PC's graphics card is for Thinking and the voice: a model on a host, or online, keeps games smooth.";
            return (true, where, why);
        }

        /// <summary>Reading: this PC's choice on its page (reading.json): Windows OCR inside Martlet by default, or Martlet's
        /// Reading role on a computer: RapidOCR or PP-OCRv5 mobile on its processor, or PP-OCRv5 server on its graphics card.</summary>
        private (bool On, string Where, string Why) ReadingStatus()
        {
            const PlanComponent part = PlanComponent.Reading;
            var page = ComponentRanking.Page(part);
            if (!Wants(part)) return (false, OffWhere(part), "Not planned.");
            var choice = Choice(part);
            var runs = nodes.Where(n => n.Presence == Presence.Here && n.Roles.Any(r => r.Kind == ReadingRole)).Select(n => n.Name).ToList();
            if (choice is { On: false })
                return (false, OffWhere(part), $"Reading is off on this PC. Turn it on in {page}." +
                    (runs.Count > 0 ? $" Martlet's Reading role stays on {List(runs)} for your other companion PCs." : ""));
            var chosen = Find(choice?.OptionId);
            if (choice?.HostId is not null || chosen?.HostRoleKind == ReadingRole)
            {
                var node = choice?.HostId is { } hostId ? NodeOf(hostId)
                    : nodes.FirstOrDefault(n => n.Presence == Presence.Here && n.Roles.Any(r => r.Kind == ReadingRole));
                if (node is null)
                    return (true, choice?.Where ?? "Martlet's Reading role", "It reads on that computer's processor.");
                if (node.Presence == Presence.Gone)
                    return (false, OffWhere(part), $"Martlet's Reading role runs on {node.Name}, which isn't answering.");
                if (!node.Roles.Any(r => r.Kind == ReadingRole))
                    return (false, OffWhere(part), $"{node.Name} doesn't run Martlet's Reading role. Set it up in {page}.");
                var role = node.Roles.First(r => r.Kind == ReadingRole);
                if (role.Model?.StartsWith("ppocrv5", StringComparison.Ordinal) != true)
                    return (true, $"Martlet's Reading role (RapidOCR) on {node.Name}'s processor",
                        "It is often better with game fonts, and takes about a second for one screenshot.");
                return role.Model == "ppocrv5-server" || role.Card is not null
                    ? (true, $"Martlet's Reading role (PP-OCRv5) on {node.Name}'s graphics card",
                        "It is the most accurate reader, also with game fonts and small text on 4K screens.")
                    : (true, $"Martlet's Reading role (PP-OCRv5) on {node.Name}'s processor",
                        "It is the most accurate reader, also with game fonts and small text on 4K screens, and takes a few seconds for one screenshot.");
            }
            var inApp = chosen ?? catalog.For(part).FirstOrDefault(o => o.IsLocal && o.RunsInApp);
            return (true, $"{(inApp is null ? "Windows OCR" : Plain(inApp))} inside Martlet on this PC's processor",
                "It is fast and free, and nothing leaves this PC.");
        }

        /// <summary>Smart home: Home Assistant where it runs (the home-assistant role, which the recommender never moves or removes,
        /// or the owner's own hub) and whether this PC connects to it.</summary>
        private (bool On, string Where, string Why) SmartHomeStatus()
        {
            const PlanComponent part = PlanComponent.SmartHome;
            var page = ComponentRanking.Page(part);
            if (!Wants(part)) return (false, OffWhere(part), "Not planned.");
            var choice = Choice(part);
            var hubs = nodes.Where(n => n.Machine.Roles.Any(r => r?.Kind == SmartHomeRole))
                .Select(n => n.Presence == Presence.Gone ? $"{n.Name}, which isn't answering" : $"{n.Name}'s processor").ToList();
            var where = hubs.Count > 0 ? $"Home Assistant on {List(hubs)}" : null;
            if (choice is { On: false } || choice is null && where is null)
                return (false, OffWhere(part), where is null
                    ? $"It isn't set up. Set it up in {page}."
                    : $"Martlet isn't connected to Home Assistant on this PC. {where} still runs your home; connect in {page}.");
            if (where is not null)
                return (true, where, "Home Assistant runs your home all the time, and Martlet asks it to do things when you ask. Martlet never moves it.");
            return (true, choice?.Where ?? "Your own Home Assistant", "Martlet connects to the Home Assistant you already run.");
        }
    }
}
