using System.Net.Http;
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
        root.Children.Add(HelpTip.Explain(
            "Martlet makes a key for one person or app, and it reaches only the language models your hosts run. Any app that works " +
            "with the OpenAI API can use it. The key never reaches your settings, memories, voices, other engines, or the cloud " +
            "models and keys you set in Martlet; revoke it at any time.",
            new Thickness(0, 4, 0, 6), "OpenAiAccessKey", "the key", wiki: "Sharing-with-Friends#share-your-language-models"));
        AutomationProperties.SetAutomationId(name, "OpenAiAccessName");
        AutomationProperties.SetName(name, "Who the key is for");
        InputHint.SetText(name, "Alex, or Alex's coding app");
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
            Text = "Same priority as your background work: they run beside your Thinking pool's work, but your live conversations still stop them.",
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
        var note = HelpTip.Explain(publicAddress
                ? "Copy this and send it in a private message; Martlet can't show the key again. If it's lost, revoke the key and make a new one."
                : "Copy this and send it in a private message; it has a home address, so it works only inside your home. Martlet can't " +
                  "show the key again; if it's lost, revoke the key and make a new one. A home address works only in apps that can skip " +
                  "certificate checks; to share outside your home, give the front door an address first (Show setup).",
            new Thickness(0, 4, 0, 10), "OpenAiInviteKey", "the key", wiki: "Sharing-with-Friends#share-your-language-models");
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

/// <summary>How to give the front door an https address with a real certificate (docs/OPENAI_ENDPOINT.md): the steps, then the
/// text to copy for Nginx Proxy Manager, Nginx, Caddy, Cloudflare Tunnel and Tailscale Funnel, filled in with your address and
/// the front door. Shows text only; changes nothing.</summary>
internal sealed class OpenAiProxyDialog : ThemedWindow
{
    private OpenAiProxyDialog(string serverName, string? hostId, string upstreamHost, int port, bool open, string? publicAddress)
    {
        var frontDoorOrigin = open ? $"https://{upstreamHost}:{port}" : null;
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Give your front door an address";
        Width = 780;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "OpenAiProxyDialog");
        var upstream = $"https://{upstreamHost}:{port}";
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Give your front door an address", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        var steps = HelpTip.Explain(
            (open ? $"Your front door is open on {hostId} at {upstream}: give it an https address with a real certificate in one of the ways below."
                : $"First open the front door (Share › Share your language models › Front door), then give it an https address in one of the ways below. " +
                  $"These steps assume {upstream}.") +
            " Tailscale Funnel needs no domain; Cloudflare Tunnel needs a domain on Cloudflare; Nginx Proxy Manager, Nginx and Caddy need " +
            "your own domain and your router's port 443. Then type the address in Address to give people, and choose Save and Check address. " +
            "Never point a proxy at the gateway's own port (9443).",
            new Thickness(0, 4, 0, 10), "OpenAiProxyWays", "the ways to get an address", wiki: "Sharing-with-Friends#get-an-https-address");
        AutomationProperties.SetAutomationId(steps, "OpenAiProxySteps");
        root.Children.Add(steps);
        root.Children.Add(Config("Tailscale Funnel (run on the front door's computer)", "OpenAiProxyTailscale", OpenAiEndpoint.TailscaleCommand(port)));
        root.Children.Add(Config("Nginx Proxy Manager", "OpenAiProxyManager", OpenAiEndpoint.ProxyManagerSteps(serverName, upstreamHost, port)));
        root.Children.Add(ProxyManagerRow(serverName, hostId, upstreamHost, port, open, publicAddress, frontDoorOrigin));
        root.Children.Add(Config("Nginx", "OpenAiProxyNginx", OpenAiEndpoint.NginxConfig(serverName, upstream)));
        root.Children.Add(Config("Caddy", "OpenAiProxyCaddy", OpenAiEndpoint.CaddyConfig(serverName, upstream)));
        root.Children.Add(Config("Cloudflare Tunnel (config.yml)", "OpenAiProxyCloudflare", OpenAiEndpoint.CloudflareTunnelConfig(serverName, upstream)));
        var close = new Button { Content = "_Close", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(close, "OpenAiProxyClose");
        close.Click += (_, _) => DialogResult = true;
        root.Children.Add(close);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 860 };
    }

    /// <summary>Set it up in Nginx Proxy Manager: finds NPM on the front door's computer, signs in once (nothing is saved), shows
    /// what it would change or add, does it after the owner's yes, then checks the address.</summary>
    private FrameworkElement ProxyManagerRow(string serverName, string? hostId, string upstreamHost, int port, bool open,
        string? publicAddress, string? frontDoorOrigin)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var button = new Button
        {
            Content = "Set it up in _Nginx Proxy Manager", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4),
            IsEnabled = open && publicAddress is not null
        };
        AutomationProperties.SetAutomationId(button, "OpenAiProxyManagerSetup");
        AutomationProperties.SetName(button, "Sign in to Nginx Proxy Manager and point your address at the front door");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        status.SetResourceReference(StyleProperty, "Muted");
        status.Text = !open ? "Open the front door first, then Martlet can set it up in Nginx Proxy Manager for you."
            : publicAddress is null ? "Save the address to give people first, then Martlet can set it up in Nginx Proxy Manager for you."
            : "Martlet signs in to Nginx Proxy Manager once, changes the site for this name if there is one (or adds it), and checks the address.";
        AutomationProperties.SetAutomationId(status, "OpenAiProxyManagerStatus");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { status.Text = await SetUpProxyManagerAsync(serverName, hostId ?? upstreamHost, upstreamHost, port, publicAddress!, frontDoorOrigin, status); }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException ||
                error is TaskCanceledException)
            {
                status.Text = error is TaskCanceledException ? "Nginx Proxy Manager didn't answer in time." : error.Message;
            }
            finally { button.IsEnabled = true; }
            ErrorLog.Info("Share: Nginx Proxy Manager: " + status.Text);
        };
        stack.Children.Add(button);
        stack.Children.Add(status);
        return stack;
    }

    private async Task<string> SetUpProxyManagerAsync(string serverName, string frontDoor, string upstreamHost, int port, string publicAddress,
        string? frontDoorOrigin, TextBlock status)
    {
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        status.Text = $"Looking for Nginx Proxy Manager on {upstreamHost}...";
        var found = ProxyManager.Address($"{upstreamHost}:81") is { } near && await ProxyManager.AnswersAsync(http, near, CancellationToken.None)
            ? near.ToString().TrimEnd('/') : null;
        var dialog = new HostInputDialog("Nginx Proxy Manager", "Sign in to Nginx Proxy Manager",
            $"Martlet signs in once to send {serverName} to your front door ({frontDoorOrigin}) and doesn't save the email or password.", "Sign _in");
        dialog.AddText("manager", "Nginx Proxy Manager's address", found ?? "",
            found is null ? "Its admin page, usually port 81." : "Martlet found it at this address.", maxLength: 200);
        dialog.AddText("email", "Email", "", maxLength: 200);
        dialog.AddSecret("manager-password", "Password");
        if (dialog.Ask(this) is not { } answers) return "Nginx Proxy Manager wasn't changed.";
        var address = ProxyManager.Address(answers["manager"]) ?? throw new InvalidOperationException("Type Nginx Proxy Manager's address, for example http://192.168.1.20:81.");
        var manager = new ProxyManager(http, address);
        status.Text = $"Signing in to Nginx Proxy Manager at {address}...";
        await manager.SignInAsync(answers["email"], answers["manager-password"], CancellationToken.None);
        var plan = await manager.PlanAsync(serverName, upstreamHost, port, frontDoor, CancellationToken.None);
        if (plan.Changes && !ConfirmationDialog.Confirm(this, plan.Text, "Nginx Proxy Manager",
                yes: plan.Site is null ? "_Add it" : "_Change it", no: "_Cancel", questionId: "ProxyManagerQuestion"))
            return "Nginx Proxy Manager wasn't changed. " + plan.Text;
        var done = await manager.ApplyAsync(plan, upstreamHost, port, answers["email"], CancellationToken.None);
        status.Text = done + " Checking the address...";
        return done + " " + await FrontDoorCheck.RunAsync(publicAddress, frontDoorOrigin, CancellationToken.None);
    }

    private static FrameworkElement Config(string title, string id, string text)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(6, 4, 6, 4), MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, title);
        stack.Children.Add(box);
        return stack;
    }

    internal static void Show(Window owner, string serverName, string? hostId, string upstreamHost, int port, bool open, string? publicAddress) =>
        new OpenAiProxyDialog(serverName, hostId, upstreamHost, port, open, publicAddress) { Owner = owner }.ShowDialog();
}