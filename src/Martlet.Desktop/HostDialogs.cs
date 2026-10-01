using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary><see cref="IHostShellPrompts"/> as desktop dialogs over a window (safe to call from the runner's thread).</summary>
internal sealed class HostShellDialogs(Window owner) : IHostShellPrompts
{
    public bool TrustHostKey(HostShellTarget target, string hostKey) => owner.Dispatcher.Invoke(() =>
        ConfirmationDialog.Confirm(owner,
            $"This is Martlet's first connection to {target.Host}. It identifies itself with this SSH host key:\n\n{hostKey}\n\n" +
            "To be sure it is really that computer, compare it with what the computer shows for: ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub\n\n" +
            "Trust this computer? Martlet remembers the key and refuses to connect if it ever changes.", "Trust this computer"));

    public string? LoginPassword(HostShellTarget target, bool retry) => owner.Dispatcher.Invoke(() =>
    {
        var dialog = new HostInputDialog("Sign in once", $"Password for {target}",
            (retry ? "That password was not accepted. " : "") +
            "Martlet signs in with it once to add its own SSH key to ~/.ssh/authorized_keys there. The password is not saved; " +
            "from now on Martlet uses its key and you do not need to log in to that computer.", "Sign in");
        dialog.AddSecret("password", "Password");
        return dialog.Ask(owner)?["password"];
    });

    public HostShellSudo? SudoPassword(HostShellTarget target, bool retry) => owner.Dispatcher.Invoke(() =>
    {
        var dialog = new HostInputDialog("sudo password", $"sudo password for {target}",
            (retry ? "sudo did not accept that password. " : "") +
            "Some steps need administrator rights there (sudo), for example using Docker or installing packages. Martlet passes " +
            "the password to sudo over this SSH connection only; it never appears in commands or logs.", "Continue");
        dialog.AddSecret("password", "sudo password (usually the same as the sign-in password)");
        dialog.AddRemember("Remember it for this computer (Windows Credential Manager)");
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
        ResizeMode = ResizeMode.NoResize;
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
            var missing = required.FirstOrDefault(key => values[key]().Length == 0);
            if (missing is null) { DialogResult = true; return; }
            error.Text = "Fill in every field.";
            error.Visibility = Visibility.Visible;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => (fields.Children.OfType<Control>().FirstOrDefault(c => c is PasswordBox or ComboBox))?.Focus();
    }

    internal bool Remembered => remember?.IsChecked == true;

    internal void AddSecret(string key, string label, string? hint = null, bool optional = false)
    {
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
        fields.Children.Add(new Label { Content = label, Padding = new Thickness(0, 6, 0, 4) });
        var combo = new ComboBox { ItemsSource = options.ToArray(), SelectedItem = selected };
        AutomationProperties.SetName(combo, label);
        AutomationProperties.SetAutomationId(combo, "HostInput-" + key);
        fields.Children.Add(combo);
        values[key] = () => combo.SelectedItem as string ?? selected;
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

    /// <summary>The entered values, or null when canceled.</summary>
    internal Dictionary<string, string>? Ask(Window owner)
    {
        Owner = owner;
        return ShowDialog() == true ? values.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal) : null;
    }

    /// <summary>Asks for a role's secrets and choices (declared by the host's role.conf) and shows its terms; the Install
    /// click is the owner's confirmation. Returns martlet-host answers (secret.name=..., choice.VAR=...), or null.
    /// <paramref name="recommended"/> preselects answers Martlet worked out for this machine (for example GPU or CPU from
    /// what already runs on its graphics card), each with its reason.</summary>
    internal static Dictionary<string, string>? ForRole(Window owner, string host, string role, HostRoleInputs inputs,
        IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null, bool local = false)
    {
        var message = $"{inputs.Title}\n\nNeeds: {inputs.Requires}." +
            (inputs.Terms.Length > 0 ? $"\n\n{inputs.Terms}" : "") +
            (local
                ? "\n\nMartlet installs it now in this PC's host service (Docker Desktop), without further questions or console windows. " +
                  "Secrets are kept in the host service's private config on this PC."
                : "\n\nMartlet installs it now without further questions (missing Docker, NVIDIA driver or NVIDIA Container Toolkit " +
                  "are installed too). Secrets go to the host over SSH and are kept there in its private config (0600).");
        var dialog = new HostInputDialog($"Add {role}", $"Add {role} on {host}", message, "_Install");
        foreach (var secret in inputs.Secrets)
            dialog.AddSecret("secret." + secret.Name, secret.Prompt,
                secret.Stored ? "Already saved on the host; leave empty to keep it." : null, optional: secret.Stored);
        (string Value, string Why)? Pick(string key, IEnumerable<string> options) =>
            recommended?.GetValueOrDefault(key) is { Value: { } value } pick && options.Contains(value) ? pick : null;
        if (inputs.GpuOrCpu)
        {
            string[] options = [Automatic, "gpu", "cpu"];
            dialog.AddChoice("choice.accelerator", Pick("choice.accelerator", options) is { } pick
                    ? $"Run it on (recommended: {pick.Value}, {pick.Why})"
                    : "Run it on (Automatic: the NVIDIA GPU when the host can use one, otherwise the CPU)",
                options, Pick("choice.accelerator", options)?.Value ?? Automatic);
        }
        foreach (var choice in inputs.Choices)
        {
            var key = "choice." + choice.Variable;
            string[] options = choice.Suggested ? [Automatic, .. choice.Options] : [.. choice.Options];
            var pick = Pick(key, options);
            dialog.AddChoice(key,
                pick is { } chosen ? $"{choice.Label} (recommended: {chosen.Value}, {chosen.Why})"
                    : choice.Suggested ? choice.Label + " (Automatic: suggested by the host's GPU memory)" : choice.Label,
                options, pick?.Value ?? (choice.Suggested ? Automatic : choice.Default));
        }
        var values = dialog.Ask(owner);
        return values?.Where(pair => pair.Value.Length > 0 && pair.Value != Automatic)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private const string Automatic = "automatic";
}
