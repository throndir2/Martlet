using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Loading the settings, following the talk preferences and warming the models up.
public partial class LiveConversationWindow
{
    private async Task LoadAsync()
    {
        if (closed) return;
        loadingSince = clock.GetTimestamp();
        if (operations.IsRunning)
        {
            // Another Martlet task holds settings (often right after Martlet starts): load as soon as it finishes.
            notice = Remedy("conversation.ownership_busy");
            loadPending = true;
            return;
        }
        loadPending = false;
        // Any load picks up the situation's Thinking route as it is now.
        situationReason = null;
        ready = false;
        listening = false;
        StopListening(keepHeard: true);
        if (watching) StopWatching(null);
        var backend = settings;
        var worker = operations.TryStart(async token => new(SetupWorkOutcome.Completed, Loaded: await backend.LoadAsync(token).ConfigureAwait(false)));
        if (worker is null) return;
        loading = worker;
        long current = ++generation;
        using var stop = new CancellationTokenSource();
        observation = stop;
        RenderActions();
        var completed = await Task.WhenAny(worker.Completion, Task.Delay(TimeSpan.FromSeconds(5), clock, stop.Token));
        if (closed || generation != current) return;
        observation = null;
        loading = null;
        if (completed != worker.Completion || stop.IsCancellationRequested)
        {
            worker.RequestCancellation();
            notice = "Couldn't load settings, so close this window and try again.";
        }
        else if ((await worker.Completion).Loaded is { } loaded)
        {
            controller.Configure(loaded);
            ShowCharacter(loaded.Settings?.Companion is { } companion
                ? companion.Personas.FirstOrDefault(persona => persona.Id == companion.ActivePersonaId)?.Name : null);
            ready = loaded.Error is null;
            notice = loaded.Error?.Summary ?? (controller.Configuration is null
                ? "Set up Thinking in Companion, then come back to talk." : null);
            if (following && controller.Configuration is { } now)
                ErrorLog.Info("The open conversation follows the changed setup between replies: " +
                    string.Join(", ", now.Routes.Select(r => $"{r.Role} {r.RouteType}{(r.ModelId is { Length: > 0 } id ? " " + id : "")}")) + ".");
            following = false;
            // While gaming or Host away, live Thinking uses the situation's route (LiveSituation); say once when it switches.
            if (controller.Configuration is { } configured && configured.Situation?.Key != situationKey)
            {
                if (configured.Situation is { } moved) ErrorLog.Info($"Situations: the conversation uses {moved.Text} for live Thinking from its next reply.");
                else if (situationKey is not null) ErrorLog.Info("Situations: the conversation uses its usual Thinking route again from its next reply.");
                situationKey = configured.Situation?.Key;
            }
            Warm();
            StartLive();
            if (listenWhenReady && preferences.HandsFree && listenPaused && Available) Mic_Click(this, new RoutedEventArgs());
        }
        else notice = "Couldn't load settings, so close this window and try again.";
        listenWhenReady = false;
        RenderActions();
    }

    private bool listenWhenReady;

    /// <summary>Opened by Start listening in the notification-area menu: listening starts as soon as the window is ready, as if
    /// its own Start listening button were pressed.</summary>
    internal void ListenWhenReady()
    {
        if (!ready || loading is not null) listenWhenReady = true;
        else if (listenPaused) Mic_Click(this, new RoutedEventArgs());
    }

    private bool Voice => preferences.SpeakReplies && controller.Configuration is { } selected && selected.Unavailable(true, false) is null;
    // Listening uses the microphone chosen in Companion › Listening (the Windows default unless another is picked); testing it
    // there is optional, and a microphone that can't be opened says so here.
    private bool MicrophoneUsable => controller.Configuration is { } selected && selected.Unavailable(Voice, true) is null;
    private bool Available => ready && !locked && loading is null && controller.Configuration is not null;

    /// <summary>The conversation loaded and still can't talk (Thinking isn't set up, or its settings couldn't be read).</summary>
    internal bool CantTalk => begun && !loadPending && loading is null && (!ready || controller.Configuration is null);

    private bool Recording => owned is { OwnershipReleased: false, HandsFree: false } live && live.Authorization.Microphone &&
        live.Turn is null && live.Transcription is null && !live.Status.Finished;

    /// <summary>Starts what you started: always listening (once you pressed Start listening and listening is set up) and watching
    /// (once you pressed Start watching and vision is on).</summary>
    private void StartLive()
    {
        if (closed || !Available) return;
        listening = preferences.HandsFree && !listenPaused && MicrophoneUsable;
        if (preferences.Watch && !watchPaused && !watching) StartWatching();
    }

    /// <summary>Follows a change made in Companion while this window is open: how you talk, whether replies are spoken and what
    /// Martlet may look at. A running listener or look keeps the options it started with, so either starts again with the new
    /// ones; turning vision off in Companion stops watching (turning it on only offers Start watching), and switching to
    /// push-to-talk stops always listening.</summary>
    internal void UsePreferences(TalkPreferences next, string? address)
    {
        if (closed) return;
        var before = preferences;
        var addressChanged = address != videoAddress;
        preferences = next;
        videoAddress = address;
        if (before.HandsFree != next.HandsFree || before.Sensitivity != next.Sensitivity || before.PauseIndex != next.PauseIndex ||
            before.VoiceId != next.VoiceId || before.HearVoice != next.HearVoice || before.BargeIn != next.BargeIn ||
            before.ReduceEcho != next.ReduceEcho || before.WordCheck != next.WordCheck || before.TranscribeFirst != next.TranscribeFirst ||
            before.BargeInStyle != next.BargeInStyle || before.JudgeTurns != next.JudgeTurns ||
            before.EarlyReplyOptions != next.EarlyReplyOptions)
        {
            StopListening(keepHeard: true);
            listening = Available && next.HandsFree && !listenPaused && MicrophoneUsable;
        }
        // Hearing what this PC plays starts or stops beside the microphone, which carries on.
        if (before.HearPc != next.HearPc) StopPcListening(keepHeard: next.HearPc);
        // Muting Martlet's voice (Speak Martlet's replies aloud off, or Mute voice on the character's menu) silences what it is
        // saying now; the reply's words still show. The next reply starts without a voice.
        if (before.SpeakReplies && !next.SpeakReplies) controller.MuteVoice();
        if (!next.Watch)
        {
            watchPaused = true;
            visionProblem = null;
            if (watching) StopWatching(null);
        }
        else if (addressChanged || !before.Watch || before.ScreenScope != next.ScreenScope || before.CameraId != next.CameraId ||
            before.VideoAddress != next.VideoAddress)
        {
            if (!watchPaused)
            {
                if (watching) StopWatching(null);
                StartWatching();
                // Typing a camera address saves on each keystroke: look once it has settled.
                if (watching) nextGlance = After(ScreenCommentaryPacer.Tick);
            }
            // What kept Martlet from seeing may be fixed now: Start watching tries again.
            else visionProblem = null;
        }
        // A new chattiness choice retunes the looks from now on, without starting over.
        if (before.ScreenChattiness != next.ScreenChattiness) pacer?.Retune(ChattinessNow);
        RenderActions();
    }

    /// <summary>When Thinking runs in Ollama on this PC, has it load the model now (and follows a change of model).</summary>
    private void Warm()
    {
        var model = controller.Configuration is { LocalOllama: true } selected ? selected.Route(SetupRole.Llm).ModelId : null;
        if (warmup?.Model != model)
        {
            warmup?.Dispose();
            warmup = model is null ? null : new(model, clock, controller.DataDirectory);
        }
        warmup?.Touch();
    }

    /// <summary>A reply or a look on its way, or someone talking, asks Ollama again after a quiet spell, so a model it unloaded is
    /// loading already.</summary>
    private void KeepWarm()
    {
        if (warmup is not null && Available &&
            (owned is { OwnershipReleased: false } || commentary is { OwnershipReleased: false } || listener is { Hearing: true }))
            warmup.Touch();
    }

    // When the conversation's latest load started, for how long getting it ready takes.
    private long loadingSince;

    /// <summary>The parts of this conversation that still load (its settings, Thinking's model in Ollama on this PC), however
    /// short; <see cref="CompanionWarmup.Showing"/> keeps those worth showing. Home adds Parakeet and the character.</summary>
    internal IReadOnlyList<WarmupItem> Warmup
    {
        get
        {
            var items = new List<WarmupItem>();
            if (closed || !begun) return items;
            if (loading is not null || loadPending) items.Add(new(WarmupPart.Conversation, null, clock.GetElapsedTime(loadingSince)));
            if (warmup is { } local && local.Status is { State: LocalModelState.Loading } status)
                items.Add(new(WarmupPart.Thinking, local.Model, status.Elapsed, LocalOllamaWarmup.LastLoad(local.Model)));
            return items;
        }
    }

    /// <summary>What else loads on this PC for the conversation (Parakeet for listening, the character), from Home.</summary>
    internal Func<IReadOnlyList<WarmupItem>>? MoreWarmup { get; set; }

    private bool warmupSpinning;

    /// <summary>The warming-up banner under the header: what still loads, how long it has taken, and a progress bar.</summary>
    private void RenderWarmup()
    {
        var items = CompanionWarmup.Showing(Warmup.Concat(MoreWarmup?.Invoke() ?? []));
        var show = items.Count > 0;
        WarmupBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show != warmupSpinning)
        {
            warmupSpinning = show;
            Motion.Spin(WarmupSpinner, show);
        }
        if (!show) return;
        var line = CompanionWarmup.Line(items, lead: false);
        if (WarmupText.Text != line) WarmupText.Text = line;
        var advice = CompanionWarmup.Advice(items, talkWindow: true);
        if (WarmupAdvice.Text != advice) WarmupAdvice.Text = advice;
        var progress = CompanionWarmup.Progress(items);
        WarmupProgress.IsIndeterminate = progress is null;
        if (progress is { } value) WarmupProgress.Value = value;
        AutomationProperties.SetHelpText(WarmupBanner, advice);
    }

    /// <summary>What Ollama on this PC is doing with the Thinking model when it matters: still loading it (warming up), or why
    /// it can't. <paramref name="replying"/>: a reply waits for it.</summary>
    private string? LocalModelNote(bool problems, bool replying = false)
    {
        if (warmup is not { } local) return null;
        var status = local.Status;
        return status.State switch
        {
            LocalModelState.Loading when status.Elapsed >= CompanionWarmup.ShowAfter => replying
                ? $"Waiting for {local.Model} to load; Martlet answers once it's ready."
                : "Warming up, but you can talk or type now.",
            _ when !problems => null,
            LocalModelState.NotRunning => LocalOllamaRecovery.Short() ?? "Start Ollama, then try again.",
            LocalModelState.MissingModel => $"Download {local.Model} in Companion › Thinking.",
            LocalModelState.Failed => $"Couldn't start {local.Model} ({status.Detail?.TrimEnd('.')}); choose a smaller model in " +
                "Companion › Thinking, or close programs that use a lot of memory.",
            _ => null
        };
    }
}
