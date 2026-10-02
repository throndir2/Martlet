using System.Windows;
using System.Windows.Automation;
using System.Windows.Shapes;

namespace Martlet.Desktop;

/// <summary>Talking without the talk window: Home's Start listening (and the notification-area menu's) runs the conversation
/// hidden, Home's indicator says whether Martlet is listening, and Settings › Startup and closing can have Martlet show the
/// character and start listening as it starts. The talk window only shows the history; closing it while Martlet listens hides it.</summary>
public partial class MainWindow
{
    private string? listeningSignature;
    private bool listeningTwinkle;
    /// <summary>Home has no problem to fix first, so its main talk action shows as the primary button.</summary>
    private bool stageReady;

    /// <summary>The running conversation, created (hidden) when there is none yet; null while Martlet can't talk right now.</summary>
    private LiveConversationWindow? ConversationSession()
    {
        if (openConversation is { } open) return open;
        if (conversation is null || closing || saving || model?.IsRunning == true) return null;
        var window = new LiveConversationWindow(setupService!, setupOperations, conversation, audioSessionEvents, voiceIdentity: voiceIdentity,
            preferences: Talk, videoAddress: visionAddress)
            { Owner = this, Support = support };
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
            ActionText.Text = "Martlet keeps listening without the talk window. Stop listening on Home or from the notification-area icon.";
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

    /// <summary>Starts always listening without opening the talk window (it keeps running hidden until you show it).</summary>
    private void StartListening()
    {
        if (closing) return;
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

    /// <summary>Settings › When Martlet starts, show the character and start listening.</summary>
    private async Task StartCompanionAsync()
    {
        await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        if (closing) return;
        if (Talk.HandsFree) StartListening();
        ErrorLog.Info(Talk.HandsFree ? "Martlet started with the character and listening." : "Martlet started with the character; push-to-talk needs the talk window.");
    }

    private void StartCompanion_Changed(object sender, RoutedEventArgs e)
    {
        if (changingBackgroundChoice) return;
        background = background with { StartCompanion = StartCompanionCheck.IsChecked == true };
        RenderBackground(background.Save(store?.DataDirectory) ? null : "Couldn't save this choice. It applies until Martlet closes.");
    }

    /// <summary>Home's Start listening / Stop listening button and its listening indicator.</summary>
    private void RenderListening()
    {
        if (closing || ListenButton is null) return;
        var talk = openConversation;
        var handsFree = talk?.HandsFree ?? Talk.HandsFree;
        var started = talk is { ListeningStarted: true };
        var (text, problem) = talk?.ListeningStatus ?? ("Not listening", false);
        var show = handsFree && conversation is not null;
        var enabled = started || !saving && model?.IsRunning != true;
        var signature = $"{show}|{enabled}|{started}|{problem}|{stageReady}|{handsFree}|{text}";
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
    }
}
