using System.Text.RegularExpressions;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Core.Planning;

public static partial class NetworkRecommender
{
    private sealed partial class Planner
    {
        private readonly NetworkSetupRequest request;
        private readonly FootprintCatalog catalog;
        private readonly List<Node> nodes;
        private readonly HashSet<string> voiceKinds;
        private readonly HashSet<string> optOut;
        /// <summary>The voice engine every computer uses (a host role kind), or null when the owner speaks with a hosted voice.</summary>
        private readonly string? engine;
        /// <summary>The voice engine Speaking uses in the plan: <see cref="engine"/>, or Chatterbox Nano while no computer has
        /// room for it.</summary>
        private string? voice;
        /// <summary>One companion PC alone (computers that aren't answering don't count). Its card still takes only Thinking
        /// and the voice (the priority list's companion rule); it may run those itself without a host.</summary>
        private readonly bool singlePc;
        private readonly int companions;
        private readonly Dictionary<string, Decision> decisions = new(StringComparer.Ordinal);
        /// <summary>The jobs whose pool a step has decided.</summary>
        private readonly HashSet<string> poolsDone = new(StringComparer.Ordinal);
        private readonly List<string> notes = [];
        /// <summary>Every sentence a step wrote about a computer that stays away (SetupChange.Away).</summary>
        private readonly HashSet<string> awayWords = new(StringComparer.Ordinal);
        private readonly List<OfflineComputer> offline = [];
        /// <summary>The note that says Martlet can't reply: no computer and no hosted provider can do Thinking.</summary>
        private string? cannotReply;
        /// <summary>The note that says Martlet can't speak: no computer and no hosted voice can do Speaking.</summary>
        private string? cannotSpeak;

        public NetworkSetup Current { get; }

        public Planner(NetworkSetupRequest request, FootprintCatalog catalog)
        {
            this.request = request;
            this.catalog = catalog;
            voiceKinds = catalog.For(PlanComponent.Voice).Where(o => o.IsLocal && o.HostRoleKind is not null)
                .Select(o => o.HostRoleKind!).ToHashSet(StringComparer.Ordinal);
            optOut = new HashSet<string>(request.ThinkingPoolOptOut ?? [], StringComparer.Ordinal);
            nodes = (request.Machines ?? []).Where(m => m?.Specs?.Id is { Length: > 0 })
                .GroupBy(m => m.Specs.Id, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(m => m.Specs.Id, StringComparer.Ordinal).Select(Build).ToList();
            var present = nodes.Where(n => n.Presence != Presence.Gone).ToList();
            singlePc = present.Count == 1 && present[0].Companion;
            companions = nodes.Count(n => n.Companion && n.Presence != Presence.Gone);
            engine = Engine();
            voice = engine;
            foreach (var node in nodes) Resolve(node);
            Current = BuildCurrent();
        }

        public NetworkRecommendation Run()
        {
            foreach (var step in ComponentRanking.ClaimOrder(request.Preference))
            {
                switch (step)
                {
                    case PlanStep.Voice: Speaking(); break;
                    case PlanStep.Listening: Listening(); break;
                    case PlanStep.LipSync: LipSync(); break;
                    case PlanStep.ThinkingPrimary: Thinking(); break;
                    case PlanStep.ListeningUpgrade: ListeningUpgrade(); break;
                }
                if (step != PlanStep.Voice) RetryVoice();
            }
            NewLipSync();
            Separate();
            Pool(ClusterJobs.Speaking);
            Pool(ClusterJobs.Listening);
            SpareThinkingModels();
            Extras();
            DeepThinking();
            Leftovers();
            var target = BuildTarget();
            var changes = Diff(target).Select(MarkAway).ToList();
            AddNotes(target, changes);
            // Today's Thinking job kept exactly as it is (each companion PC itself, no option known) isn't a new problem.
            var stays = TodayJob(ClusterJobs.Thinking) is { HostId: null, OptionId: null, Off: false };
            return new(Current, target, changes)
            {
                Fingerprint = FingerprintOf(target), Notes = notes.Distinct(StringComparer.Ordinal).ToArray(), Offline = offline.ToArray(),
                CannotReplyNote = stays ? null : cannotReply, CannotSpeakNote = cannotSpeak, Components = Components(target)
            };
        }

        private SetupChange MarkAway(SetupChange change) =>
            awayWords.Where(w => change.Why.Contains(w, StringComparison.Ordinal)).OrderByDescending(w => w.Length).FirstOrDefault() is { } away
                ? change with { Away = away } : change;

        // ---------- Today's network ----------

        private Node Build(NetworkMachine machine)
        {
            var spec = machine.Specs;
            var presence = machine.Online ? Presence.Here : Presence.Gone;
            var all = Enumerable.Range(0, spec.Gpus.Count).ToArray();
            var nvidia = all.Where(i => spec.Gpus[i].IsNvidia).ToArray();
            int? main = all.Length == 0 ? null
                : (nvidia.Length > 0 ? nvidia : all).OrderByDescending(i => spec.Gpus[i].VramGb).ThenBy(i => i).First();
            return new Node
            {
                Machine = machine, Presence = presence,
                Capacity = spec.Gpus.Select(g => PlacementEngine.GpuCapacityGb(g, spec.KeepGpuForGames)).ToArray(),
                Pinnable = nvidia.Length >= 2, Nvidia = nvidia, MainCard = main,
                RamCapacity = PlacementEngine.RamCapacityGb(spec), CpuCapacity = PlacementEngine.CpuCapacity(spec),
                DiskCapacity = PlacementEngine.DiskCapacityGb(spec)
            };
        }

        /// <summary>Today's roles on <paramref name="node"/>, with their footprints and cards. A role that isn't pinned to a card
        /// on a computer with two or more NVIDIA cards is counted on the one with the most room.</summary>
        private void Resolve(Node node)
        {
            var presumed = new double[node.Spec.Gpus.Count];
            foreach (var placement in node.Machine.Roles.Where(r => r?.Kind is { Length: > 0 }).GroupBy(r => r.Kind, StringComparer.Ordinal)
                .Select(g => g.First()).OrderBy(r => r.Kind, StringComparer.Ordinal))
            {
                var option = OptionFor(placement.Kind, placement.Model, node);
                int? card = null;
                if (option is { UsesGpu: true })
                {
                    var usable = node.Cards(option).ToList();
                    card = placement.GpuIndex is { } index && usable.Contains(index) ? index
                        : usable.Count == 0 ? null
                        : usable.OrderByDescending(c => node.Capacity[c] - presumed[c]).ThenBy(c => c).First();
                    if (card is { } c) presumed[c] += option.GpuGb;
                }
                var role = new Role
                {
                    Kind = placement.Kind, Model = placement.Model, Option = option, Card = card, Was = placement,
                    Fixed = node.Presence != Presence.Here || !Managed(placement.Kind) && !Extra(placement.Kind)
                };
                node.Today.Add(role);
                if (node.Presence == Presence.Gone) continue;
                (role.Fixed ? node.Roles : node.Pending).Add(role);
            }
            if (node.Companion && node.Presence != Presence.Gone && TodayJob(ClusterJobs.Thinking) is { HostId: null } thinking &&
                Find(thinking.OptionId) is { IsLocal: true, Component: PlanComponent.Thinking } local &&
                (local.ServedOn is null || local.ServedOn == node.Id))
            {
                var role = new Role
                {
                    Kind = ThinkingRole, Model = local.ModelId, Option = local, Native = true,
                    Card = local.UsesGpu ? node.Cards(local).Select(c => (int?)c).FirstOrDefault() : null,
                    Fixed = node.Presence != Presence.Here || !Wants(PlanComponent.Thinking)
                };
                node.Today.Add(role);
                (role.Fixed ? node.Roles : node.Pending).Add(role);
            }
        }

        /// <summary>The catalog option a host role of <paramref name="kind"/> with <paramref name="model"/> runs on
        /// <paramref name="node"/>: its graphics card variant where the node has a card for it (a host role uses the card by
        /// default), else its processor variant. The stt role's Parakeet models count as the app's; a language model the
        /// catalog doesn't know is sized from its name ("qwen3-vl:8b"); another unknown model counts as the role's biggest.</summary>
        private ComponentOption? OptionFor(string kind, string? model, Node node)
        {
            var ofKind = catalog.Options.Where(o => o.IsLocal && o.HostRoleKind == kind).ToList();
            if (ofKind.Count == 0) return null;
            var named = model is null ? ofKind : ofKind.Where(o => Names(o, model)).ToList();
            if (named.Count == 0 && model is not null)
            {
                var inApp = catalog.For(ofKind[0].Component).FirstOrDefault(o => o.RunsInApp && o.ModelId is { } id &&
                    model.StartsWith(id, StringComparison.OrdinalIgnoreCase));
                if (inApp is not null) return inApp;
                if (kind is ThinkingRole or DeepThinkingRole && ModelGb(model) is { } gb &&
                    ofKind.Where(o => o.UsesGpu).OrderBy(o => o.GpuGb).FirstOrDefault() is { } template)
                    return template with
                    {
                        Id = $"{template.Id}~{model}", DisplayName = model, ModelId = model, HearsAudio = false, FirstWordMs = null,
                        Steady = template.Steady with { VramGb = gb }, Peak = template.Peak with { VramGb = gb },
                        Evidence = FootprintEvidence.Estimate, Source = "Estimate from the model's size in its name"
                    };
                named = ofKind;
            }
            return named.Where(o => o.UsesGpu && node.Cards(o).Any()).OrderByDescending(o => o.GpuGb).FirstOrDefault()
                ?? named.FirstOrDefault(o => !o.UsesGpu)
                ?? named.OrderByDescending(o => o.GpuGb).First();
        }

        /// <summary>Whether a host role's <paramref name="model"/> is <paramref name="option"/>'s: its id, its model, its model
        /// as a host route names it (an Ollama tag's ':' becomes '-': "gemma4-e4b" is "gemma4:e4b"), or its model with the
        /// engine's name in front ("whisper-large-v3-turbo" is the catalog's "large-v3-turbo", not "small").</summary>
        private static bool Names(ComponentOption option, string model) =>
            Same(option.ModelId, model) || Same(option.Id, model) || option.ModelId is { Length: > 0 } id &&
            (Same(id.Replace(':', '-'), model.Replace(':', '-')) ||
             model.EndsWith("-" + id, StringComparison.OrdinalIgnoreCase) || model.EndsWith("/" + id, StringComparison.OrdinalIgnoreCase));

        /// <summary>About how much graphics memory an Ollama model takes from the size in its name ("14b": 4-bit weights,
        /// about 0.65 GB per billion parameters, plus its context and buffers), or null.</summary>
        private static double? ModelGb(string model)
        {
            var match = Regex.Match(model, @"(\d+(?:\.\d+)?)b\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var billions)
                ? Math.Round(billions * 0.65 + 0.8, 1) : null;
        }

        private bool Wants(PlanComponent component) => request.Wanted is null || request.Wanted.Contains(component);

        /// <summary>The owner turned this part off: in the review (<see cref="NetworkSetupRequest.Off"/>), or on its Companion page
        /// for a part this PC sets there (<see cref="NetworkSetupRequest.Choices"/>). Its roles go and it is planned off.</summary>
        private bool IsOff(PlanComponent component) => ComponentRanking.CanBeOff(component) && (ComponentRanking.SetOnPage(component)
            ? Choice(component) is { On: false }
            : (request.Off ?? []).Contains(component));

        /// <summary>This PC's choice for a part it sets on its Companion page, or null when the request doesn't say.</summary>
        private PartChoice? Choice(PlanComponent component) => (request.Choices ?? []).FirstOrDefault(c => c?.Component == component);

        /// <summary>The part a host role kind does, or null for a kind the recommender doesn't know.</summary>
        private PlanComponent? KindComponent(string kind) => kind switch
        {
            ThinkingRole => PlanComponent.Thinking,
            DeepThinkingRole => PlanComponent.DeepThinking,
            ListeningRole => PlanComponent.Listening,
            LipSyncRole => PlanComponent.LipSync,
            ReadingRole => PlanComponent.Reading,
            SmartHomeRole => PlanComponent.SmartHome,
            SingingRole => PlanComponent.Singing,
            PicturesRole => PlanComponent.Pictures,
            _ => IsVoice(kind) ? PlanComponent.Voice : null
        };

        /// <summary>The priority list's rule for companion PCs (<see cref="ComponentRanking.UsesCompanionCard"/>): only Thinking
        /// and the voice take a companion PC's graphics card; everything else runs there on the processor, or is off.</summary>
        private bool CompanionCard(string kind) => KindComponent(kind) is { } component && ComponentRanking.UsesCompanionCard(component);

        /// <summary>Whether the recommender places roles of <paramref name="kind"/> (the part they do is wanted).</summary>
        private bool Managed(string kind) => kind switch
        {
            ThinkingRole => Wants(PlanComponent.Thinking),
            DeepThinkingRole => Wants(PlanComponent.DeepThinking),
            ListeningRole => Wants(PlanComponent.Listening),
            LipSyncRole => Wants(PlanComponent.LipSync),
            _ => voiceKinds.Contains(kind) && Wants(PlanComponent.Voice)
        };

        /// <summary>An optional extra (singing, pictures, Reading): it keeps its place only with the room the jobs Martlet needs to
        /// talk leave (<see cref="ComponentRanking"/>).</summary>
        private static bool Extra(string kind) => kind is SingingRole or PicturesRole or ReadingRole;

        private bool IsVoice(string kind) => voiceKinds.Contains(kind);

        /// <summary>A role a step may still need: not a voice engine other than the owner's or the plan's fallback, and not one
        /// a step already decided to remove.</summary>
        private bool Useful(Role role) => role.Leave is null && (!IsVoice(role.Kind) || role.Kind == engine || role.Kind == voice);

        private bool MatchesJob(string job, string kind) => job switch
        {
            ClusterJobs.Thinking => kind == ThinkingRole,
            ClusterJobs.Speaking => IsVoice(kind),
            ClusterJobs.Listening => kind == ListeningRole,
            ClusterJobs.LipSync => kind == LipSyncRole,
            _ => false
        };

        private ComponentOption? Find(string? id) => id is null ? null : catalog.Find(id);

        /// <summary>The option when the catalog knows it (not one sized from a model's name).</summary>
        private ComponentOption? Known(ComponentOption? option) => option is null ? null : catalog.Find(option.Id);

        private Node? NodeOf(string? id) => id is null ? null : nodes.FirstOrDefault(n => n.Id == id);

        /// <summary>Where a job runs when no host does it: "this PC" for one companion PC alone, else "each companion PC".</summary>
        private string OwnPcs(bool capital = false) => singlePc
            ? capital ? "This PC" : "this PC"
            : capital ? "Each companion PC" : "each companion PC";

        private string NameOf(string? id) => NodeOf(id)?.Name ?? id ?? "";

        private JobPlan? TodayJob(string job) => (request.CurrentJobs ?? []).FirstOrDefault(j => j?.Job == job);

        /// <summary>What does <paramref name="job"/> today: the role its host runs, else the job's option.</summary>
        private ComponentOption? TodayOption(string job)
        {
            var today = TodayJob(job);
            if (today is null) return null;
            if (today.Off) return null;
            if (NodeOf(today.HostId) is { } host &&
                host.Today.FirstOrDefault(r => !r.Native && MatchesJob(job, r.Kind)) is { Option: { } running })
                return running;
            return Find(today.OptionId);
        }

        /// <summary>The voice engine for every computer: the owner's (<see cref="NetworkSetupRequest.VoiceEngine"/>), else the
        /// one Speaking's host runs, else Martlet's default when Speaking isn't set up; null when the owner speaks with a
        /// hosted voice.</summary>
        private string? Engine()
        {
            if (request.VoiceEngine is { } chosen && voiceKinds.Contains(chosen)) return chosen;
            var today = TodayJob(ClusterJobs.Speaking);
            if (today?.HostId is { } id && (request.Machines ?? []).FirstOrDefault(m => m?.Specs?.Id == id)?.Roles
                    .FirstOrDefault(r => voiceKinds.Contains(r.Kind)) is { } role)
                return role.Kind;
            if (Find(today?.OptionId) is { IsLocal: true, HostRoleKind: { } kind } && voiceKinds.Contains(kind)) return kind;
            if (today is null || today is { HostId: null, OptionId: null })
                return voiceKinds.Contains(SpeechEngines.Default.HostRoleKind) ? SpeechEngines.Default.HostRoleKind : null;
            return null;
        }

        // ---------- Places ----------

        /// <summary>The best place for the first of <paramref name="options"/> that has one.</summary>
        private Slot? FindSlot(IEnumerable<ComponentOption> options, Query query)
        {
            foreach (var option in options)
            {
                if (!option.IsLocal || option.RunsInApp) continue;
                if (query.MaxMs is { } most && !(option.FirstWordMs is { } ms && ms <= most)) continue;
                Slot? best = null;
                double[]? bestKey = null;
                foreach (var node in nodes)
                {
                    if (!Eligible(node, query)) continue;
                    IEnumerable<int?> cards = option.UsesGpu ? node.Cards(option).Select(c => (int?)c) : [null];
                    foreach (var card in cards)
                    {
                        if (!Valid(node, card, option, query) || Key(node, card, option, query) is not { } key) continue;
                        if (bestKey is null || Compare(key, bestKey) < 0)
                        {
                            best = new(node, card, option, query.Native);
                            bestKey = key;
                        }
                    }
                }
                if (best is not null) return best;
            }
            return null;
        }

        private bool Eligible(Node node, Query query)
        {
            if (node.Presence != Presence.Here || query.Only is { } only && node.Id != only) return false;
            if (query.Native) return node.Companion;
            if (!node.CanHost) return false;
            if (node.Companion ? !(query.Companions || singlePc) : !query.Hosts) return false;
            return !query.Optional || node.Machine.Manageable;
        }

        /// <summary>The hard rules: room on the card, in memory, on the processor and the disk; one role of a kind and one voice
        /// engine per computer; one language model and one voice per card; a companion PC's card only for Thinking and the
        /// voice; a host role's processor variant only where no card has room for it (it uses the card by default).</summary>
        private bool Valid(Node node, int? card, ComponentOption option, Query query)
        {
            if (!option.RunsOn(node.Spec.Platform)) return false;
            if (!query.Native && (node.Roles.Any(r => !r.Native && r.Kind == query.Kind) ||
                IsVoice(query.Kind) && node.Roles.Any(r => !r.Native && IsVoice(r.Kind))))
                return false;
            var cardAllowed = !node.Companion || CompanionCard(query.Kind);
            if (card is { } c)
            {
                if (!cardAllowed) return false;
                var onCard = node.Roles.Where(r => r.Card == c).ToList();
                var reserved = node.Pending.Where(r => r.Card == c && !(r.Kind == query.Kind && r.Native == query.Native) && Reserved(node, r, query)).ToList();
                if (node.Free(c) - reserved.Sum(r => r.Gb) + Epsilon < option.GpuGb) return false;
                if (onCard.Any(r => r.Option is { CanShareGpu: false }) || !option.CanShareGpu && onCard.Count > 0) return false;
                if (query.Kind is ThinkingRole or DeepThinkingRole && onCard.Concat(reserved).Any(r => r.IsModel)) return false;
                if (IsVoice(query.Kind) && onCard.Any(r => IsVoice(r.Kind))) return false;
            }
            else if (option.UsesGpu) return false;
            else if (!query.Native && cardAllowed && catalog.Options.Any(o => o.IsLocal && o.HostRoleKind == query.Kind && o.UsesGpu &&
                node.Cards(o).Any(other => node.Free(other) + Epsilon >= o.GpuGb)))
                return false;
            var ram = option.Peak.RamGb + (card is { } u && node.Spec.Gpus[u].UnifiedMemory ? option.GpuGb : 0);
            var reservedRam = node.Pending.Where(r => !(r.Kind == query.Kind && r.Native == query.Native) && Reserved(node, r, query))
                .Sum(r => r.Option is { IsLocal: true } o ? o.Peak.RamGb : 0);
            if (node.RamCapacity - node.RamUsed - reservedRam + Epsilon < ram) return false;
            if (node.CpuCapacity * PlacementEngine.CpuOversubscription - node.CpuUsed + Epsilon < option.Steady.CpuThreads) return false;
            var downloaded = node.Pending.Any(r => r.Kind == query.Kind && (r.Option == option || Same(r.Model, option.ModelId))) ||
                !query.Native && KeptOnDisk(node, query.Kind, option);
            return downloaded || node.DiskCapacity <= 0 || node.DiskCapacity - node.DiskUsed + Epsilon >= option.Peak.DiskGb;
        }

        /// <summary>Whether <paramref name="node"/>'s host service keeps <paramref name="option"/>'s model downloaded from a role of
        /// <paramref name="kind"/> it turned off (<see cref="NetworkMachine.Downloaded"/>), so turning it back on downloads nothing.
        /// An option without a model (lip-sync) matches any kept download of its kind.</summary>
        private static bool KeptOnDisk(Node node, string kind, ComponentOption option) =>
            (node.Machine.Downloaded ?? []).Any(d => d?.Kind == kind && d.Model is { Length: > 0 } model &&
                (option.ModelId is null || Names(option, model)));

        /// <summary>How good a valid place is (lower is better), or null when a soft rule of <paramref name="query"/> rules it
        /// out: hosts before companion PCs; not pushing out what runs today; a voice alone on a Windows card; the idlest card;
        /// computers Martlet can change and that stay on; where the job runs today; what is downloaded already; the least
        /// loaded; then the tightest fit, so bigger cards stay free for bigger models.</summary>
        private double[]? Key(Node node, int? card, ComponentOption option, Query query)
        {
            var others = Others(node, card, query);
            var windows = node.OnWindows && card is not null &&
                (IsVoice(query.Kind) ? others.Count > 0 : others.Any(r => IsVoice(r.Kind)));
            if (query.StrictWindows && windows) return null;
            var contention = others.Count(r => !OnDemand(r.Kind) && (query.Kind != DeepThinkingRole || r.Kind != DeepThinkingRole)) +
                (node.Companion ? CompanionLoad : 0);
            if (query.MaxContention is { } most && contention > most) return null;
            var mine = node.Pending.FirstOrDefault(r => r.Kind == query.Kind && r.Native == query.Native);
            var reuse = mine is not null && (mine.Option == option || Same(mine.Model, option.ModelId)) ? 0
                : !query.Native && KeptOnDisk(node, query.Kind, option) ? 0.5
                : mine is null ? 2 : 1;
            var room = card is { } c ? node.Free(c) - option.GpuGb
                : node.CpuCapacity * PlacementEngine.CpuOversubscription - node.CpuUsed - option.Steady.CpuThreads;
            return
            [
                node.Companion && !singlePc ? 1 : 0,
                Displaces(node, card, option, query) ? 1 : 0,
                windows ? 1 : 0,
                query.Kind != ThinkingRole && card is { } shared && ThinkingCard(node, shared) ? 1 : 0,
                contention,
                node.Machine.Manageable ? 0 : 1,
                node.Spec.OnBattery ? 1 : 0,
                query.Prefer is { } prefer && node.Id != prefer ? 1 : 0,
                reuse,
                query.LeastLoaded ? node.Load : 0,
                query.MostRoom ? -room : room
            ];
        }

        /// <summary><see cref="Reserved(Node, Role)"/>, and today's optional extras against <paramref name="query"/> when it
        /// places something only nice to have or only makes things better (<see cref="Query.SparesExtras"/>).</summary>
        private bool Reserved(Node node, Role role, Query query) =>
            Reserved(node, role) || query.SparesExtras && Extra(role.Kind) && role.Leave is null;

        /// <summary>Today's role of a job or pool place keeps its room until the step that decides that job or pool (rule 11):
        /// a new role never pushes it out first. Deep thinking, spare Thinking models, other voice engines, optional extras
        /// and a companion PC's pool place (it leaves when a host can share the job; rule 6) don't.</summary>
        private bool Reserved(Node node, Role role)
        {
            if (role.Leave is not null || !Useful(role) || role.Kind == DeepThinkingRole) return false;
            foreach (var job in ClusterJobs.All)
            {
                if (!MatchesJob(job, role.Kind) || TodayJob(job) is not { } today) continue;
                var decided = decisions.ContainsKey(job);
                if (role.Native)
                {
                    if (today.HostId is null && !decided) return true;
                    continue;
                }
                if (today.HostId == node.Id && !decided) return true;
                if (today.Pool.Contains(node.Id) && !poolsDone.Contains(job) && !node.Companion) return true;
            }
            return false;
        }

        /// <summary>Rule 7: the card of Thinking's model (placed, or where it runs today until the Thinking step decides), which
        /// other roles leave to it when another card has room, so the first sentence comes from an idle card.</summary>
        private bool ThinkingCard(Node node, int card) =>
            node.Roles.Any(r => r.Card == card && r.Purpose == ClusterJobs.Thinking && !r.Native) ||
            !decisions.ContainsKey(ClusterJobs.Thinking) && TodayJob(ClusterJobs.Thinking)?.HostId == node.Id &&
            node.Pending.Any(r => r.Card == card && r.Kind == ThinkingRole && !r.Native && Useful(r));

        private static int Compare(double[] a, double[] b)
        {
            for (var i = 0; i < a.Length; i++)
            {
                var order = a[i].CompareTo(b[i]);
                if (order != 0) return order;
            }
            return 0;
        }

        /// <summary>The other roles on a card: the ones placed, and today's that may stay (Deep thinking moves before a live job
        /// does, and another voice engine than the network's goes).</summary>
        private List<Role> Others(Node node, int? card, Query query)
        {
            if (card is null) return [];
            return node.Roles.Where(r => r.Card == card)
                .Concat(node.Pending.Where(r => r.Card == card && Useful(r) && r.Kind != DeepThinkingRole &&
                    !(r.Kind == query.Kind && r.Native == query.Native)))
                .ToList();
        }

        /// <summary>Whether placing <paramref name="option"/> there would push out a role that runs there today.</summary>
        private bool Displaces(Node node, int? card, ComponentOption option, Query query)
        {
            if (card is not { } c) return false;
            var onCard = node.Pending.Where(r => r.Card == c && Useful(r) && !(r.Kind == query.Kind && r.Native == query.Native)).ToList();
            if (query.Kind is ThinkingRole or DeepThinkingRole && onCard.Any(r => r.IsModel)) return true;
            return node.Free(c) - onCard.Sum(r => r.Gb) + Epsilon < option.GpuGb;
        }

        /// <summary>Puts a new role (or today's role of the kind, consumed) in <paramref name="slot"/>. A voice engine replaces
        /// the computer's other voice engines (exclusive=voice).</summary>
        private Role Place(Slot slot, string kind, string purpose, SetupChangeBenefit benefit, string why)
        {
            var node = slot.Node;
            var old = node.Pending.FirstOrDefault(r => r.Kind == kind && r.Native == slot.Native);
            if (old is not null) node.Pending.Remove(old);
            var sameModel = old is not null && (old.Option == slot.Option || Same(old.Model, slot.Option.ModelId));
            var role = new Role
            {
                Kind = kind, Option = slot.Option, Card = slot.Card, Native = slot.Native, Was = old?.Was,
                OnDisk = !slot.Native && !sameModel && KeptOnDisk(node, kind, slot.Option),
                Model = sameModel ? old!.Model : slot.Option.ModelId, Purpose = purpose, Benefit = benefit, Why = why
            };
            node.Roles.Add(role);
            if (IsVoice(kind))
                foreach (var other in node.Pending.Where(r => IsVoice(r.Kind)))
                    other.Leave ??= (benefit, $"{Label(slot.Option)} replaces it: a computer runs one voice engine at a time.");
            return role;
        }

        private static void Commit(Node node, Role role, int? card, string purpose)
        {
            node.Pending.Remove(role);
            role.Card = card;
            role.Purpose = purpose;
            node.Roles.Add(role);
        }

        private static void Unplace(Node node, Role role)
        {
            node.Roles.Remove(role);
            node.Pending.Add(role);
            role.Purpose = Kept;
        }

        /// <summary>Keeps today's <paramref name="role"/> on its computer: on its card, else (two or more NVIDIA cards) on
        /// another card there that obeys the rules. <paramref name="problem"/> says what was wrong with its own card.</summary>
        private bool Keep(Node node, Role role, string purpose, out Problem problem, Func<int, bool>? cardOk = null)
        {
            problem = Problem.None;
            if (role.Option is not { UsesGpu: true } option || role.Card is not { } own)
            {
                if (IsVoice(role.Kind) && node.Roles.Any(r => IsVoice(r.Kind)))
                {
                    problem = Problem.SharesVoice;
                    return false;
                }
                Commit(node, role, role.Card, purpose);
                return true;
            }
            var cards = new List<int> { own };
            if (node.Pinnable) cards.AddRange(node.Cards(option).Where(c => c != own).OrderByDescending(node.Free).ThenBy(c => c));
            foreach (var card in cards)
            {
                var fault = Fits(node, card, role);
                if (fault == Problem.None && cardOk?.Invoke(card) == false) fault = Problem.SharesVoice;
                if (fault == Problem.None)
                {
                    if (card != own && role.Was?.GpuIndex is not null)
                    {
                        role.Benefit = problem == Problem.NoRoom ? SetupChangeBenefit.Required : SetupChangeBenefit.Improvement;
                        role.Why = problem switch
                        {
                            Problem.NoRoom => $"{CardText(node, own)} has no room left for it.",
                            Problem.SharesModel => "One language model per graphics card: each gets a card of its own, so neither waits for the other.",
                            _ => $"{CardText(node, card)} keeps it apart from the voice and the conversation."
                        };
                    }
                    Commit(node, role, card, purpose);
                    return true;
                }
                if (problem == Problem.None) problem = fault;
            }
            return false;
        }

        private Problem Fits(Node node, int card, Role role)
        {
            var onCard = node.Roles.Where(r => r.Card == card).ToList();
            if (IsVoice(role.Kind) && node.Roles.Any(r => IsVoice(r.Kind))) return Problem.SharesVoice;
            if (role.IsModel && onCard.Any(r => r.IsModel)) return Problem.SharesModel;
            if (onCard.Any(r => r.Option is { CanShareGpu: false })) return Problem.NoRoom;
            return node.Free(card) + Epsilon < role.Option!.GpuGb ? Problem.NoRoom : Problem.None;
        }

        /// <summary>How busy a role's card is today: the other roles on it, and on a companion PC its games and the character's
        /// renderer (a voice took about twice as long beside the character; docs/VOICE_LATENCY.md).</summary>
        private static int Contention(Node node, Role role) =>
            (role.Card is { } card ? node.Today.Count(r => r != role && r.Card == card && !OnDemand(r.Kind)) : 0) +
            (node.Companion ? CompanionLoad : 0);

        /// <summary>A companion PC's card counts as this many busy roles (its games and the character's renderer).</summary>
        private const int CompanionLoad = 2;

        /// <summary>Singing and pictures free the card after a few idle minutes, so they don't make a card busy.</summary>
        private static bool OnDemand(string kind) => Extra(kind);

        private static string CardText(Node node, int? card) => card is { } c && c < node.Spec.Gpus.Count
            ? $"{node.Name}'s {GpuName(node, c)}"
            : $"{node.Name}'s processor";

        private static string GpuName(Node node, int card) =>
            node.Spec.Gpus[card].Name + (node.Spec.Gpus.Count > 1 ? $" (card {card + 1})" : "");

        private static string CardText(Slot slot) => CardText(slot.Node, slot.Card);
    }
}
