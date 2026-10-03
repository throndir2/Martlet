using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Core.Access;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>What the owner chose for a new API key.</summary>
internal sealed record ApiKeyRequest(string Name, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt);

/// <summary>Asks for a new API key's name, what it may do and when it expires (docs/API.md). Making the key is the
/// owner's action on the primary button; nothing is created when the dialog is canceled.</summary>
internal sealed class ApiKeyCreateDialog : ThemedWindow
{
    private static readonly (string Label, TimeSpan? Lifetime)[] Expiries =
        [("Never", null), ("In 30 days", TimeSpan.FromDays(30)), ("In 90 days", TimeSpan.FromDays(90)), ("In a year", TimeSpan.FromDays(365))];
    private readonly TextBox name = new() { MaxLength = ApiKeyList.MaximumNameCharacters };
    private readonly Dictionary<string, CheckBox> scopes = new(StringComparer.Ordinal);
    private readonly ComboBox expiry = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private ApiKeyRequest? result;

    private ApiKeyCreateDialog()
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Create an API key";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "ApiKeyCreateDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Create an API key", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "Give an app or script its own key, so you can see what uses your hosts and revoke it on its own. The key works on " +
                "every host in your Martlet network. Choose only what the app needs.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
        });
        AutomationProperties.SetAutomationId(name, "ApiKeyName");
        AutomationProperties.SetName(name, "Key name");
        root.Children.Add(new Label { Content = "_Name (for example the app that uses it)", Target = name, Padding = new Thickness(0, 10, 0, 4) });
        root.Children.Add(name);
        root.Children.Add(new Label { Content = "It may", Padding = new Thickness(0, 12, 0, 2) });
        foreach (var scope in ApiKeyScopes.All)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = ApiKeyScopes.Title(scope) + ScopeHint(scope), TextWrapping = TextWrapping.Wrap },
                IsChecked = scope == ApiKeyScopes.Read, Margin = new Thickness(0, 4, 0, 0)
            };
            AutomationProperties.SetAutomationId(box, "ApiKeyScope-" + scope);
            AutomationProperties.SetName(box, ApiKeyScopes.Title(scope));
            scopes[scope] = box;
            root.Children.Add(box);
        }
        foreach (var (label, _) in Expiries) expiry.Items.Add(label);
        expiry.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(expiry, "ApiKeyExpiry");
        AutomationProperties.SetName(expiry, "Expires");
        root.Children.Add(new Label { Content = "_Expires", Target = expiry, Padding = new Thickness(0, 12, 0, 4) });
        root.Children.Add(expiry);
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetAutomationId(cancel, "ApiKeyCreateCancel");
        var ok = new Button { Content = "Create _key", IsDefault = true, MinWidth = 110 };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "ApiKeyCreateConfirm");
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => name.Focus();
    }

    private static string ScopeHint(string scope) => scope switch
    {
        ApiKeyScopes.Read => " (version, roles, hardware, who does what, logs)",
        ApiKeyScopes.Voice => " (the hosts' own models; what it sends goes to them)",
        ApiKeyScopes.Perception => " (on hosts that run it)",
        ApiKeyScopes.Manage => " (update Martlet, add or remove roles)",
        _ => ""
    };

    internal static ApiKeyRequest? Ask(Window owner)
    {
        var dialog = new ApiKeyCreateDialog { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.result : null;
    }

    private void Accept()
    {
        var chosen = scopes.Where(s => s.Value.IsChecked == true).Select(s => s.Key).ToArray();
        var problem = name.Text.Trim().Length == 0 ? "Name the key, for example after the app that will use it."
            : name.Text.Any(char.IsControl) ? "Use a name on one line."
            : chosen.Length == 0 ? "Choose at least one thing the key may do."
            : null;
        if (problem is not null)
        {
            error.Text = problem;
            error.Visibility = Visibility.Visible;
            return;
        }
        var lifetime = Expiries[Math.Max(0, expiry.SelectedIndex)].Lifetime;
        result = new(name.Text.Trim(), chosen, lifetime is { } span ? DateTimeOffset.UtcNow + span : null);
        DialogResult = true;
    }
}

/// <summary>Shows a key just made: the only time its secret is visible. Copy puts it on the clipboard; the addresses, host
/// key pins and the example tell the app's owner how to call the hosts (docs/API.md).</summary>
internal sealed class ApiKeyCreatedDialog : ThemedWindow
{
    private ApiKeyCreatedDialog(IssuedApiKey issued, IReadOnlyList<PairedHost> hosts)
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - API key created";
        Width = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "ApiKeyCreatedDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = $"\"{issued.Key.Name}\" is ready", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        AutomationProperties.SetAutomationId(heading, "ApiKeyCreatedTitle");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "Copy the key now and keep it in the app's secret settings. Martlet doesn't keep it and can't show it again; " +
                "if it's lost, revoke it and make a new one.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
        });
        var value = new TextBox
        {
            Text = issued.Token, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(6, 4, 6, 4)
        };
        AutomationProperties.SetAutomationId(value, "ApiKeyValue");
        AutomationProperties.SetName(value, "The new API key");
        // Its own Copy button sits beside it and says where the key goes.
        CopyText.SetButton(value, false);
        var copy = new Button { Content = "_Copy", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(copy, "ApiKeyCopy");
        var copied = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        copied.SetResourceReference(StyleProperty, "Muted");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(issued.Token); copied.Text = "Copied. Paste it where the app keeps secrets."; }
            catch (System.Runtime.InteropServices.COMException) { copied.Text = "The clipboard is busy; select the key and copy it."; }
        };
        var row = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(value);
        root.Children.Add(row);
        root.Children.Add(copied);

        var reach = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        reach.Text = hosts.Count == 0
            ? "No host is paired yet. The key reaches your hosts as soon as you pair one."
            : "Your hosts (the key reaches each within a minute; pin the host key, it never changes):\n" +
              string.Join("\n", hosts.Select(h => $"• {h.HostId}: {h.Pairing.Origin}  (curl --pinnedpubkey {ApiKeyText.CurlPin(h.Pairing.SpkiFingerprint)})"));
        AutomationProperties.SetAutomationId(reach, "ApiKeyHosts");
        root.Children.Add(reach);
        var example = new TextBox
        {
            Text = ApiKeyText.Example(hosts.FirstOrDefault()), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetAutomationId(example, "ApiKeyExample");
        AutomationProperties.SetName(example, "Example request");
        root.Children.Add(example);
        var docs = new TextBlock
        {
            Text = "Every endpoint, scope and error is listed in Martlet's API guide (docs/API.md).",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        };
        docs.SetResourceReference(StyleProperty, "Muted");
        root.Children.Add(docs);
        var done = new Button { Content = "_Done", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        done.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(done, "ApiKeyCreatedDone");
        done.Click += (_, _) => DialogResult = true;
        root.Children.Add(done);
        Content = root;
        Loaded += (_, _) => { value.Focus(); value.SelectAll(); };
    }

    internal static void Show(Window owner, IssuedApiKey issued, IReadOnlyList<PairedHost> hosts) =>
        new ApiKeyCreatedDialog(issued, hosts) { Owner = owner }.ShowDialog();
}

/// <summary>How integrators reach a host with a key, in text the dialogs and docs share.</summary>
internal static class ApiKeyText
{
    /// <summary>curl's --pinnedpubkey form of a host's SPKI pin: sha256// and the base64 digest.</summary>
    internal static string CurlPin(string spkiFingerprint) =>
        "sha256//" + Convert.ToBase64String(Convert.FromHexString(spkiFingerprint["sha256:".Length..]));

    internal static string Example(PairedHost? host)
    {
        var origin = host?.Pairing.Origin ?? "https://<host address>:9443";
        var pin = host is null ? "sha256//<host key pin>" : CurlPin(host.Pairing.SpkiFingerprint);
        return $"curl -k --pinnedpubkey \"{pin}\" -H \"Authorization: Bearer $MARTLET_API_KEY\" {origin}/martlet/v1/version";
    }
}
