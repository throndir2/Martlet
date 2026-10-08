using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

public static partial class NetworkRecommender
{
    private sealed partial class Planner
    {
        /// <summary>Rule 9: the Speaking or Listening pool (Devices › Sharing work). It keeps today's members and every host
        /// that runs the job's engine, then adds hosts with a card free, least loaded first, until there is one place for each
        /// companion PC. A companion PC stays in it only when the job itself runs on a companion PC (rule 6).</summary>
        private void Pool(string job)
        {
            poolsDone.Add(job);
            if (!decisions.TryGetValue(job, out var decision) || decision.Frozen) return;
            var kind = job == ClusterJobs.Speaking ? engine : ListeningRole;
            var primary = NodeOf(decision.HostId);
            if (kind is null || primary is null || !primary.Roles.Any(r => r.Kind == kind && r.Purpose == job)) return;
            var purpose = job == ClusterJobs.Speaking ? SpeakingPool : ListeningPool;
            var verb = job == ClusterJobs.Speaking ? "speaks" : "hears";
            var places = Math.Max(0, companions - 1);
            var members = new List<string>();
            var companionPcs = new List<(Node Node, Role Role)>();
            var candidates = (TodayJob(job)?.Pool ?? []).Where(p => p is not null)
                .Concat(nodes.Where(n => n.Pending.Any(r => r.Kind == kind && !r.Native && r.Leave is null)).OrderBy(n => n.Load).ThenBy(n => n.Id, StringComparer.Ordinal).Select(n => n.Id))
                .Distinct(StringComparer.Ordinal).ToList();
            foreach (var id in candidates.Where(id => id != decision.HostId))
            {
                var node = NodeOf(id);
                if (node is null)
                {
                    members.Add(id);
                    notes.Add($"{id} is in the {Title(job)} pool today, but Martlet has no report from it, so it stays as it is.");
                    continue;
                }
                if (node.Presence == Presence.Away)
                {
                    if (node.Roles.Any(r => r.Kind == kind)) members.Add(id);
                    continue;
                }
                if (node.Presence == Presence.Gone || node.Pending.FirstOrDefault(r => r.Kind == kind && !r.Native && r.Leave is null) is not { } role) continue;
                if (node.Companion && !singlePc)
                {
                    companionPcs.Add((node, role));
                    continue;
                }
                if (Keep(node, role, purpose, out var problem)) members.Add(id);
                else role.Leave ??= problem == Problem.NoRoom
                    ? (SetupChangeBenefit.Required, $"{CardText(node, role.Card)} has no room left for it beside the more important jobs.")
                    : (SetupChangeBenefit.Improvement, $"{node.Name} runs the network's voice engine for another job.");
            }
            var options = (job == ClusterJobs.Speaking ? EngineOptions() : ListeningOrder(Find(decision.OptionId))).Where(o => o.UsesGpu).ToArray();
            while (members.Count < places)
            {
                var slot = FindSlot(options, new Query(kind) { Optional = true, StrictWindows = true, LeastLoaded = true });
                if (slot is null) break;
                Place(slot, kind, purpose, SetupChangeBenefit.Improvement,
                    $"When {primary.Name} is busy, {slot.Node.Name} {verb} for another companion PC with {Plain(slot.Option)} instead of making it wait.");
                members.Add(slot.Node.Id);
            }
            foreach (var (node, role) in companionPcs)
            {
                if (members.Count < places && Keep(node, role, purpose, out _))
                {
                    members.Add(node.Id);
                    continue;
                }
                role.Leave ??= role.Moved ?? (SetupChangeBenefit.Improvement, $"Hosts share {Lower(job)} now, so {node.Name}, a companion PC, stays light for games.");
            }
            decision.Pool.AddRange(members);
        }

        /// <summary>Thinking models (the ollama role) on hosts that don't do Thinking: they stay where they obey the rules
        /// (they join the Thinking pool by themselves), and leave companion PCs (rule 6).</summary>
        private void SpareThinkingModels()
        {
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here))
                foreach (var role in node.Pending.Where(r => r.Kind == ThinkingRole && !r.Native && r.Leave is null).ToList())
                {
                    if (node.Companion && !singlePc)
                    {
                        role.Leave ??= (SetupChangeBenefit.Improvement, $"Thinking runs elsewhere, so {node.Name}, a companion PC, stays light for games.");
                        continue;
                    }
                    if (!Keep(node, role, Kept, out var problem))
                        role.Leave ??= problem == Problem.NoRoom
                            ? (SetupChangeBenefit.Required, $"{CardText(node, role.Card)} has no room left for it.")
                            : (SetupChangeBenefit.Improvement, "One language model per graphics card: its card runs Thinking's model.");
                }
        }

        private static bool LiveOn(Node node, int card) => node.Roles.Any(r => r.Card == card && r.Kind != DeepThinkingRole);

        private bool VoiceOnWindowsCard(Node node, int card) => node.OnWindows && node.Roles.Any(r => r.Card == card && IsVoice(r.Kind));

        /// <summary>Rules 1, 5 and 9: the Thinking pool. Today's Deep thinking roles stay on hosts, moved to a card no live job
        /// uses when there is one; then every host the owner didn't leave out of the pool gets one on a card free for it, with
        /// the biggest model that fits. A companion PC keeps its own only while no host thinks in the background.</summary>
        private void DeepThinking()
        {
            if (!Wants(PlanComponent.DeepThinking)) return;
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here && (!n.Companion || singlePc)))
                foreach (var role in node.Pending.Where(r => r.Kind == DeepThinkingRole && r.Leave is null).ToList())
                    KeepDeep(node, role);
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here && n.CanHost && (!n.Companion || singlePc) &&
                n.Machine.Manageable && !optOut.Contains(n.Id) && !n.Roles.Any(r => r.Kind == DeepThinkingRole)))
            {
                if (DeepSlot(node) is not { } slot) continue;
                var card = slot.Card is { } c && LiveOn(node, c) ? "beside the live jobs, which come first" : "that no live job uses";
                Place(slot, DeepThinkingRole, ThinkingPoolPlace, SetupChangeBenefit.Improvement,
                    $"{Plain(slot.Option)} on {CardText(slot)}, a card {card}: more background thinking, and {node.Name} joins the Thinking pool by itself.");
            }
            var hostsThink = nodes.Any(n => !n.Companion && n.Roles.Any(r => r.Kind == DeepThinkingRole && r.Purpose == ThinkingPoolPlace));
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here && n.Companion && !singlePc))
                foreach (var role in node.Pending.Where(r => r.Kind == DeepThinkingRole && r.Leave is null).ToList())
                {
                    if (hostsThink) role.Leave ??= (SetupChangeBenefit.Improvement, $"Hosts think in the background now, so {node.Name}, a companion PC, stays light for games.");
                    else KeepDeep(node, role);
                }
        }

        private void KeepDeep(Node node, Role role)
        {
            var purpose = optOut.Contains(node.Id) ? Kept : ThinkingPoolPlace;
            if (role.Option is not { UsesGpu: true } option || role.Card is not { } own)
            {
                Commit(node, role, role.Card, purpose);
                return;
            }
            var cards = (node.Pinnable ? node.Cards(option).ToList() : [own])
                .OrderBy(c => LiveOn(node, c) ? 1 : 0).ThenBy(c => c == own ? 0 : 1).ThenByDescending(node.Free).ThenBy(c => c);
            foreach (var card in cards)
            {
                if (VoiceOnWindowsCard(node, card) || Fits(node, card, role) != Problem.None) continue;
                if (card != own)
                {
                    var fault = Fits(node, own, role);
                    role.Benefit = fault == Problem.NoRoom ? SetupChangeBenefit.Required : SetupChangeBenefit.Improvement;
                    role.Why = fault == Problem.NoRoom ? $"{CardText(node, own)} has no room left for it."
                        : fault == Problem.SharesModel ? "One language model per graphics card: its card runs Thinking's model."
                        : VoiceOnWindowsCard(node, own) ? "The voice keeps its Windows graphics card to itself."
                        : "A card no live job uses: replies never wait for a think, and thinks don't stop for replies.";
                }
                Commit(node, role, card, purpose);
                return;
            }
            var problem = Fits(node, own, role);
            role.Leave ??= problem == Problem.NoRoom
                ? (SetupChangeBenefit.Required, $"{CardText(node, own)} has no room left for it beside the live jobs.")
                : problem == Problem.SharesModel
                    ? (SetupChangeBenefit.Improvement, $"One language model per graphics card: {CardText(node, own)} runs Thinking's model.")
                    : (SetupChangeBenefit.Improvement, $"{node.Name} runs on Windows, so the voice keeps its graphics card to itself.");
        }

        /// <summary>A card on <paramref name="node"/> for a new Deep thinking role: one no live job uses first, then the one
        /// with the most room, never the voice's Windows card or a card with a language model; the biggest model that fits.</summary>
        private Slot? DeepSlot(Node node)
        {
            var options = catalog.For(PlanComponent.DeepThinking).Where(o => o.IsLocal && o.UsesGpu && o.HostRoleKind == DeepThinkingRole)
                .OrderByDescending(o => o.QualityTier).ThenBy(o => o.GpuGb).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
            var query = new Query(DeepThinkingRole) { Optional = true, StrictWindows = true, Only = node.Id, Companions = singlePc };
            var cards = (node.Pinnable ? node.Nvidia : node.MainCard is { } main ? [main] : [])
                .OrderBy(c => LiveOn(node, c) ? 1 : 0).ThenByDescending(node.Free).ThenBy(c => c);
            foreach (var card in cards)
                foreach (var option in options)
                    if (node.Cards(option).Contains(card) && Eligible(node, query) && Valid(node, card, option, query) &&
                        Key(node, card, option, query) is not null)
                        return new(node, card, option, false);
            return null;
        }

        /// <summary>Today's roles no step kept: they go (rule 6 on companion PCs; Minor tidy-ups on hosts).</summary>
        private void Leftovers()
        {
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here))
                foreach (var role in node.Pending.Where(r => !r.Native))
                    role.Leave ??= role.Moved ?? (node.Companion && !singlePc
                        ? (SetupChangeBenefit.Improvement, $"Nothing needs it on {node.Name}, a companion PC, so games get its graphics card back.")
                        : !Useful(role)
                            ? (SetupChangeBenefit.Minor, $"Every computer speaks with {EngineName}, so {Label(role.Option, role.Kind)} isn't used.")
                            : (SetupChangeBenefit.Minor, role.Gb > 0
                                ? $"Nothing uses it; removing it frees about {Gb(role.Gb)} GB on {CardText(node, role.Card)}."
                                : "Nothing uses it."));
        }
    }
}
