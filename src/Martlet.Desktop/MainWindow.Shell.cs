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

/// <summary>The app shell's shared state, start and release. Its parts are in their own files: the status line
/// (MainWindow.StatusLine.cs), navigation (MainWindow.Navigation.cs), the device role and welcome tour (MainWindow.RoleTour.cs),
/// the stage-aware home (MainWindow.Home.cs), the host dashboard (MainWindow.HostDashboard.cs, with its steps in
/// Pages\HostDashboard), the devices map (MainWindow.DevicesMap.cs) and who does what (MainWindow.WhoDoesWhat.cs).</summary>
public partial class MainWindow
{
    private static StepHelp StepTip(string topic, string text, string id, string? wiki = null) => new(topic, text, id, wiki);
    private sealed record MapElement(NetworkNode Node, Button Card, System.Windows.Shapes.Path? Track, System.Windows.Shapes.Path? Flow, Ellipse? Ring);

    private static readonly string Version = AppVersions.Current;
    private DeviceRole? deviceRole;
    private MachineInfo machine = MachineInfo.Unknown;
    private AppSettings? homeSettingsValue;
    /// <summary>The newest loaded settings. Setting them also lets screen looks follow Companion › Replies › Personal content
    /// (<see cref="ScreenGlancer.SeesPrivateWindows"/>).</summary>
    private AppSettings? homeSettings
    {
        get => homeSettingsValue;
        set
        {
            homeSettingsValue = value;
            ScreenGlancer.SeesPrivateWindows = GenerationSettings.Personal(value?.Generation) == PersonalContent.Everything;
        }
    }
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
    private readonly Dictionary<Panel, string> previousSteps = [];
    private int tourStep;
    /// <summary>This PC's own host service as the last read found it (<see cref="LocalHostService"/>); null until the first read.</summary>
    private LocalHostServiceState? hostState;
    private Task? hostProbe;
    private bool hostProbeAgain;
    private DateTime hostProbedAt;
    private string? hostStateSignature;
    /// <summary>When Show a pairing code last paired a computer, until the host's network names it.</summary>
    private DateTime? hostPairedAt;
    internal static readonly TimeSpan HostProbeInterval = TimeSpan.FromSeconds(30);
    private readonly System.Windows.Threading.DispatcherTimer hostProbeTimer = new() { Interval = HostProbeInterval };
    private WindowsVirtualization? virtualization;
    private bool refreshingHome;
    /// <summary>A refresh was asked for while another ran: read settings.json again when it ends.</summary>
    private bool refreshHomeAgain;
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
        deviceRole = store is null ? DeviceRole.Companion : LoadPcRole();
        ErrorLog.Info($"This PC runs as a {(Role == DeviceRole.Host ? "host" : "companion")} PC{(deviceRole is null ? " (not chosen yet)" : "")}. " +
            PcRoleWhere());
        InitializeHealth();
        // The host dashboard reads this PC's host service by itself: at start, every 30 seconds while the window shows and
        // whenever it shows again, so finished steps tick without a button. It keeps reading while setup runs work, so a step
        // another run finishes (Docker Desktop starting, for example) ticks and the next ones open up.
        hostProbeTimer.Tick += (_, _) =>
        {
            FollowPcRole();
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
        // A Windows user who hasn't chosen yet gets the welcome tour, unless another Windows user made this PC a host PC.
        if (deviceRole is null || Role == DeviceRole.Companion && !roleChosenHere) ShowTour(TourWelcome);
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
        var working = HostDashboard.DockerUnderway;
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
        RenderDevicePages();
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
        RenderDevicePages();
        // Reading's model pills turn off the NVIDIA-only model once this PC's graphics cards are known.
        if (openTab == CompanionTab.Reading && !tabEdited) RenderTab();
        if (HostsHere) CheckThisPcHostAsync().Forget();
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
