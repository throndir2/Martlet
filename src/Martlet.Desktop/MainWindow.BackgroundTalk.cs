using System.Windows;
using System.Windows.Automation;
using System.Windows.Shapes;

namespace Martlet.Desktop;

/// <summary>Talking without the talk window: Home's Start listening and Start watching (and the notification-area menu's) run the
/// conversation hidden, Home's indicators say whether Martlet is listening and watching, and Settings › Startup and closing can
/// have Martlet show the character and start listening (and watching) as it starts. The talk window only shows the history;
/// closing it while Martlet listens or watches hides it.</summary>
public partial class MainWindow
{
    private string? listeningSignature;
    private bool listeningTwinkle, watchingTwinkle;
    /// <summary>Home has no problem to fix first, so its main talk action shows as the primary button.</summary>
    private bool stageReady;

    /// <summary>The running conversation, created (hidden) when there is none yet; null while Martlet can't talk right now, and
    /// always on a Martlet host.</summary>
    private LiveConversationWindow? ConversationSession()
    {
        if (openConversation is { } open) return open;
        if (conversation is null || closing || saving || model?.IsRunning == true || Role == DeviceRole.Host) return null;
        var window = new LiveConversationWindow(setupService!, setupOperations, conversation, audioSessionEvents, voiceIdentity: voiceIdentity,
            preferences: Talk, videoAddress: visionAddress)
            { Owner = this, Support = support, Gaze = avatar.Gaze };
        if (!IsVisible) window.UseOwnTaskbarButton();
        window.Closed += async (_, _) =>
        {
            if (!ReferenceEquals(openConversation, window)) return;
            openConversation = null;
            RenderConversationButton();
            RenderListening();
            UpdateTray();
            if (!closing) await RefreshAsync();
        };
        window.HiddenToBackground += () =>
        {
            if (closing) return;
            ActionText.Text = (window.ListeningStarted, window.WatchingStarted) switch
            {
                (true, true) => "Martlet keeps listening and watching without the talk window. Stop them on Home or from the notification-area icon.",
                (false, true) => "Martlet keeps watching without the talk window. Stop watching on Home or from the notification-area icon.",
                _ => "Martlet keeps listening without the talk window. Stop listening on Home or from the notification-area icon."
            };
            UpdateTray();
        };
        openConversation = window;
        RenderConversationButton();
        UpdateTray();
        return window;
    }

    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (openConversation is { ListeningStarted: true } talk)
        {
            talk.ToggleListening();
            ActionText.Text = "Martlet stopped listening.";
        }
        else StartListening();
        RenderListening();
        UpdateTray();
    }

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        if (openConversation is { WatchingStarted: true } talk)
        {
            talk.StopWatchingNow();
            ActionText.Text = "Martlet stopped watching.";
        }
        else StartWatching();
        RenderListening();
        UpdateTray();
    }

    /// <summary>Starts always listening without opening the talk window (it keeps running hidden until you show it).</summary>
    private void StartListening()
    {
        if (closing) return;
        if (Role == DeviceRole.Host)
        {
            ActionText.Text = HostHasNoCompanionText;
            return;
        }
        if (ConversationSession() is not { } talk)
        {
            ActionText.Text = "Martlet can't listen right now. Try again in a moment.";
            return;
        }
        if (!talk.IsVisible) talk.StartInBackground();
        talk.ListenWhenReady();
        RenderListening();
        UpdateTray();
    }

    /// <summary>Starts watching (vision) without opening the talk window (it keeps running hidden until you show it).</summary>
    private void StartWatching()
    {
        if (closing) return;
        if (Role == DeviceRole.Host)
        {
            ActionText.Text = HostHasNoCompanionText;
            return;
        }
        if (ConversationSession() is not { } talk)
        {
            ActionText.Text = "Martlet can't watch right now. Try again in a moment.";
            return;
        }
        if (!talk.IsVisible) talk.StartInBackground();
        talk.WatchWhenReady();
        RenderListening();
        UpdateTray();
    }

    /// <summary>Settings › When Martlet starts, show the character and start listening (and watching, while vision is on). Only a
    /// companion PC does this.</summary>
    private async Task StartCompanionAsync()
    {
        if (Role != DeviceRole.Companion) return;
        await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        if (closing) return;
        if (Talk.HandsFree) StartListening();
        if (Talk.Watch) StartWatching();
        ErrorLog.Info((Talk.HandsFree ? "Martlet started with the character and listening" : "Martlet started with the character; push-to-talk needs the talk window") +
            (Talk.Watch ? ", and watching." : "."));
    }

    private const string HostHasNoCompanionText =
        "This PC is a Martlet host, so it doesn't talk, listen, watch or show the character. Use your companion PC, or choose Use as my companion PC in Settings.";

    /// <summary>This PC just became a Martlet host: it ends the conversation (listening and vision stop with it) and hides the
    /// character. Their saved choices stay as they are, so they come back if this PC is your companion PC again.</summary>
    private async Task StopCompanionForHostAsync()
    {
        messaging.Stop();
        var talking = openConversation is not null;
        openConversation?.End();
        var showing = avatar.IsShowing;
        var hidden = !showing || await StopAvatarSafelyAsync();
        UpdateCharacterButton();
        RenderListening();
        UpdateTray();
        if (closing || !talking && !showing) return;
        var stopped = (talking, showing) switch
        {
            (true, true) => "ended the conversation and hid the character",
            (true, false) => "ended the conversation",
            _ => "hid the character"
        };
        ErrorLog.Info($"This PC became a Martlet host, so Martlet {stopped} here.");
        if (hidden) ActionText.Text = $"This PC is now a Martlet host, so Martlet {stopped}. Your companion choices are kept for when it's your companion PC again.";
    }

    private void StartCompanion_Changed(object sender, RoutedEventArgs e)
    {
        if (changingBackgroundChoice) return;
        background = background with { StartCompanion = StartCompanionCheck.IsChecked == true };
        RenderBackground(background.Save(store?.DataDirectory) ? null : "Couldn't save this choice. It applies until Martlet closes.");
    }

    /// <summary>Home's Start listening / Stop listening and Start watching / Stop watching buttons and their indicators.</summary>
    private void RenderListening()
    {
        if (closing || ListenButton is null) return;
        var talk = openConversation;
        var handsFree = talk?.HandsFree ?? Talk.HandsFree;
        var started = talk is { ListeningStarted: true };
        var (text, problem) = talk?.ListeningStatus ?? ("Not listening", false);
        var show = handsFree && conversation is not null;
        var idle = !saving && model?.IsRunning != true;
        var enabled = started || idle;
        var visionOn = talk?.VisionOn ?? Talk.Watch;
        var watchStarted = talk is { WatchingStarted: true };
        var (watchText, watchProblem) = talk?.WatchingStatus ?? ("Not watching", false);
        var showWatch = visionOn && conversation is not null;
        var watchEnabled = watchStarted || idle;
        var signature = $"{show}|{enabled}|{started}|{problem}|{stageReady}|{handsFree}|{text}|" +
            $"{showWatch}|{watchEnabled}|{watchStarted}|{watchProblem}|{watchText}";
        if (signature == listeningSignature) return;
        listeningSignature = signature;

        ListenButton.Visibility = ListeningIndicator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ListenButton.IsEnabled = enabled;
        ListenButton.Content = started ? "Stop _listening" : "Start _listening";
        AutomationProperties.SetName(ListenButton, started ? "Stop listening" : "Start listening");
        if (stageReady && show && !started) ListenButton.SetResourceReference(StyleProperty, "PrimaryButton");
        else ListenButton.ClearValue(StyleProperty);
        if (stageReady && !show) ConversationButton.SetResourceReference(StyleProperty, "PrimaryButton");
        else ConversationButton.ClearValue(StyleProperty);

        ListeningStatusText.Text = text;
        var live = started && !problem;
        ListeningDot.SetResourceReference(Shape.FillProperty, problem ? "WarningBrush" : live ? "SuccessBrush" : "MutedBrush");
        if (live != listeningTwinkle)
        {
            listeningTwinkle = live;
            if (live) Motion.Twinkle(ListeningDot, 0.9);
            else
            {
                ListeningDot.BeginAnimation(OpacityProperty, null);
                ListeningDot.Opacity = 1;
            }
        }

        // Watching (Companion › Vision) has its own button beside listening: either runs without the other.
        WatchButton.Visibility = WatchingIndicator.Visibility = showWatch ? Visibility.Visible : Visibility.Collapsed;
        WatchButton.IsEnabled = watchEnabled;
        WatchButton.Content = watchStarted ? "Stop _watching" : "Start _watching";
        AutomationProperties.SetName(WatchButton, watchStarted ? "Stop watching" : "Start watching");
        WatchingStatusText.Text = watchText;
        var watchingNow = talk is { IsWatching: true };
        WatchingDot.SetResourceReference(Shape.FillProperty, watchProblem ? "WarningBrush" : watchingNow ? "SuccessBrush" : "MutedBrush");
        if (watchingNow != watchingTwinkle)
        {
            watchingTwinkle = watchingNow;
            if (watchingNow) Motion.Twinkle(WatchingDot, 0.9);
            else
            {
                WatchingDot.BeginAnimation(OpacityProperty, null);
                WatchingDot.Opacity = 1;
            }
        }
    }
}
