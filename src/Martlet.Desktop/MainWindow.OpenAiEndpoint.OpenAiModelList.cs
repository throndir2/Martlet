using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Core.Access;
using Martlet.Core.Contracts;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.OpenAiEndpoint.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>Each model your hosts offer, with the hosts that run it, in name order.</summary>
    private IReadOnlyList<(string Model, IReadOnlyList<string> Hosts)> OpenAiModelList() => openAiModels
        .SelectMany(p => p.Value.Select(model => (Model: model, Host: p.Key)))
        .GroupBy(m => m.Model, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(m => m.Host).Order(StringComparer.Ordinal).ToArray()))
        .ToArray();

    /// <summary>Keeps what the Share page last read (models, the front door, the last Check address) in openai-models.json for MCP.</summary>
    private void SaveOpenAiSummary()
    {
        if (store is null || openAiCheckedAt is not { } at) return;
        var door = OpenFrontDoor(OpenAiEndpointSettings.Load(store.DataDirectory));
        try
        {
            OpenAiShare.SaveSummary(store.DataDirectory, openAiModels, openAiProblems, sharedHostModels, at,
                door is { } open ? new(open.Host.HostId, open.Port, open.Listening, FrontDoorOrigin(open.Host, open.Port)) : null, frontDoorCheck);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Share: couldn't keep the language model summary for MCP: " + error.Message);
        }
    }

    private void RenderOpenAi()
    {
        var settings = OpenAiEndpointSettings.Load(store?.DataDirectory);
        var hosts = FriendsHosts();
        var models = OpenAiModelList();
        var now = DateTimeOffset.UtcNow;
        var keys = apiKeys.Keys.Where(k => !k.Revoked && k.Allows(ApiKeyScopes.OpenAi))
            .OrderBy(k => k.Expired(now)).ThenBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        OpenAiGiveAccessButton.IsEnabled = store is not null && hosts.Count > 0;
        OpenAiCheckButton.IsEnabled = !openAiBusy && hosts.Count > 0;
        OpenAiStatusText.Text = store is null ? "Unavailable without a local data folder."
            : hosts.Count == 0 ? "Pair a host first (Devices › Add a computer): the endpoint runs on your hosts, with the models they run."
            : openAiBusy ? "Reading which language models your hosts offer..."
            : string.Join("; ", new[]
                {
                    models.Count == 0
                        ? openAiCheckedAt is null ? "Not checked yet" : "No host offers a language model yet (add the Thinking or Thinking pool role to a host in Devices)"
                        : $"{models.Count} language model{(models.Count == 1 ? "" : "s")} on {openAiModels.Count(p => p.Value.Count > 0)} of {hosts.Count} " +
                          $"host{(hosts.Count == 1 ? "" : "s")}",
                    keys.Length == 1 ? "1 person has a key" : $"{keys.Length} people have a key"
                }
                .Concat(openAiProblems.Select(p => $"{p.Key} {p.Value}"))
                .Concat(openAiCheckedAt is { } at ? new[] { $"checked {at:t}" } : [])) + ".";
        OpenAiPanel.Children.Clear();
        if (store is null || hosts.Count == 0) return;
        OpenAiPanel.Children.Add(FrontDoorBlock(settings));
        OpenAiPanel.Children.Add(OpenAiAddressEditor(settings));
        var frontDoor = OpenFrontDoor(settings);
        var addressStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        addressStatus.SetResourceReference(StyleProperty, "Muted");
        addressStatus.Text = settings.PublicAddress is { } address
            ? $"People put {address} in their app as the base URL (or open {address[..^OpenAiEndpoint.BasePath.Length]} in a browser to sign " +
              "in with their key), " + (frontDoor is { } door
                ? $"and your proxy sends it to the front door on {door.Host.HostId} ({FrontDoorOrigin(door.Host, door.Port)})."
                : "but first open the front door and point your proxy at it.")
            : "No address yet: to share outside your home, give the front door an https address with a real certificate (Show setup).";
        AutomationProperties.SetAutomationId(addressStatus, "OpenAiAddressStatus");
        OpenAiPanel.Children.Add(addressStatus);
        if (frontDoorCheck is { } checkedText)
        {
            var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Text = checkedText,
                FontWeight = checkedText.StartsWith("Works", StringComparison.Ordinal) ? FontWeights.SemiBold : FontWeights.Normal };
            AutomationProperties.SetAutomationId(result, "OpenAiCheckResult");
            OpenAiPanel.Children.Add(result);
        }
        var home = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        home.SetResourceReference(StyleProperty, "Muted");
        home.Text = frontDoor is { } inside
            ? $"At home, apps that can skip certificate checks may also use {FrontDoorOrigin(inside.Host, inside.Port)}{OpenAiEndpoint.BasePath}."
            : "At home, apps that can skip certificate checks may also use " +
              string.Join(", ", hosts.Select(h => $"{h.Pairing.Origin}{OpenAiEndpoint.BasePath} ({h.HostId})")) + ".";
        AutomationProperties.SetAutomationId(home, "OpenAiHomeAddresses");
        OpenAiPanel.Children.Add(home);

        var modelLine = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeights.SemiBold };
        modelLine.Text = models.Count == 0 ? "Models: none yet."
            : "Models: " + string.Join(", ", models.Select(m => $"{m.Model} ({string.Join(", ", m.Hosts)})")) + ".";
        AutomationProperties.SetAutomationId(modelLine, "OpenAiModels");
        OpenAiPanel.Children.Add(modelLine);

        var priority = HelpTip.Explain(
            "Each key has a priority, which you can change on its row. Your Martlet first: any request of yours stops theirs, and they " +
            "wait while yours run. Same priority as your background work: they run beside your Thinking pool's work, and only your live " +
            "conversations stop them. Their requests use your own context size, so a host never loads its model again for them.",
            new Thickness(0, 4, 0, 0), "OpenAiPriority", "the priority of a key", wiki: "Sharing-with-Friends#share-your-language-models");
        priority.FontSize = 12;
        OpenAiPanel.Children.Add(priority);

        var who = new TextBlock { Text = keys.Length == 0 ? "Nobody has a key yet: choose Give someone access to make one." : "Who has access",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };
        OpenAiPanel.Children.Add(who);
        foreach (var key in keys) OpenAiPanel.Children.Add(OpenAiKeyRow(key, now));
    }

    private FrameworkElement OpenAiAddressEditor(OpenAiEndpointSettings settings)
    {
        if (openAiAddressBox is null)
        {
            openAiAddressBox = new TextBox { MinWidth = 280, MaxLength = OpenAiEndpointSettings.MaximumAddressLength, VerticalAlignment = VerticalAlignment.Center };
            InputHint.SetText(openAiAddressBox, "https://martlet.example.com/v1");
            AutomationProperties.SetAutomationId(openAiAddressBox, "OpenAiPublicAddress");
            AutomationProperties.SetName(openAiAddressBox, "Address to give people, for example https://martlet.example.com/v1");
            openAiAddressProblem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            openAiAddressProblem.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(openAiAddressProblem, "OpenAiAddressProblem");
        }
        if (!openAiAddressBox.IsKeyboardFocusWithin) openAiAddressBox.Text = settings.PublicAddress ?? "";
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new Label { Content = "_Address to give people (https, with a real certificate)", Target = openAiAddressBox, Padding = new Thickness(0, 0, 0, 4) });
        var help = HelpTip.For("Giving your front door an address", OpenAiEndpoint.ExposeHelp, "OpenAiExpose", wiki: "Sharing-with-Friends#get-an-https-address");
        help.Margin = new Thickness(8, 0, 0, 4);
        heading.Children.Add(help);
        stack.Children.Add(heading);
        var row = new WrapPanel();
        (openAiAddressBox.Parent as Panel)?.Children.Remove(openAiAddressBox);
        row.Children.Add(openAiAddressBox);
        void Add(string label, string id, string name, Action run, bool enabled = true)
        {
            var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = enabled };
            AutomationProperties.SetAutomationId(button, id);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => run();
            row.Children.Add(button);
        }
        Add("Save", "OpenAiSaveAddress", "Save the address to give people", SaveOpenAiAddress);
        Add(frontDoorChecking ? "Checking..." : "Check address", "OpenAiCheckAddress",
            "Check that the address reaches your front door (sends no key)", () => CheckFrontDoorAddressAsync().Forget(),
            enabled: settings.PublicAddress is not null && !frontDoorChecking);
        Add("Show setup", "OpenAiShowProxy", "Show how to give the front door an https address: Nginx Proxy Manager, Nginx, Caddy, Cloudflare Tunnel or Tailscale",
            ShowOpenAiProxy);
        stack.Children.Add(row);
        (openAiAddressProblem!.Parent as Panel)?.Children.Remove(openAiAddressProblem);
        stack.Children.Add(openAiAddressProblem);
        return stack;
    }

    private void SaveOpenAiAddress()
    {
        if (store is null || openAiAddressBox is null) return;
        var typed = openAiAddressBox.Text.Trim();
        string? address = null;
        if (typed.Length > 0 && (address = OpenAiEndpointSettings.NormalizeAddress(typed, out var problem)) is null)
        {
            openAiAddressProblem!.Text = problem;
            openAiAddressProblem.Visibility = Visibility.Visible;
            return;
        }
        openAiAddressProblem!.Visibility = Visibility.Collapsed;
        var settings = OpenAiEndpointSettings.Load(store.DataDirectory) with { PublicAddress = address };
        if (!settings.Save(store.DataDirectory))
        {
            ActionText.Text = "Couldn't save the address: check that Martlet can write to its data folder.";
            return;
        }
        ErrorLog.Info($"Share: the address to give people is {address ?? "not set"}.");
        ActionText.Text = address is null ? "Removed the address to give people."
            : $"Saved: people use {address}, and your other computers get it too; choose Check address to test it.";
        frontDoorCheck = null;
        QueueSettingsSync();
        RenderOpenAi();
    }

    private void ShowOpenAiProxy()
    {
        var settings = OpenAiEndpointSettings.Load(store?.DataDirectory);
        var door = OpenFrontDoor(settings);
        var name = settings.PublicAddress is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : "martlet.example.com";
        var port = door?.Port ?? OpenAiEndpoint.DefaultFrontDoorPort;
        var chosen = (frontDoorHostChoice?.SelectedItem as ComboBoxItem)?.Tag as string ?? settings.FrontDoorHostId;
        var host = door?.Host ?? FriendsHosts().FirstOrDefault(h => h.HostId == chosen);
        OpenAiProxyDialog.Show(this, name, host?.HostId, host is null ? "<front door address>" : new Uri(host.Pairing.Origin).Host, port, door is not null,
            settings.PublicAddress);
    }
    private FrameworkElement OpenAiKeyRow(ApiKey key, DateTimeOffset now)
    {
        var row = NetworkRowFrame();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var priority = new ComboBox { MinWidth = 210, VerticalAlignment = VerticalAlignment.Center };
        priority.Items.Add(new ComboBoxItem { Content = "Your Martlet first", Tag = false });
        priority.Items.Add(new ComboBoxItem { Content = "Same as your background work", Tag = true });
        priority.SelectedIndex = key.Allows(ApiKeyScopes.EqualPriority) ? 1 : 0;
        AutomationProperties.SetAutomationId(priority, "OpenAiKeyPriority-" + key.Id);
        AutomationProperties.SetName(priority, $"Priority of {key.Name}'s requests");
        priority.SelectionChanged += (_, _) =>
        {
            if (priority.SelectedItem is ComboBoxItem { Tag: bool equal } && equal != key.Allows(ApiKeyScopes.EqualPriority))
                ChangeOpenAiPriority(key, equal);
        };
        buttons.Children.Add(priority);
        var revoke = new Button { Content = "Revoke", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(revoke, "OpenAiKeyRevoke-" + key.Id);
        AutomationProperties.SetName(revoke, $"Revoke {key.Name}'s key");
        revoke.Click += (_, _) => RevokeApiKey(key);
        buttons.Children.Add(revoke);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var used = apiKeysUsed.TryGetValue(key.Id, out var seen) ? $"Last used {seen.At.ToLocalTime():g} on {seen.HostId}." : "Not used since your hosts started.";
        var expires = key.ExpiresAt is not { } at ? "Never expires." : key.Expired(now) ? $"Expired {at.ToLocalTime():d}." : $"Expires {at.ToLocalTime():d}.";
        row.Children.Add(NetworkRowText("OpenAiKey-" + key.Id, key.Name,
            $"{OpenAiEndpoint.Priority(key)}. Made on {key.CreatedBy} {key.CreatedAt.ToLocalTime():d}. {used} {expires} ID {key.Id[..6]}."));
        return NetworkRowCard(row, warning: key.Expired(now));
    }

    private void ChangeOpenAiPriority(ApiKey key, bool equal)
    {
        if (store is null) return;
        try
        {
            var list = LoadApiKeys(store.DataDirectory);
            var current = list.Find(key.Id) ?? throw new ContractException(ErrorCode.InvalidContract, "No such API key.");
            var next = list.ChangeScopes(key.Id, OpenAiEndpoint.WithPriority(current, equal), NetworkIdentity.DeviceId(homeHosts), DateTimeOffset.UtcNow);
            SaveApiKeys(store.DataDirectory, next);
            apiKeys = next;
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
        {
            ActionText.Text = "Couldn't change the key's priority: " + error.Message;
            return;
        }
        ErrorLog.Info($"API key \"{key.Name}\": {(equal ? "same priority as your background work" : "your Martlet first")}.");
        ActionText.Text = $"{key.Name}'s requests now run {(equal ? "with the same priority as your background work" : "only when your Martlet doesn't need the model")}; " +
            "your hosts follow within a minute.";
        RenderApiKeys();
        RenderOpenAi();
        QueueApiKeySync();
    }

    private void OpenAiGiveAccess_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        if (OpenAiAccessDialog.Ask(this) is not { } request) return;
        IssuedApiKey issued;
        try
        {
            var (next, key) = LoadApiKeys(store.DataDirectory).Create(request.Name, OpenAiEndpoint.Scopes(request.EqualPriority), request.ExpiresAt,
                NetworkIdentity.DeviceId(homeHosts), DateTimeOffset.UtcNow);
            SaveApiKeys(store.DataDirectory, next);
            apiKeys = next;
            issued = key;
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
        {
            ActionText.Text = "Couldn't make the key: " + error.Message;
            return;
        }
        ErrorLog.Info($"API key \"{issued.Key.Name}\" made for the OpenAI-compatible endpoint ({string.Join(", ", issued.Key.Scopes)}).");
        ActionText.Text = $"Made a key for {issued.Key.Name}; it reaches your hosts within a minute.";
        RenderApiKeys();
        RenderOpenAi();
        QueueApiKeySync();
        var settings = OpenAiEndpointSettings.Load(store.DataDirectory);
        var baseUrl = settings.PublicAddress ?? (OpenFrontDoor(settings) is { } door ? FrontDoorOrigin(door.Host, door.Port) + OpenAiEndpoint.BasePath
            : FriendsHosts().FirstOrDefault()?.Pairing.Origin is { } origin ? origin + OpenAiEndpoint.BasePath : "https://<your address>/v1");
        OpenAiInviteDialog.Show(this, issued, baseUrl, settings.PublicAddress is not null, OpenAiModelList().Select(m => m.Model).ToArray());
    }

    /// <summary>The language models a host a friend shares with this PC offers, for its row; null before they were read.</summary>
    private string? SharedHostModelsText(string hostId) => sharedHostModels.TryGetValue(hostId, out var models)
        ? models.Count == 0 ? "No language models." : $"Language models: {string.Join(", ", models)}."
        : null;
}
