using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.Pools.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
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
        ErrorLog.Info($"Pools: {done}");
        ActionText.Text = area.Shared ? done.TrimEnd('.') + ", and your other computers follow it." : done;
        if (area.Shared) QueueSettingsSync();
        // Rebuilt after the click's own event finishes, so the control that changed isn't replaced under it.
        Dispatcher.InvokeAsync(async () =>
        {
            if (options.Saved is { } saved)
            {
                try { await saved(before, next); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException)
                {
                    ActionText.Text = $"{done.TrimEnd('.')}, but {area.Title} couldn't follow it yet: {error.Message}";
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
            if (WorkSharingRoster.Shared.Pool(store.DataDirectory, area) is { } existing)
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
        if (member is null || route!.Enabled == false || WorkSharingRoster.Shared.Pool(store.DataDirectory, area) is not { } list) return;
        // A cloud route's key stays the member's own (pool-keys.json), so it can take its turn after the route moves elsewhere.
        if (member.Kind == PoolMemberKind.Cloud && route.CredentialId is { } credential)
        {
            var keys = PoolKeys.Load(store.DataDirectory);
            if (keys.For(area.Id, member.Key) != credential) keys.With(area.Id, member.Key, credential).Save(store.DataDirectory);
        }
        PoolList next;
        if (list.Find(member.Key) is null) next = list with { Members = [member, .. list.Members] };
        else if (known && seen != member.Key && list.Members.FirstOrDefault(m => !m.Off)?.Key != member.Key)
            next = list.With(list.Find(member.Key)! with { Off = false }).Move(member.Key, -PoolSettings.MaximumMembers);
        else if (member.Setting(PoolSettingKeys.Voice) is { } voice && list.Find(member.Key)!.Setting(PoolSettingKeys.Voice) != voice)
            next = list.With(list.Find(member.Key)!.WithSetting(PoolSettingKeys.Voice, voice));
        else return;
        if (!PoolSettings.SaveFor(store.DataDirectory, area, next)) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: {member.Name} does {area.Title} now, so it is first in the list.");
    }

    /// <summary>Speaking and Listening follow their lists: with no member on, the route is turned off (no voice, or typing only);
    /// with one on again, it is turned back on. A change made on another computer follows here too.</summary>
    private async Task FollowPoolOnOffAsync(PoolArea area, SetupRole role)
    {
        if (store is null || setupService is null || area.ConversationFirst) return;
        if (WorkSharingRoster.Shared.Pool(store.DataDirectory, area) is not { } list) return;
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
            var why = HelpTip.Explain("Set up the provider, its model and your key below. Choosing Use puts it first in the list; move it down with Down.",
                new Thickness(0, 2, 0, 0), "PoolAddCloud-" + area.Id, "adding a cloud provider", wiki: "Multiple-Computers#each-job-has-a-list-of-machines");
            var stack = new StackPanel();
            stack.Children.Add(line);
            stack.Children.Add(why);
            return stack;
        }
        var options = new PoolListOptions
        {
            Area = area,
            Heading = area.ConversationFirst ? "When the Thinking model is busy" : "Where it runs",
            Wiki = "Multiple-Computers#the-order-of-the-list",
            Intro = area.ConversationFirst
                ? "A reply always tries the conversation's own model first, so nothing changes while it is free. Add computers that run " +
                  "the same model to take a reply when it is busy. Empty: a reply waits for its own model. Another computer starts " +
                  "without the conversation in its prompt cache, so its first word comes later."
                : $"The machines that do {job.Job}, in order. The first one that is on does it, and the next ones help when it is busy.",
            Status = Status,
            Settings = Settings,
            CanAdd = host => !area.ConversationFirst || host.HostId != routeMember?.HostId && Runs(host.HostId),
            AddCloud = area.ConversationFirst ? null : AddCloud,
            Saved = area.ConversationFirst ? null : (before, after) => LeadPoolAsync(area, role, before, after)
        };
        return PoolListCard(area.ConversationFirst ? options with { Wiki = "Thinking#when-the-thinking-model-is-busy" } : options);
    }
}
