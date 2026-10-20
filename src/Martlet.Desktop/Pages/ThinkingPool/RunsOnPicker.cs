using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>A Runs on choice for a kind of Thinking pool job: Companion › Thinking pool's Runs on card has one for each kind of
/// job, and each check-in's card (Companion › Check-ins) has its own.</summary>
internal static class RunsOnPicker
{
    /// <summary>A Runs on choice: a list (<paramref name="id"/>) with Any member, Prefer smart, Smart only and These members
    /// (and, with <paramref name="inherit"/>, Like other check-ins first, which reads as null), and a box for each machine in the
    /// pool with its smarts (<paramref name="memberId"/>-0, -1...), shown with These members. A change calls
    /// <paramref name="save"/>; the returned function reads the choice.</summary>
    internal static (UIElement View, Func<ThinkingRunsOn?> Read) Create(string id, string memberId, string name, ThinkingRunsOn? start,
        bool inherit, ThinkingPoolSettings pool, Action save)
    {
        string[] modes = ["Any member", "Prefer smart", "Smart only", "These members"];
        var offset = inherit ? 1 : 0;
        const string help = "Any member: the free machine that shares least with the conversation. Prefer smart: the smartest free " +
            "machine, else a less smart one after a short wait. Smart only: only Smart machines. These members: only the machines you tick.";
        var list = new ComboBox
        {
            Width = 220, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Which machines in the Thinking pool may take this job.",
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
        // A check-in's card has no Runs on card above it, so its choice carries the "?" with what each one means.
        UIElement choice = list;
        if (inherit)
        {
            var tip = HelpTip.For("Runs on", help, "RunsOn-" + id, wiki: "Thinking#runs-on");
            tip.Margin = new Thickness(6, 0, 0, 0);
            tip.VerticalAlignment = VerticalAlignment.Center;
            choice = new StackPanel { Orientation = Orientation.Horizontal, Children = { list, tip } };
        }
        view.Children.Add(TerminalRow(inherit ? "Runs on (Thinking pool)" : "Runs on", choice));
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
}
