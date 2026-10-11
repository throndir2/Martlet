using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Martlet.Avatar.Hosting;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
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
    private HostingPreference welcomePreference = HostingPreference.Backup;
    private RecommendationPreferences welcomePreferences = new();
    private Martlet.Core.Planning.MachineSpecs? welcomeSpecs;
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
        // The plan step plans with the model catalog: load it now, while the owner reads the hardware.
        await PlanningOptionsAsync();
        if (closing) return;
        var specs = welcomeSpecs = DefaultSetup.Specs(machine, welcomeGpus);
        static string Gb(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        var card = specs.Gpus.MaxBy(g => g.VramGb);
        SpecRow("Gpu", "Graphics card", card is null ? "None Martlet can use" : $"{card.Name} ({card.Vendor})");
        SpecRow("Vram", "Graphics memory", card is null ? "None" : Gb(card.VramGb) +
            (welcomeGpus.Count > 0 ? $", {Gb(card.UsedGb)} in use now" : ""));
        SpecRow("Ram", "Memory", machine.MemoryGb is null ? $"unknown (planned as {Gb(specs.RamGb)})" : Gb(specs.RamGb));
        SpecRow("Cpu", "Processor", $"{specs.CpuThreads} threads" + (machine.Processor is { } cpu ? $" · {cpu}" : ""));
        WizardSpecs.Text = DescribeSpecs(specs) + (card is { IsNvidia: true } && welcomeGpus.Count == 0
            ? ". Martlet can't reach the NVIDIA driver (nvidia-smi), so jobs that need it stay on the processor or online"
            : "");
        WizardSpecsNextButton.IsEnabled = true;
        ErrorLog.Info($"Welcome: this PC has {WizardSpecs.Text}.");
    }

    private string DescribeSpecs(Martlet.Core.Planning.MachineSpecs specs)
    {
        var card = specs.Gpus.MaxBy(g => g.VramGb);
        var gpu = card is null ? "no graphics card Martlet can use"
            : $"{card.Name} ({card.Vendor}, {card.VramGb.ToString("0.#", CultureInfo.InvariantCulture)} GB graphics memory" +
              (welcomeGpus.Count > 0 ? $", {card.UsedGb.ToString("0.#", CultureInfo.InvariantCulture)} GB in use" : "") + ")";
        return $"{gpu} · {specs.RamGb.ToString("0", CultureInfo.InvariantCulture)} GB memory · {specs.CpuThreads} processor threads";
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

    private void WizardSpecsNext_Click(object sender, RoutedEventArgs e) => ShowWelcomeQuestions();

    // ---------- steps 3 and 4: three questions and the suggestion ----------

    /// <summary>The three questions (docs/RECOMMENDATION_DESIGN.md, "What Martlet asks"), with the saved answers, or Martlet's
    /// guess for games (a game library on this PC) and the defaults (only as a backup, Balanced).</summary>
    private void ShowWelcomeQuestions()
    {
        var saved = RecommendationPreferences.Load(store?.DataDirectory);
        var found = GamesHere;
        var games = saved.PlaysGames(ClusterDevice) ?? found;
        (games ? WizardGamesYes : WizardGamesNo).IsChecked = true;
        WizardGamesHint.Text = saved.PlaysGames(ClusterDevice) is not null ? "Your earlier answer is selected."
            : found ? "Martlet found a game library on this PC (Steam, Epic, GOG or another), so it selected Yes."
            : "Martlet found no game library on this PC, so it selected No.";
        (saved.Online switch { OnlineServices.Never => WizardOnlineNever, OnlineServices.Yes => WizardOnlineYes, _ => WizardOnlineBackup }).IsChecked = true;
        (saved.Quality switch { ReplyQuality.Quick => WizardQualityQuick, ReplyQuality.Smarter => WizardQualitySmarter, _ => WizardQualityBalanced })
            .IsChecked = true;
        ShowTour(TourPreference);
    }

    /// <summary>Show my setup: saves the three answers (the games answer for this PC) and shows the suggestion planned with them.</summary>
    private void WizardQuestionsNext_Click(object sender, RoutedEventArgs e)
    {
        var games = WizardGamesYes.IsChecked == true;
        var preferences = RecommendationPreferences.Load(store?.DataDirectory) with
        {
            Online = WizardOnlineNever.IsChecked == true ? OnlineServices.Never : WizardOnlineYes.IsChecked == true ? OnlineServices.Yes : OnlineServices.Backup,
            Quality = WizardQualityQuick.IsChecked == true ? ReplyQuality.Quick : WizardQualitySmarter.IsChecked == true ? ReplyQuality.Smarter : ReplyQuality.Balanced
        };
        preferences = preferences.WithGames(ClusterDevice, games);
        SaveRecommendationPreferences(preferences, reopen: false);
        ErrorLog.Info($"Welcome: games on this PC {(games ? "yes" : "no")}, online services {RecommendationPreferences.Words(preferences.Online).ToLowerInvariant()}, " +
            $"{RecommendationPreferences.Words(preferences.Quality).ToLowerInvariant()}.");
        ShowWelcomePlan(preferences);
    }

    private void ShowWelcomePlan(RecommendationPreferences preferences)
    {
        welcomePreferences = preferences;
        welcomePreference = preferences.Hosting;
        welcomeNeedsKey = false;
        ShowTour(TourPlan);
        RenderWelcomePlan();
    }

    /// <summary>The network this PC joined as the placement engine reads it (the other computers' hardware and what they run
    /// today, <see cref="DeviceCapacityInputs.JoinNetwork"/>), for the join suggestion.</summary>
    private PlanRequest? WelcomeNetwork()
    {
        if (!welcomeJoined) return null;
        var inputs = Inputs();
        return DeviceCapacityInputs.JoinNetwork(inputs, NetworkMap.Build(inputs), DefaultSetup.Catalog(null));
    }

    private IReadOnlyCollection<string> ConfiguredProviders() =>
        DefaultSetup.ConfiguredProviders(homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm), homeSettings?.ThinkingFallback);

    private void RenderWelcomePlan()
    {
        WizardAcceptButton.IsEnabled = false;
        WizardPlanList.Children.Clear();
        if (welcomeSpecs is not { } specs)
        {
            WizardPlanSummary.Text = "Go back a step: Martlet hasn't read this PC's hardware yet.";
            return;
        }
        var games = welcomePreferences.PlaysGames(ClusterDevice) ?? GamesHere;
        var plan = welcomePlan = DefaultSetup.Recommend(specs, welcomeGpus, machine.BestGpu, CultureInfo.CurrentUICulture, welcomePreference,
            ConfiguredProviders(), WelcomeNetwork(), welcomePreferences, games);
        var routes = homeSettings?.Setup?.Routes ?? [];
        WizardPlanTitle.Text = welcomeJoined ? "Here's what this PC can do for your network" : "Here's what fits this PC";
        var summary = $"You chose: {WelcomePreferences.Describe(welcomePreferences, games)}. ";
        if (welcomeJoined)
            summary += "Jobs your Martlet network already does stay where they are; this PC sets up the ones it should take on. ";
        summary += plan.ThinkingHosted is { } hosted
            ? $"Thinking goes online with {hosted.Option.DisplayName}" + (ConfiguredProviders().Contains(hosted.Option.ProviderId ?? "") ? "." : ", so you'll get a free key next.")
            : plan.Placement.External.Count == 0 ? "Everything runs on your computers." : "";
        WizardPlanSummary.Text = summary.Trim();
        foreach (var component in WelcomePlan.Shown) WizardPlanList.Children.Add(PlanCard(plan, component, routes));
        if (plan.Joining.Count > 0)
        {
            var heading = new TextBlock { Text = "What changes now that this PC joined", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) };
            WizardPlanList.Children.Add(heading);
            for (var i = 0; i < plan.Joining.Count; i++)
            {
                var line = new TextBlock { Text = "• " + plan.Joining[i].Why, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                AutomationProperties.SetAutomationId(line, $"WizardJoinSuggestion-{i}");
                WizardPlanList.Children.Add(line);
            }
        }
        var (vram, ram, cpu) = plan.Total();
        WizardPlanTotals.Text = $"All together on this PC: {vram}% graphics memory, {ram}% memory, {cpu}% processor while Martlet talks." +
            (plan.Placement.Notes.Count > 0 ? " " + string.Join(" ", plan.Placement.Notes) : "");
        WizardAcceptButton.IsEnabled = true;
        ErrorLog.Info($"Welcome: suggested ({welcomePreference}) {string.Join(" | ", WelcomePlan.Shown.Select(plan.Describe))}" +
            (plan.Joining.Count > 0 ? " Joining: " + string.Join(" | ", plan.Joining.Select(j => $"{j.Kind} {j.Component} {j.ToOptionId}")) : ""));
    }

    /// <summary>The Companion page where the welcome card's backup for <paramref name="component"/> is set up.</summary>
    internal static string FallbackPage(PlanComponent component) => TabTitle(component switch
    {
        PlanComponent.Voice => CompanionTab.Voice,
        PlanComponent.Listening => CompanionTab.Listening,
        PlanComponent.LipSync => CompanionTab.LipSync,
        _ => CompanionTab.Thinking
    });

    private Border PlanCard(WelcomePlan plan, PlanComponent component, IReadOnlyList<SetupRoute> routes)
    {
        var role = component switch
        {
            PlanComponent.Thinking => SetupRole.Llm,
            PlanComponent.Listening => SetupRole.Stt,
            PlanComponent.Voice => SetupRole.Tts,
            _ => (SetupRole?)null
        };
        var kept = role is { } r && routes.FirstOrDefault(x => x.Role == r) is { } route ? PlaceName(route) : null;
        var assignment = plan.Placement.Primary(component);
        var name = ComponentRanking.Name(component);
        var stack = new StackPanel();
        var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.Text = kept is not null ? $"{name}: keeps {kept}"
            : assignment is null ? $"{name}: left out"
            : $"{name}: {assignment.Option.DisplayName}, {WelcomePlan.Where(assignment)}";
        stack.Children.Add(title);
        var why = kept is not null ? "Already set up, so it stays as it is."
            : assignment is null ? plan.Placement.Dropped.FirstOrDefault(d => d.Component == component)?.Why ?? ""
            : assignment.Why + (plan.Placement.Fallback(component) is { } fallback
                ? $" If it's down, {fallback.Option.DisplayName} ({WelcomePlan.Where(fallback)}) can take over; set that up later in Companion › {FallbackPage(component)}."
                : "");
        var reason = new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        reason.SetResourceReference(StyleProperty, "Muted");
        stack.Children.Add(reason);
        if (kept is null && assignment is not null)
        {
            var (vram, ram, cpu) = plan.Share(component);
            var bars = new UniformGrid { Columns = 3 };
            bars.Children.Add(Bar("Graphics memory", vram, plan.Specs.Gpus.Count > 0, plan.UsualVram(component)));
            bars.Children.Add(Bar("Memory", ram, true));
            bars.Children.Add(Bar("Processor", cpu, true));
            stack.Children.Add(bars);
        }
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
        AutomationProperties.SetAutomationId(card, "WizardPlanCard-" + component);
        AutomationProperties.SetAutomationId(title, "WizardPlanItem-" + component);
        AutomationProperties.SetName(title, kept is null ? plan.Describe(component) : title.Text);
        return card;
    }

    /// <summary>One share of this PC: "Graphics memory 31-35%" when the part usually holds less (<paramref name="usual"/>)
    /// than the most it takes; the bar shows the most.</summary>
    private static StackPanel Bar(string label, int percent, bool available, int? usual = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var share = $"{label} {WelcomePlan.Percents(usual ?? percent, percent)}";
        var text = new TextBlock { Text = available ? share : $"{label}: none", FontSize = 12 };
        text.SetResourceReference(StyleProperty, "Muted");
        panel.Children.Add(text);
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = Math.Min(100, percent), Margin = new Thickness(0, 3, 0, 0) };
        AutomationProperties.SetName(bar, share);
        panel.Children.Add(bar);
        return panel;
    }

    private void WizardAccept_Click(object sender, RoutedEventArgs e)
    {
        if (welcomePlan is not { } plan) return;
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        if (plan.ThinkingHosted?.Option.ProviderId == "nvidia-build" && thinking is null)
        {
            welcomeNeedsKey = true;
            ShowWelcomeKey();
            return;
        }
        ApplyWelcomeAsync().Forget();
    }

    // ---------- step 5: a free NVIDIA Build key ----------

    private void ShowWelcomeKey()
    {
        var model = PlanningCatalog.SuggestedModel(ChatCompletionsEndpointCatalog.NvidiaBuildId) ?? ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId;
        WizardKeyIntro.Text = $"Martlet thinks with {model} on NVIDIA Build: free, no card needed, with a free NVIDIA account. It takes " +
            "about two minutes. NVIDIA logs what is sent to improve its products, so don't share personal data or voices with it. " +
            "Listening stays on this PC (Parakeet), so Thinking gets the words you said, not your voice.";
        WizardKeySteps.Text =
            "1. Press Open build.nvidia.com below and sign in, or create a free NVIDIA account with your email and confirm the email NVIDIA sends.\n" +
            "2. On the API Keys page choose Generate API Key. Accept NVIDIA's trial terms if asked; some accounts must verify a phone number.\n" +
            "3. Copy the key now: it starts with nvapi- and isn't shown again.\n" +
            "4. Paste it below, tick the box and press Save key. Windows Credential Manager keeps it; Martlet never shows it again. The same key also works for Pictures.\n" +
            "Want Thinking to hear your voice? Google Gemini can, with a free Google key: choose it later in Companion › Thinking.";
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
                PlanningCatalog.SuggestedModel(ChatCompletionsEndpointCatalog.NvidiaBuildId) ?? ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId,
                null, WizardKeyBox, consent: true);
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
            await ApplyWelcomeAsync();
        }
        finally { if (!closing) WizardKeySaveButton.IsEnabled = true; }
    }

    private void WizardKeySkip_Click(object sender, RoutedEventArgs e)
    {
        welcomeNeedsKey = false;
        ApplyWelcomeAsync().Forget();
    }

    // ---------- step 6: apply ----------

    /// <summary>Sets up what the accepted suggestion puts on this PC after one confirmation (<see cref="SetUpDefaultsAsync"/>:
    /// jobs already set up, or done by another computer in the network or online, are left alone), then lip-sync (Audio2Face here
    /// or on the host the plan chose, otherwise the voice's loudness), and leaves the owner on Home.</summary>
    private async Task ApplyWelcomeAsync()
    {
        if (welcomePlan is not { } plan) return;
        HideTour();
        Navigate(NavHome);
        bool Here(PlanComponent component) => plan.Placement.Primary(component)?.MachineId == DefaultSetup.ThisPc;
        var lipSync = plan.Placement.Primary(PlanComponent.LipSync);
        var lipSyncHost = lipSync is { MachineId: { } id } && id != DefaultSetup.ThisPc && FindHost(id) is not null ? id : null;
        var lipSyncLine = plan.Setup.LipSyncOnGpu
            ? "Lip-sync: Audio2Face on the graphics card (Martlet sets up its host service in Docker on this PC). The mouth follows the voice's loudness until it's ready."
            : lipSyncHost is not null ? $"Lip-sync: Audio2Face on {lipSyncHost}."
            : "Lip-sync: the mouth opens and closes with the voice's loudness. Nothing to install.";
        ErrorLog.Info($"Welcome: applying the suggestion ({welcomePreference}).");
        if (!await SetUpDefaultsAsync(Here(PlanComponent.Thinking), Here(PlanComponent.Listening), Here(PlanComponent.Voice),
                extra: lipSyncLine, planned: plan.Setup))
            return;
        if (closing) return;
        if (plan.Setup.LipSyncOnGpu) await SetUpThisPcHostAsync(AssignLipSyncAsync);
        else await AssignLipSyncAsync(lipSyncHost is null ? "off" : "host:" + lipSyncHost);
        if (!closing) ActionText.Text = DefaultSetupOutcome();
    }
}
