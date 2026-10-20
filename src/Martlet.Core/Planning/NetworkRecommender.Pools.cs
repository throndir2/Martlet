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
            var kind = job == ClusterJobs.Speaking ? voice : ListeningRole;
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

        private static bool LiveOn(Node node, int card) => node.Roles.Any(r => r.On(card) && r.Kind != DeepThinkingRole);

        /// <summary>Needed jobs before optional extras (<see cref="ComponentRanking"/>): today's singing, pictures, motion and Reading
        /// roles stay where the jobs Martlet needs to talk (Thinking, the voice, listening, lip-sync) and their pools left room, and go
        /// where they didn't, or when the owner turned their part off. Reading is this PC's choice, so its role goes only while no
        /// other companion PC may use it.</summary>
        private void Extras()
        {
            var otherCompanions = Math.Max(request.CompanionPcs, nodes.Count(n => n.Companion)) > 1;
            var hostAnswers = nodes.Any(n => !n.Companion && n.Presence == Presence.Here);
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here))
                foreach (var role in node.Pending.Where(r => Extra(r.Kind) && r.Leave is null).ToList())
                    if (KindComponent(role.Kind) is { } component && IsOff(component) && !(ComponentRanking.SetOnPage(component) && otherCompanions))
                        role.Leave = (SetupChangeBenefit.Improvement, ComponentRanking.SetOnPage(component)
                            ? $"{ComponentRanking.Name(component)} is off in {ComponentRanking.Page(component)}, and no other companion PC uses it."
                            : $"You turned {ComponentRanking.Name(component).ToLowerInvariant()} off.");
                    // Rule 6: with a host that answers, a companion PC runs no models (Reading is this PC's choice on its page).
                    else if (node.Companion && hostAnswers && role.Kind is SingingRole or PicturesRole or MotionRole)
                        role.Leave = (SetupChangeBenefit.Improvement, $"A host answers, so {node.Name}, a companion PC, runs no models and stays light for games.");
                    else if (!Keep(node, role, Kept, out _))
                        role.Leave = (SetupChangeBenefit.Required,
                            $"{Label(role.Option, role.Kind)} is optional: {CardText(node, role.Card)} has no room left for it beside " +
                            "the jobs Martlet needs to talk, which come first.");
        }

        private bool VoiceOnWindowsCard(Node node, int card) => node.OnWindows && node.Roles.Any(r => r.On(card) && IsVoice(r.Kind));

        /// <summary>People (rank 9, docs/RECOMMENDED_SETUPS.md#people-in-the-recommended-setup): while this PC's People choice is on
        /// and a host answers, a host recognizes voices with Martlet's Voice recognition role and goes first in Companion › People's
        /// Voice recognition list, so the companion PCs stay light for games; each companion PC's processor stays the fallback.
        /// Today's role stays where it runs (the recommender never moves or removes it), and a host that runs it but isn't first
        /// in the list goes first. Else the role goes where it has the most room: a host's NVIDIA graphics card (the CUDA build)
        /// when one has room beside the jobs that come first, else a host's processor. Nothing changes for one companion PC
        /// alone, a request without this PC's choice, or a network whose hosts have no room.</summary>
        private void People()
        {
            const PlanComponent part = PlanComponent.People;
            if (!Wants(part) || singlePc || Choice(part) is not { On: true } choice) return;
            bool Runs(Node node) => node.Presence == Presence.Here && !node.Companion && node.Roles.Any(r => r.Kind == VoiceRecognitionRole);
            if (NodeOf(choice.HostId) is { } listed && Runs(listed)) return;
            if (nodes.Where(Runs).OrderBy(n => n.Id, StringComparer.Ordinal).FirstOrDefault() is { } running)
            {
                peopleFirst = (running.Id, $"{running.Name} already runs it, so Reconfigure puts {running.Name} first in {ComponentRanking.Page(part)}'s " +
                    "Voice recognition list.", true);
                return;
            }
            var options = catalog.For(part).Where(o => o.IsLocal && o.HostRoleKind == VoiceRecognitionRole)
                .OrderByDescending(o => o.UsesGpu).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
            if (FindSlot(options, new Query(VoiceRecognitionRole) { Optional = true, StrictWindows = true, LeastLoaded = true }) is not { } slot) return;
            var why = $"{CardText(slot)} recognizes who is talking" + (slot.Card is null ? "" : ", faster than a processor") +
                $", so {OwnPcs()} stays light for games.";
            Place(slot, VoiceRecognitionRole, PeoplePlace, SetupChangeBenefit.Improvement, why);
            var join = slot.Node.Id != choice.HostId;
            peopleFirst = (slot.Node.Id, join
                ? $"Reconfigure installs it and puts {slot.Node.Name} first in {ComponentRanking.Page(part)}'s Voice recognition list."
                : $"Reconfigure installs it on {slot.Node.Name}, which is first in {ComponentRanking.Page(part)}'s Voice recognition list already.", join);
        }

        /// <summary>Rules 1, 5 and 9: the Thinking pool. Today's Deep thinking roles stay on hosts, moved to a card no live job
        /// uses when there is one; then every host the owner didn't leave out of the pool gets one on a card free for it, with
        /// the biggest model that fits. A companion PC keeps its own only while no host thinks in the background.</summary>
        private void DeepThinking()
        {
            if (!Wants(PlanComponent.DeepThinking)) return;
            if (IsOff(PlanComponent.DeepThinking))
            {
                foreach (var node in nodes.Where(n => n.Presence == Presence.Here))
                    foreach (var role in node.Pending.Where(r => r.Kind == DeepThinkingRole))
                        role.Leave = (SetupChangeBenefit.Improvement, "You turned Deep thinking off.");
                return;
            }
            // A smarter model the owner's own app already serves on a host takes that host's Thinking pool place (#1053).
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here)) AppDeep(node);
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here && (!n.Companion || singlePc)))
                foreach (var role in node.Pending.Where(r => r.Kind == DeepThinkingRole && r.Leave is null).ToList())
                    if (!Bigger(node, role)) KeepDeep(node, role);
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here && n.CanHost && (!n.Companion || singlePc) &&
                n.Machine.Manageable && !optOut.Contains(n.Id) && !n.Roles.Any(r => r.Kind == DeepThinkingRole)))
            {
                if (DeepSlot(node) is not { } slot) continue;
                var card = slot.Span is { Length: > 1 } ? $"split across cards no live job uses (no single card has room for it)"
                    : slot.Card is { } c && LiveOn(node, c) ? "a card beside the live jobs, which come first" : "a card that no live job uses";
                Place(slot, DeepThinkingRole, ThinkingPoolPlace, SetupChangeBenefit.Improvement,
                    $"{Plain(slot.Option)} on {SplitText(slot)}, {card}: more background thinking, and {node.Name} joins the Thinking pool by itself.");
            }
            // Rule 6: with a host that answers, a companion PC runs no models, so its own Deep thinking goes.
            var hostAnswers = nodes.Any(n => !n.Companion && n.Presence == Presence.Here);
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here && n.Companion && !singlePc))
                foreach (var role in node.Pending.Where(r => r.Kind == DeepThinkingRole && r.Leave is null).ToList())
                {
                    if (hostAnswers) role.Leave ??= (SetupChangeBenefit.Improvement, $"A host answers, so {node.Name}, a companion PC, runs no models and stays light for games.");
                    else KeepDeep(node, role);
                }
        }

        /// <summary>Bigger models for big hardware (#663): today's Deep thinking role on a host Martlet can change switches to a
        /// bigger model at least one quality step smarter when a card there (or several, split) has room for it
        /// (<see cref="DeepSlot"/>, as if today's model were gone). Deep thinking runs in the background, so a bigger model adds no
        /// conversation latency. Once switched, no smarter model fits, so the setup stays stable.</summary>
        private bool Bigger(Node node, Role role)
        {
            if (node.Companion || !node.Machine.Manageable || optOut.Contains(node.Id) || role.Option is not { } now) return false;
            if (DeepSlot(node) is not { } slot || slot.Option.QualityTier < now.QualityTier + 1) return false;
            // Bigger models for big hardware: never a smaller model in place of a big one the owner runs.
            if (now.UsesGpu && slot.Option.GpuGb <= now.GpuGb) return false;
            var card = slot.Span is { Length: > 1 } ? "cards no live job uses, split across them because no single card has room for it,"
                : slot.Card is { } c && LiveOn(node, c) ? "a card beside the live jobs, which come first," : "a card that no live job uses,";
            Place(slot, DeepThinkingRole, ThinkingPoolPlace, SetupChangeBenefit.Improvement,
                $"{SplitText(slot)}, {card} has room for {Plain(slot.Option)}, which thinks better than {Plain(now)}. Deep " +
                "thinking runs in the background, so replies come as soon as before.");
            return true;
        }

        /// <summary>Use models your apps already run, on a host (#1053): when a model app on host <paramref name="node"/> serves a chat
        /// model smarter than the Thinking pool model Martlet would run there (today's, else the smartest that fits a card), the
        /// deep-thinking-app role relays that host's Thinking pool to it. Nothing downloads, and the app keeps the model's memory.
        /// Deep thinking runs in the background, so replies come as soon as before. A place relayed to an app already stays.</summary>
        private void AppDeep(Node node)
        {
            if (node.Companion || !node.CanHost || !node.Machine.Manageable || optOut.Contains(node.Id) ||
                node.Roles.Any(r => r.Kind == DeepThinkingRole)) return;
            var best = (request.ServedModels ?? []).Where(m => m is { ModelId.Length: > 0 } && m.MachineId == node.Id && ServedModels.Chats(m.ModelId))
                .Select(m => (Model: m, Option: AppOption(m)))
                .OrderByDescending(x => x.Option.QualityTier).ThenByDescending(x => ServedModels.Gb(x.Model, catalog.Models) ?? 0)
                .ThenBy(x => x.Model.ModelId, StringComparer.Ordinal).FirstOrDefault();
            if (best.Option is null || !Uri.TryCreate(best.Model.BaseUrl, UriKind.Absolute, out var url)) return;
            var today = node.Pending.FirstOrDefault(r => r.Kind == DeepThinkingRole && !r.Native && r.Leave is null)?.Option;
            var own = DeepSlot(node)?.Option;
            var floor = new[] { today, own }.OfType<ComponentOption>().MaxBy(o => o.QualityTier);
            if (floor is not null && best.Option.QualityTier <= floor.QualityTier) return;
            var why = $"{best.Model.AppName} on {node.Name} already serves {best.Model.ModelId}, which thinks better than " +
                (floor is null ? "any model that fits there" : Plain(floor)) + ", so the Thinking pool uses it there: nothing downloads, and " +
                "your app keeps the model in its own memory. Deep thinking runs in the background, so replies come as soon as before.";
            var role = Place(new Slot(node, null, best.Option, false), DeepThinkingRole, ThinkingPoolPlace, SetupChangeBenefit.Improvement, why);
            role.App = true;
            role.Choices = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["APP_PORT"] = url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["APP_PATH"] = url.AbsolutePath.TrimEnd('/')
            };
        }

        private void KeepDeep(Node node, Role role)
        {
            var purpose = optOut.Contains(node.Id) ? Kept : ThinkingPoolPlace;
            if (role.Option is not { UsesGpu: true } option || role.Card is not { } own)
            {
                Commit(node, role, role.Card, purpose);
                return;
            }
            if (role.Span is { Length: > 1 } span)
            {
                // A model split across several cards: it stays on them while each still has room for its share and no other model.
                if (span.All(c => SplitCardFree(node, c) && node.Free(c) + Epsilon >= SplitGb(option.GpuGb, span.Length)))
                {
                    Commit(node, role, own, purpose);
                    return;
                }
                if (SplitSlot(node, [option], new Query(DeepThinkingRole) { Optional = true, Only = node.Id, Companions = singlePc }, 0) is { Span: { } again })
                {
                    role.Span = again;
                    role.Benefit = SetupChangeBenefit.Improvement;
                    role.Why = "Its cards are needed for the live jobs, so it moves to other cards no live job uses.";
                    Commit(node, role, again[0], purpose);
                    return;
                }
                // No room to split it here any more: it is planned as a model on one card (and goes when none has room).
                role.Span = null;
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

        /// <summary>Deep thinking online (docs/RECOMMENDATION_DESIGN.md, "When to use NVIDIA Build"): when the owner's online
        /// services allow it and no host card runs a Deep thinking model at least one quality step smarter than live Thinking,
        /// the smartest free hosted model the model catalog names (NVIDIA Build's) is suggested for the Thinking pool. A few
        /// seconds don't matter in the background. Only a hosted option with a model counts (the catalog's; Martlet's own list
        /// names none), and none while the Thinking pool already has an online member. It is a suggestion: the owner adds it
        /// in Companion › Thinking pool, so it changes no setup.</summary>
        private ComponentOption? OnlineDeepThinking(NetworkSetup target)
        {
            const PlanComponent part = PlanComponent.DeepThinking;
            if (!Wants(part) || IsOff(part) || !HostingRules.Allows(request.Preference, part) || (request.OnlineThinkingPool ?? []).Count > 0) return null;
            var live = ThinkingOption(target) is { } thinking ? TierOf(thinking) : 0;
            var hosts = nodes.Where(n => n.Presence == Presence.Here)
                .SelectMany(n => n.Roles.Where(r => r.Kind == DeepThinkingRole && r.Option is not null)).Select(r => r.Option!.QualityTier)
                .DefaultIfEmpty(0).Max();
            if (hosts >= live + 1) return null;
            return catalog.For(part).Where(o => !o.IsLocal && o.ModelId is not null && o.FreeTier && o.QualityTier >= live + 1)
                .OrderByDescending(o => request.ConfiguredProviders.Contains(o.ProviderId ?? "", StringComparer.OrdinalIgnoreCase))
                .ThenByDescending(o => o.QualityTier).ThenBy(o => o.Id, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>The note for <see cref="OnlineDeepThinking"/>'s suggestion.</summary>
        private string OnlineDeepNote(ComponentOption deep) =>
            $"Deep thinking: {deep.DisplayName} is free and smarter than Thinking's model, and no host's graphics card has room for a " +
            "smarter one. Add it in Companion › Thinking pool" + (request.ConfiguredProviders.Contains(deep.ProviderId ?? "", StringComparer.OrdinalIgnoreCase)
                ? " with the key you saved." : " with a free key from its website.") +
            " What it thinks about goes to that service.";

        /// <summary>A card on <paramref name="node"/> for a new Deep thinking role: one no live job uses first, then the one
        /// with the most room, never the voice's Windows card or a card with a language model; the smartest model that fits, the
        /// fastest of those (a mixture-of-experts model writes as fast as its active parameters allow), then the smallest (a model
        /// the host keeps downloaded first when the owner prefers models their hosts already have). With the model catalog this
        /// includes 70B and larger models on cards with room for them.</summary>
        private Slot? DeepSlot(Node node)
        {
            var options = catalog.For(PlanComponent.DeepThinking).Where(o => o.IsLocal && o.UsesGpu && o.HostRoleKind == DeepThinkingRole)
                .OrderByDescending(o => request.PreferHostModels && KeptOnDisk(node, DeepThinkingRole, o))
                .ThenByDescending(o => o.QualityTier).ThenByDescending(o => o.WordsPerSecond ?? 0).ThenBy(o => o.GpuGb)
                .ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
            var query = new Query(DeepThinkingRole) { Optional = true, StrictWindows = true, Only = node.Id, Companions = singlePc };
            var cards = (node.Pinnable ? node.Nvidia : node.MainCard is { } main ? [main] : [])
                .OrderBy(c => LiveOn(node, c) ? 1 : 0).ThenByDescending(node.Free).ThenBy(c => c);
            Slot? single = null;
            foreach (var card in cards)
            {
                foreach (var option in options)
                    if (node.Cards(option).Contains(card) && Eligible(node, query) && Valid(node, card, option, query) &&
                        Key(node, card, option, query) is not null)
                    {
                        single = new(node, card, option, false);
                        break;
                    }
                if (single is not null) break;
            }
            // Tensor split: a smarter model than any single card has room for, split across cards no live job uses.
            return SplitSlot(node, options, query, single?.Option.QualityTier ?? 0) ?? single;
        }

        /// <summary>One model split across several NVIDIA cards (tensor split, #663) on <paramref name="node"/>: the first of
        /// <paramref name="options"/> smarter than <paramref name="floor"/> that the fewest cards (2 to <see cref="MaxSplitCards"/>)
        /// no live job or other model uses have room for, each taking its share (<see cref="SplitGb"/>). Ollama splits a model across
        /// the cards it sees (CUDA_VISIBLE_DEVICES lists them; martlet-host choice.gpu=GPU-a,GPU-b). Only for Deep thinking: the
        /// cards pass each token along, which a live reply's first word can't wait for.</summary>
        private Slot? SplitSlot(Node node, IEnumerable<ComponentOption> options, Query query, int floor)
        {
            if (!node.Pinnable || !Eligible(node, query) || node.Roles.Any(r => !r.Native && r.Kind == query.Kind)) return null;
            foreach (var option in options.Where(o => o.QualityTier > floor && o.UsesGpu && !o.NativeOnly && o.RunsOn(node.Spec.Platform)))
            {
                var free = node.Cards(option).Where(c => SplitCardFree(node, c) &&
                        !node.Pending.Any(r => r.On(c) && r.Kind != query.Kind && Reserved(node, r, query)))
                    .OrderByDescending(node.Free).ThenBy(c => c).ToList();
                // Room for one card's share is not room for the model: a model that fits one card isn't split.
                if (free.Any(c => node.Free(c) + Epsilon >= option.GpuGb)) continue;
                if (node.RamCapacity - node.RamUsed + Epsilon < option.Peak.RamGb) continue;
                if (node.DiskCapacity > 0 && !KeptOnDisk(node, query.Kind, option) && node.DiskCapacity - node.DiskUsed + Epsilon < option.Peak.DiskGb) continue;
                for (var count = 2; count <= Math.Min(MaxSplitCards, free.Count); count++)
                {
                    var cards = free.Take(count).ToList();
                    if (cards.All(c => node.Free(c) + Epsilon >= SplitGb(option.GpuGb, count)))
                    {
                        int[] span = [.. cards.Order()];
                        return new(node, span[0], option, false) { Span = span };
                    }
                }
            }
            return null;
        }

        /// <summary>A card a split model may take: no live job and no other language model on it, and not the voice's Windows card.</summary>
        private bool SplitCardFree(Node node, int card) =>
            !LiveOn(node, card) && !VoiceOnWindowsCard(node, card) && !node.Roles.Any(r => r.On(card) && r.IsModel);

        /// <summary>Today's roles no step kept: they go (rule 6 on companion PCs; Minor tidy-ups on hosts).</summary>
        private void Leftovers()
        {
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here))
                foreach (var role in node.Pending.Where(r => !r.Native))
                    role.Leave ??= role.Moved ?? (node.Companion && !singlePc
                        ? (SetupChangeBenefit.Improvement, $"Nothing needs it on {node.Name}, a companion PC, so games get its graphics card back.")
                        : !Useful(role)
                            ? (SetupChangeBenefit.Minor, $"Every computer speaks with {Label(EngineOptions().FirstOrDefault(), voice ?? "")}, so {Label(role.Option, role.Kind)} isn't used.")
                            : (SetupChangeBenefit.Minor, role.Gb > 0
                                ? $"Nothing uses it; removing it frees about {Gb(role.Gb)} GB on {CardText(node, role.Card)}."
                                : "Nothing uses it."));
        }
    }
}
