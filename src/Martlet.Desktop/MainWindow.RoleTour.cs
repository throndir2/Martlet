using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// The app shell's device role and welcome tour (see MainWindow.Shell.cs for the shell's shared state).
public partial class MainWindow
{
    private void SetRole(DeviceRole role)
    {
        var previous = deviceRole;
        deviceRole = role;
        if (store is not null)
        {
            try
            {
                PcRole.Save(store.DataDirectory, Pc!, role);
                roleChosenHere = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ActionText.Text = "Couldn't save this choice: check access to Martlet's data folder and " + Pc!.Where + ".";
                ErrorLog.Warn($"Couldn't save that this PC is a {(role == DeviceRole.Host ? "host" : "companion")} PC in {Pc.Where}", error);
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
        RenderDevicePages();
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
        // A host PC doesn't talk, so it has no conversations of its own to show, and its companions' Thinking requests show on
        // the companion PCs that send them.
        NavConversations.Visibility = host || conversationHistory is null ? Visibility.Collapsed : Visibility.Visible;
        RailAdvanced();
        ShowMode();
        UseCompanionButton.IsEnabled = host;
        UseHostButton.IsEnabled = !host;
        if (host && (NavCompanion.IsChecked == true || NavConversations.IsChecked == true || NavThinkingRequests.IsChecked == true)) Navigate(NavHome);
    }

    /// <summary>The three ways a PC runs Martlet: a companion PC, a companion PC that also runs a host service for your
    /// computers (its Home then checks that host service as a host PC's does), or a host PC.</summary>
    private void ShowMode()
    {
        var host = Role == DeviceRole.Host;
        var both = !host && ThisPcHost() is not null;
        ModeText.Text = host ? "Host PC" : both ? "Companion PC + host" : "Companion PC";
        RoleText.Text = host
            ? "This PC is a Martlet host that does heavier tasks for your main PC."
            : both
            ? "This is your companion PC, where you talk with Martlet; it also runs a host service that Home checks."
            : "This is your companion PC, where you talk with Martlet.";
        RoleWhereText.Text = PcRoleWhere();
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
        var step = new[] { HostDashboard.DockerStep(state), HostDashboard.ServiceStep(state) }.FirstOrDefault(s => !s.Done);
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
        ActionText.Text = hostState is { } state ? HostDashboard.ServiceSentence(state) : "This PC's host service couldn't be read.";
    }

    private void UseCompanion_Click(object sender, RoutedEventArgs e) { SetRole(DeviceRole.Companion); Navigate(NavHome); }
    private void UseHost_Click(object sender, RoutedEventArgs e) { SetRole(DeviceRole.Host); Navigate(NavHome); }
    private void ReplayTour_Click(object sender, RoutedEventArgs e) => ShowTour(TourWelcome);

    // The welcome wizard (MainWindow.Welcome.cs). A host stops after the network step. The key step shows only when Thinking
    // goes online. The character step (MainWindow.Welcome.Character.cs) comes last. The wizard installs nothing until its suggestions are accepted and confirmed.
    private StackPanel[] TourPanels => Role == DeviceRole.Host && deviceRole is not null ? [TourWelcome, TourNetwork]
        : welcomeNeedsKey ? [TourWelcome, TourNetwork, TourSpecs, TourPreference, TourPlan, TourKey, TourCharacter]
        : [TourWelcome, TourNetwork, TourSpecs, TourPreference, TourPlan, TourCharacter];

    private void ShowTour(StackPanel panel)
    {
        var appearing = Tour.Visibility != Visibility.Visible;
        Tour.BeginAnimation(OpacityProperty, null);
        Tour.Opacity = 1;
        Tour.Visibility = Visibility.Visible;
        var panels = TourPanels;
        var step = Math.Max(0, Array.IndexOf(panels, panel));
        tourStep = step;
        foreach (var candidate in new[] { TourWelcome, TourNetwork, TourSpecs, TourPreference, TourPlan, TourKey, TourCharacter })
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
        else KeepRoleChoice();
        HideTour();
    }
}
