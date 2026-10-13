using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking pool's machine list, which is the pool.</summary>
internal sealed partial class ThinkingPoolPage
{
    /// <summary>The machine list, which is the pool: the conversation's own model (never in the pool), each machine that is on and
    /// each that is off (<see cref="MemberRow"/>), each graphics card of a computer in the pool without a Thinking pool model yet
    /// (<see cref="SpareCardRow"/>) and each paired computer that isn't a member (<see cref="ComputerRow"/>); then the pool's
    /// guidance, warnings and live floor, and Add a machine.</summary>
    private Border MachinesCard(ThinkingPoolPageModel model)
    {
        var stack = new List<UIElement>
        {
            Heading("Machines"),
            HelpTip.Explain("This list is the Thinking pool. Each machine that is on (a graphics card on one of your computers, a model " +
                "in Ollama on this PC, or a cloud provider or server) does Martlet's background work; untick On to stop using one and " +
                "keep its settings. With no machine on, the pool is off. Quick jobs are the judges (are you done talking, did you " +
                "interrupt) and the screen and sound summaries. Long jobs are thinking longer, research, a song's lyrics and the " +
                "other helpers. Each job goes to a free machine that takes it, the one that shares least with the conversation " +
                "first. Tick what each machine may do. Cloud providers and servers on your network, with their API keys and " +
                "choices, are the same on all your computers, and so are Backup for slow replies and Busy pool; paired computers join each PC's pool by themselves, and models in " +
                "Ollama on this PC stay this PC's own.", new Thickness(0, 0, 0, 8), "ThinkingPool", "the Thinking pool")
        };

        var summary = Note(model.Summary, new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(summary, "ThinkingPoolSummary");
        stack.Add(summary);
        stack.Add(ConversationRow(model));
        for (var i = 0; i < model.Pool.All.Count; i++) stack.Add(MemberRow(model, model.Member(i)));
        stack.AddRange(model.SpareCards().Select(line => SpareCardRow(model, line)));
        var others = model.Others;
        if (others.Length > 0)
        {
            var heading = new TextBlock { Text = "Paired computers not in the pool", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) };
            stack.Add(heading);
            foreach (var host in others) stack.Add(ComputerRow(model, model.Computer(host)));
        }

        var guidance = Note(model.Guidance, new Thickness(0, 4, 0, 4));
        AutomationProperties.SetAutomationId(guidance, "ThinkingPoolGuidance");
        stack.Add(guidance);
        var warnings = model.Warnings;
        var warning = Note(string.Join(" ", warnings), new Thickness(0, 4, 0, 4));
        warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        warning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(warning, "ThinkingPoolWarnings");
        stack.Add(warning);
        // The live floor: which members wait while you talk with Martlet (they share the conversation's computer).
        var floorLine = Note(model.LiveFloor, new Thickness(0, 4, 0, 4));
        floorLine.Visibility = floorLine.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(floorLine, "ThinkingPoolLiveFloor");
        stack.Add(floorLine);
        stack.Add(Note("Martlet sets the order by itself: quick jobs go first, and when the pool has two or more slots, long jobs " +
            "leave one slot that takes quick jobs free for them.", new Thickness(0, 4, 0, 0)));

        // Add a machine: each kind opens its own form, and what it adds shows in the list above, on.
        stack.Add(new TextBlock { Text = "Add a machine", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        stack.Add(Fold.Create("Another of your computers", "ThinkingPoolAddComputer", false, [.. DeepComputersPanel(model)]));
        stack.Add(Fold.Create("A model in Ollama on this PC", "ThinkingPoolAddLocal", false, [.. DeepLocalPanel(model)]));
        stack.Add(Fold.Create("A cloud provider or server", "ThinkingPoolAddCloud", false, [.. DeepCloudPanel(model)]));
        return Card([.. stack]);
    }

    /// <summary>The conversation's own Thinking model, first in the machine list. It is never in the pool, so no pool job waits in
    /// front of a reply and its model stays ready for the conversation. Its box lets thinking longer and research run on it when
    /// no machine in the pool takes long jobs.</summary>
    private static StackPanel ConversationRow(ThinkingPoolPageModel model)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        text.Children.Add(new TextBlock { Text = model.ConversationTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = model.ConversationDetail;
        var line = Note(detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(line, $"The conversation: {detail}");
        AutomationProperties.SetAutomationId(line, "ThinkingPoolConversation");
        text.Children.Add(line);
        const string label = "Do thinking longer and research here when no machine in the pool takes long jobs";
        var fallback = new CheckBox
        {
            IsChecked = model.Pool.UseConversationModelWhenEmpty, Margin = new Thickness(0, 6, 0, 0),
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            ToolTip = "Thinking longer and research then run on the conversation's own Thinking model when its provider answers " +
                "several requests at once. Quick jobs then use their own simple rules."
        };
        AutomationProperties.SetAutomationId(fallback, "ThinkingPoolUseConversationModel");
        AutomationProperties.SetName(fallback, label);
        // Checked/Unchecked rather than Click, so UI Automation's toggle (MCP ui_toggle) changes the choice too.
        void Use(bool use) => model.SavePoolAsync(p => p with { UseConversationModelWhenEmpty = use },
            use ? "The conversation model is used while no machine in the pool takes long jobs."
                : "The conversation model is no longer used for background work.").Forget();
        fallback.Checked += (_, _) => Use(true);
        fallback.Unchecked += (_, _) => Use(false);
        text.Children.Add(fallback);
        return text;
    }

    /// <summary>A graphics card of a paired computer in the list that runs no Thinking pool model yet: a row with Add a model
    /// (<c>DeepThinkingAddCard-&lt;host&gt;-gpu&lt;n&gt;</c>), which sets the model up on that card; it then joins as a machine of
    /// its own.</summary>
    private static DockPanel SpareCardRow(ThinkingPoolPageModel model, ThinkingPoolPageModel.SpareCardLine spare)
    {
        var (host, hostId, card) = (spare.Host, spare.HostId, spare.Card);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = spare.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var line = Note(spare.Detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(line, $"{spare.Title}: {spare.Detail}");
        AutomationProperties.SetAutomationId(line, $"DeepThinkingCard-{hostId}-gpu{card}");
        text.Children.Add(line);
        var add = PageButton("Add a model", () => model.AddDeepRoleAsync(host, card).Forget(), id: $"DeepThinkingAddCard-{hostId}-gpu{card}");
        AutomationProperties.SetName(add, $"Add a Thinking pool model on {hostId}'s graphics card {card}, as a machine of its own");
        add.ToolTip = "One Thinking model per graphics card: each card thinks over its own jobs at full speed.";
        add.IsEnabled = spare.Cannot is null;
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A paired computer that isn't in the pool: what it offers and why it isn't in (<see cref="ThinkingPoolPageModel.Computer"/>),
    /// with Add the Thinking pool role, Change model and In the pool.</summary>
    private DockPanel ComputerRow(ThinkingPoolPageModel model, ThinkingPoolPageModel.ComputerLine computer)
    {
        var (host, check, own) = (computer.Host, computer.Check, computer.Own);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
        var line = Note(computer.Detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(line, $"{host.HostId}: {computer.Detail}");
        AutomationProperties.SetAutomationId(line, "DeepThinkingHost-" + host.HostId);
        text.Children.Add(line);
        if (computer.SharedCard is { } shared)
        {
            var warning = Note(shared, new Thickness(0, 2, 0, 0));
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetName(warning, $"{host.HostId}: {shared}");
            AutomationProperties.SetAutomationId(warning, "DeepThinkingShare-" + host.HostId);
            text.Children.Add(warning);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (own is null && check?.Reachable == true && host.CanLaunch && services.CannotHand(host.HostId, HostRoles.DeepThinking, "deep thinking") is null)
        {
            var add = PageButton("Add the Thinking pool role", () => model.AddDeepRoleAsync(host).Forget(), primary: !computer.Usable,
                id: "DeepThinkingAddRole-" + host.HostId);
            AutomationProperties.SetName(add, $"Add the Thinking pool role on {host.HostId}");
            add.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(add);
        }
        if (own is not null && check?.Reachable == true && services.ChangesRolesOn(host))
        {
            var change = PageButton("Change model", () => services.RunHostActionAsync(host, HostAction.Change(HostRoles.DeepThinking)).Forget(),
                id: "DeepThinkingChangeModel-" + host.HostId);
            AutomationProperties.SetName(change, $"Change the Thinking pool model and slots on {host.HostId} (now {own})");
            change.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(change);
        }
        var inPool = new CheckBox
        {
            Content = "In the pool", IsChecked = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            IsEnabled = computer.Usable && check?.Reachable == true,
            ToolTip = "Computers with a Thinking model join the pool by themselves. Tick to add this one now."
        };
        AutomationProperties.SetName(inPool, $"{host.HostId} in the Thinking pool");
        AutomationProperties.SetAutomationId(inPool, "DeepThinkingPool-" + host.HostId);
        inPool.Checked += (_, _) => model.UseDeepHostAsync(host, alsoHere: true).Forget();
        buttons.Children.Add(inPool);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }
}
