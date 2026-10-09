using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

public static partial class NetworkRecommender
{
    private sealed partial class Planner
    {
        private Decision Decide(string job, string? hostId, string? optionId, string why,
            SetupChangeBenefit benefit = SetupChangeBenefit.Minor, bool off = false)
        {
            var decision = new Decision { Job = job, HostId = hostId, OptionId = optionId, Why = why, Benefit = benefit, Off = off };
            decisions[job] = decision;
            return decision;
        }

        private static bool NotSlower(ComponentOption option, ComponentOption? now) =>
            now?.FirstWordMs is not { } today || option.FirstWordMs is { } ms && ms <= today;

        private static string Sooner(ComponentOption now, ComponentOption next) =>
            now.FirstWordMs is { } before && next.FirstWordMs is { } after && after < before
                ? $"sooner (about {Seconds(after)} instead of {Seconds(before)})"
                : "as soon";

        private string Gone(Node node, string job) => Away($"{node.Name} {Absence(node.Machine)}, so {Lower(job)} moves.");

        /// <summary>A sentence about a computer that stays away, remembered so each change can say which of its words they are.</summary>
        private string Away(string sentence)
        {
            awayWords.Add(sentence);
            return sentence;
        }

        /// <summary>Today's option first, then the same model's other variants (rule 8: what the owner chose).</summary>
        private IReadOnlyList<ComponentOption> SameModel(ComponentOption? now, string kind) => now is not { IsLocal: true }
            ? []
            : [now, .. catalog.Options.Where(o => o != now && o.IsLocal && o.HostRoleKind == kind && Same(o.ModelId, now.ModelId))
                .OrderBy(o => o.UsesGpu ? 0 : 1).ThenBy(o => o.FirstWordMs ?? int.MaxValue).ThenBy(o => o.Id, StringComparer.Ordinal)];

        /// <summary>A job that stays exactly as today: its part isn't wanted, or its computer has no report (S4 leaves a host
        /// without a hardware report out of the request). A computer that isn't answering is planned without (rule 10).</summary>
        private bool Settled(string job)
        {
            var today = TodayJob(job);
            if (!Wants(ComponentOf(job)))
            {
                Freeze(job, today, today is null ? "" : "As it is today.");
                return true;
            }
            if (today?.HostId is not { } id) return false;
            if (NodeOf(id) is not null) return false;
            Freeze(job, today, $"{id} does it today; Martlet has no report from {id}, so it stays as it is.");
            notes.Add($"{id} does {Lower(job)} today, but Martlet has no report from it, so it stays as it is.");
            return true;
        }

        private void Freeze(string job, JobPlan? today, string why)
        {
            var decision = Decide(job, today?.HostId, today?.OptionId, why, off: today?.Off ?? false);
            decision.Frozen = true;
            decision.Pool.AddRange((today?.Pool ?? []).Where(p => p is not null));
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here))
                foreach (var role in node.Pending.Where(r => MatchesJob(job, r.Kind) && (!r.Native || today?.HostId is null)).ToList())
                {
                    if (role.Card is { } card && role.Option is { UsesGpu: true } && Fits(node, card, role) != Problem.None)
                    {
                        role.Leave = (SetupChangeBenefit.Required, $"{CardText(node, card)} has no room left for it beside the more important jobs.");
                        continue;
                    }
                    Commit(node, role, role.Card, node.Id == today?.HostId || role.Native ? job : Kept);
                }
        }

        /// <summary>A live job whose host is here: keeps it there, moves it off a companion PC when a host can run the same
        /// model as fast (rule 6, rule 7), and apart from the voice on a Windows card (rule 3). Returns why it must move
        /// instead (Slower: its computer's card is too full, so a later first word is allowed).</summary>
        private (SetupChangeBenefit Benefit, string Why, bool Slower)? Settle(string job, string kind, ComponentOption? now, Node host,
            IReadOnlyList<ComponentOption> same)
        {
            var role = host.Pending.FirstOrDefault(r => !r.Native && r.Kind == kind);
            if (role is null) return (SetupChangeBenefit.Required, $"{host.Name} doesn't run {Lower(job)} any more.", true);
            now ??= role.Option;
            if (now is null)
            {
                Commit(host, role, role.Card, job);
                Decide(job, host.Id, TodayJob(job)?.OptionId, $"{host.Name} does it today.");
                return null;
            }
            if (host.Companion && !singlePc)
            {
                var slot = FindSlot(same, new Query(kind) { MaxMs = now.FirstWordMs, MaxContention = Contention(host, role) });
                if (slot is not null)
                {
                    var why = $"{host.Name} is a companion PC, so {CardText(slot)} takes {Lower(job)} off it with {Plain(slot.Option)}: " +
                        "games keep the companion's graphics card, and the first word comes as soon.";
                    Place(slot, kind, job, SetupChangeBenefit.Improvement, why);
                    role.Moved = (SetupChangeBenefit.Improvement, $"{slot.Node.Name} does {Lower(job)} now, so {host.Name} stays light for games.");
                    Decide(job, slot.Node.Id, slot.Option.Id, why, SetupChangeBenefit.Improvement);
                    return null;
                }
            }
            if (!Keep(host, role, job, out var problem))
            {
                var why = problem switch
                {
                    Problem.NoRoom => $"{CardText(host, role.Card)} has no room for {Label(now)} beside the more important jobs.",
                    Problem.SharesModel => $"One language model per graphics card: {CardText(host, role.Card)} runs another.",
                    _ => $"{host.Name} runs another voice engine."
                };
                var benefit = problem == Problem.NoRoom ? SetupChangeBenefit.Required : SetupChangeBenefit.Improvement;
                role.Leave = (benefit, why);
                return (benefit, why, problem == Problem.NoRoom);
            }
            Decide(job, host.Id, Known(role.Option)?.Id ?? TodayJob(job)?.OptionId, $"{Label(role.Option, kind)} on {CardText(host, role.Card)}{FirstWord(role.Option)}: it runs there today.");
            Isolate(job, role, host, now, same);
            return null;
        }

        /// <summary>Rule 3: on a Windows computer the voice gets a card of its own, and another live job leaves the voice's
        /// card, when a place as fast exists.</summary>
        private void Isolate(string job, Role role, Node node, ComponentOption now, IReadOnlyList<ComponentOption> same)
        {
            if (!node.OnWindows || role.Card is not { } card) return;
            var voice = IsVoice(role.Kind);
            var others = node.Roles.Where(r => r != role && r.Card == card)
                .Concat(node.Pending.Where(r => r.Card == card && Useful(r) && r.Kind != DeepThinkingRole)).ToList();
            if (voice ? others.Count == 0 : !others.Any(r => IsVoice(r.Kind))) return;
            var busy = Contention(node, role);
            Unplace(node, role);
            busy = Math.Max(busy, Others(node, card, new Query(role.Kind)).Count(r => !OnDemand(r.Kind)) + (node.Companion ? CompanionLoad : 0));
            var slot = FindSlot(same, new Query(role.Kind)
            {
                MaxMs = now.FirstWordMs, MaxContention = busy, StrictWindows = true, Prefer = node.Id,
                Only = node.Companion && !singlePc ? node.Id : null
            });
            if (slot is null)
            {
                Commit(node, role, card, job);
                return;
            }
            var why = voice
                ? $"{node.Name} runs on Windows, where a full graphics card quietly pages into main memory and the voice can start " +
                  $"seconds late: {CardText(slot)} gives {Plain(slot.Option)} a card of its own."
                : $"{node.Name} runs on Windows, so the voice keeps its graphics card to itself: {Plain(slot.Option)} moves to {CardText(slot)}.";
            // A role a step has just placed keeps its own benefit when that is stronger (a missing job is Required).
            var benefit = role.Was is null && role.Benefit < SetupChangeBenefit.Improvement ? role.Benefit : SetupChangeBenefit.Improvement;
            Place(slot, role.Kind, job, benefit, why);
            if (slot.Node != node) role.Leave = (benefit, why);
            Decide(job, slot.Node.Id, slot.Option.Id, why, benefit);
        }

        /// <summary>Rule 3 for what the steps placed new as well: on a Windows card with a voice (Speaking's, or a pool voice
        /// that runs there today), the other live jobs move to a card as fast elsewhere, then Speaking's voice does, so applying
        /// a recommendation never leads to another.</summary>
        private void Separate()
        {
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here && n.OnWindows))
                foreach (var card in Enumerable.Range(0, node.Spec.Gpus.Count))
                {
                    if (!node.Roles.Any(r => r.Card == card && IsVoice(r.Kind)) && !node.Pending.Any(r => r.Card == card && IsVoice(r.Kind) && Useful(r)))
                        continue;
                    foreach (var role in node.Roles.Where(r => r.Card == card && !IsVoice(r.Kind) && !r.Native && !r.Fixed && r.Option is not null &&
                        r.Purpose is ClusterJobs.Thinking or ClusterJobs.Listening or ClusterJobs.LipSync).ToList())
                        Isolate(role.Purpose, role, node, role.Option!, role.Purpose switch
                        {
                            ClusterJobs.Thinking => SameModel(role.Option, ThinkingRole),
                            ClusterJobs.Listening => SameModel(role.Option, ListeningRole),
                            _ => FaceOptions()
                        });
                    if (node.Roles.FirstOrDefault(r => r.Card == card && IsVoice(r.Kind) && !r.Fixed && r.Purpose == ClusterJobs.Speaking) is
                        { Option: { } option } voice)
                        Isolate(ClusterJobs.Speaking, voice, node, option, EngineOptions());
                }
        }

        // ---------- Thinking ----------

        private void Thinking()
        {
            const string job = ClusterJobs.Thinking;
            if (Settled(job)) return;
            var host = NodeOf(TodayJob(job)?.HostId);
            var now = TodayOption(job);
            if (ServedThinking(now)) return;
            if (now is { IsLocal: false })
            {
                // Rule 8: the provider the owner chose stays, unless they keep everything on their computers (and only as fast).
                if (request.Preference == HostingPreference.PreferLocal &&
                    (ThinkHere(now, SetupChangeBenefit.Improvement, "You keep everything on your computers.", now.FirstWordMs, strict: !singlePc) ||
                     ThinkHere(now, SetupChangeBenefit.Improvement, "You keep everything on your computers.", now.FirstWordMs, strict: false)))
                    return;
                Decide(job, null, now.Id, $"{now.DisplayName}, as you chose{FirstWord(now)}.");
                if (request.Preference == HostingPreference.PreferLocal)
                    notes.Add($"You keep everything on your computers, but none of them can run a Thinking model with a first word as soon as {now.DisplayName}'s, so Thinking stays with it.");
                return;
            }
            if (host is { Presence: Presence.Here })
            {
                if (Settle(job, ThinkingRole, now, host, SameModel(now, ThinkingRole)) is { } forced) ChooseThinking(forced, now);
                return;
            }
            if (host is { Presence: Presence.Gone })
            {
                ChooseThinking((SetupChangeBenefit.Required, Gone(host, job), true), now);
                return;
            }
            if (now is { IsLocal: true })
            {
                NativeThinking(now);
                return;
            }
            ChooseThinking((SetupChangeBenefit.Required, "Martlet can't reply without Thinking.", true), null);
        }

        /// <summary>Rule 8: a chat model the owner's own model app serves (<see cref="NetworkSetupRequest.ServedModels"/>).
        /// Thinking keeps one it already uses. Otherwise, with one companion PC and no host, Thinking uses the best served model that
        /// fits a graphics card there beside the jobs placed so far (the biggest first). It never replaces a hosted provider the
        /// owner chose or moves Thinking off a host; a note names the model instead.</summary>
        private bool ServedThinking(ComponentOption? now)
        {
            const string job = ClusterJobs.Thinking;
            if (ServedModels.IsServed(now))
            {
                foreach (var node in nodes.Where(n => n.Presence == Presence.Here))
                    foreach (var role in node.Pending.Where(r => r.Native && r.Kind == ThinkingRole).ToList()) Commit(node, role, role.Card, job);
                Decide(job, null, now!.Id, $"{Plain(now)} in {now.ServedBy} on {OwnPcs()}, as you chose.");
                return true;
            }
            var served = catalog.For(PlanComponent.Thinking).Where(o => o.ServedOn is not null).ToList();
            if (served.Count == 0) return false;
            var names = List([.. served.Take(3).Select(o => $"{Plain(o)} in {o.ServedBy}")]);
            if (now is { IsLocal: false })
            {
                notes.Add($"You also run {names}. Choose it in Companion › Thinking to think with it instead of {now.DisplayName}.");
                return false;
            }
            if (nodes.FirstOrDefault(n => n.Presence == Presence.Here && !n.Companion) is { } host)
            {
                notes.Add($"You also run {names}, but {host.Name} can think for you, so your companion PC stays light. Choose it in Companion › Thinking to think with it.");
                return false;
            }
            var here = nodes.Where(n => n.Presence == Presence.Here && n.Companion).ToList();
            if (here.Count != 1)
            {
                notes.Add($"You run {names}, but Martlet plans Thinking for all your companion PCs, so it keeps its own model.");
                return false;
            }
            // Thinking can come before the voice (PreferLocal): on a PC alone, keep room for the voice beside the served model.
            var voiceGb = singlePc && !decisions.ContainsKey(ClusterJobs.Speaking) && engine is { } kind
                ? catalog.Options.Where(o => o.IsLocal && o.UsesGpu && o.HostRoleKind == kind).Select(o => o.GpuGb).DefaultIfEmpty(0).Min() : 0;
            var order = served.Where(o => o.ServedOn == here[0].Id && o.UsesGpu && here[0].Cards(o).Any(c => here[0].Free(c) >= o.GpuGb + voiceGb))
                .OrderByDescending(o => o.QualityTier).ThenByDescending(o => o.GpuGb).ThenBy(o => o.Id, StringComparer.Ordinal);
            var slot = FindSlot(order, new Query(ThinkingRole) { Native = true, Companions = true, Only = here[0].Id });
            if (slot is null)
            {
                notes.Add($"You run {names}, but no graphics card on {here[0].Name} has room for it beside the voice, so Martlet keeps its own model.");
                return false;
            }
            var benefit = now is null ? SetupChangeBenefit.Required : SetupChangeBenefit.Improvement;
            var why = $"You already run {Plain(slot.Option)} in {slot.Option.ServedBy}, so Martlet thinks with it on {CardText(slot)}" +
                (now is null ? "." : $" instead of {Plain(now)}. Its first word may come later.");
            Place(slot, ThinkingRole, job, benefit, why);
            Decide(job, null, slot.Option.Id, why, benefit);
            return true;
        }

        /// <summary>Thinking in each companion PC's own Ollama today: a host takes it when it can run the same model as soon.</summary>
        private void NativeThinking(ComponentOption now)
        {
            const string job = ClusterJobs.Thinking;
            var natives = nodes.SelectMany(n => n.Pending.Where(r => r.Native).Select(r => (Node: n, Role: r))).ToList();
            if (!singlePc)
            {
                var busy = natives.Select(x => Contention(x.Node, x.Role)).DefaultIfEmpty(1).Min();
                var slot = FindSlot(SameModel(now, ThinkingRole), new Query(ThinkingRole) { MaxMs = now.FirstWordMs, MaxContention = busy });
                if (slot is not null)
                {
                    var why = $"{CardText(slot)} thinks for every companion PC with {Plain(slot.Option)}{FirstWord(slot.Option)}: " +
                        "companion PCs keep their graphics cards for games, and replies start as soon.";
                    Place(slot, ThinkingRole, job, SetupChangeBenefit.Improvement, why);
                    Decide(job, slot.Node.Id, slot.Option.Id, why, SetupChangeBenefit.Improvement);
                    return;
                }
            }
            if (natives.FirstOrDefault(x => x.Role.Card is { } card && x.Role.Option is { UsesGpu: true } && Fits(x.Node, card, x.Role) != Problem.None) is
                { Node: not null } full)
            {
                ChooseThinking((SetupChangeBenefit.Required,
                    $"{CardText(full.Node, full.Role.Card)} has no room for {Plain(now)} beside the more important jobs.", true), now);
                return;
            }
            foreach (var (node, role) in natives) Commit(node, role, role.Card, job);
            Decide(job, null, now.Id, $"{Plain(now)} in Ollama on {OwnPcs()}{FirstWord(now)}.");
        }

        /// <summary>A new place for Thinking (rule 7: the fastest model that hears, on an idle card of a host): a hosted
        /// provider first when the owner prefers hosted, then a host's card of its own, then a hosted provider rather than the
        /// voice's Windows card (rule 3), then any host card, the processor, and last the companion PC with the most free
        /// hardware (rule 6).</summary>
        private void ChooseThinking((SetupChangeBenefit Benefit, string Why, bool Slower) forced, ComponentOption? now)
        {
            const string job = ClusterJobs.Thinking;
            var most = forced.Slower || now is null ? null : now.FirstWordMs;
            var hosted = HostedThinking().FirstOrDefault(o => most is null || o.FirstWordMs is { } ms && ms <= most);
            if (request.Preference == HostingPreference.PreferHosted && hosted is not null && now is not { IsLocal: true })
            {
                DecideHosted(hosted, forced.Benefit, $"{forced.Why} {hosted.DisplayName}: you're happy with hosted endpoints, so your graphics cards go to the voice and face.");
                return;
            }
            if (ThinkHere(now, forced.Benefit, forced.Why, most, strict: !singlePc)) return;
            if (hosted is not null)
            {
                DecideHosted(hosted, forced.Benefit, $"{forced.Why} {hosted.DisplayName}: " + (singlePc
                    ? "no graphics card on this PC has room for a local model beside the other jobs."
                    : "no host has a graphics card of its own free for a local model."));
                return;
            }
            if (ThinkHere(now, forced.Benefit, forced.Why, most, strict: false)) return;
            if (ThinkHere(now, forced.Benefit, forced.Why, most, strict: false, gpu: false)) return;
            var slot = FindSlot(ThinkingOrder(now), new Query(ThinkingRole) { Hosts = false, Companions = true, MostRoom = true, MaxMs = most });
            if (slot is not null)
            {
                var why = $"{forced.Why} No host can run Thinking, so {CardText(slot)} runs {Plain(slot.Option)} for every companion PC{FirstWord(slot.Option)}.";
                Place(slot, ThinkingRole, job, forced.Benefit, why.TrimStart());
                Decide(job, slot.Node.Id, slot.Option.Id, why.TrimStart(), forced.Benefit);
                return;
            }
            var nobody = $"No computer has room for a Thinking model, and {NoHostedThinking()}.";
            Decide(job, null, null, $"{forced.Why} {nobody}".TrimStart(), forced.Benefit);
            cannotReply = $"{nobody} Martlet can't reply until one is set up.";
            notes.Add(cannotReply);
        }

        /// <summary>Why no hosted provider does Thinking: no saved key for one, the owner keeps everything on their computers,
        /// or none answers as soon as today's model.</summary>
        private string NoHostedThinking() =>
            !catalog.For(PlanComponent.Thinking).Any(o => !o.IsLocal && Configured(o)) ? "no free API key is saved"
            : request.Preference == HostingPreference.PreferLocal ? "you keep everything on your computers"
            : "no hosted provider answers as soon";

        private bool ThinkHere(ComponentOption? now, SetupChangeBenefit benefit, string reason, int? most, bool strict, bool gpu = true)
        {
            const string job = ClusterJobs.Thinking;
            // Thinking inside this PC needs a model the catalog knows (Martlet sets it up by its option); a model sized only from
            // its name can still move to a host role, which installs it by name.
            var order = ThinkingOrder(now).Where(o => o.UsesGpu == gpu && (!singlePc || Known(o) is not null));
            var slot = singlePc
                ? FindSlot(order, new Query(ThinkingRole) { Native = true, Companions = true, MaxMs = most })
                : FindSlot(order, new Query(ThinkingRole) { MaxMs = most, StrictWindows = strict });
            if (slot is null) return false;
            var where = slot.Native ? "this PC" : CardText(slot);
            var merit = gpu
                ? "private, and it keeps working without the internet" + (slot.Option.HearsAudio ? "; it hears your voice itself" : "")
                : "no graphics card has room, so it runs on the processor";
            var why = $"{reason} {Plain(slot.Option)} on {where}{FirstWord(slot.Option)}: {merit}.".TrimStart();
            Place(slot, ThinkingRole, job, benefit, why);
            Decide(job, slot.Native ? null : slot.Node.Id, slot.Option.Id, why, benefit);
            return true;
        }

        /// <summary>Today's model and its variants, then the fastest models that hear, on a card first.</summary>
        private IEnumerable<ComponentOption> ThinkingOrder(ComponentOption? now)
        {
            var same = SameModel(now, ThinkingRole);
            return same.Concat(catalog.For(PlanComponent.Thinking).Where(o => o.IsLocal && o.HostRoleKind == ThinkingRole && !same.Contains(o))
                .OrderBy(o => o.UsesGpu ? 0 : 1).ThenByDescending(o => o.HearsAudio).ThenBy(o => o.FirstWordMs ?? int.MaxValue)
                .ThenBy(o => o.Id, StringComparer.Ordinal));
        }

        private bool Configured(ComponentOption option) => option.ProviderId is { } provider &&
            (request.ConfiguredProviders ?? []).Contains(provider, StringComparer.OrdinalIgnoreCase);

        /// <summary>The hosted Thinking providers the owner allows (PlacementEngine's rules): configured ones, and free ones
        /// to sign up for, never when they keep everything local.</summary>
        private IEnumerable<ComponentOption> HostedThinking() => catalog.For(PlanComponent.Thinking)
            .Where(o => !o.IsLocal && request.Preference != HostingPreference.PreferLocal && (Configured(o) || o.FreeTier))
            .OrderByDescending(o => (Configured(o) ? 20 : 0) + (o.HearsAudio ? 10 : 0) + o.QualityTier * 10 - Penalty(o) * 2)
            .ThenBy(o => o.FirstWordMs ?? int.MaxValue).ThenBy(o => o.Id, StringComparer.Ordinal);

        private static int Penalty(ComponentOption option) => option.Reliability switch
        {
            OptionReliability.Low => 10,
            OptionReliability.Medium => 5,
            _ => 0
        };

        private void DecideHosted(ComponentOption option, SetupChangeBenefit benefit, string why)
        {
            Decide(ClusterJobs.Thinking, null, option.Id, why.TrimStart(), benefit);
            if (option.NeedsSignup && !Configured(option))
                notes.Add($"Sign up for {option.DisplayName} (free) and save its API key in Companion › Thinking.");
        }
    }
}
