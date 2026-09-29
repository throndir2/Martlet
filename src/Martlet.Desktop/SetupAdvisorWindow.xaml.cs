using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>Four-step advisor: goal, features and computers become a per-role plan that explains what each
/// part does, where it runs, what data leaves and what to use until planned parts arrive. Saves and contacts nothing.</summary>
public partial class SetupAdvisorWindow : ThemedWindow
{
    private const int ResultIndex = 3;
    private static readonly string[] GpuNames =
    [
        "No dedicated GPU (or not sure)", "NVIDIA, 8 GB", "NVIDIA, 12 GB", "NVIDIA, 16 GB",
        "NVIDIA, 24 GB", "NVIDIA, 32 GB or more", "AMD or Intel GPU"
    ];
    private static readonly string[] MachineCounts =
        ["None", "1 other computer", "2 other computers", "3 other computers", "4 other computers", "5 or more"];

    private int step;
    private SetupAdvice? advice;

    internal AdvisorAnswers Answers { get; private set; }
    internal AdvisorNextStep? RequestedStep { get; private set; }

    internal SetupAdvisorWindow(AdvisorAnswers? previous = null)
    {
        InitializeComponent();
        ThisPcGpuChoice.ItemsSource = GpuNames;
        ExtraGpuChoice.ItemsSource = GpuNames;
        ExtraMachinesChoice.ItemsSource = MachineCounts;
        Answers = previous ?? new AdvisorAnswers();
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
        ExtraMachinesChoice.SelectedIndex = Math.Clamp(answers.ExtraMachines, 0, 5);
        ExtraGpuChoice.SelectedIndex = (int)answers.ExtraMachineGpu;
        UpdateExtraGpu();
    }

    private AdvisorAnswers Read() => new()
    {
        Goal = SmartestGoal.IsChecked == true ? AdvisorGoal.Smartest
            : FastestGoal.IsChecked == true ? AdvisorGoal.Fastest
            : PrivateGoal.IsChecked == true ? AdvisorGoal.Private
            : AdvisorGoal.Balanced,
        VoiceInput = VoiceInputChoice.IsChecked == true,
        SpokenReplies = SpokenRepliesChoice.IsChecked == true,
        Character = CharacterChoice.IsChecked == true,
        CustomVoice = CustomVoiceChoice.IsChecked == true,
        ThisPcGpu = (AdvisorGpu)Math.Max(0, ThisPcGpuChoice.SelectedIndex),
        GamesOnThisPc = GamesChoice.IsChecked == true,
        ExtraMachines = Math.Max(0, ExtraMachinesChoice.SelectedIndex),
        ExtraMachineGpu = (AdvisorGpu)Math.Max(0, ExtraGpuChoice.SelectedIndex)
    };

    private void ShowStep(int value)
    {
        step = value;
        StackPanel[] panels = [GoalStep, FeatureStep, HardwareStep, ResultStep];
        for (var i = 0; i < panels.Length; i++) panels[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = step == ResultIndex ? "Your recommended setup" : $"Step {step + 1} of 3";
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step switch { 2 => "_See my setup", ResultIndex => "_Start over", _ => "_Next" };
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
    private void ExtraMachines_Changed(object sender, SelectionChangedEventArgs e) => UpdateExtraGpu();

    private void UpdateExtraGpu()
    {
        var any = ExtraMachinesChoice.SelectedIndex > 0;
        ExtraGpuChoice.IsEnabled = any;
        ExtraGpuLabel.IsEnabled = any;
    }

    private void BuildResult()
    {
        advice = SetupAdvisor.Recommend(Answers);
        ResultStep.Children.Clear();
        Add(new TextBlock { Text = advice.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Add(Muted(advice.Summary, new Thickness(0, 4, 0, 12)));

        Add(Heading("What runs where"));
        foreach (var role in advice.Roles)
        {
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
            card.Children.Add(new TextBlock { Text = role.Choice, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            card.Children.Add(Line("Runs on", role.Where));
            card.Children.Add(Line("What it does", role.WhatItDoes));
            card.Children.Add(Line("Why", role.Why));
            card.Children.Add(Line("Your data", role.Data));
            if (role.UntilThen is not null) card.Children.Add(Line("For now", role.UntilThen));
            var border = new Border { Child = card };
            border.SetResourceReference(StyleProperty, "CardStyle");
            AutomationProperties.SetName(border, $"{role.Role}: {role.Choice}, {role.Where}, {role.Status}");
            Add(border);
        }

        Add(Heading("Your computers"));
        foreach (var machine in advice.Machines)
        {
            Add(new TextBlock { Text = machine.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
            foreach (var run in machine.Runs) Add(Bullet(run));
        }

        Add(Heading("Good to know"));
        foreach (var note in advice.Notes) Add(Bullet(note));

        Add(Heading("Next steps"));
        Add(Muted("Open each window in order. Nothing changes until you save or confirm there.", new Thickness(0, 0, 0, 8)));
        var actions = new WrapPanel();
        foreach (var next in advice.NextSteps)
        {
            var button = new Button { Content = NextStepLabel(next), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 12, 4) };
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
        AdvisorNextStep.Setup => "Open Setup / resume (models and keys)",
        AdvisorNextStep.AudioSetup => "Open Audio setup (microphone and speakers)",
        AdvisorNextStep.Hosts => "Open Martlet hosts (GPU computers and Docker)",
        AdvisorNextStep.VoiceLibrary => "Open Voice Library (your voice samples)",
        _ => "Open Character settings"
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
        }
        text.AppendLine();
        foreach (var machine in plan.Machines) text.AppendLine($"{machine.Name}: {string.Join("; ", machine.Runs)}");
        text.AppendLine();
        foreach (var note in plan.Notes) text.AppendLine("- " + note);
        return text.ToString();
    }
}
