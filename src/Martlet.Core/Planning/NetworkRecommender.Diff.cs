using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

public static partial class NetworkRecommender
{
    private sealed partial class Planner
    {
        /// <summary>The changes from today's setup to <paramref name="target"/>, make before break (rule 12): pin and switch
        /// roles, add roles, assign jobs, join pools, then leave pools and remove roles. Nothing changes on a computer that isn't
        /// answering, and the Thinking pool needs no change of its own (a host joins it once it runs Deep thinking).</summary>
        private List<SetupChange> Diff(NetworkSetup target)
        {
            var changes = new List<SetupChange>();
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here)) RoleChanges(node, changes);
            foreach (var job in ClusterJobs.All)
            {
                if (!decisions.TryGetValue(job, out var decision) || decision.Frozen) continue;
                var today = TodayJob(job);
                // A job changes when another computer (or none) does it, or, with no host before or after, another option
                // (a hosted provider or the app's own) does it; a job that wasn't set up changes when the plan gives it one.
                var moved = today?.HostId != decision.HostId || (today?.Off ?? false) != decision.Off;
                var switched = decision.HostId is null && today?.HostId is null && decision.OptionId is not null &&
                    !string.Equals(today?.OptionId, decision.OptionId, StringComparison.Ordinal);
                if (moved || switched)
                {
                    var option = Find(decision.OptionId);
                    var summary = decision.Off ? "The character's face follows the voice's loudness on each companion PC."
                        : decision.HostId is { } host ? $"{NameOf(host)} does {Lower(job)}" + (option is null ? "." : $" with {Plain(option)}.")
                        : option is { IsLocal: false } ? $"{option.DisplayName} does {Lower(job)}."
                        : option is not null ? $"Each companion PC does {Lower(job)} itself with {Plain(option)}."
                        : $"Nobody does {Lower(job)}.";
                    changes.Add(new SetupChange(SetupChangeKind.AssignJob, decision.HostId ?? "", summary, Reason(decision.Why, summary))
                    {
                        Benefit = decision.Benefit, Job = job, FromMachineId = today?.HostId, OptionId = decision.OptionId
                    });
                }
                if (job is ClusterJobs.Speaking or ClusterJobs.Listening) PoolChanges(job, decision, today, changes);
            }
            return changes.OrderBy(c => Rank(c.Kind)).ThenBy(c => JobOrder(c.Job)).ThenBy(c => c.MachineId, StringComparer.Ordinal)
                .ThenBy(c => c.RoleKind, StringComparer.Ordinal).ToList();
        }

        private static int Rank(SetupChangeKind kind) => kind switch
        {
            SetupChangeKind.MoveToGpu => 0,
            SetupChangeKind.ChangeModel => 1,
            SetupChangeKind.AddRole => 2,
            SetupChangeKind.AssignJob => 3,
            SetupChangeKind.JoinPool => 4,
            SetupChangeKind.LeavePool => 5,
            _ => 6
        };

        private static string Reason(string why, string summary) => string.IsNullOrWhiteSpace(why) ? summary : why;

        private void RoleChanges(Node node, List<SetupChange> changes)
        {
            var today = node.Machine.Roles.Where(r => r?.Kind is { Length: > 0 }).GroupBy(r => r.Kind, StringComparer.Ordinal)
                .Select(g => g.First()).ToList();
            foreach (var role in node.Roles.Where(r => !r.Native && !r.Fixed).OrderBy(r => r.Kind, StringComparer.Ordinal))
            {
                var was = today.FirstOrDefault(r => r.Kind == role.Kind);
                int? gpu = node.Pinnable && role.Option is { UsesGpu: true } ? role.Card : null;
                var name = Label(role.Option, role.Kind);
                if (was is null)
                {
                    var summary = $"Install {name} on {CardText(node, role.Card)}." +
                        (role.Kind == DeepThinkingRole ? $" {node.Name} then joins the Thinking pool by itself." : "");
                    changes.Add(RoleChange(SetupChangeKind.AddRole, node, role, summary, role.Benefit, role.Why) with
                    {
                        Model = role.Model, GpuIndex = gpu, DownloadGb = Download(role)
                    });
                    continue;
                }
                if (role.Model is not null && !Same(role.Model, was.Model))
                {
                    var summary = $"Switch {node.Name}'s {KindName(role.Kind)} from {was.Model ?? "its model"} to {name}.";
                    changes.Add(RoleChange(SetupChangeKind.ChangeModel, node, role, summary, role.Benefit, role.Why) with
                    {
                        Model = role.Model, FromModel = was.Model, DownloadGb = Download(role)
                    });
                }
                if (gpu is { } card && card != was.GpuIndex)
                {
                    var (benefit, why) = was.GpuIndex is not null && role.Why.Length > 0 ? (role.Benefit, role.Why) : PinReason(node);
                    changes.Add(RoleChange(SetupChangeKind.MoveToGpu, node, role, $"Pin {node.Name}'s {name} to its {GpuName(node, card)}.", benefit, why) with
                    {
                        GpuIndex = card
                    });
                }
            }
            foreach (var was in today.Where(w => !node.Roles.Any(r => !r.Native && r.Kind == w.Kind)))
            {
                var role = node.Today.FirstOrDefault(r => !r.Native && r.Kind == was.Kind);
                var (benefit, why) = role?.Leave ?? (SetupChangeBenefit.Minor, "Nothing uses it.");
                changes.Add(new SetupChange(SetupChangeKind.RemoveRole, node.Id, $"Remove {Label(role?.Option, was.Kind)} from {node.Name}.", why)
                {
                    Benefit = benefit, RoleKind = was.Kind, Model = was.Model, NeedsSomeoneThere = !node.Machine.Manageable
                });
            }
        }

        private static SetupChange RoleChange(SetupChangeKind kind, Node node, Role role, string summary, SetupChangeBenefit benefit, string why) =>
            new(kind, node.Id, summary, Reason(why, summary)) { Benefit = benefit, RoleKind = role.Kind, NeedsSomeoneThere = !node.Machine.Manageable };

        private static double? Download(Role role) => role.Option?.Peak.DiskGb is > 0 and var gb ? Math.Round(gb, 1) : null;

        /// <summary>Rule 1: on a computer with two or more NVIDIA cards every role is pinned to one card.</summary>
        private static (SetupChangeBenefit, string) PinReason(Node node) =>
            node.Roles.Count(r => !r.Native && r.Card is not null && r.Option is { UsesGpu: true }) >= 2
                ? (SetupChangeBenefit.Improvement, $"{node.Name} has more than one NVIDIA card: pinning each role to its own card keeps " +
                    "language models apart and live jobs off the Thinking pool's card, so none waits for another or runs short of memory.")
                : (SetupChangeBenefit.Minor, "Pinned to one card, so it keeps that card when other roles come.");

        private string KindName(string kind) => kind switch
        {
            ThinkingRole => "Thinking model",
            DeepThinkingRole => "Deep thinking model",
            ListeningRole => "speech recognizer",
            LipSyncRole => "lip-sync model",
            _ => IsVoice(kind) ? "voice engine" : kind
        };

        private void PoolChanges(string job, Decision decision, JobPlan? today, List<SetupChange> changes)
        {
            var before = (today?.Pool ?? []).Where(p => p is not null).ToList();
            var verb = job == ClusterJobs.Speaking ? "speaks" : "hears";
            var kind = job == ClusterJobs.Speaking ? engine : ListeningRole;
            foreach (var member in decision.Pool.Where(m => !before.Contains(m)))
                changes.Add(new SetupChange(SetupChangeKind.JoinPool, member, $"{NameOf(member)} joins the {Title(job)} pool.",
                    $"When {NameOf(decision.HostId)} is busy, {NameOf(member)} {verb} for another companion PC instead of making it wait.")
                {
                    Benefit = SetupChangeBenefit.Improvement, Job = job
                });
            foreach (var member in before.Where(m => !decision.Pool.Contains(m)))
            {
                var node = NodeOf(member);
                var (benefit, why) = member == decision.HostId ? (SetupChangeBenefit.Minor, $"{NameOf(member)} does {Lower(job)} itself now.")
                    : node is { Presence: Presence.Gone } ? (SetupChangeBenefit.Required, $"{node.Name} hasn't answered for {Minutes(node.Machine.OfflineFor)}.")
                    : node?.Today.FirstOrDefault(r => r.Kind == kind)?.Leave is { } leave ? leave
                    : (SetupChangeBenefit.Minor, $"{NameOf(member)} doesn't run {Lower(job)}'s engine.");
                changes.Add(new SetupChange(SetupChangeKind.LeavePool, member, $"{NameOf(member)} leaves the {Title(job)} pool.", why)
                {
                    Benefit = benefit, Job = job
                });
            }
        }

        private void AddNotes(NetworkSetup target, List<SetupChange> changes)
        {
            var first = new List<string>();
            foreach (var node in nodes.Where(n => n.Presence == Presence.Gone))
            {
                var worked = (request.CurrentJobs ?? []).Any(j => j?.HostId == node.Id || j?.Pool.Contains(node.Id) == true);
                first.Add($"{node.Name} hasn't answered for {Minutes(node.Machine.OfflineFor)}, so Martlet plans without it" +
                    (worked ? " and its jobs move." : "."));
            }
            foreach (var node in nodes.Where(n => n.Presence == Presence.Away))
                first.Add((node.Machine.OfflineFor is null ? $"{node.Name} isn't answering" : $"{node.Name} hasn't answered for {Minutes(node.Machine.OfflineFor)}") +
                    "; Martlet plans as if it were back and changes nothing there.");
            notes.InsertRange(0, first);
            foreach (var node in nodes.Where(n => n.Presence == Presence.Here && n.OnWindows))
                foreach (var voice in node.Roles.Where(r => IsVoice(r.Kind) && r.Card is not null))
                {
                    var others = node.Roles.Where(r => r != voice && r.Card == voice.Card && r.Gb > 0).Select(r => Label(r.Option, r.Kind)).ToList();
                    if (others.Count > 0)
                        notes.Add($"{node.Name} runs on Windows, so {Label(voice.Option, voice.Kind)} shares its {GpuName(node, voice.Card!.Value)} with " +
                            $"{List(others)}; if the card's memory runs short, the voice can start late.");
                }
            foreach (var id in changes.Where(c => c.NeedsSomeoneThere).Select(c => c.MachineId).Distinct(StringComparer.Ordinal))
                notes.Add($"{NameOf(id)} can't be changed from here: someone has to make its changes at that computer.");
            if (changes.Count > 0) notes.Add("Sizes are planning estimates (docs/RESOURCE_FOOTPRINTS.md).");
        }
    }
}
