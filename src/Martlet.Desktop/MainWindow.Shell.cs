using System.ComponentModel;
using System.IO;
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
    /// <summary>A step's button. <paramref name="Enabled"/> false greys it out while a run already does what it would do (its
    /// label then says so, such as "Starting Docker Desktop...").</summary>
    private sealed record StepCommand(string Label, Action Run, bool Primary = false, bool Enabled = true);
    private sealed record MapElement(NetworkNode Node, Button Card, System.Windows.Shapes.Path? Track, System.Windows.Shapes.Path? Flow, Ellipse? Ring);

    private static readonly string Version = AppVersions.Current;
    private DeviceRole? deviceRole;
    private MachineInfo machine = MachineInfo.Unknown;
    private AppSettings? homeSettings;
    private AvatarProfile? homeAvatar;
    private IReadOnlyList<PairedHost> homeHosts = [];
    /// <summary>Hosts friends share with this PC (<see cref="PairedHost.Shared"/>): their engines only, kept apart from
    /// <see cref="homeHosts"/> so nothing between your own computers ever talks to them.</summary>
    private IReadOnlyList<PairedHost> sharedHosts = [];
    private bool renderingBoard;
    /// <summary>Settings changes made here take turns (<see cref="ChangeTurns"/>): a change waits for the one saving before
    /// it rather than being refused.</summary>
    private readonly ChangeTurns changes = new();
    private readonly Dictionary<string, HostCheck> hostChecks = new(StringComparer.Ordinal);
    private readonly List<MapElement> mapElements = [];
    private Ellipse? mapGlow;
    private string selectedNode = "this-pc";
    private readonly Dictionary<Panel, HashSet<string>> previousDone = [];
    private int tourStep;
    /// <summary>This PC's own host service as the last read found it (<see cref="LocalHostService"/>); null until the first read.</summary>
    private LocalHostServiceState? hostState;
    private Task? hostProbe;
    private bool hostProbeAgain;
    private DateTime hostProbedAt;
    private string? hostStateSignature;
    /// <summary>When Show a pairing code last paired a computer, until the host's network names it.</summary>
    private DateTime? hostPairedAt;
    private static readonly TimeSpan HostProbeInterval = TimeSpan.FromSeconds(30);
    private readonly System.Windows.Threading.DispatcherTimer hostProbeTimer = new() { Interval = HostProbeInterval };
    private WindowsVirtualization? virtualization;
    private bool refreshingHome;
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
        ErrorLog.Info($"This PC runs as a {(Role == DeviceRole.Host ? "host" : "companion")} PC{(deviceRole is null ? " (not chosen yet)" : "")}.");
        InitializeHealth();
        // The host dashboard reads this PC's host service by itself: at start, every 30 seconds while the window shows and
        // whenever it shows again, so finished steps tick without a button. It keeps reading while setup runs work, so a step
        // another run finishes (Docker Desktop starting, for example) ticks and the next ones open up.
        hostProbeTimer.Tick += (_, _) =>
        {
            if (!closing && IsVisible && HostsHere && !Talking) CheckThisPcHostAsync().Forget();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!closing && IsVisible && HostsHere && DateTime.UtcNow - hostProbedAt > TimeSpan.FromSeconds(10))
                CheckThisPcHostAsync().Forget();
        };
        hostProbeTimer.Start();
        SharedSteps.Changed += SharedStepsChanged;
        HostRunWindow.RunsChanged += ShowHostRuns;
        ApplyRole();
        RenderHome();
        if (deviceRole is null) ShowTour(TourWelcome);
    }

    private void ReleaseShell()
    {
        actionTextDescriptor?.RemoveValueChanged(ActionText, ActionTextChanged);
        hostProbeTimer.Stop();
        SharedSteps.Changed -= SharedStepsChanged;
        HostRunWindow.RunsChanged -= ShowHostRuns;
        ReleaseHealth();
        awake.Dispose();
    }

    /// <summary>A shared setup step (installing or starting Docker Desktop, for example) started or ended: the host dashboard
    /// says so on its Docker Desktop step and offers what can go ahead meanwhile; a companion PC's Home and Devices do so for
    /// its own host service. Once no run works on Docker Desktop any more, this PC's host service is read again at once, so
    /// the step ticks as soon as Docker Desktop runs (or offers its button again) without waiting for the next check.</summary>
    private void SharedStepsChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (closing) return;
        var working = DockerUnderway;
        // Background reads of this PC's host service wait while a conversation replies or hears you, as the timed ones do.
        if (dockerWorkShown && !working && HostsHere && !Talking)
        {
            ErrorLog.Info("No run works on Docker Desktop any more; reading this PC's host service again now.");
            CheckThisPcHostAsync().Forget();
        }
        dockerWorkShown = working;
        RenderHost();
        if (Role != DeviceRole.Companion) return;
        RenderHealth();
        if (DevicesPage.IsVisible) RenderMap();
    });

    /// <summary>Whether a run worked on Docker Desktop when the shared setup steps last changed.</summary>
    private bool dockerWorkShown;

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
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
        if (HostsHere) CheckThisPcHostAsync().Forget();
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

    /// <summary>This change's turn to save (<see cref="ChangeTurns"/>): at once when no other change is saving, otherwise
    /// after the ones before it, saying so on the status line meanwhile. Dispose it as soon as the save is done; never hold
    /// it while something long (an install, a host run) works.</summary>
    private async Task<ChangeTurns.Turn> ChangeTurnAsync()
    {
        if (changes.TryTake() is { } now) return now;
        ActionText.Text = "Waiting for the change before this one to finish saving...";
        return await changes.TakeAsync(lifetime.Token);
    }

    // ---------- navigation ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage is null || DevicesPage is null || CompanionPage is null || CreationsPage is null || TasksPage is null ||
            DiagnosticsPage is null || SettingsPage is null) return;
        if (sender is RadioButton { IsChecked: false }) return;
        FrameworkElement page = ReferenceEquals(sender, NavDevices) ? DevicesPage
            : ReferenceEquals(sender, NavCompanion) ? CompanionPage
            : ReferenceEquals(sender, NavCreations) ? CreationsPage
            : ReferenceEquals(sender, NavTasks) ? TasksPage
            : ReferenceEquals(sender, NavDiagnostics) ? DiagnosticsPage
            : ReferenceEquals(sender, NavSettings) ? SettingsPage
            : HomePage;
        openTab = null;
        foreach (var candidate in new FrameworkElement[] { HomePage, DevicesPage, CompanionPage, CreationsPage, TasksPage, DiagnosticsPage, SettingsPage })
            candidate.Visibility = ReferenceEquals(candidate, page) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(page, CompanionPage)) ShowCompanionTab(entering: true);
        else Motion.Enter(page);
        if (ReferenceEquals(page, DevicesPage)) RenderMap();
        if (ReferenceEquals(page, CreationsPage)) RenderCreations();
        if (ReferenceEquals(page, TasksPage)) EnterTasks();
        else LeaveTasks();
        // Windows' own Startup apps switch can change while Martlet runs.
        if (ReferenceEquals(page, SettingsPage))
        {
            RenderBackground();
            RenderAppearance();
            RenderOtherRoles();
        }
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
        var previous = deviceRole;
        deviceRole = role;
        if (store is not null)
        {
            try { DeviceRolePreference.Save(store.DataDirectory, role); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ActionText.Text = "Couldn't save this choice. Check access to Martlet's data folder.";
            }
        }
        if (previous != role)
            ErrorLog.Info(role == DeviceRole.Host
                ? "This PC is now a host PC: it doesn't talk or listen, keeps the Martlet network it is in (letting your other computers in), " +
                  "and receives who does what without choosing jobs."
                : "This PC is now a companion PC.");
        ApplyRole();
        RenderBackground();
        UpdateStayAwake();
        if (role == DeviceRole.Host)
        {
            // What was on, so it comes back when this PC is your companion PC again.
            if (previous != DeviceRole.Host)
                companionBeforeHost = (avatar.IsShowing, openConversation is { HandsFree: true, ListeningStarted: true, Paused: false },
                    openConversation is { WatchingStarted: true, Paused: false });
            StopCompanionForHostAsync().Forget();
        }
        else if (previous != role)
        {
            messaging.Start();
            // A host PC that is your companion PC again (chosen here or asked from another computer) talks with you at once.
            if (previous == DeviceRole.Host) ResumeCompanionAsync().Forget();
        }
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
        QueueNetworkSync();
        QueueClusterSync();
        // Your other computers' Devices maps learn what this PC is now with the next settings sync.
        if (previous != role) QueueSettingsSync();
        if (HostsHere) CheckThisPcHostAsync().Forget();
        // A companion PC that becomes a host PC starts Docker Desktop and its host roles by itself, warm for the first request.
        if (role == DeviceRole.Host && previous != role) StartHostRolesByItself();
    }

    private void ApplyRole()
    {
        var host = Role == DeviceRole.Host;
        CompanionHome.Visibility = host ? Visibility.Collapsed : Visibility.Visible;
        HostHome.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        NavCompanion.Visibility = host ? Visibility.Collapsed : Visibility.Visible;
        ShowMode();
        UseCompanionButton.IsEnabled = host;
        UseHostButton.IsEnabled = !host;
        if (host && NavCompanion.IsChecked == true) Navigate(NavHome);
    }

    /// <summary>The three ways a PC runs Martlet: a companion PC, a companion PC that also runs a host service for your
    /// computers (its Home then checks that host service as a host PC's does), or a host PC.</summary>
    private void ShowMode()
    {
        var host = Role == DeviceRole.Host;
        var both = !host && ThisPcHost() is not null;
        ModeText.Text = host ? "Host PC" : both ? "Companion PC + host" : "Companion PC";
        RoleText.Text = host
            ? "This PC is a Martlet host. Use it for heavier tasks from your main PC."
            : both
            ? "This is your companion PC. Talk with Martlet here. It also runs a host service, which Home keeps checking."
            : "This is your companion PC. Talk with Martlet here.";
    }

    /// <summary>Whether this PC runs a host service of its own that Martlet should keep reading: it is a host PC, or a
    /// companion PC paired with a host service on this PC.</summary>
    private bool HostsHere => Role == DeviceRole.Host || ThisPcHost() is not null;

    /// <summary>A conversation is replying or hearing you: background reads of this PC's host service wait.</summary>
    private bool Talking => conversation?.Replying == true || openConversation?.HearingYou == true;

    /// <summary>The step that gets this PC's own host service working again (start or install Docker Desktop, start or set
    /// up the host service), as the host dashboard offers it; null when its last reading found it ready.</summary>
    private StepCommand? OwnHostRepair()
    {
        if (hostState is not { Ready: false } state) return null;
        var step = new[] { DockerStep(state), ServiceStep(state) }.FirstOrDefault(s => !s.Done);
        return step?.Commands.FirstOrDefault(c => c.Primary) ?? step?.Commands.FirstOrDefault();
    }

    /// <summary>Reads this PC and its own host service again (Docker, then the host service's address) and checks the
    /// pairing with it, so every place that shows it follows.</summary>
    private async Task CheckOwnHostServiceAsync()
    {
        if (closing) return;
        ActionText.Text = "Checking this PC's host service...";
        await ReadMachineAsync();
        await (hostProbe is { IsCompleted: false } reading ? reading : CheckThisPcHostAsync());
        if (ThisPcHost() is { } own) await CheckHostsAsync([own]);
        if (closing) return;
        ActionText.Text = hostState is { } state ? HostServiceSentence(state) : "This PC's host service couldn't be read.";
    }

    private void UseCompanion_Click(object sender, RoutedEventArgs e) { SetRole(DeviceRole.Companion); Navigate(NavHome); }
    private void UseHost_Click(object sender, RoutedEventArgs e) { SetRole(DeviceRole.Host); Navigate(NavHome); }
    private void ReplayTour_Click(object sender, RoutedEventArgs e) => ShowTour(TourWelcome);

    // The welcome wizard (MainWindow.Welcome.cs). A host stops after the network step. The key step shows only when Thinking
    // goes online. The wizard installs nothing until its suggestions are accepted and confirmed.
    private StackPanel[] TourPanels => Role == DeviceRole.Host && deviceRole is not null ? [TourWelcome, TourNetwork]
        : welcomeNeedsKey ? [TourWelcome, TourNetwork, TourSpecs, TourPreference, TourPlan, TourKey]
        : [TourWelcome, TourNetwork, TourSpecs, TourPreference, TourPlan];

    private void ShowTour(StackPanel panel)
    {
        var appearing = Tour.Visibility != Visibility.Visible;
        Tour.BeginAnimation(OpacityProperty, null);
        Tour.Opacity = 1;
        Tour.Visibility = Visibility.Visible;
        var panels = TourPanels;
        var step = Math.Max(0, Array.IndexOf(panels, panel));
        tourStep = step;
        foreach (var candidate in new[] { TourWelcome, TourNetwork, TourSpecs, TourPreference, TourPlan, TourKey })
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

    private void TourBegin_Click(object sender, RoutedEventArgs e) => ShowTour(TourNetwork);
    private void TourBack_Click(object sender, RoutedEventArgs e) => ShowTour(TourPanels[Math.Max(0, tourStep - 1)]);

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

    /// <summary>Said after a job change the open conversation follows by itself (<see cref="FollowSavedSetup"/>).</summary>
    private const string OpenConversationFollows = " An open conversation switches over before its next reply.";

    /// <summary>The talk window takes a setup saved here between replies, keeping what was said so far, so switching a job's
    /// engine, model or computer never needs the conversation reopened. Nothing happens when it already uses
    /// <paramref name="revision"/>.</summary>
    private void FollowSavedSetup(string? revision, string reason = "Your setup changed.")
    {
        if (openConversation is { } talking && revision is not null && conversation?.Configuration?.Revision != revision)
            talking.ReloadWhenIdle(reason);
    }

    private async Task RefreshHomeAsync()
    {
        if (store is null || setupService is null || closing || refreshingHome || setupOperations.IsRunning) return;
        refreshingHome = true;
        try
        {
            var loaded = await setupService.LoadAsync(lifetime.Token);
            homeSettings = await LeaveRetiredVoiceAsync(loaded, lifetime.Token) ?? loaded.Settings;
            SpeakingEngineChoice.Sync(store.DataDirectory, homeSettings);
            // No voice goes by the companion's own name: one learned by mistake is dropped (names the owner typed stay).
            localVoices.DropCompanionNames(Martlet.Core.Speakers.CompanionNames.From(homeSettings?.Companion?.Personas.Select(p => p.Name),
                homeSettings?.Companion?.Personas.Select(p => p.Text)));
            homeSettingsState = loaded.State;
            homeSettingsProblem = loaded.Error?.Summary;
            // The talk window stays open while you change things in Companion: it picks up a saved change once Martlet is free.
            if (openConversation is { IsReady: true } talking && loaded.Revision is { } revision &&
                (conversation?.Configuration is { } current ? revision != current.Revision : LiveConversationConfiguration.From(loaded) is not null))
                talking.ReloadWhenIdle("Your setup changed.");
            homeAvatar = loaded.Settings is { } settings
                ? (await new AvatarProfileStore(store.DataDirectory).LoadAsync(settings.Profile.Id, lifetime.Token)).Profile
                : null;
            await audioPresence.CheckAsync(lifetime.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ContractException or JsonException or OperationCanceledException) { }
        finally { refreshingHome = false; }
        if (closing) return;
        // A character profile switched to on another computer brings back what it keeps on this PC.
        FollowCharacterProfile();
        // Another persona may be in use now, with another usual gaze.
        avatar.Gaze.Refresh();
        var hostIds = string.Join(",", homeHosts.Select(h => h.HostId + "/" + h.Pairing.CredentialId));
        var sharedIds = string.Join(",", sharedHosts.Select(h => h.HostId + "/" + h.Pairing.CredentialId));
        try
        {
            var all = HostRegistry.Load(store.DataDirectory, homeAvatar?.RemoteHost, machine.LanAddress ?? HostSetupCommands.ThisPcAddress());
            // Hosts friends share with this PC are kept apart: none of the syncs between your own computers talks to them.
            homeHosts = all.Where(h => !h.Shared).ToArray();
            sharedHosts = all.Where(h => h.Shared).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            homeHosts = [];
            sharedHosts = [];
            ActionText.Text = error.Message;
        }
        // A pairing made elsewhere (Add a computer) is shared with the Martlet network right away.
        if (hostIds != string.Join(",", homeHosts.Select(h => h.HostId + "/" + h.Pairing.CredentialId))) QueueNetworkSync();
        // A host a friend just shared with this PC (or one forgotten): read what it offers this PC.
        if (sharedIds != string.Join(",", sharedHosts.Select(h => h.HostId + "/" + h.Pairing.CredentialId)))
        {
            sharedHostsCheckedAt = null;
            if (started) CheckSharedHostsAsync().Forget();
        }
        ObserveLocalJobs();
        UpdateNearby();
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
        CheckOwnLipSyncAsync().Forget();
        CheckLocalServicesAsync().Forget();
        // A companion PC paired with its own host service reads it as the host dashboard does, once its pairings are known.
        if (hostState is null && HostsHere) CheckThisPcHostAsync().Forget();
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
        ShowMode();
        EvaluateCoverage();
        RenderHealth(force: true);
        RenderHost();
        RenderHomeCharacters();
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
            // Whether the step is ticked, for screen readers and MCP ("Host service: done").
            AutomationProperties.SetAutomationId(title, $"StepState-{step.Id}");
            AutomationProperties.SetName(title, $"{step.Title}: {(step.Done ? "done" : step.Optional ? "optional, not done" : "to do")}");
            text.Children.Add(title);
            var detail = new TextBlock { Text = step.Detail, Margin = new Thickness(0, 2, 0, 0) };
            detail.SetResourceReference(StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(detail, $"StepDetail-{step.Id}");
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            // Many buttons wrap on their own line under the text, so they never squeeze the title and detail to nothing.
            var below = step.Commands.Count > 2 || step.Commands.Sum(c => c.Label.Length) > 32;
            Panel actions = below
                ? new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }
                : new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            foreach (var command in step.Commands)
            {
                var button = new Button { Content = command.Label, Margin = below ? new Thickness(0, 0, 8, 8) : new Thickness(8, 0, 0, 0), MinWidth = 86,
                    IsEnabled = command.Enabled };
                if (command.Primary && ReferenceEquals(step, current)) button.SetResourceReference(StyleProperty, "PrimaryButton");
                AutomationProperties.SetName(button, $"{command.Label}: {step.Title}");
                AutomationProperties.SetAutomationId(button, $"Step-{step.Id}-{actions.Children.Count}");
                var run = command.Run;
                button.Click += (_, _) => run();
                actions.Children.Add(button);
            }
            if (below)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(actions, 1);
                Grid.SetColumn(actions, 1);
                Grid.SetColumnSpan(actions, 2);
            }
            else Grid.SetColumn(actions, 2);
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

    /// <summary>Home's first steps on a new PC: Add a computer, which opens on Martlet on your network.</summary>
    private HealthFix ConnectComputersFix() => new("network", "Connect to your other computers", () => RunNodeAction(NodeAction.AddComputer), Passive: true);

    private void ConnectComputers_Click(object sender, RoutedEventArgs e) => ConnectComputersFix().Run();

    // ---------- host dashboard ----------

    private HostSetupTarget ThisPcTarget() => new(HostSetupMethod.ThisPcDocker, "",
        machine.LanAddress ?? HostSetupCommands.ThisPcAddress() ?? "", HostSetupCommands.SuggestedHostId(Environment.MachineName), Version);

    private void RenderHost()
    {
        if (Role != DeviceRole.Host) return;
        var state = hostState;
        var lan = machine.LanAddress ?? HostSetupCommands.ThisPcAddress();
        HostAddressText.Text = state?.Address is { } published ? $"https://{published}"
            : lan is not null ? $"https://{lan}:{WindowsFirewall.Port}" : "No home network address found yet";
        HostStatusText.Text = HostHeadline(state);
        if (state?.Ready == true) Motion.PulseRing(HostPulse, to: 1.35);
        else
        {
            HostPulse.BeginAnimation(OpacityProperty, null);
            HostPulse.Opacity = 0;
        }
        var setUp = state?.Stage is LocalHostServiceStage.Stopped or LocalHostServiceStage.Running;
        var steps = new List<HomeStep> { DockerStep(state), ServiceStep(state), PairStep(state), RolesStep(state), UpdateStep(state, setUp) };
        // Your other computers asking to join the network show right under pairing: this PC may be the only one that can
        // let them in (it started the network while it was a companion PC).
        var joinAt = steps.FindIndex(s => s.Id == "pair") + 1;
        foreach (var join in networkJoins.Reverse())
            steps.Insert(joinAt, new($"join-{join.DeviceId}", $"Let {join.DisplayName} into your Martlet network",
                $"{join.DisplayName} ({join.DeviceId}) is paired with {join.HostId} and asks to join, so it can use all your hosts. Allow " +
                $"it only if that computer shows check number {join.CheckNumber}.",
                false, false, [new("Allow", () => AllowJoin(join), true), new("Turn down", () => DenyJoinAsync(join).Forget())]));
        RenderSteps(HostStepsPanel, steps, numbered: true);

        var left = steps.Where(s => !s.Optional && !s.Done).ToList();
        HostStepsHeading.Text = left.Count == 0 && state is not null ? "This host is ready" : "Get this host running";
        var checkedAt = hostProbedAt == default ? "" : $" Last checked {hostProbedAt.ToLocalTime():t}.";
        HostStepsSummary.Text = state is null ? "Checking this PC's host service..."
            : left.Count == 0
                ? "All set: the host service is running and paired." +
                  (state.Roles is { Count: 0 } ? " Add a role so it has work to do." : "") +
                  $" Martlet keeps checking it every {HostProbeInterval.TotalSeconds:0} seconds.{checkedAt}"
                : $"{left.Count} step{(left.Count == 1 ? "" : "s")} left. Next: {left[0].Title}. Steps tick by themselves once " +
                  $"they're done; Martlet checks every {HostProbeInterval.TotalSeconds:0} seconds.{checkedAt}";
        ShowHostRuns();
    }

    /// <summary>The setup runs working now under the host dashboard's steps, each with its status line ("Start Docker Desktop:
    /// Waiting for Docker Desktop to start..."). They run side by side: what they share waits for the run doing it.</summary>
    private void ShowHostRuns()
    {
        if (closing || Role != DeviceRole.Host) return;
        var runs = HostRunWindow.Running;
        HostRunsText.Visibility = runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HostRunsText.Text = runs.Count == 0 ? ""
            : (runs.Count == 1 ? "Working now: " : $"{runs.Count} runs working side by side: ") +
              string.Join(" · ", runs.Select(run => run.CurrentStatus.Length == 0 ? run.Heading : $"{run.Heading}: {run.CurrentStatus}"));
    }

    /// <summary>Docker Desktop is being installed, Windows readied for it or it is being started, by a run working now.</summary>
    private static bool DockerUnderway => DockerWorkNow() is not null;

    /// <summary>The Docker Desktop step's greyed-out label for what the runs working now do with Docker Desktop, or null when
    /// none does (<see cref="DockerWorkLabel"/>).</summary>
    private static string? DockerWorkNow() =>
        DockerWorkLabel(SharedSteps.Now().Select(step => step.Key), HostRunWindow.IsRunningTitled(HostsWindow.InstallDockerTitle));

    /// <summary>What the Docker Desktop step's button says, greyed out, while runs work on Docker Desktop: installing it
    /// (<paramref name="installing"/>: the Install Docker Desktop run works), starting it, or getting Windows ready for it.
    /// A start checks Windows too, so that check doesn't change the label. <paramref name="steps"/> are the keys of the
    /// <see cref="SharedSteps"/> running now. Null when no run works on Docker Desktop.</summary>
    internal static string? DockerWorkLabel(IEnumerable<string> steps, bool installing)
    {
        var running = steps.ToHashSet(StringComparer.Ordinal);
        return installing || running.Contains(SharedSteps.DockerInstall) ? "Installing Docker Desktop..."
            : running.Contains(SharedSteps.DockerStart) ? "Starting Docker Desktop..."
            : running.Contains(SharedSteps.WindowsReady) ? "Getting Windows ready..."
            : null;
    }

    /// <summary>What the runs working on Docker Desktop are doing now ("\"Start Docker Desktop\" is starting Docker Desktop"),
    /// or null when none is.</summary>
    private static string? DockerWork()
    {
        var working = SharedSteps.Now().Where(step => step.Key is SharedSteps.DockerInstall or SharedSteps.WindowsReady or SharedSteps.DockerStart)
            .Select(step => $"\"{step.By}\" is {step.Doing}").ToList();
        if (working.Count == 0 && HostRunWindow.IsRunningTitled(HostsWindow.InstallDockerTitle)) working.Add($"\"{HostsWindow.InstallDockerTitle}\" is working");
        return working.Count == 0 ? null : string.Join("; ", working);
    }

    private string HostHeadline(LocalHostServiceState? state) => state?.Stage switch
    {
        null => "Checking...",
        LocalHostServiceStage.DockerMissing => "Needs Docker Desktop",
        LocalHostServiceStage.DockerNotRunning when virtualization?.RestartRequired == true => "Needs Windows restart",
        LocalHostServiceStage.DockerNotRunning when virtualization?.Blocked == true => "Windows isn't ready for Docker Desktop",
        LocalHostServiceStage.DockerNotRunning => "Waiting for Docker Desktop",
        LocalHostServiceStage.NotSetUp => "Not set up yet",
        LocalHostServiceStage.Stopped => "Host service stopped",
        _ when state.AddressOnThisPc == false => "Address changed",
        _ when state.Answering != true => "Not answering yet",
        _ => "Host is running"
    };

    private HomeStep DockerStep(LocalHostServiceState? state)
    {
        var installed = state is null ? machine.DockerInstalled : state.Stage != LocalHostServiceStage.DockerMissing;
        var done = state is not null && state.Stage is not (LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning);
        var windowsBlocks = !done && installed && virtualization?.Blocked == true;
        var engineWaiting = machine.DockerRunning && state?.Stage == LocalHostServiceStage.DockerNotRunning;
        var underway = done ? null : DockerWork();
        // While a run works on Docker Desktop (for example the one a new host PC starts by itself), the step's button says what
        // that run does and stays greyed out until it ends, so nobody starts Docker Desktop a second time.
        var working = done ? null : DockerWorkNow();
        var action = virtualization switch
        {
            { RestartRequired: true } => "Restart Windows",
            { FirmwareOff: true, VirtualMachine: false } => "Turn on virtualization",
            { NeedsChanges: true } => "Turn on Windows features",
            _ => "Review Windows setup"
        };
        StepCommand next = windowsBlocks ? new(action, PrepareWindows, true)
            : installed ? new("Start Docker Desktop", StartDocker, true)
            : new("Install Docker Desktop", InstallDocker, true);
        return new("docker", "Docker Desktop",
            done ? "Running."
                : underway is not null ? underway + ". You don't have to wait: other setup can go ahead meanwhile, and steps that need " +
                    "Docker Desktop carry on once it runs."
                : windowsBlocks ? string.Join("; ", virtualization!.Problems()) + ". " + virtualization.Recovery
                : engineWaiting ? "Docker Desktop is open, but its engine isn't answering yet. The first start can take a few minutes."
                : state is null && installed ? "Checking Docker Desktop's engine..."
                : installed ? "Installed, but not running." : "Required for the host service.",
            done, false,
            done ? [] : [working is null ? next : next with { Label = working, Enabled = false }]);
    }

    private HomeStep ServiceStep(LocalHostServiceState? state)
    {
        StepCommand SetUp(string label, bool primary = true) => new(label, () => SetUpHostServiceAsync().Forget(), primary);
        // While Docker Desktop is still being installed or started, a host service Martlet hasn't seen set up can be set up
        // already: its run waits for Docker Desktop and carries on as soon as it runs.
        var early = state?.Stage is LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning && DockerUnderway &&
            ThisPcHost() is null && thisPcHostVersion is null;
        (string Detail, StepCommand[] Commands) step = state?.Stage switch
        {
            null => ("Checking this PC's host service...", Array.Empty<StepCommand>()),
            LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning when early =>
                ("Runs in Docker Desktop. You don't have to wait for it: set it up now and it carries on as soon as Docker Desktop " +
                 "runs. Windows may ask to allow private-network access.", [SetUp("Set up host service", primary: false)]),
            LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning =>
                ("Runs in Docker Desktop. Martlet checks it again once Docker Desktop is running.", []),
            LocalHostServiceStage.NotSetUp =>
                ("Not set up yet. Sets up the host service; Windows may ask to allow private-network access.", [SetUp("Set up host service")]),
            LocalHostServiceStage.Stopped => ("Set up, but not running. Start it again; pairings and roles stay.", [SetUp("Start host service")]),
            _ when state.AddressOnThisPc == false =>
                ("This PC's network address changed since setup, so your other computers can't reach the host service. Set it up " +
                 "again for the new address; pairings and roles stay.", [SetUp("Set up again")]),
            _ when state.Answering != true =>
                ("Running, but not answering on your network yet. It can take a moment after a start; if this lasts, set it up " +
                 "again (pairings and roles stay).", [SetUp("Set up again", primary: false)]),
            _ => ($"Running and reachable on your network{(state.HostId is { } id ? $" as {id}" : "")}.", [])
        };
        // A setup run working now says so (pressing its button again brings that run's window forward).
        if (state?.Ready != true && HostRunWindow.IsRunningTitled(HostActions.ThisPcSetupTitle))
            step.Detail = $"Being set up now (\"{HostActions.ThisPcSetupTitle}\"). " + step.Detail;
        return new("service", "Host service", step.Detail, state?.Ready == true, false, step.Commands);
    }

    /// <summary>The other computers paired with this PC's host service, as it said on the last network sync: members of its
    /// network or not (such as one still waiting to join), never a friend's computer. Empty when this PC isn't paired with its
    /// own host service, or before the first sync.</summary>
    private IReadOnlyList<HostPairedDevice> PairedComputers() =>
        ThisPcHost() is { } own && PairedWith(own.HostId) is { } devices ? devices.Where(d => !IsThisDevice(d.DeviceId) && !d.Friend).ToArray() : [];

    private HomeStep PairStep(LocalHostServiceState? state)
    {
        var self = NetworkIdentity.DeviceId(homeHosts);
        var desktops = state?.Desktops ?? [];
        // The host's network members, and every computer the host service reports as paired with it.
        var now = DateTimeOffset.UtcNow;
        var reported = PairedComputers();
        var others = desktops.Where(d => d.Id != self && !IsThisDevice(d.Id)).Select(d => (d.Id, d.Name))
            .Concat(reported.Select(d => (Id: d.DeviceId, Name: d.DisplayName)))
            .DistinctBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => reported.FirstOrDefault(r => r.DeviceId == d.Id) is { } seen ? $"{d.Name} ({Seen(seen.LastSeen, now)})" : d.Name)
            .ToList();
        var thisPc = desktops.Any(d => d.Id == self) || homeHosts.Any(h => h.Method == HostSetupMethod.ThisPcDocker);
        var justPaired = hostPairedAt is { } at && DateTime.UtcNow - at < TimeSpan.FromMinutes(5);
        var paired = others.Count > 0 || thisPc || justPaired;
        // Who is paired is read from the running host service; until it runs there is nothing to pair with.
        if (!paired && state?.Stage != LocalHostServiceStage.Running)
            return new("pair", "Pair your main PC", state?.Stage switch
            {
                null => "Checking which computers are paired...",
                LocalHostServiceStage.NotSetUp => "Set up the host service first, then pair your main PC here.",
                _ => "Martlet sees which computers are paired once the host service runs."
            }, false, false, []);
        var detail = others.Count > 0 ? $"Paired with {JoinNames(others)}{(thisPc ? " and this PC" : "")}. You can pair more computers any time."
            : thisPc ? "Paired with this PC. To use this host from another computer too, choose Devices > Add a computer there: " +
                "this PC is listed under Martlet on your network."
            : justPaired ? "Paired. Your main PC shows here by name once the host service reports it."
            : "On your main PC, choose Devices > Add a computer: this PC is listed under Martlet on your network. Press Connect " +
              "there, then Allow here when both show the same check number. Or show a pairing code and type it there (Enter a " +
              "pairing code).";
        if (NearbyBlocked)
            detail += " Windows Firewall doesn't let your other computers find this PC yet; Let my other computers find this PC " +
                "fixes that (administrator approval once).";
        var pair = new StepCommand(paired ? "Pair another computer" : "Show a pairing code", () => LaunchHost(HostAction.Pair), !paired);
        return new("pair", "Pair your main PC", detail, paired, false, NearbyBlocked
            ? [pair, new("Let my other computers find this PC", () => NearbyFirewall_Click(this, new RoutedEventArgs()))]
            : [pair]);
    }

    private HomeStep RolesStep(LocalHostServiceState? state)
    {
        var nvidia = machine.BestGpu is { IsNvidia: true } gpu ? $"This PC has {gpu.Describe()}."
            : machine.ProcessorType.WindowsOnArm ? "This is a Windows on Arm PC, where NVIDIA graphics cards don't work, so GPU roles need another computer."
            : "No NVIDIA graphics card found.";
        // Roles are read from the running host service; until it runs there is nothing to add them to.
        if (state?.Roles is not { } installed)
            return new("roles", "Add roles", "Add tasks this host can handle once the host service runs. " + nvidia, false, true, []);
        string Name(string kind) => HostRoles.All.FirstOrDefault(r => r.Kind == kind)?.Name ?? kind;
        // Jobs your Martlet network gives this PC (it was set up to do them, as a companion or a host PC) need their roles here.
        var needed = clusterEnabled && state.HostId is { } self
            ? ClusterJobs.All.Where(j => clusterPlan.For(j)?.HostId == self).Select(j => (Job: j, Kind: ClusterSync.RoleKind(j)))
                .Where(n => !installed.Contains(n.Kind, StringComparer.Ordinal)).ToList()
            : [];
        var missing = HostRoles.All.Where(r => !installed.Contains(r.Kind, StringComparer.Ordinal))
            .OrderBy(r => needed.Any(n => n.Kind == r.Kind) ? 0 : 1).ToList();
        IReadOnlyList<StepCommand> commands =
        [
            .. missing.Select((r, i) => new StepCommand($"Add {r.Name}", () => LaunchHost(r.Add),
                needed.Any(n => n.Kind == r.Kind) || installed.Count == 0 && i == 0)),
            .. HostRoles.All.Where(r => installed.Contains(r.Kind, StringComparer.Ordinal))
                .Select(r => new StepCommand($"Remove {r.Name}", () => LaunchHost(r.Remove)))
        ];
        var asked = needed.Count == 0 ? ""
            : $"Your computers use this PC for {JoinNames([.. needed.Select(n => n.Job)])}, so add " +
              $"{JoinNames([.. needed.Select(n => Name(n.Kind))])} here; until then they keep what they use now. ";
        return new("roles", "Add roles",
            asked + (installed.Count == 0 ? "No roles yet. Add tasks this host can handle. " + nvidia
                : $"Runs {JoinNames([.. installed.Select(Name)])}. Add or remove roles any time."),
            installed.Count > 0 && needed.Count == 0, true, commands);
    }

    private HomeStep UpdateStep(LocalHostServiceState? state, bool setUp)
    {
        var update = new StepCommand("Update host service", () => LaunchHost(HostAction.Update));
        if (!setUp)
            return new("update", "Keep it up to date", state?.Stage switch
            {
                null => "Checking the host service's version...",
                LocalHostServiceStage.NotSetUp => $"Once it's set up, the host service runs this app's version (Martlet {Version}).",
                _ => "Martlet checks the host service's version once Docker Desktop is running."
            }, false, true, []);
        if (state!.Version is not { } running)
            return new("update", "Keep it up to date", $"Updates the host service to Martlet {Version}. Pairings and roles stay.",
                false, true, [update]);
        if (!AppVersions.IsOlder(running, Version))
            return new("update", "Keep it up to date", $"Up to date: the host service runs Martlet {running}.", true, true, []);
        if (hostUpdates.IsUpdating(ThisPcHostId))
            return new("update", "Keep it up to date",
                $"Updating the host service from Martlet {running} to {Version}. Pairings and roles stay.", false, true, []);
        return new("update", "Keep it up to date",
            $"The host service runs Martlet {running}. Martlet updates it to {Version} by itself " +
            (state.Stage == LocalHostServiceStage.Running ? "in the background" : "once it runs again") +
            "; Update host service does it now. Pairings and roles stay.", false, true, [update with { Primary = true }]);
    }

    /// <summary>"A", "A and B", "A, B and C", "A, B, C and 2 more".</summary>
    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        <= 3 => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        _ => string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more"
    };

    /// <summary>Reads this PC's host service from Docker (<see cref="LocalHostService"/>) and shows it on the host dashboard. A
    /// call while a read runs returns that read, which then reads once more so it sees what just changed.</summary>
    private Task CheckThisPcHostAsync()
    {
        if (hostProbe is { IsCompleted: false } running)
        {
            hostProbeAgain = true;
            return running;
        }
        return hostProbe = ProbeThisPcHostAsync();
    }

    private async Task ProbeThisPcHostAsync()
    {
        do
        {
            hostProbeAgain = false;
            if (closing || !HostsHere) return;
            LocalHostServiceState state;
            try
            {
                state = await LocalHostService.ProbeAsync(lifetime.Token);
                // An open Docker Desktop window is not a working engine. Diagnose Windows even while that window is open.
                virtualization = state.Stage == LocalHostServiceStage.DockerNotRunning
                    ? await WindowsVirtualization.ProbeAsync(lifetime.Token) : null;
            }
            catch (OperationCanceledException) { return; }
            if (closing) return;
            ApplyHostState(state);
        } while (hostProbeAgain);
    }

    private void ApplyHostState(LocalHostServiceState state)
    {
        var wasReady = hostState?.Ready == true;
        hostState = state;
        hostProbedAt = DateTime.UtcNow;
        RememberThisPcHostRoles(state);
        var before = thisPcHostVersion;
        if (state.Stage is LocalHostServiceStage.Stopped or LocalHostServiceStage.Running) thisPcHostVersion = state.Version;
        else if (state.Stage == LocalHostServiceStage.NotSetUp) thisPcHostVersion = null;
        var self = NetworkIdentity.DeviceId(homeHosts);
        if (state.Desktops.Any(d => d.Id != self)) hostPairedAt = null;
        var signature = $"{state.Stage}|{state.Ready}|{state.Version}|{virtualization?.Describe()}|{string.Join(",", state.Roles ?? [])}|" +
            string.Join(",", state.Desktops.Select(d => d.Id));
        if (signature != hostStateSignature)
        {
            hostStateSignature = signature;
            ErrorLog.Info($"This PC's host service: {HostHeadline(state)} (stage {state.Stage}, version {state.Version ?? "-"}, " +
                $"roles {(state.Roles is { } roles ? roles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}, " +
                $"network {state.Network ?? "unknown"} with {state.Desktops.Count} desktop(s))" +
                (state.Problem is { } problem ? $": {problem}" : "."));
        }
        if (before != thisPcHostVersion) UpdateNearby();
        // An update that finished by any route (or one that reported busy against Martlet's own run) leaves no stale note.
        HostFoundCurrent(ThisPcHostId, thisPcHostVersion);
        RenderHost();
        UpdateStayAwake();
        // A companion PC that also hosts shows its host service on Home and the Devices map, and in each job it does.
        if (Role == DeviceRole.Companion)
        {
            // Back up (Docker Desktop or the host service started): the jobs it does check it again rather than wait.
            if (state.Ready && !wasReady && ThisPcHost() is { } own && hostChecks.GetValueOrDefault(own.HostId)?.Reachable == false)
                CheckHostsAsync([own]).Forget();
            RefreshCoverage();
            RenderHealth();
            if (DevicesPage.IsVisible) RenderMap();
        }
    }

    /// <summary>What the last read of this PC's host service means, as one sentence for the status line.</summary>
    private string HostServiceSentence(LocalHostServiceState state) => state.Stage switch
    {
        LocalHostServiceStage.DockerMissing => "Docker Desktop isn't installed. Install it first (step 1).",
        LocalHostServiceStage.DockerNotRunning when virtualization?.Blocked == true =>
            string.Join("; ", virtualization.Problems()) + ". " + virtualization.Recovery,
        LocalHostServiceStage.DockerNotRunning => machine.DockerRunning
            ? "Docker Desktop is open, but its engine isn't answering yet, so the host service can't run. The first start can take a few minutes."
            : "Docker Desktop isn't running yet, so the host service can't run. Start Docker Desktop (step 1).",
        LocalHostServiceStage.NotSetUp => "The host service isn't set up yet. Use Set up host service.",
        LocalHostServiceStage.Stopped => "The host service is set up but stopped. Use Start host service; pairings and roles stay.",
        _ when state.AddressOnThisPc == false => "This PC's network address changed since setup. Use Set up again; pairings and roles stay.",
        _ when state.Answering != true => "The host service runs but isn't answering on your network yet. Check again in a moment.",
        _ => "The host service is running and reachable on your network." +
            (PairStep(state).Done ? "" : " Pair your main PC next.")
    };

    /// <summary>Runs a host-dashboard step on this PC's host service in a run window (never a console). Pairing shows the
    /// one-use code for the main PC. Steps run side by side: changes to the host service run side by side in its engine,
    /// where only colliding ones wait (a run waiting for another says so in its window), what they share (starting Docker Desktop, the host image) is done
    /// once, and pressing a step that is still working brings its window forward.</summary>
    private void LaunchHost(HostAction action) => LaunchHostAsync(action).Forget();

    private async Task LaunchHostAsync(HostAction action)
    {
        if (closing || store is null) return;
        // Martlet's automatic host update leaves this PC's host service to this run rather than colliding with it.
        using var updating = action.Verb == HostVerb.Update ? hostUpdates.Begin(ThisPcHostId) : null;
        try
        {
            ActionText.Text = "Running host setup. The progress window shows details.";
            var done = action.Verb == HostVerb.Pair
                ? await HostActions.PairOtherDesktopAsync(this, ThisPcTarget())
                : await HostActions.RunAsync(this, store.DataDirectory, ThisPcTarget(), null, action);
            if (closing) return;
            if (done is not null && action.Verb == HostVerb.Update) HostUpdateSettled(ThisPcHostId);
            ActionText.Text = done ?? "Stopped. See the progress window for details.";
            if (done is not null && action.Verb == HostVerb.Pair) hostPairedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            if (!closing)
            {
                RenderHost();
                CheckThisPcHostAsync().Forget();
            }
        }
    }

    private void HostStatusConsole_Click(object sender, RoutedEventArgs e) => LaunchHost(HostAction.Status);

    private async Task SetUpHostServiceAsync()
    {
        var target = ThisPcTarget();
        if (!HostSetupCommands.IsPrivate(target.Address))
        {
            ActionText.Text = "Connect this PC to your home network first.";
            return;
        }
        string? firewall;
        try
        {
            firewall = await HostsWindow.OpenFirewallAsync(this, target.Address, text => ActionText.Text = text, lifetime.Token,
                HostActions.ThisPcSetupTitle);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { firewall = $"Windows Firewall wasn't changed. Other PCs may not reach this host. {error.Message}"; }
        catch (OperationCanceledException) { return; }
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
        if (closing) return;
        var done = await HostRunWindow.RunAsync(this, "Get Windows ready for Docker Desktop", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            return "Docker Desktop is running.";
        }, join: true);
        if (!closing) ActionText.Text = done ?? "Windows isn't ready for Docker Desktop yet. See the progress window for details.";
        if (!closing) await ReadMachineAsync();
    }

    /// <summary>After Windows restarted to finish turning on virtualization, continues the setup that asked for it
    /// (<see cref="HostSetupResume"/>): this PC's host service and its pairing, the host dashboard's host service, or
    /// starting Docker Desktop so the owner can repeat the step that needed it.</summary>
    private async Task ContinueSetupAsync()
    {
        if (closing || store is null || HostSetupResume.Take() is not { } note) return;
        ErrorLog.Info($"Continuing after a Windows restart: {note.Kind} ({note.Task})");
        ActionText.Text = $"Continuing setup: {note.Task}...";
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
        if (closing) return;
        var done = await HostRunWindow.RunAsync(this, "Start Docker Desktop", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            return $"Docker Desktop is running. Continue: {task}.";
        }, join: true);
        if (!closing) ActionText.Text = done ?? "Docker Desktop didn't start. See the progress window for details.";
        if (!closing) await ReadMachineAsync();
    }

    private void StartDocker() => StartDockerAfterRestartAsync("host setup").Forget();

    /// <summary>Check again: reads this PC and its host service now (Docker, the gateway's roles and network, its published
    /// port) and says what that means. Read-only; it changes nothing.</summary>
    private async void CheckHostService_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        CheckHostServiceButton.IsEnabled = false;
        HostStatusText.Text = "Checking...";
        try
        {
            await ReadMachineAsync();
            // Reading this PC also starts a read of its host service; wait for that one rather than starting another.
            await (hostProbe is { IsCompleted: false } reading ? reading : CheckThisPcHostAsync());
        }
        finally { if (!closing) CheckHostServiceButton.IsEnabled = true; }
        if (closing) return;
        RenderHost();
        if (hostState is { } state) ActionText.Text = HostServiceSentence(state);
    }

    // ---------- devices map ----------

    private HostHardwareStore? HardwareStore => store is null ? null : new(store.DataDirectory);

    private NetworkInputs Inputs() => new(machine, Role, homeSettings, homeAvatar, avatar.IsShowing, hostChecks,
        HardwareStore?.Load() ?? [], homeHosts, hostUpdates.Notes, HostUsers(), clusterEnabled ? clusterPlan : null, OtherComputers(),
        DeepThinkingHosts(), HostOutsideFacts(), OwnHostTrouble(), ThinkingPoolLeft(), PresenceAway(), ConfiguringMachines(), sharedHosts);

    /// <summary>The paired computers the owner keeps out of the Thinking pool (unticked), or null.</summary>
    private IReadOnlyCollection<string>? ThinkingPoolLeft() =>
        store is not null && ThinkingPoolSettings.Load(store.DataDirectory).LeftByOwner is { Count: > 0 } left ? left : null;

    /// <summary>The paired computers whose Deep thinking role this PC thinks with, while Deep thinking is on.</summary>
    private IReadOnlyCollection<string>? DeepThinkingHosts() =>
        store is not null && ThinkLongerSettings.Of(homeSettings?.Generation).On &&
        ThinkingPoolSettings.Load(store.DataDirectory).Places.Places.Where(p => p.OnHostRole).Select(p => p.HostId!).ToArray() is { Length: > 0 } hosts ? hosts : null;

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
    private void DevicesPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SizeMap();
        SizeDeviceList();
    }

    private int mapRows = 1;

    private void SizeMap() =>
        MapHost.Height = Math.Max(Math.Clamp(DevicesPage.ActualHeight * 0.42, 300, 400), mapRows * 104 + 40);

    private void RenderMap()
    {
        networkDevicesShown = NetworkDevicesSignature();
        var nodes = NetworkMap.Build(Inputs());
        RenderDeviceSettings(nodes);
        RenderNetworkCapacity(nodes);
        RenderNetwork();
        RenderFriends();
        RenderSharedHosts();
        RefreshFriendsWhenShown();
        if (nodes.All(n => n.Id != selectedNode)) selectedNode = "this-pc";
        mapNodes = nodes;
        DevicesSummary.Text = DeviceOverview.Summary(nodes);
        deviceListShown = deviceListChosen ?? !DeviceOverview.FitsMap(nodes);
        settingDevicesView = true;
        DevicesViewMap.IsChecked = !deviceListShown;
        DevicesViewList.IsChecked = deviceListShown;
        settingDevicesView = false;
        MapHost.Visibility = deviceListShown ? Visibility.Collapsed : Visibility.Visible;
        DeviceListHost.Visibility = deviceListShown ? Visibility.Visible : Visibility.Collapsed;
        MapCanvas.Children.Clear();
        mapElements.Clear();
        DeviceList.Children.Clear();
        deviceListCards.Clear();
        if (deviceListShown)
        {
            RenderDeviceList();
            SelectNode(selectedNode, animate: false);
            return;
        }
        nodes = DeviceOverview.MapNodes(nodes, selectedNode);
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
        var right = mapElements.Where(m => m.Node.Kind is NodeKind.Host or NodeKind.Computer).ToList();
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
        // Add a computer has no status, so its name gets the room.
        if (node.Kind == NodeKind.Add) status.Visibility = Visibility.Collapsed;
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
            : DeviceOverview.IsMore(node.Id) ? $"{node.Title}: {node.HealthText}. Opens the list of every device."
            : $"{node.Title}, {node.Subtitle}. {node.HealthText}. Runs: {string.Join(", ", node.Roles.Select(r => r.Name))}");
        var id = node.Id;
        card.Click += (_, _) =>
        {
            if (DeviceOverview.IsMore(id)) ShowDeviceList();
            else SelectNode(id, animate: true);
        };
        return (card, ring);
    }

    private void SelectNode(string id, bool animate)
    {
        // A device the map folded into "N more" (selected from Home or a job's Show) takes a place on the map.
        if (!deviceListShown && !remapping && mapNodes.Any(n => n.Id == id) && mapElements.All(m => m.Node.Id != id))
        {
            selectedNode = id;
            remapping = true;
            try { RenderMap(); }
            finally { remapping = false; }
            return;
        }
        selectedNode = id;
        MarkSelectedDevice();
        if (mapNodes.FirstOrDefault(n => n.Id == id) is not { } node) return;
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

    /// <summary>A host a friend shares with this PC, or null.</summary>
    private PairedHost? FindSharedHost(string? hostId) => hostId is null ? null : sharedHosts.FirstOrDefault(h => h.HostId == hostId);

    /// <summary>A shared host's option text in a job's choice: its name, that a friend shares it, and whether it offers the job
    /// (for Speaking, any voice engine: this PC speaks with the one it runs).</summary>
    private string SharedOption(PairedHost host, string roleKind)
    {
        var check = hostChecks.GetValueOrDefault(host.HostId);
        var offers = check?.Offers?.Keys.Any(k => k == roleKind || HostRoles.Speaks(roleKind) && HostRoles.Speaks(k)) == true;
        return $"{host.HostId}, shared by a friend" + (offers ? " (ready)"
            : check?.Reachable == true ? " (doesn't offer it)" : check?.Reachable == false ? " (not reachable)" : "");
    }

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
        Option("this-pc", ownLipSyncAnswers == false ? "This PC lip-sync (not running)" : "This PC lip-sync");
        var local = ThisPcHost()?.HostId;
        foreach (var host in NetworkMap.Hosts(Inputs()))
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var offers = check?.Offers?.ContainsKey(HostRoles.Audio2Face) == true;
            var name = host.HostId == local ? $"{host.HostId}, this PC" : host.HostId;
            if (!offers && CannotHand(host.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is { } cannot)
            {
                Option("host:" + host.HostId, $"{name} (can't take it now)", cannot);
                continue;
            }
            Option("host:" + host.HostId, name + (offers ? " (ready for lip-sync)"
                : check?.Reachable == true ? " (needs lip-sync setup)" : check?.Reachable == false ? " (not reachable)" : ""));
        }
        // Hosts friends share with this PC: lip-sync on this PC only.
        foreach (var host in sharedHosts) Option("host:" + host.HostId, SharedOption(host, HostRoles.Audio2Face));
        Option("off", "No one (basic mouth movement)");
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
        ChangeTurns.Turn? turn = null;
        try
        {
            turn = await ChangeTurnAsync();
            PairedHost? host = null;
            var install = false;
            if (key.StartsWith("host:", StringComparison.Ordinal))
            {
                host = FindHost(key[5..]) ?? FindSharedHost(key[5..]) ?? throw new InvalidOperationException("That host is no longer paired.");
                ActionText.Text = $"Checking {host.HostId}...";
                var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token, host.Shared);
                hostChecks[host.HostId] = check;
                if (check.Reachable != true)
                {
                    ActionText.Text = $"Lip-sync stays where it is: {host.HostId} didn't answer ({check.Text})";
                    return;
                }
                if (host.Shared)
                {
                    // A friend's host offers what its owner set up there; this PC can't add roles to it.
                    if (check.Offers?.ContainsKey(HostRoles.Audio2Face) != true)
                    {
                        ActionText.Text = $"Lip-sync stays where it is: {host.HostId} doesn't offer lip-sync to this PC. Only its owner can add it there.";
                        return;
                    }
                    if (!ConfirmationDialog.Confirm(this, $"Use {host.HostId} for lip-sync on this PC? A friend shares it with you: the generated " +
                            "voice audio goes there and isn't kept. Only this PC uses it; your other computers keep theirs. Its owner's own work " +
                            "comes first, so lip-sync may pause while it is busy; your voice goes on.", $"Use {host.HostId}"))
                        return;
                }
                else if (check.Offers?.ContainsKey(HostRoles.Audio2Face) != true)
                {
                    if (CannotHand(host.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is { } cannot)
                    {
                        ActionText.Text = $"Lip-sync stays where it is: {cannot}";
                        return;
                    }
                    var role = HostRoles.Get(HostRoles.Audio2Face);
                    if (!ConfirmationDialog.Confirm(this,
                            $"Install lip-sync support on {host.HostId}? " +
                            (host.CanLaunch ? "Martlet will set it up and show progress. " : "Set how Martlet reaches it on the Devices map first. ") +
                            $"It needs {role.Needs}. Until it is ready, the mouth uses basic movement.",
                            "Install lip-sync"))
                        return;
                    install = true;
                }
            }
            await ApplyLipSyncAsync(host, key == "off");
            tabPlace.Remove(CompanionTab.LipSync);
            RecordClusterJob(ClusterJobs.LipSync, ClusterSync.Local(ClusterJobs.LipSync, homeSettings, homeAvatar, shared: SharedHostIds()));
            var who = key == "off" ? "no one (basic mouth movement)" : host?.HostId ?? "this PC";
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
            turn?.Dispose();
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

    private void CheckHosts_Click(object sender, RoutedEventArgs e)
    {
        CheckHostsAsync(NetworkMap.Hosts(Inputs())).Forget();
        QueueSettingsSync();
    }

    private async Task CheckHostsAsync(IReadOnlyList<PairedHost> hosts)
    {
        if (hosts.Count == 0)
        {
            ActionText.Text = "No host is paired yet. Add a computer first.";
            return;
        }
        foreach (var host in hosts) hostChecks[host.HostId] = new(null, "Checking...");
        if (DevicesPage.IsVisible) RenderMap();
        (string Id, HostCheck Check)[] results;
        try { results = await Task.WhenAll(hosts.Select(async h => (h.HostId, await HostControl.CheckAsync(h.Pairing, HardwareStore, lifetime.Token)))); }
        catch (OperationCanceledException) { return; }
        foreach (var (id, check) in results) hostChecks[id] = check;
        // What answered and what didn't counts for the Thinking pool too, also while Keep in sync is off.
        foreach (var (id, check) in results)
            if (check.Reachable is { } reachable) HostPresence.Note(id, reachable);
        if (closing) return;
        // A role whose settings changed there (from here or another computer) serves another model now: follow it.
        FollowHostModelsAsync(results.Where(r => r.Check.Reachable == true).Select(r => r.Id).ToArray()).Forget();
        // The Singing card reads each checked computer's voice matches again (a role added or removed elsewhere).
        foreach (var (id, _) in results) singingServices.Remove(id);
        NoteSingingHost();
        foreach (var host in hosts)
            if (hostChecks.GetValueOrDefault(host.HostId) is { Reachable: true } found) HostFoundCurrent(UpdateKey(host), found.MartletVersion);
        // A computer with a Thinking model joins the Thinking pool by itself, also while Keep in sync is off.
        var joined = results.Select(r => NoteThinkingPoolHost(r.Id)).OfType<string>().ToArray();
        ActionText.Text = (results.Length == 1 ? $"{results[0].Id}: {results[0].Check.Text}"
            : $"Checked {results.Length} hosts: {results.Count(r => r.Check.Reachable == true)} reachable, " +
              string.Join(", ", HostRoles.All.Select(role =>
                  $"{results.Count(r => r.Check.Offers?.ContainsKey(role.Kind) == true)} running {role.Name}")) + ".") +
            string.Concat(joined.Select(line => " " + line));
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
        QueueClusterSync();
    }

    /// <summary>Runs a martlet-host command on a paired host the way this PC reaches it, in Martlet with its output and Cancel
    /// in a run window: over SSH, on this PC's Docker Desktop, or through Martlet on that computer (its paired connection).
    /// Never in a console, and never by asking you to type it there.</summary>
    private void LaunchOnHost(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers = null) =>
        RunHostActionAsync(host, action, answers).Forget();

    /// <summary><see cref="LaunchOnHost"/>, awaitable: returns the run's summary, or null when it stopped or could not run.
    /// <paramref name="confirmed"/>: the owner already agreed to a role removal, so its run window doesn't ask again.</summary>
    private async Task<string?> RunHostActionAsync(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers = null,
        bool confirmed = false)
    {
        try
        {
            if (store is null) return null;
            if (!host.CanLaunch)
            {
                ActionText.Text = $"Set how Martlet signs in to {host.HostId} over SSH on the Devices map, or choose Through Martlet on that computer.";
                ShowDevice("host:" + host.HostId);
                return null;
            }
            var local = host.Method == HostSetupMethod.ThisPcDocker;
            // One graphics card for each Thinking model: adding Deep thinking beside the host's Thinking asks first.
            if (answers is null && action.Verb == HostVerb.Add && action.Role == HostRoles.DeepThinking &&
                DeepThinkingFit.SharedCard(host.HostId, HardwareStore?.Find(host.HostId), hostChecks.GetValueOrDefault(host.HostId)?.Offers)
                    is { } shared &&
                !ConfirmationDialog.Confirm(this, shared + " Add the Thinking pool role there anyway?", "The Thinking pool shares a graphics card",
                    yes: "_Add anyway", no: "_Cancel", questionId: "DeepThinkingShareQuestion"))
            {
                ActionText.Text = $"The Thinking pool role wasn't added on {host.HostId}.";
                return null;
            }
            // Martlet's automatic host update leaves this host to this run rather than colliding with it (and reporting it busy).
            using var updating = action.Verb == HostVerb.Update ? hostUpdates.Begin(UpdateKey(host)) : null;
            ActionText.Text = $"Running {HostSetupCommands.Engine(action)} on {(local ? "this PC" : host.HostId)}" +
                (host.Method == HostSetupMethod.Agent ? " through Martlet there" : "") + ". The progress window shows details.";
            // Adding listening preselects what suits the computer: on this PC whisper on the graphics card or the processor by
            // what already runs there; elsewhere whisper or Parakeet by its hardware; and the Parakeet model for your language.
            var recommended = answers is null && action.Verb == HostVerb.Add && action.Role == HostRoles.Stt
                ? ListeningAdvisor.HostAnswers(HardwareStore?.Find(host.HostId), System.Globalization.CultureInfo.CurrentUICulture,
                    local ? (await ListeningAdviceAsync()).Answers() : null)
                // Deep thinking's thinks at once: what fits on its graphics card beside the host's other roles, for each model.
                : answers is null && action.Verb == HostVerb.Add && action.Role == HostRoles.DeepThinking
                ? DeepThinkingFit.Recommend(HardwareStore?.Find(host.HostId), hostChecks.GetValueOrDefault(host.HostId)?.Offers)
                : null;
            var done = await HostActions.RunAsync(this, store.DataDirectory, host.Target(Version), host.SshHostKey, action, answers, recommended,
                host.Pairing, confirmed);
            if (done is not null && action.Verb == HostVerb.Update) HostUpdateSettled(UpdateKey(host));
            if (closing) return done;
            ActionText.Text = done is null ? $"{HostSetupCommands.Engine(action)} on {host.HostId} stopped. See the progress window for details." : $"{host.HostId}: {done}";
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
        if (parts[1] == HomeAssistantHosts.Role)
        {
            if (add) InstallHomeAssistantAsync(host).Forget();
            else LaunchOnHost(host, HostAction.Remove(HomeAssistantHosts.Role));
            return;
        }
        var role = HostRoles.Get(parts[1]);
        if (add && CannotHand(host.HostId, role.Kind, role.Job) is { } cannot)
        {
            ActionText.Text = $"{role.Name} can't be set up on {host.HostId}: {cannot}";
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
        var shared = networkState.Roster?.Host(host.HostId) is { Removed: false };
        if (!ConfirmationDialog.Confirm(this,
                $"Forget {host.HostId}? Martlet will stop using it on this PC and remove its pairing." +
                (impact.Count > 0 ? $" It handles: {string.Join(" ", impact)}" : "") +
                (shared
                    ? " It stays in your Martlet network, so your other computers keep using it and this PC won't pair with it again automatically. To remove it everywhere, use Remove from network instead."
                    : $" To remove this PC from the host too, revoke {host.Pairing.DeviceId} in the host console."),
                "Forget host"))
            return;
        await HostTaskAsync(async token =>
        {
            if (shared) IgnoreNetworkHost(host.HostId);
            var stranded = await ForgetPairingAsync(host, token);
            ActionText.Text = $"Forgot {host.HostId}." +
                (shared ? " Your other computers still use it." : "") +
                (stranded.Count > 0 ? $" Nobody handles {string.Join(" or ", stranded)} now. Choose another device in Companion or Devices." : "");
        });
        await RefreshHomeAsync();
    }

    /// <summary>Forgets a host on this PC: jobs it did go back to the choice kept aside for them first (so no route is left
    /// pointing at it), then its pairing, lip-sync assignment and secret go. Returns the jobs nobody does now.</summary>
    private async Task<IReadOnlyList<string>> ForgetPairingAsync(PairedHost host, CancellationToken token)
    {
        var inCharge = homeAvatar?.RemoteHost?.HostId == host.HostId;
        var stranded = new List<string>();
        foreach (var job in HostJob.All.Where(j => NetworkMap.JobHost(homeSettings, j.Role) == host.HostId))
        {
            // A job on a host a friend shared was this PC's own choice: it goes back without changing your shared plan, and
            // this PC follows the plan again on its next check.
            if (store is not null && JobSavedRoute.Load(store.DataDirectory, job.SavedFile) is { } saved) await HandBackAsync(job, saved, record: !host.Shared);
            else stranded.Add(job.Job);
        }
        await Pairings().ForgetAsync(host.HostId, token);
        hostChecks.Remove(host.HostId);
        hostReleases.Remove(host.HostId);
        // A host a friend shares was never in your shared plan, so forgetting it changes nothing there.
        if (!host.Shared) ForgetClusterHost(host.HostId);
        if (inCharge)
        {
            if (!host.Shared) RecordClusterJob(ClusterJobs.LipSync, new(null, false));
            if (avatar.IsShowing) await avatar.UseHostAsync(null, token);
        }
        return stranded;
    }

    private async Task HostTaskAsync(Func<CancellationToken, Task> action)
    {
        if (store is null || setupService is null || closing) return;
        ChangeTurns.Turn? turn = null;
        try
        {
            turn = await ChangeTurnAsync();
            await action(lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            turn?.Dispose();
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
            case NodeAction.AddComputer: OpenHosts(0); break;
            case NodeAction.ManageHost: OpenHosts(1, FindHost(argument)); break;
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
            case NodeAction.ChangeRole: ChangeHostRole(argument); break;
            case NodeAction.RemoveRole: RunHostRole(argument, add: false); break;
            case NodeAction.HostStatus: if (FindHost(argument) is { } host) LaunchOnHost(host, HostAction.Status); break;
            case NodeAction.UpdateHost:
                if (FindHost(argument) is { } outdated)
                {
                    hostUpdates.ClearNote(outdated.HostId);
                    LaunchOnHost(outdated, HostAction.Update);
                }
                break;
            case NodeAction.ForgetHost: ForgetHostAsync(argument).Forget(); break;
            case NodeAction.PrepareHost: if (FindHost(argument) is { } prepare) OpenPrepare(prepare, PrepareStart.Status); break;
            case NodeAction.RebootHost: if (FindHost(argument) is { } reboot) OpenPrepare(reboot, PrepareStart.Reboot); break;
            case NodeAction.ShutdownHost: if (FindHost(argument) is { } shutdown) OpenPrepare(shutdown, PrepareStart.Shutdown); break;
            case NodeAction.WakeHost: if (FindHost(argument) is { } wake) OpenPrepare(wake, PrepareStart.Wake); break;
            case NodeAction.PrepareComputer: OpenPrepare(null, PrepareStart.Status); break;
            case NodeAction.MakeHostPc: RequestRoleAsync(argument, DeviceRole.Host).Forget(); break;
            case NodeAction.MakeCompanionPc: RequestRoleAsync(argument, DeviceRole.Companion).Forget(); break;
            case NodeAction.OutsideAccess: if (FindHost(argument) is { } exposed) EditHostExposureAsync(exposed).Forget(); break;
        }
    }

    private void OpenHosts(int step, PairedHost? manage = null, NearbyMartlet? connect = null)
    {
        if (store is null || setupService is null || closing) return;
        new HostsWindow(new AvatarProfileStore(store.DataDirectory), setupService, step, manage, connect) { Owner = this }.ShowDialog();
        RefreshHomeAsync().Forget();
    }

    /// <summary>Sets up and pairs Martlet's host service on this PC in one click, in a run window; with
    /// <paramref name="andThen"/> it then continues straight into handing a job to it ("host:ID"). Whisper and F5 chain
    /// their install into the same window instead (<see cref="SetUpJobHereAsync"/>). Started again (or by another flow)
    /// while one runs, it waits for that setup and pairing instead of repeating them, then carries on with its own next step.</summary>
    private async Task SetUpThisPcHostAsync(Func<string, Task>? andThen = null)
    {
        if (store is null || setupService is null || closing) return;
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
            NodeHealth.Configuring => "AccentBrush",
            _ => "MutedBrush"
        });
        return dot;
    }
}
