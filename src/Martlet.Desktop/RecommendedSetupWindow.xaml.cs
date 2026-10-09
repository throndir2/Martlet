using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Planning;

namespace Martlet.Desktop;

/// <summary>A key one change needs (a Hugging Face token, say), asked for in the review: <see cref="Key"/> ties the typed value
/// back to the executor's need.</summary>
internal sealed record RecommendedSetupSecretField(string Key, string Label, string Prompt);

/// <summary>What Reconfigure needs before it starts, in words (from the executor's preflight): each change's state, downloads
/// and steps someone has to do at a computer (<see cref="Lines"/>), the terms Reconfigure accepts and the keys it asks for.
/// <see cref="Ready"/> false: it can't start, and <see cref="Problem"/> says why. <see cref="Run"/> is the executor's own
/// preflight, handed back to it when the owner chooses Reconfigure.</summary>
internal sealed record RecommendedSetupPreflightView(IReadOnlyList<string> Lines, bool Ready, string? Problem = null, object? Run = null)
{
    public IReadOnlyList<string> Terms { get; init; } = [];
    public IReadOnlyList<RecommendedSetupSecretField> Secrets { get; init; } = [];
}

/// <summary>Home's Recommended setup review: what the recommended setup changes on each computer and why, who does each job,
/// notes, downloads and what needs someone at a computer, then Reconfigure (which applies it on every computer) or Not now
/// (this PC doesn't ask about the same setup again). It reads nothing and changes nothing itself: <c>prepare</c> checks what
/// Reconfigure needs, and <c>apply</c> starts it as a background task with its own run window (Reconfigure your computers),
/// after which the review closes; it returns why it couldn't start, if it couldn't.</summary>
public partial class RecommendedSetupWindow : ThemedWindow
{
    private readonly RecommendedSetupReview review;
    private readonly Func<CancellationToken, Task<RecommendedSetupPreflightView>>? prepare;
    private readonly Func<Window, RecommendedSetupPreflightView, IReadOnlyDictionary<string, string>, string?>? apply;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, PasswordBox> secretBoxes = new(StringComparer.Ordinal);
    private RecommendedSetupPreflightView? preflight;
    private bool applying;
    private bool rendering;

    /// <summary>The owner chose Not now: the review's fingerprint.</summary>
    internal event Action<string>? Declined;

    /// <summary>The owner ticked or cleared an optional part's Off. The review closes; Martlet saves the choice and plans again.</summary>
    internal event Action<PlanComponent, bool>? PartOff;

    /// <summary>The owner ticked or cleared Use models your apps already run. The review closes; Martlet saves the choice and
    /// plans again.</summary>
    internal event Action<bool>? ServedChanged;

    /// <summary>Reconfigure started (it carries on in Background tasks), in words.</summary>
    internal string? Outcome { get; private set; }

    internal RecommendedSetupWindow(RecommendedSetupReview review, Func<CancellationToken, Task<RecommendedSetupPreflightView>>? prepare,
        Func<Window, RecommendedSetupPreflightView, IReadOnlyDictionary<string, string>, string?>? apply)
    {
        InitializeComponent();
        this.review = review;
        this.prepare = prepare;
        this.apply = apply;
        Render();
        Loaded += (_, _) => PrepareAsync().Forget();
    }

    internal string Fingerprint => review.Fingerprint;

    private void Render()
    {
        TitleText.Text = review.Title;
        SummaryText.Text = review.Summary;
        OfflineText.Text = review.Offline ?? "";
        OfflineText.Visibility = review.Offline is null ? Visibility.Collapsed : Visibility.Visible;
        rendering = true;
        UseServedBox.IsChecked = review.UseServed;
        rendering = false;
        ServedText.Text = !review.UseServed ? "Off: Martlet plans only with its own models and your keys."
            : review.Served.Count == 0 ? "No model app on this PC serves a chat model now."
            : "Found: " + string.Join(", ", review.Served) + ".";
        RenderBanner();
        PartsSection.Visibility = review.Parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PartsPanel.Children.Clear();
        foreach (var part in review.Parts) PartsPanel.Children.Add(PartRow(part));
        ChangesSection.Visibility = review.Changes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ChangesPanel.Children.Clear();
        for (var i = 0; i < review.Changes.Count; i++) ChangesPanel.Children.Add(ChangeRow(review.Changes[i], i));

        ComputersPanel.Children.Clear();
        for (var i = 0; i < review.Computers.Count; i++) ComputersPanel.Children.Add(ComputerCard(review.Computers[i], i));

        JobsPanel.Children.Clear();
        for (var i = 0; i < review.Jobs.Count; i++) JobsPanel.Children.Add(Line("\u2022 " + review.Jobs[i], $"RecommendedSetupJob-{i}"));

        ManualSection.Visibility = review.Manual.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ManualPanel.Children.Clear();
        for (var i = 0; i < review.Manual.Count; i++) ManualPanel.Children.Add(Line("\u2022 " + review.Manual[i], $"RecommendedSetupManual-{i}"));

        DownloadsText.Text = review.Downloads ?? "";
        DownloadsText.Visibility = review.Downloads is null ? Visibility.Collapsed : Visibility.Visible;

        NotesSection.Visibility = review.Notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NotesPanel.Children.Clear();
        for (var i = 0; i < review.Notes.Count; i++) NotesPanel.Children.Add(Line("\u2022 " + review.Notes[i], $"RecommendedSetupNote-{i}", muted: true));

        if (review.AlreadyOptimal)
        {
            ApplyButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.IsCancel = true;
            CloseButton.IsDefault = true;
            StatusText.Text = "Nothing to change.";
        }
        else StatusText.Text = prepare is null ? "Reconfigure isn't available in this Martlet." : "Checking what the change needs on each computer...";
    }

    /// <summary>The problem (Martlet can't reply) or the free-key tip at the top; nothing when neither applies.</summary>
    private void RenderBanner()
    {
        Banner.Visibility = review.CannotReply || review.OffersFreeKey ? Visibility.Visible : Visibility.Collapsed;
        BannerTitle.Text = review.CannotReply ? FreeKeyPrompt.ProblemTitle : FreeKeyPrompt.Title;
        BannerText.Text = review.CannotReply ? FreeKeyPrompt.Problem(review.OffersFreeKey) : FreeKeyPrompt.Tip;
        Banner.SetResourceReference(Border.BorderBrushProperty, review.CannotReply ? "WarningBrush" : "BorderBrush");
        BannerTitle.SetResourceReference(TextBlock.ForegroundProperty, review.CannotReply ? "WarningBrush" : "TextBrush");
        FreeKeyAddButton.Content = review.OffersFreeKey ? FreeKeyPrompt.AddLabel : FreeKeyPrompt.OpenThinkingLabel;
        AutomationProperties.SetName(FreeKeyAddButton, (string)FreeKeyAddButton.Content);
        AutomationProperties.SetName(FreeKeyGetButton, FreeKeyPrompt.GetLabel + " (opens NVIDIA Build in your browser)");
        FreeKeyGetButton.Visibility = review.OffersFreeKey ? Visibility.Visible : Visibility.Collapsed;
        if (review.CannotReply) FreeKeyAddButton.SetResourceReference(StyleProperty, "PrimaryButton");
    }

    /// <summary>The owner chose Add your key (Companion › Thinking with NVIDIA Build ready for the key: Thinking itself when
    /// Martlet can't reply, else If Thinking fails) or Open Thinking (<see cref="FreeKeyUse.None"/>). The review closes: it would
    /// cover the page, and it opens again with the new setup once the key is saved.</summary>
    internal event Action<FreeKeyUse>? OpenThinking;

    private void FreeKeyAdd_Click(object sender, RoutedEventArgs e)
    {
        if (applying) return;
        OpenThinking?.Invoke(FreeKeyPrompt.Use(review.OffersFreeKey, review.CannotReply));
        Close();
    }

    private void FreeKeyGet_Click(object sender, RoutedEventArgs e) =>
        BannerText.Text = FreeKeyPrompt.OpenKeyPage() ?? FreeKeyPrompt.OpenedText;

    private async Task PrepareAsync()
    {
        if (review.AlreadyOptimal || prepare is null) return;
        try { preflight = await prepare(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (!IsLoaded || applying) return;
        PreflightPanel.Children.Clear();
        for (var i = 0; i < preflight.Lines.Count; i++)
            PreflightPanel.Children.Add(Line("\u2022 " + preflight.Lines[i], $"RecommendedSetupPreflight-{i}"));
        TermsPanel.Children.Clear();
        for (var i = 0; i < preflight.Terms.Count; i++)
            TermsPanel.Children.Add(Line("\u2022 " + preflight.Terms[i], $"RecommendedSetupTerms-{i}"));
        TermsSection.Visibility = preflight.Terms.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SecretsPanel.Children.Clear();
        secretBoxes.Clear();
        for (var i = 0; i < preflight.Secrets.Count; i++)
        {
            var secret = preflight.Secrets[i];
            var label = new Label { Content = secret.Label, Padding = new Thickness(0, 4, 0, 2) };
            AutomationProperties.SetAutomationId(label, $"RecommendedSetupSecret-{i}");
            var box = new PasswordBox { MinHeight = 30, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left, Width = 420, ToolTip = secret.Prompt };
            // Not under the RecommendedSetup prefix, so MCP never reads it as a value.
            AutomationProperties.SetAutomationId(box, $"SetupSecretInput-{i}");
            AutomationProperties.SetName(box, secret.Label);
            AutomationProperties.SetHelpText(box, secret.Prompt);
            label.Target = box;
            SecretsPanel.Children.Add(label);
            SecretsPanel.Children.Add(box);
            if (secret.Prompt.Length > 0) SecretsPanel.Children.Add(Line(secret.Prompt, $"RecommendedSetupSecretHelp-{i}", muted: true));
            secretBoxes[secret.Key] = box;
        }
        SecretsSection.Visibility = preflight.Secrets.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PreflightSection.Visibility = preflight.Lines.Count + preflight.Terms.Count + preflight.Secrets.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.IsEnabled = preflight.Ready && apply is not null;
        StatusText.Text = !preflight.Ready ? preflight.Problem ?? "Martlet can't reconfigure your computers right now."
            : preflight.Terms.Count > 0 ? "Reconfigure accepts the terms listed under Before it starts."
            : "Ready. Reconfigure changes your computers as listed.";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (applying || preflight is not { Ready: true } ready || apply is null) return;
        applying = true;
        ApplyButton.IsEnabled = false;
        var secrets = secretBoxes.Where(s => s.Value.Password.Length > 0).ToDictionary(s => s.Key, s => s.Value.Password, StringComparer.Ordinal);
        foreach (var box in secretBoxes.Values)
        {
            box.Clear();
            box.IsEnabled = false;
        }
        // The run window takes over: it shows the progress, and Background tasks keeps it after its window is hidden.
        if (apply(this, ready, secrets) is not { } problem)
        {
            Outcome = "Reconfiguring your computers in Background tasks.";
            Close();
            return;
        }
        StatusText.Text = problem;
        ApplyButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        CloseButton.IsCancel = true;
        CloseButton.IsDefault = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (applying) return;
        Declined?.Invoke(review.Fingerprint);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UseServed_Changed(object sender, RoutedEventArgs e)
    {
        var now = UseServedBox.IsChecked == true;
        if (rendering || now == review.UseServed) return;
        if (applying)
        {
            UseServedBox.IsChecked = review.UseServed;
            return;
        }
        ServedChanged?.Invoke(now);
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => lifetime.Cancel();

    private static TextBlock Line(string text, string id, bool muted = false)
    {
        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        if (muted) line.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(line, id);
        return line;
    }

    /// <summary>One part of the priority list: its number, name and need, where it runs or that it is off, why, and for an
    /// optional part its Off choice.</summary>
    private FrameworkElement PartRow(ReviewPart part)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (part.CanBeOff)
        {
            var off = new CheckBox
            {
                Content = "Off", IsChecked = part.OwnerOff, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 2, 0, 0),
                ToolTip = $"Keep {part.Name.ToLowerInvariant()} off: Martlet removes it from your computers and plans without it."
            };
            AutomationProperties.SetAutomationId(off, $"RecommendedSetupOff-{part.Key}");
            AutomationProperties.SetName(off, $"{part.Name} off");
            // Checked and Unchecked (not Click): a keyboard, a screen reader and UI Automation's Toggle change IsChecked too.
            void Changed(object sender, RoutedEventArgs e)
            {
                var now = off.IsChecked == true;
                if (now == part.OwnerOff) return;
                if (applying)
                {
                    off.IsChecked = part.OwnerOff;
                    return;
                }
                PartOff?.Invoke(part.Component, now);
                Close();
            }
            off.Checked += Changed;
            off.Unchecked += Changed;
            DockPanel.SetDock(off, Dock.Right);
            row.Children.Add(off);
        }
        var body = new StackPanel();
        var text = new TextBlock { Text = part.Text, TextWrapping = TextWrapping.Wrap, FontWeight = part.On ? FontWeights.SemiBold : FontWeights.Normal };
        if (!part.On) text.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(text, $"RecommendedSetupPart-{part.Key}");
        AutomationProperties.SetName(text, $"{part.Text} {part.Why}".Trim());
        body.Children.Add(text);
        if (part.Why.Length > 0)
        {
            var why = new TextBlock { Text = part.Why, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(16, 1, 0, 0) };
            why.SetResourceReference(StyleProperty, "Muted");
            body.Children.Add(why);
        }
        row.Children.Add(body);
        return row;
    }

    private static FrameworkElement ChangeRow(ReviewChange change, int index)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var chip = new Border { Margin = new Thickness(0, 1, 10, 0), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = change.Benefit, FontSize = 12 } };
        chip.SetResourceReference(StyleProperty, "Chip");
        DockPanel.SetDock(chip, Dock.Left);
        row.Children.Add(chip);
        var body = new StackPanel();
        var summary = new TextBlock { Text = change.Summary, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(summary, $"RecommendedSetupChange-{index}");
        AutomationProperties.SetName(summary, $"{change.Benefit}: {change.Summary} {change.Why}".Trim());
        body.Children.Add(summary);
        if (change.Why.Length > 0)
        {
            var why = new TextBlock { Text = change.Why, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            why.SetResourceReference(StyleProperty, "Muted");
            body.Children.Add(why);
        }
        if (change.NeedsSomeoneThere)
        {
            var there = new TextBlock { Text = $"Someone has to make this change at {change.Computer}.", TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0) };
            there.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            body.Children.Add(there);
        }
        row.Children.Add(body);
        return row;
    }

    private static FrameworkElement ComputerCard(ReviewComputer computer, int index)
    {
        var panel = new StackPanel();
        var name = new TextBlock { Text = computer.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(name, $"RecommendedSetupComputer-{index}");
        AutomationProperties.SetName(name, $"{computer.Name}. {computer.Kind}.");
        panel.Children.Add(name);
        var kind = Line(computer.Kind, $"RecommendedSetupComputerKind-{index}", muted: true);
        panel.Children.Add(kind);
        panel.Children.Add(Line(computer.Today, $"RecommendedSetupToday-{index}"));
        var target = Line(computer.Recommended, $"RecommendedSetupTarget-{index}");
        if (computer.Changes) target.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(target);
        if (computer.Load.Length > 0) panel.Children.Add(Line(computer.Load, $"RecommendedSetupLoad-{index}", muted: true));
        foreach (var bar in computer.Bars)
        {
            var text = new TextBlock { Text = bar.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
            text.SetResourceReference(StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(text, $"RecommendedSetupBar-{index}-{DeviceCapacity.Key(bar.Resource)}");
            AutomationProperties.SetHelpText(text, DeviceCapacity.BreakdownText(bar));
            panel.Children.Add(text);
            panel.Children.Add(MainWindow.BarVisual(bar));
        }
        var card = new Border { Child = panel, CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 8), Margin = new Thickness(0, 0, 0, 10),
            BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }
}
