using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.Devices.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>How this desktop reaches a host to install or remove its roles and update it: through Martlet on that computer
    /// (its paired connection; the default), SSH (Docker or native Linux) or this PC's Docker Desktop.</summary>
    private StackPanel ReachEditor(PairedHost host)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 4, 0, 4), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        var now = new TextBlock { Text = $"Reached via: {host.Reach}.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(now, "HostReachNow");
        panel.Children.Add(now);
        var method = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(method, "Connection method");
        AutomationProperties.SetAutomationId(method, "HostReachMethod");
        foreach (var (value, text) in new[]
        {
            (HostSetupMethod.Agent, "Through Martlet on that computer (paired connection)"),
            (HostSetupMethod.SshDocker, "SSH to a Docker host"), (HostSetupMethod.SshNative, "SSH to an Ubuntu host"),
            (HostSetupMethod.ThisPcDocker, "This PC with Docker Desktop")
        })
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            method.Items.Add(item);
            if (value == host.Method || value == HostSetupMethod.Agent && host.Method == HostSetupMethod.OnHost) method.SelectedItem = item;
        }
        var hint = HelpTip.Explain("Martlet on that computer runs the commands over your paired connection; use SSH for a Linux computer " +
            "without Martlet. The connection is pinned and signed, and commands wait until Martlet runs on that computer.",
            new Thickness(0, 0, 0, 8), "DeviceReachMethod", "connection method", wiki: "Multiple-Computers#machine-cards");
        AutomationProperties.SetAutomationId(hint, "HostReachHint");
        var label = new TextBlock { Text = "SSH target, for example me@192.168.1.20", Margin = new Thickness(0, 0, 0, 4) };
        label.SetResourceReference(StyleProperty, "Muted");
        var ssh = new TextBox { Text = host.SshTarget ?? "", Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(ssh, "SSH target (user@computer)");
        AutomationProperties.SetAutomationId(ssh, "HostReachSsh");
        InputHint.SetText(ssh, "me@192.168.1.20");
        void Toggle()
        {
            var usesSsh = method.SelectedItem is ComboBoxItem { Tag: HostSetupMethod.SshDocker or HostSetupMethod.SshNative };
            ssh.IsEnabled = label.IsEnabled = usesSsh;
            if (usesSsh && ssh.Text.Length == 0) ssh.Text = host.Address;
        }
        Toggle();
        // No Save button: choosing a method saves at once; the SSH target saves when you leave the field or press Enter (or
        // after a pause in typing). The map isn't rebuilt under your typing.
        var reachSave = new AutoSave(async () =>
        {
            if (method.SelectedItem is not ComboBoxItem { Tag: HostSetupMethod chosen } || closing || store is null) return true;
            var target = ssh.Text.Trim();
            if (chosen == host.Method && (chosen is not (HostSetupMethod.SshDocker or HostSetupMethod.SshNative) || target == (host.SshTarget ?? "")))
                return true;
            ChangeTurns.Turn? turn = null;
            try
            {
                turn = await ChangeTurnAsync();
                var updated = await Pairings().SetReachAsync(host.HostId, chosen, target, Version, lifetime.Token);
                homeHosts = HostRegistry.Upsert(homeHosts, updated);
                host = updated;
                now.Text = $"Reached via: {updated.Reach}.";
                ActionText.Text = $"Saved how Martlet reaches {updated.HostId}.";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
                JsonException or ArgumentException)
            {
                ActionText.Text = $"Not saved yet: {error.Message}";
            }
            finally { turn?.Dispose(); }
            return true;
        }, TimeSpan.FromSeconds(1.5));
        method.SelectionChanged += (_, _) =>
        {
            Toggle();
            reachSave.SaveNowAsync().Forget();
        };
        ssh.TextChanged += (_, _) => reachSave.Changed();
        ssh.LostKeyboardFocus += (_, _) => { if (reachSave.Pending) reachSave.SaveNowAsync().Forget(); };
        ssh.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) reachSave.SaveNowAsync().Forget(); };
        panel.Children.Add(method);
        panel.Children.Add(hint);
        panel.Children.Add(label);
        panel.Children.Add(ssh);
        return panel;
    }

    // ---------- settings for all devices ----------

    private void RenderDeviceSettings(IReadOnlyList<NetworkNode> nodes)
    {
        var companion = Role == DeviceRole.Companion;
        RolesCard.Visibility = companion ? Visibility.Visible : Visibility.Collapsed;
        if (!companion)
        {
            RolesCoverage.Children.Clear();
            return;
        }
        EvaluateCoverage();
        string? NodeFor(string job)
        {
            var component = job == ClusterJobs.LipSync ? DeviceComponent.LipSync
                : HostJob.All.FirstOrDefault(j => j.Job == job) is { } hostJob ? DeviceComponent.Job(hostJob.Role) : null;
            return nodes.FirstOrDefault(n => n.Roles.Any(r => r.Component == component))?.Id;
        }
        ShowCoverage(RolesCoverage, NodeFor);
        RenderUnassignedJobs();
    }

    /// <summary>Jobs no device does yet (no route chosen), so they still have a place to be set up.</summary>
    private void RenderUnassignedJobs()
    {
        UnassignedJobs.Children.Clear();
        var routes = homeSettings?.Setup?.Routes ?? [];
        var missing = HostJob.All.Where(job => routes.All(r => r.Role != job.Role)).ToList();
        if (missing.Count == 0) return;
        var heading = new TextBlock { Text = "Not set up yet", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) };
        UnassignedJobs.Children.Add(heading);
        foreach (var job in missing)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var setUp = new Button { Content = "Set up in Companion", VerticalAlignment = VerticalAlignment.Center };
            CompactButton(setUp, job.Role == SetupRole.Llm);
            setUp.Margin = new Thickness(12, 0, 0, 0);
            AutomationProperties.SetAutomationId(setUp, "RoleSetup-" + job.Role);
            var tab = TabFor(job.Role);
            setUp.Click += (_, _) => OpenCompanion(tab);
            DockPanel.SetDock(setUp, Dock.Right);
            row.Children.Add(setUp);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = NetworkMap.RoleName(job.Role) });
            var effect = new TextBlock { Text = JobCoverageRules.Effect(job.Job), FontSize = 13 };
            effect.SetResourceReference(StyleProperty, "Muted");
            text.Children.Add(effect);
            row.Children.Add(text);
            UnassignedJobs.Children.Add(row);
        }
    }
}
