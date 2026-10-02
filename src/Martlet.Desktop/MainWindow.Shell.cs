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
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The app shell: navigation, the welcome tour, the stage-aware home, the host dashboard and the devices map.</summary>
public partial class MainWindow
{
    private sealed record HomeStep(string Id, string Title, string Detail, bool Done, bool Optional, IReadOnlyList<StepCommand> Commands);
    private sealed record StepCommand(string Label, Action Run, bool Primary = false);
    private sealed record MapElement(NetworkNode Node, Button Card, System.Windows.Shapes.Path? Track, System.Windows.Shapes.Path? Flow, Ellipse? Ring);

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
    private WindowsVirtualization? virtualization;
    private bool refreshingHome;
    private bool hostBusy;
    private readonly AudioDevicePresence audioPresence = new();
    private DependencyPropertyDescriptor? actionTextDescriptor;

    private DeviceRole Role => deviceRole ?? DeviceRole.Companion;

    /// <summary>Microphones and speakers are assumed to work unless the last check found the chosen one missing.</summary>
    private bool AudioMissing(bool output) => AudioDeviceList.Present(output ? homeSettings?.Audio?.Output : homeSettings?.Audio?.Input,
        output ? audioPresence.Last?.Outputs : audioPresence.Last?.Inputs) == false;

    private void InitializeShell()
    {
        actionTextDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        actionTextDescriptor.AddValueChanged(ActionText, ActionTextChanged);
        deviceRole = store is null ? DeviceRole.Companion : DeviceRolePreference.Load(store.DataDirectory);
        InitializeHealth();
        ApplyRole();
        RenderHome();
        if (deviceRole is null) ShowTour(TourWelcome);
    }

    private void ReleaseShell()
    {
        actionTextDescriptor?.RemoveValueChanged(ActionText, ActionTextChanged);
        ReleaseHealth();
    }

    private void StartAmbientMotion()
    {
        Motion.Float(HeroFloat);
        Motion.Sway(HeroMascot);
        Motion.Breathe(HeroGlow);
        Motion.Twinkle(Sparkle1, 1.6);
        Motion.Twinkle(Sparkle2, 2.1, 0.5);
        Motion.Twinkle(Sparkle3, 1.8, 1.0);
        Motion.Float(TourFloat);
        Motion.Sway(TourMascot);
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
        // The host dashboard says why Docker Desktop can't start when Windows is the reason (virtualization, WSL 2).
        if (Role == DeviceRole.Host && machine.DockerInstalled && !machine.DockerRunning)
        {
            try { virtualization = await WindowsVirtualization.ProbeAsync(lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (closing) return;
        }
        else virtualization = null;
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }

    // ---------- status line ----------

    private void ActionTextChanged(object? sender, EventArgs e)
    {
        var visible = !string.IsNullOrWhiteSpace(ActionText.Text);
        if (!visible) { StatusBar.Visibility = Visibility.Collapsed; return; }
        LogStatusLine(ActionText.Text);
        StatusBar.Visibility = Visibility.Visible;
        Motion.Enter(StatusBar, dy: 10, milliseconds: 200);
    }

    private void DismissStatus_Click(object sender, RoutedEventArgs e) => ActionText.Text = "";

    // ---------- navigation ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage is null || DevicesPage is null || CompanionPage is null || DiagnosticsPage is null || SettingsPage is null) return;
        if (sender is RadioButton { IsChecked: false }) return;
        FrameworkElement page = ReferenceEquals(sender, NavDevices) ? DevicesPage
            : ReferenceEquals(sender, NavCompanion) ? CompanionPage
            : ReferenceEquals(sender, NavDiagnostics) ? DiagnosticsPage
            : ReferenceEquals(sender, NavSettings) ? SettingsPage
            : HomePage;
        openTab = null;
        foreach (var candidate in new FrameworkElement[] { HomePage, DevicesPage, CompanionPage, DiagnosticsPage, SettingsPage })
            candidate.Visibility = ReferenceEquals(candidate, page) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(page, CompanionPage)) ShowCompanionTab(entering: true);
        else Motion.Enter(page);
        if (ReferenceEquals(page, DevicesPage)) RenderMap();
        if (ReferenceEquals(page, DiagnosticsPage)) EnterDiagnostics();
        else LeaveDiagnostics();
    }

    private void Navigate(RadioButton item)
    {
        if (item.IsChecked == true) Nav_Checked(item, new RoutedEventArgs());
        else item.IsChecked = true;
    }

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
    private void ReplayTour_Click(object sender, RoutedEventArgs e) => ShowTour(TourWelcome);

    // A host has no "how to start" step. The tour installs nothing: each setup installs what it needs.
    private StackPanel[] TourPanels => Role == DeviceRole.Host ? [TourWelcome, TourRole] : [TourWelcome, TourRole, TourStart];

    private void ShowTour(StackPanel panel)
    {
        var appearing = Tour.Visibility != Visibility.Visible;
        Tour.BeginAnimation(OpacityProperty, null);
        Tour.Opacity = 1;
        Tour.Visibility = Visibility.Visible;
        var panels = TourPanels;
        var step = Math.Max(0, Array.IndexOf(panels, panel));
        tourStep = step;
        foreach (var candidate in new[] { TourWelcome, TourRole, TourStart })
            candidate.Visibility = ReferenceEquals(candidate, panel) ? Visibility.Visible : Visibility.Collapsed;
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
        Motion.Enter(panel, dx: appearing ? 0 : 36, dy: 0, milliseconds: 320);
        Motion.Cascade(panel.Children.OfType<Button>(), 70);
    }

    private void HideTour() => Motion.FadeOut(Tour, () =>
    {
        Tour.Visibility = Visibility.Collapsed;
        Tour.Opacity = 1;
    });

    private void TourBegin_Click(object sender, RoutedEventArgs e) => ShowTour(TourRole);
    private void TourBack_Click(object sender, RoutedEventArgs e) => ShowTour(TourPanels[Math.Max(0, tourStep - 1)]);

    private void TourCompanion_Click(object sender, RoutedEventArgs e)
    {
        SetRole(DeviceRole.Companion);
        ShowTour(TourStart);
    }

    private void TourHost_Click(object sender, RoutedEventArgs e)
    {
        SetRole(DeviceRole.Host);
        HideTour();
        Navigate(NavHome);
    }

    private void TourAdvisor_Click(object sender, RoutedEventArgs e) { HideTour(); Advisor_Click(sender, e); }
    private void TourSetup_Click(object sender, RoutedEventArgs e) { HideTour(); OpenCompanion(CompanionTab.Thinking); }


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
            homeSettings = await LeaveRetiredSampleAsync(loaded, lifetime.Token) ?? loaded.Settings;
            homeSettingsState = loaded.State;
            homeSettingsProblem = loaded.Error?.Summary;
            homeAvatar = loaded.Settings is { } settings
                ? (await new AvatarProfileStore(store.DataDirectory).LoadAsync(settings.Profile.Id, lifetime.Token)).Profile
                : null;
            await audioPresence.CheckAsync(lifetime.Token);
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
        ObserveLocalJobs();
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
        CheckOwnLipSyncAsync().Forget();
        CheckLocalServicesAsync().Forget();
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
        EvaluateCoverage();
        RenderHealth(force: true);
        RenderHost();
        if (openTab is not null && !tabEdited) RenderTab();
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
            AutomationProperties.SetAutomationId(detail, $"StepDetail-{step.Id}");
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

    private void PrimaryStage_Click(object sender, RoutedEventArgs e) => (stageFix ?? (() => OpenCompanion(CompanionTab.Thinking)))();

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
        var windowsBlocks = !machine.DockerRunning && machine.DockerInstalled && virtualization is { } windows &&
            (windows.FirmwareOff || windows.NeedsChanges);
        var steps = new List<HomeStep>
        {
            new("docker", "Docker Desktop",
                machine.DockerRunning ? "Running."
                    : windowsBlocks ? $"Can't start yet: {string.Join(", ", virtualization!.Problems())}. " + (virtualization.FirmwareOff
                        ? "Turn it on in the firmware settings; Martlet can restart into them and continue afterwards."
                        : "Martlet turns these on (one administrator prompt; usually a restart, then it continues by itself).")
                    : machine.DockerInstalled ? "Installed, but not running." : "Runs the host service in containers. Free for personal use.",
                machine.DockerRunning, false,
                machine.DockerRunning ? []
                    : windowsBlocks ? [new(virtualization!.FirmwareOff ? "Turn on virtualization" : "Turn on Windows features", PrepareWindows, true)]
                    : machine.DockerInstalled
                    ? [new("Start Docker Desktop", StartDocker, true)]
                    : [new("Install Docker Desktop", InstallDocker, true)]),
            new("service", "Host service",
                hostServiceReachable == true ? "Set up and reachable on your network."
                    : "Sets up the gateway once. Windows asks once to allow TCP 9443 from your private network.",
                hostServiceReachable == true, false, [new("Set up host service", () => SetUpHostServiceAsync().Forget(), true)]),
            new("pair", "Pair your main PC",
                "Martlet shows a one-use code here (and copies it). On your main PC, go to Devices > Add a computer > Pair and paste it.",
                false, false, [new("Show a pairing code", () => LaunchHost(HostAction.Pair), true)]),
            new("roles", "Add roles",
                string.Join(" ", HostRoles.All.Select(r => $"{r.Name} needs {r.Needs}.")) + " " + nvidia,
                false, true, [.. HostRoles.All.SelectMany(r => new[]
                {
                    new StepCommand($"Add {r.Name}", () => LaunchHost(r.Add), r == HostRoles.All[0]),
                    new StepCommand($"Remove {r.Name}", () => LaunchHost(r.Remove))
                })]),
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

    /// <summary>Runs a host-dashboard step on this PC's host service in a run window (never a console). Pairing shows the
    /// one-use code for the main PC.</summary>
    private void LaunchHost(HostAction action) => LaunchHostAsync(action).Forget();

    private async Task LaunchHostAsync(HostAction action)
    {
        if (closing || store is null) return;
        if (hostBusy) { ActionText.Text = "Another host service step is still running."; return; }
        hostBusy = true;
        try
        {
            ActionText.Text = "Running on this PC's host service; its progress shows in a separate window.";
            var done = action.Verb == HostVerb.Pair
                ? await HostActions.PairOtherDesktopAsync(this, ThisPcTarget())
                : await HostActions.RunAsync(this, store.DataDirectory, ThisPcTarget(), null, action);
            if (closing) return;
            ActionText.Text = done ?? "Stopped. The run window shows why.";
            if (done is not null && action.Verb is HostVerb.Setup or HostVerb.Update)
                thisPcHostVersion = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            hostBusy = false;
            if (!closing) RenderHost();
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
        await LaunchHostAsync(HostAction.Setup);
        if (firewall is not null) ActionText.Text = firewall + " " + ActionText.Text;
    }

    private async void InstallDocker()
    {
        if ((await HostsWindow.InstallDockerDesktopAsync(this)).Status is { } status && !closing) ActionText.Text = status;
        if (!closing) await ReadMachineAsync();
    }

    /// <summary>Turns on what Docker Desktop needs from Windows (virtualization features and WSL) in a run window; a restart,
    /// when needed, continues by starting Docker Desktop after the next sign-in.</summary>
    private async void PrepareWindows()
    {
        if (hostBusy || closing) return;
        hostBusy = true;
        try
        {
            var done = await HostRunWindow.RunAsync(this, "Get Windows ready for Docker Desktop", async run =>
            {
                await WindowsVirtualizationSetup.EnsureReadyAsync(run, ContinueSetupKind.Docker);
                return "Windows is ready for Docker Desktop. Start it next.";
            });
            if (!closing) ActionText.Text = done ?? "Windows isn't ready for Docker Desktop yet. The run window shows why.";
        }
        finally { hostBusy = false; }
        if (!closing) await ReadMachineAsync();
    }

    /// <summary>After Windows restarted to finish turning on virtualization, continues the setup that asked for it
    /// (<see cref="HostSetupResume"/>): this PC's host service and its pairing, the host dashboard's host service, or
    /// starting Docker Desktop so the owner can repeat the step that needed it.</summary>
    private async Task ContinueSetupAsync()
    {
        if (closing || store is null || HostSetupResume.Take() is not { } note) return;
        ErrorLog.Info($"Continuing after a Windows restart: {note.Kind} ({note.Task})");
        ActionText.Text = $"Windows restarted. Continuing: {note.Task}...";
        switch (note.Kind)
        {
            case ContinueSetupKind.HostService when Role == DeviceRole.Host:
                await SetUpHostServiceAsync();
                break;
            case ContinueSetupKind.ThisPc when ThisPcHost() is null:
                await SetUpThisPcHostAsync();
                break;
            default:
                await StartDockerAfterRestartAsync(note.Task);
                break;
        }
    }

    private async Task StartDockerAfterRestartAsync(string task)
    {
        if (hostBusy || closing) return;
        hostBusy = true;
        try
        {
            var done = await HostRunWindow.RunAsync(this, "Start Docker Desktop", async run =>
            {
                await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
                return $"Docker Desktop is running. Continue where you left off: {task}.";
            });
            if (!closing) ActionText.Text = done ?? "Docker Desktop did not start. The run window shows why.";
        }
        finally { hostBusy = false; }
        if (!closing) await ReadMachineAsync();
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
        ReadMachineAsync().Forget();
        RefreshHomeAsync().Forget();
    }

    private void MapHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Narrow windows scale the whole map down rather than squeezing devices into each other.
        var available = MapHost.ActualWidth - 18;
        if (available <= 0) return;
        MapCanvas.Width = Math.Max(available, 760);
        MapCanvas.Height = Math.Max((MapHost.ActualHeight - 18) * MapCanvas.Width / available, 300);
        LayoutMap();
    }

    /// <summary>The map takes the top of the page, a little under half its height (taller when a side has many devices);
    /// the selected device's details follow.</summary>
    private void DevicesPage_SizeChanged(object sender, SizeChangedEventArgs e) => SizeMap();

    private int mapRows = 1;

    private void SizeMap() =>
        MapHost.Height = Math.Max(Math.Clamp(DevicesPage.ActualHeight * 0.42, 300, 400), mapRows * 104 + 40);

    private void RenderMap()
    {
        var nodes = NetworkMap.Build(Inputs());
        RenderDeviceSettings(nodes);
        if (nodes.All(n => n.Id != selectedNode)) selectedNode = "this-pc";
        MapCanvas.Children.Clear();
        mapElements.Clear();
        mapGlow = new Ellipse { Width = 210, Height = 210, Opacity = 0.55, IsHitTestVisible = false };
        mapGlow.SetResourceReference(Shape.FillProperty, "GlowBrush");
        MapCanvas.Children.Add(mapGlow);
        foreach (var node in nodes.Skip(1))
        {
            var track = new System.Windows.Shapes.Path { StrokeThickness = 2, Opacity = 0.35, IsHitTestVisible = false };
            track.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
            var flow = new System.Windows.Shapes.Path
            {
                StrokeThickness = 2.5, StrokeDashArray = [2, 4], StrokeDashCap = PenLineCap.Round, IsHitTestVisible = false,
                Opacity = node.Kind is NodeKind.Add or NodeKind.Missing ? 0.4 : 0.9
            };
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
        var (left, right) = MapSides();
        mapRows = Math.Max(1, Math.Max(left.Count, right.Count));
        SizeMap();
        LayoutMap();
        foreach (var element in mapElements)
        {
            if (element.Flow is { } flow && element.Node.Kind is not (NodeKind.Add or NodeKind.Missing)) Motion.Flow(flow);
            if (element.Ring is { } ring && element.Node.Health == NodeHealth.Ready) Motion.PulseRing(ring, delay: index++ * 0.3 % 2);
        }
        Motion.Breathe(mapGlow);
        SelectNode(selectedNode, animate: false);
    }

    /// <summary>Cloud services (and a job nobody does yet) sit left of This PC and your computers right; Add a computer
    /// joins the side with fewer devices.</summary>
    private (List<MapElement> Left, List<MapElement> Right) MapSides()
    {
        var left = mapElements.Where(m => m.Node.Kind is NodeKind.Cloud or NodeKind.Missing).ToList();
        var right = mapElements.Where(m => m.Node.Kind == NodeKind.Host).ToList();
        (left.Count < right.Count ? left : right).AddRange(mapElements.Where(m => m.Node.Kind == NodeKind.Add));
        return (left, right);
    }

    private void LayoutMap()
    {
        if (mapElements.Count == 0 || mapGlow is null) return;
        var (left, right) = MapSides();
        double width = MapCanvas.Width, height = MapCanvas.Height, cy = height / 2;
        // This PC moves toward the empty side, so a map with devices on one side only stays balanced.
        var cx = left.Count == 0 ? width * 0.36 : right.Count == 0 ? width * 0.64 : width / 2;
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal) { ["this-pc"] = new(cx, cy) };
        void Place(List<MapElement> side, int direction)
        {
            var reach = Math.Min((direction < 0 ? cx : width - cx) - 112, 380);
            var spacing = side.Count <= 1 ? 0 : Math.Min(112, (height - 96) / (side.Count - 1));
            for (var i = 0; i < side.Count; i++)
            {
                // A gentle arc: the outer devices of a column lean in toward This PC.
                var t = side.Count == 1 ? 0 : 2.0 * i / (side.Count - 1) - 1;
                positions[side[i].Node.Id] = new(cx + direction * (reach - 40 * t * t), cy + t * spacing * (side.Count - 1) / 2);
            }
        }
        Place(left, -1);
        Place(right, 1);
        var hubWidth = mapElements.FirstOrDefault(m => m.Node.Kind == NodeKind.ThisPc)?.Card is { } hub ? hub.Width / 2 : 0;
        foreach (var element in mapElements)
        {
            if (!positions.TryGetValue(element.Node.Id, out var point)) continue;
            element.Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = element.Card.DesiredSize;
            Canvas.SetLeft(element.Card, point.X - size.Width / 2);
            Canvas.SetTop(element.Card, point.Y - size.Height / 2);
            if (element.Track is null && element.Flow is null) continue;
            // Edge to edge, so a connection never runs through a see-through card.
            var direction = Math.Sign(point.X - cx);
            var connector = Connector(new(cx + direction * hubWidth, cy), new(point.X - direction * size.Width / 2, point.Y));
            if (element.Track is { } track) track.Data = connector;
            if (element.Flow is { } flow) flow.Data = connector;
        }
        Canvas.SetLeft(mapGlow, cx - mapGlow.Width / 2);
        Canvas.SetTop(mapGlow, cy - mapGlow.Height / 2);
    }

    /// <summary>A soft S-curve from This PC to a device.</summary>
    private static PathGeometry Connector(Point from, Point to)
    {
        var middle = (from.X + to.X) / 2;
        var figure = new PathFigure { StartPoint = from };
        figure.Segments.Add(new BezierSegment(new Point(middle, from.Y), new Point(middle, to.Y), to, true));
        return new PathGeometry([figure]);
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
        var roles = node.Roles.Where(r => r.Component != DeviceComponent.App).Select(r => r.Chip).Distinct().ToList();
        foreach (var chip in roles.Take(3).Append(roles.Count > 3 ? $"+{roles.Count - 3}" : null).OfType<string>())
        {
            var border = new Border { Child = new TextBlock { Text = chip, FontSize = 11, TextWrapping = TextWrapping.NoWrap } };
            border.SetResourceReference(StyleProperty, "Chip");
            chips.Children.Add(border);
        }
        if (chips.Children.Count > 0) content.Children.Add(chips);

        var card = new Button { Content = content, Width = node.Kind == NodeKind.ThisPc ? 220 : 204 };
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
        if (!animate) return;
        Motion.Enter(DetailContent, dx: 0, dy: 12, milliseconds: 240);
        // When little of the details shows (a short window), scroll so their top part is in view.
        var top = DetailCard.TranslatePoint(new Point(0, 0), DevicesPage).Y;
        if (top > DevicesPage.ViewportHeight - 160)
            DevicesPage.ScrollToVerticalOffset(DevicesPage.VerticalOffset + top - Math.Max(0, DevicesPage.ViewportHeight - 280));
    }

    // ---------- who does what ----------

    private HostPairings Pairings() => new(store!.DataDirectory, new AvatarProfileStore(store.DataDirectory), setupService!);

    private PairedHost? FindHost(string? hostId) => hostId is null ? null
        : NetworkMap.Hosts(Inputs()).FirstOrDefault(h => h.HostId == hostId);

    /// <summary>Who handles lip-sync: this PC, a paired host (installing Audio2Face there if needed) or nobody.</summary>
    private ComboBox LipSyncChoice()
    {
        var current = NetworkMap.LipSync(homeAvatar) switch
        {
            LipSyncHandler.Loudness => "off",
            LipSyncHandler.Host => "host:" + homeAvatar!.RemoteHost!.HostId,
            _ => "this-pc"
        };
        var choice = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choice, "Who handles lip-sync");
        AutomationProperties.SetAutomationId(choice, "LipSyncOwner");
        void Option(string key, string text, string? blocked = null)
        {
            var item = new ComboBoxItem { Content = text, Tag = key };
            if (blocked is not null)
            {
                item.IsEnabled = false;
                item.ToolTip = blocked;
                ToolTipService.SetShowOnDisabled(item, true);
                AutomationProperties.SetHelpText(item, blocked);
            }
            choice.Items.Add(item);
            if (key == current) choice.SelectedItem = item;
        }
        Option("this-pc", ownLipSyncAnswers == false ? "This PC's own Audio2Face service (not running)" : "This PC's own Audio2Face service");
        var local = ThisPcHost()?.HostId;
        foreach (var host in NetworkMap.Hosts(Inputs()))
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var offers = check?.Offers?.ContainsKey(HostRoles.Audio2Face) == true;
            var name = host.HostId == local ? $"{host.HostId}, this PC's host service" : host.HostId;
            if (!offers && CannotHand(host.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is { } cannot)
            {
                Option("host:" + host.HostId, $"{name} (can't take it now)", cannot);
                continue;
            }
            Option("host:" + host.HostId, name + (offers ? " (runs Audio2Face)"
                : check?.Reachable == true ? " (Audio2Face not installed)" : check?.Reachable == false ? " (not reachable)" : ""));
        }
        Option("off", "Nobody (mouth follows voice loudness)");
        choice.SelectionChanged += (_, _) =>
        {
            if (!renderingBoard && choice.SelectedItem is ComboBoxItem { Tag: string key } && key != current) AssignLipSyncAsync(key).Forget();
        };
        return choice;
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
                    if (CannotHand(host.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is { } cannot)
                    {
                        ActionText.Text = $"Lip-sync stays where it is: {cannot}";
                        return;
                    }
                    var role = HostRoles.Get(HostRoles.Audio2Face);
                    if (!ConfirmationDialog.Confirm(this,
                            $"{host.HostId} does not run Audio2Face yet. Hand lip-sync to it and install Audio2Face there now? " +
                            (host.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative
                                ? $"Martlet installs it over SSH ({host.Reach}) and shows its progress. "
                                : host.CanLaunch ? "Martlet installs it in this PC's host service and shows its progress. " : "Martlet copies the command to run on it. ") +
                            $"It needs {role.Needs}. Until it is ready, the mouth follows the voice's loudness; then it switches over by itself.",
                            "Install and hand over"))
                        return;
                    install = true;
                }
            }
            await ApplyLipSyncAsync(host, key == "off");
            tabPlace.Remove(CompanionTab.LipSync);
            RecordClusterJob(ClusterJobs.LipSync, ClusterSync.Local(ClusterJobs.LipSync, homeSettings, homeAvatar));
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
                CheckOwnLipSyncAsync().Forget();
            }
        }
    }

    /// <summary>Saves who handles lip-sync and switches a showing character over without restarting it (unless the
    /// lip-sync mode itself changed).</summary>
    private async Task ApplyLipSyncAsync(PairedHost? host, bool off)
    {
        var (before, after) = await Pairings().AssignLipSyncAsync(host, off, lifetime.Token);
        homeAvatar = after;
        if (!avatar.IsShowing) return;
        if (before.LipSync != after.LipSync)
        {
            if (await StopAvatarSafelyAsync()) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        }
        else await avatar.UseHostAsync(after.RemoteHost, lifetime.Token);
    }

    private void CheckHosts_Click(object sender, RoutedEventArgs e) => CheckHostsAsync(NetworkMap.Hosts(Inputs())).Forget();

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
              string.Join(", ", HostRoles.All.Select(role =>
                  $"{results.Count(r => r.Check.Offers?.ContainsKey(role.Kind) == true)} running {role.Name}")) + ".";
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
        QueueClusterSync();
    }

    /// <summary>Runs a martlet-host command on a paired host the way this PC reaches it, in Martlet with its output and Cancel
    /// in a run window (over SSH, or on this PC's Docker Desktop); never in a console. Without a known route it copies the
    /// command instead.</summary>
    private void LaunchOnHost(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers = null) =>
        RunHostActionAsync(host, action, answers).Forget();

    /// <summary><see cref="LaunchOnHost"/>, awaitable: returns the run's summary, or null when it stopped or could not run.</summary>
    private async Task<string?> RunHostActionAsync(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers = null)
    {
        try
        {
            if (!host.CanLaunch || store is null)
            {
                var command = HostSetupCommands.Preview(host.Target(Version) with { Method = HostSetupMethod.OnHost }, action);
                try { Clipboard.SetText(command); }
                catch (System.Runtime.InteropServices.ExternalException) { }
                ActionText.Text = $"Martlet does not know how to reach {host.HostId} yet, so the command to run on it was copied. " +
                    "Or choose how Martlet reaches it in its details on the Devices map.";
                return null;
            }
            var local = host.Method == HostSetupMethod.ThisPcDocker;
            ActionText.Text = $"Running {HostSetupCommands.Engine(action)} on {(local ? "this PC's host service" : host.HostId + " over SSH")}; " +
                "its progress shows in a separate window.";
            // Adding a role on this PC preselects what suits it (for example whisper on the processor when the graphics card is full).
            var recommended = answers is null && local && action.Verb == HostVerb.Add && action.Role == HostRoles.Stt
                ? (await ListeningAdviceAsync()).Answers() : null;
            var done = await HostActions.RunAsync(this, store.DataDirectory, host.Target(Version), host.SshHostKey, action, answers, recommended);
            if (closing) return done;
            ActionText.Text = done is null ? $"{HostSetupCommands.Engine(action)} on {host.HostId} stopped; its window shows why." : $"{host.HostId}: {done}";
            if (done is not null && action != HostAction.Status) CheckHostsAsync([host]).Forget();
            if (local) gpuProbe = null;
            return done;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            ActionText.Text = error.Message;
            return null;
        }
    }

    private void RunHostRole(string? argument, bool add)
    {
        var parts = argument?.Split('/') ?? [];
        if (parts.Length != 2 || FindHost(parts[0]) is not { } host) return;
        var role = HostRoles.Get(parts[1]);
        if (add && CannotHand(host.HostId, role.Kind, role.Job) is { } cannot)
        {
            ActionText.Text = $"{role.Name} can't be installed on {host.HostId}: {cannot}";
            return;
        }
        var offered = hostChecks.GetValueOrDefault(host.HostId)?.Offers?.ContainsKey(role.Kind) == true;
        if (!add && (offered || hostChecks.GetValueOrDefault(host.HostId)?.Offers is null))
        {
            RemoveHostRoleAsync(host, role).Forget();
            return;
        }
        LaunchOnHost(host, add ? role.Add : role.Remove);
    }

    private async Task ForgetHostAsync(string? hostId)
    {
        if (FindHost(hostId) is not { } host) return;
        var impact = JobCoverageRules.ForgetImpact(JobSituations(), host.HostId);
        if (!ConfirmationDialog.Confirm(this,
                $"Forget {host.HostId} on this PC? Martlet stops using it and this PC's pairing secret is deleted." +
                (impact.Count > 0 ? $" It does jobs for this PC: {string.Join(" ", impact)}" : "") +
                $" To remove this PC from the host too, revoke {host.Pairing.DeviceId} in its pairing console.",
                "Forget host"))
            return;
        var inCharge = homeAvatar?.RemoteHost?.HostId == host.HostId;
        await HostTaskAsync(async token =>
        {
            // Jobs it did go back to the choice kept aside for them first, so no route is left pointing at a forgotten host.
            var stranded = new List<string>();
            foreach (var job in HostJob.All.Where(j => NetworkMap.JobHost(homeSettings, j.Role) == host.HostId))
            {
                if (store is not null && JobSavedRoute.Load(store.DataDirectory, job.SavedFile) is { } saved) await HandBackAsync(job, saved);
                else stranded.Add(job.Job);
            }
            await Pairings().ForgetAsync(host.HostId, token);
            hostChecks.Remove(host.HostId);
            ForgetClusterHost(host.HostId);
            if (inCharge)
            {
                RecordClusterJob(ClusterJobs.LipSync, new(null, false));
                if (avatar.IsShowing) await avatar.UseHostAsync(null, token);
            }
            ActionText.Text = $"Forgot {host.HostId}. Revoke {host.Pairing.DeviceId} in its pairing console to finish." +
                (stranded.Count > 0 ? $" Nobody does the {string.Join(" or ", stranded)} now; choose another in Companion or on the Devices page." : "");
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

    private void RunNodeAction(NodeAction action, string? argument = null)
    {
        var args = new RoutedEventArgs();
        switch (action)
        {
            case NodeAction.Companion: OpenCompanion(Enum.TryParse<SetupRole>(argument, out var jobRole) ? TabFor(jobRole)
                : Enum.TryParse<CompanionTab>(argument, out var tab) ? tab : CompanionTab.Thinking); break;
            case NodeAction.AudioSetup: AudioSetup_Click(this, args); break;
            case NodeAction.Character: Avatar_Click(this, args); break;
            case NodeAction.ToggleCharacter: Character_Click(this, args); break;
            case NodeAction.Prerequisites: Prerequisites_Click(this, args); break;
            case NodeAction.HostThisPc: SetUpThisPcHostAsync().Forget(); break;
            case NodeAction.AddComputer: OpenHosts(null, 0); break;
            case NodeAction.ManageHost: OpenHosts(null, 2, FindHost(argument)); break;
            case NodeAction.CheckHost:
                var hosts = NetworkMap.Hosts(Inputs());
                CheckHostsAsync(argument is null ? hosts : hosts.Where(h => h.HostId == argument).ToArray()).Forget();
                break;
            case NodeAction.HostDashboard: Navigate(NavHome); break;
            case NodeAction.Advisor: Advisor_Click(this, args); break;
            case NodeAction.UseForLipSync: AssignLipSyncAsync("host:" + argument).Forget(); break;
            case NodeAction.UseForThinking: AssignThinkingAsync("host:" + argument).Forget(); break;
            case NodeAction.UseForListening: AssignJobAsync(HostJob.Listening, "host:" + argument).Forget(); break;
            case NodeAction.UseForSpeaking: AssignJobAsync(HostJob.Speaking, "host:" + argument).Forget(); break;
            case NodeAction.LipSyncThisPc: AssignLipSyncAsync("this-pc").Forget(); break;
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
            case NodeAction.ForgetHost: ForgetHostAsync(argument).Forget(); break;
            case NodeAction.PrepareHost: if (FindHost(argument) is { } prepare) OpenPrepare(prepare, PrepareStart.Status); break;
            case NodeAction.RebootHost: if (FindHost(argument) is { } reboot) OpenPrepare(reboot, PrepareStart.Reboot); break;
            case NodeAction.ShutdownHost: if (FindHost(argument) is { } shutdown) OpenPrepare(shutdown, PrepareStart.Shutdown); break;
            case NodeAction.WakeHost: if (FindHost(argument) is { } wake) OpenPrepare(wake, PrepareStart.Wake); break;
            case NodeAction.PrepareComputer: OpenPrepare(null, PrepareStart.Status); break;
        }
    }

    private void OpenHosts(HostSetupMethod? method, int step, PairedHost? manage = null)
    {
        if (store is null || setupService is null || closing) return;
        new HostsWindow(new AvatarProfileStore(store.DataDirectory), setupService, method, step, manage) { Owner = this }.ShowDialog();
        RefreshHomeAsync().Forget();
    }

    /// <summary>Sets up and pairs Martlet's host service on this PC in one click, in a run window; with
    /// <paramref name="andThen"/> it then continues straight into handing a job to it ("host:ID"). Whisper and F5 chain
    /// their install into the same window instead (<see cref="SetUpJobHereAsync"/>).</summary>
    private async Task SetUpThisPcHostAsync(Func<string, Task>? andThen = null)
    {
        if (store is null || setupService is null || closing) return;
        if (hostBusy) { ActionText.Text = "This PC's host service is already being set up."; return; }
        hostBusy = true;
        PairedHost? host;
        try
        {
            string? status;
            (host, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(store.DataDirectory), setupService,
                text => ActionText.Text = text, lifetime.Token);
            if (status is not null && !closing) ActionText.Text = status;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ContractException or JsonException)
        {
            ActionText.Text = error.Message;
            return;
        }
        finally { hostBusy = false; }
        if (closing || host is null) return;
        await ReadMachineAsync();
        await RefreshHomeAsync();
        if (andThen is not null && FindHost(host.HostId) is not null) await andThen("host:" + host.HostId);
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
