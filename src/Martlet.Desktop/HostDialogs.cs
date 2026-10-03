using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary><see cref="IHostShellPrompts"/> as desktop dialogs over a window (safe to call from the runner's thread).</summary>
internal sealed class HostShellDialogs(Window owner) : IHostShellPrompts
{
    public bool TrustHostKey(HostShellTarget target, string hostKey) => owner.Dispatcher.Invoke(() =>
        ConfirmationDialog.Confirm(owner,
            $"Trust {target.Host}?\n\nThis is Martlet's first connection to this computer. Its SSH key fingerprint is:\n\n{hostKey}\n\n" +
            "Martlet will remember it and refuse to connect if it changes.",
            "Trust computer"));

    public string? LoginPassword(HostShellTarget target, bool retry) => owner.Dispatcher.Invoke(() =>
    {
        var dialog = new HostInputDialog("Sign in once", $"Password for {target}",
            (retry ? "That password was not accepted. " : "") +
            "Martlet signs in once to set up SSH access. The password is not saved.", "Sign in");
        dialog.AddSecret("password", "Password");
        return dialog.Ask(owner)?["password"];
    });

    public HostShellSudo? SudoPassword(HostShellTarget target, bool retry) => owner.Dispatcher.Invoke(() =>
    {
        var dialog = new HostInputDialog("Administrator password", $"Administrator password for {target}",
            (retry ? "That password was not accepted. " : "") +
            "Some setup steps need administrator rights (sudo) on that computer. The password is sent only over this SSH connection and is not shown in logs.",
            "Continue");
        dialog.AddSecret("password", "Administrator password", "Usually the same as the sign-in password.");
        dialog.AddRemember("Remember for this computer");
        return dialog.Ask(owner) is { } values ? new HostShellSudo(values["password"], dialog.Remembered) : null;
    });
}

/// <summary>A small form for secrets (masked) and choices; the primary button is the owner's confirmation.</summary>
internal sealed class HostInputDialog : ThemedWindow
{
    private readonly StackPanel fields = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly Dictionary<string, Func<string>> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> required = new(StringComparer.Ordinal);
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
    private CheckBox? remember;

    internal HostInputDialog(string title, string heading, string message, string action)
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - " + title;
        Width = 620;
        MinWidth = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "HostInputDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var headingText = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap };
        headingText.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(headingText);
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 8, 8) }
        });
        root.Children.Add(fields);
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetAutomationId(cancel, "HostInputCancel");
        var ok = new Button { Content = action, IsDefault = true, MinWidth = 90 };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "HostInputOk");
        ok.Click += (_, _) =>
        {
            var missing = required.FirstOrDefault(key => Shown(key) && values[key]().Length == 0);
            if (missing is null) { DialogResult = true; return; }
            error.Text = "Fill in all required fields.";
            error.Visibility = Visibility.Visible;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        // Copy takes the dialog's text (never what was typed into it), for example a role's terms to ask about.
        var copy = CopyText.DialogButton("HostInputCopy", () => string.Join(Environment.NewLine + Environment.NewLine,
            new[] { $"{CopyText.Product}: {title}", heading, message }
                .Concat(followers.Select(f => f.Text).Append(error).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text))));
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(new Grid { Children = { buttons, copy } });
        Content = root;
        Loaded += (_, _) => (fields.Children.OfType<Control>().FirstOrDefault(c => c is PasswordBox or ComboBox))?.Focus();
    }

    internal bool Remembered => remember?.IsChecked == true;

    // Fields shown only while a choice has a given value (a role variant's own secret), and text that follows a choice.
    private readonly Dictionary<string, (string Choice, string Value, UIElement[] Elements)> conditional = new(StringComparer.Ordinal);
    private readonly List<(string Choice, TextBlock Text, IReadOnlyDictionary<string, string> ByValue)> followers = [];

    private string? Selected(string key) => values.TryGetValue(key, out var value) ? value() : null;

    private bool Shown(string key) =>
        !conditional.TryGetValue(key, out var when) || Selected(when.Choice) == when.Value;

    private void Refresh()
    {
        foreach (var (key, when) in conditional)
            foreach (var element in when.Elements) element.Visibility = Shown(key) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (choice, text, byValue) in followers)
        {
            text.Text = Selected(choice) is { } value && byValue.TryGetValue(value, out var shown) ? shown : "";
            text.Visibility = text.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Shows the field <paramref name="key"/> (added last) only while <paramref name="choice"/> is <paramref name="value"/>;
    /// otherwise it is neither required nor returned.</summary>
    internal void ShowWhen(string key, string choice, string value)
    {
        var start = fieldStarts[key];
        conditional[key] = (choice, value, fields.Children.Cast<UIElement>().Skip(start).ToArray());
        Refresh();
    }

    /// <summary>Adds text that reads <paramref name="byValue"/>[the selected value of <paramref name="choice"/>], hidden for other values.</summary>
    internal void AddFollowingText(string id, string choice, IReadOnlyDictionary<string, string> byValue)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(text, id);
        fields.Children.Add(text);
        followers.Add((choice, text, byValue));
        Refresh();
    }

    private readonly Dictionary<string, int> fieldStarts = new(StringComparer.Ordinal);

    internal void AddSecret(string key, string label, string? hint = null, bool optional = false)
    {
        fieldStarts[key] = fields.Children.Count;
        fields.Children.Add(new Label { Content = label, Padding = new Thickness(0, 6, 0, 4) });
        var box = new PasswordBox();
        AutomationProperties.SetName(box, label);
        AutomationProperties.SetAutomationId(box, "HostInput-" + key);
        fields.Children.Add(box);
        if (hint is not null)
        {
            var text = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            text.SetResourceReference(StyleProperty, "Muted");
            fields.Children.Add(text);
        }
        values[key] = () => box.Password;
        if (!optional) required.Add(key);
    }

    internal void AddChoice(string key, string label, IEnumerable<string> options, string selected)
    {
        fieldStarts[key] = fields.Children.Count;
        fields.Children.Add(new Label { Content = label, Padding = new Thickness(0, 6, 0, 4) });
        var combo = new ComboBox { ItemsSource = options.ToArray(), SelectedItem = selected };
        AutomationProperties.SetName(combo, label);
        AutomationProperties.SetAutomationId(combo, "HostInput-" + key);
        fields.Children.Add(combo);
        values[key] = () => combo.SelectedItem as string ?? selected;
        combo.SelectionChanged += (_, _) => Refresh();
    }

    internal void AddText(string key, string label, string text, string? hint = null)
    {
        fields.Children.Add(new Label { Content = label, Padding = new Thickness(0, 6, 0, 4) });
        var box = new TextBox { Text = text, MaxLength = 64 };
        AutomationProperties.SetName(box, label);
        AutomationProperties.SetAutomationId(box, "HostInput-" + key);
        fields.Children.Add(box);
        if (hint is not null)
        {
            var note = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            note.SetResourceReference(StyleProperty, "Muted");
            fields.Children.Add(note);
        }
        values[key] = () => box.Text.Trim();
        required.Add(key);
    }

    internal void AddCheck(string key, string label, bool isChecked)
    {
        var box = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = isChecked,
            Margin = new Thickness(0, 6, 0, 4) };
        AutomationProperties.SetName(box, label);
        AutomationProperties.SetAutomationId(box, "HostInput-" + key);
        fields.Children.Add(box);
        values[key] = () => box.IsChecked == true ? "yes" : "";
    }

    internal void AddRemember(string text)
    {
        remember = new CheckBox { Content = text, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(remember, "HostInputRemember");
        fields.Children.Add(remember);
    }

    /// <summary>The entered values of the fields shown, or null when canceled.</summary>
    internal Dictionary<string, string>? Ask(Window owner)
    {
        Owner = owner;
        return ShowDialog() == true ? Answers() : null;
    }

    internal Dictionary<string, string> Answers() =>
        values.Where(pair => Shown(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal);

    /// <summary>Asks for a role's secrets and choices (declared by the host's role.conf) and shows its terms; the Install
    /// click is the owner's confirmation. Returns martlet-host answers (secret.name=..., choice.VAR=...), or null.
    /// <paramref name="recommended"/> preselects answers Martlet worked out for this machine (for example GPU or CPU from
    /// what already runs on its graphics card), each with its reason.</summary>
    internal static Dictionary<string, string>? ForRole(Window owner, string host, string role, HostRoleInputs inputs,
        IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null, bool local = false, bool agent = false) =>
        Cleaned(RoleDialog(host, role, inputs, recommended, local, agent).Ask(owner));

    /// <summary>What <see cref="ForRole"/> returns for the dialog's values: answers without empty fields or "automatic".</summary>
    internal static Dictionary<string, string>? Cleaned(Dictionary<string, string>? values) =>
        values?.Where(pair => pair.Value.Length > 0 && pair.Value != Automatic)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    internal static HostInputDialog RoleDialog(string host, string role, HostRoleInputs inputs,
        IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null, bool local = false, bool agent = false)
    {
        var message = $"{inputs.Title}\n\nNeeds: {inputs.Requires}." +
            (inputs.Terms.Length > 0 ? $"\n\n{inputs.Terms}" : "") +
            (inputs.Stops.Count > 0
                ? $"\n\nOnly one voice engine runs on a computer, so installing it stops {HostRoles.Names(inputs.Stops)} on {host} first " +
                  "and frees the graphics card's memory it used. Downloads are kept, so adding one again is quick."
                : "") +
            (local
                ? "\n\nMartlet installs it on this PC's host. Secrets stay on this PC."
                : agent
                ? $"\n\nMartlet on {host} installs it. Secrets go over its paired connection, are held only in memory until Martlet there takes them, and are saved there."
                : "\n\nMartlet installs it on the host. Secrets are sent over SSH and saved there.");
        var dialog = new HostInputDialog($"Add {role}", $"Add {role} on {host}", message, "_Install");
        (string Value, string Why)? Pick(string key, IEnumerable<string> options) =>
            recommended?.GetValueOrDefault(key) is { Value: { } value } pick && options.Contains(value) ? pick : null;
        if (inputs.GpuOrCpu)
        {
            string[] options = [Automatic, "gpu", "cpu"];
            dialog.AddChoice("choice.accelerator", Pick("choice.accelerator", options) is { } pick
                    ? $"Run on (recommended: {pick.Value}, {pick.Why})"
                    : "Run on (automatic chooses the GPU when available)",
                options, Pick("choice.accelerator", options)?.Value ?? Automatic);
        }
        foreach (var choice in inputs.Choices)
        {
            var key = "choice." + choice.Variable;
            string[] options = choice.Suggested ? [Automatic, .. choice.Options] : [.. choice.Options];
            var pick = Pick(key, options);
            dialog.AddChoice(key,
                pick is { } chosen ? $"{choice.Label} (recommended: {chosen.Value}, {chosen.Why})"
                    : choice.Suggested ? choice.Label + " (automatic recommended by the host)" : choice.Label,
                options, pick?.Value ?? (choice.Suggested ? Automatic : choice.Default));
        }
        // A variant's own terms (for example each Audio2Face engine's) follow the choice that selects it.
        foreach (var variable in inputs.TermsWhen.Select(t => t.Variable).Distinct(StringComparer.Ordinal))
            dialog.AddFollowingText("HostInputTerms-" + variable, "choice." + variable, inputs.TermsWhen
                .Where(t => t.Variable == variable).GroupBy(t => t.Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => string.Join("\n\n", g.Select(t => t.Text)), StringComparer.Ordinal));
        foreach (var secret in inputs.Secrets)
        {
            dialog.AddSecret("secret." + secret.Name, secret.Prompt,
                secret.Stored ? "Already saved. Leave empty to keep it." : null, optional: secret.Stored);
            if (inputs.SecretWhen.GetValueOrDefault(secret.Name) is { } when)
                dialog.ShowWhen("secret." + secret.Name, "choice." + when.Variable, when.Value);
        }
        return dialog;
    }

    private const string Automatic = "automatic";
}
