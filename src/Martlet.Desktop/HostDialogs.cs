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
        AutomationProperties.SetAutomationId(headingText, "HostInputHeading");
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

    // Fields shown only while a choice has a given value (a role variant's own secret, choices or GPU option), and text that
    // follows a choice.
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

    internal void AddChoice(string key, string label, IEnumerable<string> options, string selected) =>
        AddChoice(key, label, options.Select(option => (option, option)).ToArray(), selected);

    /// <summary>A choice whose options read <c>Text</c> but answer <c>Value</c> (for example a graphics card's name for its UUID).</summary>
    internal void AddChoice(string key, string label, IReadOnlyList<(string Value, string Text)> options, string selected)
    {
        fieldStarts[key] = fields.Children.Count;
        fields.Children.Add(new Label { Content = label, Padding = new Thickness(0, 6, 0, 4) });
        var texts = options.Select(o => o.Text).ToArray();
        var combo = new ComboBox { ItemsSource = texts, SelectedItem = options.FirstOrDefault(o => o.Value == selected).Text ?? selected };
        AutomationProperties.SetName(combo, label);
        AutomationProperties.SetAutomationId(combo, "HostInput-" + key);
        fields.Children.Add(combo);
        values[key] = () => combo.SelectedItem is string text && options.FirstOrDefault(o => o.Text == text) is { Value: { } value } ? value : selected;
        combo.SelectionChanged += (_, _) => Refresh();
    }

    internal void AddText(string key, string label, string text, string? hint = null, bool optional = false, int maxLength = 64)
    {
        fields.Children.Add(new Label { Content = label, Padding = new Thickness(0, 6, 0, 4) });
        var box = new TextBox { Text = text, MaxLength = maxLength };
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
        if (!optional) required.Add(key);
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
    /// what already runs on its graphics card), each with its reason. For a role the host already runs, the same dialog
    /// changes its settings: it shows and preselects what the role runs with now, and Apply sends them all, so only what
    /// the owner changed changes.</summary>
    internal static Dictionary<string, string>? ForRole(Window owner, string host, string role, HostRoleInputs inputs,
        IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null, bool local = false, bool agent = false) =>
        Cleaned(RoleDialog(host, role, inputs, recommended, local, agent).Ask(owner));

    /// <summary>What <see cref="ForRole"/> returns for the dialog's values: answers without empty fields or "automatic", with a
    /// variant's own choice (<c>choice.VAR@WHEN=VALUE</c>) answered as <c>choice.VAR</c>.</summary>
    internal static Dictionary<string, string>? Cleaned(Dictionary<string, string>? values)
    {
        if (values is null) return null;
        var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            if (value.Length > 0 && value != Automatic) cleaned[key.Split('@')[0]] = value;
        return cleaned;
    }

    internal static HostInputDialog RoleDialog(string host, string role, HostRoleInputs inputs,
        IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null, bool local = false, bool agent = false)
    {
        var change = inputs.Installed;
        // A host whose martlet-host predates reporting what a role runs with (role.choice_current) applies the choices shown.
        var keeps = inputs.Current.Count > 0 || inputs.AcceleratorCurrent is not null || inputs.Choices.Count == 0 && !inputs.GpuOrCpu;
        var message = $"{inputs.Title}\n\n" +
            (!change ? $"Needs: {inputs.Requires}."
                : keeps ? $"It already runs on {host}. Change what you like below; what you leave stays as it is."
                : $"It already runs on {host}. Apply sets it up again with the choices below; update {host} to see what it runs with now.") +
            (inputs.Terms.Length > 0 ? $"\n\n{inputs.Terms}" : "") +
            (inputs.Stops.Count > 0
                ? $"\n\nOnly one voice engine runs on a computer, so installing it stops {HostRoles.Names(inputs.Stops)} on {host} first " +
                  "and frees the graphics card's memory it used. Downloads are kept, so adding one again is quick."
                : "") +
            (local
                ? $"\n\nMartlet {(change ? "applies the change" : "installs it")} on this PC's host. Secrets stay on this PC."
                : agent
                ? $"\n\nMartlet on {host} {(change ? "applies the change" : "installs it")}. Secrets go over its paired connection, are held only in memory until Martlet there takes them, and are saved there."
                : $"\n\nMartlet {(change ? "applies the change" : "installs it")} on the host. Secrets are sent over SSH and saved there.");
        var dialog = change
            ? new HostInputDialog($"Change {role}", $"Change {role} on {host}", message, "_Apply")
            : new HostInputDialog($"Add {role}", $"Add {role} on {host}", message, "_Install");
        // A recommendation for a variant's own choice ("choice.VAR@WHEN=VALUE") wins over one for the choice in general.
        (string Value, string Why)? Pick(IEnumerable<string> options, params string[] keys) =>
            keys.Select(key => recommended?.GetValueOrDefault(key)).FirstOrDefault(pick => pick is { Value: { } value } && options.Contains(value));
        void When(string key, (string Variable, string Value)? when)
        {
            if (when is { } condition) dialog.ShowWhen(key, "choice." + condition.Variable, condition.Value);
        }
        // Choices whose value picks a variant (its own choices, GPU option, terms or secret).
        var conditions = inputs.Choices.Select(c => c.When?.Variable).Append(inputs.GpuWhen?.Variable)
            .Concat(inputs.TermsWhen.Select(t => t.Variable)).Concat(inputs.SecretWhen.Values.Select(w => w.Variable))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        void AddRoleChoice(HostRoleChoice choice)
        {
            // Installed: what it runs with now, chosen. A variant's own choice has it only when that variant runs now.
            var now = inputs.Current.GetValueOrDefault(choice.Variable) is { } current && choice.Options.Contains(current) ? current : null;
            var automatic = choice.Suggested && now is null;
            string[] options = automatic ? [Automatic, .. choice.Options] : [.. choice.Options];
            var pick = Pick(options, choice.Key, "choice." + choice.Variable);
            var label = pick is { } chosen ? $"{choice.Label} (recommended: {OptionText(chosen.Value)}, {chosen.Why})"
                : automatic ? choice.Label + " (automatic recommended by the host)" : choice.Label;
            dialog.AddChoice(choice.Key, now is null ? label : $"{label} (now: {OptionText(now)})",
                options.Select(option => (option, OptionText(option))).ToArray(), now ?? pick?.Value ?? (automatic ? Automatic : choice.Default));
            When(choice.Key, choice.When);
            // Recommendations that depend on another (non-variant) choice, "choice.VAR@OTHER=value" (Deep thinking's thinks at
            // once for each model), follow it: the text under this choice reads the one for what is chosen there now.
            if (choice.When is not null || recommended is null) return;
            foreach (var group in recommended.Where(r => r.Key.StartsWith(choice.Key + "@", StringComparison.Ordinal))
                .Select(r => (Other: r.Key[(choice.Key.Length + 1)..].Split('=', 2), r.Value))
                .Where(r => r.Other is [{ Length: > 0 } other, { Length: > 0 }] && !conditions.Contains(other) &&
                    inputs.Choices.Any(c => c.When is null && c.Variable == other))
                .GroupBy(r => r.Other[0], StringComparer.Ordinal))
                dialog.AddFollowingText($"HostInputFit-{choice.Variable}-{group.Key}", "choice." + group.Key, group.ToDictionary(
                    r => r.Other[1], r => $"With {OptionText(r.Other[1])}: {OptionText(r.Value.Value)} recommended ({r.Value.Why}).",
                    StringComparer.Ordinal));
        }
        // The choices that pick a variant (the stt or Audio2Face engine) come first, then what that variant asks.
        var first = inputs.Choices.Where(c => c.When is null && conditions.Contains(c.Variable)).ToArray();
        foreach (var choice in first) AddRoleChoice(choice);
        if (inputs.GpuOrCpu)
        {
            // Installed: what it runs on now, chosen (no Automatic, which would keep it anyway).
            var now = inputs.AcceleratorCurrent;
            string[] options = now is null ? [Automatic, "gpu", "cpu"] : ["gpu", "cpu"];
            var pick = Pick(options, "choice.accelerator");
            dialog.AddChoice("choice.accelerator",
                (now is null ? "Run on" : $"Run on (now: {now})") + (pick is { } chosen ? $" (recommended: {chosen.Value}, {chosen.Why})"
                    : now is null ? " (automatic chooses the GPU when available)" : ""),
                options, now ?? pick?.Value ?? Automatic);
            When("choice.accelerator", inputs.GpuWhen);
        }
        if (inputs.Gpus.Count > 1)
        {
            AddGpuChoice(dialog, inputs, recommended);
            When("choice.gpu", inputs.GpuWhen);
        }
        foreach (var choice in inputs.Choices.Except(first)) AddRoleChoice(choice);
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

    /// <summary>A choice option as the dialog shows it: a Parakeet model by its name ("Parakeet TDT 110M (English)"), any other as
    /// the host names it.</summary>
    internal static string OptionText(string option) => Martlet.Sherpa.ParakeetModels.Find(option)?.ToString() ?? option;

    /// <summary>On a host with several NVIDIA cards, which one the role runs on (<c>choice.gpu</c>): automatic, one card, or
    /// every card together. An installed role starts on the card it runs on now (or every card), so changing something else
    /// never moves it.</summary>
    private static void AddGpuChoice(HostInputDialog dialog, HostRoleInputs inputs, IReadOnlyDictionary<string, (string Value, string Why)>? recommended)
    {
        string Name(string id) => inputs.Gpus.FirstOrDefault(g => g.Id == id)?.Name ?? (id == "all" ? "every card" : id);
        (string Value, string Text)[] cards =
        [
            (Automatic, inputs.GpuCurrent is { } current
                ? "Automatic (keeps " + Name(current) + ")"
                : "Automatic (a card no other role uses, with the most free memory)"),
            .. inputs.Gpus.Select((g, i) => (g.Id, $"Card {i + 1}: {g.Describe()}")),
            ("all", "All cards (one model spread over every card)")
        ];
        var now = inputs.Installed && inputs.GpuCurrent is { } runs && cards.Any(c => c.Value == runs) ? runs : null;
        var pick = recommended?.GetValueOrDefault("choice.gpu") is { Value: { } value } chosen && cards.Any(c => c.Value == value)
            ? chosen : ((string Value, string Why)?)null;
        dialog.AddChoice("choice.gpu",
            (now is null ? "Graphics card" : $"Graphics card (now: {Name(now)})") + (pick is { } p
                ? $" (recommended: {Name(p.Value)}, {p.Why})"
                : now is null ? " (when it runs on the GPU)" : ""),
            cards, now ?? pick?.Value ?? Automatic);
    }

    /// <summary>For an install whose other answers Martlet already made (one-click setups): on a host with several NVIDIA
    /// cards, asks which one the role runs on and adds it to <paramref name="answers"/>; null when canceled. Hosts with one
    /// card, roles without a GPU and roles going on the processor ask nothing. A <c>choice.gpu</c> already in the answers is
    /// preselected as the recommendation.</summary>
    internal static Dictionary<string, string>? WithGpu(Window owner, string host, string role, HostRoleInputs inputs,
        IReadOnlyDictionary<string, string> answers, IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null)
    {
        var result = new Dictionary<string, string>(answers, StringComparer.Ordinal);
        if (GpuDialog(host, role, inputs, answers, recommended) is not { } dialog) return result;
        if (Cleaned(dialog.Ask(owner)) is not { } picked) return null;
        result.Remove("choice.gpu");
        foreach (var (key, value) in picked) result[key] = value;
        return result;
    }

    internal static HostInputDialog? GpuDialog(string host, string role, HostRoleInputs inputs, IReadOnlyDictionary<string, string> answers,
        IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null)
    {
        if (inputs.Gpus.Count < 2 || answers.GetValueOrDefault("choice.accelerator") == "cpu") return null;
        var suggested = answers.GetValueOrDefault("choice.gpu") is { } id
            ? new Dictionary<string, (string, string)>(StringComparer.Ordinal) { ["choice.gpu"] = (id, "it has the most graphics memory free") }
            : recommended;
        var dialog = new HostInputDialog($"Graphics card for {role}", $"Which graphics card runs {role} on {host}?",
            $"{host} has {inputs.Gpus.Count} NVIDIA graphics cards. Choose the one {role} uses, so each part of Martlet " +
            "(thinking, listening, its voice) can have a card of its own.", "_Continue");
        AddGpuChoice(dialog, inputs, suggested);
        return dialog;
    }

    internal const string Automatic = "automatic";
}
