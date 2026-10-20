using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Martlet.Audio;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Memory;
using Martlet.Participation;
using Martlet.Providers;

namespace Martlet.Desktop;

internal sealed partial class LiveConversationController
{
    /// <summary>How the user's turn is heard (docs/CONVERSATION.md): recording it (<see cref="CaptureAsync"/>), finding where the
    /// speech ends (<see cref="EndpointAsync"/>), putting it into words (<see cref="TranscribeAsync"/>, with Listening's
    /// stand-in when its route fails) and who spoke (<see cref="HeardAsync"/>).</summary>
    private sealed class TurnCapture(LiveConversationController owner)
    {
        internal async Task<BoundedWaveAudio?> CaptureAsync(LiveConversationOperation operation)
        {
            var speech = await CaptureSpeechAsync(operation).ConfigureAwait(false);
            if (speech is null) return null;
            try { return Screen(operation, speech); }
            finally { CryptographicOperations.ZeroMemory(speech); }
        }

        // Records one utterance and returns a private copy of the speech to send (the caller clears it), or null with the reason
        // published. The microphone is released before this returns.
        internal async Task<byte[]?> CaptureSpeechAsync(LiveConversationOperation operation)
        {
            var permission = operation.Authorization;
            permission.Check();
            var audio = permission.Configuration.Audio!;
            var selected = audio.Input;
            // What the PC plays is its own device; echo reduction hears the output Martlet's own voice plays on (the chosen one, or
            // Windows' default).
            var pc = operation.Listening?.Pc == true;
            // Hands-free listening also learns, frame by frame, which of what it hears was the speakers' sound.
            if (!pc && operation.HandsFree && operation.Listening?.ReduceEcho == true && owner.echoReducer is not null)
                operation.Echo = new EchoTimeline(LiveConversationConfiguration.CaptureDuration);
            var devices = pc ? owner.pcSession.pcAudio ?? throw new LiveActionException("conversation.configuration_unsupported")
                : operation.Listening?.ReduceEcho == true && owner.echoReducer is not null
                ? owner.echoReducer.For(audio.Output.EndpointId, operation.Echo) : owner.captureDevices;
            await using var microphone = new MicrophoneCapture(owner.runtime.SessionId, devices,
                new() { MaximumDuration = LiveConversationConfiguration.CaptureDuration, MaximumPcmBytes = LiveConversationConfiguration.CaptureBytes }, owner.clock);
            var request = new CaptureRequest(owner.Ids(), Interlocked.Increment(ref owner.captureEpoch),
                pc ? new(InputPolicy.FollowDefaultOnNextPress)
                    : new(selected.EndpointId is null ? InputPolicy.FollowDefaultOnNextPress : InputPolicy.FixedEndpoint, selected.EndpointId),
                LiveConversationConfiguration.CaptureDuration, permission.Deadline(LiveConversationConfiguration.CapturePermission, fromAcceptance: true));
            var run = microphone.Press(request, new(request, true), operation.OriginalCaller);
            operation.Attach(run);
            operation.Publish(new(operation.HandsFree ? "mic.listening" : "mic.capturing"));
            try
            {
                SpeechRange? heard = null;
                if (operation.HandsFree)
                {
                    heard = await EndpointAsync(operation, run).ConfigureAwait(false);
                    if (heard is null)
                    {
                        // A microphone that failed (absent, busy, denied) ends the run by itself; say so instead of "no speech",
                        // which would quietly re-arm listening on a microphone that can't work.
                        var failed = run.Completion.IsCompleted ? await run.Completion.ConfigureAwait(false) : null;
                        var held = operation.Status.Code == "listen.held";
                        await run.CancelAsync().ConfigureAwait(false);
                        await run.Completion.ConfigureAwait(false);
                        permission.Check();
                        operation.Publish(failed is { State: CaptureState.Failed }
                            ? new LiveConversationStatus("mic." + failed.State, Finished: true, AudioFailure: failed.Error?.Code)
                            : new LiveConversationStatus(held ? "listen.held" : "mic.no_speech", Finished: true));
                        return null;
                    }
                }
                var terminal = await run.Completion.ConfigureAwait(false);
                permission.Check();
                using var utterance = run.TakeUtterance();
                if (terminal.State != CaptureState.Completed || utterance is null)
                {
                    operation.Publish(new("mic." + terminal.State, Finished: true, AudioFailure: terminal.Error?.Code));
                    return null;
                }
                byte[] pcm = new byte[utterance.ByteCount];
                try
                {
                    permission.Check();
                    utterance.CopyPcmTo(pcm);
                    var total = pcm.Length / 2;
                    // Hands-free uploads only the detected speech (with pre-roll/tail), not the idle wait before it.
                    var start = Math.Min(heard?.StartSample ?? 0, total);
                    var end = Math.Min(heard?.EndSampleExclusive ?? total, total);
                    var speech = pcm.AsSpan(start * 2, Math.Max(0, end - start) * 2).ToArray();
                    // The end-of-turn judge's quick transcript is reused only for exactly this audio.
                    if (operation.QuickWords is { } quick && !quick.Pcm.AsSpan().SequenceEqual(speech))
                    {
                        operation.QuickWords = null;
                        ErrorLog.Info("End of turn: the speech kept differs from the quick transcript's; speech-to-text transcribes it again.");
                    }
                    return speech;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pcm);
                    utterance.Dispose();
                }
            }
            finally
            {
                await run.CancelAsync().ConfigureAwait(false);
                var release = await run.DeviceRelease.ConfigureAwait(false);
                if (!release.Released)
                {
                    operation.Publish(new("mic.cleanup_quarantined", Finished: true, Quarantined: true, AudioFailure: release.Error?.Code));
                    await owner.quarantine.Task.ConfigureAwait(false);
                }
            }
        }

        // Who is talking (alongside speech-to-text), then Voice ID (before anything is uploaded): the audio to transcribe, or null
        // with the reason published. Both look at the first minute of a longer turn (what the speaker engine takes at once).
        internal BoundedWaveAudio? Screen(LiveConversationOperation operation, byte[] speech)
        {
            var permission = operation.Authorization;
            permission.Check();
            var voice = speech.AsSpan(0, Math.Min(speech.Length, VoiceCheckBytes));
            // Voices are recognized (and learned) only from the microphone, never from what the PC plays. People hears every voice
            // the microphone hears, also one Voice ID then ignores: Voice ID only decides whether Martlet answers.
            if (operation.Listening?.Pc != true) owner.Recognize(operation, voice);
            if (operation.Voiceprint is { } voiceprint)
            {
                operation.Publish(new("speaker.checking"));
                var check = SpeakerVerifier.Check(VoiceIdentity.Encoder, voiceprint.Embedding, voiceprint.Threshold, voice);
                permission.Check();
                operation.SpeakerCheck = check;
                if (check.Verdict != SpeakerVerdict.User)
                {
                    operation.Publish(new(check.Verdict == SpeakerVerdict.TooShort ? "speaker.too_short" : "speaker.not_user", Finished: true));
                    return null;
                }
                operation.Publish(new("speaker.verified"));
            }
            if (speech.Length < 2)
            {
                operation.Publish(new("mic.no_speech", Finished: true));
                return null;
            }
            var wave = BoundedWaveAudio.FromPcm(CapturedUtterance.Format, speech);
            operation.Publish(new("mic.transferred_and_cleared"));
            return wave;
        }

        /// <summary>Whether a recording of what was said may go with its message to a model that hears: at most
        /// <see cref="BoundedTextInput.MessageAudioSeconds"/>. A longer turn goes as its whole transcript only; the log says so when
        /// <paramref name="to"/> names who would have heard it.</summary>
        internal static bool Hearable(BoundedWaveAudio audio, string? to = null)
        {
            if (audio.Duration.TotalSeconds <= BoundedTextInput.MessageAudioSeconds) return true;
            if (to is not null)
                ErrorLog.Info($"Your recording ({audio.Duration.TotalSeconds:0} s) is longer than the {BoundedTextInput.MessageAudioSeconds:0} s " +
                    $"a message's recording may be: {to} gets its whole transcript instead.");
            return false;
        }

        // Speech-to-text for one recorded utterance through its one-use upload permission; null (with the reason published) when
        // nothing usable came back.
        internal async Task<TranscriptionResult?> TranscribeAsync(LiveConversationOperation operation, BoundedWaveAudio audio,
            OpenAiTranscriptionAdapter openAi, CancellationToken worker)
        {
            var pieces = audio.Split(LiveConversationConfiguration.TranscriptionPiece);
            if (pieces.Count == 1) return await TranscribePieceAsync(operation, audio, openAi, worker).ConfigureAwait(false);
            // Talking longer than one speech-to-text request hears: each piece, cut at a pause, is heard in turn and the words are
            // joined. The quick transcript (of at most a minute) never covers all of it.
            if (operation.QuickWords is { } quick)
            {
                CryptographicOperations.ZeroMemory(quick.Pcm);
                operation.QuickWords = null;
            }
            operation.Authorization.HearInPieces(pieces.Count);
            ErrorLog.Info($"Speech-to-text hears {audio.Duration.TotalSeconds:0} s of talking in {pieces.Count} pieces.");
            var heard = new List<TranscriptionResult>(pieces.Count);
            var offsets = new List<TimeSpan>(pieces.Count);
            var at = TimeSpan.Zero;
            foreach (var part in pieces)
            {
                if (await TranscribePieceAsync(operation, part, openAi, worker, piece: true).ConfigureAwait(false) is not { } result)
                    return null;
                heard.Add(result);
                offsets.Add(at);
                at += part.Duration;
            }
            var whole = TranscriptionResult.Join(heard, offsets);
            operation.Transcription = whole;
            if (whole.Outcome == TranscriptionOutcome.Completed) return whole;
            operation.Publish(new("stt." + whole.Outcome, Finished: true));
            return null;
        }

        // One speech-to-text request for a whole turn, or for one piece of a longer one (piece: a piece without speech is returned,
        // not published, since the turn's other pieces may have words).
        internal async Task<TranscriptionResult?> TranscribePieceAsync(LiveConversationOperation operation, BoundedWaveAudio audio,
            OpenAiTranscriptionAdapter openAi, CancellationToken worker, bool piece = false)
        {
            operation.Authorization.Check(worker);
            await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
            var stt = operation.Authorization.Configuration.Route(SetupRole.Stt);
            // Listening's own route failed moments ago: Parakeet on this PC hears this turn at once, instead of waiting for the route
            // to fail again (a computer that is off takes seconds to time out). The route is asked again after StandInFor.
            if (owner.routing.RouteFailedAgo(stt) is { } ago && owner.routing.StandInModel(stt) is { } standIn)
            {
                operation.Publish(new("stt.uploading"));
                return await StandInAsync(operation, audio, null, standIn, worker, ago, piece).ConfigureAwait(false);
            }
            var context = new ProviderRequestContext
            {
                Ids = owner.Ids(), Epoch = operation.Capture!.Snapshot.Epoch,
                Deadline = operation.Authorization.Deadline(TimeSpan.FromSeconds(30))
            };
            var permission = operation.Authorization.AuthorizeAudio(context);
            operation.Publish(new("stt.uploading"));
            operation.BeginTranscription(owner.clock, context.Deadline);
            TranscriptionResult result;
            // The end-of-turn judge's quick transcript of exactly this speech (Parakeet on this PC) is used instead of a second one.
            var quick = operation.QuickWords;
            operation.QuickWords = null;
            ReusedWords? reused = null;
            try
            {
                // Listening handed to a paired host: the utterance goes only to its pinned gateway.
                result = operation.Authorization.Configuration.SttHostTarget() is { } listener
                    ? await owner.hostTranscription.TranscribeAsync(context, listener, stt.ModelId, audio,
                        LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false)
                    // Parakeet on this PC: transcribed in memory here, nothing is sent anywhere.
                    : operation.Authorization.Configuration.LocalStt()
                        ? await (quick is not null && quick.ModelId == stt.ModelId && owner.localWords is not null
                                ? new LocalTranscriptionAdapter(reused = new ReusedWords(quick.Transcript, owner.localWords), owner.clock)
                                : owner.localTranscription ?? throw new LiveActionException("conversation.configuration_unsupported"))
                            .TranscribeAsync(context, stt.ModelId, audio, LiveConversationConfiguration.TranscriptionLimits, permission,
                                operation.OriginalCaller, worker).ConfigureAwait(false)
                    : await openAi.TranscribeAsync(context, stt.ModelId, audio,
                        LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false);
            }
            finally
            {
                operation.EndTranscription();
                if (quick is not null) CryptographicOperations.ZeroMemory(quick.Pcm);
            }
            if (reused?.Reused == true)
                ErrorLog.Info($"End of turn: speech-to-text reused the quick transcript started {owner.clock.GetElapsedTime(quick!.StartedAt).TotalMilliseconds:0} ms ago (no second transcription).");
            operation.Authorization.Check(worker);
            // Listening's own route (a paired host or OpenAI) failed: Parakeet on this PC's processor hears the same utterance, still
            // in memory, when a model is downloaded here. A route that answers is never held up by it.
            if (result.Outcome is TranscriptionOutcome.Failed or TranscriptionOutcome.DeadlineExceeded &&
                result.Failure?.Code != ProviderFailureCode.AudioLimit && owner.routing.StandInModel(stt) is { } model)
                return await StandInAsync(operation, audio, result, model, worker, piece: piece).ConfigureAwait(false);
            operation.Transcription = result;
            // The route answered: turns ask it first again.
            if (result.Outcome is TranscriptionOutcome.Completed or TranscriptionOutcome.NoSpeech) owner.routing.AskRoute();
            if (result.Outcome == TranscriptionOutcome.Completed)
            {
                owner.routing.Succeeded(SetupRole.Stt);
                return result;
            }
            if (piece && result.Outcome == TranscriptionOutcome.NoSpeech) return result;
            if (result.Outcome != TranscriptionOutcome.NoSpeech)
                owner.routing.LogFailure("Transcription", operation.Authorization.Configuration, SetupRole.Stt, Outcome(result));
            operation.Publish(new("stt." + result.Outcome, Finished: true, ProviderFailure: result.Failure?.Code));
            return null;
        }

        /// <summary>Listening's own route failed (<paramref name="failed"/>), or failed <paramref name="ago"/> and isn't asked again
        /// yet (<paramref name="failed"/> null): Parakeet <paramref name="model"/> on this PC's processor hears the utterance with its
        /// own local-only permission, so the turn goes on and nothing is sent anywhere. The route's failure stays on Home until the
        /// route answers again. Null (with the reason published) when nothing usable came back; a stand-in that fails after the
        /// route did publishes the route's own failure.</summary>
        internal async Task<TranscriptionResult?> StandInAsync(LiveConversationOperation operation, BoundedWaveAudio audio,
            TranscriptionResult? failed, string model, CancellationToken worker, TimeSpan? ago = null, bool piece = false)
        {
            var context = new ProviderRequestContext
            {
                Ids = owner.Ids(), Epoch = operation.Capture!.Snapshot.Epoch,
                Deadline = operation.Authorization.Deadline(TimeSpan.FromSeconds(30))
            };
            var permission = operation.Authorization.AuthorizeStandIn(context, model);
            operation.StandIn = model;
            operation.BeginTranscription(owner.clock, context.Deadline);
            var started = owner.clock.GetTimestamp();
            TranscriptionResult heard;
            try
            {
                heard = await owner.localTranscription!.TranscribeAsync(context, model, audio, LiveConversationConfiguration.TranscriptionLimits,
                    permission, operation.OriginalCaller, worker).ConfigureAwait(false);
            }
            finally { operation.EndTranscription(); }
            var took = $"{owner.clock.GetElapsedTime(started).TotalMilliseconds:0} ms";
            var stoodIn = heard.Outcome is TranscriptionOutcome.Completed or TranscriptionOutcome.NoSpeech;
            // Later turns skip the route only while the stand-in hears them; one that fails sends them back to the route.
            if (stoodIn && failed is not null)
                owner.routing.StoodIn(operation.Authorization.Configuration.Route(SetupRole.Stt), started);
            else if (!stoodIn && heard.Outcome != TranscriptionOutcome.Canceled)
                owner.routing.AskRoute();
            var standIn = stoodIn ? $"Parakeet {model} on this PC heard it instead in {took}"
                : heard.Outcome == TranscriptionOutcome.Canceled ? null
                : $"Parakeet {model} on this PC couldn't hear it either ({Outcome(heard)}, {took})";
            if (failed is not null)
                owner.routing.LogFailure("Transcription", operation.Authorization.Configuration, SetupRole.Stt, Outcome(failed) + (standIn is null ? "" : "; " + standIn));
            else if (standIn is not null)
            {
                var line = $"Listening: {standIn}, without asking Listening's own route: it failed {ago!.Value.TotalSeconds:0} s ago " +
                    $"and is asked again {StandInFor.TotalSeconds:0} s after that.";
                if (stoodIn) ErrorLog.Info(line);
                else ErrorLog.Warn(line);
            }
            operation.Authorization.Check(worker);
            var result = failed is not null && heard.Outcome is TranscriptionOutcome.Failed or TranscriptionOutcome.DeadlineExceeded ? failed : heard;
            operation.Transcription = result;
            if (result.Outcome == TranscriptionOutcome.Completed || piece && result.Outcome == TranscriptionOutcome.NoSpeech)
            {
                // The reply latency line names the model that heard the turn.
                if (operation.LatencyTimeline is { } timeline) timeline.StandIn = model;
                return result;
            }
            operation.Publish(new("stt." + result.Outcome, Finished: true, ProviderFailure: result.Failure?.Code));
            return null;
        }

        internal static string Outcome(TranscriptionResult result) =>
            $"outcome {result.Outcome}" + (result.Failure?.Code is { } code ? $", provider {code}" : "");

        /// <summary>The engine's evidence in a few numbers for the log (never the words).</summary>
        internal static string Describe(TranscriptionEvidence evidence) => string.Join(", ", new[]
        {
            evidence.MeanProbability is { } mean ? $"{evidence.Engine} mean probability {mean:0.00}" : null,
            evidence.MinimumProbability is { } least ? $"lowest {least:0.00}" : null,
            evidence.NoSpeechProbability is { } silence ? $"no-speech {silence:0.00}" : null,
            evidence.AverageLogProbability is { } average ? $"average log probability {average:0.00}" : null
        }.OfType<string>());

        // Recognition runs alongside speech-to-text and is usually done first; a slow one never holds the reply back for long.
        internal async Task<HeardVoices?> HeardAsync(LiveConversationOperation operation, CancellationToken worker)
        {
            if (operation.Recognition is not { } pending) return null;
            var done = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3), owner.clock)).ConfigureAwait(false);
            operation.Authorization.Check(worker);
            if (done != pending) return null;
            var heard = await pending.ConfigureAwait(false);
            if (heard.Voices.Count > 0)
            {
                operation.Publish(new("voices.recognized"));
                owner.NoteVoices(heard);
            }
            return heard;
        }

        // Reads the capture's own 20 ms frames (no second audio queue) and releases it when the speaker pauses.
        // Returns the speech range to send, or null when nobody spoke before the idle restart. With echo reduction, sound that is
        // mostly what the speakers played (Martlet's own voice, a video) is let go like a cough: it is never you. Only the
        // microphone talks over Martlet, and only with real words: while Martlet speaks, BargeInGate has what was said so far
        // transcribed by Parakeet on this PC and BargeInPolicy decides (elsewhere the utterance's own transcript decides).
        internal async Task<SpeechRange?> EndpointAsync(LiveConversationOperation operation, CaptureRun run)
        {
            var settings = operation.Listening!.Activity;
            // With the end-of-turn judge, the detector itself only ends speech after the longer pause for unfinished speech; the
            // gate ends it sooner when the judge says the turn is complete, and at the plain pause otherwise.
            var judge = owner.listeningSession.TurnJudge(operation);
            var turn = judge is null ? null : new EndOfTurnGate(settings.EndSilence, owner.listeningSession.EndOfTurn);
            var detector = new EnergyVoiceActivityDetector(turn is null ? settings : settings with { EndSilence = turn.DetectorEndSilence });
            Task<(EndOfTurnJudgement? Judgement, Exception? Error, long At)>? judging = null;
            using var judgeCancel = CancellationTokenSource.CreateLinkedTokenSource(operation.OriginalCaller);
            QuickWords? quick = null;
            CancellationTokenSource? quickCancel = null;
            int judgedPause = 0, quiet = 0;
            long judgeStartedAt = 0, judgeEndedAt = 0;
            EndOfTurnJudgement? judgement = null;
            Exception? judgeError = null;
            var decided = EndOfTurnStep.Wait;
            // Companion › Listening › Start replies early: with Parakeet on this PC, a reply starts early on the quick transcript of
            // the pause under way (without a judge, the quick transcript still starts at the same short pause) and is let go when
            // the user's own voice comes back. Pauses are counted here for both.
            var early = owner.EarlyGateFor(operation);
            LiveConversationOperation? earlyReply = null;
            int pauses = 0, quickPause = -1, quickEnd = 0;
            bool quickTried = false, earlyKept = false;
            bool? quickWorth = null;
            var quickFrames = Math.Max(1, (int)Math.Ceiling(owner.listeningSession.EndOfTurn.JudgeAfter.TotalMilliseconds / 20));
            var plainFrames = Math.Max(1, (int)Math.Round(settings.EndSilence.TotalMilliseconds / 20));
            var talkOver = operation.Listening.Pc ? null : new TalkOverDetector();
            // Quick checks of the words said over Martlet: for barge-in, and while it sings (to hear "stop singing" at once).
            var bargeIn = operation is { Listen: true, Listening.Pc: false } && (operation.Listening.BargeIn || owner.singing is not null) &&
                WordsCheck(operation) is { } check ? (Gate: new BargeInGate(operation.Listening.WordCheck), Check: check) : default;
            Task? checking = null;
            var echo = operation.Echo;
            var minimumFrames = (int)(ListeningOptions.MinimumUtterance.TotalMilliseconds / 20);
            var frame = new byte[EnergyVoiceActivityDetector.FrameBytes];
            // Running counts of loud frames that were a voice the speakers don't explain, of those that were the speakers' sound, and
            // of all frames (loud or not) the speakers explain.
            var userSum = new List<int> { 0 };
            var speakerSum = new List<int> { 0 };
            var explainedSum = new List<int> { 0 };
            var started = owner.clock.GetTimestamp();
            int index = 0, accepted = -1;
            try
            {
                while (!run.Completion.IsCompleted)
                {
                    bool copied;
                    do
                    {
                        try { copied = run.TryCopyMonoFrame(index, frame); }
                        catch (OperationCanceledException) { copied = false; }
                        if (!copied) break;
                        var speakers = echo?.Speakers((long)index * EnergyVoiceActivityDetector.FrameSamples, EnergyVoiceActivityDetector.FrameSamples) == true;
                        index++;
                        var silenceBefore = detector.SilenceFrames;
                        var transition = detector.Process(frame);
                        if (transition == VoiceActivityTransition.None && silenceBefore == 0 && detector.SilenceFrames > 0) pauses++;
                        operation.VoiceLevel = detector.LastLevelDb;
                        var loud = detector.LastFrameLoud;
                        userSum.Add(userSum[^1] + (loud && !speakers ? 1 : 0));
                        speakerSum.Add(speakerSum[^1] + (loud && speakers ? 1 : 0));
                        explainedSum.Add(explainedSum[^1] + (speakers ? 1 : 0));
                        talkOver?.Process(loud, speakers);
                        // The user's own voice (speech, not a click or a key press; never the speakers' sound or what this PC plays)
                        // puts the live floor at Listening.
                        if (!operation.Listening.Pc && LiveFloor.Voice(detector, speakers)) owner.floor.Heard();
                        // Paused for what was said over Martlet: how long the user talks on decides too (BargeInHold).
                        operation.Held?.Hold.Frame(loud && !speakers);
                        // Talking over Martlet: once the voice has gone on long enough (or a short word just ended), what was said so
                        // far is checked for words without waiting for the pause.
                        if (bargeIn.Gate?.Process(loud, speakers, checking is { IsCompleted: false }) == true && !operation.TalkingOver &&
                            owner.Speaking is { } mode && (operation.Listening.BargeIn || mode == PlaybackMode.Song))
                            checking = owner.CheckWordsAsync(operation, run, bargeIn.Gate, bargeIn.Check, index, mode);
                        if (turn is not null && transition == VoiceActivityTransition.None && detector.Speaking)
                        {
                            TakeJudgement();
                            var step = turn.Step(detector.SilenceFrames, accepted >= 0);
                            if (turn.WentOn is { } unfinished)
                            {
                                // The judge was right that it wasn't over: the next pause is judged again.
                                owner.listeningSession.RecordTurn(new(owner.clock.GetUtcNow(), EndOfTurnDecision.WentOn, TimeSpan.FromMilliseconds(quiet * 20),
                                    settings.EndSilence, Took(), unfinished.Probability, judge!.Name));
                                DropQuick();
                            }
                            quiet = detector.SilenceFrames;
                            if (step == EndOfTurnStep.Judge) AskJudge();
                            else if (step is EndOfTurnStep.Complete or EndOfTurnStep.Fallback && detector.EndSpeech())
                            {
                                decided = step;
                                transition = VoiceActivityTransition.SpeechEnded;
                            }
                        }
                        if (early is not null && transition == VoiceActivityTransition.None && detector.Speaking && accepted >= 0)
                        {
                            if (detector.SilenceFrames == 0)
                            {
                                // The user's own voice came back in the pause (never what the speakers played): the reply started
                                // early goes, and the next pause may start another.
                                if (silenceBefore > 0 && !speakers && early.VoiceResumed() && earlyReply is { } resumed)
                                    owner.LetGoEarly(resumed, EarlyReplyRecord.Cancelled, "you went on talking");
                            }
                            else
                            {
                                // Without a judge the quick transcript starts at the same short pause: a reply may start early on
                                // it, and speech-to-text reuses it when the turn ends in this pause.
                                if (turn is null && detector.SilenceFrames == quickFrames && quickFrames < plainFrames) QuickAtPause();
                                if (!quickTried && quick is { Transcript.IsCompleted: true } && quickPause == pauses) TryEarly();
                            }
                        }
                        if (transition == VoiceActivityTransition.SpeechStarted)
                        {
                            if (accepted < 0) operation.Publish(new("mic.hearing_speech"));
                        }
                        else if (transition == VoiceActivityTransition.SpeechEnded)
                        {
                            // A cough or click, or what the speakers played, is ignored; keep listening for real speech.
                            if (accepted < 0 && (detector.SpeechEndFrame - detector.SpeechStartFrame < minimumFrames || !Voice()))
                            {
                                decided = EndOfTurnStep.Wait;
                                operation.Publish(new("mic.listening"));
                                continue;
                            }
                            if (accepted < 0) Accept();
                            operation.Hearing = true;
                            if (!operation.Listening.Pc)
                            {
                                // The speech ended where the silence began; the detector noticed after the end-of-speech pause, or the
                                // end-of-turn judge decided sooner.
                                var now = owner.clock.GetTimestamp();
                                var silence = (long)((index - detector.SpeechEndFrame) * 0.02 * owner.clock.TimestampFrequency);
                                operation.LatencyTimeline = new ReplyTimeline(owner.clock, ReplyTimeline.YouStopped, now - Math.Max(0, silence));
                                if (turn is not null) TurnEnded(now, silence);
                                else operation.LatencyTimeline.Mark(ReplyLatency.EndOfSpeech, now);
                                if (early is not null) EarlyTurnEnded();
                            }
                            await run.ReleaseAsync().ConfigureAwait(false);
                            return Range(accepted, detector.SpeechEndFrame);
                        }
                        if (detector.Speaking && accepted < 0 && index - detector.SpeechStartFrame >= minimumFrames && Voice())
                        {
                            Accept();
                            operation.Hearing = true;
                        }
                    }
                    while (true);
                    // A quick transcript that came between frames, or a start that found the conversation busy a moment ago (the
                    // reply let go before it still leaving), starts its reply early now rather than at the next frame.
                    if (early is not null && !quickTried && quick is { Transcript.IsCompleted: true } && quickPause == pauses &&
                        detector is { Speaking: true, SilenceFrames: > 0 } && accepted >= 0)
                        TryEarly();
                    // Always listening that can't tell Martlet's own voice from yours stops listening the moment Martlet starts
                    // speaking, unless you were already talking. Hearing the output you hear (Martlet's voice included) ends what it
                    // was hearing right there instead, so a video that was talking goes on without Martlet's own words.
                    if (operation.Listen && (accepted < 0 || operation.Listening!.Pc) && owner.listeningSession.Held(operation.Listening!, operation.Echo))
                    {
                        if (accepted < 0)
                        {
                            operation.Publish(new("listen.held"));
                            return null;
                        }
                        if (operation.Listening!.Pc)
                        {
                            await run.ReleaseAsync().ConfigureAwait(false);
                            return Range(accepted, index) with { EndSampleExclusive = index * EnergyVoiceActivityDetector.FrameSamples };
                        }
                    }
                    if (accepted < 0 && !detector.Speaking && owner.clock.GetElapsedTime(started) >= ListeningOptions.IdleRestart)
                        return null;
                    // The judge's answer is acted on at the next frame, so wake for it as well.
                    await (judging is { IsCompleted: false } pending
                        ? Task.WhenAny(run.Completion, pending, Task.Delay(TimeSpan.FromMilliseconds(20), owner.clock))
                        : Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromMilliseconds(20), owner.clock))).ConfigureAwait(false);
                }
                // Duration limit or Finish: send everything from the onset to the end of the recording.
                if (accepted < 0 && detector.Speaking && Voice()) Accept();
                if (accepted >= 0) operation.Hearing = true;
                return accepted < 0 ? null : Range(accepted, 0) with { EndSampleExclusive = int.MaxValue };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame);
                operation.VoiceLevel = -100;
                judgeCancel.Cancel();
                if (!ReferenceEquals(operation.QuickWords, quick)) DropQuick();
                // Listening stopped (or the turn ended in another pause) before a reply started early was kept for this turn.
                if (earlyReply is { } left && !earlyKept) owner.LetGoEarly(left, EarlyReplyRecord.Cancelled, "your turn didn't end in that pause");
                // The user's voice ended (or listening stopped): a paused reply plays on once its verdict is not for Martlet.
                operation.Held?.Hold.Ended();
            }

            // The end-of-turn judge's answer, once it is in, for the pause it was asked about.
            void TakeJudgement()
            {
                if (judging is not { IsCompleted: true } done) return;
                judging = null;
                (judgement, judgeError, judgeEndedAt) = done.Result;
                if (!turn!.Judged(judgedPause, judgeError is null ? judgement : null))
                {
                    judgement = null;
                    judgeError = null;
                }
            }

            TimeSpan? Took() => judgeEndedAt > judgeStartedAt && judgeStartedAt != 0
                ? owner.clock.GetElapsedTime(judgeStartedAt, judgeEndedAt) : null;

            // What was said so far, from its pre-roll to now (with the pause), and where in it the speech that would be kept lies
            // (pre-roll to the pause, with the tail). The caller clears it.
            (byte[] Heard, int Offset, int Length, int Kept) HeardSoFar()
            {
                var end = index - detector.SilenceFrames;
                var startSample = Math.Max(0, accepted * EnergyVoiceActivityDetector.FrameSamples - EnergyVoiceActivityDetector.Samples(settings.PreRoll));
                var endSample = end * EnergyVoiceActivityDetector.FrameSamples + EnergyVoiceActivityDetector.Samples(settings.Tail);
                var firstFrame = startSample / EnergyVoiceActivityDetector.FrameSamples;
                var heard = new byte[(index - firstFrame) * EnergyVoiceActivityDetector.FrameBytes];
                var frames = 0;
                try
                {
                    for (var at = firstFrame; at < index; at++, frames++)
                        if (!run.TryCopyMonoFrame(at, heard.AsSpan(frames * EnergyVoiceActivityDetector.FrameBytes, EnergyVoiceActivityDetector.FrameBytes)))
                            break;
                }
                catch (OperationCanceledException) { }
                var offset = (startSample - firstFrame * EnergyVoiceActivityDetector.FrameSamples) * 2;
                return (heard, offset, frames * EnergyVoiceActivityDetector.FrameBytes - offset, (endSample - startSample) * 2);
            }

            // With Parakeet on this PC, a quick transcript of exactly the speech that would be kept starts now: speech-to-text reuses
            // it when the turn ends in this pause, and a reply may start early on it.
            void StartQuick(byte[] heard, int offset, int length, int keptBytes, long at)
            {
                var configured = operation.Authorization.Configuration;
                if (owner.localWords is not { } local || !configured.LocalStt() || configured.SttHostTarget() is not null || length < keptBytes || keptBytes <= 0 ||
                    keptBytes > Martlet.Sherpa.ParakeetEngine.MaximumSeconds * 16_000 * 2)
                    return;
                var kept = heard.AsSpan(offset, keptBytes).ToArray();
                var model = configured.Route(SetupRole.Stt).ModelId;
                quickCancel = CancellationTokenSource.CreateLinkedTokenSource(operation.OriginalCaller);
                Task<LocalTranscript> transcript;
                try { transcript = local.TranscribeAsync(model, kept, quickCancel.Token); }
                catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { transcript = Task.FromException<LocalTranscript>(error); }
                quick = new(model, kept, transcript, at);
                // Real words in it make the live floor Live before the turn ends (docs/CONVERSATION.md, Live floor).
                if (operation.Listening?.Pc != true) owner.FloorWords(operation, transcript);
                quickPause = pauses;
                quickEnd = index - detector.SilenceFrames;
                quickTried = false;
                quickWorth = null;
            }

            // Without a judge (off, or none can answer), the quick transcript still starts at the short pause the judge would be
            // asked at, for a reply started early.
            void QuickAtPause()
            {
                DropQuick();
                var (heard, offset, length, kept) = HeardSoFar();
                try { StartQuick(heard, offset, length, kept, owner.clock.GetTimestamp()); }
                finally { CryptographicOperations.ZeroMemory(heard); }
            }

            // The quick transcript of the pause under way is in: a reply starts early when it has real words and the conversation
            // is free (StartEarly decides the rest; a busy slot, say the reply let go a moment ago, is tried again at the next
            // frame).
            void TryEarly()
            {
                if (quick!.Transcript is not { IsCompletedSuccessfully: true } done)
                {
                    quickTried = true;
                    return;
                }
                var text = done.Result.Text?.Trim() ?? "";
                var (voiced, speech) = Measure(accepted, quickEnd);
                quickWorth ??= EarlyReplyGate.Worth(text, owner.WordsContext(operation, voiced, done.Result.Evidence, speech), operation.Listening!.WordCheck);
                if (!early!.TryStart(quickPause, pauses, detector.SilenceFrames > 0, quickWorth.Value))
                {
                    quickTried = true;
                    return;
                }
                var pauseStartedAt = owner.clock.GetTimestamp() - (long)(detector.SilenceFrames * 0.02 * owner.clock.TimestampFrequency);
                if (owner.StartEarly(operation, quick, text, quickPause, early.Starts, pauseStartedAt, voiced) is { } started)
                {
                    quickTried = true;
                    operation.EarlyStarted = earlyReply = started;
                }
                else early.NotStarted();
            }

            // The turn ended: a reply started early in this very pause waits for the talk window to take it (the same words and
            // what goes with them are checked then); one from an earlier pause goes. Without a judge, speech-to-text reuses the
            // quick transcript of this pause. The reply latency line says how many replies started early.
            void EarlyTurnEnded()
            {
                if (turn is null && quick is not null && quickPause == pauses)
                {
                    operation.QuickWords = quick;
                    quickCancel = null;
                }
                if (early!.Ended(pauses)) earlyKept = earlyReply is not null;
                else if (earlyReply is { } waiting) owner.LetGoEarly(waiting, EarlyReplyRecord.Cancelled, "you went on talking");
                if (early.Starts > 0 && operation.LatencyTimeline is { } line) line.Early = new(false, early.Starts, early.Cancelled);
            }

            // After a short pause: the judge hears what was said so far (from its pre-roll to now, with the pause), and with Parakeet
            // on this PC a quick transcript of exactly the speech that would be kept starts beside it, for speech-to-text to reuse.
            void AskJudge()
            {
                DropQuick();
                judgement = null;
                judgeError = null;
                judgeEndedAt = 0;
                judgedPause = turn!.Pause;
                var (heard, offset, length, keptBytes) = HeardSoFar();
                var silence = TimeSpan.FromMilliseconds(detector.SilenceFrames * 20);
                judgeStartedAt = owner.clock.GetTimestamp();
                StartQuick(heard, offset, length, keptBytes, judgeStartedAt);
                var request = new EndOfTurnRequest(heard.AsMemory(offset, Math.Max(0, length)), silence,
                    quick?.Transcript.ContinueWith(t => t.IsCompletedSuccessfully ? (string?)t.Result.Text : null, TaskScheduler.Default));
                var token = judgeCancel.Token;
                judging = Task.Run(async () =>
                {
                    try
                    {
                        var answer = await judge!.JudgeAsync(request, token).ConfigureAwait(false);
                        return ((EndOfTurnJudgement?)answer, (Exception?)null, owner.clock.GetTimestamp());
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { return (null, error, owner.clock.GetTimestamp()); }
                    finally { CryptographicOperations.ZeroMemory(heard); }
                }, CancellationToken.None);
            }

            void DropQuick()
            {
                if (quick is null) return;
                quickCancel?.Cancel();
                quickCancel?.Dispose();
                CryptographicOperations.ZeroMemory(quick.Pcm);
                quick = null;
                quickCancel = null;
            }

            // The turn ended while the judge was on: the decision goes to the log and the status, and the reply latency line gets the
            // pause before the judge was asked and its answer. The quick transcript of this pause is kept for speech-to-text.
            void TurnEnded(long now, long silenceTicks)
            {
                TakeJudgement();
                var timeline = operation.LatencyTimeline!;
                var silence = TimeSpan.FromSeconds(silenceTicks / (double)owner.clock.TimestampFrequency);
                var asked = turn!.Asked && judgeStartedAt != 0 && judgedPause == turn.Pause;
                if (asked) timeline.Mark(ReplyLatency.EndOfTurnWait, judgeStartedAt);
                string outcome;
                if (decided == EndOfTurnStep.Complete)
                {
                    outcome = EndOfTurnDecision.Complete;
                    timeline.Mark(ReplyLatency.EndOfTurnJudge, now);
                }
                else
                {
                    if (asked && judgeEndedAt != 0 && judgeEndedAt <= now) timeline.Mark(ReplyLatency.EndOfTurnJudge, judgeEndedAt);
                    timeline.Mark(ReplyLatency.EndOfSpeech, now);
                    outcome = decided != EndOfTurnStep.Fallback ? EndOfTurnDecision.Incomplete : turn.Fallback switch
                    {
                        EndOfTurnFallback.Failed => EndOfTurnDecision.Failed,
                        EndOfTurnFallback.Slow => EndOfTurnDecision.Slow,
                        _ => EndOfTurnDecision.NotJudged
                    };
                }
                owner.listeningSession.RecordTurn(new(owner.clock.GetUtcNow(), outcome, silence, settings.EndSilence, asked ? Took() : null,
                    asked ? judgement?.Probability : null, judge!.Name,
                    outcome == EndOfTurnDecision.Failed ? judgeError?.GetBaseException().Message : outcome == EndOfTurnDecision.NotJudged ? "your voice wasn't accepted yet" : null));
                if (asked && quick is not null)
                {
                    // Its own cancellation stays with it: the transcript may still be on its way.
                    operation.QuickWords = quick;
                    quickCancel = null;
                }
            }

            // The utterance starts here: when its voice began, on the controller's clock (each frame is 20 ms of it). A reply started
            // early for the turn before this one is let go: the talk window answers both together.
            void Accept()
            {
                accepted = Onset();
                operation.SpeechStartedAt = owner.clock.GetTimestamp() - (long)((index - accepted) * 0.02 * owner.clock.TimestampFrequency);
                // Your voice starting a new utterance lets go of a reply started early for the one before; what this PC plays never
                // does (lines of it that go with your words are caught when the reply is taken).
                if (operation.Listening is { Pc: false } && owner.EarlyReply is { Early: { } other } waiting && !ReferenceEquals(other.Utterance, operation))
                    owner.LetGoEarly(waiting, EarlyReplyRecord.Cancelled, "you went on talking");
            }

            // The speech under way is someone's voice, not mostly what the speakers played (or the user has talked over them).
            bool Voice() => talkOver?.Sustained == true || !SpeakersMostly(detector.SpeechStartFrame);
            bool SpeakersMostly(int from) => from >= 0 && from < userSum.Count &&
                speakerSum[^1] - speakerSum[from] > userSum[^1] - userSum[from];
            // Where what is sent starts: the speech's onset, or where the user's own voice began over what the speakers played.
            int Onset() => SpeakersMostly(detector.SpeechStartFrame) && talkOver is { StretchStartFrame: >= 0 } over
                ? Math.Max(detector.SpeechStartFrame, over.StretchStartFrame) : detector.SpeechStartFrame;

            // How much of the speech between two frames was the user's voice (loud frames the speakers don't explain) and how long
            // that voice went on (every frame from its onset to the silence that the speakers don't explain).
            (TimeSpan Voiced, TimeSpan Speech) Measure(int startFrame, int endFrame)
            {
                var last = Math.Clamp(endFrame <= startFrame ? userSum.Count - 1 : endFrame, 0, userSum.Count - 1);
                var first = Math.Clamp(startFrame, 0, last);
                return (TimeSpan.FromMilliseconds((userSum[last] - userSum[first]) * 20),
                    TimeSpan.FromMilliseconds((last - first - (explainedSum[last] - explainedSum[first])) * 20));
            }

            // What is sent, with how much of it was the user's voice and how long that voice went on (Measure).
            SpeechRange Range(int startFrame, int endFrame)
            {
                (operation.Voiced, operation.Speech) = Measure(startFrame, endFrame);
                return new(
                    Math.Max(0, startFrame * EnergyVoiceActivityDetector.FrameSamples - EnergyVoiceActivityDetector.Samples(settings.PreRoll)),
                    endFrame * EnergyVoiceActivityDetector.FrameSamples + EnergyVoiceActivityDetector.Samples(settings.Tail));
            }
        }

        /// <summary>The quick words check for talking over Martlet: Parakeet on this PC (free, private and fast), or null when
        /// Listening uses a host or the cloud, or only the user's voice may be answered (Voice ID needs the whole utterance), where
        /// the utterance's own transcript decides instead.</summary>
        internal Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalTranscript>>? WordsCheck(LiveConversationOperation operation)
        {
            var configured = operation.Authorization.Configuration;
            if (owner.localWords is not { } local || !configured.LocalStt() || operation.Voiceprint is not null) return null;
            var model = configured.Route(SetupRole.Stt).ModelId;
            return (pcm, token) => local.TranscribeAsync(model, pcm, token);
        }
    }
}
