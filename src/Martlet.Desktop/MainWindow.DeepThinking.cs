using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking pool (an optional extra): one list of the machines that do Martlet's background work (thinking
/// longer, research, screen and sound summaries, the judges and other helpers). There is no separate on/off choice: the list is
/// the pool. Each row is one machine (one graphics card of a paired computer, a model in Ollama on this PC or an endpoint) with
/// its On box (<c>ThinkingPoolOn-&lt;n&gt;</c>), its model, slots and rules (Quick jobs, Long jobs, Backup for slow replies); a
/// machine that is off keeps its settings and takes no jobs. With no machine on, the pool is off. Below the list: a paired
/// computer's graphics card without a Thinking pool model yet, paired computers that aren't in the pool (why, and In the pool),
/// and Add a machine (another of your computers, a model in Ollama on this PC, a cloud provider or server). Then Runs on,
/// Thinking longer (its own on/off, shared with your computers), Web research, Backup for slow replies and Busy pool. The
/// members are this PC's own choice (thinking-pool.json). The conversation keeps its own Thinking model, which is never a member,
/// so its replies start as fast as they can (<see cref="DeepThinkingPlan"/>).</summary>
public partial class MainWindow
{
    private SideBySideFit? deepLocalFit;
    private string? deepLocalFitModel;
    private static readonly ThinkEffort[] ThinkEfforts = [ThinkEffort.Medium, ThinkEffort.High];
    private static readonly ThinkDelivery[] ThinkDeliveries = [ThinkDelivery.WhenFree, ThinkDelivery.NextMessage];

    private static readonly IReadOnlyList<CloudProvider> DeepThinkingProviders =
    [
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => new CloudProvider(e.Name, e.BaseUrl, true, e.DefaultModelId, true)),
        new("OpenAI", OpenAiChatBaseUrl, true, OpenAiTextGenerationCatalog.DefaultModelId, true),
        CustomCloud
    ];

    private void RenderDeepThinkingTab(Panel page)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var poolSettings = ThinkingPoolSettings.Load(store?.DataDirectory);
        var routes = homeSettings?.Setup?.Routes ?? [];
        var sharing = WorkSharingRoster.Settings(store?.DataDirectory);
        var poolPlan = poolSettings.Plan(routes, sharing, WorkSharingRoster.Device);
        // Thinking longer, research and a song's lyrics go only to the members that take long jobs.
        var longJobs = poolSettings.ForLongJobs();
        var plan = longJobs.Plan(routes, sharing, WorkSharingRoster.Device).Plan;
        var on = ThinkLongerSettings.Of(homeSettings?.Generation).On;
        var hosts = NetworkMap.Hosts(Inputs());

        var now = new TextBlock { Text = DeepThinkingNow(longJobs, route, plan, on), FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "DeepThinkingNow");
        var why = Note(on ? plan.Why : "Thinking longer is off. Turn it on under Thinking longer below.", new Thickness(0, 4, 0, 0));
        if (on && !plan.Available) why.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(why, "DeepThinkingParallel");
        page.Children.Add(Card(Heading("Now"), now, why));

        // The list is the pool: no machine on means the pool is off. Add a machine is at the end of the list.
        page.Children.Add(MachinesCard(poolSettings, poolPlan, longJobs, plan, routes, route, hosts, on));
        page.Children.Add(RunsOnCard(poolSettings));
        page.Children.Add(ThinkLongerCard(homeSettings?.Generation?.ThinkLonger, route, plan, on));
        page.Children.Add(WebResearchCard(ThinkLongerSettings.Of(homeSettings?.Generation), route, plan));
        page.Children.Add(BackupThinkingCard(poolSettings));
        page.Children.Add(PriorityCard(poolSettings));
    }

    /// <summary>The machine list, which is the pool: the conversation's own model (never in the pool), each machine that is on and
    /// each that is off (<see cref="MemberRow"/>), each graphics card of a computer in the pool without a Thinking pool model yet
    /// (<see cref="SpareCardRows"/>) and each paired computer that isn't a member (<see cref="ComputerRow"/>); then the pool's
    /// guidance, warnings and live floor, and Add a machine.</summary>
    private Border MachinesCard(ThinkingPoolSettings pool, DeepThinkingPool plan, ThinkingPoolSettings longJobs, DeepThinkingPlan longPlan,
        IReadOnlyList<SetupRoute> routes, SetupRoute? route, IReadOnlyList<PairedHost> hosts, bool on)
    {
        var stack = new List<UIElement>
        {
            Heading("Machines"),
            HelpTip.Explain("This list is the Thinking pool. Each machine that is on (a graphics card on one of your computers, a model " +
                "in Ollama on this PC, or a cloud provider or server) does Martlet's background work; untick On to stop using one and " +
                "keep its settings. With no machine on, the pool is off. Quick jobs are the judges (are you done talking, did you " +
                "interrupt) and the screen and sound summaries. Long jobs are thinking longer, research, a song's lyrics and the " +
                "other helpers. Each job goes to a free machine that takes it, the one that shares least with the conversation " +
                "first. Tick what each machine may do.", new Thickness(0, 0, 0, 8), "ThinkingPool", "the Thinking pool")
        };

        var places = ThinkLonger.Places(plan, BackgroundDuties.Of(store?.DataDirectory), choices: pool).Where(p => p.Id != "thinking").ToArray();
        var all = pool.All;
        var members = all.Select(m => new ThinkingPoolMemberStatus(m.Key, m.Computer(null), m.ThinksAtOnce, 0,
            conversation?.PoolCan(m) ?? ThinkingCapability.Text, 0)).ToArray();
        var summary = Note(PoolSummary(pool, places.Sum(p => p.Slots)), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(summary, "ThinkingPoolSummary");
        stack.Add(summary);
        stack.Add(ConversationRow(pool, route));
        var resources = LiveResources.For(routes, HostRouteGpus.For);
        // Members whose providers limit requests now (a busy free cloud endpoint): they wait, or run fewer jobs at once, for a while.
        var cooling = conversation?.PoolCooling() ?? [];
        for (var i = 0; i < all.Count; i++)
        {
            var member = all[i];
            stack.Add(MemberRow(pool, plan, member, i, members[i].Can, places.FirstOrDefault(p => p.Id == member.Key), resources, hosts,
                route, cooling.FirstOrDefault(c => c.Id == member.Key)));
        }
        stack.AddRange(SpareCardRows(pool, hosts));
        var others = hosts.Where(h => !all.Any(m => m is { Place: DeepThinkingPlace.Host } && m.HostId == h.HostId)).ToArray();
        if (others.Length > 0)
        {
            var heading = new TextBlock { Text = "Paired computers not in the pool", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) };
            stack.Add(heading);
            var thinkingHost = NetworkMap.ThinkingHost(homeSettings);
            var sharing = WorkSharingRoster.Settings(store?.DataDirectory);
            foreach (var host in others) stack.Add(ComputerRow(pool, host, thinkingHost, sharing));
        }

        var guidance = Note(string.Join(" ", ThinkingJobBoard.Guidance([.. members.Select(m => m with
        {
            Slots = places.FirstOrDefault(p => p.Id == m.Id)?.Slots ?? 0
        }).Where(m => m.Slots > 0)])), new Thickness(0, 4, 0, 4));
        AutomationProperties.SetAutomationId(guidance, "ThinkingPoolGuidance");
        stack.Add(guidance);
        var warnings = ThinkingPoolWarnings.For(plan, routes, HostRouteGpus.For);
        var warning = Note(string.Join(" ", warnings), new Thickness(0, 4, 0, 4));
        warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        warning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(warning, "ThinkingPoolWarnings");
        stack.Add(warning);
        // The live floor: which members wait while you talk with Martlet (they share the conversation's computer).
        var floorLine = Note(LiveFloorLine(pool, places.Length, [.. places.Where(resources.Shares).Select(p => p.Name).Distinct(StringComparer.Ordinal)]),
            new Thickness(0, 4, 0, 4));
        floorLine.Visibility = floorLine.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(floorLine, "ThinkingPoolLiveFloor");
        stack.Add(floorLine);
        stack.Add(Note("Martlet sets the order by itself: quick jobs go first, and when the pool has two or more slots, long jobs " +
            "leave one slot that takes quick jobs free for them.", new Thickness(0, 4, 0, 0)));

        // Add a machine: each kind opens its own form, and what it adds shows in the list above, on.
        stack.Add(new TextBlock { Text = "Add a machine", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        stack.Add(Fold.Create("Another of your computers", "ThinkingPoolAddComputer", false, [.. DeepComputersPanel(pool, hosts)]));
        stack.Add(Fold.Create("A model in Ollama on this PC", "ThinkingPoolAddLocal", false, [.. DeepLocalPanel(all, route)]));
        stack.Add(Fold.Create("A cloud provider or server", "ThinkingPoolAddCloud", false, [.. DeepCloudPanel(all, route)]));
        return Card([.. stack]);
    }

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

    /// <summary>The conversation's own Thinking model, first in the machine list. It is never in the pool, so no pool job waits in
    /// front of a reply and its model stays ready for the conversation. Its box lets thinking longer and research run on it when
    /// no machine in the pool takes long jobs.</summary>
    private UIElement ConversationRow(ThinkingPoolSettings pool, SetupRoute? route)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        text.Children.Add(new TextBlock { Text = "The conversation" + (route is null ? "" : $" ({route.ModelId})"), FontSize = 15,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = route is null ? "Thinking isn't set up yet."
            : "Answers you. Never in the pool: no pool job waits in front of a reply, and its model stays ready for the conversation, " +
              "so replies start as fast as they can.";
        var line = Note(detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(line, $"The conversation: {detail}");
        AutomationProperties.SetAutomationId(line, "ThinkingPoolConversation");
        text.Children.Add(line);
        const string label = "Do thinking longer and research here when no machine in the pool takes long jobs";
        var fallback = new CheckBox
        {
            IsChecked = pool.UseConversationModelWhenEmpty, Margin = new Thickness(0, 6, 0, 0),
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            ToolTip = "Thinking longer and research then run on the conversation's own Thinking model when its provider answers " +
                "several requests at once. Quick jobs then use their own simple rules."
        };
        AutomationProperties.SetAutomationId(fallback, "ThinkingPoolUseConversationModel");
        AutomationProperties.SetName(fallback, label);
        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        void Use(bool use) => SavePoolAsync(p => p with { UseConversationModelWhenEmpty = use },
            use ? "The conversation model is used while no machine in the pool takes long jobs."
                : "The conversation model is no longer used for background work.").Forget();
        fallback.Checked += (_, _) => Use(true);
        fallback.Unchecked += (_, _) => Use(false);
        text.Children.Add(fallback);
        return text;
    }

    /// <summary>One machine in the list: whether it is on (its On box; off keeps its settings and takes no jobs), what it reads and
    /// writes, its slots and whether it can run now; what may slow it or cost money (Waits while you talk, Costs money, Offline);
    /// its model (<see cref="ModelChoice"/>), its rules (Quick jobs, Long jobs, Backup for slow replies) and smarts; and its slots
    /// and Remove (an endpoint). A paired computer's Thinking pool model and slots are that computer's role settings: choosing
    /// another sets them there, and each check of that computer takes the role's slot count again.</summary>
    private UIElement MemberRow(ThinkingPoolSettings pool, DeepThinkingPool plan, DeepThinkingSettings member, int i, ThinkingCapability can,
        BackgroundPlace? place, LiveResources resources, IReadOnlyList<PairedHost> hosts, SetupRoute? thinking, ThinkingPoolCooling? cooling = null)
    {
        var key = member.Key;
        var name = member.Describe();
        var on = !pool.IsOff(key);
        var hostId = member is { Place: DeepThinkingPlace.Host, HostId: { } id } ? id : null;
        // A host with a Thinking pool model on each of several graphics cards has one member per card, each on or off by itself.
        var card = member.Card;
        var suffix = card > 1 ? $"-gpu{card}" : "";
        var host = hostId is null ? null : hosts.FirstOrDefault(h => h.HostId == hostId);
        var check = hostId is null ? null : hostChecks.GetValueOrDefault(hostId);
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
            (on && plan.Find(key) is { Plan.Available: false } spot ? $" Can't run now: {spot.Plan.Why}" : "") +
            // A paired computer that stopped answering stays a member: its slots come back by themselves.
            (on && offline ? $" Offline now: its slot{(slots == 1 ? "" : "s")} come{(slots == 1 ? "s" : "")} back when it answers again." : "") +
            // Its provider limited requests: it waits, or runs fewer jobs at once, and its jobs wait in line instead of failing.
            (cooling is not null ? $" {cooling.Describe(DateTimeOffset.UtcNow)}." : "");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Opacity = on ? 1 : 0.75 };
        text.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var line = Note(detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(line, $"{name}: {detail}");
        AutomationProperties.SetAutomationId(line, $"ThinkingPoolMember-{i}");
        text.Children.Add(line);
        string[] badges = [.. place is not null && resources.Shares(place) ? ["Waits while you talk"] : Array.Empty<string>(),
            .. paid ? ["Costs money"] : Array.Empty<string>(), .. offline ? ["Offline"] : Array.Empty<string>(),
            .. cooling is not null ? ["Limiting requests"] : Array.Empty<string>()];
        var badgeLine = Note(string.Join("  \u00b7  ", badges), new Thickness(0, 2, 0, 0));
        badgeLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        badgeLine.Visibility = badges.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        badgeLine.ToolTip = "Waits while you talk: it shares the conversation's computer, so it starts no new pool work while you talk. " +
            "Costs money: a paid cloud provider. Offline: its computer doesn't answer now. Limiting requests: its provider asked " +
            "Martlet to slow down, so it gets fewer jobs for a while and its jobs wait instead of failing.";
        AutomationProperties.SetName(badgeLine, $"{name}: {badgeLine.Text}");
        AutomationProperties.SetAutomationId(badgeLine, $"ThinkingPoolBadges-{i}");
        text.Children.Add(badgeLine);
        // One graphics card for each Thinking model: a pool model on the card of this computer's Thinking shares it.
        if (hostId is not null && check?.Reachable == true &&
            DeepThinkingFit.SharedCard(member, HardwareStore?.Find(hostId), check) is { } shared)
        {
            var warning = Note(shared, new Thickness(0, 2, 0, 0));
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetName(warning, $"{hostId}: {shared}");
            AutomationProperties.SetAutomationId(warning, "DeepThinkingShare-" + hostId + suffix);
            text.Children.Add(warning);
        }

        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        CheckBox Rule(string id, string label, bool value, string tip, Action<bool> save)
        {
            var box = new CheckBox { IsChecked = value, Content = label, ToolTip = tip, Margin = new Thickness(0, 6, 18, 0) };
            AutomationProperties.SetAutomationId(box, $"{id}-{i}");
            AutomationProperties.SetName(box, $"{name}: {label}");
            box.Checked += (_, _) => save(true);
            box.Unchecked += (_, _) => save(false);
            return box;
        }
        var rules = new WrapPanel();
        rules.Children.Add(Rule("ThinkingPoolQuick", "Quick jobs", pool.TakesQuickJobs(key),
            "The judges (are you done talking, did you interrupt) and the screen and sound summaries. They are short and need a fast answer.",
            take => SavePoolAsync(p => p.WithJobs(key, quick: take), take ? $"{name} takes quick jobs again."
                : $"{name} no longer takes quick jobs: the judges and the summaries go to other machines.").Forget()));
        rules.Children.Add(Rule("ThinkingPoolLong", "Long jobs", pool.TakesLongJobs(key),
            "Thinking longer, research, a song's lyrics, touch zones, remembering, naming and check-ins. They can run for minutes.",
            take => SavePoolAsync(p => p.WithJobs(key, @long: take), take ? $"{name} takes long jobs again."
                : $"{name} no longer takes long jobs: thinking longer, research and the other long jobs go to other machines.").Forget()));
        rules.Children.Add(Rule("ThinkingPoolAnswers", "Backup for slow replies" + (paid ? " (each request may cost money)" : ""), pool.Answers(key),
            "With Backup for slow replies on (below), a reply that is slow to start sends the same request here too, and the first " +
                "to start gives the reply. Choose a machine with the same model as the conversation, or a similar one.",
            allow => SavePoolAsync(p => p.WithAnswers(key, allow), allow
                ? $"{name} may answer for the conversation when a reply is slow to start."
                : $"{name} no longer answers for the conversation.").Forget()));
        // Only an external member asks first: this PC and paired computers always may receive pictures and recordings.
        if (ThinkingPoolSettings.IsExternal(member))
            rules.Children.Add(Rule("ThinkingPoolMedia", "May receive pictures and recordings", pool.MayReceiveMedia(member),
                "Check-ins, screen and sound summaries and other pool jobs can include a screenshot or a recording. This machine is " +
                    "outside this PC and your paired computers, so it gets them only when this is ticked. Text-only jobs go to it either way.",
                allow => SavePoolAsync(p => p.WithMedia(key, allow), allow
                    ? $"{name} may now receive pictures and recordings."
                    : $"{name} no longer receives pictures and recordings: jobs with them go to other machines or use their simple rules.").Forget()));
        text.Children.Add(rules);
        // How smart its model is, for Runs on (below): Martlet's guess from the model name, or the owner's choice.
        var guess = ThinkingSmartsGuess.From(member.ModelId);
        var smarts = new ComboBox
        {
            Width = 190, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = new[] { $"Guessed: {guess}", "Fast", "Standard", "Smart" },
            SelectedIndex = pool.Smarts.TryGetValue(key, out var chosenSmarts) ? 1 + (int)chosenSmarts : 0,
            ToolTip = "How smart this machine's model is. Martlet guesses it from the model name: small models (about 4B or less) are " +
                "Fast, large ones (about 26B or more, and big cloud models) are Smart. Runs on (below) uses it: Prefer smart and " +
                "Smart only send jobs to the smartest machines."
        };
        AutomationProperties.SetAutomationId(smarts, $"ThinkingPoolSmarts-{i}");
        AutomationProperties.SetName(smarts, $"{name}: smarts");
        smarts.SelectionChanged += (_, _) =>
        {
            ThinkingSmarts? level = smarts.SelectedIndex <= 0 ? null : (ThinkingSmarts)(smarts.SelectedIndex - 1);
            SavePoolAsync(p => p.WithSmarts(key, level), level is { } set ? $"{name} is {set} now."
                : $"{name} uses Martlet's guess again: {guess}.").Forget();
        };
        text.Children.Add(TerminalRow("Smarts", smarts));
        if (ModelChoice(member, i, name, host, check, thinking) is { } model) text.Children.Insert(text.Children.Count - 1, model);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // On: the list is the pool. Unticking keeps the machine and its settings in the list but gives it no jobs (a paired
        // computer's card then never joins again by itself); ticking it uses it again.
        const string onLabel = "On";
        var onBox = new CheckBox
        {
            Content = onLabel, IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Untick to stop using this machine for background work; it keeps its settings. Tick it to use it again. " +
                "With no machine on, the Thinking pool is off."
        };
        AutomationProperties.SetAutomationId(onBox, $"ThinkingPoolOn-{i}");
        AutomationProperties.SetName(onBox, $"{name}: {onLabel}");
        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        onBox.Checked += (_, _) => SavePoolAsync(p => p.TurnOn(key), $"{name} is on: it takes jobs again.").Forget();
        onBox.Unchecked += (_, _) => SavePoolAsync(p => p.TurnOff(key),
            $"{name} is off: it takes no jobs and keeps its settings. Tick On to use it again.").Forget();
        buttons.Children.Add(onBox);
        if (member.OnHostRole && hostId is not null)
        {
            // Its Thinking pool role's slots: the role's settings on that computer (card 2's role for card 2...), set there.
            var role = SelfHostSetup.DeepThinkingRoleKind(card);
            var choices = Enumerable.Range(1, Math.Max(SelfHostSetup.DeepThinkingMaximumSlots, slots)).ToArray();
            var choice = new ComboBox
            {
                Width = 110, ItemsSource = choices.Select(n => $"{n} slot{(n == 1 ? "" : "s")}").ToArray(), SelectedIndex = slots - 1,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = host is not null && check is { Reachable: true } && check.Offers?.ContainsKey(role) == true && ChangesRolesOn(host),
                ToolTip = $"How many jobs this graphics card runs at once. Choosing another number sets it on {hostId}: a run window " +
                    "shows the change. Each slot beyond the first needs room on the card for another job's context."
            };
            AutomationProperties.SetAutomationId(choice, $"ThinkingPoolSlots-{i}");
            AutomationProperties.SetName(choice, $"Slots on {name}");
            choice.SelectionChanged += (_, _) =>
            {
                var chosen = choice.SelectedIndex + 1;
                if (chosen < 1 || chosen == slots || host is null) return;
                ChangeDeepRoleAsync(host, card, DeepThinkingFit.Tag(member.ModelId ?? ""), chosen).Forget();
            };
            buttons.Children.Add(choice);
        }
        else if (hostId is null)
        {
            var choice = new ComboBox { Width = 110, ItemsSource = Enumerable.Range(1, DeepThinkingSettings.MaxPlaces).Select(n => $"{n} slot{(n == 1 ? "" : "s")}").ToArray(),
                SelectedIndex = slots - 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(choice, $"ThinkingPoolSlots-{i}");
            AutomationProperties.SetName(choice, $"Slots on {name}");
            choice.SelectionChanged += (_, _) =>
            {
                var chosen = choice.SelectedIndex + 1;
                SavePoolAsync(p => p with
                {
                    Members = [.. p.Members.Select(m => m.Key == key ? m with { Slots = chosen } : m)],
                    OffMembers = [.. p.OffMembers.Select(m => m.Key == key ? m with { Slots = chosen } : m)]
                }, $"{name} now takes {chosen} job{(chosen == 1 ? "" : "s")} at once.").Forget();
            };
            buttons.Children.Add(choice);
            var remove = PageButton("Remove", () => RemovePoolMemberAsync(key, name).Forget(), id: $"ThinkingPoolRemove-{i}");
            AutomationProperties.SetName(remove, $"Remove {name} from the Thinking pool");
            buttons.Children.Add(remove);
        }
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A machine's model, chosen in its row (<c>ThinkingPoolModel-&lt;n&gt;</c>): for a graphics card on a paired computer,
    /// the models its Thinking pool role offers (choosing one sets it up there, in a run window); for a model in Ollama on this PC,
    /// the models Ollama has and Martlet recommends; for a cloud provider or server, the model ID typed (saved when you press Enter
    /// or leave the box). Null for a paired computer's member on its Ollama, whose model is the computer's conversation model.</summary>
    private UIElement? ModelChoice(DeepThinkingSettings member, int i, string name, PairedHost? host, HostCheck? check, SetupRoute? thinking)
    {
        var key = member.Key;
        var current = member.ModelId ?? "";
        if (member is { OnHostRole: true, HostId: { } hostId })
        {
            var card = member.Card;
            var role = SelfHostSetup.DeepThinkingRoleKind(card);
            var now = DeepThinkingFit.Tag(current);
            var choices = DeepThinkingFit.Models.Contains(now) ? DeepThinkingFit.Models : [now, .. DeepThinkingFit.Models];
            var box = new ComboBox
            {
                Width = 190, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices,
                SelectedItem = now,
                IsEnabled = host is not null && check is { Reachable: true } && check.Offers?.ContainsKey(role) == true && ChangesRolesOn(host),
                ToolTip = $"The model this graphics card runs. Choosing another one sets it up on {hostId}: a run window shows the " +
                    "download, and the old model keeps working until the new one is ready."
            };
            AutomationProperties.SetAutomationId(box, $"ThinkingPoolModel-{i}");
            AutomationProperties.SetName(box, $"{name}: model");
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is string chosen && chosen != now && host is not null)
                    ChangeDeepRoleAsync(host, card, chosen, null, member.ThinksAtOnce).Forget();
            };
            return TerminalRow("Model", box);
        }
        if (member.Place != DeepThinkingPlace.Endpoint) return null;
        if (member.Origin == LocalOllamaBaseUrl)
        {
            var beside = IsLocalOllama(thinking) ? thinking!.ModelId : null;
            string[] choices = [.. new[] { current }.Concat(ollamaModels ?? []).Concat(LocalChatModels.Select(m => m.Id))
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
            var box = new ComboBox
            {
                Width = 190, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices,
                SelectedItem = current,
                ToolTip = "The model in Ollama on this PC that does this machine's jobs. It can't be the conversation's own model, which " +
                    "can't think something over while it answers you."
            };
            AutomationProperties.SetAutomationId(box, $"ThinkingPoolModel-{i}");
            AutomationProperties.SetName(box, $"{name}: model");
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is not string chosen || chosen == current) return;
                if (beside is not null && OllamaSideBySide.Same(chosen, beside))
                {
                    ActionText.Text = $"{chosen} is Thinking's own model, which can't think something over while it answers you. Choose another model.";
                    box.SelectedItem = current;
                    return;
                }
                SavePoolAsync(p => p.WithModel(key, chosen), $"{name} now uses {chosen}." +
                    (ollamaModels is { } known && !LocalOllama.Serves(known, chosen) ? $" Download {chosen} on Thinking to use it." : "")).Forget();
            };
            return TerminalRow("Model", box);
        }
        var typed = new TextBox { Width = 260, MaxLength = 128, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Text = current, ToolTip = "The model ID at this provider. Press Enter or leave the box to save it." };
        AutomationProperties.SetAutomationId(typed, $"ThinkingPoolModel-{i}");
        AutomationProperties.SetName(typed, $"{name}: model");
        var saved = false;
        void Commit()
        {
            var chosen = typed.Text.Trim();
            if (saved || chosen.Length == 0 || chosen == current) return;
            saved = true;
            SavePoolAsync(p => p.WithModel(key, chosen), $"{name} now uses {chosen}.").Forget();
        }
        typed.LostKeyboardFocus += (_, _) => Commit();
        typed.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        return TerminalRow("Model", typed);
    }

    /// <summary>Sets a paired computer's Thinking pool model on graphics card <paramref name="card"/> to <paramref name="model"/>
    /// with <paramref name="slots"/> jobs at once, in a run window (the role's own settings there). Null slots: what fits for the
    /// new model, at most <paramref name="before"/>. Once it is done, the next check of that computer follows the new model and slots.</summary>
    private async Task ChangeDeepRoleAsync(PairedHost host, int card, string model, int? slots, int before = int.MaxValue)
    {
        if (closing) return;
        var role = SelfHostSetup.DeepThinkingRoleKind(card);
        var fit = DeepThinkingFit.Recommend(HardwareStore?.Find(host.HostId), hostChecks.GetValueOrDefault(host.HostId)?.Offers, card);
        var fits = fit.TryGetValue($"choice.{DeepThinkingFit.SlotsVariable}@{DeepThinkingFit.ModelVariable}={model}", out var said) &&
            int.TryParse(said.Value, System.Globalization.CultureInfo.InvariantCulture, out var most) ? most : 1;
        var count = slots ?? Math.Max(1, Math.Min(fits, before));
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["choice." + DeepThinkingFit.ModelVariable] = model,
            ["choice." + DeepThinkingFit.SlotsVariable] = count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        ErrorLog.Info($"Thinking pool: changing {host.HostId}'s graphics card {card} to {model} with {count} slot{(count == 1 ? "" : "s")}.");
        await RunHostActionAsync(host, HostAction.Change(role), answers);
        // Put the row back as it is now: a run that stopped changed nothing; one that finished shows after the computer's check.
        if (!closing && openTab == CompanionTab.DeepThinking) RenderTab();
    }

    /// <summary>Each graphics card of a paired computer in the list (on or off) that runs no Thinking pool model yet, when the
    /// computer answers and reports that many NVIDIA cards: a row with Add a model (<c>DeepThinkingAddCard-&lt;host&gt;-gpu&lt;n&gt;</c>),
    /// which sets the model up on that card; it then joins as a machine of its own.</summary>
    private IEnumerable<UIElement> SpareCardRows(ThinkingPoolSettings pool, IReadOnlyList<PairedHost> hosts)
    {
        foreach (var hostId in pool.All.Where(m => m is { OnHostRole: true, HostId: not null }).Select(m => m.HostId!).Distinct(StringComparer.Ordinal))
        {
            var host = hosts.FirstOrDefault(h => h.HostId == hostId);
            if (host is null || hostChecks.GetValueOrDefault(hostId) is not { Reachable: true } check) continue;
            var cards = Math.Min(HostRoles.NvidiaCards(HardwareStore?.Find(hostId)) ?? 0, SelfHostSetup.DeepThinkingMaximumCards);
            for (var card = 2; card <= cards; card++)
            {
                var kind = SelfHostSetup.DeepThinkingRoleKind(card);
                if (check.Offers?.ContainsKey(kind) == true) continue;
                var cannot = !ChangesRolesOn(host) ? $"Martlet can't set up roles on {hostId} from here." : CannotHand(hostId, kind, "deep thinking");
                var title = $"{hostId}, graphics card {card}";
                var detail = "No Thinking pool model on this card yet. Add one and it joins the pool as a machine of its own, at full " +
                    "speed beside the computer's other cards." + (cannot is null ? "" : $" {cannot}");
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                var line = Note(detail, new Thickness(0, 2, 0, 0));
                AutomationProperties.SetName(line, $"{title}: {detail}");
                AutomationProperties.SetAutomationId(line, $"DeepThinkingCard-{hostId}-gpu{card}");
                text.Children.Add(line);
                var at = card;
                var add = PageButton("Add a model", () => AddDeepRoleAsync(host, at).Forget(), id: $"DeepThinkingAddCard-{hostId}-gpu{card}");
                AutomationProperties.SetName(add, $"Add a Thinking pool model on {hostId}'s graphics card {card}, as a machine of its own");
                add.ToolTip = "One Thinking model per graphics card: each card thinks over its own jobs at full speed.";
                add.IsEnabled = cannot is null;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
                DockPanel.SetDock(add, Dock.Right);
                row.Children.Add(add);
                row.Children.Add(text);
                yield return row;
            }
        }
    }

    /// <summary>A paired computer that isn't in the pool: what it offers and why it isn't in (kept out, its Ollama does this PC's
    /// Thinking, no Thinking model, not reachable), or that it joins by itself at its next check (<see cref="ThinkingPoolAutoJoin"/>);
    /// with Add the Thinking pool role, Change model and In the pool.</summary>
    private UIElement ComputerRow(ThinkingPoolSettings pool, PairedHost host, string? thinkingHost, WorkSharingSettings sharing)
    {
        var check = hostChecks.GetValueOrDefault(host.HostId);
        var own = check?.Offers?.GetValueOrDefault(HostRoles.DeepThinking);
        var ollama = check?.Offers?.GetValueOrDefault(HostRoles.Ollama);
        var thinksThere = thinkingHost == host.HostId;
        var usable = own is not null || ollama is not null && !thinksThere;
        // What the auto-join rule does with it at its next check: join, or why not.
        var rule = check is { Reachable: true, Routes: { } routes } ? ThinkingPoolAutoJoin.For(pool, PoolHost(host, routes),
            thinkingHost, sharing, WorkSharingRoster.Device, Role == DeviceRole.Host) : null;
        string Joins(string offer) => rule is null or { Changed: true } ? $"{offer} It joins the pool by itself at its next check." : $"{offer} {rule.Why}";
        var detail = pool.Left(host.HostId)
            ? (own is not null ? $"Its Thinking pool role runs {own}. " : ollama is not null ? $"Ollama runs {ollama}. " : "") +
                "Kept out of the pool, because you unticked it. Tick In the pool to add it again."
            : own is not null ? Joins($"Its Thinking pool role runs {own}.")
            : ollama is not null && thinksThere ? $"Its Ollama ({ollama}) does Thinking for the conversation. Add the Thinking pool role " +
                "there to join the pool beside it."
            : ollama is not null ? Joins($"Ollama runs {ollama}.")
            : check?.Reachable == true ? "Add the Thinking pool role there to join the pool."
            : check?.Reachable == false ? "Not reachable right now." : "Not checked yet.";
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
        var line = Note(detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(line, $"{host.HostId}: {detail}");
        AutomationProperties.SetAutomationId(line, "DeepThinkingHost-" + host.HostId);
        text.Children.Add(line);
        if (check?.Reachable == true &&
            DeepThinkingFit.SharedCard(host.HostId, HardwareStore?.Find(host.HostId), check.Offers) is { } shared)
        {
            var warning = Note(shared, new Thickness(0, 2, 0, 0));
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetName(warning, $"{host.HostId}: {shared}");
            AutomationProperties.SetAutomationId(warning, "DeepThinkingShare-" + host.HostId);
            text.Children.Add(warning);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (own is null && check?.Reachable == true && host.CanLaunch && CannotHand(host.HostId, HostRoles.DeepThinking, "deep thinking") is null)
        {
            var add = PageButton("Add the Thinking pool role", () => AddDeepRoleAsync(host).Forget(), primary: !usable,
                id: "DeepThinkingAddRole-" + host.HostId);
            AutomationProperties.SetName(add, $"Add the Thinking pool role on {host.HostId}");
            add.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(add);
        }
        if (own is not null && check?.Reachable == true && ChangesRolesOn(host))
        {
            var change = PageButton("Change model", () => LaunchOnHost(host, HostAction.Change(HostRoles.DeepThinking)),
                id: "DeepThinkingChangeModel-" + host.HostId);
            AutomationProperties.SetName(change, $"Change the Thinking pool model and slots on {host.HostId} (now {own})");
            change.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(change);
        }
        var inPool = new CheckBox
        {
            Content = "In the pool", IsChecked = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            IsEnabled = usable && check?.Reachable == true,
            ToolTip = "Computers with a Thinking model join the pool by themselves. Tick to add this one now."
        };
        AutomationProperties.SetName(inPool, $"{host.HostId} in the Thinking pool");
        AutomationProperties.SetAutomationId(inPool, "DeepThinkingPool-" + host.HostId);
        inPool.Checked += (_, _) => UseDeepHostAsync(host, alsoHere: true).Forget();
        buttons.Children.Add(inPool);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Another of your computers (Add a machine): a paired computer with a Thinking model joins the list by itself, Add a
    /// computer or Check computers, and what goes there.</summary>
    private List<UIElement> DeepComputersPanel(ThinkingPoolSettings pool, IReadOnlyList<PairedHost> hosts)
    {
        var stack = new List<UIElement>();
        var left = pool.LeftByOwner;
        var automatic = Note("Your paired computers with a Thinking model join the list by themselves, on: their Thinking pool role " +
            "(one machine for each graphics card with a Thinking pool model), or their Ollama when it doesn't do this PC's Thinking. " +
            "Untick On on a machine to stop using it; it stays off until you tick it again." +
            (left.Count == 0 ? "" : $" Kept out now: {string.Join(", ", left)}."), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(automatic, "DeepThinkingAutoJoin");
        stack.Add(automatic);
        if (hosts.Count == 0)
        {
            var none = Note("No computer is paired yet. Add one on Devices, then add the Thinking pool role there.", new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(none, "DeepThinkingHosts");
            stack.Add(none);
        }
        stack.Add(Row(hosts.Count == 0 ? PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), primary: true, id: "DeepThinkingAddComputer")
            : PageButton("Check computers", () => RunNodeAction(NodeAction.CheckHost), id: "DeepThinkingCheckHosts")));
        stack.Add(Note("A job's text (for a think, the conversation so far that fits in 16 KiB and the task) goes to the machine " +
            "that runs it; to a paired computer through its paired, pinned connection. While it works there, Thinking, the voice and " +
            "listening keep working here at full speed. The Thinking pool role is an Ollama of its own, so it works even on the " +
            "computer that does Thinking (they share its graphics card, and replies may start later); without it, a computer that " +
            "also does Thinking for the conversation can't join. Adding the role asks which model it runs and how many slots; " +
            "Change model switches them later. A computer's model can only work as long as its Martlet allows: update it to this " +
            "version for jobs over a minute.", new Thickness(0, 8, 0, 0)));
        return stack;
    }
    /// <summary>The kinds of pool job on the Runs on card, by kind name, in plain words.</summary>
    private static readonly (string Kind, string Label)[] RunsOnKinds =
    [
        ("think-longer", "Thinking longer and a song's lyrics"), ("research", "Web research"), ("check-in", "Check-ins"),
        ("memory", "Remembering"), ("naming", "Naming"), ("touch-zones", "Touch zones"), ("digest", "Screen and sound summaries"),
        ("end-of-turn-judge", "Are you done talking (judge)"), ("barge-in-judge", "Did you interrupt (judge)")
    ];

    /// <summary>Runs on: for each kind of job, which machines in the pool may take it (Any member, Prefer smart, Smart only or
    /// These members). Each check-in can choose its own on its card.</summary>
    private Border RunsOnCard(ThinkingPoolSettings pool)
    {
        var stack = new List<UIElement>
        {
            Heading("Runs on"),
            Note("Choose which machines do each kind of job. Prefer smart sends it to the smartest free machine; when none of the " +
                "smartest comes free within a short wait (30 seconds at most), a less smart one takes it. Thinking longer, research " +
                "and lyrics take the smartest free machine at once. Smart only waits for a Smart machine; without one, the job uses " +
                "its simple fallback. These members uses only the machines you tick. Each machine's Smarts is above. Your " +
                "conversation's own replies never change.", new Thickness(0, 0, 0, 8))
        };
        foreach (var (kind, label) in RunsOnKinds)
        {
            var heading = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
            stack.Add(heading);
            Func<ThinkingRunsOn?>? read = null;
            var (view, current) = RunsOnPicker("ThinkingPoolRunsOn-" + kind, "ThinkingPoolRunsOnMember-" + kind, label, pool.RunsOn(kind),
                inherit: false, pool, () =>
                {
                    var where = read?.Invoke() ?? ThinkingRunsOn.Any;
                    SavePoolAsync(p => p.WithRunsOn(kind, where), $"{label}: runs on {where.Describe()}.").Forget();
                });
            read = current;
            stack.Add(view);
        }
        return Card([.. stack]);
    }

    /// <summary>A Runs on choice: a list (<paramref name="id"/>) with Any member, Prefer smart, Smart only and These members
    /// (and, with <paramref name="inherit"/>, Like other check-ins first, which reads as null), and a box for each machine in the
    /// pool with its smarts (<paramref name="memberId"/>-0, -1...), shown with These members. A change calls
    /// <paramref name="save"/>; the returned function reads the choice.</summary>
    private (UIElement View, Func<ThinkingRunsOn?> Read) RunsOnPicker(string id, string memberId, string name, ThinkingRunsOn? start,
        bool inherit, ThinkingPoolSettings pool, Action save)
    {
        string[] modes = ["Any member", "Prefer smart", "Smart only", "These members"];
        var offset = inherit ? 1 : 0;
        const string help = "Any member: the free machine that shares least with the conversation. Prefer smart: the smartest free " +
            "machine, else a less smart one after a short wait. Smart only: only Smart machines. These members: only the machines you tick.";
        var list = new ComboBox
        {
            Width = 220, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = help,
            ItemsSource = inherit ? ["Like other check-ins", .. modes] : modes,
            SelectedIndex = start is null ? 0 : offset + (int)start.Mode
        };
        AutomationProperties.SetAutomationId(list, id);
        AutomationProperties.SetName(list, name + ": runs on");
        AutomationProperties.SetHelpText(list, help);
        var boxes = new List<(string Key, CheckBox Box)>();
        var chosen = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < pool.Members.Count; i++)
        {
            var member = pool.Members[i];
            var label = $"{member.Describe()} ({pool.SmartsOf(member)})";
            var box = new CheckBox { Content = label, IsChecked = start?.Members.Contains(member.Key, StringComparer.Ordinal) == true,
                Margin = new Thickness(0, 0, 16, 6) };
            AutomationProperties.SetAutomationId(box, $"{memberId}-{i}");
            AutomationProperties.SetName(box, $"{name}: runs on {label}");
            box.Checked += (_, _) => save();
            box.Unchecked += (_, _) => save();
            boxes.Add((member.Key, box));
            chosen.Children.Add(box);
        }
        if (pool.Members.Count == 0) chosen.Children.Add(Note("No machine in the pool yet.", new Thickness(0, 0, 0, 0)));
        void Show() => chosen.Visibility = list.SelectedIndex == offset + (int)ThinkingRunsOnMode.Members ? Visibility.Visible : Visibility.Collapsed;
        Show();
        list.SelectionChanged += (_, _) =>
        {
            Show();
            save();
        };
        var view = new StackPanel();
        view.Children.Add(TerminalRow(inherit ? "Runs on (Thinking pool)" : "Runs on", list));
        view.Children.Add(chosen);
        return (view, () =>
        {
            var index = list.SelectedIndex - offset;
            if (index < 0) return null;
            return (ThinkingRunsOnMode)index switch
            {
                ThinkingRunsOnMode.PreferSmart => ThinkingRunsOn.PreferSmart,
                ThinkingRunsOnMode.SmartOnly => ThinkingRunsOn.SmartOnly,
                ThinkingRunsOnMode.Members => ThinkingRunsOn.Only(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Key)),
                _ => ThinkingRunsOn.Any
            };
        });
    }

    /// <summary>Backup for slow replies (Backup Thinking, off by default): when a reply's Thinking model has no words after a short wait, the same request
    /// also goes to a member that may answer for the conversation, and the first to start gives the reply.</summary>
    private Border BackupThinkingCard(ThinkingPoolSettings pool)
    {
        var on = new CheckBox
        {
            IsChecked = pool.BackupThinking, Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { Text = "Ask a machine in the pool too when a reply is slow to start", TextWrapping = TextWrapping.Wrap }
        };
        AutomationProperties.SetAutomationId(on, "ThinkingPoolBackup");
        AutomationProperties.SetName(on, "Backup for slow replies: ask a machine in the pool too when a reply is slow to start");
        void TurnOn(bool value) => SavePoolAsync(p => p with { BackupThinking = value }, value
            ? "Backup for slow replies is on: a slow reply also asks a machine ticked for it."
            : "Backup for slow replies is off.").Forget();
        on.Checked += (_, _) => TurnOn(true);
        on.Unchecked += (_, _) => TurnOn(false);
        var choices = ThinkingPoolSettings.BackupDelayChoices;
        var delay = new ComboBox
        {
            Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = new[] { "Automatic (from recent replies)" }.Concat(choices.Select(ms => (ms / 1000.0).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " s")).ToArray(),
            SelectedIndex = pool.BackupDelayMs is { } chosen ? Math.Max(0, IndexOf(choices, chosen) + 1) : 0
        };
        AutomationProperties.SetAutomationId(delay, "ThinkingPoolBackupDelay");
        AutomationProperties.SetName(delay, "How long a reply waits for its first words before a member is asked too");
        delay.SelectionChanged += (_, _) =>
        {
            int? value = delay.SelectedIndex <= 0 ? null : choices[delay.SelectedIndex - 1];
            SavePoolAsync(p => p with { BackupDelayMs = value }, value is null
                ? "Backup Thinking waits an automatic time, from how fast recent replies began."
                : $"Backup Thinking waits {value} ms for the first words.").Forget();
        };
        var answering = pool.Members.Where(m => pool.Answers(m.Key)).Select(m => m.Describe()).ToArray();
        var words = conversation?.FirstWords;
        var status = Note(LiveConversationController.BackupLine(pool, answering, words is { Count: > 0 } ? words.Delay : null, words?.Count ?? 0),
            new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(status, "ThinkingPoolBackupStatus");
        return Card(Heading("Backup for slow replies"),
            HelpTip.Explain("Off by default. When the conversation's Thinking model has no words after a short wait, the same request also goes " +
                "to a machine you ticked for it (Backup for slow replies, on a machine above). Whichever starts first gives the reply, and the " +
                "other stops at once, so a slow or busy model doesn't keep you waiting. Choose machines with the same model as the " +
                "conversation, or a similar one. A paid cloud provider is asked only when you tick it, and only for a reply already " +
                "taken.", new Thickness(0, 0, 0, 8), "ThinkingFill", "this setting"),
            on, TerminalRow("Wait for first words", delay), status);

        static int IndexOf(IReadOnlyList<int> list, int value)
        {
            for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
            return -1;
        }
    }

    /// <summary>Companion › Thinking pool's line of requests: whether higher-priority requests may stop lower ones, after how many
    /// stops a stopped request's priority goes up, and how many times a failed request is tried again.</summary>
    private Border PriorityCard(ThinkingPoolSettings pool)
    {
        var preempt = new CheckBox
        {
            IsChecked = pool.PreemptLowerPriority, Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { Text = "Higher priority requests may stop lower ones", TextWrapping = TextWrapping.Wrap }
        };
        AutomationProperties.SetAutomationId(preempt, "ThinkingPoolPreempt");
        AutomationProperties.SetName(preempt, "Higher priority requests may stop lower ones");
        void Preempt(bool value) => SavePoolAsync(p => p with { PreemptLowerPriority = value }, value
            ? "Higher priority requests may stop lower ones when the pool is full."
            : "Requests wait for a free slot and never stop each other.").Forget();
        preempt.Checked += (_, _) => Preempt(true);
        preempt.Unchecked += (_, _) => Preempt(false);

        var raiseChoices = Enumerable.Range(ThinkingPoolSettings.MinRaiseAfterStops,
            ThinkingPoolSettings.MaxRaiseAfterStops - ThinkingPoolSettings.MinRaiseAfterStops + 1).ToArray();
        var raise = new ComboBox
        {
            Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = raiseChoices.Select(n => n == 1 ? "After every stop" : $"After {n} stops").ToArray(),
            SelectedIndex = Array.IndexOf(raiseChoices, pool.RaisePriorityAfterStops) is var at and >= 0 ? at : Array.IndexOf(raiseChoices, ThinkingPoolSettings.DefaultRaiseAfterStops),
            IsEnabled = pool.PreemptLowerPriority
        };
        AutomationProperties.SetAutomationId(raise, "ThinkingPoolRaiseAfterStops");
        AutomationProperties.SetName(raise, "Raise a stopped request's priority after this many stops");
        raise.SelectionChanged += (_, _) =>
        {
            if (raise.SelectedIndex < 0) return;
            var value = raiseChoices[raise.SelectedIndex];
            SavePoolAsync(p => p with { RaisePriorityAfterStops = value }, value == 1
                ? "A stopped request becomes more important each time it's stopped."
                : $"A stopped request becomes more important after every {value} stops.").Forget();
        };

        var retryChoices = Enumerable.Range(0, ThinkingPoolSettings.MaxRetries + 1).ToArray();
        var retries = new ComboBox
        {
            Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = retryChoices.Select(n => n switch { 0 => "Don't try again", 1 => "Once more", _ => $"{n} more times" }).ToArray(),
            SelectedIndex = Math.Clamp(pool.RetriesOnFailure, 0, ThinkingPoolSettings.MaxRetries)
        };
        AutomationProperties.SetAutomationId(retries, "ThinkingPoolRetries");
        AutomationProperties.SetName(retries, "Try a failed request again this many times");
        retries.SelectionChanged += (_, _) =>
        {
            if (retries.SelectedIndex < 0) return;
            var value = retryChoices[retries.SelectedIndex];
            SavePoolAsync(p => p with { RetriesOnFailure = value }, value switch
            {
                0 => "A failed request isn't tried again.",
                1 => "A failed request is tried once more.",
                _ => $"A failed request is tried up to {value} more times."
            }).Forget();
        };

        var status = Note(PriorityLine(pool, conversation?.PoolStatusNow()), new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(status, "ThinkingPoolPriorityStatus");
        return Card(Heading("Busy pool"),
            Note("When every slot is busy, a more important request can stop a less important one to go first. The stopped request " +
                "keeps its place at the front of its line and starts again when there's room. If it keeps getting stopped, it becomes " +
                "more important so it finishes in the end. A request that fails can be tried again, at the importance it had.",
                new Thickness(0, 0, 0, 8)),
            preempt, TerminalRow("More important after", raise), TerminalRow("Try a failed request again", retries), status);
    }

    /// <summary>Companion › Thinking pool's Busy pool line (MCP reads it as ThinkingPoolPriorityStatus): the choices in words and,
    /// when the pool runs, how many requests were stopped, raised and tried again since Martlet started.</summary>
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

    /// <summary>Saves thinking-pool.json changed by <paramref name="change"/> and reloads the conversation's pool.</summary>
    private async Task SavePoolAsync(Func<ThinkingPoolSettings, ThinkingPoolSettings> change, string done)
    {
        if (store is null || closing) return;
        try
        {
            var next = change(ThinkingPoolSettings.Load(store.DataDirectory));
            if (!next.Save(store.DataDirectory)) throw new InvalidOperationException("Couldn't save the Thinking pool. Check access to Martlet's data folder.");
            conversation?.ReloadThinkingPool();
            ErrorLog.Info($"Thinking pool: {done}");
            ActionText.Text = done;
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = error.Message;
        }
        await Task.CompletedTask;
        if (!closing && openTab == CompanionTab.DeepThinking) RenderTab();
    }

    /// <summary>Removes a member from the pool; its own key (an endpoint's) is deleted from Windows Credential Manager.</summary>
    private async Task RemovePoolMemberAsync(string key, string name)
    {
        if (store is null || closing) return;
        var old = ThinkingPoolSettings.Load(store.DataDirectory);
        var gone = old.All.FirstOrDefault(m => m.Key == key);
        // A paired computer stays out until it is ticked again; otherwise it would join again by itself at its next check.
        if (gone is { Place: DeepThinkingPlace.Host, HostId: { } hostId })
            await SavePoolAsync(p => p.TakeOut(hostId), $"{name} left the Thinking pool and stays out. Tick In the pool to add it again.");
        else await SavePoolAsync(p => p.Remove(key), $"{name} left the Thinking pool.");
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        if (gone?.CredentialId is { } oldKey && profile != Guid.Empty &&
            ThinkingPoolSettings.Load(store.DataDirectory).All.All(m => m.CredentialId != oldKey))
        {
            var binding = gone.Binding(profile, oldKey);
            await Task.Run(() => new WindowsCredentialStore().Delete(binding), CancellationToken.None);
        }
    }

    // ---------- Thinking longer (saved with the reply settings) ----------

    /// <summary>Whether Martlet may think longer at all (ThinkLongerOn, saved at once with the reply settings, so your computers
    /// share it), how hard it thinks something over and when it shares the result (a think has no time or hourly limit). How hard
    /// and when save a moment after each change, with the reply settings.</summary>
    private Border ThinkLongerCard(ThinkLongerSettings? saved, SetupRoute? route, DeepThinkingPlan plan, bool on)
    {
        var current = saved ?? new ThinkLongerSettings();
        var changed = (Action)(() => { });
        const string onLabel = "Let Martlet think things over in the background";
        var turnOn = new CheckBox
        {
            IsChecked = on, Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { Text = onLabel, TextWrapping = TextWrapping.Wrap },
            ToolTip = "Off: Martlet answers everything right away and never thinks in the background or researches. Your computers share this choice."
        };
        AutomationProperties.SetAutomationId(turnOn, "ThinkLongerOn");
        AutomationProperties.SetName(turnOn, "Thinking longer: " + onLabel);
        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        turnOn.Checked += (_, _) => TurnDeepThinkingAsync(true, "Thinking longer is on.").Forget();
        turnOn.Unchecked += (_, _) => TurnDeepThinkingAsync(false, "Thinking longer is off. Martlet answers everything right away.").Forget();
        ComboBox Choice(string id, string name, IEnumerable<string> items, int selected)
        {
            var box = new ComboBox { Width = 260, ItemsSource = items.ToArray(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, name);
            box.SelectionChanged += (_, _) => changed();
            return box;
        }
        var effort = Choice("ThinkLongerEffort", "How hard it thinks", ["Medium (default)", "High"], Array.IndexOf(ThinkEfforts, current.HowHard));
        var when = Choice("ThinkLongerDelivery", "When Martlet shares the result",
            ["As soon as Martlet is free (default)", "When I talk next"], Array.IndexOf(ThinkDeliveries, current.When));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(status, "ThinkLongerStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        void Show(ThinkLongerSettings? settings)
        {
            var (text, problem) = ThinkLongerStatus(settings ?? new(), route, plan);
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
            foreach (var control in new Control[] { effort, when }) control.IsEnabled = settings?.On ?? true;
        }
        Show(saved);
        ThinkLongerSettings Read(ThinkLongerSettings? loaded) => new()
        {
            Enabled = loaded?.Enabled,
            WebResearch = loaded?.WebResearch,
            Effort = ThinkEfforts[Math.Max(0, effort.SelectedIndex)],
            Delivery = ThinkDeliveries[Math.Max(0, when.SelectedIndex)]
        };
        // There is no Save button: a change saves a moment later, into the newest saved reply settings (whether it is on stays
        // as saved: ThinkLongerOn turns it on or off).
        var autoSave = new AutoSave(() =>
        {
            ThinkLongerSettings? next = null;
            return SaveRepliesAsync(loaded =>
            {
                next = ThinkLongerSettings.Normalize(Read(loaded?.ThinkLonger));
                return GenerationSettings.Normalize((loaded ?? new()) with { ThinkLonger = next });
            }, generation =>
            {
                Show(generation?.ThinkLonger);
                var now = next ?? new();
                ErrorLog.Info($"Thinking longer: {now.HowHard} effort, no time or hourly limit, " +
                    $"shares {(now.When == ThinkDelivery.WhenFree ? "as soon as Martlet is free." : "when you talk next.")}");
            }, "Thinking longer saved. Reload an open conversation to use it.");
        });
        tabAutoSave = autoSave;
        changed = () => { tabEdited = true; autoSave.Changed(); };
        return Card(Heading("Thinking longer"),
            HelpTip.Explain("Replies answer right away (Thinking steps are off by default). When a task really needs thought, such as writing " +
                "song lyrics, a story or a plan, or tricky math or code, Martlet can say it'll think it over and work on it in the " +
                "background while you keep talking, then bring it up when it's done.", new Thickness(0, 0, 0, 8), "ThinkingSteps", "Thinking steps"),
            turnOn, status,
            TerminalRow("How hard", effort), TerminalRow("Share it", when),
            Note("It thinks with Thinking steps on, whatever replies use; one think runs on each free slot of a machine that takes " +
                "long jobs (when the pool has two or more slots, one that takes quick jobs stays free), with no time limit and no limit " +
                "on how many. Stop (Esc) doesn't end it: its Cancel in the talk window or closing the conversation do. A paid " +
                "provider may charge for its thinking.",
                new Thickness(0, 8, 0, 0)));
    }

    /// <summary>Whether Martlet can think longer at all: Thinking hands the task off with a tool, so it needs a route that does
    /// function calling, and Deep thinking needs a model of its own to think on.</summary>
    private (string Text, bool Problem) ThinkLongerStatus(ThinkLongerSettings settings, SetupRoute? route, DeepThinkingPlan plan)
    {
        if (!settings.On) return ("Off. Martlet answers everything right away and never thinks in the background.", false);
        if (route is null) return ("On. Set up Thinking so Martlet can use it.", true);
        if (route.RouteType is not (SetupRouteType.OpenAi or SetupRouteType.ChatCompletions))
            return ("On, but your Thinking model can't use tools here (a Martlet host's model), so it can't hand a task off. Use " +
                "OpenAI or a Chat Completions endpoint (such as Ollama on this PC) for Thinking.", true);
        if (mcpTools.IsUnsupported(McpToolService.ModelKey($"{route.RouteType}", route.Origin, route.ModelId)))
            return ($"On, but {route.ModelId} turned down tools, so it can't hand a task off. Choose a Thinking model that can use tools.", true);
        if (!plan.Available)
            return ($"On, but the Thinking pool has nowhere to think in parallel, so Martlet doesn't offer to think things over. {plan.Why}", true);
        return ($"On. When a task needs it, Martlet says it'll think it over and works on it in the background ({settings.HowHard} " +
            "effort, no time limit, no limit on how many) while you keep talking, " +
            (settings.When == ThinkDelivery.WhenFree ? "then brings it up as soon as it's free." : "then brings it up when you talk next."), false);
    }

    // ---------- Web research (saved with the reply settings) ----------

    /// <summary>Whether Martlet may look things up on the web when asked (off by default: the search words go to DuckDuckGo and
    /// the pages' sites see this PC's internet address). It saves at once, with the reply settings, so your computers share it;
    /// Deep thinking off turns it off too.</summary>
    private Border WebResearchCard(ThinkLongerSettings settings, SetupRoute? route, DeepThinkingPlan plan)
    {
        var allow = new CheckBox
        {
            IsChecked = settings.WebResearch == true, IsEnabled = settings.On, Margin = new Thickness(0, 8, 0, 6),
            Content = new TextBlock { Text = "Let Martlet search the web and read pages when I ask it to look something up", TextWrapping = TextWrapping.Wrap }
        };
        AutomationProperties.SetAutomationId(allow, "WebResearchOn");
        AutomationProperties.SetName(allow, "Let Martlet search the web when I ask it to look something up");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(status, "WebResearchStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var (text, problem) = WebResearchStatus(settings, route, plan);
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
        allow.Checked += (_, _) => SetWebResearchAsync(true).Forget();
        allow.Unchecked += (_, _) => SetWebResearchAsync(false).Forget();
        var disclosure = Note("What leaves this PC: your search words go to DuckDuckGo, and each page Martlet reads sees this PC's " +
            "internet address. What the pages say goes to the Thinking pool member that works on it, with the recent conversation. Martlet only " +
            "connects to public websites (never your own network), reads at most 8 pages, takes at most 12 minutes and starts at " +
            "most 4 an hour. The report and its sources are kept in Creations; Martlet offers to show it in your browser.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(disclosure, "WebResearchDisclosure");
        return Card(Heading("Web research"),
            HelpTip.Explain("Ask Martlet to look something up or research it and it says it'll look into it, searches the web and reads pages " +
                "in the background while you keep talking, then tells you what it found and offers the full report.", new Thickness(0, 0, 0, 4), "Research", "looking things up"),
            status, allow, disclosure);
    }

    private (string Text, bool Problem) WebResearchStatus(ThinkLongerSettings settings, SetupRoute? route, DeepThinkingPlan plan)
    {
        if (!settings.On) return ("Off, because thinking longer is off. Turn it on under Thinking longer above to use web research.", false);
        if (settings.WebResearch != true) return ("Off. Martlet never searches the web or reads web pages.", false);
        var (thinking, problem) = ThinkLongerStatus(settings, route, plan);
        if (problem)
        {
            var why = thinking.StartsWith("On, but ", StringComparison.Ordinal) ? thinking[8..]
                : thinking.StartsWith("On. ", StringComparison.Ordinal) ? thinking[4..] : thinking;
            return ("On, but Martlet can't look things up yet: " + char.ToLowerInvariant(why[0]) + why[1..], true);
        }
        return ($"On. When you ask, Martlet looks it up (up to {BackgroundJobs.Duration(WebResearch.TimeLimit)}, at most " +
            $"{WebResearch.Kind.MaxPerHour} an hour), then offers the report.", false);
    }

    /// <summary>Saves whether Martlet may research on the web into the newest reply settings (your computers share it).</summary>
    private async Task SetWebResearchAsync(bool on)
    {
        if (closing) return;
        var saved = false;
        for (var attempt = 0; attempt < 20 && !closing && !saved; attempt++)
        {
            saved = await SaveRepliesAsync(loaded => GenerationSettings.Normalize((loaded ?? new()) with
            {
                ThinkLonger = ThinkLongerSettings.Normalize((loaded?.ThinkLonger ?? new()) with { WebResearch = on })
            }), _ => { }, on ? "Web research is on. Reload an open conversation to use it." : "Web research is off.");
            if (!saved)
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token); }
                catch (OperationCanceledException) { return; }
        }
        if (saved) ErrorLog.Info($"Web research: turned {(on ? "on" : "off")}.");
        if (!closing && openTab == CompanionTab.DeepThinking) RenderTab();
    }

    /// <summary>Opens a research report's web page (written by <see cref="ResearchReports"/>) in the default browser.</summary>
    private static void OpenReportPage(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        ErrorLog.Info("Web research: opened a report in the browser.");
    }

    // ---------- where it thinks ----------

    /// <summary>The Now card's line: whether Martlet may think longer, which pool members (or the conversation model) a job goes
    /// to and whether one can run there.</summary>
    private static string DeepThinkingNow(ThinkingPoolSettings pool, SetupRoute? route, DeepThinkingPlan plan, bool on)
    {
        if (!on) return "Off. Martlet answers everything right away and never thinks in the background.";
        var where = pool.Members.Count > 0 ? pool.Places.DescribeAll()
            : !pool.UseConversationModelWhenEmpty ? "no member"
            : route is null ? "the conversation model (not set up yet)" : $"the conversation model ({route.ModelId}), until a machine in the pool takes long jobs";
        return plan.Available ? $"The Thinking pool thinks on {where}, in parallel with the conversation."
            : $"On, but the pool can't think on {where}, so Martlet doesn't offer to think things over. Add a machine below, or tick Long jobs on one.";
    }

    /// <summary>Installs the Deep thinking role on a paired computer (its own run window, with the model choice) and, once it
    /// runs, thinks there. With <paramref name="card"/> 2 to 4 it installs the Thinking pool model for that graphics card
    /// (deep-thinking-2...), which joins as a member of its own.</summary>
    private async Task AddDeepRoleAsync(PairedHost host, int card = 1)
    {
        if (closing) return;
        var kind = SelfHostSetup.DeepThinkingRoleKind(card);
        if (CannotHand(host.HostId, kind, "deep thinking") is { } cannot)
        {
            ActionText.Text = $"The Thinking pool role can't be set up on {host.HostId}: {cannot}";
            return;
        }
        if (await RunHostActionAsync(host, HostRoles.Get(kind).Add) is null || closing) return;
        await UseDeepHostAsync(host);
    }

    /// <summary>A check saw <paramref name="hostId"/> answer: a paired computer with a Thinking model joins the Thinking pool by
    /// itself (<see cref="ThinkingPoolAutoJoin"/>; never one the owner took out), a member on its Ollama moves to its Thinking
    /// pool role once it has one, and a member on that role takes the role's slot count, so the broker places that many jobs
    /// there. thinking-pool.json is written only when something changed, and the owner is told once. Returns what it told the
    /// owner, or null.</summary>
    private string? NoteThinkingPoolHost(string hostId)
    {
        if (store is null || closing || hostChecks.GetValueOrDefault(hostId) is not { Reachable: true, Routes: { } routes }) return null;
        // A host a friend shares with this PC never joins the Thinking pool: background work there is its owner's to allow.
        if (sharedHosts.Any(h => h.HostId == hostId)) return null;
        var host = homeHosts.FirstOrDefault(h => h.HostId == hostId) ??
            (homeAvatar?.RemoteHost is { } lipSync && lipSync.HostId == hostId ? new PairedHost { Pairing = lipSync } : null);
        if (host is null) return null;
        // A file this Martlet can't read (a newer Martlet's, or a damaged one) is never written over.
        var (pool, state) = ThinkingPoolSettings.Read(store.DataDirectory);
        if (state == "unreadable") return null;
        var result = ThinkingPoolAutoJoin.For(pool, PoolHost(host, routes), NetworkMap.ThinkingHost(homeSettings),
            WorkSharingRoster.Settings(store.DataDirectory), WorkSharingRoster.Device, Role == DeviceRole.Host, DateTimeOffset.Now);
        if (!result.Changed) return null;
        bool saved;
        try { saved = result.Pool.Save(store.DataDirectory); }
        catch (ContractException) { saved = false; }
        if (!saved)
        {
            if (poolJoinWarned.Add(hostId)) ErrorLog.Warn($"Thinking pool: {hostId} couldn't join, because thinking-pool.json can't be saved.");
            return null;
        }
        poolJoinWarned.Remove(hostId);
        conversation?.ReloadThinkingPool();
        ErrorLog.Info("Thinking pool: " + result.Why);
        if (openTab == CompanionTab.DeepThinking && !tabEdited) RenderTab();
        if (result.OnlySlots) return null;
        ActionText.Text = result.Why;
        return result.Why;
    }

    private readonly HashSet<string> poolJoinWarned = new(StringComparer.Ordinal);

    /// <summary><paramref name="host"/> and the Thinking routes it offers now, as <see cref="ThinkingPoolAutoJoin"/> reads them.</summary>
    private static ThinkingPoolHost PoolHost(PairedHost host, IReadOnlyList<HostRoute> routes) => new(host.HostId, host.Pairing.Origin,
        host.Pairing.SpkiFingerprint, host.Pairing.DeviceId, HostPairingCredential.ToGuid(host.Pairing.CredentialId),
        [.. routes.Where(r => r.RouteId == HostRoute.OllamaChatRouteId || SelfHostSetup.IsDeepThinkingRoute(r.RouteId))
            .Select(r => new ThinkingPoolOffer(r.RouteId, r.ModelId, r.MaximumConcurrency))]);

    /// <summary>Thinks on <paramref name="host"/>: in place of where it thinks now, or with <paramref name="alsoHere"/> as one more
    /// place, so several thinks run at once (where it thinks now becomes this computer when it can't think there). The owner
    /// asked for it, so the computer is no longer kept out: when it doesn't answer now, it joins by itself once it does.</summary>
    private async Task UseDeepHostAsync(PairedHost host, bool alsoHere = false)
    {
        if (closing) return;
        if (store is not null && ThinkingPoolSettings.Read(store.DataDirectory) is ({ } kept, not "unreadable") && kept.Left(host.HostId))
        {
            try
            {
                if (kept.KeepOut(host.HostId, false).Save(store.DataDirectory))
                    ErrorLog.Info($"Thinking pool: {host.HostId} is no longer kept out.");
            }
            catch (ContractException) { }
        }
        ActionText.Text = $"Checking {host.HostId}...";
        try
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            hostChecks[host.HostId] = check;
            if (check.Reachable != true)
            {
                ActionText.Text = $"{host.HostId} didn't join the Thinking pool. It didn't respond ({check.Text}).";
                RenderTab();
                return;
            }
            // Its own Thinking pool role first; otherwise its Ollama, when that doesn't do Thinking for the conversation.
            var seen = PoolHost(host, check.Routes ?? []);
            if (ThinkingPoolAutoJoin.Route(seen, NetworkMap.ThinkingHost(homeSettings)) is not { } route)
            {
                ActionText.Text = seen.Offers.Count > 0
                    ? $"{host.HostId}'s Ollama does Thinking for the conversation. Add the Thinking pool role there; it then joins by itself."
                    : $"{host.HostId} doesn't run the Thinking pool role yet. Add the role there; it then joins by itself.";
                RenderTab();
                return;
            }
            // A card the owner turned off stays off: only the others join (or follow the computer's role).
            var current = store is null ? new ThinkingPoolSettings() : ThinkingPoolSettings.Load(store.DataDirectory);
            var cards = ThinkingPoolAutoJoin.Members(seen, NetworkMap.ThinkingHost(homeSettings), DateTimeOffset.Now)
                .Where(m => !current.IsOff(m.Key)).ToArray();
            if (cards.Length == 0)
            {
                ActionText.Text = $"{host.HostId} is turned off in the Thinking pool. Tick On on its row to use it again.";
                RenderTab();
                return;
            }
            var next = cards[0];
            // A host with a Thinking pool model on each of several graphics cards joins with one member per card.
            if (store is not null && cards.Length > 1)
            {
                var pool = cards.Aggregate(current, (p, m) =>
                    p.Members.Any(x => x.Key == m.Key) || p.Members.Count < DeepThinkingSettings.MaxPlaces ? p.Add(m) : p);
                try { pool.Save(store.DataDirectory); }
                catch (ContractException) { }
            }
            await SaveDeepThinkingAsync(next, null, next.OnHostRole ? $"{host.HostId}'s Thinking pool role ({next.ModelId}) joined the Thinking pool."
                : $"{host.HostId} ({next.ModelId}) joined the Thinking pool.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or ContractException or Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException)
        {
            ActionText.Text = error.Message;
        }
    }

    /// <summary>A second model in Ollama on this PC, beside Thinking's: Ollama runs each model in its own process, so it thinks in
    /// parallel with the conversation while both fit on the graphics card (checked here, and before each think). The model box
    /// starts with one that isn't Thinking's and isn't in the list (<paramref name="members"/>) yet.</summary>
    private List<UIElement> DeepLocalPanel(IReadOnlyList<DeepThinkingSettings> members, SetupRoute? thinking)
    {
        var beside = IsLocalOllama(thinking) ? thinking!.ModelId : null;
        var choices = (ollamaModels ?? []).Concat(LocalChatModels.Select(m => m.Id)).Distinct(StringComparer.Ordinal).ToArray();
        var model = new ComboBox { IsEditable = true, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices,
            Text = choices.FirstOrDefault(id => !OllamaSideBySide.Same(id, beside) &&
                !members.Any(m => m.Place == DeepThinkingPlace.Endpoint && m.Origin == LocalOllamaBaseUrl && m.ModelId == id)) ?? "" };
        AutomationProperties.SetName(model, "Model for the Thinking pool");
        AutomationProperties.SetAutomationId(model, "DeepThinkingLocalModel");
        var state = Note(ollamaModels is null ? "Check Ollama to see which models are downloaded."
            : ollamaModels.Count == 0 ? "Ollama is running, but no model is downloaded yet."
            : $"Downloaded: {string.Join(", ", ollamaModels)}.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(state, "DeepThinkingLocalStatus");
        var fit = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(fit, "DeepThinkingLocalFit");
        AutomationProperties.SetLiveSetting(fit, AutomationLiveSetting.Polite);
        void ShowFit(string text, bool problem)
        {
            fit.Text = text;
            fit.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
        }
        void Check(string id)
        {
            if (beside is null) ShowFit($"Thinking doesn't use Ollama on this PC, so {(id.Length > 0 ? id : "this model")} has it to " +
                "itself and thinks alongside the conversation.", false);
            else if (id.Length == 0) ShowFit("Choose a model.", true);
            else if (OllamaSideBySide.Same(id, beside))
                ShowFit($"{id} is Thinking's own model, which can't think something over while it answers you. Choose another model.", true);
            else if (deepLocalFitModel == id && deepLocalFit is { } known) ShowFit((known.Fits ? "Fits: " : "Doesn't fit: ") + known.Why, !known.Fits);
            else
            {
                ShowFit($"Checking whether {id} fits beside {beside} on the graphics card...", false);
                CheckDeepFitAsync(beside, id, ShowFit).Forget();
            }
        }
        Check(model.Text.Trim());
        model.SelectionChanged += (_, _) => { if (model.SelectedItem is string chosen) Check(chosen); };
        model.LostKeyboardFocus += (_, _) => Check(model.Text.Trim());
        var shared = Note(DeepThinkingFit.SharedCard("This PC", beside,
            ReferenceEquals(machine, MachineInfo.Unknown) ? 1 : DeepThinkingFit.DedicatedCards(machine.Gpus.Select(g => (g.Name, g.MemoryGb)))) ?? "",
            new Thickness(0, 4, 0, 0));
        shared.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        shared.Visibility = shared.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(shared, "DeepThinkingLocalShare");
        return [
            HelpTip.Explain("A second model can think here while Thinking's answers you, such as a larger one beside a small, fast one. Ollama runs " +
                "each model in its own process, so they answer at the same time, but only while both fit on the graphics card: Martlet " +
                "checks before each think and doesn't think it over when they don't. They share the graphics card, so replies may start " +
                "a little later while it thinks.", new Thickness(0, 0, 0, 8), "SecondThinking", "a second model"),
            new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 0, 0, 4) }, model, state, fit, shared,
            Row(PageButton("Add to the Thinking pool", () =>
                {
                    var id = model.Text.Trim();
                    try { ChatCompletionsSetup.ModelId(id); }
                    catch (ContractException error) { ActionText.Text = error.Message; return; }
                    if (beside is not null && OllamaSideBySide.Same(id, beside))
                    {
                        ActionText.Text = $"{id} is Thinking's own model, which can't think something over while it answers you. Choose another model.";
                        return;
                    }
                    if (shared.Text.Length > 0 && !ConfirmationDialog.Confirm(this, shared.Text + " Use it anyway?",
                            "The Thinking pool shares a graphics card", yes: "_Use anyway", no: "_Cancel", questionId: "DeepThinkingShareQuestion"))
                        return;
                    SaveDeepThinkingAsync(new() { Place = DeepThinkingPlace.Endpoint, Origin = LocalOllamaBaseUrl, ModelId = id, ChosenAt = DateTimeOffset.Now },
                        null, $"{id} in Ollama on this PC joined the Thinking pool." +
                        (ollamaModels is { } known && !LocalOllama.Serves(known, id) ? $" Download {id} on Thinking to use it." : "")).Forget();
                }, primary: true, id: "DeepThinkingUseLocal"),
                PageButton("Check Ollama", () =>
                {
                    deepLocalFitModel = null;
                    CheckOllamaAsync().Forget();
                }, id: "DeepThinkingCheckOllama"))];
    }

    /// <summary>Checks whether <paramref name="deep"/> fits beside Thinking's <paramref name="thinking"/> in Ollama on this PC
    /// (read-only: it loads nothing) and shows the result.</summary>
    private async Task CheckDeepFitAsync(string thinking, string deep, Action<string, bool> show)
    {
        try
        {
            var fit = await LocalDeepThinking.CheckAsync(thinking, deep, loadThinking: false, lifetime.Token);
            deepLocalFit = fit;
            deepLocalFitModel = deep;
            ErrorLog.Info($"Thinking pool: {deep} {(fit.Fits ? "fits" : "doesn't fit")} beside {thinking} in Ollama on this PC. {fit.Why}");
            if (!closing) show((fit.Fits ? "Fits: " : "Doesn't fit: ") + fit.Why, !fit.Fits);
        }
        catch (OperationCanceledException) { }
    }
    /// <summary>Adds a cloud provider or any OpenAI-compatible server (HTTPS, or a server on this PC) to the list, with its own key
    /// or Thinking's. A provider already in the list (<paramref name="members"/>, on or off) with a saved key uses that key again
    /// unless a new one is pasted, so a second model there needs no key.</summary>
    private List<UIElement> DeepCloudPanel(IReadOnlyList<DeepThinkingSettings> members, SetupRoute? thinking)
    {
        var provider = new ComboBox { ItemsSource = DeepThinkingProviders, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(provider, "Thinking pool provider");
        AutomationProperties.SetAutomationId(provider, "DeepThinkingProvider");
        provider.SelectedItem = DeepThinkingProviders[0];
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(baseUrl, "Thinking pool API base URL");
        AutomationProperties.SetAutomationId(baseUrl, "DeepThinkingBaseUrl");
        var baseUrlPanel = new StackPanel { Children = { new Label { Content = "API base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 8, 0, 4) }, baseUrl } };
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(model, "Thinking pool model ID");
        AutomationProperties.SetAutomationId(model, "DeepThinkingModel");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "Thinking pool API key");
        AutomationProperties.SetAutomationId(key, "DeepThinkingKey");
        var keyStatus = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "DeepThinkingKeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8) };
        AutomationProperties.SetAutomationId(consent, "DeepThinkingConsent");
        var consentText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        consent.Content = consentText;
        CloudProvider Selected() => provider.SelectedItem as CloudProvider ?? CustomCloud;
        string Url() => Selected().BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim();
        void Refresh(bool keepModel)
        {
            var p = Selected();
            baseUrlPanel.Visibility = p == CustomCloud ? Visibility.Visible : Visibility.Collapsed;
            if (!keepModel) model.Text = p.DefaultModel ?? "";
            var url = Url();
            keyStatus.Text = SavedKey(members, url) is { } saved
                    ? $"Its {p.Name} key is saved (for {saved.Describe()}). Leave this empty to use it again, or paste a new key."
                : thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == url
                    ? "Leave this empty to use Thinking's key for the same provider, or paste another key."
                : p.NeedsKey ? $"Paste your {p.Name} API key. Martlet saves it in Windows Credential Manager."
                : "Add a key only if your server needs one.";
            consentText.Text = $"I add {p.Name} to the Thinking pool. When it takes a job, the job's text (for a think, the recent conversation and " +
                "the task) goes there, and requests (with their hidden thinking) may cost money.";
            consent.IsChecked = false;
        }
        Refresh(keepModel: false);
        provider.SelectionChanged += (_, _) => { tabEdited = true; Refresh(keepModel: false); };
        baseUrl.TextChanged += (_, _) => { if (!baseUrl.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        model.TextChanged += (_, _) => { if (!model.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => tabEdited = true;
        return [
            Note("A strong reasoning model (for example on OpenRouter) thinks while your Thinking model keeps talking, even one on this " +
                "PC: they never wait for each other.", new Thickness(0, 0, 0, 8)),
            new Label { Content = "_Provider", Target = provider, Padding = new Thickness(0, 0, 0, 4) }, provider, baseUrlPanel,
            new Label { Content = "_Model ID", Target = model, Padding = new Thickness(0, 8, 0, 4) }, model,
            new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) }, key, keyStatus, consent,
            Row(PageButton("Add to the Thinking pool", () => SaveDeepCloudAsync(Selected(), Url(), model.Text.Trim(), key, consent.IsChecked == true,
                members, thinking).Forget(), primary: true, id: "DeepThinkingSaveCloud"))];
    }

    /// <summary>The member in <paramref name="members"/> on <paramref name="url"/> with a key of its own, or null.</summary>
    private static DeepThinkingSettings? SavedKey(IReadOnlyList<DeepThinkingSettings> members, string url) =>
        members.FirstOrDefault(m => m.Place == DeepThinkingPlace.Endpoint && m.Origin == url && m.CredentialId is not null);

    private async Task SaveDeepCloudAsync(CloudProvider provider, string url, string model, PasswordBox keyBox, bool consent,
        IReadOnlyList<DeepThinkingSettings> members, SetupRoute? thinking)
    {
        if (!consent)
        {
            ActionText.Text = $"Tick the box to confirm {provider.Name} for the Thinking pool, then press Add to the Thinking pool.";
            return;
        }
        SecretLease? key = null;
        try
        {
            _ = ChatCompletionsSetup.BaseUri(url);
            ChatCompletionsSetup.ModelId(model);
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            var keep = SavedKey(members, url)?.CredentialId;
            var next = new DeepThinkingSettings
            {
                Place = DeepThinkingPlace.Endpoint, Origin = url, ModelId = model, CredentialId = keep, ChosenAt = DateTimeOffset.Now
            };
            if (key is null && provider.NeedsKey && keep is null && !next.UsesThinkingKey(thinking))
                throw new ContractException(ErrorCode.InvalidContract, $"Paste your {provider.Name} API key first.");
            await SaveDeepThinkingAsync(next, key, $"{provider.Name} ({model}) joined the Thinking pool." +
                (key is null ? "" : " Its API key is saved in Windows Credential Manager."));
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = error.Message;
        }
        finally { key?.Dispose(); }
    }

    /// <summary>Adds <paramref name="next"/> to the Thinking pool on this PC (thinking-pool.json), or replaces the member for the
    /// same computer or the same endpoint and model. A new key is written to Windows Credential Manager first and removed again
    /// if the file can't be saved; a key no member uses any more is removed after. The next job uses it.</summary>
    private async Task SaveDeepThinkingAsync(DeepThinkingSettings next, SecretLease? key, string done)
    {
        if (store is null || closing) return;
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        var old = ThinkingPoolSettings.Load(store.DataDirectory);
        CredentialBinding? written = null;
        try
        {
            if (key is not null)
            {
                if (profile == Guid.Empty) throw new InvalidOperationException("Set up Martlet first, then add the key.");
                next = next with { CredentialId = Guid.NewGuid() };
                var binding = next.Binding(profile, next.CredentialId!.Value);
                var lease = key;
                var error = await Task.Run(() => vault.Write(binding, lease), lifetime.Token);
                if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
                written = binding;
            }
            var pool = old.Add(next);
            if (!pool.Save(store.DataDirectory)) throw new InvalidOperationException("Couldn't save the Thinking pool. Check access to Martlet's data folder.");
            written = null;
            foreach (var gone in old.All.Where(m => m.CredentialId is { } k && pool.All.All(p => p.CredentialId != k)))
                if (profile != Guid.Empty)
                {
                    var oldBinding = gone.Binding(profile, gone.CredentialId!.Value);
                    await Task.Run(() => vault.Delete(oldBinding), CancellationToken.None);
                }
            conversation?.ReloadThinkingPool();
            var plan = pool.Plan(homeSettings?.Setup?.Routes ?? [], WorkSharingRoster.Settings(store?.DataDirectory), WorkSharingRoster.Device).Plan;
            ErrorLog.Info($"Thinking pool: now {pool.Places.DescribeAll()}; {(plan.Available ? "in parallel with the conversation" : "can't run there")}. {plan.Why}");
            var turnedOn = !ThinkLongerSettings.Of(homeSettings?.Generation).On && await SetThinkLongerAsync(true);
            ActionText.Text = done + (turnedOn ? " Thinking longer is on again." : "") +
                (plan.Available ? " It works alongside the conversation." : " " + plan.Why);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            if (!closing && openTab == CompanionTab.DeepThinking) RenderTab();
        }
    }

    /// <summary>Thinking longer's on/off (ThinkLongerOn), saved with the reply settings (your computers share
    /// it). Shows <paramref name="done"/> once saved.</summary>
    private async Task TurnDeepThinkingAsync(bool on, string done)
    {
        if (closing) return;
        if (await SetThinkLongerAsync(on))
        {
            ErrorLog.Info($"Thinking pool: thinking longer turned {(on ? "on" : "off")}.");
            ActionText.Text = done + " Reload an open conversation to use it.";
        }
        if (!closing && openTab == CompanionTab.DeepThinking) RenderTab();
    }

    /// <summary>Saves whether Martlet may think longer into the newest reply settings, trying again shortly while another change
    /// holds them. True once it is saved that way.</summary>
    private async Task<bool> SetThinkLongerAsync(bool on)
    {
        for (var attempt = 0; attempt < 20 && !closing; attempt++)
        {
            if (await SaveRepliesAsync(loaded => GenerationSettings.Normalize((loaded ?? new()) with
                {
                    ThinkLonger = ThinkLongerSettings.Normalize((loaded?.ThinkLonger ?? new()) with { Enabled = on })
                }), _ => { }, on ? "Thinking longer is on." : "Thinking longer is off."))
                return ThinkLongerSettings.Of(homeSettings?.Generation).On == on;
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }
}
