using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Background thinking's Where it runs: the Thinking pool as one simple list.</summary>
internal sealed partial class ThinkingPoolPage
{
    /// <summary>Where it runs: every machine in the pool (on, then off) with its box and model (<see cref="MachineRow"/>), then
    /// every machine Martlet knows that isn't in the pool yet (<see cref="JoinRow"/>: a paired computer, a spare graphics card,
    /// Ollama on this PC), the likely slowdowns, Add a cloud provider, and pairing or checking computers.</summary>
    private Border WhereItRunsCard(ThinkingPoolPageModel model)
    {
        var stack = new List<UIElement>
        {
            Heading("Where it runs"),
            HelpTip.Explain("Tick each machine that Martlet may use for background thinking, and choose its model. Together these " +
                "machines are the Thinking pool. Untick one to stop using it; it keeps its settings. Cloud providers and their keys " +
                "are the same on all your computers; paired computers and Ollama on this PC are set on each PC.",
                new Thickness(0, 0, 0, 4), "ThinkingPool", "the Thinking pool", wiki: "Thinking#machines-in-the-pool")
        };
        for (var i = 0; i < model.Pool.All.Count; i++) stack.Add(MachineRow(model, model.Member(i)));
        var candidates = model.Candidates();
        foreach (var line in candidates) stack.Add(JoinRow(model, line));
        if (model.Pool.All.Count == 0 && candidates.Count == 0)
            stack.Add(Note("Martlet doesn't know another machine yet. Add a cloud provider below, or pair another computer on Devices.",
                new Thickness(0, 6, 0, 0)));

        var warnings = model.Warnings;
        var warning = Note(string.Join(" ", warnings), new Thickness(0, 8, 0, 0));
        warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        warning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(warning, "ThinkingPoolWarnings");
        stack.Add(warning);

        stack.Add(Fold.Create("Add a cloud provider", "ThinkingPoolAddCloud", model.Pool.All.Count == 0, [.. DeepCloudPanel(model)]));
        stack.Add(Row(model.Hosts.Count == 0
            ? PageButton("Pair another computer", () => services.RunNodeAction(NodeAction.AddComputer), id: "DeepThinkingAddComputer")
            : PageButton("Check computers", () => services.RunNodeAction(NodeAction.CheckHost), id: "DeepThinkingCheckHosts")));
        return Card([.. stack]);
    }

    /// <summary>One line of the list: its box, its model and, at the end, <paramref name="end"/> (Remove), with
    /// <paramref name="notes"/> under it.</summary>
    private static StackPanel ListRow(CheckBox box, UIElement? choice, UIElement? end, params UIElement?[] notes)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(box);
        if (choice is FrameworkElement shown)
        {
            shown.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(shown, 1);
            grid.Children.Add(shown);
        }
        if (end is FrameworkElement last)
        {
            last.Margin = new Thickness(8, 0, 0, 0);
            last.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(last, 2);
            grid.Children.Add(last);
        }
        var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(grid);
        foreach (var note in notes)
        {
            if (note is not FrameworkElement element) continue;
            element.Margin = new Thickness(24, 2, 0, 0);
            row.Children.Add(element);
        }
        return row;
    }

    /// <summary>The name of a machine in the list, next to its box.</summary>
    private static TextBlock RowName(string name) => new() { Text = name, FontSize = 15, TextWrapping = TextWrapping.Wrap };

    /// <summary>A machine Martlet knows that isn't in the pool yet: its box (<c>ThinkingPoolJoin-&lt;id&gt;</c>, unticked; ticking
    /// adds it with the chosen model), its model (<c>ThinkingPoolJoinModel-&lt;id&gt;</c>) and its line
    /// (<c>ThinkingPoolJoinDetail-&lt;id&gt;</c>: what it runs, or why it can't be ticked).</summary>
    private StackPanel JoinRow(ThinkingPoolPageModel model, ThinkingPoolPageModel.JoinLine line)
    {
        var box = new CheckBox { Content = RowName(line.Title), IsChecked = false, IsEnabled = line.Cannot is null,
            ToolTip = "Tick to use this machine for background thinking with the model chosen beside it." };
        AutomationProperties.SetAutomationId(box, "ThinkingPoolJoin-" + line.Id);
        AutomationProperties.SetName(box, $"{line.Title}: use for background thinking");
        var choice = new ComboBox
        {
            Width = 240, ItemsSource = line.Models, SelectedItem = line.Selected, IsEditable = line.Kind == ThinkingPoolPageModel.JoinKind.ThisPc,
            IsEnabled = line.Cannot is null, ToolTip = line.Kind == ThinkingPoolPageModel.JoinKind.ThisPc
                ? "A model in Ollama on this PC (not Thinking's own model). Type another Ollama model if you like."
                : "The model to run there. A new model is set up there in a run window; the computer needs a graphics card for it."
        };
        if (choice.IsEditable) choice.Text = line.Selected;
        AutomationProperties.SetAutomationId(choice, "ThinkingPoolJoinModel-" + line.Id);
        AutomationProperties.SetName(choice, $"{line.Title}: model");
        // Checked rather than Click, so UI Automation's toggle (MCP ui_toggle) adds it too.
        box.Checked += (_, _) =>
        {
            var chosen = (choice.IsEditable ? choice.Text : choice.SelectedItem as string)?.Trim() ?? "";
            if (chosen.Length == 0)
            {
                services.ShowStatus("Choose a model first.");
                box.IsChecked = false;
                return;
            }
            if (line.Kind == ThinkingPoolPageModel.JoinKind.ThisPc && !CanAddLocal(model, chosen))
            {
                box.IsChecked = false;
                return;
            }
            model.JoinAsync(line, chosen).Forget();
        };
        var detail = Note(line.Cannot ?? line.Detail, new Thickness(0));
        AutomationProperties.SetAutomationId(detail, "ThinkingPoolJoinDetail-" + line.Id);
        AutomationProperties.SetName(detail, $"{line.Title}: {detail.Text}");
        TextBlock? shared = null;
        if (line.SharedCard is { } text)
        {
            shared = HelpTip.Explain(text + " " + DeepThinkingFit.SharedCardMore, new Thickness(0), "SharedCard-" + line.Id,
                "sharing a graphics card", wiki: "Thinking#add-a-machine");
            shared.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(shared, "ThinkingPoolJoinShare-" + line.Id);
        }
        return ListRow(box, choice, null, detail, shared);
    }

    // ---------- Advanced › Each machine ----------

    /// <summary>Advanced › Each machine: the pool's summary, the conversation's own model (never in the pool) with its fallback
    /// box, each machine's settings (<see cref="MemberSettings"/>), the guidance and the live floor.</summary>
    private List<UIElement> MachineSettings(ThinkingPoolPageModel model)
    {
        var stack = new List<UIElement>
        {
            HelpTip.After(SubHeading("Each machine"), "Each machine", "Quick jobs are the judges (are you done talking, did you " +
                "interrupt) and the screen and sound summaries. Long jobs are thinking longer, research, a song's lyrics and the other " +
                "helpers. Each job goes to a free machine that takes it, the one that shares least with the conversation first.",
                "ThinkingPoolMachines", wiki: "Thinking#machines-in-the-pool")
        };
        var summary = Note(model.Summary, new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(summary, "ThinkingPoolSummary");
        stack.Add(summary);
        stack.Add(ConversationRow(model));
        for (var i = 0; i < model.Pool.All.Count; i++) stack.Add(MemberSettings(model, model.Member(i)));

        var guidance = Note(model.Guidance, new Thickness(0, 4, 0, 4));
        AutomationProperties.SetAutomationId(guidance, "ThinkingPoolGuidance");
        stack.Add(guidance);
        // The live floor: which members wait while you talk with Martlet (they share the conversation's computer).
        var floorText = model.LiveFloor;
        var floorLine = floorText.Length > 0
            ? HelpTip.Explain(floorText, new Thickness(0, 4, 0, 4), "ThinkingPoolLiveFloor", "waiting while you talk", wiki: "Thinking#machines-in-the-pool")
            : new TextBlock { Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(floorLine, "ThinkingPoolLiveFloor");
        stack.Add(floorLine);
        stack.Add(Note("Martlet sets the order by itself: quick jobs go first, and when the pool has two or more slots, long jobs " +
            "leave one slot that takes quick jobs free for them.", new Thickness(0, 4, 0, 0)));
        return stack;
    }

    /// <summary>The conversation's own Thinking model. It is never in the pool, so no pool job waits in front of a reply and its
    /// model stays ready for the conversation. Its box lets thinking longer and research run on it when no machine in the pool
    /// takes long jobs.</summary>
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
                "several requests at once."
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
}
