using System.Security.Cryptography;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

internal sealed partial class LiveConversationController
{
    /// <summary>Always listening: one loop for the microphone (and one for what this PC plays) on its own slot beside replies. It
    /// records one utterance at a time and transcribes each in order while it already listens for the next, decides with the
    /// end-of-turn judge when the user finished talking, holds off while Martlet speaks when it can't tell Martlet's voice from
    /// the user's, and hands what it heard to the talk window (or straight to Thinking). It is nested in the controller, so it
    /// reads the conversation's state through <c>owner</c>; the controller's members that other code calls forward to it.</summary>
    internal sealed class ListeningSession
    {
        private readonly LiveConversationController owner;
        private long spokeUntil;

        internal ListeningSession(LiveConversationController owner, IEndOfTurnJudge? turnJudge,
            Func<IProviderCredentialSource, TimeProvider, OpenAiTranscriptionAdapter>? transcriptionFactory)
        {
            this.owner = owner;
            // Smart Turn on this PC first; a Thinking-pool member only when it is missing or fails.
            smartTurn = turnJudge as SmartTurnJudge;
            this.turnJudge = smartTurn is not null ? EndOfTurnJudges.WithFallback(smartTurn, new PoolTurnJudge(() => owner.ThinkingPool)) : turnJudge;
            var listenCredentials = new ConversationCredentialSource(() => Volatile.Read(ref transcribing)?.Authorization);
            listenTranscription = transcriptionFactory?.Invoke(listenCredentials, owner.clock) ??
                OpenAiTranscriptionAdapter.Create(listenCredentials, owner.clock);
        }

        private readonly IEndOfTurnJudge? turnJudge;
        private readonly SmartTurnJudge? smartTurn;
        private readonly object turnGate = new();
        private readonly Queue<EndOfTurnDecision> turnDecisions = new();
        private int turnJudgeMissingLogged;

        // Always listening runs on its own slot beside replies (so it keeps hearing while Martlet thinks), with its own
        // speech-to-text credentials bound to the one utterance being transcribed.
        private readonly SetupOperationRunner listenSlot = new();
        internal readonly OpenAiTranscriptionAdapter listenTranscription;
        internal LiveListener? listener;
        private LiveConversationOperation? transcribing;
        // Speech-to-text for what went straight to Thinking: one utterance after another, beside the replies (TranscribeWordsAsync).
        private Task wordsTail = Task.CompletedTask;

        /// <summary>Starts always listening: one loop on its own slot beside replies that records one utterance at a time and
        /// transcribes each (in order) while it already listens for the next, so nothing said while Martlet thinks or speaks is
        /// lost. Each utterance is still its own action (fresh authorization, capture epoch, Voice ID check and STT request). It
        /// holds off only while other setup work owns the app slot, and while Martlet speaks when it can't tell Martlet's own voice
        /// from yours (no echo reduction and no barge-in), so it never hears itself; microphone and speech-to-text failures are
        /// reported and listening carries on.</summary>
        internal LiveListener Listen(ListeningOptions options)
        {
            if (!options.HandsFree || options.Pc && (options.RequireVoiceId || options.Hear || options.ReduceEcho || options.Straight))
                throw new LiveActionException("conversation.invalid_input");
            if (options.Pc && owner.pcSession.pcAudio is null) throw new LiveActionException("conversation.configuration_unsupported");
            options.Activity.Validate();
            Voiceprint? voiceprint = null;
            if (options.RequireVoiceId) voiceprint = owner.voiceIdentity?.Current ?? throw new LiveActionException("voiceid.not_enrolled");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listening = new LiveListener(options, voiceprint);
            lock (owner.gate)
            {
                if (owner.disposed || owner.paused || owner.muted || owner.locked) throw new LiveActionException("conversation.controls_blocked");
                var selected = owner.configuration ?? throw new LiveActionException("conversation.setup_required");
                if (selected.Unavailable(false, true) is not null) throw new LiveActionException("conversation.configuration_unsupported");
                // What the PC plays is heard on a slot of its own, beside the microphone.
                listening.Worker = (options.Pc ? owner.pcSession.pcSlot : listenSlot).TryStart(async token =>
                {
                    await started.Task.ConfigureAwait(false);
                    return await ListenLoopAsync(listening, token).ConfigureAwait(false);
                }) ?? throw new LiveActionException("conversation.ownership_busy");
                if (options.Pc) owner.pcSession.pcListener = listening;
                else listener = listening;
            }
            started.SetResult();
            // The end-of-turn judge loads in the background so the first pause is judged too.
            if (!options.Pc && options.JudgeTurns && smartTurn is { } smart) smart.WarmAsync().Forget();
            return listening;
        }

        /// <summary>The end-of-turn judge's state for Companion › Listening and MCP: its name, whether it can answer and why not, how
        /// long it took to load, and the newest decisions (newest last; no words, no audio).</summary>
        internal (string? Judge, bool Available, string? Problem, TimeSpan? LoadTime, EndOfTurnDecision[] Decisions) TurnJudgeStatus
        {
            get
            {
                lock (turnGate)
                    return (turnJudge?.Name, turnJudge?.Available == true, turnJudge is null ? "no judge" : smartTurn?.Problem,
                        smartTurn?.LoadTime, turnDecisions.ToArray());
            }
        }

        /// <summary>Raised (on a background thread) after each end-of-turn decision.</summary>
        internal event Action? TurnDecided;

        /// <summary>When the end-of-turn judge is asked and how long an unfinished pause may run (EndOfTurnGate).</summary>
        internal EndOfTurnOptions EndOfTurn { get; set; } = new();

        internal void RecordTurn(EndOfTurnDecision decision)
        {
            lock (turnGate)
            {
                turnDecisions.Enqueue(decision);
                while (turnDecisions.Count > 20) turnDecisions.Dequeue();
            }
            ErrorLog.Info(decision.Describe());
            TurnDecided?.Invoke();
        }

        /// <summary>The judge for this utterance, or null for the plain pause rule (judge off, what the PC plays, or no judge that can
        /// answer, which the log says once).</summary>
        internal IEndOfTurnJudge? TurnJudge(LiveConversationOperation operation)
        {
            if (operation.Listening is not { JudgeTurns: true, Pc: false } || turnJudge is null) return null;
            if (turnJudge.Available) return turnJudge;
            if (Interlocked.Exchange(ref turnJudgeMissingLogged, 1) == 0)
                ErrorLog.Warn($"End-of-turn judge: {turnJudge.Name} can't answer ({smartTurn?.Problem ?? "unavailable"}, and no Thinking-pool member can judge); " +
                    "the plain pause decides when you finished talking.");
            return null;
        }

        /// <summary>Stops always listening: the utterance being recorded is discarded and one being transcribed is canceled, and a
        /// reply started early for what it heard is let go.</summary>
        internal void StopListening(LiveListener listening)
        {
            listening.Revoke();
            listening.Worker.RequestCancellation();
            if (!listening.Pc) owner.LetGoEarly("listening stopped");
        }

        /// <summary>Whether this listener holds off right now. Hearing what this PC plays holds off only while Martlet speaks (or
        /// sings) and Windows can't leave Martlet's own voice out of it (never for barge-in: only the user interrupts). The
        /// microphone keeps listening while Martlet speaks or sings whenever it can tell Martlet's own voice from yours
        /// (<see cref="ListensOverMartlet"/>), so what you say then is heard and answered after; otherwise it holds off.
        /// <paramref name="echo"/>: the capture under way, whose own echo reduction decides once it has opened.</summary>
        internal bool Held(ListeningOptions options, EchoTimeline? echo = null)
        {
            if (options.Pc) return owner.pcSession.pcAudio?.WithoutMartlet != true && (Held(false) || owner.singing?.Playing == true);
            var over = ListensOverMartlet(options, echo);
            return Held(over) || !over && owner.singing?.Playing == true;
        }

        /// <summary>Whether the microphone can go on listening while Martlet speaks or sings: barge-in is on (talking over it is the
        /// point), echo reduction works (what the speakers play is taken out of what the microphone hears, and what is mostly their
        /// sound is let go like a cough), or the microphone is the FIXTURE one that hears only its clips. Echo reduction that is off,
        /// couldn't start or was lost can't tell Martlet's voice from yours: listening would hear Martlet and answer its own words.</summary>
        private bool ListensOverMartlet(ListeningOptions options, EchoTimeline? echo = null) =>
            options.BargeIn || owner.captureDevices is SimulatedMicrophone or KeptCaptureDeviceFactory { Inner: SimulatedMicrophone } ||
            options.ReduceEcho && owner.echoReducer?.Works(echo) == true;

        /// <summary>Always listening holds off while other setup work (a microphone test, Voice ID enrollment) owns the app slot,
        /// and, when it can't tell Martlet's own voice from yours (<paramref name="overMartlet"/> false), while Martlet speaks (a
        /// reply or a remark, plus a short tail for the room's echo), so it never hears itself.</summary>
        internal bool Held(bool overMartlet)
        {
            lock (owner.gate)
            {
                var now = owner.clock.GetTimestamp();
                if (owner.active is { Worker: not null } current && !current.OwnershipReleased)
                {
                    if (overMartlet) return false;
                    if (current.Turn?.Snapshot is { State: ConversationState.Playing } or { MayHavePlayed: true, OwnershipReleased: false })
                    {
                        spokeUntil = now + (long)(SpeechTail.TotalSeconds * owner.clock.TimestampFrequency);
                        return true;
                    }
                    return now < spokeUntil;
                }
                return owner.operations.IsRunning || !overMartlet && now < spokeUntil;
            }
        }

        internal static TimeSpan SpeechTail => TimeSpan.FromMilliseconds(300);

        /// <summary>How long always listening waits before it opens the microphone (or this PC's sound) again after this many
        /// failures in a row: soon after the first (a device that was busy or changing a moment ago often works at once), then
        /// longer, so a microphone that keeps failing isn't opened and dropped every few seconds.</summary>
        internal static TimeSpan MicrophoneRetry(int failures) =>
            TimeSpan.FromSeconds(failures switch { <= 1 => 1, 2 => 5, 3 => 10, 4 => 20, _ => 30 });

        private async Task<SetupWorkResult> ListenLoopAsync(LiveListener listening, CancellationToken token)
        {
            var pending = Task.CompletedTask;
            var failures = 0;
            ErrorCode? lastFailure = null;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (Held(listening.Options))
                    {
                        listening.Held = true;
                        await Task.Delay(TimeSpan.FromMilliseconds(50), owner.clock, token).ConfigureAwait(false);
                        continue;
                    }
                    listening.Held = false;
                    var utterance = Utterance(listening, token);
                    listening.Utterance = utterance;
                    byte[]? speech;
                    try
                    {
                        await utterance.Authorization.ValidateSettingsAsync(token).ConfigureAwait(false);
                        speech = await owner.CaptureSpeechAsync(utterance).ConfigureAwait(false);
                    }
                    catch (LiveActionException error) when (error.Code is "conversation.revoked" or "conversation.expired")
                    {
                        token.ThrowIfCancellationRequested();
                        utterance.Publish(new(error.Code, Finished: true));
                        continue;
                    }
                    catch (Exception error) when (error is ContractException or InvalidOperationException && !token.IsCancellationRequested)
                    {
                        // A microphone that can't be pressed right now is reported like a failed one and tried again shortly.
                        utterance.Publish(new("mic.Failed", Finished: true, AudioFailure: (error as ContractException)?.Code ?? ErrorCode.AudioDeviceUnavailable));
                        speech = null;
                    }
                    if (speech is null)
                    {
                        utterance.Hearing = false;
                        utterance.TalkingOver = false;
                        var status = utterance.Status;
                        TimeSpan? wait = null;
                        if (status.AudioFailure is { } failure)
                        {
                            wait = MicrophoneRetry(++failures);
                            listening.Retry = wait;
                            // Every failure for a while, then each change and every tenth, so a broken microphone can't flood the log.
                            if (failures <= 5 || failure != lastFailure || failures % 10 == 0)
                                LogMicrophoneFailure(listening, utterance, failure, failures, wait.Value);
                            lastFailure = failure;
                        }
                        else if (utterance.Capture?.Snapshot is { CanonicalSamples: > 0 }) Recovered();
                        if (status.Code is not ("mic.no_speech" or "listen.held"))
                            listening.Post(Result(utterance));
                        if (wait is { } delay)
                            await Task.Delay(delay, owner.clock, token).ConfigureAwait(false);
                        continue;
                    }
                    Recovered();
                    listening.BeginTranscribing();
                    utterance.SpeechEndedAt = owner.clock.GetTimestamp();
                    utterance.Hearing = false;
                    utterance.TalkingOver = false;
                    if (!listening.Options.Pc && !listening.Options.BargeIn && owner.Speaking == PlaybackMode.Reply)
                        ErrorLog.Info("Always listening heard you while Martlet spoke; without barge-in that doesn't stop the reply, " +
                            "and what you said is answered after it.");
                    if (!listening.Options.Pc)
                    {
                        // An utterance that ran to the recording limit has no detected end: count from now.
                        utterance.LatencyTimeline ??= new ReplyTimeline(owner.clock, ReplyTimeline.YouStopped);
                        utterance.LatencyTimeline.Mark("recording");
                    }
                    var previous = pending;
                    pending = Task.Run(() => TranscribeHeardAsync(previous, listening, utterance, speech, token), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new(SetupWorkOutcome.Canceled);
            }
            catch (LiveActionException error)
            {
                listening.Ended = error.Code;
                return new(SetupWorkOutcome.Failed);
            }
            finally
            {
                listening.Held = false;
                await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!listening.Pc) await wordsTail.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            // The microphone delivered sound and ended without failing: the failures in a row are over.
            void Recovered()
            {
                if (failures == 0) return;
                ErrorLog.Info($"Always listening: {(listening.Pc ? "hearing this PC's sound" : "the microphone")} works again after " +
                    $"{failures} failure{(failures == 1 ? "" : "s")} in a row.");
                failures = 0;
                lastFailure = null;
                listening.Retry = null;
            }
        }

        // Why always listening lost the microphone, for the log (a user's log is the only place that shows it): the error and what
        // Windows reported (the device's own detail), whether the microphone gave any sound first, which microphone and whether echo
        // reduction wrapped it, and the wait before the next try. Never a device name or anything heard.
        private void LogMicrophoneFailure(LiveListener listening, LiveConversationOperation utterance, ErrorCode failure, int failures,
            TimeSpan wait)
        {
            var snapshot = utterance.Capture?.Snapshot;
            var samples = snapshot?.CanonicalSamples ?? 0;
            var when = samples > 0 ? $"after {samples / 16000.0:0.0} s of sound" : "before any sound";
            var why = snapshot?.FailureDetail is { Length: > 0 } detail ? $": {detail}" : "";
            var details = listening.Pc ? failure + why
                : $"{failure}{why}, " + (utterance.Authorization.Configuration.Audio?.Input.EndpointId is null
                    ? "Windows' default microphone" : "the microphone chosen in Companion")
                + (listening.Options.ReduceEcho && owner.echoReducer is not null ? ", echo reduction on" : "");
            ErrorLog.Warn($"Always listening: {(listening.Pc ? "hearing this PC's sound" : "the microphone")} failed {when} ({details}); " +
                $"Martlet opens it again in {wait.TotalSeconds:0} s ({failures} failure{(failures == 1 ? "" : "s")} in a row).");
        }

        // One utterance: its own authorization (revoked with listening), recorded with the listener's options.
        private LiveConversationOperation Utterance(LiveListener listening, CancellationToken token)
        {
            lock (owner.gate)
            {
                if (owner.disposed || owner.paused || owner.muted || owner.locked) throw new LiveActionException("conversation.controls_blocked");
                var selected = owner.configuration ?? throw new LiveActionException("conversation.setup_required");
                if (selected.Unavailable(false, true) is not null) throw new LiveActionException("conversation.configuration_unsupported");
                var epoch = Volatile.Read(ref owner.listenEpoch);
                var revision = listening.Revision;
                var authorization = new ConversationAuthorization(selected, voice: false, microphone: true, owner.clock,
                    () => Volatile.Read(ref owner.listenEpoch) == epoch && listening.Revision == revision, owner.settings.LoadAsync, owner.vault, token);
                authorization.BindWorker(token);
                return new(authorization, token)
                {
                    Worker = listening.Worker, Listen = true, Listening = listening.Options, Voiceprint = listening.Voiceprint
                };
            }
        }

        private static HeardSpeech Result(LiveConversationOperation utterance) => new(utterance.Status,
            utterance.Status.Code is "listen.heard" or "listen.ignored" ? utterance.Transcript : null, utterance.Transcription?.Confidence, utterance.Heard,
            utterance.SpeakerCheck, utterance.Voiceprint, utterance.Status.Code == "listen.heard" ? utterance.Recording : null,
            utterance.LatencyTimeline, utterance.Status.Code == "listen.ignored" ? utterance.Ignored : null,
            utterance.Status.Code == "listen.heard" ? utterance.Interrupts : null, utterance.SpeechStartedAt,
            utterance.Status.Code == "listen.heard" ? utterance.Words : null, utterance.SpeechEndedAt,
            utterance.Status.Code == "listen.heard" ? utterance.VoiceNote : null);

        /// <summary>Whether what was just heard goes straight to Thinking as the recording alone, with speech-to-text beside the
        /// reply: Thinking may hear it and the straight path is chosen (Companion › Listening), no audio model of its own takes
        /// recordings, the Thinking model hears and hasn't
        /// refused a recording this session, Home Assistant's Assist doesn't need the words first, and it wasn't said over Martlet
        /// while it speaks without a quick check deciding (its words decide whether that stops Martlet).</summary>
        internal bool GoesStraight(ListeningOptions options, LiveConversationOperation utterance)
        {
            if (options is not { Straight: true, Hear: true, Pc: false }) return false;
            var configured = utterance.Authorization.Configuration;
            if (!owner.senseSession.ThinkingTakesVoice(configured)) return false;
            if (!options.HearsWith(owner.senseSession.RecordingStaysOnThisPc(configured)) || configured.Hearing() != HearingSupport.Supported) return false;
            if (owner.routing.Deaf(configured)) return false;
            if (owner.smartHome is { ControlEnabled: true, ModelToolsEnabled: false }) return false;
            return !(options.BargeIn && owner.Speaking is not null && utterance.TalkOver is null);
        }

        // Voice ID, then speech-to-text, one utterance after another (so what you said stays in order) while the next is recorded.
        // Straight to Thinking, the recording is handed on right after Voice ID and who spoke, and speech-to-text runs on its own
        // chain (wordsTail), off the reply's path.
        private async Task TranscribeHeardAsync(Task previous, LiveListener listening, LiveConversationOperation utterance, byte[] speech,
            CancellationToken token)
        {
            var handedOn = false;
            try
            {
                await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                BoundedWaveAudio? audio;
                try { audio = owner.Screen(utterance, speech); }
                finally { CryptographicOperations.ZeroMemory(speech); }
                if (audio is null) return;
                if (utterance.Voiceprint is not null) utterance.LatencyTimeline?.Mark("Voice ID");
                if (Hearable(audio) && GoesStraight(listening.Options, utterance))
                {
                    utterance.Heard = await owner.HeardAsync(utterance, token).ConfigureAwait(false);
                    if (utterance.Recognition is not null) utterance.LatencyTimeline?.Mark("voice recognition");
                    utterance.Recording = audio;
                    // A quick check while it was said over Martlet may already have decided that it stops Martlet.
                    utterance.Interrupts = utterance.TalkOver?.Decision;
                    var spokenWords = utterance.Words = new SpokenWords(owner.clock) { Voiced = utterance.Voiced };
                    utterance.Publish(new("listen.heard", Finished: true));
                    listening.Post(Result(utterance));
                    handedOn = true;
                    listening.EndTranscribing();
                    var before = wordsTail;
                    wordsTail = Task.Run(() => TranscribeWordsAsync(before, listening, utterance, audio, spokenWords, token), CancellationToken.None);
                    return;
                }
                // An audio model of its own hears it beside speech-to-text and puts what the words miss into words (never on the
                // reply's path: the reply takes them only if they are ready in time).
                utterance.VoiceNote = owner.senseSession.StartVoiceNote(utterance, audio);
                // Never longer than the uploads' own 30 s deadlines (one for each piece of a long turn), even if a native boundary
                // ignores them.
                using var deadline = new CancellationTokenSource(LiveConversationConfiguration.TranscriptionTime(audio), owner.clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                // What the PC plays is transcribed beside the microphone, with its own speech-to-text credentials.
                var pc = listening.Options.Pc;
                if (pc) Volatile.Write(ref owner.pcSession.pcTranscribing, utterance);
                else Volatile.Write(ref transcribing, utterance);
                TranscriptionResult? result;
                try { result = await owner.TranscribeAsync(utterance, audio, pc ? owner.pcSession.pcTranscription : listenTranscription, linked.Token).ConfigureAwait(false); }
                finally
                {
                    if (pc) Interlocked.CompareExchange(ref owner.pcSession.pcTranscribing, null, utterance);
                    else Interlocked.CompareExchange(ref transcribing, null, utterance);
                }
                if (result is null) return;
                utterance.LatencyTimeline?.Mark("speech-to-text");
                utterance.Transcript = result.Text;
                // What isn't words (mm, a cough, "Thank you." made up from noise) never becomes a turn or stops Martlet. Local and
                // instant: it adds nothing to the time until Martlet answers.
                var options = listening.Options;
                var words = owner.WordsContext(utterance, utterance.Voiced, result.Evidence, utterance.Speech);
                if (UtteranceFilter.Check(result.Text, words, options.WordCheck) is { Keep: false } ignored)
                {
                    utterance.Ignored = ignored;
                    if (!pc)
                    {
                        // Only a sound or filler: the live floor stops listening for it.
                        owner.floor.NotWords(ignored.Reason);
                        ErrorLog.Info($"Always listening ignored what it heard: {ignored.Reason} ({ignored.Kind}" +
                            (utterance.Voiced is { } voiced ? $", {voiced.TotalMilliseconds:0} ms of voice" : "") +
                            (utterance.Speech is { } spoken ? $" in {spoken.TotalMilliseconds:0} ms of speech" : "") +
                            (result.Evidence is { } evidence ? ", " + Describe(evidence) : "") + $", word check {options.WordCheck}).");
                    }
                    utterance.Publish(new("listen.ignored", Finished: true));
                    return;
                }
                // Real words from the user's microphone: the live turn comes first from now on.
                if (!pc) owner.FloorWords(utterance, result.Text, words, options.WordCheck, "the transcript had real words");
                // Said over Martlet: its words may stop the reply (a quick check while it was said may already have decided, or paused
                // it for the judge).
                if (options is { BargeIn: true, Pc: false } && owner.Speaking is { } mode)
                    utterance.Interrupts = await owner.InterruptsAsync(utterance, result.Text, words, options, mode,
                        result.Evidence?.MeanProbability).ConfigureAwait(false);
                // Said while Martlet sings: asking it to stop ends the song musically; a quick check may already have, and then
                // the note quotes everything that was said.
                if (!options.Pc && !owner.StopSongIfAsked(utterance, result.Text, words, options.WordCheck) && utterance.StoppedSong)
                    owner.singing?.Heard(result.Text ?? "");
                utterance.Heard = await owner.HeardAsync(utterance, linked.Token).ConfigureAwait(false);
                if (utterance.Recognition is not null) utterance.LatencyTimeline?.Mark("voice recognition");
                var heardBy = utterance.Authorization.Configuration;
                if (listening.Options.HearsWith(owner.senseSession.RecordingStaysOnThisPc(heardBy)) && owner.senseSession.ThinkingTakesVoice(heardBy) && Hearable(audio, "Thinking"))
                    utterance.Recording = audio;
                utterance.Publish(new("listen.heard", Finished: true));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                utterance.Publish(new("conversation.canceled", Finished: true));
            }
            catch (OperationCanceledException)
            {
                utterance.Publish(new("stt.deadline_exceeded", Finished: true));
            }
            catch (LiveActionException error)
            {
                utterance.Publish(new(error.Code, Finished: true));
            }
            catch (ContractException error)
            {
                utterance.Publish(new("conversation.invalid_input", Finished: true, AudioFailure: error.Code));
            }
            finally
            {
                if (!handedOn)
                {
                    if (!token.IsCancellationRequested) listening.Post(Result(utterance));
                    listening.EndTranscribing();
                }
                // What always listening heard was let go (not words, another voice, a failure): a reply started early for it goes too.
                if (utterance.EarlyStarted is { } early && utterance.Status.Code != "listen.heard")
                    owner.LetGoEarly(early, EarlyReplyRecord.Changed, "what you said was let go");
                // Nobody needs the audio model's words about what was let go.
                if (utterance.Status.Code != "listen.heard") utterance.VoiceNote?.Cancel();
            }
        }

        // Speech-to-text for what went straight to Thinking, beside the reply and one utterance after another: the words go to the
        // talk window, the conversation and memory once they come. The word check only labels them (Thinking already heard it).
        private async Task TranscribeWordsAsync(Task previous, LiveListener listening, LiveConversationOperation utterance,
            BoundedWaveAudio audio, SpokenWords words, CancellationToken token)
        {
            try
            {
                await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                // Speech-to-text and Thinking both on this PC: transcribe once the reply is under way, so the two never compete for
                // the processor before the first audio (at most TranscribeAfter later, for words no reply asked for yet).
                if (utterance.Authorization.Configuration is { LocalThinking: true } local && local.LocalStt())
                    await Task.WhenAny(words.MayTranscribe, Task.Delay(TranscribeAfter, owner.clock, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using var deadline = new CancellationTokenSource(LiveConversationConfiguration.TranscriptionTime(audio), owner.clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                var started = owner.clock.GetTimestamp();
                Volatile.Write(ref transcribing, utterance);
                TranscriptionResult? result;
                try { result = await owner.TranscribeAsync(utterance, audio, listenTranscription, linked.Token).ConfigureAwait(false); }
                finally { Interlocked.CompareExchange(ref transcribing, null, utterance); }
                if (result is null)
                {
                    words.Failed(utterance.Status.Code);
                    return;
                }
                var context = owner.WordsContext(utterance, utterance.Voiced, result.Evidence, utterance.Speech);
                var ignored = UtteranceFilter.Check(result.Text, context, listening.Options.WordCheck) is { Keep: false } filtered ? filtered : null;
                words.Heard(result.Text ?? "", result.Confidence, ignored, owner.clock.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { words.Failed("conversation.canceled"); }
            catch (OperationCanceledException) { words.Failed("stt.deadline_exceeded"); }
            catch (LiveActionException error) { words.Failed(error.Code); }
            catch (ContractException) { words.Failed("conversation.invalid_input"); }
            finally
            {
                // Never left waiting: whatever happened, the words are ready (with or without any).
                words.Failed("conversation.canceled");
            }
        }
    }
}
