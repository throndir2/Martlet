using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Your computers on a job page (Where it runs › Another of your computers).</summary>
internal sealed partial class JobPage
{
    private Border ComputersCard(HostJob job, SetupRoute? route, PairedHost? exclude) =>
        ComputersCard(job.Job, job.Engine, job.HostRoleKind, NetworkMap.JobHost(services.Settings, job.Role),
            $"In use: {job.Engine} {(route?.ModelId is { } model ? HostInputDialog.OptionText(model) : null)}.",
            null,
            key => services.AssignJob(job, key), job.Disclosure, exclude, change: "Change model");

    /// <summary>Every paired host (except <paramref name="exclude"/>) that can run the job, with what it runs and <i>Use it</i>,
    /// plus <i>Add a computer</i>, <i>Check hosts</i> and the Devices map; hosts whose platform or hardware can't run it are
    /// named underneath with why. <paramref name="again"/> labels the owner's button when using it again does something
    /// (choosing another voice); otherwise the owner shows <i>In use</i>. A host that runs the role also has
    /// <paramref name="change"/>, which opens the role's settings there (its model, GPU or CPU...), showing what it runs now.</summary>
    private Border ComputersCard(string job, string engine, string roleKind, string? owner, string ownerDetail, string? again,
        Func<string, Task> assign, string disclosure, PairedHost? exclude, string change = "Change settings")
    {
        var stack = new List<UIElement> { Heading("Your computers") };
        var paired = services.PairedHosts().Where(h => h.HostId != exclude?.HostId).ToArray();
        bool Runs(PairedHost host) => services.Check(host.HostId)?.Offers?.ContainsKey(roleKind) == true;
        var unable = paired.Where(h => h.HostId != owner && !Runs(h) && services.HostCan(h.HostId, roleKind) is { Allowed: false }).ToArray();
        var hosts = paired.Except(unable).ToArray();
        if (hosts.Length == 0)
        {
            var add = $"add another computer to run {job} there.";
            var none = Note(unable.Length > 0 ? $"None of your paired computers can run {engine}, so {add}"
                : exclude is not null ? $"This PC is already selected, so {add}"
                : $"No other computers are paired yet, so {add}", new Thickness(0, 0, 0, 8));
            AutomationProperties.SetAutomationId(none, "HostChoices-" + job);
            stack.Add(none);
        }
        foreach (var host in hosts)
        {
            var check = services.Check(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(roleKind);
            var cannot = model is null ? services.CannotHand(host.HostId, roleKind, job) : null;
            var detail = JobPageModel.ComputerDetail(owner == host.HostId, ownerDetail, cannot, model, engine, check?.Reachable);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            var line = Note(detail, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetName(line, $"{host.HostId}: {detail}");
            AutomationProperties.SetAutomationId(line, $"HostChoice-{job}-{host.HostId}");
            text.Children.Add(line);
            var use = PageButton(owner == host.HostId ? again ?? "In use" : "Use it",
                () => assign("host:" + host.HostId).Forget(), primary: owner != host.HostId && cannot is null, id: $"SetupUseHost-{job}-{host.HostId}");
            use.IsEnabled = cannot is null && !(owner == host.HostId && again is null);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (model is not null && check?.Reachable == true && services.ChangesRolesOn(host))
            {
                var settings = PageButton(change, () => services.ChangeRoleOn(host, roleKind), id: $"SetupChangeHost-{job}-{host.HostId}");
                AutomationProperties.SetName(settings, $"{change}: {engine} on {host.HostId} (now {model})");
                settings.Margin = new Thickness(0, 0, 8, 0);
                buttons.Children.Add(settings);
            }
            buttons.Children.Add(use);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);
            stack.Add(row);
        }
        if (unable.Length > 0)
        {
            var why = Note(string.Join(" ", unable.Select(h => $"{h.HostId} can't run {engine}: {services.HostCan(h.HostId, roleKind)!.Reason}")),
                new Thickness(0, 0, 0, 8));
            AutomationProperties.SetAutomationId(why, "HostChoicesUnable-" + job);
            stack.Add(why);
        }
        // Hosts friends share with this PC: this PC only, never your network's choice; their engines are their owners'.
        if (services.SharedHosts.Count > 0) stack.Add(Heading("Shared with this PC"));
        foreach (var host in services.SharedHosts)
        {
            var check = services.Check(host.HostId);
            var offers = check?.Offers?.Keys.Any(k => k == roleKind || HostRoles.Speaks(roleKind) && HostRoles.Speaks(k)) == true;
            var detail = JobPageModel.SharedDetail(owner == host.HostId, ownerDetail, offers, engine, check?.Reachable);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            var line = Note(detail, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetName(line, $"{host.HostId}: {detail}");
            AutomationProperties.SetAutomationId(line, $"HostChoice-{job}-{host.HostId}");
            text.Children.Add(line);
            var use = PageButton(owner == host.HostId ? again ?? "In use" : "Use it on this PC",
                () => assign("host:" + host.HostId).Forget(), id: $"SetupUseHost-{job}-{host.HostId}");
            var inUse = owner == host.HostId && again is null;
            use.IsEnabled = !inUse && (offers || check?.Reachable != true);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(text);
            stack.Add(row);
        }
        stack.Add(Row(
            PageButton("Add a computer", () => services.RunNodeAction(NodeAction.AddComputer), primary: hosts.Length == 0, id: "SetupAddComputer-" + job),
            paired.Length == 0 ? null : PageButton("Check hosts", () => services.RunNodeAction(NodeAction.CheckHost), id: "SetupCheckHosts-" + job),
            PageButton("Open Devices", services.OpenDevices, id: "SetupOpenMap-" + job)));
        // What goes to that computer shows; the "?" also has the voice engine's model licence.
        stack.Add(HelpTip.Explain(disclosure, new Thickness(0, 8, 0, 0), "HostJobData-" + job, $"{job} on your computers",
            wiki: "Multiple-Computers#hand-a-job-to-another-computer"));
        return Card([.. stack]);
    }
}
