using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>
/// The Share page (docs/OPENAI_ENDPOINT.md): your Martlet network, friends, your language models for other people's apps (the
/// OpenAI-compatible endpoint every host serves), API keys and the hosts friends share with this PC. This file draws the page and
/// the endpoint's card: the address people use (a reverse proxy with a real certificate in front of one host, the front door),
/// the models your hosts run (read with this PC's signed pairings when the page shows, at most every two minutes, or on Check
/// now; never on a reply's path), and the keys made for people, with their priority. Keys are ordinary API keys with the openai
/// scope, so they reach every host like any other key. Cloud providers and keys set in Martlet are never reachable through it.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan OpenAiStale = TimeSpan.FromMinutes(2);
    private Dictionary<string, IReadOnlyList<string>> openAiModels = new(StringComparer.Ordinal);
    private Dictionary<string, string> openAiProblems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> sharedHostModels = new(StringComparer.Ordinal);
    private DateTimeOffset? openAiCheckedAt;
    private bool openAiBusy;
    private string? openAiLogged;
    private TextBox? openAiAddressBox;
    private ComboBox? openAiFrontDoorChoice;
    private TextBlock? openAiAddressProblem;

    /// <summary>Draws the Share page's cards and reads again what is stale while it shows.</summary>
    private void RenderShare()
    {
        RenderNetwork();
        RenderFriends();
        RenderSharedHosts();
        RefreshFriendsWhenShown();
        RenderApiKeys();
        RenderOpenAi();
        RefreshOpenAiWhenShown();
        RenderShareSummary();
    }

    /// <summary>Draws the Devices map or the Share page, whichever shows, after something they show changed.</summary>
    private void RenderDevicePages()
    {
        if (DevicesPage.IsVisible) RenderMap();
        else if (SharePage.IsVisible) RenderShare();
    }

    private void RenderShareSummary()
    {
        var members = networkState.Roster is { } roster ? roster.ActiveDesktops.Count() + roster.ActiveHosts.Count() : homeHosts.Count + 1;
        var people = apiKeys.Keys.Count(k => !k.Revoked && k.Allows(ApiKeyScopes.OpenAi));
        var friends = FriendsOverview.Build(friendsRead, shareLinks.Made).Count(f => f.SharedOn.Any());
        ShareSummary.Text = $"{Count(members, "computer")} in your Martlet network · {Count(friends, "friend")} you share hosts with · " +
            $"{Count(people, "person", "people")} with a key to your language models · {Count(sharedHosts.Count, "host")} shared with this PC.";

        static string Count(int count, string what, string? many = null) => count == 1 ? $"1 {what}" : $"{count} {many ?? what + "s"}";
    }

    private void RefreshOpenAiWhenShown()
    {
        if (!SharePage.IsVisible || closing || store is null) return;
        if ((FriendsHosts().Count > 0 || sharedHosts.Count > 0) &&
            (openAiCheckedAt is not { } read || DateTimeOffset.Now - read > OpenAiStale))
            ReadOpenAiModelsAsync().Forget();
    }

    private void OpenAiCheck_Click(object sender, RoutedEventArgs e) => ReadOpenAiModelsAsync().Forget();

    /// <summary>Reads the language models each of your hosts, and each host a friend shares with this PC, offers (GET /v1/models,
    /// signed with this PC's pairing). Keeps a summary for MCP in openai-models.json.</summary>
    private async Task ReadOpenAiModelsAsync()
    {
        if (openAiBusy || closing || store is null) return;
        openAiBusy = true;
        OpenAiStatusText.Text = "Reading which language models your hosts offer...";
        try
        {
            var own = FriendsHosts();
            var shared = sharedHosts.ToArray();
            var results = await Task.WhenAll(own.Concat(shared).Select(async host =>
            {
                try
                {
                    using var connection = ClusterSync.Connect(host.Pairing);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    return (host.HostId, Models: await connection.ReadOpenAiModelsAsync(timeout.Token), Problem: (string?)null);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    return (host.HostId, Models: (IReadOnlyList<string>?)null, Problem: "didn't answer in time");
                }
                catch (Exception error) when (ClusterSync.IsHostFailure(error))
                {
                    return (host.HostId, Models: null, Problem: error is Audio2FaceHostException { Code: "request.invalid" or "not_found" or "route.not_found" }
                        ? "runs an older Martlet without the OpenAI-compatible endpoint; update it" : error.Message);
                }
            }));
            if (closing) return;
            var ownIds = own.Select(h => h.HostId).ToHashSet(StringComparer.Ordinal);
            openAiModels = results.Where(r => r.Models is not null && ownIds.Contains(r.HostId))
                .ToDictionary(r => r.HostId, r => r.Models!, StringComparer.Ordinal);
            openAiProblems = results.Where(r => r.Problem is not null && ownIds.Contains(r.HostId))
                .ToDictionary(r => r.HostId, r => r.Problem!, StringComparer.Ordinal);
            sharedHostModels.Clear();
            foreach (var result in results.Where(r => r.Models is not null && !ownIds.Contains(r.HostId)))
                sharedHostModels[result.HostId] = result.Models!;
            openAiCheckedAt = DateTimeOffset.Now;
            try { OpenAiShare.SaveSummary(store.DataDirectory, openAiModels, openAiProblems, sharedHostModels, openAiCheckedAt.Value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn("Share: couldn't keep the language model summary for MCP: " + error.Message);
            }
            var picture = string.Join("; ", openAiModels.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}: {(p.Value.Count == 0 ? "none" : string.Join(", ", p.Value))}"));
            if (picture != openAiLogged)
            {
                openAiLogged = picture;
                ErrorLog.Info("Share: your hosts' language models for the OpenAI-compatible endpoint: " + (picture.Length == 0 ? "none." : picture + "."));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            openAiBusy = false;
            if (!closing)
            {
                RenderOpenAi();
                RenderSharedHosts();
            }
        }
    }

    /// <summary>Each model your hosts offer, with the hosts that run it, in name order.</summary>
    private IReadOnlyList<(string Model, IReadOnlyList<string> Hosts)> OpenAiModelList() => openAiModels
        .SelectMany(p => p.Value.Select(model => (Model: model, Host: p.Key)))
        .GroupBy(m => m.Model, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(m => m.Host).Order(StringComparer.Ordinal).ToArray()))
        .ToArray();

    /// <summary>The host your proxy sends requests to: the one chosen, else the first of your network's hosts.</summary>
    private PairedHost? OpenAiFrontDoor(OpenAiEndpointSettings settings)
    {
        var hosts = FriendsHosts();
        return hosts.FirstOrDefault(h => h.HostId == settings.FrontDoorHostId) ?? hosts.OrderBy(h => h.HostId, StringComparer.Ordinal).FirstOrDefault();
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
            : (models.Count == 0
                ? openAiCheckedAt is null ? "Not checked yet." : "No host offers a language model yet. Add the Thinking or Thinking pool role to a host (Devices)."
                : $"{models.Count} language model{(models.Count == 1 ? "" : "s")} on {openAiModels.Count(p => p.Value.Count > 0)} of {hosts.Count} " +
                  $"host{(hosts.Count == 1 ? "" : "s")}.") +
              $" {(keys.Length == 1 ? "1 person has" : $"{keys.Length} people have")} a key." +
              (openAiProblems.Count > 0 ? " " + string.Join(" ", openAiProblems.Select(p => $"{p.Key} {p.Value}.")) : "") +
              (openAiCheckedAt is { } at ? $" Checked {at:t}." : "");
        OpenAiPanel.Children.Clear();
        if (store is null || hosts.Count == 0) return;
        OpenAiPanel.Children.Add(OpenAiAddressEditor(settings, hosts));
        var frontDoor = OpenAiFrontDoor(settings);
        var addressStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        addressStatus.SetResourceReference(StyleProperty, "Muted");
        addressStatus.Text = settings.PublicAddress is { } address
            ? $"People put {address} in their app as the base URL. Your proxy sends it to {frontDoor?.HostId ?? "your first host"}" +
              (frontDoor is null ? "." : $" ({frontDoor.Pairing.Origin}), which runs what it has and sends other models to the host that has them.")
            : "No address yet. People outside your home need one with a real certificate: put a reverse proxy (for example Nginx on your " +
              "Linux server) in front of one of your hosts. Show proxy setup tells you how.";
        AutomationProperties.SetAutomationId(addressStatus, "OpenAiAddressStatus");
        OpenAiPanel.Children.Add(addressStatus);
        var home = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        home.SetResourceReference(StyleProperty, "Muted");
        home.Text = "At home, apps that can skip certificate checks may also use " +
            string.Join(", ", hosts.Select(h => $"{h.Pairing.Origin}{OpenAiEndpoint.BasePath} ({h.HostId})")) + ".";
        AutomationProperties.SetAutomationId(home, "OpenAiHomeAddresses");
        OpenAiPanel.Children.Add(home);

        var modelLine = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeights.SemiBold };
        modelLine.Text = models.Count == 0 ? "Models: none yet."
            : "Models: " + string.Join(", ", models.Select(m => $"{m.Model} ({string.Join(", ", m.Hosts)})")) + ".";
        AutomationProperties.SetAutomationId(modelLine, "OpenAiModels");
        OpenAiPanel.Children.Add(modelLine);

        var priority = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        priority.SetResourceReference(StyleProperty, "Muted");
        priority.Text = "Your Martlet first: any request of yours stops theirs, and they wait while yours run. Same priority as your " +
            "background work: they run beside your Thinking pool's work, and only your live conversations stop them. Requests use " +
            "the context size your own requests use, so a shared request never makes a host load its model again.";
        OpenAiPanel.Children.Add(priority);

        var who = new TextBlock { Text = keys.Length == 0 ? "Nobody has a key yet. Give someone access to make one." : "Who has access",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };
        OpenAiPanel.Children.Add(who);
        foreach (var key in keys) OpenAiPanel.Children.Add(OpenAiKeyRow(key, now));
    }

    private FrameworkElement OpenAiAddressEditor(OpenAiEndpointSettings settings, IReadOnlyList<PairedHost> hosts)
    {
        if (openAiAddressBox is null)
        {
            openAiAddressBox = new TextBox { MinWidth = 260, MaxLength = OpenAiEndpointSettings.MaximumAddressLength, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(openAiAddressBox, "OpenAiPublicAddress");
            AutomationProperties.SetName(openAiAddressBox, "Address to give people, for example https://llm.example.com/v1");
            openAiFrontDoorChoice = new ComboBox { MinWidth = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetAutomationId(openAiFrontDoorChoice, "OpenAiFrontDoor");
            AutomationProperties.SetName(openAiFrontDoorChoice, "The host your proxy sends requests to");
            openAiAddressProblem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            openAiAddressProblem.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(openAiAddressProblem, "OpenAiAddressProblem");
        }
        if (!openAiAddressBox.IsKeyboardFocusWithin) openAiAddressBox.Text = settings.PublicAddress ?? "";
        if (!openAiFrontDoorChoice!.IsKeyboardFocusWithin)
        {
            openAiFrontDoorChoice.Items.Clear();
            foreach (var host in hosts.OrderBy(h => h.HostId, StringComparer.Ordinal))
                openAiFrontDoorChoice.Items.Add(new ComboBoxItem { Content = host.HostId, Tag = host.HostId });
            openAiFrontDoorChoice.SelectedItem = openAiFrontDoorChoice.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string)i.Tag == OpenAiFrontDoor(settings)?.HostId);
        }
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        stack.Children.Add(new Label { Content = "_Address to give people (your proxy's https address)", Target = openAiAddressBox, Padding = new Thickness(0, 0, 0, 4) });
        var row = new WrapPanel();
        (openAiAddressBox.Parent as Panel)?.Children.Remove(openAiAddressBox);
        (openAiFrontDoorChoice.Parent as Panel)?.Children.Remove(openAiFrontDoorChoice);
        row.Children.Add(openAiAddressBox);
        var through = new TextBlock { Text = "sends to", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        through.SetResourceReference(StyleProperty, "Muted");
        row.Children.Add(through);
        row.Children.Add(openAiFrontDoorChoice);
        var save = new Button { Content = "Save", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(save, "OpenAiSaveAddress");
        AutomationProperties.SetName(save, "Save the address and the host it sends to");
        save.Click += (_, _) => SaveOpenAiAddress();
        row.Children.Add(save);
        var proxy = new Button { Content = "Show proxy setup", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(proxy, "OpenAiShowProxy");
        AutomationProperties.SetName(proxy, "Show how to put a reverse proxy with a real certificate in front of your host");
        proxy.Click += (_, _) => ShowOpenAiProxy();
        row.Children.Add(proxy);
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
        var chosen = (openAiFrontDoorChoice?.SelectedItem as ComboBoxItem)?.Tag as string;
        var settings = OpenAiEndpointSettings.Load(store.DataDirectory) with { PublicAddress = address, FrontDoorHostId = chosen };
        if (!settings.Save(store.DataDirectory))
        {
            ActionText.Text = "Couldn't save the address. Check access to Martlet's data folder.";
            return;
        }
        ErrorLog.Info($"Share: the OpenAI-compatible endpoint's address is {address ?? "not set"}, sent to {chosen ?? "the first host"}.");
        ActionText.Text = address is null ? "Removed the address to give people." : $"Saved: people use {address}. Your other computers get it too.";
        QueueSettingsSync();
        RenderOpenAi();
    }

    private void ShowOpenAiProxy()
    {
        var settings = OpenAiEndpointSettings.Load(store?.DataDirectory);
        var frontDoor = OpenAiFrontDoor(settings);
        var name = settings.PublicAddress is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : "llm.example.com";
        OpenAiProxyDialog.Show(this, name, frontDoor?.HostId ?? "your host", frontDoor?.Pairing.Origin ?? "https://<host address>:9443");
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
        ActionText.Text = $"{key.Name}'s requests now run {(equal ? "with the same priority as your background work" : "only when your Martlet doesn't need the model")}. " +
            "Your hosts follow within a minute.";
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
        var baseUrl = settings.PublicAddress ?? (OpenAiFrontDoor(settings)?.Pairing.Origin is { } origin ? origin + OpenAiEndpoint.BasePath : "https://<your address>/v1");
        OpenAiInviteDialog.Show(this, issued, baseUrl, settings.PublicAddress is not null, OpenAiModelList().Select(m => m.Model).ToArray());
    }

    /// <summary>The language models a host a friend shares with this PC offers, for its row; null before they were read.</summary>
    private string? SharedHostModelsText(string hostId) => sharedHostModels.TryGetValue(hostId, out var models)
        ? models.Count == 0 ? "No language models." : $"Language models: {string.Join(", ", models)}."
        : null;
}

/// <summary>What the Share page last read of your hosts' language models, kept in openai-models.json (host IDs and model names;
/// never a key or an address) so MCP openai_endpoint_status can show it without contacting a host.</summary>
internal static class OpenAiShare
{
    internal const string SummaryFile = "openai-models.json";

    internal static void SaveSummary(string directory, IReadOnlyDictionary<string, IReadOnlyList<string>> models,
        IReadOnlyDictionary<string, string> problems, IReadOnlyDictionary<string, IReadOnlyList<string>> shared, DateTimeOffset at)
    {
        var document = new
        {
            version = 1,
            checkedAt = at,
            hosts = models.Keys.Concat(problems.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => new
            {
                hostId = id,
                models = models.GetValueOrDefault(id) ?? [],
                problem = problems.GetValueOrDefault(id)
            }).ToArray(),
            sharedWithThisPc = shared.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { hostId = p.Key, models = p.Value }).ToArray()
        };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SummaryFile);
        var temporary = Path.Combine(directory, $"openai-models.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
