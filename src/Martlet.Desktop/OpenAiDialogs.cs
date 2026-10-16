using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Core.Access;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Who gets access to your language models, at which priority and until when.</summary>
internal sealed record OpenAiAccessRequest(string Name, bool EqualPriority, DateTimeOffset? ExpiresAt);

/// <summary>Asks who gets a key for the OpenAI-compatible endpoint (docs/OPENAI_ENDPOINT.md), how their requests rank against
/// your own work, and when the key expires. Nothing is made when the dialog is canceled.</summary>
internal sealed class OpenAiAccessDialog : ThemedWindow
{
    private static readonly (string Label, TimeSpan? Lifetime)[] Expiries =
        [("Never", null), ("In 30 days", TimeSpan.FromDays(30)), ("In 90 days", TimeSpan.FromDays(90)), ("In a year", TimeSpan.FromDays(365))];
    private readonly TextBox name = new() { MaxLength = ApiKeyList.MaximumNameCharacters };
    private readonly RadioButton first = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly RadioButton equal = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox expiry = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private OpenAiAccessRequest? result;

    private OpenAiAccessDialog()
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Give someone access to your language models";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "OpenAiAccessDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Give someone access", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "Martlet makes a key for one person or app. With it, any app that works with the OpenAI API can use the language " +
                "models your hosts run. The key can't reach anything else: not your settings, memories, voices, other engines, " +
                "or the cloud models and keys you set in Martlet. Revoke it at any time.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
        });
        AutomationProperties.SetAutomationId(name, "OpenAiAccessName");
        AutomationProperties.SetName(name, "Who the key is for");
        root.Children.Add(new Label { Content = "_Who is it for (a name you recognize)", Target = name, Padding = new Thickness(0, 10, 0, 4) });
        root.Children.Add(name);
        root.Children.Add(new Label { Content = "Their requests", Padding = new Thickness(0, 12, 0, 2) });
        first.GroupName = equal.GroupName = "OpenAiPriority";
        first.Content = new TextBlock
        {
            Text = "Your Martlet first (recommended): any request of yours stops theirs, and they wait while yours run.",
            TextWrapping = TextWrapping.Wrap
        };
        equal.Content = new TextBlock
        {
            Text = "Same priority as your background work: they run beside your Thinking pool's work. Your live conversations still stop them.",
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(first, "OpenAiAccessPriorityFirst");
        AutomationProperties.SetName(first, "Your Martlet first");
        AutomationProperties.SetAutomationId(equal, "OpenAiAccessPriorityEqual");
        AutomationProperties.SetName(equal, "Same priority as your background work");
        root.Children.Add(first);
        root.Children.Add(equal);
        foreach (var (label, _) in Expiries) expiry.Items.Add(label);
        expiry.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(expiry, "OpenAiAccessExpiry");
        AutomationProperties.SetName(expiry, "Expires");
        root.Children.Add(new Label { Content = "_Expires", Target = expiry, Padding = new Thickness(0, 12, 0, 4) });
        root.Children.Add(expiry);
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetAutomationId(cancel, "OpenAiAccessCancel");
        var ok = new Button { Content = "Make _key", IsDefault = true, MinWidth = 110 };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "OpenAiAccessConfirm");
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => name.Focus();
    }

    internal static OpenAiAccessRequest? Ask(Window owner)
    {
        var dialog = new OpenAiAccessDialog { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.result : null;
    }

    private void Accept()
    {
        var problem = name.Text.Trim().Length == 0 ? "Type who the key is for, for example their name."
            : name.Text.Any(char.IsControl) ? "Use a name on one line."
            : null;
        if (problem is not null)
        {
            error.Text = problem;
            error.Visibility = Visibility.Visible;
            return;
        }
        var lifetime = Expiries[Math.Max(0, expiry.SelectedIndex)].Lifetime;
        result = new(name.Text.Trim(), equal.IsChecked == true, lifetime is { } span ? DateTimeOffset.UtcNow + span : null);
        DialogResult = true;
    }
}

/// <summary>Shows what to send to the person who got a key: the base URL, the key (shown this once), the models and a first
/// request. Copy puts it all on the clipboard.</summary>
internal sealed class OpenAiInviteDialog : ThemedWindow
{
    private OpenAiInviteDialog(IssuedApiKey issued, string baseUrl, bool publicAddress, IReadOnlyList<string> models)
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Ready to send";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "OpenAiInviteDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = $"Ready to send to {issued.Key.Name}", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        AutomationProperties.SetAutomationId(heading, "OpenAiInviteTitle");
        root.Children.Add(heading);
        var note = new TextBlock
        {
            Text = "Copy this and send it in a private message. Martlet doesn't keep the key and can't show it again; if it's lost, " +
                "revoke it and make a new one." + (publicAddress ? "" :
                " You haven't set an address to give people yet, so this uses a home address: it works only inside your home and " +
                "only in apps that can skip certificate checks. Set up a proxy first to share outside your home."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
        };
        AutomationProperties.SetAutomationId(note, "OpenAiInviteNote");
        root.Children.Add(note);
        var text = OpenAiEndpoint.Invite(issued.Key.Name, baseUrl, issued.Token, models);
        var value = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(6, 4, 6, 4), MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(value, "OpenAiInviteText");
        AutomationProperties.SetName(value, "What to send, with the new key");
        CopyText.SetButton(value, false);
        root.Children.Add(value);
        var copied = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        copied.SetResourceReference(StyleProperty, "Muted");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "_Copy all", MinWidth = 100, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetAutomationId(copy, "OpenAiInviteCopy");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); copied.Text = "Copied. Paste it into a private message."; }
            catch (System.Runtime.InteropServices.COMException) { copied.Text = "The clipboard is busy; select the text and copy it."; }
        };
        var done = new Button { Content = "_Done", IsDefault = true, IsCancel = true, MinWidth = 90 };
        done.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(done, "OpenAiInviteDone");
        done.Click += (_, _) => DialogResult = true;
        root.Children.Add(copied);
        buttons.Children.Add(copy);
        buttons.Children.Add(done);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => copy.Focus();
    }

    internal static void Show(Window owner, IssuedApiKey issued, string baseUrl, bool publicAddress, IReadOnlyList<string> models) =>
        new OpenAiInviteDialog(issued, baseUrl, publicAddress, models) { Owner = owner }.ShowDialog();
}

/// <summary>How to give your hosts' language models an address on the internet: a reverse proxy with a real certificate in front
/// of one host (the front door), with the configuration to paste for Nginx and for Caddy. Shows text only; changes nothing.</summary>
internal sealed class OpenAiProxyDialog : ThemedWindow
{
    private OpenAiProxyDialog(string serverName, string hostId, string origin)
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Give your language models an address";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "OpenAiProxyDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Give your language models an address", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        var steps = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10),
            Text = "Apps that use the OpenAI API need an https address with a real certificate. Your hosts use their own certificate, so " +
                "put a reverse proxy in front of one host. Any host works: it runs the models it has and sends the other models to the " +
                "host of your network that has them.\n\n" +
                $"1. Choose a name, for example {serverName}. In your DNS, point it at your home's internet address.\n" +
                "2. Make your router send port 443 to the computer that runs the proxy (for example your Linux server). If it already does " +
                "for your other sites, nothing changes.\n" +
                "3. Get a certificate for the name (certbot, Nginx Proxy Manager or Caddy do it for you).\n" +
                $"4. Add one of the configurations below to the proxy and reload it. It sends only /v1/ to {hostId} ({origin}) on your " +
                "home network. Everything else of Martlet stays at home.\n" +
                $"5. In Martlet, type https://{serverName}/v1 in Address to give people and choose Save.\n\n" +
                "Each request still needs a key you made. The proxy limits how fast one address can ask; your hosts check every key."
        };
        AutomationProperties.SetAutomationId(steps, "OpenAiProxySteps");
        root.Children.Add(steps);
        root.Children.Add(Config("Nginx", "OpenAiProxyNginx", OpenAiEndpoint.NginxConfig(serverName, origin)));
        root.Children.Add(Config("Caddy", "OpenAiProxyCaddy", OpenAiEndpoint.CaddyConfig(serverName, origin)));
        var close = new Button { Content = "_Close", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(close, "OpenAiProxyClose");
        close.Click += (_, _) => DialogResult = true;
        root.Children.Add(close);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 820 };
    }

    private static FrameworkElement Config(string title, string id, string text)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(6, 4, 6, 4), MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, title + " configuration");
        stack.Children.Add(box);
        return stack;
    }

    internal static void Show(Window owner, string serverName, string hostId, string origin) =>
        new OpenAiProxyDialog(serverName, hostId, origin) { Owner = owner }.ShowDialog();
}
