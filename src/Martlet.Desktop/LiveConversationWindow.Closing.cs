using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Windows lock, memory reports, Home Assistant and tool confirmations, and closing.
public partial class LiveConversationWindow
{
    // ---------- Windows lock, memory and closing ----------

    private void SessionSwitch(bool value)
    {
        locked = value;
        // Revoke immediately on the OS signal, not after a potentially busy dispatcher.
        if (value) controller.Revoke("conversation.locked");
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            controller.SetControls(false, false, locked);
            if (value)
            {
                mouseHeld = keyHeld = false;
                if (pendingText is not null) pendingMessage?.AddNote("Not sent.");
                pendingText = null;
                pendingMessage = null;
                listening = false;
                StopListening(keepHeard: false);
                answering = null;
                if (watching) StopWatching(null);
                if (Messages.Count > 0) AddNote("Windows was locked, so Martlet stopped and started a fresh conversation.");
            }
            else
            {
                notice = null;
                StartLive();
            }
            RenderActions();
        });
    }

    // Opens a shown picture full size (Esc closes it).
    private void Picture_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessage { Picture: { } picture } message }) return;
        var viewer = new Window
        {
            Title = message.Text, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Min(SystemParameters.WorkArea.Width * 0.8, 1100), Height = Math.Min(SystemParameters.WorkArea.Height * 0.85, 1100),
            Content = new System.Windows.Controls.Image { Source = picture, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(8) }
        };
        viewer.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        AutomationProperties.SetAutomationId(viewer, "LivePictureViewer");
        viewer.KeyDown += (_, key) => { if (key.Key == Key.Escape) viewer.Close(); };
        viewer.Show();
    }

    // Raised off the dispatcher when Martlet shows a picture: it joins the history as the character's, with its title.
    private void PictureShown(ShownPicture picture) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        var image = PictureView.Decode(picture.Image);
        if (image is null)
        {
            AddNote($"Couldn't show “{picture.Title}”.");
            return;
        }
        Messages.Add(new ChatMessage(ChatRole.Martlet, picture.Title + (picture.Fixture ? " (FIXTURE - NOT AI)" : ""), $"{CharacterName} · {DateTime.Now:t}")
        {
            Picture = image
        });
        LastShownPicture = picture.Key;
    });

    /// <summary>The creation key of the last picture shown here (MCP: LivePicture).</summary>
    internal string? LastShownPicture { get; private set; }

    // Raised off the dispatcher once background remembering finishes for an exchange.
    private void MemoryCaptured(MemoryCaptureReport report) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        var text = report.Failure is { } failure
            ? Lead("Couldn't update memory", MemoryFailureReason(failure, controller.Configuration))
            : string.Join("  ", report.Changes!.Select(change => change.Kind switch
            {
                MemoryCaptureKind.Remember => "Remembered: ",
                MemoryCaptureKind.Update => "Updated memory: ",
                _ => "Forgot: "
            } + change.Content + (change.Person is { } person ? $" ({person})" : "")));
        if (lastReply is not null) lastReply.AddNote(text);
        else AddNote(text);
    });

    // Raised off the dispatcher when a check-in that does the reply's work after an exchange couldn't (once for each problem).
    private void AfterExchangeFailed(string why) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        var text = "Couldn't do what was asked after the reply: " + why + ".";
        if (lastReply is not null) lastReply.AddNote(text);
        else AddNote(text);
    });
    // Raised off the dispatcher once learning names changed voices from an exchange.
    private void VoicesNamed(IReadOnlyList<Martlet.Core.Speakers.VoiceUpdateResult> learned) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        var text = string.Join("  ", learned.Select(l => l.Text));
        if (lastReply is not null) lastReply.AddNote(text);
        else AddNote(text);
    });

    // A reply or glance switched how chatty Martlet is (while it decides): looks and what this PC plays follow the new level
    // from now on, and the history says so.
    private void ChattinessDecided(Chattiness before, Chattiness level) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        pacer?.Retune(ChattinessNow);
        chattinessAt = DateTime.Now;
        if (SavedChoice == ChattinessChoice.MartletDecides) AddNote(ChattinessSwitched(before, level, CharacterName));
        RenderActions();
    });

    /// <summary>The note the history gets when the character (<paramref name="character"/>, the persona's name) switches how
    /// chatty it is.</summary>
    internal static string ChattinessSwitched(Chattiness before, Chattiness level, string character = Martlet.Core.Speakers.CompanionNames.Default) => level switch
    {
        Chattiness.Quiet => $"{character} went quiet about what it sees and hears and speaks up only for something notable.",
        Chattiness.Chatty => $"{character} got chattier about what it sees and hears.",
        _ => before == Chattiness.Quiet ? $"{character} is less quiet again about what it sees and hears."
            : $"{character} calmed down about what it sees and hears."
    };

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // The window only shows the conversation: while Martlet listens or watches, closing it hides it and Martlet carries on.
        if (!ending && !closed && (!listenPaused || WatchingStarted || watching))
        {
            e.Cancel = true;
            Hide();
            HiddenToBackground?.Invoke();
            return;
        }
        listening = false;
        StopListening(keepHeard: false);
        StopAll("conversation.closed", keepContext: false);
        FailRemote(RemoteCantAnswer);
        if (remoteAnswering is { } answering) answering.Ask.Reply.TrySetResult(RemoteCantAnswer);
        remoteAnswering = null;
        // Ending the conversation ends its background work: nothing is brought up any more.
        controller.EndBackgroundWork();
        attentionWatcher.Stop();
        Task.Run(glancer.Release).Forget();
        Task.Run(video.Release).Forget();
        Gaze?.Stop();
        closed = true;
        generation++;
        timer.Stop();
        warmup?.Dispose();
        warmup = null;
        reader?.Dispose();
        reader = null;
        sessionEvents.LockedChanged -= SessionSwitch;
        controller.MemoryCaptured -= MemoryCaptured;
        controller.AfterExchangeFailed -= AfterExchangeFailed;
        controller.PictureShown -= PictureShown;
        controller.VoicesNamed -= VoicesNamed;
        controller.ChattinessDecided -= ChattinessDecided;
        if (controller.SoundDigest is { } digest) digest.On = false;
        if (controller.PcActivity is { } activity) activity.On = false;
        controller.TouchesSent -= TouchesSent;
        homeQuestion?.TrySetResult(false);
        if (controller.Home is { } smartHome && smartHome.Confirm == ConfirmHomeAsync) smartHome.Confirm = null;
    }

    private TaskCompletionSource<bool>? homeQuestion;

    /// <summary>Asks above the message box before a request about a lock, door, garage, gate, alarm or valve goes to Home
    /// Assistant. Stop, Close or the timeout answer no.</summary>
    private Task<bool> ConfirmHomeAsync(string question, CancellationToken token) => Dispatcher.InvokeAsync(() =>
    {
        if (closed || token.IsCancellationRequested) return Task.FromResult(false);
        homeQuestion?.TrySetResult(false);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        homeQuestion = answer;
        HomeQuestionText.Text = question;
        HomeQuestionPanel.Visibility = Visibility.Visible;
        Motion.Enter(HomeQuestionPanel, dy: 10);
        Reveal();
        if (IsActive) HomeNoButton.Focus();
        var registration = token.Register(() => answer.TrySetResult(false));
        answer.Task.ContinueWith(_ =>
        {
            registration.Dispose();
            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(homeQuestion, answer)) return;
                homeQuestion = null;
                HomeQuestionPanel.Visibility = Visibility.Collapsed;
            });
        }, TaskScheduler.Default);
        return answer.Task;
    }).Task.Unwrap();

    private void HomeYes_Click(object sender, RoutedEventArgs e) => homeQuestion?.TrySetResult(true);
    private void HomeNo_Click(object sender, RoutedEventArgs e) => homeQuestion?.TrySetResult(false);

    private ToolApprovalRequest? shownApproval;

    /// <summary>Shows the MCP tool call waiting for an answer above the message box. The request declines itself when the
    /// reply stops or after 60 seconds.</summary>
    private void RenderToolApproval()
    {
        if (closed || ToolApprovalPanel is null) return;
        if (controller.Tools?.PendingApproval is not { Answer.IsCompleted: false } request)
        {
            shownApproval = null;
            ToolApprovalPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var left = Math.Max(0, (int)Math.Ceiling((request.Expires - clock.GetUtcNow()).TotalSeconds));
        ToolApprovalTitle.Text = request.Title;
        ToolApprovalText.Text = $"{request.Question} Automatically denied in {left} s.";
        if (ReferenceEquals(shownApproval, request)) return;
        shownApproval = request;
        ToolApprovalArguments.Text = request.Arguments;
        ToolAlwaysButton.Visibility = request.AllowAlways ? Visibility.Visible : Visibility.Collapsed;
        ToolApprovalPanel.Visibility = Visibility.Visible;
        Motion.Enter(ToolApprovalPanel, dy: 10);
        Reveal();
    }

    /// <summary>A question that needs your answer shows the window when it runs hidden in the background.</summary>
    private void Reveal()
    {
        if (closed || IsVisible) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void AnswerTool(ToolApprovalChoice choice)
    {
        if (shownApproval is { } request) controller.Tools?.Answer(request, choice);
        RenderToolApproval();
    }

    private void ToolAllow_Click(object sender, RoutedEventArgs e) => AnswerTool(ToolApprovalChoice.AllowOnce);
    private void ToolAlways_Click(object sender, RoutedEventArgs e) => AnswerTool(ToolApprovalChoice.AlwaysAllow);
    private void ToolDeny_Click(object sender, RoutedEventArgs e) => AnswerTool(ToolApprovalChoice.Deny);
}
