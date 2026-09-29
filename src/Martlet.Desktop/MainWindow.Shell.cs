using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The app shell: navigation, the welcome tour, the stage-aware home, the host dashboard and the devices map.</summary>
public partial class MainWindow
{
    private sealed record HomeStep(string Id, string Title, string Detail, bool Done, bool Optional, IReadOnlyList<StepCommand> Commands);
    private sealed record StepCommand(string Label, Action Run, bool Primary = false);
    private sealed record MapElement(NetworkNode Node, Button Card, Line? Track, Line? Flow, Ellipse? Ring);

    private static readonly string Version = AppVersions.Current;
    private DeviceRole? deviceRole;
    private MachineInfo machine = MachineInfo.Unknown;
    private AppSettings? homeSettings;
    private AvatarProfile? homeAvatar;
    private IReadOnlyList<PairedHost> homeHosts = [];
    private bool renderingBoard;
    private bool assigningRole;
    private readonly Dictionary<string, HostCheck> hostChecks = new(StringComparer.Ordinal);
    private readonly List<MapElement> mapElements = [];
    private Ellipse? mapGlow;
    private string selectedNode = "this-pc";
    private readonly Dictionary<Panel, HashSet<string>> previousDone = [];
    private int tourStep;
    private bool? hostServiceReachable;
    private bool refreshingHome;
    private bool hostBusy;
    private DependencyPropertyDescriptor? actionTextDescriptor;

    private DeviceRole Role => deviceRole ?? DeviceRole.Companion;

    private void InitializeShell()
    {
        actionTextDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        actionTextDescriptor.AddValueChanged(ActionText, ActionTextChanged);
        deviceRole = store is null ? DeviceRole.Companion : DeviceRolePreference.Load(store.DataDirectory);
        ApplyRole();
        RenderHome();
        if (deviceRole is null) ShowTour(0);
    }

    private void ReleaseShell() => actionTextDescriptor?.RemoveValueChanged(ActionText, ActionTextChanged);

    private void StartAmbientMotion()
    {
        Motion.Float(HeroFloat);
        Motion.Heartbeat(HeroHeart);
        Motion.Breathe(HeroGlow);
        Motion.Twinkle(Sparkle1, 1.6);
        Motion.Twinkle(Sparkle2, 2.1, 0.5);
        Motion.Twinkle(Sparkle3, 1.8, 1.0);
        Motion.Float(TourFloat);
        Motion.Heartbeat(TourHeart);
        Motion.Twinkle(TourSparkle, 1.7);
        Motion.Float(TourBlob1, 18, 9);
        Motion.Float(TourBlob2, 14, 11);
    }

    private async Task ReadMachineAsync()
    {
        try { machine = await Task.Run(MachineInfo.Read); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception) { }
        if (closing) return;
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }

    // ---------- status line ----------

    private void ActionTextChanged(object? sender, EventArgs e)
    {
        var visible = !string.IsNullOrWhiteSpace(ActionText.Text);
        if (!visible) { StatusBar.Visibility = Visibility.Collapsed; return; }
        StatusBar.Visibility = Visibility.Visible;
        Motion.Enter(StatusBar, dy: 10, milliseconds: 200);
    }

    private void DismissStatus_Click(object sender, RoutedEventArgs e) => ActionText.Text = "";

    // ---------- navigation ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage is null || DevicesPage is null || CompanionPage is null || SettingsPage is null) return;
        FrameworkElement page = ReferenceEquals(sender, NavDevices) ? DevicesPage
            : ReferenceEquals(sender, NavCompanion) ? CompanionPage
            : ReferenceEquals(sender, NavSettings) ? SettingsPage
            : HomePage;
        foreach (var candidate in new FrameworkElement[] { HomePage, DevicesPage, CompanionPage, SettingsPage })
            candidate.Visibility = ReferenceEquals(candidate, page) ? Visibility.Visible : Visibility.Collapsed;
        Motion.Enter(page);
        if (ReferenceEquals(page, DevicesPage)) RenderMap();
        if (ReferenceEquals(page, CompanionPage)) Motion.Cascade(CompanionCards.Children.OfType<UIElement>());
    }

    private void Navigate(RadioButton item)
    {
        if (item.IsChecked == true) Nav_Checked(item, new RoutedEventArgs());
        else item.IsChecked = true;
    }

    private void OpenDevices_Click(object sender, RoutedEventArgs e) => Navigate(NavDevices);

    // ---------- device role and welcome tour ----------

    private void SetRole(DeviceRole role)
    {
        deviceRole = role;
        if (store is not null)
        {
            try { DeviceRolePreference.Save(store.DataDirectory, role); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ActionText.Text = "Could not save device-role.txt. This choice applies until Martlet closes; check access to your data directory.";
            }
        }
        ApplyRole();
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }

    private void ApplyRole()
    {
        var host = Role == DeviceRole.Host;
        CompanionHome.Visibility = host ? Visibility.Collapsed : Visibility.Visible;
        HostHome.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        NavCompanion.Visibility = host ? Visibility.Collapsed : Visibility.Visible;
        ModeText.Text = host ? "Host PC" : "Companion PC";
        RoleText.Text = host
            ? "This PC is a Martlet host. It lends its graphics card to your main PC, and Home shows the host dashboard."
            : "This is your companion PC. You talk with Martlet here, and Home shows your setup and Start talking.";
        UseCompanionButton.IsEnabled = host;
        UseHostButton.IsEnabled = !host;
        if (host && NavCompanion.IsChecked == true) Navigate(NavHome);
    }

    private void UseCompanion_Click(object sender, RoutedEventArgs e) { SetRole(DeviceRole.Companion); Navigate(NavHome); }
    private void UseHost_Click(object sender, RoutedEventArgs e) { SetRole(DeviceRole.Host); Navigate(NavHome); }
    private void ReplayTour_Click(object sender, RoutedEventArgs e) => ShowTour(0);

    private StackPanel[] TourPanels => [TourWelcome, TourRole, TourStart];

    private void ShowTour(int step)
    {
        var appearing = Tour.Visibility != Visibility.Visible;
        Tour.BeginAnimation(OpacityProperty, null);
        Tour.Opacity = 1;
        Tour.Visibility = Visibility.Visible;
        tourStep = step;
        var panels = TourPanels;
        for (var i = 0; i < panels.Length; i++) panels[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        TourBackButton.Visibility = step > 0 ? Visibility.Visible : Visibility.Hidden;
        TourDots.Children.Clear();
        for (var i = 0; i < panels.Length; i++)
        {
            var dot = new Border { Height = 8, Width = i == step ? 26 : 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, i == step ? "AccentBrush" : "BorderBrush");
            TourDots.Children.Add(dot);
        }
        AutomationProperties.SetName(TourDots, $"Step {step + 1} of {panels.Length}");
        if (appearing) Motion.Enter(TourCard, dy: 26, milliseconds: 360);
        Motion.Enter(panels[step], dx: appearing ? 0 : 36, dy: 0, milliseconds: 320);
        Motion.Cascade(panels[step].Children.OfType<Button>(), 70);
    }

    private void HideTour() => Motion.FadeOut(Tour, () =>
    {
        Tour.Visibility = Visibility.Collapsed;
        Tour.Opacity = 1;
    });

    private void TourBegin_Click(object sender, RoutedEventArgs e) => ShowTour(1);
    private void TourBack_Click(object sender, RoutedEventArgs e) => ShowTour(Math.Max(0, tourStep - 1));

    private void TourCompanion_Click(object sender, RoutedEventArgs e)
    {
        SetRole(DeviceRole.Companion);
        ShowTour(2);
    }

    private void TourHost_Click(object sender, RoutedEventArgs e)
    {
        SetRole(DeviceRole.Host);
        HideTour();
        Navigate(NavHome);
    }

    private void TourAdvisor_Click(object sender, RoutedEventArgs e) { HideTour(); Advisor_Click(sender, e); }
    private void TourSetup_Click(object sender, RoutedEventArgs e) { HideTour(); Setup_Click(sender, e); }

    private void TourDemo_Click(object sender, RoutedEventArgs e)
    {
        HideTour();
        Navigate(NavSettings);
        Dispatcher.BeginInvoke(() => DemoCard.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void TourSkip_Click(object sender, RoutedEventArgs e)
    {
        if (deviceRole is null) SetRole(DeviceRole.Companion);
        HideTour();
    }

    // ---------- home ----------

    private async Task RefreshHomeAsync()
    {
        if (store is null || setupService is null || closing || refreshingHome || setupOperations.IsRunning) return;
        refreshingHome = true;
        try
        {
            var loaded = await setupService.LoadAsync(lifetime.Token);
            homeSettings = loaded.Settings;
            homeAvatar = loaded.Settings is { } settings
                ? (await new AvatarProfileStore(store.DataDirectory).LoadAsync(settings.Profile.Id, lifetime.Token)).Profile
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ContractException or JsonException or OperationCanceledException) { }
        finally { refreshingHome = false; }
        if (closing) return;
        try { homeHosts = HostRegistry.Load(store.DataDirectory, homeAvatar?.RemoteHost, machine.LanAddress ?? HostSetupCommands.ThisPcAddress()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            homeHosts = [];
            ActionText.Text = error.Message;
        }
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }

    private static string Greeting() => DateTime.Now.Hour switch
    {
        < 5 => "UP LATE?",
        < 12 => "GOOD MORNING",
        < 18 => "GOOD AFTERNOON",
        _ => "GOOD EVENING"
    };

    private void RenderHome()
    {
        GreetingText.Text = Greeting();
        var routes = homeSettings?.Setup?.Routes ?? [];
        SetupRoute? Route(SetupRole role) => routes.FirstOrDefault(r => r.Role == role);
        var llm = Route(SetupRole.Llm);
        var stt = Route(SetupRole.Stt);
        var tts = Route(SetupRole.Tts);
        var brainReady = NetworkMap.IsReady(llm);
        var voiceReady = NetworkMap.IsReady(stt) && NetworkMap.IsReady(tts);
        var audio = homeSettings?.Audio;
        var audioTested = audio is { Input.Checkpoint: not null, Output.Checkpoint: not null };
        var characterName = homeAvatar is { } saved && BundledLive2D.IsBuiltIn(saved.ModelPath)
            ? saved.ModelPath[BundledLive2D.Prefix.Length..] : homeAvatar is null ? "Hiyori" : "Your character";
        var paired = NetworkMap.Hosts(Inputs()).Count;
        var hosts = paired > 0 || routes.Any(r => r.Gateway is not null);

        var steps = new List<HomeStep>
        {
            new("brain", "How Martlet thinks",
                brainReady ? $"{NetworkMap.ProviderName(llm!)}: {llm!.ModelId}"
                    : llm is not null ? "Chosen. Review its data and cost details in Setup to finish."
                    : "Choose a cloud model (OpenRouter, NVIDIA Build or OpenAI) or one on your own computers.",
                brainReady, false, [new(brainReady ? "Change" : "Choose", () => RunNodeAction(NodeAction.Setup), !brainReady)]),
            new("voice", "Its voice and ears",
                voiceReady ? $"Listens with {NetworkMap.ProviderName(stt!)}, speaks with {NetworkMap.ProviderName(tts!)}"
                    : "Add speech-to-text and a voice so you can talk out loud. You can always type instead.",
                voiceReady, true, [new(voiceReady ? "Change" : "Set up", () => RunNodeAction(NodeAction.Setup))]),
            new("audio", "Microphone and speakers",
                audioTested ? "Tested on this PC" : audio is not null ? "Chosen, not tested yet" : "Pick and test them. Nothing leaves this PC.",
                audioTested, true, [new(audioTested ? "Change" : "Test", () => RunNodeAction(NodeAction.AudioSetup))]),
            new("character", "Character",
                avatar.IsShowing ? $"{characterName} is on your desktop" : $"{characterName} is ready. Show it or choose your own model.",
                homeAvatar is not null || avatar.IsShowing, true,
                [new(avatar.IsShowing ? "Hide" : "Show", () => RunNodeAction(NodeAction.ToggleCharacter)),
                 new("Customize", () => RunNodeAction(NodeAction.Character))]),
            new("hosts", "More computers",
                paired > 1 ? $"{paired} Martlet hosts are paired. Hand them jobs on the Devices map."
                    : hosts ? "A Martlet host is paired. See it on the Devices map." : "Lend a GPU PC to Martlet for lip-sync and more.",
                hosts, true, [new(hosts ? "Open map" : "Add", () => { if (hosts) Navigate(NavDevices); else RunNodeAction(NodeAction.AddComputer); })])
        };

        if (brainReady)
        {
            StageTitle.Text = "Ready when you are";
            StageText.Text = $"{NetworkMap.ProviderName(llm!)} is its brain. Type or hold to talk; you approve each message before anything is sent.";
            PrimaryStageButton.Visibility = Visibility.Collapsed;
            ConversationButton.SetResourceReference(StyleProperty, "PrimaryButton");
        }
        else
        {
            var nothingYet = homeSettings is null || routes.Count == 0;
            StageTitle.Text = nothingYet ? "Let's bring your companion to life" : "Almost there";
            StageText.Text = nothingYet
                ? "First, choose how Martlet thinks: a cloud model, or one on your own computers. Everything else is optional."
                : llm is not null ? "Your conversation model is chosen. Review its data and cost details in Setup to finish."
                : "Choose how Martlet thinks to start talking. Everything else is optional.";
            PrimaryStageButton.Content = nothingYet ? "Choose how Martlet thinks" : "Finish setup";
            PrimaryStageButton.Visibility = Visibility.Visible;
            ConversationButton.ClearValue(StyleProperty);
        }

        RenderSteps(StepsPanel, steps, numbered: true);
        var done = steps.Count(s => s.Done);
        ProgressText.Text = $"{done} of {steps.Count} done";
        if (ProgressFill.RenderTransform is ScaleTransform fill) Motion.ScaleX(fill, (double)done / steps.Count);

        DeviceChips.Children.Clear();
        foreach (var node in NetworkMap.Build(Inputs()).Where(n => n.Kind != NodeKind.Add))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Dot(node.Health, 8, new Thickness(0, 0, 6, 0)));
            row.Children.Add(Glyph(node.Glyph, 12, new Thickness(0, 0, 6, 0)));
            row.Children.Add(new TextBlock { Text = node.Title, FontSize = 13, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border { Child = row, Padding = new Thickness(10, 4, 10, 4) };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, $"{node.Title}: {node.HealthText}");
            DeviceChips.Children.Add(chip);
        }
        RenderHost();
    }

    private void RenderSteps(Panel panel, IReadOnlyList<HomeStep> steps, bool numbered)
    {
        var firstRender = !previousDone.TryGetValue(panel, out var before) || panel.Children.Count == 0;
        panel.Children.Clear();
        var current = steps.FirstOrDefault(s => !s.Done && !s.Optional) ?? steps.FirstOrDefault(s => !s.Done);
        var done = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Done) done.Add(step.Id);
            var row = new Border { Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 0, 6) };
            if (ReferenceEquals(step, current)) row.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var mark = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            if (step.Done)
            {
                mark.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                mark.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                var check = Glyph("\uE73E", 14, default);
                check.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
                mark.Child = check;
            }
            else
            {
                mark.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                mark.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                mark.Child = new TextBlock
                {
                    Text = numbered ? (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) : "\u2022", FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap
                };
            }
            grid.Children.Add(mark);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
            title.Inlines.Add(new System.Windows.Documents.Run(step.Title));
            if (step.Optional && !step.Done)
            {
                var optional = new System.Windows.Documents.Run("  optional") { FontSize = 12, FontWeight = FontWeights.Normal };
                optional.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
                title.Inlines.Add(optional);
            }
            text.Children.Add(title);
            var detail = new TextBlock { Text = step.Detail, Margin = new Thickness(0, 2, 0, 0) };
            detail.SetResourceReference(StyleProperty, "Muted");
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            foreach (var command in step.Commands)
            {
                var button = new Button { Content = command.Label, Margin = new Thickness(8, 0, 0, 0), MinWidth = 86 };
                if (command.Primary && ReferenceEquals(step, current)) button.SetResourceReference(StyleProperty, "PrimaryButton");
                AutomationProperties.SetName(button, $"{command.Label}: {step.Title}");
                AutomationProperties.SetAutomationId(button, $"Step-{step.Id}-{actions.Children.Count}");
                var run = command.Run;
                button.Click += (_, _) => run();
                actions.Children.Add(button);
            }
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);
            row.Child = grid;
            AutomationProperties.SetName(row, $"{step.Title}: {(step.Done ? "done" : "to do")}. {step.Detail}");
            panel.Children.Add(row);

            if (firstRender) Motion.Enter(row, delay: i * 60);
            if (step.Done && (firstRender || before?.Contains(step.Id) == false)) Motion.Pop(mark, firstRender ? 200 + i * 60 : 0);
        }
        previousDone[panel] = done;
    }

    private void PrimaryStage_Click(object sender, RoutedEventArgs e) => Setup_Click(sender, e);

    // ---------- host dashboard ----------

    private HostSetupTarget ThisPcTarget() => new(HostSetupMethod.ThisPcDocker, "",
        machine.LanAddress ?? HostSetupCommands.ThisPcAddress() ?? "", HostSetupCommands.SuggestedHostId(Environment.MachineName), Version);

    private void RenderHost()
    {
        if (Role != DeviceRole.Host) return;
        HostAddressText.Text = machine.LanAddress is { } address
            ? $"https://{address}:{WindowsFirewall.Port}" : "No private network address found yet";
        HostStatusText.Text = hostServiceReachable switch
        {
            true => "Host service is reachable",
            false => "Not reachable yet",
            null => "Not checked yet"
        };
        if (hostServiceReachable == true) Motion.PulseRing(HostPulse, to: 1.35);
        else
        {
            HostPulse.BeginAnimation(OpacityProperty, null);
            HostPulse.Opacity = 0;
        }
        var nvidia = machine.BestGpu is { IsNvidia: true } gpu ? $"This PC has {gpu.Describe()}." : "No NVIDIA graphics card was found on this PC.";
        var steps = new List<HomeStep>
        {
            new("docker", "Docker Desktop",
                machine.DockerRunning ? "Running." : machine.DockerInstalled ? "Installed, but not running." : "Runs the host service in containers. Free for personal use.",
                machine.DockerRunning, false,
                machine.DockerRunning ? [] : machine.DockerInstalled
                    ? [new("Start Docker Desktop", StartDocker, true)]
                    : [new("Install Docker Desktop", InstallDocker, true)]),
            new("service", "Host service",
                hostServiceReachable == true ? "Set up and reachable on your network."
                    : "Sets up the gateway once. Windows asks once to allow TCP 9443 from your private network.",
                hostServiceReachable == true, false, [new("Set up host service", () => _ = SetUpHostServiceAsync(), true)]),
            new("pair", "Pair your main PC",
                "Open the pairing console here; it shows a one-use code. On your main PC, go to Devices > Add a computer > Pair and paste it.",
                false, false, [new("Open pairing console", () => LaunchHost(HostAction.Pair), true)]),
            new("roles", "Add roles",
                "Audio2Face lip-sync needs an NVIDIA GPU (4 GB+) and a free NVIDIA NGC API key. " + nvidia,
                false, true, [new("Add Audio2Face", () => LaunchHost(HostAction.AddAudio2Face), true), new("Remove", () => LaunchHost(HostAction.RemoveAudio2Face))]),
            new("update", "Keep it up to date",
                thisPcHostVersion is null
                    ? $"Rebuilds the host service from Martlet {Version} and restarts it; pairings and roles stay. Turn on host updates in Settings to do this by itself."
                    : AppVersions.IsOlder(thisPcHostVersion, Version)
                        ? $"The host service runs Martlet {thisPcHostVersion}; this app is {Version}. Update it; pairings and roles stay."
                        : $"The host service runs Martlet {thisPcHostVersion}, the same as this app.",
                thisPcHostVersion is not null && !AppVersions.IsOlder(thisPcHostVersion, Version), true,
                [new("Update host service", () => LaunchHost(HostAction.Update))])
        };
        RenderSteps(HostStepsPanel, steps, numbered: true);
    }

    private void LaunchHost(HostAction action)
    {
        try
        {
            HostSetupCommands.Launch(ThisPcTarget(), action);
            ActionText.Text = action switch
            {
                HostAction.Setup => "Host setup opened in a console window. Answer its questions there, then check the host service.",
                HostAction.Pair => "The pairing console opened. Type start, then pair with your main PC's device ID and role voice; it shows a one-use code.",
                HostAction.Status => "Host status opened in a console window.",
                HostAction.Update => $"Host service update opened in a console window. It rebuilds from Martlet {Version} and restarts the gateway; pairings and roles stay.",
                _ => "Opened in a console window. Confirm each change there."
            };
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            ActionText.Text = error.Message;
        }
    }

    private void HostStatusConsole_Click(object sender, RoutedEventArgs e) => LaunchHost(HostAction.Status);

    private async Task SetUpHostServiceAsync()
    {
        if (hostBusy) return;
        var target = ThisPcTarget();
        if (!HostSetupCommands.IsPrivate(target.Address))
        {
            ActionText.Text = "This PC has no private network address (10.x, 172.16-31.x or 192.168.x). Connect it to your home network first.";
            return;
        }
        hostBusy = true;
        string? firewall;
        try { firewall = await HostsWindow.OpenFirewallAsync(this, target.Address, text => ActionText.Text = text, lifetime.Token); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { firewall = $"Windows Firewall was not changed ({error.Message}). Other PCs may not reach this host."; }
        catch (OperationCanceledException) { return; }
        finally { hostBusy = false; }
        LaunchHost(HostAction.Setup);
        if (firewall is not null) ActionText.Text = firewall + " " + ActionText.Text;
    }

    private void InstallDocker()
    {
        if (HostsWindow.InstallDockerDesktop(this) is { } status) ActionText.Text = status;
    }

    private void StartDocker()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose();
            ActionText.Text = "Starting Docker Desktop. The first start can take a few minutes; accept Docker's terms if it asks, then check the host service.";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { ActionText.Text = error.Message; }
    }

    private async void CheckHostService_Click(object sender, RoutedEventArgs e)
    {
        if (hostBusy) return;
        var address = machine.LanAddress ?? HostSetupCommands.ThisPcAddress();
        if (address is null || !IPAddress.TryParse(address, out var ip))
        {
            ActionText.Text = "This PC has no private network address to check. Connect it to your home network first.";
            return;
        }
        hostBusy = true;
        CheckHostServiceButton.IsEnabled = false;
        HostStatusText.Text = "Checking...";
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(ip, WindowsFirewall.Port, timeout.Token);
            hostServiceReachable = true;
        }
        catch (SocketException) { hostServiceReachable = false; }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { hostServiceReachable = false; }
        catch (OperationCanceledException) { return; }
        finally
        {
            hostBusy = false;
            if (!closing) CheckHostServiceButton.IsEnabled = true;
        }
        if (hostServiceReachable == true) thisPcHostVersion = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
        await ReadMachineAsync();
        if (!closing)
            ActionText.Text = hostServiceReachable == true
                ? $"The host service answers on {address}:{WindowsFirewall.Port}. Pair your main PC next."
                : $"Nothing answered on {address}:{WindowsFirewall.Port}. Make sure Docker Desktop is running, then set up the host service.";
    }

    // ---------- devices map ----------

    private HostHardwareStore? HardwareStore => store is null ? null : new(store.DataDirectory);

    private NetworkInputs Inputs() => new(machine, Role, homeSettings, homeAvatar, avatar.IsShowing, hostChecks,
        HardwareStore?.Load() ?? [], homeHosts, hostUpdateNotes);

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        _ = ReadMachineAsync();
        _ = RefreshHomeAsync();
    }

    private void MapHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MapCanvas.Width = Math.Max(MapHost.ActualWidth - 16, 560);
        MapCanvas.Height = Math.Max(MapHost.ActualHeight - 16, 400);
        LayoutMap();
    }

    /// <summary>Side-by-side map and details when there is room; otherwise the details stack under the map.</summary>
    private void DevicesPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = DevicesPage.ActualWidth < 880;
        var height = Math.Max(420, DevicesPage.ActualHeight - 150 - (RolesCard.IsVisible ? RolesCard.ActualHeight + 16 : 0));
        Grid.SetRow(DetailCard, narrow ? 3 : 2);
        Grid.SetColumn(DetailCard, narrow ? 0 : 1);
        Grid.SetColumnSpan(DetailCard, narrow ? 2 : 1);
        Grid.SetColumnSpan(MapHost, narrow ? 2 : 1);
        DetailColumn.Width = new GridLength(narrow ? 0 : 320);
        MapHost.Margin = narrow ? new Thickness(0, 0, 0, 16) : new Thickness(0, 0, 16, 0);
        MapHost.Height = narrow ? 440 : height;
        DetailCard.Height = narrow ? double.NaN : height;
    }

    private void RenderMap()
    {
        var nodes = NetworkMap.Build(Inputs());
        RenderRolesBoard(nodes);
        if (nodes.All(n => n.Id != selectedNode)) selectedNode = "this-pc";
        MapCanvas.Children.Clear();
        mapElements.Clear();
        mapGlow = new Ellipse { Width = 220, Height = 220, Opacity = 0.55, IsHitTestVisible = false };
        mapGlow.SetResourceReference(Shape.FillProperty, "GlowBrush");
        MapCanvas.Children.Add(mapGlow);
        foreach (var node in nodes.Skip(1))
        {
            var track = new Line { StrokeThickness = 2, Opacity = 0.35, IsHitTestVisible = false };
            track.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
            var flow = new Line { StrokeThickness = 2.5, StrokeDashArray = [2, 4], StrokeDashCap = PenLineCap.Round, IsHitTestVisible = false, Opacity = node.Kind is NodeKind.Add or NodeKind.Missing ? 0.4 : 0.9 };
            flow.SetResourceReference(Shape.StrokeProperty, node.Health == NodeHealth.Attention ? "WarningBrush" : "AccentBrush");
            MapCanvas.Children.Add(track);
            MapCanvas.Children.Add(flow);
            mapElements.Add(new(node, null!, track, flow, null));
        }
        var index = 0;
        foreach (var node in nodes)
        {
            var (card, ring) = NodeCard(node);
            MapCanvas.Children.Add(card);
            var existing = mapElements.FindIndex(m => m.Node.Id == node.Id);
            if (existing >= 0) mapElements[existing] = mapElements[existing] with { Card = card, Ring = ring };
            else mapElements.Insert(0, new(node, card, null, null, ring));
            Motion.Enter(card, dy: 0, milliseconds: 360, delay: index++ * 70);
        }
        LayoutMap();
        foreach (var element in mapElements)
        {
            if (element.Flow is { } flow && element.Node.Kind is not (NodeKind.Add or NodeKind.Missing)) Motion.Flow(flow);
            if (element.Ring is { } ring && element.Node.Health == NodeHealth.Ready) Motion.PulseRing(ring, delay: index++ * 0.3 % 2);
        }
        Motion.Breathe(mapGlow);
        SelectNode(selectedNode, animate: false);
    }

    private static readonly Dictionary<int, double[]> Spreads = new()
    {
        [1] = [90], [2] = [130, 50], [3] = [155, 90, 25], [4] = [165, 118, 62, 15]
    };

    private void LayoutMap()
    {
        if (mapElements.Count == 0 || mapGlow is null) return;
        double width = MapCanvas.Width, height = MapCanvas.Height, cx = width / 2, cy = height / 2;
        var sizes = new Dictionary<string, Size>(StringComparer.Ordinal);
        foreach (var element in mapElements)
        {
            element.Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            sizes[element.Node.Id] = element.Card.DesiredSize;
        }
        double rx = width / 2 - 104, ry = height / 2 - 66;
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal) { ["this-pc"] = new(cx, cy) };
        void Place(IReadOnlyList<MapElement> group, bool top)
        {
            var angles = Spreads.TryGetValue(group.Count, out var known) ? known
                : Enumerable.Range(0, group.Count).Select(i => 170 - 160.0 * i / Math.Max(1, group.Count - 1)).ToArray();
            for (var i = 0; i < group.Count; i++)
            {
                var radians = angles[i] * Math.PI / 180;
                positions[group[i].Node.Id] = new(cx + rx * Math.Cos(radians), cy + (top ? -1 : 1) * ry * Math.Sin(radians));
            }
        }
        var others = mapElements.Where(m => m.Node.Kind != NodeKind.ThisPc).ToList();
        Place(others.Where(m => m.Node.Kind is NodeKind.Cloud or NodeKind.Missing).ToList(), top: true);
        Place(others.Where(m => m.Node.Kind is NodeKind.Host or NodeKind.Add).OrderBy(m => m.Node.Kind == NodeKind.Add).Reverse().ToList(), top: false);
        foreach (var element in mapElements)
        {
            var point = positions[element.Node.Id];
            var size = sizes[element.Node.Id];
            Canvas.SetLeft(element.Card, point.X - size.Width / 2);
            Canvas.SetTop(element.Card, point.Y - size.Height / 2);
            foreach (var line in new[] { element.Track, element.Flow })
            {
                if (line is null) continue;
                line.X1 = cx; line.Y1 = cy; line.X2 = point.X; line.Y2 = point.Y;
            }
        }
        Canvas.SetLeft(mapGlow, cx - mapGlow.Width / 2);
        Canvas.SetTop(mapGlow, cy - mapGlow.Height / 2);
    }

    private (Button Card, Ellipse Ring) NodeCard(NetworkNode node)
    {
        var header = new DockPanel();
        var status = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
        var ring = Dot(node.Health, 12, default);
        ring.Opacity = 0;
        status.Children.Add(ring);
        status.Children.Add(Dot(node.Health, 10, default));
        if (node.Kind == NodeKind.Add) status.Visibility = Visibility.Hidden;
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(status);
        var bubble = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 10, 0) };
        bubble.SetResourceReference(Border.BackgroundProperty, node.Kind == NodeKind.ThisPc ? "AccentBrush" : "SoftBrush");
        var glyph = Glyph(node.Glyph, 17, default);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, node.Kind == NodeKind.ThisPc ? "OnAccentBrush" : "AccentBrush");
        bubble.Child = glyph;
        DockPanel.SetDock(bubble, Dock.Left);
        header.Children.Add(bubble);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = node.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var subtitle = new TextBlock { Text = node.Subtitle, FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        names.Children.Add(subtitle);
        header.Children.Add(names);

        var content = new StackPanel();
        content.Children.Add(header);
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var chip in node.Roles.Select(r => r.Chip).Distinct().Take(3))
        {
            var border = new Border { Child = new TextBlock { Text = chip, FontSize = 11, TextWrapping = TextWrapping.NoWrap } };
            border.SetResourceReference(StyleProperty, "Chip");
            chips.Children.Add(border);
        }
        if (chips.Children.Count > 0) content.Children.Add(chips);

        var card = new Button { Content = content, Width = node.Kind == NodeKind.ThisPc ? 200 : 184 };
        card.SetResourceReference(StyleProperty, "NodeCard");
        AutomationProperties.SetAutomationId(card, "Node-" + node.Id);
        AutomationProperties.SetName(card, node.Kind == NodeKind.Add ? $"{node.Title}: {node.Subtitle}"
            : $"{node.Title}, {node.Subtitle}. {node.HealthText}. Runs: {string.Join(", ", node.Roles.Select(r => r.Name))}");
        var id = node.Id;
        card.Click += (_, _) => SelectNode(id, animate: true);
        return (card, ring);
    }

    private void SelectNode(string id, bool animate)
    {
        selectedNode = id;
        foreach (var element in mapElements)
            element.Card.Tag = element.Node.Id == id ? "Selected" : element.Node.Kind is NodeKind.Add or NodeKind.Missing ? "Ghost" : null;
        if (mapElements.FirstOrDefault(m => m.Node.Id == id)?.Node is not { } node) return;
        RenderDetail(node);
        if (animate) Motion.Enter(DetailContent, dx: 24, dy: 0, milliseconds: 240);
    }

    private void RenderDetail(NetworkNode node)
    {
        DetailContent.Children.Clear();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var bubble = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Margin = new Thickness(0, 0, 14, 0) };
        bubble.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
        var glyph = Glyph(node.Glyph, 24, default);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        bubble.Child = glyph;
        DockPanel.SetDock(bubble, Dock.Left);
        header.Children.Add(bubble);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = node.Title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock { Text = node.Subtitle };
        subtitle.SetResourceReference(StyleProperty, "Muted");
        names.Children.Add(subtitle);
        header.Children.Add(names);
        DetailContent.Children.Add(header);

        if (node.Kind != NodeKind.Add)
        {
            var pill = new StackPanel { Orientation = Orientation.Horizontal };
            pill.Children.Add(Dot(node.Health, 9, new Thickness(0, 0, 8, 0)));
            pill.Children.Add(new TextBlock { Text = node.HealthText, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap });
            var chip = new Border { Child = pill, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 12, 4), Margin = new Thickness(0, 0, 0, 6) };
            chip.SetResourceReference(StyleProperty, "Chip");
            DetailContent.Children.Add(chip);
        }

        if (node.Roles.Count > 0)
        {
            DetailContent.Children.Add(DetailHeading(node.Kind == NodeKind.Add ? "What a host does" : "What it runs"));
            foreach (var role in node.Roles)
            {
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                item.Children.Add(new TextBlock { Text = role.Name, FontWeight = FontWeights.SemiBold });
                var detail = new TextBlock { Text = role.Detail };
                detail.SetResourceReference(StyleProperty, "Muted");
                item.Children.Add(detail);
                DetailContent.Children.Add(item);
            }
        }

        if (node.Facts.Count > 0)
        {
            DetailContent.Children.Add(DetailHeading(node.Kind == NodeKind.ThisPc ? "Hardware" : "Details"));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < node.Facts.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = node.Facts[i].Label, Margin = new Thickness(0, 0, 8, 6) };
                label.SetResourceReference(StyleProperty, "Muted");
                var value = new TextBlock { Text = node.Facts[i].Value, Margin = new Thickness(0, 0, 0, 6) };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }
            DetailContent.Children.Add(grid);
        }

        if (node.Notes.Count > 0)
        {
            DetailContent.Children.Add(DetailHeading("Good to know"));
            foreach (var note in node.Notes.Distinct())
            {
                var text = new TextBlock { Text = "\u2022 " + note, Margin = new Thickness(0, 0, 0, 4) };
                text.SetResourceReference(StyleProperty, "Muted");
                DetailContent.Children.Add(text);
            }
        }

        if (node.PairedHostId is { } pairedId && node.Kind == NodeKind.Host && FindHost(pairedId) is { } paired)
            RenderReachEditor(paired);

        if (node.Commands.Count > 0)
        {
            DetailContent.Children.Add(DetailHeading("Configure"));
            foreach (var command in node.Commands)
            {
                var button = new Button { Content = command.Label, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
                if (command.Primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
                AutomationProperties.SetAutomationId(button, $"NodeAction-{command.Action}");
                var action = command.Action;
                var argument = command.Argument;
                button.Click += (_, _) => RunNodeAction(action, argument);
                DetailContent.Children.Add(button);
            }
        }
    }

    /// <summary>How this desktop reaches a host to install or remove its roles: SSH (Docker or native Ubuntu), this PC's
    /// Docker Desktop, or by hand on the host.</summary>
    private void RenderReachEditor(PairedHost host)
    {
        DetailContent.Children.Add(DetailHeading("How Martlet reaches it"));
        var method = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(method, "How Martlet reaches this host");
        AutomationProperties.SetAutomationId(method, "HostReachMethod");
        foreach (var (value, text) in new[]
        {
            (HostSetupMethod.SshDocker, "SSH, with Docker there"), (HostSetupMethod.SshNative, "SSH, native Ubuntu"),
            (HostSetupMethod.ThisPcDocker, "This PC, with Docker Desktop"), (HostSetupMethod.OnHost, "I run its commands on it myself")
        })
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            method.Items.Add(item);
            if (value == host.Method) method.SelectedItem = item;
        }
        var label = new TextBlock { Text = "SSH target, for example me@192.168.1.20", Margin = new Thickness(0, 0, 0, 4) };
        label.SetResourceReference(StyleProperty, "Muted");
        var ssh = new TextBox { Text = host.SshTarget ?? "", Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(ssh, "SSH target (user@computer)");
        AutomationProperties.SetAutomationId(ssh, "HostReachSsh");
        void Toggle()
        {
            var usesSsh = method.SelectedItem is ComboBoxItem { Tag: HostSetupMethod.SshDocker or HostSetupMethod.SshNative };
            ssh.IsEnabled = label.IsEnabled = usesSsh;
            if (usesSsh && ssh.Text.Length == 0) ssh.Text = host.Address;
        }
        method.SelectionChanged += (_, _) => Toggle();
        Toggle();
        var save = new Button { Content = "Save how to reach it", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(save, "HostReachSave");
        save.Click += async (_, _) =>
        {
            if (method.SelectedItem is not ComboBoxItem { Tag: HostSetupMethod chosen }) return;
            await HostTaskAsync(async token =>
            {
                var updated = await Pairings().SetReachAsync(host.HostId, chosen, ssh.Text, Version, token);
                homeHosts = HostRegistry.Upsert(homeHosts, updated);
                ActionText.Text = $"Saved. Martlet reaches {updated.HostId} via: {updated.Reach}.";
            });
        };
        DetailContent.Children.Add(method);
        DetailContent.Children.Add(label);
        DetailContent.Children.Add(ssh);
        DetailContent.Children.Add(save);
    }

    // ---------- who does what ----------

    private HostPairings Pairings() => new(store!.DataDirectory, new AvatarProfileStore(store.DataDirectory), setupService!);

    private PairedHost? FindHost(string? hostId) => hostId is null ? null
        : NetworkMap.Hosts(Inputs()).FirstOrDefault(h => h.HostId == hostId);

    private void RenderRolesBoard(IReadOnlyList<NetworkNode> nodes)
    {
        var companion = Role == DeviceRole.Companion;
        RolesCard.Visibility = companion ? Visibility.Visible : Visibility.Collapsed;
        if (!companion) return;
        renderingBoard = true;
        try
        {
            RolesBoard.Children.Clear();
            foreach (var role in new[] { SetupRole.Llm, SetupRole.Stt, SetupRole.Tts })
            {
                var name = NetworkMap.RoleName(role);
                var owner = nodes.FirstOrDefault(n => n.Kind != NodeKind.Missing && n.Roles.Any(r => r.Name == name));
                var change = new Button { Content = owner is null ? "Choose in Setup" : "Change in Setup", HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetAutomationId(change, "RoleChange-" + role);
                change.Click += (_, _) => RunNodeAction(NodeAction.Setup);
                RolesBoard.Children.Add(RoleTile(name, owner?.Title ?? "Not chosen yet",
                    owner?.Roles.First(r => r.Name == name).Detail ?? "Pick a cloud model or one of your computers in Setup.", change, owner?.Id));
            }

            var hosts = NetworkMap.Hosts(Inputs());
            var handler = NetworkMap.LipSync(homeAvatar);
            var current = handler switch
            {
                LipSyncHandler.Loudness => "off",
                LipSyncHandler.Host => "host:" + homeAvatar!.RemoteHost!.HostId,
                _ => "this-pc"
            };
            var choice = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice, "Who handles lip-sync");
            AutomationProperties.SetAutomationId(choice, "LipSyncOwner");
            void Option(string key, string text)
            {
                var item = new ComboBoxItem { Content = text, Tag = key };
                choice.Items.Add(item);
                if (key == current) choice.SelectedItem = item;
            }
            Option("this-pc", "This PC");
            foreach (var host in hosts)
            {
                var check = hostChecks.GetValueOrDefault(host.HostId);
                Option("host:" + host.HostId, host.HostId + (check?.Offers?.ContainsKey(HostRoles.Audio2Face) == true ? " (runs Audio2Face)"
                    : check?.Reachable == true ? " (Audio2Face not installed)" : check?.Reachable == false ? " (not reachable)" : ""));
            }
            Option("off", "Nobody (mouth follows voice loudness)");
            choice.SelectionChanged += (_, _) =>
            {
                if (!renderingBoard && choice.SelectedItem is ComboBoxItem { Tag: string key } && key != current) _ = AssignLipSyncAsync(key);
            };
            var (who, detail, nodeId) = handler switch
            {
                LipSyncHandler.Loudness => ("Nobody", "The mouth follows the voice's loudness on this PC.", (string?)"this-pc"),
                LipSyncHandler.Host => (homeAvatar!.RemoteHost!.HostId,
                    hostChecks.GetValueOrDefault(homeAvatar.RemoteHost.HostId)?.Text ?? "Audio2Face over pinned TLS; voice loudness if it is unavailable.",
                    nodes.FirstOrDefault(n => n.PairedHostId == homeAvatar.RemoteHost.HostId)?.Id),
                _ => ("This PC", hosts.Count == 0
                    ? "Its own Audio2Face service when running, otherwise voice loudness. Add a computer to hand lip-sync to a GPU PC."
                    : "Its own Audio2Face service when running, otherwise voice loudness.", "this-pc")
            };
            RolesBoard.Children.Add(RoleTile("Lip-sync (Audio2Face)", who, detail, choice, nodeId));
        }
        finally { renderingBoard = false; }
    }

    private Border RoleTile(string title, string owner, string detail, FrameworkElement control, string? nodeId)
    {
        var tile = new Border { Width = 236, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 12, 12) };
        tile.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
        var stack = new StackPanel();
        var heading = new TextBlock { Text = title, FontSize = 12 };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.Children.Add(heading);
        if (nodeId is not null)
        {
            var link = new Button { Content = owner, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 15, FontWeight = FontWeights.SemiBold };
            link.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(link, $"{title}: {owner}. Show on the map");
            link.Click += (_, _) => SelectNode(nodeId, animate: true);
            stack.Children.Add(link);
        }
        else stack.Children.Add(new TextBlock { Text = owner, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var text = new TextBlock { Text = detail, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 8), ToolTip = detail };
        text.SetResourceReference(StyleProperty, "Muted");
        stack.Children.Add(text);
        stack.Children.Add(control);
        tile.Child = stack;
        AutomationProperties.SetName(tile, $"{title}: handled by {owner}. {detail}");
        return tile;
    }

    /// <summary>Hands lip-sync to a paired host ("host:ID"), this PC ("this-pc") or nobody ("off"). A host that does not run
    /// Audio2Face yet can install it in the same step; the showing character switches without restarting.</summary>
    private async Task AssignLipSyncAsync(string key)
    {
        if (store is null || setupService is null || closing) return;
        if (assigningRole) { ActionText.Text = "Another role change is still finishing."; RenderMap(); return; }
        assigningRole = true;
        try
        {
            PairedHost? host = null;
            var install = false;
            if (key.StartsWith("host:", StringComparison.Ordinal))
            {
                host = FindHost(key[5..]) ?? throw new InvalidOperationException("That host is no longer paired.");
                ActionText.Text = $"Checking {host.HostId}...";
                var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
                hostChecks[host.HostId] = check;
                if (check.Reachable != true)
                {
                    ActionText.Text = $"Lip-sync stays where it is: {host.HostId} did not answer ({check.Text})";
                    return;
                }
                if (check.Offers?.ContainsKey(HostRoles.Audio2Face) != true)
                {
                    var role = HostRoles.Get(HostRoles.Audio2Face);
                    if (!ConfirmationDialog.Confirm(this,
                            $"{host.HostId} does not run Audio2Face yet. Hand lip-sync to it and install Audio2Face there now? " +
                            (host.CanLaunch ? $"A console opens ({host.Reach}) where you confirm each step. " : "Martlet copies the command to run on it. ") +
                            $"It needs {role.Needs}. Until it is ready, the mouth follows the voice's loudness; then it switches over by itself.",
                            "Install and hand over"))
                        return;
                    install = true;
                }
            }
            var (before, after) = await Pairings().AssignLipSyncAsync(host, key == "off", lifetime.Token);
            homeAvatar = after;
            if (avatar.IsShowing)
            {
                if (before.LipSync != after.LipSync)
                {
                    if (await StopAvatarSafelyAsync()) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
                }
                else await avatar.UseHostAsync(after.RemoteHost, lifetime.Token);
            }
            var who = key == "off" ? "nobody (the mouth follows the voice's loudness)" : host?.HostId ?? "this PC";
            var message = $"Lip-sync is now handled by {who}.";
            if (install) LaunchOnHost(host!, HostRoles.Get(HostRoles.Audio2Face).Add);
            ActionText.Text = install ? message + " " + ActionText.Text : avatar.IsShowing ? message + " " + avatar.Status : message;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            assigningRole = false;
            if (!closing)
            {
                UpdateCharacterButton();
                RenderHome();
                if (DevicesPage.IsVisible) RenderMap();
            }
        }
    }

    private void CheckHosts_Click(object sender, RoutedEventArgs e) => _ = CheckHostsAsync(NetworkMap.Hosts(Inputs()));

    private async Task CheckHostsAsync(IReadOnlyList<PairedHost> hosts)
    {
        if (hosts.Count == 0)
        {
            ActionText.Text = "No Martlet host is paired yet. Add a computer first.";
            return;
        }
        foreach (var host in hosts) hostChecks[host.HostId] = new(null, "Checking...");
        if (DevicesPage.IsVisible) RenderMap();
        (string Id, HostCheck Check)[] results;
        try { results = await Task.WhenAll(hosts.Select(async h => (h.HostId, await HostControl.CheckAsync(h.Pairing, HardwareStore, lifetime.Token)))); }
        catch (OperationCanceledException) { return; }
        foreach (var (id, check) in results) hostChecks[id] = check;
        if (closing) return;
        ActionText.Text = results.Length == 1 ? $"{results[0].Id}: {results[0].Check.Text}"
            : $"Checked {results.Length} hosts: {results.Count(r => r.Check.Reachable == true)} reachable, " +
              $"{results.Count(r => r.Check.Offers?.ContainsKey(HostRoles.Audio2Face) == true)} running Audio2Face.";
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }

    /// <summary>Runs a martlet-host command on a paired host the way this PC reaches it (SSH or this PC's Docker Desktop);
    /// the host owner confirms each change in that console. Without a known route it copies the command instead.</summary>
    private void LaunchOnHost(PairedHost host, HostAction action)
    {
        try
        {
            if (!host.CanLaunch)
            {
                var command = HostSetupCommands.Preview(host.Target(Version) with { Method = HostSetupMethod.OnHost }, action);
                try { Clipboard.SetText(command); }
                catch (System.Runtime.InteropServices.ExternalException) { }
                ActionText.Text = $"Martlet does not know how to reach {host.HostId} yet, so the command to run on it was copied. " +
                    "Or choose how Martlet reaches it in its details on the Devices map.";
                return;
            }
            HostSetupCommands.Launch(host.Target(Version), action);
            ActionText.Text = action switch
            {
                HostAction.Status => $"{host.HostId}'s status opened in a console window ({host.Reach}).",
                HostAction.AddAudio2Face => $"Installing Audio2Face on {host.HostId} in a console window ({host.Reach}). Confirm each step there; " +
                    "this PC picks the role up by itself once it is running.",
                HostAction.RemoveAudio2Face => $"Removing Audio2Face from {host.HostId} in a console window ({host.Reach}). Confirm there.",
                HostAction.Update => $"Updating {host.HostId} to Martlet {Version} in a console window ({host.Reach}). Its pairings and roles stay; " +
                    "press Check connection afterwards.",
                _ => $"Opened on {host.HostId} in a console window ({host.Reach})."
            };
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            ActionText.Text = error.Message;
        }
    }

    private void RunHostRole(string? argument, bool add)
    {
        var parts = argument?.Split('/') ?? [];
        if (parts.Length != 2 || FindHost(parts[0]) is not { } host) return;
        var role = HostRoles.Get(parts[1]);
        if (!add && hostChecks.GetValueOrDefault(host.HostId)?.Offers?.ContainsKey(role.Kind) == true &&
            homeAvatar?.RemoteHost?.HostId == host.HostId && role.Kind == HostRoles.Audio2Face &&
            !ConfirmationDialog.Confirm(this, $"{host.HostId} handles lip-sync right now. Remove Audio2Face from it anyway? " +
                "The mouth follows the voice's loudness until you hand lip-sync to another computer.", "Remove role"))
            return;
        LaunchOnHost(host, add ? role.Add : role.Remove);
    }

    private async Task ForgetHostAsync(string? hostId)
    {
        if (FindHost(hostId) is not { } host) return;
        var inCharge = homeAvatar?.RemoteHost?.HostId == host.HostId;
        if (!ConfirmationDialog.Confirm(this,
                $"Forget {host.HostId} on this PC? Martlet stops using it{(inCharge ? " and lip-sync goes back to this PC" : "")}, and this PC's " +
                $"pairing secret is deleted. To remove this PC from the host too, revoke {host.Pairing.DeviceId} in its pairing console.",
                "Forget host"))
            return;
        await HostTaskAsync(async token =>
        {
            await Pairings().ForgetAsync(host.HostId, token);
            hostChecks.Remove(host.HostId);
            if (inCharge && avatar.IsShowing) await avatar.UseHostAsync(null, token);
            ActionText.Text = $"Forgot {host.HostId}. Revoke {host.Pairing.DeviceId} in its pairing console to finish.";
        });
        await RefreshHomeAsync();
    }

    private async Task HostTaskAsync(Func<CancellationToken, Task> action)
    {
        if (store is null || setupService is null || closing) return;
        if (assigningRole) { ActionText.Text = "Another role change is still finishing."; return; }
        assigningRole = true;
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            assigningRole = false;
            if (!closing && DevicesPage.IsVisible) RenderMap();
        }
    }

    private static TextBlock DetailHeading(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8)
    };

    private void RunNodeAction(NodeAction action, string? argument = null)
    {
        var args = new RoutedEventArgs();
        switch (action)
        {
            case NodeAction.Setup: Setup_Click(this, args); break;
            case NodeAction.AudioSetup: AudioSetup_Click(this, args); break;
            case NodeAction.Character: Avatar_Click(this, args); break;
            case NodeAction.ToggleCharacter: Character_Click(this, args); break;
            case NodeAction.Prerequisites: Prerequisites_Click(this, args); break;
            case NodeAction.HostThisPc: OpenHosts(HostSetupMethod.ThisPcDocker, 1); break;
            case NodeAction.AddComputer: OpenHosts(null, 0); break;
            case NodeAction.ManageHost: OpenHosts(null, 2, FindHost(argument)); break;
            case NodeAction.CheckHost:
                var hosts = NetworkMap.Hosts(Inputs());
                _ = CheckHostsAsync(argument is null ? hosts : hosts.Where(h => h.HostId == argument).ToArray());
                break;
            case NodeAction.HostDashboard: Navigate(NavHome); break;
            case NodeAction.Advisor: Advisor_Click(this, args); break;
            case NodeAction.UseForLipSync: _ = AssignLipSyncAsync("host:" + argument); break;
            case NodeAction.LipSyncThisPc: _ = AssignLipSyncAsync("this-pc"); break;
            case NodeAction.InstallRole: RunHostRole(argument, add: true); break;
            case NodeAction.RemoveRole: RunHostRole(argument, add: false); break;
            case NodeAction.HostStatus: if (FindHost(argument) is { } host) LaunchOnHost(host, HostAction.Status); break;
            case NodeAction.UpdateHost:
                if (FindHost(argument) is { } outdated)
                {
                    hostUpdateNotes.Remove(outdated.HostId);
                    LaunchOnHost(outdated, HostAction.Update);
                }
                break;
            case NodeAction.ForgetHost: _ = ForgetHostAsync(argument); break;
        }
    }

    private void OpenHosts(HostSetupMethod? method, int step, PairedHost? manage = null)
    {
        if (store is null || setupService is null || closing) return;
        new HostsWindow(new AvatarProfileStore(store.DataDirectory), setupService, method, step, manage) { Owner = this }.ShowDialog();
        _ = RefreshHomeAsync();
    }

    // ---------- small visuals ----------

    private static TextBlock Glyph(string glyph, double size, Thickness margin)
    {
        var text = new TextBlock { Text = glyph, FontSize = size, Margin = margin };
        text.SetResourceReference(StyleProperty, "Icon");
        return text;
    }

    private static Ellipse Dot(NodeHealth health, double size, Thickness margin)
    {
        var dot = new Ellipse { Width = size, Height = size, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, health switch
        {
            NodeHealth.Ready => "SuccessBrush",
            NodeHealth.Attention => "WarningBrush",
            _ => "MutedBrush"
        });
        return dot;
    }
}
