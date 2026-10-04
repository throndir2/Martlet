using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>Four-step advisor: goal, features and computers become a per-role plan that explains what each
/// part does, where it runs, what data leaves and what to use until planned parts arrive. This PC's GPU and paired
/// hosts' reported hardware prefill the computers step. Saves and contacts nothing.</summary>
public partial class SetupAdvisorWindow : ThemedWindow
{
    private const int ResultIndex = 3;
    private static readonly string[] GpuNames = Enum.GetValues<AdvisorGpu>().Select(SetupAdvisor.GpuName).ToArray();
    private static readonly string[] MachineCounts =
        ["No other computers", "1 other computer", "2 other computers", "3 other computers", "4 other computers", "5 other computers"];
    private const string TaskManagerHint = "Find it in Task Manager > Performance > GPU, under Dedicated GPU memory.";

    /// <summary>One "other computer" row; keeps its answer while the count changes.</summary>
    private sealed class ComputerRow(AdvisorComputer computer)
    {
        public AdvisorComputer Initial { get; } = computer;
        public string Name { get; set; } = computer.Name ?? "";
        public AdvisorGpu Gpu { get; set; } = computer.Gpu;
        public TextBox? NameBox { get; set; }
        public ComboBox? GpuBox { get; set; }

        public void Capture()
        {
            if (NameBox is not null) Name = NameBox.Text.Trim();
            if (GpuBox is not null) Gpu = (AdvisorGpu)Math.Max(0, GpuBox.SelectedIndex);
        }

        // A host's reported hardware is shown only while the answer still matches what it reported.
        public AdvisorComputer Read() => new(Gpu, string.IsNullOrWhiteSpace(Name) ? null : Name,
            Gpu == Initial.Gpu ? Initial.Detected : null);
    }

    private readonly AdvisorGpu? detectedGpu;
    private readonly string? detectedText;
    private readonly List<ComputerRow> rows = [];
    private int step;
    private SetupAdvice? advice;

    internal AdvisorAnswers Answers { get; private set; }
    internal AdvisorNextStep? RequestedStep { get; private set; }

    /// <param name="detected">This PC's graphics card read from Windows, or null when it could not be read.</param>
    /// <param name="pairedHosts">Paired Martlet hosts and the hardware they reported, used for a first-time answer.</param>
    internal SetupAdvisorWindow(AdvisorAnswers? previous = null, (AdvisorGpu Gpu, string Text)? detected = null,
        IReadOnlyList<AdvisorComputer>? pairedHosts = null)
    {
        InitializeComponent();
        ThisPcGpuChoice.ItemsSource = GpuNames;
        ExtraMachinesChoice.ItemsSource = MachineCounts;
        detectedGpu = detected?.Gpu;
        detectedText = detected?.Text;
        Answers = previous ?? new AdvisorAnswers
        {
            ThisPcGpu = detectedGpu ?? AdvisorGpu.None,
            ThisPcDetected = detectedText,
            OtherComputers = (pairedHosts ?? []).Take(SetupAdvisor.MaxOtherComputers).ToArray()
        };
        if (pairedHosts is { Count: > 0 })
            ExtraMachinesHint.Text = $"Your paired Martlet host{(pairedHosts.Count == 1 ? " is" : "s are")} filled in with the hardware they reported. Add any other computers you plan to use.";
        Load(Answers);
        ShowStep(0);
    }

    private void Load(AdvisorAnswers answers)
    {
        (answers.Goal switch
        {
            AdvisorGoal.Smartest => SmartestGoal,
            AdvisorGoal.Fastest => FastestGoal,
            AdvisorGoal.Private => PrivateGoal,
            _ => BalancedGoal
        }).IsChecked = true;
        VoiceInputChoice.IsChecked = answers.VoiceInput;
        SpokenRepliesChoice.IsChecked = answers.SpokenReplies;
        CharacterChoice.IsChecked = answers.Character;
        CustomVoiceChoice.IsChecked = answers.CustomVoice;
        ThisPcGpuChoice.SelectedIndex = (int)answers.ThisPcGpu;
        GamesChoice.IsChecked = answers.GamesOnThisPc;
        rows.Clear();
        rows.AddRange(answers.OtherComputers.Take(SetupAdvisor.MaxOtherComputers).Select(c => new ComputerRow(c)));
        var count = rows.Count;
        if (ExtraMachinesChoice.SelectedIndex == count) BuildComputerRows();
        else ExtraMachinesChoice.SelectedIndex = count;
        UpdateThisPcHint();
    }

    private AdvisorAnswers Read()
    {
        var thisPc = (AdvisorGpu)Math.Max(0, ThisPcGpuChoice.SelectedIndex);
        foreach (var row in rows) row.Capture();
        return new()
        {
            Goal = SmartestGoal.IsChecked == true ? AdvisorGoal.Smartest
                : FastestGoal.IsChecked == true ? AdvisorGoal.Fastest
                : PrivateGoal.IsChecked == true ? AdvisorGoal.Private
                : AdvisorGoal.Balanced,
            VoiceInput = VoiceInputChoice.IsChecked == true,
            SpokenReplies = SpokenRepliesChoice.IsChecked == true,
            Character = CharacterChoice.IsChecked == true,
            CustomVoice = CustomVoiceChoice.IsChecked == true,
            ThisPcGpu = thisPc,
            ThisPcDetected = thisPc == detectedGpu ? detectedText : null,
            GamesOnThisPc = GamesChoice.IsChecked == true,
            OtherComputers = rows.Take(Math.Max(0, ExtraMachinesChoice.SelectedIndex)).Select(r => r.Read()).ToArray()
        };
    }

    private void ShowStep(int value)
    {
        step = value;
        StackPanel[] panels = [GoalStep, FeatureStep, HardwareStep, ResultStep];
        for (var i = 0; i < panels.Length; i++) panels[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = step == ResultIndex ? "Your recommended setup" : $"Step {step + 1} of 3";
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step switch { 2 => "_See setup", ResultIndex => "_Start over", _ => "_Next" };
        if (step == ResultIndex) BuildResult();
        Scroller.ScrollToTop();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        Answers = Read();
        ShowStep(step == ResultIndex ? 0 : step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        Answers = Read();
        if (step > 0) ShowStep(step - 1);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Answers = Read();
        Close();
    }

    private void CustomVoice_Checked(object sender, RoutedEventArgs e) => SpokenRepliesChoice.IsChecked = true;
    private void SpokenReplies_Unchecked(object sender, RoutedEventArgs e) => CustomVoiceChoice.IsChecked = false;
    private void ExtraMachines_Changed(object sender, SelectionChangedEventArgs e) => BuildComputerRows();

    private void UpdateThisPcHint()
    {
        var selected = (AdvisorGpu)Math.Max(0, ThisPcGpuChoice.SelectedIndex);
        ThisPcGpuHint.Text = detectedText is null ? TaskManagerHint
            : selected == detectedGpu ? $"Detected on this PC: {detectedText}."
            : $"Detected on this PC: {detectedText}. You chose something else; Martlet plans with your choice.";
    }

    private void ThisPcGpu_Changed(object sender, SelectionChangedEventArgs e) => UpdateThisPcHint();

    /// <summary>One row per other computer: a name (the paired host ID, or your own label) and its GPU.</summary>
    private void BuildComputerRows()
    {
        foreach (var row in rows) row.Capture();
        var count = Math.Max(0, ExtraMachinesChoice.SelectedIndex);
        while (rows.Count < count) rows.Add(new(new AdvisorComputer(AdvisorGpu.Unknown)));
        OtherComputersPanel.Children.Clear();
        for (var i = 0; i < count; i++)
        {
            var row = rows[i];
            var number = i + 2;
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            row.NameBox = new TextBox { Text = row.Name, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 48 };
            AutomationProperties.SetAutomationId(row.NameBox, $"OtherComputerName{number}");
            AutomationProperties.SetName(row.NameBox, $"Name of computer {number} (optional)");
            row.NameBox.ToolTip = $"Optional name. If left blank, this will be Computer {number}.";
            row.GpuBox = new ComboBox { ItemsSource = GpuNames, SelectedIndex = (int)row.Gpu, MinHeight = 30 };
            AutomationProperties.SetAutomationId(row.GpuBox, $"OtherComputerGpu{number}");
            AutomationProperties.SetName(row.GpuBox, $"Graphics card in computer {number}");
            Grid.SetColumn(row.GpuBox, 2);
            grid.Children.Add(row.NameBox);
            grid.Children.Add(row.GpuBox);
            var label = new TextBlock { Text = $"Computer {number}", Margin = new Thickness(0, 8, 0, 0) };
            OtherComputersPanel.Children.Add(label);
            OtherComputersPanel.Children.Add(grid);
            if (row.Initial.Detected is { } reported)
                OtherComputersPanel.Children.Add(Muted($"Reported by this host: {reported}.", new Thickness(0, 4, 0, 0)));
        }
        if (count > 0)
            OtherComputersPanel.Children.Add(Muted(
                "Not sure? Choose \"Has a GPU, not sure which\". Paired Martlet hosts can fill this in automatically.", new Thickness(0, 8, 0, 0)));
    }

    private void BuildResult()
    {
        advice = SetupAdvisor.Recommend(Answers);
        ResultStep.Children.Clear();
        Add(new TextBlock { Text = advice.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var summary = Muted(advice.Summary, new Thickness(0, 4, 0, 12));
        AutomationProperties.SetAutomationId(summary, "AdvisorSummary");
        Add(summary);

        Add(Heading("What runs where"));
        var number = 0;
        foreach (var role in advice.Roles)
        {
            number++;
            var card = new StackPanel();
            var header = new DockPanel();
            var status = new TextBlock
            {
                Text = role.Status, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            status.SetResourceReference(TextBlock.ForegroundProperty,
                role.Availability == AdvisorAvailability.Available ? "AccentBrush" : "MutedBrush");
            DockPanel.SetDock(status, Dock.Right);
            header.Children.Add(status);
            header.Children.Add(new TextBlock { Text = role.Role, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(header);
            // What the plan picks for each role and its status, for MCP (AdvisorChoice-1, AdvisorChoice-2 ...).
            var choice = new TextBlock { Text = role.Choice, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetAutomationId(choice, $"AdvisorChoice-{number}");
            AutomationProperties.SetName(choice, $"{role.Role}: {role.Choice} ({role.Status})");
            card.Children.Add(choice);
            card.Children.Add(Line("Runs on", role.Where));
            card.Children.Add(Line("What it does", role.WhatItDoes));
            card.Children.Add(Line("Why", role.Why));
            card.Children.Add(Line("Your data", role.Data));
            if (role.UntilThen is not null) card.Children.Add(Line("For now", role.UntilThen));
            if (role.HowTo is not null) card.Children.Add(Line("How to set it up", role.HowTo));
            var border = new Border { Child = card };
            border.SetResourceReference(StyleProperty, "CardStyle");
            AutomationProperties.SetName(border, $"{role.Role}: {role.Choice}, {role.Where}, {role.Status}");
            Add(border);
        }

        Add(Heading("Your computers"));
        Add(Muted("How to use each computer.", new Thickness(0, 0, 0, 4)));
        foreach (var machine in advice.Machines)
        {
            var name = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };
            name.Inlines.Add(new System.Windows.Documents.Run(machine.Name) { FontWeight = FontWeights.SemiBold });
            var hardware = new System.Windows.Documents.Run(" - " + machine.Hardware);
            hardware.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
            name.Inlines.Add(hardware);
            Add(name);
            foreach (var run in machine.Runs) Add(Bullet(run));
        }

        Add(Heading("Good to know"));
        foreach (var note in advice.Notes) Add(Bullet(note));

        Add(Heading("Next steps"));
        Add(Muted("Work through these steps in order. Nothing changes until you save or confirm in each window.", new Thickness(0, 0, 0, 8)));
        var actions = new WrapPanel();
        foreach (var next in advice.NextSteps)
        {
            var label = NextStepLabel(next);
            if (next == AdvisorNextStep.Prerequisites)
            {
                var needed = advice.ThisPcInstalls.Select(Prerequisites.For).ToArray();
                var missing = needed.Where(Prerequisites.IsMissing).ToArray();
                if (missing.Length == 0)
                {
                    Add(Muted($"Already on this PC: {string.Join(", ", needed.Select(i => i.Title))}.", new Thickness(0, 0, 0, 4)));
                    continue;
                }
                label = $"Install on this PC: {string.Join(", ", missing.Select(i => i.Title))}";
            }
            var button = new Button { Content = label, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 12, 4) };
            AutomationProperties.SetAutomationId(button, $"AdvisorOpen{next}");
            button.Click += (_, _) =>
            {
                Answers = Read();
                RequestedStep = next;
                Close();
            };
            actions.Children.Add(button);
        }
        var copy = new Button { Content = "Copy this plan", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 12, 4) };
        AutomationProperties.SetAutomationId(copy, "AdvisorCopyPlan");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(PlanText(advice));
                StepText.Text = "Plan copied to the clipboard.";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                StepText.Text = "The clipboard is busy. Try Copy this plan again.";
            }
        };
        actions.Children.Add(copy);
        Add(actions);
    }

    private void Add(UIElement element) => ResultStep.Children.Add(element);

    private static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, Margin = new Thickness(0, 16, 0, 6) };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        return heading;
    }

    private static TextBlock Muted(string text, Thickness margin)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return block;
    }

    private static TextBlock Line(string label, string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        block.Inlines.Add(new System.Windows.Documents.Run(label + ": ") { FontWeight = FontWeights.SemiBold });
        block.Inlines.Add(new System.Windows.Documents.Run(text));
        return block;
    }

    private static TextBlock Bullet(string text) =>
        new() { Text = "\u2022 " + text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 0, 2) };

    internal static string NextStepLabel(AdvisorNextStep next) => next switch
    {
        AdvisorNextStep.Setup => "Set up thinking",
        AdvisorNextStep.AudioSetup => "Choose microphone and speakers",
        AdvisorNextStep.Hosts => "Set up Martlet hosts",
        AdvisorNextStep.VoiceLibrary => "Add voices",
        AdvisorNextStep.Prerequisites => "Install required Windows features",
        _ => "Open character settings"
    };

    internal static string PlanText(SetupAdvice plan)
    {
        var text = new StringBuilder().AppendLine($"Martlet setup plan - {plan.Title}").AppendLine(plan.Summary).AppendLine();
        foreach (var role in plan.Roles)
        {
            text.AppendLine($"{role.Role}: {role.Choice}")
                .AppendLine($"  Runs on: {role.Where} ({role.Status})")
                .AppendLine($"  Your data: {role.Data}");
            if (role.UntilThen is not null) text.AppendLine($"  For now: {role.UntilThen}");
            if (role.HowTo is not null) text.AppendLine($"  How to set it up: {role.HowTo}");
        }
        text.AppendLine();
        foreach (var machine in plan.Machines) text.AppendLine($"{machine.Name} ({machine.Hardware}): {string.Join("; ", machine.Runs)}");
        text.AppendLine();
        foreach (var note in plan.Notes) text.AppendLine("- " + note);
        return text.ToString();
    }
}
