using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Shapes;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// What the window shows: the status lines and their details.
public partial class LiveConversationWindow
{
    // ---------- what the window shows ----------

    /// <summary>Refresh context: Martlet forgets the exchanges it kept in mind (and what it said lately), so the next
    /// reply starts fresh. Listening, vision and the messages on screen carry on unchanged.</summary>
    private void RefreshContext_Click(object sender, RoutedEventArgs e)
    {
        if (!controller.ForgetContext()) return;
        AddNote($"Context refreshed, so {CharacterName}'s next reply starts fresh.");
        RenderActions();
        InputText.Focus();
    }

    private void Observe()
    {
        if (owned is { } operation) Observe(operation);
        if (!closed) LevelMeter.Value = listener is { } live ? Math.Clamp((live.VoiceLevel + 60) / 50, 0, 1) : 0;
    }

    private void Observe(LiveConversationOperation operation)
    {
        if (closed) return;
        if (!ReferenceEquals(shown, operation))
        {
            shown = operation;
            heard = home = reply = null;
        }
        if (heard is null && operation.Transcript is { } said && !string.IsNullOrWhiteSpace(said))
            heard = Add(ChatRole.User, said.Trim(), operation.Heard?.Speaker is { Voice: { } voice } speaker
                ? $"{voice.DisplayName}{(speaker.Mine ? " (you)" : "")} (spoken)" : "You (spoken)");
        // What Home Assistant did or answered for this turn, above the reply.
        if (home is null && operation.HomeSummary is { } summary && !string.IsNullOrWhiteSpace(summary))
            home = Add(ChatRole.Note, summary.Trim(), "");
        var content = operation.Turn?.Content;
        var text = content?.Text?.Trim() ?? "";
        var refusal = content?.Refusal?.Trim() ?? "";
        // A reply to what always listening heard, or one Martlet started on its own, may be [pass]: nothing is shown until it
        // clearly isn't.
        if (operation.MayPass && LiveConversationController.MaybeSilent(text)) text = "";
        if (reply is null && (text.Length > 0 || refusal.Length > 0)) reply = lastReply = Add(ChatRole.Martlet, "", CharacterName);
        if (reply is not null) reply.Text = text.Length > 0 ? text : refusal;
        // What was sent so far; it is taken again with every request once the reply ends (Settle).
        if (reply is { Context: null }) KeepContext(reply, operation);
        var snapshot = operation.Turn?.Snapshot;
        if (Support is { } support)
            supportProjection.Observe(support, new(operation.Id, snapshot?.TurnId, operation.Status,
                snapshot is null ? operation.Transcription?.Provenance ?? EvidenceProvenance.Live :
                    snapshot.TextProvenance ?? EvidenceProvenance.NotRun,
                snapshot?.CommittedSegments ?? 0, snapshot?.QueuedSegments ?? 0));
    }

    private void RenderActions()
    {
        if (closed || SendButton is null || EmptyDetail is null) return;
        bool available = Available;
        bool held = mouseHeld || keyHeld;
        SendButton.IsEnabled = available && pendingText is null && !string.IsNullOrWhiteSpace(InputText.Text);
        Placeholder.Visibility = InputText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool pushToTalk = available && !preferences.HandsFree && MicrophoneUsable;
        PttButton.Visibility = pushToTalk || held ? Visibility.Visible : Visibility.Collapsed;
        // Keep a held button enabled until release; disabling it would lose capture and discard the recording.
        PttButton.IsEnabled = held || pushToTalk;
        var talkLabel = held || Recording ? "Release to send" : "Hold to talk";
        if (!Equals(PttButton.Content, talkLabel)) PttButton.Content = talkLabel;

        // Always listening starts and stops here (it is off when the window opens); the dot and the name say how it is going.
        MicChip.Visibility = available && preferences.HandsFree ? Visibility.Visible : Visibility.Collapsed;
        var started = !listenPaused;
        var micUsable = MicrophoneUsable;
        var micDown = listening && micProblem is not null;
        var cantListen = started ? !listening || listenProblem is not null : !micUsable;
        MicChip.IsEnabled = true;
        var micState = micDown ? "Mic unavailable" : cantListen ? "Can't listen" : listening ? "Listening" : "Not listening";
        MicLabel = started ? "Stop listening" : micUsable ? "Start listening" : "Can't listen";
        MicGlyph.Text = started && !micDown && !cantListen ? "\uE720" : "\uEC54";
        var offer = !started && micUsable;
        if (offer != micOffer)
        {
            micOffer = offer;
            if (offer) MicChip.SetResourceReference(StyleProperty, "PrimaryButton");
            else MicChip.ClearValue(StyleProperty);
        }
        MicDot.Visibility = offer ? Visibility.Collapsed : Visibility.Visible;
        MicDot.SetResourceReference(Shape.FillProperty, micDown || cantListen ? "WarningBrush" : "SuccessBrush");
        var micDetail = micDown ? Noted(micProblem!, "Martlet tries again soon; click to stop listening")
            : started && listenProblem is not null ? Noted(listenProblem, "Martlet listens when it can; click to stop listening")
            : started && !listening ? Noted(ListeningProblem(), "Martlet listens when it can; click to stop listening")
            : listening ? (listener is { Held: true }
                ? "Not listening while Martlet speaks, so it doesn't hear itself (click to stop listening)."
                : preferences.BargeIn ? "Martlet listens for you even while it speaks, so talk over it to stop it (click to stop listening)."
                : preferences.ReduceEcho ? "Martlet listens for you even while it speaks and answers when you pause (click to stop listening)."
                : "Martlet listens for you and answers when you pause (click to stop listening).")
            : micUsable ? "Click to have Martlet listen and answer when you pause, while you keep using the rest of Martlet."
            : ListeningProblem();
        MicChip.ToolTip = MicLabel + "\n" + micDetail;
        AutomationProperties.SetName(MicChip, micState + ". " + micDetail);

        // Watching starts and stops here too (it is off when the window opens); the dot and the name say how it is going.
        VisionChip.Visibility = available && preferences.Watch ? Visibility.Visible : Visibility.Collapsed;
        VisionChip.IsEnabled = true;
        var watchStarted = !watchPaused;
        var looking = watching && commentary is { OwnershipReleased: false };
        var visionState = looking ? "Looking" : watching ? "Watching" : visionProblem is not null ? "Can't see" : "Not watching";
        VisionLabel = watchStarted ? "Stop watching" : visionProblem is not null ? "Can't see" : "Start watching";
        VisionGlyph.Text = watchStarted ? "\uE890" : "\uED1A";
        VisionDot.SetResourceReference(Shape.FillProperty, watching ? "SuccessBrush"
            : visionProblem is not null || watchStarted ? "WarningBrush" : "MutedBrush");
        if (looking != twinkling)
        {
            twinkling = looking;
            if (looking) Motion.Twinkle(VisionDot, 0.9);
            else
            {
                VisionDot.BeginAnimation(OpacityProperty, null);
                VisionDot.Opacity = 1;
            }
        }
        var visionDetail = watching
            ? WatchingTip() +
              (noticing ? " It looks right away when a notification pops up or a taskbar button flashes." : "") +
              (lastCheck is { } checkedAt ? $" Last checked at {checkedAt:T}." : "") +
              (captureNote is { } why ? $" Full-screen capture is unavailable ({why.TrimEnd('.')}), so try borderless or windowed mode." : "") +
              " Click to stop watching."
            : watchStarted ? "Martlet watches once it's ready (click to stop watching)."
            : visionProblem is { } problem ? Noted(problem, "click to try again")
            : $"Click to have Martlet watch {SavedSource().Label} and now and then say something about it.";
        VisionChip.ToolTip = VisionLabel + "\n" + visionDetail;
        AutomationProperties.SetName(VisionChip, visionState + ". " + visionDetail);
        var visionLine = VisionLine();
        VisionStatusText.Text = visionLine;
        SetDetail(VisionStatusText, VisionDetail());
        VisionStatusText.Visibility = VisionChip.Visibility == Visibility.Visible && visionLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // While Martlet decides where the character looks: what its eyes are on now.
        var gazeShown = watching && watchSource.IsScreen && Gaze is { Decides: true };
        GazeStatusText.Text = gazeShown ? Gaze!.Status : "";
        GazeStatusText.Visibility = gazeShown && VisionStatusText.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        // Screen summary over time: pictures kept, the last summary's age and how long it took; the summary is the tooltip.
        var digestStatus = digest.Status;
        var digestLine = watching ? ScreenDigester.Line(digestStatus) : "";
        ScreenSummaryText.Text = digestLine;
        SetDetail(ScreenSummaryText, digestLine.Length > 0 && digestStatus.LastText is { } summary ? summary : "");
        ScreenSummaryText.Visibility = digestLine.Length > 0 && VisionStatusText.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        var (pcLine, pcDetail) = PcAudio();
        PcAudioText.Text = pcLine;
        SetDetail(PcAudioText, pcDetail);
        PcAudioText.Visibility = available && preferences.HandsFree && pcLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var chattinessLine = available ? ChattinessLine(SavedChoice, ChattinessNow, BackgroundOn) : "";
        ChattinessText.Text = chattinessLine;
        SetDetail(ChattinessText, chattinessLine.Length > 0 ? ChattinessDetail(ChattinessNow, chattinessAt) : "");
        ChattinessText.Visibility = chattinessLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // What Martlet's newest reply or look took together (MomentTurn), once its request started.
        foreach (var turn in new[] { owned, commentary })
            if (turn?.Inputs is { } took && !ReferenceEquals(turn, inputsFrom))
            {
                inputsFrom = turn;
                turnInputs = TurnInputsLine(turn.Commentary, turn.Report, took, turn.Look);
            }
        TurnInputsText.Text = turnInputs ?? "";
        TurnInputsText.Visibility = available && turnInputs is not null ? Visibility.Visible : Visibility.Collapsed;
        // The last time you talked over Martlet: what decided, the verdict and how long it paused (never what you said).
        var bargeInLine = controller.BargeIns is [.., var lastBargeIn] ? BargeInLine(lastBargeIn) : "";
        BargeInText.Text = bargeInLine;
        BargeInText.Visibility = available && preferences.BargeIn && bargeInLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // What Martlet meant to say on its own but dropped (never what it would have said).
        var drops = controller.DroppedOnItsOwn;
        UnpromptedText.Text = UnpromptedLine(drops);
        UnpromptedText.Visibility = available && drops.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        var (turns, contextTokens) = controller.ContextUse;
        var contextBudget = controller.Configuration?.Context;
        ContextText.Text = ContextLine(turns, contextTokens, contextBudget);
        SetDetail(ContextText, ContextDetail(contextTokens, contextBudget, controller.LastCache));
        ContextRow.Visibility = turns > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Not mid-reply or mid-glance: a finishing turn would put its exchange straight back.
        RefreshContextButton.IsEnabled = owned is not { OwnershipReleased: false } && commentary is not { OwnershipReleased: false };

        // Stop quiets Martlet; listening is paused only from its own button.
        StopButton.IsEnabled = owned is { OwnershipReleased: false } || commentary is { OwnershipReleased: false } ||
            pendingText is not null || heardQueue.Count > 0 || watching || loading is not null || controller.Singing?.Playing == true;
        LevelMeter.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        Title = watching ? $"Talk with {CharacterName} (watching {watchSource.Label})" : $"Talk with {CharacterName}";
        ResultText.Text = notice ?? Activity();
        EmptyDetail.Text = !available ? "" : listening ? "Just start talking, or type below."
            : pushToTalk ? "Type below, or hold the talk button to speak."
            : preferences.HandsFree && micUsable ? "Type below, or press Start listening to talk." : "Type a message below.";
        RenderJobs();
        RenderSong();
        RenderWarmup();
        RenderReadMore();
    }

    /// <summary>The line under the status while vision or hearing what this PC plays is turned on and Martlet decides how
    /// chatty it is about them: the level it picked. Empty when there is nothing in the background or the choice is a fixed
    /// level (Companion says which).</summary>
    internal static string ChattinessLine(ChattinessChoice choice, Chattiness level, bool background) =>
        !background || choice != ChattinessChoice.MartletDecides ? "" : $"Chattiness: {ChattinessTags.Name(level)} (Martlet decides).";

    /// <summary>The chattiness line's tooltip: what Martlet decides means and when it last switched.</summary>
    internal static string ChattinessDetail(Chattiness level, DateTime? switchedAt) =>
        "Martlet picks how chatty it is about what it sees and hears" +
        (switchedAt is { } at ? $" and switched to {ChattinessTags.Name(level)} at {at:t}." : ".");

    /// <summary>The line under the status while Hear what this PC plays is on: whether Martlet hears the PC now, or why it
    /// can't; empty while it is off. Its tooltip says how (every app but Martlet, or only the output you hear while it pauses
    /// for Martlet's voice), what you seem to be doing on the PC (PcActivityMonitor) and whether it left the speakers or your
    /// own voice played back out.</summary>
    private (string Line, string Detail) PcAudio()
    {
        if (CallStatus() is { } call) return call;
        if (!preferences.HearPc) return ("", "");
        if (!controller.CanHearPc) return ("Martlet can't hear what this PC plays here.", "");
        if (!listening) return ("Also hears this PC once you start listening.", "");
        if (pcProblem is { } problem) return (problem + " Martlet keeps trying.", "");
        if (pcListener is null) return ("Getting ready to hear this PC…", "");
        var how = controller.PcWithoutMartlet switch
        {
            true => "Martlet hears everything this PC plays except its own voice.",
            false => controller.PcOutput is { } output
                ? $"Martlet hears what plays on {output}, paused while it speaks."
                : "Martlet hears what this PC plays, paused while it speaks.",
            null => "Martlet hears what this PC plays."
        };
        var doing = controller.PcActivity?.Now.Summary is { } now ? $" Now: {now}." : "";
        var speakers = speakersLeftOut == 0 ? ""
            : $" The microphone also heard this PC's speakers; Martlet left out {speakersLeftOut} {(speakersLeftOut == 1 ? "line" : "lines")} of it.";
        var echoes = pcEchoes == 0 ? ""
            : $" This PC plays your voice back too; Martlet left out {pcEchoes} {(pcEchoes == 1 ? "line" : "lines")} of it.";
        return (pcListener.Hearing ? "Hearing this PC play something…" : "Also hearing this PC.", how + doing + speakers + echoes);
    }

    /// <summary>The context row: how many exchanges Martlet keeps in mind, and that replies send only the newest once they
    /// outgrow the context size.</summary>
    internal static string ContextLine(int turns, int tokens, ContextBudget? budget)
    {
        var kept = turns == 1 ? "Keeps the last exchange in mind" : $"Keeps the last {turns} exchanges in mind";
        return budget is not null && tokens > budget.InputTokens ? kept + "; replies send the newest that fit." : kept + ".";
    }

    /// <summary>The context row's tooltip: about how many tokens the exchanges are of the context size replies fit them into,
    /// and how much of the last reply's input the model read from its prompt cache, when it said.</summary>
    internal static string ContextDetail(int tokens, ContextBudget? budget, (long Input, long Cached)? cache = null)
    {
        var size = budget is null ? null
            : tokens <= budget.InputTokens ? $"About {tokens:N0} tokens of its {budget.Tokens:N0}-token context."
            : $"About {tokens:N0} tokens, more than fit its {budget.Tokens:N0}-token context.";
        var cached = cache is { Input: > 0 } last
            ? $"Last reply: {Math.Min(last.Cached, last.Input) * 100 / last.Input}% of its {last.Input:N0} input tokens came from the model's cache."
            : null;
        return string.Join(" ", new[] { size, cached }.Where(part => part is not null));
    }

    /// <summary>Puts a status line's details in its tooltip and its accessible help text (which MCP returns as help).</summary>
    private static void SetDetail(FrameworkElement line, string detail)
    {
        line.ToolTip = detail.Length > 0 ? detail : null;
        AutomationProperties.SetHelpText(line, detail);
    }

    private string Activity()
    {
        if (loading is not null || !ready && notice is null) return "Getting ready…";
        if (locked) return "Windows is locked.";
        if (pendingText is not null) return "Sending…";
        if (listener is { Hearing: true })
            return owned is { OwnershipReleased: false, Spoken: true } ? "Hearing you… Martlet waits until you're done." : "Hearing you… pause when you're done.";
        if (owned is { OwnershipReleased: false } live && !live.Status.Finished)
        {
            if (live.Authorization.Microphone && live.Transcription is null)
                return mouseHeld || keyHeld || Recording ? "Recording… release to send." : "Transcribing…";
            return live.Status.Code == "home.asking" ? "Checking with Home Assistant…" : Replying(live);
        }
        if (listener is { Transcribing: > 0 }) return "Got it. Transcribing…";
        if (heardQueue.Count > 0) return "Waiting for you to finish…";
        if (commentary is { OwnershipReleased: false }) return "Martlet is taking a look…";
        return Idle();
    }

    private string Idle() => !Available ? "" : LocalModelNote(true) ?? (listening && micProblem is not null ? "Type below; Martlet keeps trying the microphone."
        : listening && listenProblem is not null ? listenProblem
        : listening ? (listenNote is null ? "Listening, so just talk or type below." : Noted("Listening, so just talk or type below", listenNote))
        : !preferences.HandsFree && MicrophoneUsable ? "Type below, or hold the talk button to speak."
        : preferences.HandsFree && listenPaused && MicrophoneUsable ? "Type below, or press Start listening to talk." : "Type a message below.");

    private string Replying(LiveConversationOperation live)
    {
        if (live.Status.Code == "tools.preparing") return "Getting tools ready…";
        var snapshot = live.Turn?.Snapshot;
        if (snapshot?.ActiveTool is { } tool)
            return controller.Tools?.PendingApproval is { Answer.IsCompleted: false } ask
                ? $"Allow {ask.Label}? Answer above." : tool == Martlet.Mcp.Client.TerminalTool.Name ? "Running a command…"
                : tool == ThinkLonger.Name ? "Starting to think it over in the background…"
                : tool == SongTools.SingName ? "Starting a song in the background…"
                : tool == PictureTools.DrawName ? "Starting a picture in the background…"
                : tool == MotionTools.Name ? "Moving…" : $"Using {tool}…";
        if (live.Report && snapshot?.State != ConversationState.Playing) return "Martlet is bringing up what it worked on…";
        return snapshot?.State == ConversationState.Playing ? "Martlet is speaking." : LocalModelNote(false, replying: true) ??
            (live.Spoken ? "Martlet is thinking… keep talking if you're not done." : "Martlet is thinking…");
    }
}
