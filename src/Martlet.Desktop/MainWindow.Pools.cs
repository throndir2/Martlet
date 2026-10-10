using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What an area's page gives the shared pool list (<see cref="MainWindow.PoolListCard"/>).</summary>
internal sealed record PoolListOptions
{
    public required PoolArea Area { get; init; }
    public string Heading { get; init; } = "Where it runs";
    /// <summary>Text under the heading.</summary>
    public string? Intro { get; init; }
    /// <summary>The member's state in plain words ("Runs Chatterbox", "Not reachable"...); null: nothing to add.</summary>
    public Func<PoolMember, string?>? Status { get; init; }
    /// <summary>The area's settings editor for one member (engine, model, workflow...); null: the member has none. Shown under the
    /// member's row when its Settings button is pressed.</summary>
    public Func<PoolMember, UIElement?>? Settings { get; init; }
    /// <summary>Whether a paired computer can be added (it can run the area's engine); null: every paired computer.</summary>
    public Func<PairedHost, bool>? CanAdd { get; init; }
    /// <summary>The settings a member gets when it is added (engine, model...); null: none.</summary>
    public Func<PoolMember, PoolMember>? NewMember { get; init; }
    /// <summary>The area's own way to add a cloud provider (provider, model, key and the owner's agreement), when it takes cloud
    /// members. It adds the member itself.</summary>
    public Func<UIElement>? AddCloud { get; init; }
    /// <summary>Runs after the list was saved, with the list before and after (the area follows it: its route, its backend).</summary>
    public Func<PoolList, PoolList, Task>? Saved { get; init; }
    /// <summary>Draws the page again after a change; null: the Companion page.</summary>
    public Action? Rerender { get; init; }
}

/// <summary>Pools on each area's page (docs/CLUSTER.md#pools-one-ordered-list-of-members-per-area): the shared list control,
/// the first lists made from Devices › Sharing work, and Speaking, Listening and Thinking following their lists.</summary>
public partial class MainWindow
{
    /// <summary>The members whose settings are shown, by area and member key.</summary>
    private readonly HashSet<string> poolExpanded = new(StringComparer.Ordinal);
    /// <summary>The route member each area saw last, so a route chosen on its page (Use it) moves to the front of its list.</summary>
    private readonly Dictionary<string, string?> poolRouteSeen = new(StringComparer.Ordinal);

    private static readonly (PoolArea Area, SetupRole Role, string Job)[] RoutePools =
    [
        (PoolAreas.Speaking, SetupRole.Tts, WorkSharingJobs.Speaking),
        (PoolAreas.Listening, SetupRole.Stt, WorkSharingJobs.Listening),
        (PoolAreas.Thinking, SetupRole.Llm, WorkSharingJobs.Thinking)
    ];

    // ---------- the shared list control ----------

    /// <summary>One area's pool as an ordered list (automation IDs <c>Pool-&lt;area&gt;-...</c>): a summary of what the list
    /// means now; for each member its name and state, On, Up, Down, Remove, Settings (the area's editor) and which companion PCs
    /// may use it; then Add for this PC, each paired computer, each graphics card, an address and a cloud provider. An empty
    /// list (no member on) is off, or the area's fallback on this PC.</summary>
    internal Border PoolListCard(PoolListOptions options)
    {
        var area = options.Area;
        var id = "Pool-" + area.Id;
        var list = store is null ? new PoolList { Area = area.Id } : WorkSharingRoster.Pool(store.DataDirectory, area) ?? new PoolList { Area = area.Id };
        var stack = new List<UIElement> { Heading(options.Heading) };
        if (options.Intro is not null) stack.Add(Note(options.Intro, new Thickness(0, 0, 0, 8)));
        var on = list.Members.Count(m => !m.Off);
        var summary = Note(on == 0
                ? area.Required ? $"Nothing in the list is on. Instead: {area.WhenEmpty}."
                : area.ConversationFirst ? "Empty: a reply always waits for the conversation's own model."
                : "Off: nothing in the list is on."
                : area.ConversationFirst
                    ? $"The conversation's own model goes first. When it is busy, a reply goes to the first free one of {on} below."
                    : on == 1 ? "One member is on. Requests go to it."
                    : $"{on} members are on. A request goes to the first free one, in this order; when all are busy it waits for the first to finish.",
            new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(summary, id + "-Summary");
        stack.Add(summary);
        var companions = SharingCompanions();
        for (var i = 0; i < list.Members.Count; i++) stack.Add(PoolRow(options, list, i, companions));
        stack.Add(PoolAdd(options, list));
        return Card([.. stack]);
    }

    private UIElement PoolRow(PoolListOptions options, PoolList list, int i, IReadOnlyList<(string Device, string Name)> companions)
    {
        var area = options.Area;
        var id = "Pool-" + area.Id;
        var member = list.Members[i];
        var key = member.Key;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var row = NetworkRowFrame();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var onBox = new CheckBox { Content = "On", IsChecked = !member.Off, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(onBox, $"{id}-On-{i}");
        AutomationProperties.SetName(onBox, $"Use {member.Name} for {area.Title}");
        onBox.Checked += (_, _) => SavePoolList(options, list, list.With(member with { Off = false }), $"{area.Title} uses {member.Name} again.");
        onBox.Unchecked += (_, _) => SavePoolList(options, list, list.With(member with { Off = true }), $"{area.Title} no longer uses {member.Name}. Its settings are kept.");
        buttons.Children.Add(onBox);
        Button Small(string text, string automation, string name, bool enabled, Action run)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled, MinWidth = 0, Padding = new Thickness(8, 2, 8, 2) };
            AutomationProperties.SetAutomationId(button, automation);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => run();
            return button;
        }
        buttons.Children.Add(Small("Up", $"{id}-Up-{i}", $"Try {member.Name} earlier for {area.Title}", i > 0,
            () => SavePoolList(options, list, list.Move(key, -1), $"{area.Title} now tries {member.Name} earlier.")));
        buttons.Children.Add(Small("Down", $"{id}-Down-{i}", $"Try {member.Name} later for {area.Title}", i < list.Members.Count - 1,
            () => SavePoolList(options, list, list.Move(key, 1), $"{area.Title} now tries {member.Name} later.")));
        var settings = options.Settings?.Invoke(member);
        var expanded = poolExpanded.Contains(area.Id + " " + key);
        if (settings is not null)
            buttons.Children.Add(Small(expanded ? "Hide settings" : "Settings", $"{id}-Settings-{i}", $"Settings of {member.Name} for {area.Title}", true, () =>
            {
                if (!poolExpanded.Remove(area.Id + " " + key)) poolExpanded.Add(area.Id + " " + key);
                (options.Rerender ?? RenderTab)();
            }));
        buttons.Children.Add(Small("Remove", $"{id}-Remove-{i}", $"Remove {member.Name} from {area.Title}", true,
            () => SavePoolList(options, list, list.Without(key), $"{member.Name} is no longer in the {area.Title} list.")));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var facts = new List<string>();
        if (member.Off) facts.Add("off; its settings are kept");
        else if (!member.Consented(area.Id)) facts.Add("waits for your agreement to send requests there");
        if (options.Status?.Invoke(member) is { Length: > 0 } status) facts.Add(status);
        if (member.OnlyFor.Count > 0) facts.Add("kept for " + string.Join(" and ", member.OnlyFor));
        var kind = member.Kind switch
        {
            PoolMemberKind.ThisPc => "each companion PC itself",
            PoolMemberKind.Computer => "a paired computer",
            PoolMemberKind.Gpu => "one graphics card",
            PoolMemberKind.Address => "a service you run",
            _ => "a cloud provider"
        };
        row.Children.Add(NetworkRowText($"{id}-Member-{i}", $"{i + 1}. {member.Name}",
            Capitalized(kind) + (facts.Count == 0 ? "." : "; " + string.Join("; ", facts) + ".")));
        panel.Children.Add(row);
        if (companions.Count > 1 || member.OnlyFor.Count > 0)
        {
            var only = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(only, $"{id}-OnlyFor-{i}");
            AutomationProperties.SetName(only, $"Which companion PCs may use {member.Name} for {area.Title}");
            only.Items.Add(new ComboBoxItem { Content = "Every companion PC", Tag = "" });
            foreach (var (device, name) in companions) only.Items.Add(new ComboBoxItem { Content = "Only " + name, Tag = device });
            foreach (var device in member.OnlyFor.Where(d => companions.All(c => c.Device != d)))
                only.Items.Add(new ComboBoxItem { Content = "Only " + device, Tag = device });
            only.SelectedIndex = member.OnlyFor.Count == 0 ? 0
                : Math.Max(0, only.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => (string)item.Tag == member.OnlyFor[0]));
            only.SelectionChanged += (_, _) =>
            {
                if (only.SelectedItem is not ComboBoxItem { Tag: string device }) return;
                if (device.Length == 0 ? member.OnlyFor.Count == 0 : member.OnlyFor.Count == 1 && member.OnlyFor[0] == device) return;
                SavePoolList(options, list, list.With(member with { OnlyFor = device.Length == 0 ? [] : [device] }),
                    device.Length == 0 ? $"Every companion PC may use {member.Name} for {area.Title} now." : $"{member.Name} is kept for {device} now.");
            };
            panel.Children.Add(only);
        }
        if (expanded && settings is not null)
        {
            var box = new Border { Child = settings, Margin = new Thickness(24, 6, 0, 0) };
            panel.Children.Add(box);
        }
        return panel;
    }

    private UIElement PoolAdd(PoolListOptions options, PoolList list)
    {
        var area = options.Area;
        var id = "Pool-" + area.Id;
        var add = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        add.Children.Add(Heading("Add to the list"));
        PoolMember New(PoolMember member) => options.NewMember?.Invoke(member) ?? member;
        void Add(PoolMember member) => SavePoolList(options, list, list.With(New(member)), $"{member.Name} is now in the {area.Title} list.");
        var buttons = new List<Button>();
        if (area.Takes(PoolMemberKind.ThisPc) && list.Find(PoolMember.ThisPcKey) is null)
            buttons.Add(PageButton("This PC", () => Add(PoolMember.ThisPc()), id: id + "-AddThisPc"));
        var hosts = NetworkMap.Hosts(Inputs()).Where(h => options.CanAdd?.Invoke(h) ?? true).ToArray();
        if (area.Takes(PoolMemberKind.Computer))
            foreach (var host in hosts.Where(h => list.Find(PoolMember.Computer(h.HostId).Key) is null))
                buttons.Add(PageButton(host.HostId, () => Add(PoolMember.Computer(host.HostId)), id: $"{id}-AddHost-{host.HostId}"));
        if (area.Takes(PoolMemberKind.Gpu))
            foreach (var host in hosts)
            {
                var cards = HostRoles.NvidiaCards(HardwareStore?.Find(host.HostId)) ?? 0;
                if (cards < 2) continue;
                foreach (var card in Enumerable.Range(1, Math.Min(cards, PoolMember.MaximumCard)).Where(c => list.Find(PoolMember.Gpu(host.HostId, c).Key) is null))
                    buttons.Add(PageButton($"{host.HostId}, card {card}", () => Add(PoolMember.Gpu(host.HostId, card)), id: $"{id}-AddGpu-{host.HostId}-{card}"));
            }
        if (buttons.Count == 0 && !area.Takes(PoolMemberKind.Address) && !area.Takes(PoolMemberKind.Cloud))
            add.Children.Add(Note("Every machine that can do it is in the list already. Pair another computer on Devices to add more.", new Thickness(0, 0, 0, 0)));
        add.Children.Add(Row([.. buttons]));
        if (area.Takes(PoolMemberKind.Address))
        {
            var address = new TextBox { MinWidth = 260, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(address, id + "-Address");
            AutomationProperties.SetName(address, $"Address of a service you run for {area.Title}");
            var addAddress = PageButton("Add address", () =>
            {
                var text = address.Text.Trim();
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    ActionText.Text = "Enter an address that starts with http:// or https://.";
                    return;
                }
                Add(PoolMember.Service(uri.AbsoluteUri));
            }, id: id + "-AddAddress");
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            line.Children.Add(address);
            line.Children.Add(addAddress);
            add.Children.Add(line);
        }
        if (area.Takes(PoolMemberKind.Cloud) && options.AddCloud is { } cloud) add.Children.Add(cloud());
        return add;
    }

    /// <summary>Saves <paramref name="next"/> as the area's list, shares it with your other computers (a shared area) and lets
    /// the area follow it; false when it couldn't be saved.</summary>
    private bool SavePoolList(PoolListOptions options, PoolList before, PoolList next, string done)
    {
        if (store is null) return false;
        var area = options.Area;
        if (!PoolSettings.SaveFor(store.DataDirectory, area, next))
        {
            ActionText.Text = $"Couldn't save the {area.Title} list on this PC.";
            return false;
        }
        WorkSharingRoster.Forget();
        ErrorLog.Info($"Pools: {done}");
        ActionText.Text = done + (area.Shared ? " Your other computers follow it." : "");
        if (area.Shared) QueueSettingsSync();
        // Rebuilt after the click's own event finishes, so the control that changed isn't replaced under it.
        Dispatcher.InvokeAsync(async () =>
        {
            if (options.Saved is { } saved)
            {
                try { await saved(before, next); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException)
                {
                    ActionText.Text = $"{done} But {area.Title} couldn't follow it yet: {error.Message}";
                }
            }
            if (!closing) (options.Rerender ?? RenderTab)();
        });
        return true;
    }

    // ---------- Speaking, Listening and Thinking ----------

    /// <summary>A job's route as a pool member: this PC (a route on this PC, or on this PC's own host service), a paired computer
    /// or a cloud provider (agreed to when the route was); null when there is no route.</summary>
    private PoolMember? RouteMember(PoolArea area, SetupRoute? route)
    {
        if (route is null) return null;
        if (SelfHostSetup.IsGateway(route.RouteType) && route.Gateway is { } gateway)
            return gateway.HostId == ThisPcHost()?.HostId && !area.ConversationFirst ? PoolMember.ThisPc() : PoolMember.Computer(gateway.HostId);
        if (RunsHereWithoutHost(route)) return PoolMember.ThisPc();
        var member = PoolCloud.MemberOf(route);
        return member is not null && route.Consent is not null ? member.WithConsent(area.Id, DateTimeOffset.UtcNow) : member;
    }

    /// <summary>Makes the Speaking, Listening and Thinking lists once, from Devices › Sharing work and each job's route, so no
    /// order, never-use or kept-for choice is lost. An area that already has a list (made here or shared by another computer)
    /// keeps it. Settings that couldn't be read make nothing: a list made without the route would lose it.</summary>
    private void EnsurePools()
    {
        if (store is null || homeSettings is null && homeSettingsState != SettingsLoadState.FirstRun) return;
        var made = false;
        foreach (var (area, role, job) in RoutePools)
        {
            if (WorkSharingRoster.Pool(store.DataDirectory, area) is { } existing)
            {
                // A graphics card of a computer is one row for its computer here: a host runs one route per engine.
                if (existing.Members.Any(m => m.Kind == PoolMemberKind.Gpu))
                {
                    var rows = existing.Members.Select(m => m.Kind == PoolMemberKind.Gpu ? m with { Kind = PoolMemberKind.Computer, Card = null } : m)
                        .DistinctBy(m => m.Key).ToArray();
                    made |= PoolSettings.SaveFor(store.DataDirectory, area, existing with { Members = rows });
                }
                continue;
            }
            var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == role);
            var list = PoolMigration.FromWorkSharing(area, SharingSettings(), RouteMember(area, route), SharingPlaces(job));
            // Nothing to keep: no list yet, so a new computer never shares an empty list over another computer's choices.
            if (list.Members.Count == 0) continue;
            if (!PoolSettings.SaveFor(store.DataDirectory, area, list)) continue;
            made = true;
            ErrorLog.Info($"Pools: made the {area.Title} list from Sharing work: {(list.Members.Count == 0 ? "empty" : string.Join(", ", list.Members.Select(m => m.Key)))}.");
        }
        if (!made) return;
        WorkSharingRoster.Forget();
        QueueSettingsSync();
    }

    /// <summary>A route chosen on the page (a card's Use) is the list's first member: it is added when missing, and moved to the
    /// front when the route changed since the page last looked.</summary>
    private void FollowRouteIntoPool(PoolArea area, SetupRoute? route)
    {
        if (store is null || area.ConversationFirst) return;
        var member = RouteMember(area, route);
        var known = poolRouteSeen.TryGetValue(area.Id, out var seen);
        poolRouteSeen[area.Id] = member?.Key;
        if (member is null || route!.Enabled == false || WorkSharingRoster.Pool(store.DataDirectory, area) is not { } list) return;
        // A cloud route's key stays the member's own (pool-keys.json), so it can take its turn after the route moves elsewhere.
        if (member.Kind == PoolMemberKind.Cloud && route.CredentialId is { } credential)
        {
            var keys = PoolKeys.Load(store.DataDirectory);
            if (keys.For(area.Id, member.Key) != credential && keys.With(area.Id, member.Key, credential).Save(store.DataDirectory))
                WorkSharingRoster.Forget();
        }
        PoolList next;
        if (list.Find(member.Key) is null) next = list with { Members = [member, .. list.Members] };
        else if (known && seen != member.Key && list.Members.FirstOrDefault(m => !m.Off)?.Key != member.Key)
            next = list.With(list.Find(member.Key)! with { Off = false }).Move(member.Key, -PoolSettings.MaximumMembers);
        else if (member.Setting(PoolSettingKeys.Voice) is { } voice && list.Find(member.Key)!.Setting(PoolSettingKeys.Voice) != voice)
            next = list.With(list.Find(member.Key)!.WithSetting(PoolSettingKeys.Voice, voice));
        else return;
        if (!PoolSettings.SaveFor(store.DataDirectory, area, next)) return;
        WorkSharingRoster.Forget();
        QueueSettingsSync();
        ErrorLog.Info($"Pools: {member.Name} does {area.Title} now, so it is first in the list.");
    }

    /// <summary>Speaking and Listening follow their lists: with no member on, the route is turned off (no voice, or typing only);
    /// with one on again, it is turned back on. A change made on another computer follows here too.</summary>
    private async Task FollowPoolOnOffAsync(PoolArea area, SetupRole role)
    {
        if (store is null || setupService is null || area.ConversationFirst) return;
        if (WorkSharingRoster.Pool(store.DataDirectory, area) is not { } list) return;
        var loaded = await setupService.LoadAsync(lifetime.Token);
        if (loaded.Error is not null || loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == role) is not { } route) return;
        var off = list.NoneOn;
        if (off == (route.Enabled == false)) return;
        var next = SetupSettings.SetRouteEnabled(UseModels(SetupSettings.Begin(loaded.Settings)), role, !off, recordSelection: true);
        var saved = await setupService.SaveAsync(next, loaded.Revision, lifetime.Token);
        if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        homeSettings = next;
        FollowSavedSetup(saved.Save.Revision);
        ErrorLog.Info($"Pools: {area.Title} is {(off ? "off: nothing in its list is on" : "on again")}.");
        ActionText.Text = off ? $"{area.Title} is off: nothing in its list is on." : $"{area.Title} is on again.";
    }

    /// <summary>The first member that is on changed: Speaking and Listening switch their route to it when the kind of route
    /// changes (a computer after a cloud provider, say). Between computers no switch is needed: requests follow the list.</summary>
    private async Task LeadPoolAsync(PoolArea area, SetupRole role, PoolList before, PoolList after)
    {
        await FollowPoolOnOffAsync(area, role);
        var lead = after.Members.FirstOrDefault(m => !m.Off);
        if (lead is null || lead.Key == before.Members.FirstOrDefault(m => !m.Off)?.Key) return;
        var job = HostJob.For(role)!;
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == role);
        if (RouteMember(area, route)?.Key == lead.Key) return;
        var section = TabFor(role);
        var hostRoute = SelfHostSetup.IsGateway(route?.RouteType);
        var own = ThisPcHost()?.HostId;
        switch (lead.Kind)
        {
            case PoolMemberKind.Computer or PoolMemberKind.Gpu when !hostRoute:
                await AssignJobAsync(job, "host:" + lead.HostId);
                break;
            case PoolMemberKind.ThisPc when role == SetupRole.Stt && route is not null && RunsHereWithoutHost(route):
                break;
            case PoolMemberKind.ThisPc when !hostRoute && own is not null && role == SetupRole.Tts:
                await AssignJobAsync(job, "host:" + own);
                break;
            case PoolMemberKind.ThisPc when !hostRoute || role == SetupRole.Stt:
                tabPlace[section] = JobPlaceOf(lead);
                ActionText.Text = $"Choose how this PC does {job.Job} below.";
                break;
            case PoolMemberKind.Cloud when SavedRoute(job) is { } saved && RouteMember(area, saved)?.Key == lead.Key:
                await AssignJobAsync(job, "saved");
                break;
            case PoolMemberKind.Cloud:
                tabPlace[section] = JobPlace.Cloud;
                ActionText.Text = $"Set up {lead.Name} below, then choose Use, so {job.Job} goes there first.";
                break;
        }
    }

    private static JobPlace JobPlaceOf(PoolMember member) => member.Kind switch
    {
        PoolMemberKind.ThisPc => JobPlace.ThisPc,
        PoolMemberKind.Cloud => JobPlace.Cloud,
        _ => JobPlace.Computer
    };

    /// <summary>The pool list of Companion › Voice, Listening or Thinking. Speaking and Listening: the list replaces the single
    /// place; the first member that is on is the route the conversation uses, the next ones take requests when it is busy, and
    /// with none on the job is off. A member's Settings shows its place's own card under the list (its engine, model, voice or
    /// key). Thinking: the conversation's own model always goes first; the list names the computers a reply may go to when it is
    /// busy (none by default: another computer doesn't have the conversation in its prompt cache).</summary>
    private Border JobPoolCard(CompanionTab section, SetupRole role, SetupRoute? route)
    {
        var (area, _, _) = RoutePools.First(p => p.Role == role);
        var job = HostJob.For(role)!;
        EnsurePools();
        FollowRouteIntoPool(area, route);
        var routeMember = RouteMember(area, route);
        var roleKind = job.HostRoleKind;
        bool Runs(string? hostId, string? kind = null) => hostId is not null &&
            hostChecks.GetValueOrDefault(hostId)?.Offers?.Keys.Any(k => k == (kind ?? roleKind) || role == SetupRole.Tts && HostRoles.Speaks(k)) == true;
        string? Status(PoolMember member)
        {
            var parts = new List<string>();
            if (member.Key == routeMember?.Key && route?.Enabled != false) parts.Add("in use: " + PlaceName(route!));
            var hostId = member.Kind == PoolMemberKind.ThisPc ? ThisPcHost()?.HostId : member.HostId;
            if (member.OnHost || member.Kind == PoolMemberKind.ThisPc && hostId is not null)
            {
                var check = hostChecks.GetValueOrDefault(hostId!);
                var offers = check?.Offers?.Where(o => o.Key == roleKind || role == SetupRole.Tts && HostRoles.Speaks(o.Key)).ToArray() ?? [];
                parts.Add(offers.Length > 0 ? "runs " + string.Join(", ", offers.Select(o => $"{HostRoles.Get(o.Key).Name} ({HostInputDialog.OptionText(o.Value)})"))
                    : check?.Reachable == false ? "not reachable right now"
                    : check?.Reachable == true ? $"doesn't run {job.Engine} yet" : "not checked yet");
            }
            if (member.Kind == PoolMemberKind.Cloud && member.Key != routeMember?.Key)
                parts.Add(PoolCloud.Usable(store?.DataDirectory, role, member)
                    ? "takes a request in its turn when the ones before it are busy or don't answer"
                    : "can't take a turn on this PC yet: set it up below and choose Use once, so its key is here");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
        UIElement? Settings(PoolMember member)
        {
            var panel = new WrapPanel();
            var hostId = member.Kind == PoolMemberKind.ThisPc ? ThisPcHost()?.HostId : member.HostId;
            if (hostId is not null && FindHost(hostId) is { } host && Runs(hostId) && hostChecks.GetValueOrDefault(hostId)?.Reachable == true && ChangesRolesOn(host))
            {
                var kind = hostChecks[hostId].Offers!.Keys.FirstOrDefault(k => k == roleKind || role == SetupRole.Tts && HostRoles.Speaks(k)) ?? roleKind;
                var change = PageButton("Change model", () => LaunchOnHost(host, HostAction.Change(kind)), id: $"Pool-{area.Id}-Change-{member.Key}");
                AutomationProperties.SetName(change, $"Change {job.Engine} on {hostId}");
                change.Margin = new Thickness(0, 0, 8, 6);
                panel.Children.Add(change);
            }
            if (!area.ConversationFirst)
            {
                var show = PageButton(tabPlace.GetValueOrDefault(section) == JobPlaceOf(member) ? "Shown below" : "Show its setup below", () =>
                {
                    tabPlace[section] = JobPlaceOf(member);
                    RenderTab();
                }, id: $"Pool-{area.Id}-Show-{member.Key}");
                show.Margin = new Thickness(0, 0, 8, 6);
                panel.Children.Add(show);
            }
            return panel.Children.Count == 0 ? null : panel;
        }
        UIElement AddCloud()
        {
            var cloud = PageButton("A cloud provider", () =>
            {
                tabPlace[section] = JobPlace.Cloud;
                RenderTab();
            }, id: $"Pool-{area.Id}-AddCloud");
            AutomationProperties.SetName(cloud, $"Add a cloud provider to {area.Title}: set it up below and choose Use");
            var line = Row(cloud);
            line.Margin = new Thickness(0, 4, 0, 0);
            var why = Note("Set up the provider, its model and your key below. Choosing Use puts it first in the list; move it down with Down.",
                new Thickness(0, 2, 0, 0));
            var stack = new StackPanel();
            stack.Children.Add(line);
            stack.Children.Add(why);
            return stack;
        }
        return PoolListCard(new PoolListOptions
        {
            Area = area,
            Heading = area.ConversationFirst ? "When the Thinking model is busy" : "Where it runs",
            Intro = area.ConversationFirst
                ? "A reply always tries the conversation's own model first, so nothing changes while it is free. Add computers that run " +
                  "the same model to take a reply when it is busy. Empty: a reply waits for its own model. Another computer starts " +
                  "without the conversation in its prompt cache, so its first word comes later."
                : $"The machines that do {job.Job}, in order. The first one that is on does it; the next ones take a request when it is " +
                  $"busy. Turn every one off, or remove them, to turn {job.Job} off. Each machine keeps its own settings.",
            Status = Status,
            Settings = Settings,
            CanAdd = host => !area.ConversationFirst || host.HostId != routeMember?.HostId && Runs(host.HostId),
            AddCloud = area.ConversationFirst ? null : AddCloud,
            Saved = area.ConversationFirst ? null : (before, after) => LeadPoolAsync(area, role, before, after)
        });
    }
}
