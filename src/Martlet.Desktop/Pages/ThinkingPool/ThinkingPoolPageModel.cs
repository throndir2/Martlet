using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking pool's decisions and settings, without WPF. The page makes one each time it draws: it reads
/// thinking-pool.json, the reply settings and the paired computers once, and says what each line of the page shows. The changes
/// the page saves are in ThinkingPoolPageModel.Changes.cs. <see cref="ThinkingPoolPage"/> draws it, and
/// <see cref="IThinkingPoolPageServices"/> is all it reads and changes in the app.</summary>
internal sealed partial class ThinkingPoolPageModel
{
    /// <summary>Thinking longer's How hard choices, in order.</summary>
    internal static readonly ThinkEffort[] ThinkEfforts = [ThinkEffort.Medium, ThinkEffort.High];

    /// <summary>Thinking longer's Share it choices, in order.</summary>
    internal static readonly ThinkDelivery[] ThinkDeliveries = [ThinkDelivery.WhenFree, ThinkDelivery.NextMessage];

    private readonly IThinkingPoolPageServices services;

    internal ThinkingPoolPageModel(IThinkingPoolPageServices services)
    {
        this.services = services;
        var settings = services.Settings;
        Route = settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        Pool = ThinkingPoolSettings.Load(services.DataDirectory);
        Routes = settings?.Setup?.Routes ?? [];
        Sharing = WorkSharingRoster.Settings(services.DataDirectory);
        PoolPlan = Pool.Plan(Routes, Sharing, WorkSharingRoster.Device);
        // Thinking longer, research and a song's lyrics go only to the members that take long jobs.
        LongJobs = Pool.ForLongJobs();
        LongPlan = LongJobs.Plan(Routes, Sharing, WorkSharingRoster.Device).Plan;
        SavedThinkLonger = settings?.Generation?.ThinkLonger;
        ThinkLongerChoices = ThinkLongerSettings.Of(settings?.Generation);
        On = ThinkLongerChoices.On;
        Hosts = services.PairedHosts;

        Places = ThinkLonger.Places(PoolPlan, BackgroundDuties.Of(services.DataDirectory), choices: Pool).Where(p => p.Id != "thinking").ToArray();
        // What each member can do comes from the saved model abilities (the conversation's pool reads the same file), so the
        // list says the same before the conversation has read the pool.
        var abilities = services.SavedModelAbilities();
        Statuses = Pool.All.Select(m => new ThinkingPoolMemberStatus(m.Key, m.Computer(null), m.ThinksAtOnce, 0,
            ThinkingPoolCapabilities.For(m, abilities), 0)).ToArray();
        Resources = LiveResources.For(Routes, HostRouteGpus.For);
        // Members whose providers limit requests now (a busy free cloud endpoint): they wait, or run fewer jobs at once, for a while.
        Cooling = services.PoolCooling();
        ThinkingHost = NetworkMap.ThinkingHost(settings);
    }

    /// <summary>The conversation's own Thinking route, or null before Thinking is set up.</summary>
    internal SetupRoute? Route { get; }
    /// <summary>Every saved setup route.</summary>
    internal IReadOnlyList<SetupRoute> Routes { get; }
    /// <summary>thinking-pool.json as saved.</summary>
    internal ThinkingPoolSettings Pool { get; }
    /// <summary>How work is shared among your computers.</summary>
    internal WorkSharingSettings Sharing { get; }
    /// <summary>Where each member can run now.</summary>
    internal DeepThinkingPool PoolPlan { get; }
    /// <summary>The pool with only the members that take long jobs.</summary>
    internal ThinkingPoolSettings LongJobs { get; }
    /// <summary>Whether thinking longer can run in parallel with the conversation now, and why.</summary>
    internal DeepThinkingPlan LongPlan { get; }
    /// <summary>Thinking longer's settings as saved, or null when they were never saved.</summary>
    internal ThinkLongerSettings? SavedThinkLonger { get; }
    /// <summary>Thinking longer's settings, with the defaults for what was never saved.</summary>
    internal ThinkLongerSettings ThinkLongerChoices { get; }
    /// <summary>Whether Martlet may think longer at all.</summary>
    internal bool On { get; }
    /// <summary>This PC's paired computers.</summary>
    internal IReadOnlyList<PairedHost> Hosts { get; }
    /// <summary>The pool's places (one for each member that can run), without the conversation's own Thinking.</summary>
    internal BackgroundPlace[] Places { get; }
    /// <summary>Each member in the machine list (on, then off) and what it can do, in list order.</summary>
    internal ThinkingPoolMemberStatus[] Statuses { get; }
    /// <summary>The computers and graphics cards the conversation uses, so a member that shares them waits while you talk.</summary>
    internal LiveResources Resources { get; }
    /// <summary>The members whose providers limit requests now.</summary>
    internal IReadOnlyList<ThinkingPoolCooling> Cooling { get; }
    /// <summary>The paired computer that does this PC's Thinking, or null.</summary>
    internal string? ThinkingHost { get; }

    // ---------- the Now card ----------

    /// <summary>The Now card's first line (DeepThinkingNow).</summary>
    internal string NowText => DeepThinkingNow(LongJobs, Route, LongPlan, On);

    /// <summary>The Now card's second line (DeepThinkingParallel): why thinking longer can or can't run now.</summary>
    internal string WhyText => On ? LongPlan.Why : "Thinking longer is off. Turn it on under Thinking longer below.";

    /// <summary>Whether <see cref="WhyText"/> is a warning.</summary>
    internal bool WhyIsWarning => On && !LongPlan.Available;

    /// <summary>The Now card's line: whether Martlet may think longer, which pool members (or the conversation model) a job goes
    /// to and whether one can run there.</summary>
    internal static string DeepThinkingNow(ThinkingPoolSettings pool, SetupRoute? route, DeepThinkingPlan plan, bool on)
    {
        if (!on) return "Off. Martlet answers everything right away and never thinks in the background.";
        var where = pool.Members.Count > 0 ? pool.Places.DescribeAll()
            : !pool.UseConversationModelWhenEmpty ? "no member"
            : route is null ? "the conversation model (not set up yet)" : $"the conversation model ({route.ModelId}), until a machine in the pool takes long jobs";
        return plan.Available ? $"The Thinking pool thinks on {where}, in parallel with the conversation."
            : $"On, but the pool can't think on {where}, so Martlet doesn't offer to think things over. Add a machine below, or tick Long jobs on one.";
    }

    // ---------- the machine list ----------

    /// <summary>The machine list's first line (ThinkingPoolSummary).</summary>
    internal string Summary => PoolSummary(Pool, Places.Sum(p => p.Slots));

    /// <summary>The machine list's first line (MCP reads it as ThinkingPoolSummary): how many machines are on and their usable
    /// <paramref name="slots"/>, and how many are off; with none on, that the pool is off and what runs meanwhile.</summary>
    internal static string PoolSummary(ThinkingPoolSettings pool, int slots)
    {
        var off = pool.OffMembers.Count;
        var offText = off == 0 ? "" : $" {off} machine{(off == 1 ? " is" : "s are")} turned off.";
        if (pool.Members.Count == 0)
            return "The Thinking pool is off: no machine is on." + offText +
                (pool.UseConversationModelWhenEmpty ? " Thinking longer and research use the conversation model meanwhile."
                    : " The conversation model isn't used either, so Martlet doesn't think in the background.") +
                (off == 0 ? " Add a machine below." : " Tick On to use one again.");
        var on = pool.Members.Count;
        return $"{on} machine{(on == 1 ? " is" : "s are")} on, with {slots} usable slot{(slots == 1 ? "" : "s")} in all." + offText;
    }

    /// <summary>The conversation row's title: "The conversation" and its model.</summary>
    internal string ConversationTitle => "The conversation" + (Route is null ? "" : $" ({Route.ModelId})");

    /// <summary>The conversation row's line (ThinkingPoolConversation).</summary>
    internal string ConversationDetail => Route is null ? "Thinking isn't set up yet."
        : "Answers you. Never in the pool: no pool job waits in front of a reply, and its model stays ready for the conversation, " +
          "so replies start as fast as they can.";

    /// <summary>One machine in the list, as its row shows it.</summary>
    /// <param name="Index">Its place in the list, the end of its automation IDs (ThinkingPoolOn-0...).</param>
    /// <param name="HostId">The paired computer it runs on, or null for an endpoint.</param>
    /// <param name="Card">The graphics card of that computer it runs on (1 for the first).</param>
    /// <param name="Suffix">"-gpu2" and so on for a card after the first, else empty.</param>
    /// <param name="Detail">Its line (ThinkingPoolMember-n): what it reads and writes, its slots and whether it can run now.</param>
    /// <param name="Badges">What may slow it or cost money (ThinkingPoolBadges-n).</param>
    /// <param name="SharedCard">The warning that it shares a graphics card with this PC's Thinking, or null.</param>
    internal sealed record MemberLine(DeepThinkingSettings Member, int Index, string Key, string Name, bool On, string? HostId, int Card,
        string Suffix, PairedHost? Host, HostCheck? Check, bool Paid, int Slots, string Detail, string[] Badges, string? SharedCard);

    /// <summary>The machine at <paramref name="i"/> in the list (on, then off).</summary>
    internal MemberLine Member(int i)
    {
        var member = Pool.All[i];
        var can = Statuses[i].Can;
        var place = Places.FirstOrDefault(p => p.Id == member.Key);
        var cooling = Cooling.FirstOrDefault(c => c.Id == member.Key);
        var key = member.Key;
        var name = member.Describe();
        var on = !Pool.IsOff(key);
        var hostId = member is { Place: DeepThinkingPlace.Host, HostId: { } id } ? id : null;
        // A host with a Thinking pool model on each of several graphics cards has one member per card, each on or off by itself.
        var card = member.Card;
        var suffix = card > 1 ? $"-gpu{card}" : "";
        var host = hostId is null ? null : Hosts.FirstOrDefault(h => h.HostId == hostId);
        var check = hostId is null ? null : services.CheckOf(hostId);
        var offline = hostId is not null && HostPresence.IsOffline(hostId);
        var paid = ThinkingBackupMembers.Paid(member);
        var slots = member.ThinksAtOnce;
        string[] reads = ["text", .. can.HasFlag(ThinkingCapability.Vision) ? ["pictures"] : Array.Empty<string>(),
            .. can.HasFlag(ThinkingCapability.Audio) ? ["sound"] : Array.Empty<string>()];
        var detail = (on ? "" : "Off: it takes no jobs and keeps its settings. Tick On to use it again. ") +
            $"Reads {(reads.Length == 1 ? reads[0] : string.Join(", ", reads[..^1]) + " and " + reads[^1])}; writes text" +
            $"{(can.HasFlag(ThinkingCapability.Tools) ? " and calls tools" : "")}. " +
            $"{slots} slot{(slots == 1 ? "" : "s")}{(member.OnHostRole && hostId is not null ? $", set on {hostId}" : "")}." +
            (card > 1 ? $" Runs on {hostId}'s graphics card {card}, as a member of its own." : "") +
            (on && PoolPlan.Find(key) is { Plan.Available: false } spot ? $" Can't run now: {spot.Plan.Why}" : "") +
            // A paired computer that stopped answering stays a member: its slots come back by themselves.
            (on && offline ? $" Offline now: its slot{(slots == 1 ? "" : "s")} come{(slots == 1 ? "s" : "")} back when it answers again." : "") +
            // Its provider limited requests: it waits, or runs fewer jobs at once, and its jobs wait in line instead of failing.
            (cooling is not null ? $" {cooling.Describe(DateTimeOffset.UtcNow)}." : "");
        string[] badges = [.. place is not null && Resources.Shares(place) ? ["Waits while you talk"] : Array.Empty<string>(),
            .. paid ? ["Costs money"] : Array.Empty<string>(), .. offline ? ["Offline"] : Array.Empty<string>(),
            .. cooling is not null ? ["Limiting requests"] : Array.Empty<string>()];
        // One graphics card for each Thinking model: a pool model on the card of this computer's Thinking shares it.
        var shared = hostId is not null && check?.Reachable == true ? DeepThinkingFit.SharedCard(member, services.HardwareOf(hostId), check) : null;
        return new(member, i, key, name, on, hostId, card, suffix, host, check, paid, slots, detail, badges, shared);
    }

    /// <summary>A graphics card of a paired computer in the list that runs no Thinking pool model yet.</summary>
    /// <param name="Cannot">Why Martlet can't set up a model there from here, or null.</param>
    internal sealed record SpareCardLine(PairedHost Host, string HostId, int Card, string Title, string Detail, string? Cannot);

    /// <summary>Each graphics card of a paired computer in the list (on or off) that runs no Thinking pool model yet, when the
    /// computer answers and reports that many NVIDIA cards.</summary>
    internal IEnumerable<SpareCardLine> SpareCards()
    {
        foreach (var hostId in Pool.All.Where(m => m is { OnHostRole: true, HostId: not null }).Select(m => m.HostId!).Distinct(StringComparer.Ordinal))
        {
            var host = Hosts.FirstOrDefault(h => h.HostId == hostId);
            if (host is null || services.CheckOf(hostId) is not { Reachable: true } check) continue;
            var cards = Math.Min(HostRoles.NvidiaCards(services.HardwareOf(hostId)) ?? 0, SelfHostSetup.DeepThinkingMaximumCards);
            for (var card = 2; card <= cards; card++)
            {
                var kind = SelfHostSetup.DeepThinkingRoleKind(card);
                if (check.Offers?.ContainsKey(kind) == true) continue;
                var cannot = !services.ChangesRolesOn(host) ? $"Martlet can't set up roles on {hostId} from here."
                    : services.CannotHand(hostId, kind, "deep thinking");
                var title = $"{hostId}, graphics card {card}";
                var detail = "No Thinking pool model on this card yet. Add one and it joins the pool as a machine of its own, at full " +
                    "speed beside the computer's other cards." + (cannot is null ? "" : $" {cannot}");
                yield return new(host, hostId, card, title, detail, cannot);
            }
        }
    }

    /// <summary>The paired computers that aren't members.</summary>
    internal PairedHost[] Others => [.. Hosts.Where(h => !Pool.All.Any(m => m is { Place: DeepThinkingPlace.Host } && m.HostId == h.HostId))];

    /// <summary>A paired computer that isn't in the pool, as its row shows it.</summary>
    /// <param name="Own">The model its Thinking pool role runs, or null without the role.</param>
    /// <param name="Usable">Whether it has a Thinking model the pool can use.</param>
    /// <param name="Detail">Its line (DeepThinkingHost-name): what it offers and why it isn't in.</param>
    /// <param name="SharedCard">The warning that its Thinking pool model shares a graphics card, or null.</param>
    internal sealed record ComputerLine(PairedHost Host, HostCheck? Check, string? Own, bool Usable, string Detail, string? SharedCard);

    /// <summary>A paired computer that isn't in the pool: what it offers and why it isn't in (kept out, its Ollama does this PC's
    /// Thinking, no Thinking model, not reachable), or that it joins by itself at its next check (<see cref="ThinkingPoolAutoJoin"/>).</summary>
    internal ComputerLine Computer(PairedHost host)
    {
        var check = services.CheckOf(host.HostId);
        var own = check?.Offers?.GetValueOrDefault(HostRoles.DeepThinking);
        var ollama = check?.Offers?.GetValueOrDefault(HostRoles.Ollama);
        var thinksThere = ThinkingHost == host.HostId;
        var usable = own is not null || ollama is not null && !thinksThere;
        // What the auto-join rule does with it at its next check: join, or why not.
        var rule = check is { Reachable: true, Routes: { } routes } ? ThinkingPoolAutoJoin.For(Pool, MainWindow.PoolHost(host, routes),
            ThinkingHost, Sharing, WorkSharingRoster.Device, services.IsHost) : null;
        string Joins(string offer) => rule is null or { Changed: true } ? $"{offer} It joins the pool by itself at its next check." : $"{offer} {rule.Why}";
        var detail = Pool.Left(host.HostId)
            ? (own is not null ? $"Its Thinking pool role runs {own}. " : ollama is not null ? $"Ollama runs {ollama}. " : "") +
                "Kept out of the pool, because you unticked it. Tick In the pool to add it again."
            : own is not null ? Joins($"Its Thinking pool role runs {own}.")
            : ollama is not null && thinksThere ? $"Its Ollama ({ollama}) does Thinking for the conversation. Add the Thinking pool role " +
                "there to join the pool beside it."
            : ollama is not null ? Joins($"Ollama runs {ollama}.")
            : check?.Reachable == true ? "Add the Thinking pool role there to join the pool."
            : check?.Reachable == false ? "Not reachable right now." : "Not checked yet.";
        var shared = check?.Reachable == true ? DeepThinkingFit.SharedCard(host.HostId, services.HardwareOf(host.HostId), check.Offers) : null;
        return new(host, check, own, usable, detail, shared);
    }

    /// <summary>The pool's guidance under the list (ThinkingPoolGuidance).</summary>
    internal string Guidance => string.Join(" ", ThinkingJobBoard.Guidance([.. Statuses.Select(m => m with
    {
        Slots = Places.FirstOrDefault(p => p.Id == m.Id)?.Slots ?? 0
    }).Where(m => m.Slots > 0)]));

    /// <summary>The pool's warnings under the list (ThinkingPoolWarnings): likely slowdowns.</summary>
    internal IReadOnlyList<string> Warnings => ThinkingPoolWarnings.For(PoolPlan, Routes, HostRouteGpus.For);

    /// <summary>The live floor line under the list (ThinkingPoolLiveFloor).</summary>
    internal string LiveFloor => LiveFloorLine(Pool, Places.Length,
        [.. Places.Where(Resources.Shares).Select(p => p.Name).Distinct(StringComparer.Ordinal)]);

    /// <summary>Companion › Thinking pool's live floor line (MCP reads it as ThinkingPoolLiveFloor): which members wait while you
    /// talk with Martlet because they share the conversation's computer or graphics card (<paramref name="sharing"/>, by name),
    /// or that none does. Empty with no member and no conversation model in its place.</summary>
    internal static string LiveFloorLine(ThinkingPoolSettings pool, int members, IReadOnlyList<string> sharing)
    {
        if (members == 0)
            return pool.UseConversationModelWhenEmpty
                ? "While you talk with Martlet, thinking longer and research on the conversation model wait; they stop and go on later when you start."
                : "";
        if (sharing.Count == 0) return "No member shares the conversation's computer, so pool work never waits while you talk with Martlet.";
        var names = sharing.Count == 1 ? sharing[0] : string.Join(", ", sharing.Take(sharing.Count - 1)) + " and " + sharing[^1];
        return $"While you talk with Martlet, {names} start{(sharing.Count == 1 ? "s" : "")} no new pool work, because " +
            $"{(sharing.Count == 1 ? "it shares" : "they share")} the conversation's computer. Summaries, remembering and thinking longer " +
            "there stop and go on later; the judges still run there. Other members never wait.";
    }

    // ---------- Add a machine ----------

    /// <summary>The paired computers kept out of the pool by the owner, for the Another of your computers form (DeepThinkingAutoJoin).</summary>
    internal string AutoJoinText
    {
        get
        {
            var left = Pool.LeftByOwner;
            return "Your paired computers with a Thinking model join the list by themselves, on: their Thinking pool role " +
                "(one machine for each graphics card with a Thinking pool model), or their Ollama when it doesn't do this PC's Thinking. " +
                "Untick On on a machine to stop using it; it stays off until you tick it again." +
                (left.Count == 0 ? "" : $" Kept out now: {string.Join(", ", left)}.");
        }
    }

    /// <summary>Thinking's own model when it runs in Ollama on this PC, or null: a model in Ollama on this PC can't be the same one.</summary>
    internal string? LocalBeside => MainWindow.IsLocalOllama(Route) ? Route!.ModelId : null;

    /// <summary>The models offered for a model in Ollama on this PC: those Ollama has, then those Martlet recommends.</summary>
    internal string[] LocalChoices => (services.OllamaModels ?? []).Concat(MainWindow.LocalChatModels.Select(m => m.Id)).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The model the A model in Ollama on this PC form starts with: one that isn't Thinking's and isn't in the list yet.</summary>
    internal string LocalStart(IReadOnlyList<string> choices) => choices.FirstOrDefault(id => !OllamaSideBySide.Same(id, LocalBeside) &&
        !Pool.All.Any(m => m.Place == DeepThinkingPlace.Endpoint && m.Origin == MainWindow.LocalOllamaBaseUrl && m.ModelId == id)) ?? "";

    /// <summary>What Ollama on this PC has (DeepThinkingLocalStatus).</summary>
    internal string LocalState => services.OllamaModels is not { } models ? "Check Ollama to see which models are downloaded."
        : models.Count == 0 ? "Ollama is running, but no model is downloaded yet."
        : $"Downloaded: {string.Join(", ", models)}.";

    /// <summary>The warning that a model in Ollama on this PC shares the graphics card with Thinking's (DeepThinkingLocalShare), or empty.</summary>
    internal string LocalShare
    {
        get
        {
            var machine = services.Machine;
            return DeepThinkingFit.SharedCard("This PC", LocalBeside,
                ReferenceEquals(machine, MachineInfo.Unknown) ? 1 : DeepThinkingFit.DedicatedCards(machine.Gpus.Select(g => (g.Name, g.MemoryGb)))) ?? "";
        }
    }

    /// <summary>The member in <paramref name="members"/> on <paramref name="url"/> with a key of its own, or null.</summary>
    internal static DeepThinkingSettings? SavedKey(IReadOnlyList<DeepThinkingSettings> members, string url) =>
        members.FirstOrDefault(m => m.Place == DeepThinkingPlace.Endpoint && m.Origin == url && m.CredentialId is not null);

    /// <summary>The key line of the A cloud provider or server form (DeepThinkingKeyStatus) for <paramref name="provider"/> at
    /// <paramref name="url"/>.</summary>
    internal string KeyStatus(MainWindow.CloudProvider provider, string url) => SavedKey(Pool.All, url) is { } saved
            ? $"Its {provider.Name} key is saved (for {saved.Describe()}). Leave this empty to use it again, or paste a new key."
        : Route is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && Route.Origin == url
            ? "Leave this empty to use Thinking's key for the same provider, or paste another key."
        : provider.NeedsKey ? $"Paste your {provider.Name} API key. Martlet saves it in Windows Credential Manager."
        : "Add a key only if your server needs one.";

    /// <summary>The consent box's text for <paramref name="provider"/>.</summary>
    internal static string CloudConsent(MainWindow.CloudProvider provider) =>
        $"I add {provider.Name} to the Thinking pool. When it takes a job, the job's text (for a think, the recent conversation and " +
        "the task) goes there, and requests (with their hidden thinking) may cost money.";

    // ---------- Backup for slow replies and Busy pool ----------

    /// <summary>Backup for slow replies' status line (ThinkingPoolBackupStatus).</summary>
    internal string BackupStatus
    {
        get
        {
            var answering = Pool.Members.Where(m => Pool.Answers(m.Key)).Select(m => m.Describe()).ToArray();
            var words = services.FirstWords;
            return LiveConversationController.BackupLine(Pool, answering, words is { Count: > 0 } ? words.Delay : null, words?.Count ?? 0);
        }
    }

    /// <summary>Busy pool's status line (ThinkingPoolPriorityStatus).</summary>
    internal string PriorityStatus => PriorityLine(Pool, services.PoolStatusNow());

    /// <summary>Companion › Thinking pool's line of requests: whether higher-priority requests may stop lower ones, after how many
    /// stops a stopped request's priority goes up, and how many times a failed request is tried again; with
    /// <paramref name="status"/>, when the pool runs, how many requests were stopped, raised and tried again since Martlet started.</summary>
    internal static string PriorityLine(ThinkingPoolSettings pool, ThinkingPoolStatus? status)
    {
        var rules = pool.PreemptLowerPriority
            ? $"More important requests may stop less important ones; a request stopped {Times(pool.RaisePriorityAfterStops)} becomes more important."
            : "Requests wait for a free slot and never stop each other.";
        var retry = pool.RetriesOnFailure switch
        {
            0 => " A failed request isn't tried again.",
            1 => " A failed request is tried once more.",
            var n => $" A failed request is tried up to {n} more times."
        };
        var counts = status is null ? "" :
            $" Since Martlet started: {status.StoppedForPriority} stopped, {status.Raised} made more important, {status.Retried} tried again.";
        return rules + retry + counts;

        static string Times(int n) => n == 1 ? "once" : $"{n} times";
    }

    // ---------- Thinking longer and Web research ----------

    /// <summary>Whether Martlet can think longer at all: Thinking hands the task off with a tool, so it needs a route that does
    /// function calling, and Deep thinking needs a model of its own to think on.</summary>
    internal (string Text, bool Problem) ThinkLongerStatus(ThinkLongerSettings settings)
    {
        var route = Route;
        if (!settings.On) return ("Off. Martlet answers everything right away and never thinks in the background.", false);
        if (route is null) return ("On. Set up Thinking so Martlet can use it.", true);
        if (route.RouteType is not (SetupRouteType.OpenAi or SetupRouteType.ChatCompletions))
            return ("On, but your Thinking model can't use tools here (a Martlet host's model), so it can't hand a task off. Use " +
                "OpenAI or a Chat Completions endpoint (such as Ollama on this PC) for Thinking.", true);
        if (services.ToolsRefused(route))
            return ($"On, but {route.ModelId} turned down tools, so it can't hand a task off. Choose a Thinking model that can use tools.", true);
        if (!LongPlan.Available)
            return ($"On, but the Thinking pool has nowhere to think in parallel, so Martlet doesn't offer to think things over. {LongPlan.Why}", true);
        return ($"On. When a task needs it, Martlet says it'll think it over and works on it in the background ({settings.HowHard} " +
            "effort, no time limit, no limit on how many) while you keep talking, " +
            (settings.When == ThinkDelivery.WhenFree ? "then brings it up as soon as it's free." : "then brings it up when you talk next."), false);
    }

    /// <summary>Web research's status line (WebResearchStatus) and whether it is a problem.</summary>
    internal (string Text, bool Problem) WebResearchStatus()
    {
        var settings = ThinkLongerChoices;
        if (!settings.On) return ("Off, because thinking longer is off. Turn it on under Thinking longer above to use web research.", false);
        if (!settings.WebResearchOn) return ("Off. Martlet never searches the web or reads web pages.", false);
        var (thinking, problem) = ThinkLongerStatus(settings);
        if (problem)
        {
            var why = thinking.StartsWith("On, but ", StringComparison.Ordinal) ? thinking[8..]
                : thinking.StartsWith("On. ", StringComparison.Ordinal) ? thinking[4..] : thinking;
            return ("On, but Martlet can't look things up yet: " + char.ToLowerInvariant(why[0]) + why[1..], true);
        }
        return ($"On. When you ask, Martlet researches it in the background (up to {WebResearch.Budget.Searches} searches and " +
            $"{WebResearch.Budget.Pages} pages, no time limit), then offers the report.", false);
    }
}
