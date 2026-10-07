using System.Security.Cryptography;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Participation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>What the talk window sends with the next thing always listening hears while nothing else waits for a reply (no
/// earlier words, nothing this PC played, no picture, no typed text, no reply running): whether the reply is spoken, how chatty
/// replies are told to be and whether Martlet is in a Discord call. A reply started early is built with exactly this, so it is
/// the request the window will ask for. The window refreshes it on each tick (null while anything else would go with the
/// words, so no reply starts early then).</summary>
internal sealed record EarlyReplyPlan(bool Voice, ChattinessChoice? Chattiness, bool DiscordCall);

/// <summary>What the talk window asked for when it took a reply started early as its reply: its own timeline of the turn (the
/// end of the turn, speech-to-text, voice recognition, waiting to answer), who spoke as recognition finally said it, the words
/// objects of what went straight to Thinking and the transcript's confidence.</summary>
internal sealed record EarlyPromotion(ReplyTimeline? Timeline, HeardVoices? Heard, IReadOnlyList<SpokenWords>? Words, double? Confidence,
    long At);

/// <summary>A reply started early (Companion › Listening › Start replies early) while the turn is still open: which utterance and
/// pause it belongs to, which start of the turn it is, what it was started with (compared with what the talk window asks for once
/// the turn ends) and how it ends: promoted (taken as the reply) or let go.</summary>
internal sealed class EarlyReplyState(LiveConversationOperation utterance, int pause, int start, long pauseStartedAt, long startedAt,
    byte[] pcm)
{
    private readonly TaskCompletionSource<EarlyPromotion?> decided = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HeardVoices?> heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? speech = pcm;
    internal LiveConversationOperation Utterance { get; } = utterance;
    /// <summary>The pause of the utterance it started in (counted from 1).</summary>
    internal int Pause { get; } = pause;
    /// <summary>Which start of the turn it is: the ones before it were let go when the user went on talking.</summary>
    internal int Start { get; } = start;
    /// <summary>When the user's pause began and when the reply started early (controller clock).</summary>
    internal long PauseStartedAt { get; } = pauseStartedAt;
    internal long StartedAt { get; } = startedAt;
    /// <summary>What it was started with, compared with what the talk window asks for once the turn ends (its voices are filled
    /// in once recognition said who spoke, <see cref="Heard"/>).</summary>
    internal required EarlyAsk Ask { get; init; }
    internal required bool PrepareVoice { get; init; }
    internal string Text => Ask.Text;
    internal bool Voice => Ask.Voice;
    internal bool Straight => Ask.Straight;
    internal ChattinessChoice? Chattiness => Ask.Chattiness;
    /// <summary>The recording it went with (Thinking hears it), or null.</summary>
    internal BoundedWaveAudio? Recording => Ask.Recording;
    /// <summary>The oldest exchange the reply had to leave out of its request: let go of once it is taken, so the next
    /// requests start the same.</summary>
    internal long LetGoBefore { get; set; } = -1;
    /// <summary>Who spoke, as recognition said it before the turn ended (null when nobody is recognized here); set once.</summary>
    internal Task<HeardVoices?> Heard => heard.Task;
    internal void Recognized(HeardVoices? voices) => heard.TrySetResult(voices);
    internal Task<EarlyPromotion?> Decided => decided.Task;
    internal bool Waiting => !decided.Task.IsCompleted;
    internal bool Promoted => decided.Task is { IsCompletedSuccessfully: true, Result: not null };
    /// <summary>It was let go (it will never be the reply).</summary>
    internal bool LetGo => decided.Task is { IsCompletedSuccessfully: true, Result: null };
    internal string? Outcome { get; private set; }
    internal string? Reason { get; private set; }

    /// <summary>The speech it was started on, once (for Voice ID and recognition before the request); the caller clears it.</summary>
    internal byte[]? TakeSpeech() => Interlocked.Exchange(ref speech, null);

    internal bool TryPromote(EarlyPromotion promotion) => decided.TrySetResult(promotion);

    internal bool TryLetGo(string outcome, string reason)
    {
        lock (decided)
        {
            if (!decided.TrySetResult(null)) return false;
            Outcome = outcome;
            Reason = reason;
        }
        if (TakeSpeech() is { } left) CryptographicOperations.ZeroMemory(left);
        heard.TrySetResult(null);
        return true;
    }

    /// <summary>The reply latency line's facts once it is taken (<see cref="ReplyTimeline.Early"/>): every start before it was
    /// let go.</summary>
    internal EarlyStarts Starts(bool promoted) => new(promoted, Start, promoted ? Start - 1 : Start);

    public override string ToString() => nameof(EarlyReplyState);
}

internal sealed partial class LiveConversationController
{
    private EarlyReplyPlan? earlyPlan;
    private readonly Queue<EarlyReplyRecord> earlyRecords = new();

    /// <summary>What the talk window would send with the next thing always listening hears (<see cref="EarlyReplyPlan"/>); null
    /// while anything else would go with it, so no reply starts early.</summary>
    internal EarlyReplyPlan? EarlyPlan { get => Volatile.Read(ref earlyPlan); set => Volatile.Write(ref earlyPlan, value); }

    /// <summary>The newest replies started early and what became of them, newest last (no words, no audio).</summary>
    internal IReadOnlyList<EarlyReplyRecord> EarlyReplies { get { lock (earlyRecords) return [.. earlyRecords]; } }

    /// <summary>Raised (on a background thread) after a reply started early was promoted or let go.</summary>
    internal event Action? EarlyDecided;

    /// <summary>A reply started early that waits to be taken as the reply (or let go): it holds the app slot, unseen and
    /// unheard, until the talk window asks for the reply.</summary>
    internal LiveConversationOperation? EarlyReply
    {
        get { lock (gate) return active is { Early.Waiting: true, Worker: not null, OwnershipReleased: false } early ? early : null; }
    }

    /// <summary>Completes when the reply holding the app slot now (a reply started early that was let go, say) has left it.</summary>
    internal Task SlotFreed { get { lock (gate) return active is { Worker: not null } current ? current.Worker.Completion : Task.CompletedTask; } }

    /// <summary>Whether replies may start early with <paramref name="configured"/>: the Thinking model (on the user's own
    /// computers, or a cloud with Also for cloud models) and the voice (prepared early too, or only once the reply is taken),
    /// in words for Companion › Listening's status.</summary>
    internal static (bool Thinking, bool Voice, string Why) EarlyAllowed(EarlyReplyOptions options, LiveConversationConfiguration? configured)
    {
        if (!options.Enabled) return (false, false, "off");
        if (configured is null) return (false, false, "Thinking isn't set up yet");
        var own = configured.NetworkThinking;
        if (!options.ForThinking(own))
            return (false, false, "Thinking is a cloud model; turn on Also for cloud models to start its replies early too");
        var voice = options.ForVoice(own, configured.CloudVoice);
        var where = own ? configured.HostTarget() is not null ? "on a paired Martlet host" : "on your own computer" : "in the cloud";
        var why = $"Thinking runs {where}" + (voice ? "; the first spoken words are prepared early too"
            : !options.Voice ? "; the voice waits until the reply is taken"
            : "; the voice is a cloud voice, so it waits until the reply is taken");
        return (true, voice, why);
    }

    // A reply starts early for the utterance always listening hears: the quick transcript of the pause under way has real
    // words, and the conversation is free. Built exactly as the talk window will ask for it (EarlyPlan); it runs on the app slot
    // like any reply, held: nothing shows, plays or acts until the talk window takes it (TryTakeEarly) or it is let go.
    private LiveConversationOperation? StartEarly(LiveConversationOperation utterance, QuickWords quick, string text, int pause,
        int start, long pauseStartedAt, TimeSpan? voiced)
    {
        var listening = utterance.Listening!;
        if (EarlyPlan is not { } plan) return null;
        var configured = utterance.Authorization.Configuration;
        if (!listening.EarlyReplies.ForThinking(configured.NetworkThinking)) return null;
        // Home Assistant's Assist acts on the house as it answers; a reply that may use it never starts early.
        if (smartHome is { ControlEnabled: true, ModelToolsEnabled: false }) return null;
        if (Speaking is not null || singing?.Playing == true) return null;
        var straight = GoesStraight(listening, utterance);
        var hear = listening.HearsWith(configured);
        var voice = plan.Voice;
        LiveConversationOperation operation;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (disposed || paused || muted || locked || operations.IsRunning || active is { Worker: not null, OwnershipReleased: false })
                return null;
            if (!ReferenceEquals(configuration, configured) || configured.Unavailable(voice, false) is not null) return null;
            // As the talk window's ask goes: the recording only while Thinking may hear it, and straight to Thinking only with it.
            BoundedWaveAudio? recording;
            BoundedTextInput input;
            try
            {
                recording = (hear || straight) && (!listening.HearLocalOnly || configured.RecordingStaysOnThisPc())
                    ? BoundedWaveAudio.FromPcm(CapturedUtterance.Format, quick.Pcm) : null;
                input = new(straight ? LiveConversationConfiguration.VoiceOnlyText : text);
            }
            catch (ContractException) { return null; }
            if (straight && recording is null || input.UserText.Length > 4096) return null;
            long acceptedRevision = revision = checked(revision + 1);
            var authorization = new ConversationAuthorization(configured, voice, microphone: false, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, CancellationToken.None,
                screen: false, hear: recording is not null);
            var now = clock.GetTimestamp();
            var early = new EarlyReplyState(utterance, pause, start, pauseStartedAt, now, quick.Pcm.ToArray())
            {
                Ask = new(text, voice, straight, recording, straight ? null : plan.Chattiness, plan.DiscordCall, null),
                PrepareVoice = voice && listening.EarlyReplies.ForVoice(configured.NetworkThinking, configured.CloudVoice)
            };
            IReadOnlyList<SpokenWords>? words = null;
            if (straight)
            {
                // The words are known already: the quick transcript they were checked on.
                var spoken = new SpokenWords(clock) { Voiced = voiced };
                spoken.Heard(text, null, null, TimeSpan.Zero);
                words = [spoken];
            }
            operation = new(authorization, CancellationToken.None)
            {
                MemoryRequested = memory is not null && configured.Memory is { Enabled: true },
                Spoken = true, Recording = recording, StraightWords = words, DiscordCall = plan.DiscordCall,
                BackgroundChattiness = early.Chattiness, Early = early,
                LatencyTimeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, pauseStartedAt)
            };
            var worker = operations.TryStart(async token =>
            {
                await published.Task.ConfigureAwait(false);
                authorization.BindWorker(token);
                return await RunAsync(operation, input, token).ConfigureAwait(false);
            });
            if (worker is null)
            {
                authorization.Revoke();
                return null;
            }
            operation.Worker = worker;
            active = operation;
            policy.SetState(new()
            {
                AuthorizationRevision = acceptedRevision, CaptureAuthorized = true, TranscriptionAuthorized = true,
                TextDestinationAuthorized = true, SpeechOutputRequested = voice, SpeechDestinationAuthorized = voice
            });
        }
        published.SetResult();
        SuperviseAsync(operation).Forget();
        ErrorLog.Info($"Early reply: started {Ms(clock.GetElapsedTime(pauseStartedAt))} ms into your pause (start {start} of " +
            $"{listening.EarlyReplies.MaximumStarts}; {(operation.Early!.Straight ? "your recording" : "the quick transcript")}" +
            $"{(operation.Early.PrepareVoice ? ", the voice prepared too" : "")}).");
        return operation;
    }

    // Before its request: what the final turn will check of the same speech, without changing anything. Only your voice when
    // Voice ID is on, and who spoke as recognition will say it (nothing learned). False when it was let go.
    private async Task<bool> PrepareEarlyAsync(LiveConversationOperation operation, EarlyReplyState early, CancellationToken worker)
    {
        var speech = early.TakeSpeech();
        if (speech is null) return !early.LetGo;
        try
        {
            var print = early.Utterance.Voiceprint;
            var recognizer = voices is { Active: true } active ? active : null;
            if (print is null && recognizer is null)
            {
                early.Recognized(null);
                return !early.LetGo;
            }
            var heard = await Task.Run<(bool Voice, HeardVoices? Heard)>(() =>
            {
                if (print is not null &&
                    SpeakerVerifier.Check(VoiceIdentity.Encoder, print.Embedding, print.Threshold, speech).Verdict != SpeakerVerdict.User)
                    return (false, null);
                if (recognizer is null) return (true, null);
                var samples = Pcm.ToFloats(speech);
                try { return (true, recognizer.Identify(samples)); }
                // Recognition is best effort here as in the turn itself: nobody is named this time.
                catch (Exception error) when (error is not OutOfMemoryException) { return (true, HeardVoices.None); }
                finally { Array.Clear(samples); }
            }, worker).ConfigureAwait(false);
            if (!heard.Voice)
            {
                LetGoEarly(operation, EarlyReplyRecord.Refused, "not your voice");
                return false;
            }
            // Nobody heard is what the turn's own recognition says too (no voices block). The request is built with it; once
            // it is known, the talk window may take the reply.
            var named = heard.Heard is { Voices.Count: > 0 } known ? known : null;
            operation.Heard = named;
            early.Recognized(named);
            return !early.LetGo;
        }
        finally { CryptographicOperations.ZeroMemory(speech); }
    }

    // The talk window asks for its reply: a reply started early for the same words, with everything that goes with them the
    // same, is taken as it is (promoted, no second request); otherwise it is let go, and the window's reply starts once the
    // slot is free. Null when there is none, or it was let go.
    private LiveConversationOperation? TryTakeEarly(string? text, bool voice, bool microphone, ListeningOptions? listening, bool spoken,
        HeardVoices? heard, double? confidence, BoundedWaveAudio? recording, SeenScreen? seen, bool pcAudio, string? userWords,
        ReplyTimeline? timeline, PlaybackMode playback, ChattinessChoice? chattiness, IReadOnlyList<SpokenWords>? words,
        bool hearLocalOnly, bool remote, bool bringUp, AttentionSignal? attention, bool look, bool discordCall)
    {
        LiveConversationOperation candidate;
        EarlyReplyState early;
        LiveConversationConfiguration? selected;
        lock (gate)
        {
            if (active is not { Early: { Waiting: true } waiting, Worker: not null, OwnershipReleased: false } running ||
                running.Authorization.IsCanceled) return null;
            (candidate, early, selected) = (running, waiting, configuration);
        }
        var why = EarlyDiffers(candidate, early, selected, text, voice, microphone, listening, spoken, heard, recording, seen, pcAudio,
            userWords, playback, chattiness, words, hearLocalOnly, remote, bringUp, attention, look, discordCall);
        var at = clock.GetTimestamp();
        if (why is null)
        {
            if (timeline is not null)
            {
                timeline.Mark("waiting to answer", at);
                timeline.Early = early.Starts(promoted: true);
            }
            if (early.TryPromote(new(timeline, heard, words, confidence, at))) return candidate;
            why = "it was let go meanwhile";
        }
        if (timeline is not null) timeline.Early = early.Starts(promoted: false);
        // The window asks again with the utterance's own timeline once the slot is free: it counts this start as let go too.
        if (early.Utterance.LatencyTimeline is { } original) original.Early = early.Starts(promoted: false);
        LetGoEarly(candidate, EarlyReplyRecord.Changed, why);
        return null;
    }

    // Why the talk window's ask isn't the reply started early, in a few words; null when it is the same request
    // (EarlyAsk.Differs, which MCP's early_reply_check uses too).
    private string? EarlyDiffers(LiveConversationOperation candidate, EarlyReplyState early, LiveConversationConfiguration? selected,
        string? text, bool voice, bool microphone, ListeningOptions? listening, bool spoken, HeardVoices? heard,
        BoundedWaveAudio? recording, SeenScreen? seen, bool pcAudio, string? userWords, PlaybackMode playback,
        ChattinessChoice? chattiness, IReadOnlyList<SpokenWords>? words, bool hearLocalOnly, bool remote, bool bringUp,
        AttentionSignal? attention, bool look, bool discordCall)
    {
        var configured = candidate.Authorization.Configuration;
        var extra = microphone || !spoken || listening is not null || remote || playback != PlaybackMode.Reply ? "you typed or pressed to talk"
            : pcAudio || userWords is not null || bringUp ? "what this PC played goes with it"
            : seen is not null || look || attention is not null ? "a picture goes with it"
            : !ReferenceEquals(selected, configured) ? "your setup changed"
            : singing?.Now() is not null ? "Martlet is singing"
            : !early.Heard.IsCompleted ? "who spoke wasn't recognized in time" : null;
        var prompts = configured.Prompts;
        // The recording goes as Start sends it: only while Thinking may hear it.
        var sent = recording is not null && (!hearLocalOnly || configured.RecordingStaysOnThisPc()) ? recording : null;
        var asked = new EarlyAsk(text ?? "", voice, words is not null, sent, chattiness, discordCall && spoken,
            Voices(heard is { Voices.Count: > 0 } ? heard : null, prompts), extra);
        return early.Ask.WithVoices(extra is null ? Voices(early.Heard.Result, prompts) : null).Differs(asked);
    }

    // Who is talking as the request carries it: the voices block, its preamble and the history prefix, as one text.
    private static string? Voices(HeardVoices? heard, PromptSettings? prompts) =>
        heard is null ? null : string.Join("\n", VoicePromptContext.Prefix(heard), VoicePromptContext.Preamble(heard, prompts),
            VoicePromptContext.Block(heard));

    /// <summary>Lets go of a reply started early (the user went on talking, the words changed, it was refused or expired):
    /// nothing of it was shown, said or done, and what it took (touches, finished work) waits for the next reply. Returns
    /// false when it wasn't waiting any more (taken as the reply a moment ago, or let go already).</summary>
    internal bool LetGoEarly(LiveConversationOperation operation, string outcome, string reason)
    {
        if (operation.Early is not { } early || !early.TryLetGo(outcome, reason)) return false;
        // It no longer holds the live floor (docs/CONVERSATION.md, Live floor): the floor's grace, then your voice, hold it while
        // you go on talking.
        operation.FloorReply?.End(EarlyLetGoFloor);
        RecordEarly(new(clock.GetUtcNow(), outcome, Since(early.PauseStartedAt, early.StartedAt), clock.GetElapsedTime(early.StartedAt),
            early.Start, reason));
        operation.Cancel("conversation.early_let_go");
        return true;
    }

    /// <summary>Why a reply started early stops holding the live floor when it is let go.</summary>
    internal const string EarlyLetGoFloor = "the reply started early was let go";

    /// <summary>Lets go of the reply started early that waits now, if any (something else needs the conversation first).</summary>
    internal void LetGoEarly(string reason)
    {
        if (EarlyReply is { } waiting) LetGoEarly(waiting, EarlyReplyRecord.Cancelled, reason);
    }

    // A turn's early replies: always listening on the microphone with Start replies early on, and Parakeet on this PC as
    // Listening (the quick transcript a reply starts early on); null otherwise.
    private EarlyReplyGate? EarlyGateFor(LiveConversationOperation operation)
    {
        if (operation is not { Listen: true, Listening: { HandsFree: true, Pc: false } listening } || !listening.EarlyReplies.Enabled)
            return null;
        var configured = operation.Authorization.Configuration;
        return localWords is null || !configured.LocalStt() || configured.SttHostTarget() is not null
            ? null : new EarlyReplyGate(listening.EarlyReplies);
    }

    // Waits, held, for the talk window to take the reply started early (TryTakeEarly) or for it to be let go (the user went on
    // talking, the words changed, nothing took it in time). Taken: what the window had once the turn ended goes on the reply
    // (who spoke, the words of what went straight to Thinking, its timeline), Martlet commits to answering, the history the
    // request left out is let go of, and the held turn goes on: what it wrote shows and its first piece plays at once. Returns
    // the outcome to end with when it isn't taken (or Martlet doesn't answer it), the reply's dispatch lease, and whether
    // Martlet turned it down for good (the live floor no longer holds for what was heard).
    private async Task<(SetupWorkOutcome? End, DispatchLease? Lease, bool Dismissed)> TakenAsync(LiveConversationOperation operation,
        EarlyReplyState early, ConversationTurn turn, BoundedTextInput input, CancellationToken worker)
    {
        EarlyPromotion? promotion;
        try { promotion = await early.Decided.WaitAsync(EarlyReplyOptions.Expiry, clock, worker).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Taken at the last moment, it goes on; otherwise nothing took it in time.
            promotion = !LetGoEarly(operation, EarlyReplyRecord.Expired, "nothing took it") && early.Promoted ? early.Decided.Result : null;
        }
        if (promotion is null) return (SetupWorkOutcome.Canceled, null, false);
        DispatchLease? lease = null;
        bool? ready;
        lock (gate)
        {
            operation.Authorization.Check(worker);
            operation.Heard = promotion.Heard;
            operation.SpokenConfidence = promotion.Confidence;
            if (promotion.Words is { } words) operation.StraightWords = words;
            if (promotion.Timeline is { } timeline)
            {
                // The reply's own steps before the turn ended (preparing, memory, building the request) go with the turn's.
                if (operation.LatencyTimeline is { } before) timeline.Mark(before.Steps);
                operation.LatencyTimeline = timeline;
            }
            if (!operation.OnItsOwn)
            {
                var (accepted, committed, reason) = Participate(operation, input, commit: true);
                if (!accepted)
                {
                    RecordEarly(new(clock.GetUtcNow(), EarlyReplyRecord.Refused, Since(early.PauseStartedAt, early.StartedAt),
                        clock.GetElapsedTime(early.StartedAt), early.Start, "Martlet doesn't answer it now"));
                    return (SetupWorkOutcome.Completed, null, Dismisses(reason));
                }
                lease = committed;
            }
            if (early.LetGoBefore >= 0) context.LetGoBefore(early.LetGoBefore);
            ready = early.Voice ? turn.Snapshot.Timings?.FirstSpeechAudioAfter is not null : null;
            turn.Release();
        }
        RecordEarly(new(clock.GetUtcNow(), EarlyReplyRecord.Promoted, Since(early.PauseStartedAt, early.StartedAt),
            clock.GetElapsedTime(early.StartedAt), early.Start, FirstPieceReady: ready));
        return (null, lease, false);
    }

    private TimeSpan Since(long from, long to) => TimeSpan.FromSeconds((to - from) / (double)clock.TimestampFrequency);

    private void RecordEarly(EarlyReplyRecord record)
    {
        lock (earlyRecords)
        {
            earlyRecords.Enqueue(record);
            while (earlyRecords.Count > 20) earlyRecords.Dequeue();
        }
        ErrorLog.Info(record.Describe());
        EarlyDecided?.Invoke();
    }

    private static string Ms(TimeSpan value) => Math.Max(0, value.TotalMilliseconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}
