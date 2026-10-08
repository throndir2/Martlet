using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;

namespace Martlet.Desktop;

/// <summary>Devices › Sharing work (docs/CLUSTER.md#sharing-work-between-your-computers): for each job, whether a companion PC
/// may send it to another computer that runs the same engine when the one doing it is busy, which computers it tries first and
/// which never; and which computers are kept for one companion PC. The choices are work-sharing.json, shared with all your
/// computers; requests follow them through <see cref="WorkSharingRoster"/> and <see cref="WorkQueue"/>.</summary>
public partial class MainWindow
{
    private WorkSharingSettings SharingSettings() => WorkSharingRoster.Settings(store?.DataDirectory);

    /// <summary>The host role that does a shared job.</summary>
    private static string SharingRole(string job) => job switch
    {
        WorkSharingJobs.Thinking => HostRoles.Ollama,
        WorkSharingJobs.Listening => HostRoles.Stt,
        WorkSharingJobs.DeepThinking => HostRoles.DeepThinking,
        _ => HostRoles.Speaking
    };

    /// <summary>The paired computer that does <paramref name="job"/> for this PC now, if a computer does.</summary>
    private string? SharingPlanned(string job) => job switch
    {
        WorkSharingJobs.Thinking => NetworkMap.JobHost(homeSettings, Martlet.Core.Settings.SetupRole.Llm),
        WorkSharingJobs.Listening => NetworkMap.JobHost(homeSettings, Martlet.Core.Settings.SetupRole.Stt),
        WorkSharingJobs.Speaking => NetworkMap.JobHost(homeSettings, Martlet.Core.Settings.SetupRole.Tts),
        _ => null
    };

    /// <summary>The paired computers that run <paramref name="job"/>'s engine, as the shared plan last saw them.</summary>
    private IReadOnlyList<WorkPlace> SharingPlaces(string job)
    {
        var kind = SharingRole(job);
        var own = OwnHostId();
        var hosts = NetworkMap.Hosts(Inputs());
        return [.. clusterPlan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == kind) && hosts.Any(h => h.HostId == n.HostId))
            .Select(n => new WorkPlace(n.HostId, n.HostId == own, clusterPlan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == n.HostId)))];
    }

    /// <summary>Companion PCs a computer can be kept for: this PC and the other members that are companions, by device ID.</summary>
    private IReadOnlyList<(string Device, string Name)> SharingCompanions()
    {
        List<(string, string)> list = [(WorkSharingRoster.Device, $"This PC ({WorkSharingRoster.Device})")];
        if (Role == DeviceRole.Host) list.Clear();
        foreach (var computer in OtherComputers().Where(c => c.Standing == ComputerStanding.Member && c.Role != DeviceRole.Host)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            if (list.All(c => c.Item1 != computer.DeviceId)) list.Add((computer.DeviceId, computer.Name == computer.DeviceId ? computer.Name : $"{computer.Name} ({computer.DeviceId})"));
        return list;
    }

    private void RenderWorkSharing()
    {
        WorkSharingJobsPanel.Children.Clear();
        WorkSharingHostsPanel.Children.Clear();
        if (store is null)
        {
            WorkSharingStatusText.Text = "Unavailable without a local data folder.";
            return;
        }
        var settings = SharingSettings();
        WorkSharingStatusText.Text = WorkSharingStatusLine();
        foreach (var job in WorkSharingJobs.All) WorkSharingJobsPanel.Children.Add(SharingJobCard(settings, job));
        var hosts = clusterPlan.Nodes.Where(n => !n.Removed).Select(n => n.HostId)
            .Union(NetworkMap.Hosts(Inputs()).Select(h => h.HostId), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (hosts.Length == 0) return;
        var heading = new TextBlock { Text = "Keep a computer for one companion PC", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) };
        WorkSharingHostsPanel.Children.Add(heading);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            Text = "A computer kept for one companion PC takes no work from the others, whatever their order says." };
        note.SetResourceReference(StyleProperty, "Muted");
        WorkSharingHostsPanel.Children.Add(note);
        var companions = SharingCompanions();
        foreach (var host in hosts) WorkSharingHostsPanel.Children.Add(SharingKeepRow(settings, host, companions));
    }

    private static string WorkSharingStatusLine()
    {
        var recent = WorkSharingRoster.Recent();
        if (recent.Count == 0) return "Since Martlet started, no request from this PC needed another computer.";
        return "Since Martlet started: " + string.Join("; ", recent.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r =>
            $"{WorkSharingJobs.Title(r.Key)} went elsewhere {r.Value.Count} time{(r.Value.Count == 1 ? "" : "s")} (last to {r.Value.Last.HostId}" +
            (r.Value.Last.Busy > 0 ? $", {r.Value.Last.Busy} busy" : "") + (r.Value.Last.Waited.TotalMilliseconds >= 50 ? $", waited {r.Value.Last.Waited.TotalMilliseconds:0} ms" : "") + ")")) + ".";
    }

    private FrameworkElement SharingJobCard(WorkSharingSettings settings, string job)
    {
        var rules = settings.Job(job);
        var stack = new StackPanel();
        var deep = job == WorkSharingJobs.DeepThinking;
        var planned = SharingPlanned(job);
        var places = SharingPlaces(job);
        var detail = deep
            ? "The Thinking pool works on the computers that joined it in Companion › Thinking pool. Untick one here to keep every companion PC's pool jobs off it."
            : job == WorkSharingJobs.Thinking
                ? "Off by default: another computer's model starts your conversation without its prompt cache, so its first word comes later, " +
                  "and it pushes that computer's own conversation out of its cache. While off, a reply waits for its computer to finish another."
                : "When the computer doing it is busy with another companion PC, the next one in this order takes it; when all are busy it waits " +
                  "for whichever finishes first.";
        stack.Children.Add(NetworkRowText("WorkSharingJob-" + job, WorkSharingJobs.Title(job), detail));
        if (!deep)
        {
            var share = new CheckBox { Content = "Use another computer when this one is busy", IsChecked = rules.Shares, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetAutomationId(share, "WorkSharingShare-" + job);
            share.Checked += (_, _) => SaveSharing(settings.With(rules with { Share = true }), $"{WorkSharingJobs.Title(job)} now goes to another computer when its own is busy.");
            share.Unchecked += (_, _) => SaveSharing(settings.With(rules with { Share = false }), $"{WorkSharingJobs.Title(job)} now waits for its own computer.");
            stack.Children.Add(share);
            var ownFirst = new CheckBox { Content = "Each companion PC tries its own computer first", Margin = new Thickness(0, 4, 0, 0),
                IsChecked = rules.Order.FirstOrDefault() == WorkSharingSettings.ThisPc, IsEnabled = rules.Shares };
            AutomationProperties.SetAutomationId(ownFirst, "WorkSharingOwnFirst-" + job);
            ownFirst.ToolTip = "A companion PC that runs this job on its own host service uses that first, with no network hop.";
            ownFirst.Checked += (_, _) => SaveSharing(settings.With(rules with { Order = [WorkSharingSettings.ThisPc, .. rules.Order] }),
                $"Each companion PC now tries its own computer first for {WorkSharingJobs.Title(job)}.");
            ownFirst.Unchecked += (_, _) => SaveSharing(settings.With(rules with { Order = [.. rules.Order.Where(o => o != WorkSharingSettings.ThisPc)] }),
                $"{WorkSharingJobs.Title(job)} no longer tries each companion PC's own computer first.");
            stack.Children.Add(ownFirst);
        }
        // Shown in the order this PC tries them, the computers never used last.
        var everyone = settings.With(rules with { Never = [] });
        var shown = deep ? places.Select(p => p.HostId).Order(StringComparer.Ordinal).ToList()
            : [.. WorkSharing.Order(everyone with { Hosts = [] }, job, WorkSharingRoster.Device, planned, places)];
        if (shown.Count == 0)
        {
            var none = new TextBlock { Text = "No paired computer runs it yet.", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
            none.SetResourceReference(StyleProperty, "Muted");
            stack.Children.Add(none);
        }
        var own = OwnHostId();
        var ownLocked = rules.Order.FirstOrDefault() == WorkSharingSettings.ThisPc;
        for (var i = 0; i < shown.Count; i++)
        {
            var host = shown[i];
            var row = NetworkRowFrame();
            row.Margin = new Thickness(0, 6, 0, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!deep)
            {
                var index = i;
                var up = new Button { Content = "Up", Margin = new Thickness(6, 0, 0, 0), IsEnabled = rules.Shares && i > (ownLocked && shown[0] == own ? 1 : 0) && !(ownLocked && host == own) };
                AutomationProperties.SetAutomationId(up, $"WorkSharingUp-{job}-{host}");
                AutomationProperties.SetName(up, $"Try {host} earlier for {WorkSharingJobs.Title(job)}");
                up.Click += (_, _) => MoveSharing(settings, rules, shown, index, -1, own, ownLocked);
                var down = new Button { Content = "Down", Margin = new Thickness(6, 0, 0, 0), IsEnabled = rules.Shares && i < shown.Count - 1 && !(ownLocked && host == own) };
                AutomationProperties.SetAutomationId(down, $"WorkSharingDown-{job}-{host}");
                AutomationProperties.SetName(down, $"Try {host} later for {WorkSharingJobs.Title(job)}");
                down.Click += (_, _) => MoveSharing(settings, rules, shown, index, 1, own, ownLocked);
                buttons.Children.Add(up);
                buttons.Children.Add(down);
            }
            var use = new CheckBox { Content = "Use", IsChecked = !rules.Never.Contains(host), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(use, $"WorkSharingUse-{job}-{host}");
            AutomationProperties.SetName(use, $"Use {host} for {WorkSharingJobs.Title(job)}");
            use.Checked += (_, _) => SaveSharing(settings.With(rules with { Never = [.. rules.Never.Where(n => n != host)] }),
                $"{WorkSharingJobs.Title(job)} may use {host} again.");
            use.Unchecked += (_, _) => SaveSharing(settings.With(rules with { Never = [.. rules.Never, host] }),
                $"{WorkSharingJobs.Title(job)} never uses {host} now.");
            buttons.Children.Add(use);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var facts = new List<string>();
            if (host == own) facts.Add("this PC's own");
            if (host == planned) facts.Add("does it for this PC now");
            if (settings.OnlyFor(host) is { Count: > 0 } only) facts.Add("kept for " + string.Join(" and ", only));
            if (rules.Never.Contains(host)) facts.Add("never used for it");
            row.Children.Add(NetworkRowText($"WorkSharingPlace-{job}-{host}", deep ? host : $"{i + 1}. {host}",
                facts.Count == 0 ? "Runs it." : string.Join("; ", facts) + "."));
            stack.Children.Add(row);
        }
        return NetworkRowCard(stack, warning: false);
    }

    // Moves a computer up or down this PC's order and saves it as the order every companion PC uses (each one's own computer
    // stays first when that is chosen).
    private void MoveSharing(WorkSharingSettings settings, WorkSharingJob rules, IReadOnlyList<string> shown, int index, int by, string? own, bool ownLocked)
    {
        var list = shown.ToList();
        var target = index + by;
        if (target < 0 || target >= list.Count) return;
        (list[index], list[target]) = (list[target], list[index]);
        IReadOnlyList<string> order = ownLocked ? [WorkSharingSettings.ThisPc, .. list.Where(h => h != own)] : list;
        SaveSharing(settings.With(rules with { Order = order }), $"{WorkSharingJobs.Title(rules.Job)} now tries {shown[index]} {(by < 0 ? "earlier" : "later")}.");
    }

    private FrameworkElement SharingKeepRow(WorkSharingSettings settings, string host, IReadOnlyList<(string Device, string Name)> companions)
    {
        var row = NetworkRowFrame();
        row.Margin = new Thickness(0, 6, 0, 0);
        var only = settings.OnlyFor(host);
        var choice = new ComboBox { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(choice, "WorkSharingKeep-" + host);
        AutomationProperties.SetName(choice, $"Which companion PCs may use {host}");
        choice.Items.Add(new ComboBoxItem { Content = "Every companion PC", Tag = "" });
        foreach (var (device, name) in companions) choice.Items.Add(new ComboBoxItem { Content = "Only " + name, Tag = device });
        foreach (var device in only.Where(d => companions.All(c => c.Device != d)))
            choice.Items.Add(new ComboBoxItem { Content = "Only " + device, Tag = device });
        choice.SelectedIndex = only.Count == 0 ? 0
            : Math.Max(0, choice.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => (string)item.Tag == only[0]));
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is not ComboBoxItem { Tag: string device }) return;
            if (device.Length == 0 ? only.Count == 0 : only.Count == 1 && only[0] == device) return;
            SaveSharing(settings.With(new WorkSharingHost { HostId = host, OnlyFor = device.Length == 0 ? [] : [device] }),
                device.Length == 0 ? $"Every companion PC may use {host} now." : $"{host} is kept for {device} now.");
        };
        DockPanel.SetDock(choice, Dock.Right);
        row.Children.Add(choice);
        row.Children.Add(NetworkRowText("WorkSharingHost-" + host, host,
            only.Count == 0 ? "Every companion PC may use it." : "Kept for " + string.Join(" and ", only) + "."));
        return row;
    }

    /// <summary>Saves Sharing work and shares it with your other computers; false when it couldn't be saved.</summary>
    private bool SaveSharing(WorkSharingSettings next, string done)
    {
        if (store is null) return false;
        if (!next.Save(store.DataDirectory))
        {
            ActionText.Text = "Couldn't save Sharing work on this PC.";
            RenderWorkSharing();
            return false;
        }
        WorkSharingRoster.Forget();
        ErrorLog.Info("Sharing work: " + done);
        ActionText.Text = done + " Your other computers follow it.";
        QueueSettingsSync();
        conversation?.ReloadThinkingPool();
        // Rebuilt after the click's own event finishes, so the control that changed isn't replaced under it.
        Dispatcher.InvokeAsync(RenderWorkSharing);
        return true;
    }
}
