using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The welcome wizard (docs/WELCOME_WIZARD.md): start a new Martlet network or join one found on the local network,
/// read this PC's hardware, ask whether free online services are fine, suggest what runs where with each part's share of this
/// PC, get a free NVIDIA Build key when Thinking goes online, then set it all up after one confirmation and land on Home.</summary>
public partial class MainWindow
{
    internal const string NvidiaKeyPage = "https://build.nvidia.com/settings/api-keys";

    private bool welcomeJoined;
    private bool welcomeNeedsKey;
    private bool welcomeScanning;
    private WelcomePreference welcomePreference = WelcomePreference.LocalOnly;
    private WelcomeSpecs? welcomeSpecs;
    private IReadOnlyList<GpuNow> welcomeGpus = [];
    private WelcomePlan? welcomePlan;
    private IReadOnlyList<NearbyMartlet> welcomeFound = [];

    // ---------- step 1: new or existing Martlet network ----------

    private void WizardNewNetwork_Click(object sender, RoutedEventArgs e)
    {
        welcomeJoined = false;
        WizardJoinPanel.Visibility = Visibility.Collapsed;
        SetRole(DeviceRole.Companion);
        ShowWelcomeSpecs();
    }

    private void WizardJoinNetwork_Click(object sender, RoutedEventArgs e)
    {
        SetRole(DeviceRole.Companion);
        WizardJoinPanel.Visibility = Visibility.Visible;
        ScanForNetworkAsync().Forget();
    }

    private void WizardScanAgain_Click(object sender, RoutedEventArgs e)
    {
        WizardJoinPanel.Visibility = Visibility.Visible;
        ScanForNetworkAsync().Forget();
    }

    /// <summary>Looks for Martlet on this network the way Add a computer does (<see cref="Nearby.FindAsync"/>: about two
    /// seconds of broadcast queries on the private subnets, never a scan). Discovery only lists; trust comes from pairing.</summary>
    private async Task ScanForNetworkAsync()
    {
        if (welcomeScanning || closing) return;
        welcomeScanning = true;
        WizardScanAgainButton.IsEnabled = false;
        WizardFoundList.Children.Clear();
        WizardScanStatus.Text = "Looking for Martlet on your other computers...";
        try { welcomeFound = await Nearby.FindAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is SocketException or IOException)
        {
            welcomeFound = [];
            WizardScanStatus.Text = $"Couldn't look on your network ({error.Message}). Enter the other computer's address and pairing code instead.";
            return;
        }
        finally
        {
            welcomeScanning = false;
            WizardScanAgainButton.IsEnabled = true;
        }
        if (closing) return;
        ErrorLog.Info($"Welcome: found {welcomeFound.Count} Martlet network computer(s)" +
            (welcomeFound.Count == 0 ? "." : ": " + string.Join(", ", welcomeFound.Select(m => $"{m.Name} at {m.Where}")) + "."));
        ShowFoundNetworks();
    }

    private void ShowFoundNetworks()
    {
        WizardFoundList.Children.Clear();
        var known = NetworkMap.Hosts(Inputs()).Select(h => h.HostId).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < welcomeFound.Count; i++)
        {
            var martlet = welcomeFound[i];
            var joined = martlet.Hosts.All(known.Contains);
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var join = new Button
            {
                Content = joined ? "Continue" : "Join", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
                Padding = new Thickness(18, 6, 18, 6)
            };
            join.SetResourceReference(StyleProperty, "PrimaryButton");
            AutomationProperties.SetAutomationId(join, $"WizardConnect-{i}");
            AutomationProperties.SetName(join, joined ? "Continue" : $"Join {martlet.Name}'s Martlet network");
            join.Click += (_, _) => { if (joined) JoinedNetwork(); else JoinNetworkAsync(martlet).Forget(); };
            DockPanel.SetDock(join, Dock.Right);
            row.Children.Add(join);
            var text = new TextBlock
            {
                Text = $"{martlet.Name} ({martlet.Where}): Martlet {martlet.Version}, with {string.Join(", ", martlet.Hosts)}" +
                    (joined ? ". This PC is already in this network." : ""),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(text, $"WizardFound-{i}");
            row.Children.Add(text);
            WizardFoundList.Children.Add(row);
        }
        WizardScanStatus.Text = welcomeFound.Count switch
        {
            0 => "No Martlet network answered. Open Martlet on your other computer (it must run a host, with Let my other computers " +
                "find this PC on), then Look again, or enter its address and pairing code.",
            1 => "Found your Martlet network. Press Join, then Allow on that computer when both show the same check number.",
            _ => $"Found {welcomeFound.Count} computers with Martlet. Press Join on one, then Allow there when both show the same check number."
        };
    }

    /// <summary>Joins through Add a computer's check-number request (<see cref="HostsWindow"/>), so pairing and key pinning stay
    /// exactly as they are there.</summary>
    private async Task JoinNetworkAsync(NearbyMartlet? martlet)
    {
        if (store is null || setupService is null || closing) return;
        new HostsWindow(new AvatarProfileStore(store.DataDirectory), setupService, 0, null, martlet) { Owner = this }.ShowDialog();
        await RefreshHomeAsync();
        if (closing) return;
        if (InMartletNetwork()) JoinedNetwork();
        else WizardScanStatus.Text = "This PC hasn't joined yet. Press Join again, or enter the other computer's address and pairing code.";
    }

    private void WizardJoinManual_Click(object sender, RoutedEventArgs e) => JoinNetworkAsync(null).Forget();

    private void JoinedNetwork()
    {
        welcomeJoined = true;
        ErrorLog.Info("Welcome: this PC is in a Martlet network now.");
        ShowWelcomeSpecs();
    }

    // ---------- step 2: this PC's hardware ----------

    private void ShowWelcomeSpecs()
    {
        ShowTour(TourSpecs);
        ReadWelcomeSpecsAsync().Forget();
    }

    private async Task ReadWelcomeSpecsAsync()
    {
        WizardSpecsNextButton.IsEnabled = false;
        WizardSpecRows.Children.Clear();
        WizardSpecs.Text = "Reading this PC's graphics card, memory and processor...";
        if (ReferenceEquals(machine, MachineInfo.Unknown)) await ReadMachineAsync();
        try { welcomeGpus = await ListeningAdvisor.ReadGpusAsync(lifetime.Token); }
        catch (OperationCanceledException) { welcomeGpus = []; }
        if (closing) return;
        var specs = welcomeSpecs = DefaultSetup.Specs(machine, welcomeGpus);
        string Gb(double? value) => value is { } gb ? gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB" : "unknown";
        SpecRow("Gpu", "Graphics card", specs.GpuName is null ? "None Martlet can use" : $"{specs.GpuName}{(specs.GpuVendor is { } v && !specs.GpuName.Contains(v, StringComparison.OrdinalIgnoreCase) ? $" ({v})" : "")}");
        SpecRow("Vram", "Graphics memory", specs.VramGb is null ? "None" : Gb(specs.VramGb) +
            (specs.VramUsedGb is { } used ? $", {Gb(used)} in use now" : ""));
        SpecRow("Ram", "Memory", Gb(specs.RamGb));
        SpecRow("Cpu", "Processor", $"{specs.Threads} threads" + (specs.Processor is { } cpu ? $" · {cpu}" : ""));
        WizardSpecs.Text = specs.Describe();
        WizardSpecsNextButton.IsEnabled = true;
        ErrorLog.Info($"Welcome: this PC has {specs.Describe()}.");
    }

    private void SpecRow(string id, string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var name = new TextBlock { Text = label, Width = 150, FontWeight = FontWeights.SemiBold };
        row.Children.Add(name);
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(text, "WizardSpecRow-" + id);
        row.Children.Add(text);
        WizardSpecRows.Children.Add(row);
    }

    private void WizardSpecsNext_Click(object sender, RoutedEventArgs e) => ShowTour(TourPreference);

    // ---------- steps 3 and 4: preference and the suggestion ----------

    private void WizardPreferLocal_Click(object sender, RoutedEventArgs e) => ShowWelcomePlan(WelcomePreference.LocalOnly);
    private void WizardPreferOnline_Click(object sender, RoutedEventArgs e) => ShowWelcomePlan(WelcomePreference.FreeOnline);

    private void ShowWelcomePlan(WelcomePreference preference)
    {
        welcomePreference = preference;
        welcomeNeedsKey = false;
        ShowTour(TourPlan);
        RenderWelcomePlan();
    }

    private void RenderWelcomePlan()
    {
        WizardAcceptButton.IsEnabled = false;
        WizardPlanList.Children.Clear();
        if (welcomeSpecs is not { } specs)
        {
            WizardPlanSummary.Text = "Go back a step: Martlet hasn't read this PC's hardware yet.";
            return;
        }
        var plan = welcomePlan = DefaultSetup.Recommend(specs, welcomeGpus, machine.BestGpu, CultureInfo.CurrentUICulture, welcomePreference);
        var routes = homeSettings?.Setup?.Routes ?? [];
        WizardPlanTitle.Text = welcomeJoined ? "Here's what this PC can do for your network" : "Here's what fits this PC";
        var chose = welcomePreference == WelcomePreference.LocalOnly ? "everything stays on your computers" : "free online services are fine";
        WizardPlanSummary.Text = (welcomeJoined
            ? "Jobs your Martlet network already does stay where they are; this PC sets up the rest. "
            : "") + $"You chose: {chose}. " +
            (plan.ThinkingOnline ? "Thinking goes online, so you'll get a free NVIDIA key next." : "Everything runs on this PC.");
        foreach (var part in plan.Parts) WizardPlanList.Children.Add(PlanCard(plan, part, routes));
        var (vram, ram, cpu) = plan.Total();
        WizardPlanTotals.Text = $"All together on this PC: {vram}% graphics memory, {ram}% memory, {cpu}% processor while Martlet talks.";
        WizardAcceptButton.IsEnabled = true;
        ErrorLog.Info($"Welcome: suggested {string.Join(" | ", plan.Parts.Select(plan.Describe))}");
    }

    private Border PlanCard(WelcomePlan plan, WelcomePart part, IReadOnlyList<SetupRoute> routes)
    {
        var role = part.Job switch
        {
            WelcomeJob.Thinking => SetupRole.Llm,
            WelcomeJob.Listening => SetupRole.Stt,
            WelcomeJob.Voice => SetupRole.Tts,
            _ => (SetupRole?)null
        };
        var kept = role is { } r && routes.FirstOrDefault(x => x.Role == r) is { } route ? PlaceName(route) : null;
        var stack = new StackPanel();
        var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.Text = kept is null ? $"{WelcomePart.Title(part.Job)}: {part.What}, {part.Where}" : $"{WelcomePart.Title(part.Job)}: keeps {kept}";
        stack.Children.Add(title);
        var reason = new TextBlock { Text = kept is null ? part.Reason : "Already set up, so it stays as it is.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        reason.SetResourceReference(StyleProperty, "Muted");
        stack.Children.Add(reason);
        var (vram, ram, cpu) = plan.Share(part);
        if (kept is null)
        {
            var bars = new UniformGrid { Columns = 3 };
            bars.Children.Add(Bar("Graphics memory", vram, plan.Specs.VramGb is not null));
            bars.Children.Add(Bar("Memory", ram, true));
            bars.Children.Add(Bar("Processor", cpu, true));
            stack.Children.Add(bars);
        }
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
        AutomationProperties.SetAutomationId(card, "WizardPlanCard-" + part.Job);
        AutomationProperties.SetAutomationId(title, "WizardPlanItem-" + part.Job);
        AutomationProperties.SetName(title, kept is null ? plan.Describe(part) : title.Text);
        return card;
    }

    private static StackPanel Bar(string label, int percent, bool available)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var text = new TextBlock { Text = available ? $"{label} {percent}%" : $"{label}: none", FontSize = 12 };
        text.SetResourceReference(StyleProperty, "Muted");
        panel.Children.Add(text);
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = Math.Min(100, percent), Margin = new Thickness(0, 3, 0, 0) };
        AutomationProperties.SetName(bar, $"{label} {percent}%");
        panel.Children.Add(bar);
        return panel;
    }

    private void WizardAccept_Click(object sender, RoutedEventArgs e)
    {
        if (welcomePlan is not { } plan) return;
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        if (plan.ThinkingOnline && thinking is null)
        {
            welcomeNeedsKey = true;
            ShowWelcomeKey();
            return;
        }
        ApplyWelcomeAsync(thinking: !plan.ThinkingOnline).Forget();
    }

    // ---------- step 5: a free NVIDIA Build key ----------

    private void ShowWelcomeKey()
    {
        var model = ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId;
        WizardKeyIntro.Text = $"Martlet thinks with {model} on NVIDIA Build, free for personal use with an NVIDIA account. It takes about two minutes.";
        WizardKeySteps.Text =
            "1. Press Open build.nvidia.com below and sign in, or create a free NVIDIA account.\n" +
            "2. Choose Generate API Key (on the API Keys page), give it any name and press Generate Key.\n" +
            "3. Copy the key: it starts with nvapi- and NVIDIA shows it only once.\n" +
            "4. Paste it below, tick the box and press Save key. Windows Credential Manager keeps it; Martlet never shows it again.";
        WizardKeyStatus.Text = "";
        ShowTour(TourKey);
    }

    private void WizardKeyOpen_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(NvidiaKeyPage) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            WizardKeyStatus.Text = $"Couldn't open the browser ({error.Message}). Go to {NvidiaKeyPage} yourself.";
        }
    }

    private async void WizardKeySave_Click(object sender, RoutedEventArgs e)
    {
        if (WizardKeyConsent.IsChecked != true)
        {
            WizardKeyStatus.Text = "Tick the box to send your conversation to NVIDIA Build, then press Save key.";
            return;
        }
        if (WizardKeyBox.SecurePassword.Length == 0)
        {
            WizardKeyStatus.Text = "Paste your NVIDIA API key first (it starts with nvapi-).";
            return;
        }
        WizardKeySaveButton.IsEnabled = false;
        try
        {
            var provider = ThinkingProviders.First(p => p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl);
            await SaveCloudAsync(HostJob.Thinking, provider, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl,
                ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId, null, WizardKeyBox, consent: true);
            await RefreshHomeAsync();
            if (closing) return;
            var saved = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
            if (saved?.Origin != ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl || saved.CredentialId is null)
            {
                WizardKeyStatus.Text = string.IsNullOrWhiteSpace(ActionText.Text) ? "The key wasn't saved. Try again." : ActionText.Text;
                return;
            }
            ErrorLog.Info("Welcome: Thinking uses NVIDIA Build with the key saved in Windows Credential Manager.");
            welcomeNeedsKey = false;
            await ApplyWelcomeAsync(thinking: false);
        }
        finally { if (!closing) WizardKeySaveButton.IsEnabled = true; }
    }

    private void WizardKeySkip_Click(object sender, RoutedEventArgs e)
    {
        welcomeNeedsKey = false;
        ApplyWelcomeAsync(thinking: false).Forget();
    }

    // ---------- step 6: apply ----------

    /// <summary>Sets up the accepted suggestion after one confirmation (<see cref="SetUpDefaultsAsync"/>), then lip-sync, and
    /// leaves the owner on Home. <paramref name="thinking"/>: set up local Thinking too.</summary>
    private async Task ApplyWelcomeAsync(bool thinking)
    {
        if (welcomePlan is not { } plan) return;
        HideTour();
        Navigate(NavHome);
        var lipSync = plan.Parts.First(p => p.Job == WelcomeJob.LipSync);
        var audio2Face = lipSync.VramGb > 0;
        var lipSyncLine = audio2Face
            ? "Lip-sync: Audio2Face on the graphics card (Martlet sets up its host service in Docker on this PC). The mouth follows the voice's loudness until it's ready."
            : "Lip-sync: the mouth opens and closes with the voice's loudness. Nothing to install.";
        if (!await SetUpDefaultsAsync(thinking, extra: lipSyncLine, planned: plan.ThinkingOnline == !thinking ? plan.Setup : null)) return;
        if (closing) return;
        if (audio2Face) await SetUpThisPcHostAsync(AssignLipSyncAsync);
        else await AssignLipSyncAsync("off");
        if (!closing) ActionText.Text = DefaultSetupOutcome();
    }
}
