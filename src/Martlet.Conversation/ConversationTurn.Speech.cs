using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Martlet.Audio;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

// Speech pipeline: what is said aloud. Sentences are staged, synthesized and played in order, with captions for words not said, quick
// sounds ahead of the reply, pause and resume, and a voice that stops without ending the reply.
public sealed partial class ConversationTurn
{
    /// <summary>Plays <paramref name="pcm"/> now, ahead of this reply's own voice: a quick sound in Martlet's own voice (24 kHz
    /// mono 16-bit PCM, at most <see cref="QuickSoundAudio.MaximumBytes"/>) while the reply's first audio isn't ready
    /// (<see cref="QuickSoundWatcher"/>). The reply's first piece waits until it has played and then follows it, so neither is
    /// cut. It is never part of the reply's text, captions or history. Returns false, and plays nothing, for a reply that isn't
    /// spoken, is held, paused, finished or stopped, already played a quick sound, or whose own audio started.</summary>
    public bool PlayQuickSound(ReadOnlyMemory<byte> pcm)
    {
        if (pcm.Length is 0 || pcm.Length > QuickSoundAudio.MaximumBytes || pcm.Length % 2 != 0) return false;
        lock (Sync)
        {
            if (request.Speech is not { } voice || quickSound is not null || quickClosed || heldBack is not null || paused ||
                invalidated || workFinished || terminal || speaking.IsCancellationRequested || ownAudioStarted || playback is not null)
                return false;
            quickSoundAfter = Clock.GetElapsedTime(startedAt);
            var token = speaking.Token;
            quickSound = Task.Run(() => PlayQuickSoundAsync(pcm, voice, token));
            Emit(ConversationEventKind.Playback);
            return true;
        }
    }

    /// <summary>Pauses what Martlet says aloud, at once, without losing anything: the sentence playing stops reading audio
    /// and keeps what is buffered, and a sentence that starts meanwhile starts paused. The Thinking text and the voice's
    /// synthesis go on and buffer, so <see cref="Resume"/> plays on from the same sample with no new synthesis. Returns
    /// false for a reply that isn't spoken, has finished or is paused already.</summary>
    public bool Pause()
    {
        lock (Sync)
        {
            if (request.Speech is null || paused || invalidated || workFinished || terminal || speaking.IsCancellationRequested) return false;
            paused = true;
            pausedAt = Clock.GetTimestamp();
            pauses++;
            playback?.Pause();
            quickRun?.Pause();
            cueClock.Pause();
            return true;
        }
    }

    /// <summary>Plays on from where <see cref="Pause"/> stopped. Returns false when it wasn't paused.</summary>
    public bool Resume()
    {
        lock (Sync)
        {
            if (!paused) return false;
            paused = false;
            pausedTime += Clock.GetElapsedTime(pausedAt);
            resumes++;
            playback?.Resume();
            quickRun?.Resume();
            cueClock.Resume();
            return true;
        }
    }

    // A voice that fails or runs out of time ends what is said aloud, never the reply: the rest of the text still streams to
    // completion and is shown, and the turn records why it went quiet. No other voice is asked instead.
    private void StopSpeaking(ConversationFailure reason, ProviderFailureCode? provider = null)
    {
        lock (Sync)
        {
            if (originalCaller.IsCancellationRequested)
            {
                CancelByUser();
                return;
            }
            if (request.Speech is null || invalidated || workFinished || voiceMuted || speechFailure != ConversationFailure.None) return;
            // The whole turn ran out of time before the text finished, which ends the reply too.
            if (whole.Expired && !textComplete)
            {
                Fail(ConversationFailure.DeadlineExceeded);
                return;
            }
            speechFailure = reason;
            if (provider is not null)
            {
                providerFailure = provider;
                failedProvider = ProviderRole.Tts;
            }
            EndSpeaking();
        }
    }

    /// <summary>Mutes the voice for the rest of this reply (the user muted Martlet's voice while it spoke): what is playing
    /// stops now and nothing more is synthesized, while the text still streams to completion and each sentence not said aloud
    /// shows in the captions, as after a voice failure. It is not a failure (<see cref="ConversationSnapshot.VoiceMuted"/>).
    /// A no-op for a reply that isn't spoken, has finished or whose voice already stopped.</summary>
    public void MuteVoice()
    {
        lock (Sync)
        {
            if (request.Speech is null || invalidated || workFinished || voiceMuted || speechFailure != ConversationFailure.None) return;
            voiceMuted = true;
            EndSpeaking();
        }
    }

    private void EndSpeaking()
    {
        speechObservation?.Stop();
        // Capture-scoped handle: never use sink-wide Stop.
        _ = playback?.StopAsync();
        quickObservation?.Stop();
        _ = quickRun?.StopAsync();
        speechCallbacks = speaking.CancelAsync();
        // Muted before the text started: the turn is still authorizing its Thinking request.
        if (!textComplete && textProvenance is not null && state != ConversationState.Generating) SetState(ConversationState.Generating);
        else Emit(ConversationEventKind.State);
    }

    private void CheckSpeaking(MonotonicWindow window)
    {
        Check(window);
        speaking.Token.ThrowIfCancellationRequested();
    }

    private async Task StageAsync(IEnumerable<SpeechPiece> pieces, MonotonicWindow window)
    {
        using var iterator = pieces.GetEnumerator();
        while (await segments.Writer.WaitToWriteAsync(stop.Token).ConfigureAwait(false))
        {
            Check(window);
            lock (Sync)
            {
                CheckActive();
                // Reserve capacity before advancing segmentation; serialize its buffer with Stop/Clear.
                if (!iterator.MoveNext()) return;
                if (iterator.Current is { Text: null, Cues: null or { Count: 0 } })
                {
                    suppressed++;
                    Emit(ConversationEventKind.SpeechSuppressed);
                }
                else
                {
                    // A piece with no words but character cues still goes in order, so the character acts after what came before.
                    if (!segments.Writer.TryWrite(iterator.Current))
                        throw new ConversationException(ConversationFailure.InvalidStream);
                    firstSegmentAfter ??= Clock.GetElapsedTime(startedAt);
                    peakQueued = Math.Max(peakQueued, segments.Reader.Count);
                    if (iterator.Current.Text is not null) Emit(ConversationEventKind.SegmentQueued);
                }
            }
        }
        Check(window);
    }

    // One sentence ready to play: its audio arrives in Frames while it is synthesized, ahead of its turn to play.
    private sealed class SpeechTake(string text, int number, CorrelationIds ids)
    {
        internal string Text { get; } = text;
        internal int Number { get; } = number;
        internal CorrelationIds Ids { get; } = ids;
        internal Channel<PcmFrame> Frames { get; } = Channel.CreateUnbounded<PcmFrame>(new UnboundedChannelOptions
        {
            SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false
        });
        internal long? FinalSamples { get; set; }
        internal bool ProviderFailed { get; set; }
        internal ProviderFailureCode? Failure { get; set; }
        internal ExceptionDispatchInfo? Error { get; set; }
        // A sentence written after the voice failed: it is only shown in the captions, in its place after the ones before it.
        internal bool CaptionOnly { get; init; }
        // Character cues with no words of their own (a tag after the reply's last sentence): acted in their place.
        internal bool CuesOnly { get; init; }
        internal IReadOnlyList<SpeechCue>? Cues { get; init; }
        // Its words were posted to the captions (when its playback started).
        internal bool Captioned { get; set; }
    }

    // Synthesis runs one sentence ahead of playback: while sentence N plays, sentence N+1 is already being synthesized, so
    // there is no synthesis gap between sentences. A synthesis failure is raised only when playback reaches that sentence,
    // so what is already playing finishes first, as it did before. A voice failure then stops speaking (StopSpeaking); both
    // stages keep draining until the text ends, so the reply's text is never held up or cut short by its voice.
    private async Task SpeakAsync()
    {
        if (request.Speech is not { } voice) return;
        var ready = Channel.CreateBounded<SpeechTake>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false
        });
        await Task.WhenAll(GuardStageAsync(() => SynthesizeAllAsync(voice, ready.Writer)),
            GuardStageAsync(() => PlayAllAsync(voice, ready.Reader))).ConfigureAwait(false);
    }

    private async Task SynthesizeAllAsync(SpeechOutput voice, ChannelWriter<SpeechTake> ready)
    {
        var broken = false;
        try
        {
            // Started early without its voice: nothing is synthesized until the turn is released.
            if (holdVoice) await WhileHeldAsync().ConfigureAwait(false);
            await foreach (var piece in segments.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (piece.Text is null)
                {
                    if (await ready.WaitToWriteAsync(stop.Token).ConfigureAwait(false))
                        ready.TryWrite(new SpeechTake("", 0, NewIds()) { CuesOnly = true, Cues = piece.Cues });
                    continue;
                }
                // Once the speech budget is spent, a sentence failed or the voice stopped, the rest of the reply is text only;
                // keep draining so the text still finishes. After a voice failure each sentence still reaches the captions.
                if (broken || speaking.IsCancellationRequested)
                {
                    if (await ready.WaitToWriteAsync(stop.Token).ConfigureAwait(false))
                        ready.TryWrite(new SpeechTake(piece.Text, 0, NewIds()) { CaptionOnly = true, Cues = piece.Cues });
                    continue;
                }
                if (speechLimitReached) continue;
                if (!await ready.WaitToWriteAsync(stop.Token).ConfigureAwait(false)) return;
                // The voice stopped (failed or was muted) while this sentence waited for its turn: it goes to the captions too.
                if (speaking.IsCancellationRequested)
                {
                    ready.TryWrite(new SpeechTake(piece.Text, 0, NewIds()) { CaptionOnly = true, Cues = piece.Cues });
                    continue;
                }
                SpeechTake? take;
                try { take = Reserve(piece.Text, voice, piece.Cues); }
                catch (ConversationException error)
                {
                    StopSpeaking(error.Failure);
                    continue;
                }
                catch (ContractException)
                {
                    StopSpeaking(ConversationFailure.InvalidStream);
                    continue;
                }
                if (take is null) continue;
                if (!ready.TryWrite(take)) throw new ConversationException(ConversationFailure.InvalidStream);
                var window = new MonotonicWindow(Clock, voice.Limits.MaxRequestTime);
                lock (Sync)
                {
                    speechWindow = window;
                    synthesizing = true;
                }
                try { broken = !await SynthesizeAsync(take, voice, window).ConfigureAwait(false); }
                catch (Exception) { broken = true; }
                finally
                {
                    lock (Sync)
                    {
                        speechWindow = null;
                        synthesizing = false;
                    }
                }
            }
        }
        finally
        {
            ready.TryComplete();
            // Every sentence is made (or the voice stopped): only playback is left.
            synthesized.TrySetResult();
        }
    }

    private SpeechTake? Reserve(string segment, SpeechOutput voice, IReadOnlyList<SpeechCue>? cues = null)
    {
        var input = new BoundedSpeechInput(segment);
        lock (Sync)
        {
            CheckActive();
            if (committed >= request.Limits.MaxSpeechSegments ||
                input.Utf8Bytes > request.Limits.MaxSpeechTextBytes - reservedBytes ||
                voice.Limits.MaxSamples > request.Limits.MaxReservedSpeechSamples - reservedSamples)
            {
                // The aggregate speech budget ends what is said aloud, never the reply itself.
                speechLimitReached = true;
                suppressed++;
                Emit(ConversationEventKind.SpeechSuppressed);
                return null;
            }
            var take = new SpeechTake(segment, ++committed, NewIds()) { Cues = cues };
            reservedBytes += input.Utf8Bytes;
            reservedSamples += voice.Limits.MaxSamples;
            speechRequest ??= take.Ids.RequestId;
            if (playback is null) SetState(ConversationState.Authorizing);
            Emit(ConversationEventKind.SegmentStarted);
            return take;
        }
    }

    // Authorizes and synthesizes one sentence into its frame buffer. Returns false when the provider failed it.
    private async Task<bool> SynthesizeAsync(SpeechTake take, SpeechOutput voice, MonotonicWindow window)
    {
        try
        {
            CheckSpeaking(window);
            var input = new BoundedSpeechInput(take.Text);
            var context = new ProviderRequestContext { Ids = take.Ids, Epoch = Epoch, Deadline = Deadline(window) };
            var budget = new OperationBudget(take.Ids, Epoch, ProviderRole.Tts, 1, input.Utf8Bytes, 0, 0, voice.Limits.MaxSamples);
            var action = new SpeechAuthorizationAction(context, take.Number, input, voice.Selection, voice.Limits, budget);
            var permission = await authorization.AuthorizeSpeechAsync(action, speaking.Token).ConfigureAwait(false);
            CheckSpeaking(window);
            if (permission?.Authorization is not { } consent)
                throw new ConversationException(ConversationFailure.AuthorizationUnavailable);
            window = ValidateReservation(permission.Reservation, budget, consent.ExpiresAt, context.Deadline, window);
            lock (Sync) speechWindow = window;
            CheckSpeaking(window);
            context = context with { Deadline = Deadline(window) };
            var stream = Owner.StreamSpeech(context, request, input, consent, originalCaller);
            lock (Sync)
            {
                CheckActive();
                speaking.Token.ThrowIfCancellationRequested();
                speechRequestAfter ??= Clock.GetElapsedTime(startedAt);
                speechProvenance = stream.Capabilities.Provenance;
                if (playback is null) SetState(ConversationState.Synthesizing);
            }
            var heardFirst = false;
            var sampleRate = 0;
            await using (var enumeration = stream.GetAsyncEnumerator(speaking.Token))
            {
                while (true)
                {
                    CheckSpeaking(window);
                    bool moved = await enumeration.MoveNextAsync().ConfigureAwait(false);
                    CheckSpeaking(window);
                    if (!moved) break;
                    if (!take.Frames.Writer.TryWrite(enumeration.Current))
                        throw new ConversationException(ConversationFailure.InvalidStream);
                    if (!heardFirst)
                    {
                        heardFirst = true;
                        sampleRate = enumeration.Current.Format.SampleRate;
                        lock (Sync) firstSpeechAudioAfter ??= Clock.GetElapsedTime(startedAt);
                    }
                }
            }
            CheckSpeaking(window);
            if (stream.Result is not { Outcome: SpeechSynthesisOutcome.Completed, FinalSampleCount: { } samples })
            {
                take.ProviderFailed = true;
                take.Failure = stream.Result?.Failure?.Code;
                return false;
            }
            take.FinalSamples = samples;
            if (take.Number == 1 && sampleRate > 0)
                lock (Sync)
                {
                    firstPieceSynthesizedAfter ??= Clock.GetElapsedTime(startedAt);
                    firstPieceSpeech ??= TimeSpan.FromSeconds((double)samples / sampleRate);
                }
            return true;
        }
        catch (Exception error)
        {
            take.Error = ExceptionDispatchInfo.Capture(error);
            throw;
        }
        finally { take.Frames.Writer.TryComplete(); }
    }

    private async Task PlayAllAsync(SpeechOutput voice, ChannelReader<SpeechTake> ready)
    {
        // Started early: nothing plays, acts or shows until the turn is released, and the first piece waits here synthesized
        // (synthesis runs one piece ahead, so only that one is made meanwhile).
        await WhileHeldAsync().ConfigureAwait(false);
        await foreach (var take in ready.ReadAllAsync(stop.Token).ConfigureAwait(false))
        {
            if (take.CuesOnly)
            {
                PostCues(take, null, Task.CompletedTask);
                continue;
            }
            // Once the voice stopped, a sentence already synthesized is dropped unplayed (its words still show in the captions);
            // keep draining so synthesis never waits.
            if (take.CaptionOnly || speaking.IsCancellationRequested)
            {
                ShowUnsaid(take.Text);
                PostCues(take, null, Task.CompletedTask);
                continue;
            }
            var window = new MonotonicWindow(Clock, voice.Limits.MaxRequestTime);
            lock (Sync) playWindow = window;
            try { await PlayAsync(take, voice, window).ConfigureAwait(false); }
            catch (OperationCanceledException) when (speaking.IsCancellationRequested) { }
            catch (ConversationException error) { StopSpeaking(error.Failure); }
            catch (ContractException) { StopSpeaking(ConversationFailure.InvalidStream); }
            // A voice provider or the playback device can fail arbitrarily; only what is said aloud ends.
            catch (Exception) { StopSpeaking(ConversationFailure.DependencyFailed); }
            finally { lock (Sync) playWindow = null; }
            // The voice failed before this sentence started playing: its words still show.
            if (!take.Captioned && speaking.IsCancellationRequested) ShowUnsaid(take.Text);
        }
    }

    // The character acts on a sentence's cues as it starts playing, each one about where it was written: its share of the
    // sentence's words, of the sentence's audio length when that is known (otherwise of a natural reading pace). The cues
    // follow the reply's cue clock, so a pause holds them and a stop drops the ones not yet acted.
    private void PostCues(SpeechTake take, int? sampleRate, Task finished)
    {
        if (Owner.CharacterCues is not { } feed || take.Cues is not { Count: > 0 } cues) return;
        lock (Sync) if (userStopped) return;
        var length = Math.Max(1, take.Text.Length);
        var seconds = sampleRate is > 0 && take.FinalSamples is { } samples ? samples / (double)sampleRate.Value : length / 14.0;
        feed.Post(cues.Select(cue => new CharacterCue(cue.Tag,
            TimeSpan.FromSeconds(Math.Clamp(seconds * cue.Offset / length, 0, 30)))).ToArray(), finished, cueClock);
    }

    // Captions (speech bubble, subtitles) for words the voice couldn't say, one sentence after another for about as long as
    // reading each takes. A newer reply or Stop ends them; the turn never waits for them.
    private void ShowUnsaid(string text)
    {
        if (Owner.SpokenText is not { } feed || VoiceTags.Strip(text).Trim() is not { Length: > 0 } caption) return;
        lock (Sync)
        {
            if (userStopped) return;
            unsaidCaptions = CaptionAfterAsync(unsaidCaptions, feed, caption);
        }
    }

    private async Task CaptionAfterAsync(Task previous, SpokenTextFeed feed, string caption)
    {
        await previous.ConfigureAwait(false);
        lock (Sync)
            if (userStopped || Owner.CurrentEpoch != Epoch) return;
        var shown = Task.Delay(ReadingTime(caption), Clock);
        feed.Post(caption, shown);
        await shown.ConfigureAwait(false);
    }

    // A reply that isn't spoken: each sentence goes to the captions once it is written (or, started early, once it is released),
    // split and kept quiet ([pass]) the way the voice would say it. Captions never fail the reply: one that runs past its
    // limits just stops showing.
    private void Caption(Func<SpeechSegmenter, IEnumerable<SpeechPiece>> next)
    {
        if (captioner is null || captionsEnded) return;
        try
        {
            foreach (var piece in next(captioner))
                if (piece.Text is { } sentence) WhenReleased(() => ShowUnsaid(sentence));
        }
        catch (ConversationException) { captionsEnded = true; }
    }

    internal static TimeSpan ReadingTime(string caption) =>
        TimeSpan.FromSeconds(Math.Clamp(1.5 + caption.Length / 15.0, 2, 20));

    private async Task PlayAsync(SpeechTake take, SpeechOutput voice, MonotonicWindow window)
    {
        PlaybackRun? run = null;
        try
        {
            await foreach (var frame in take.Frames.Reader.ReadAllAsync(speaking.Token).ConfigureAwait(false))
            {
                CheckSpeaking(window);
                if (run is null)
                {
                    // A quick sound playing ahead of the reply ends first: the reply follows it and never cuts it.
                    await AfterQuickSoundAsync().ConfigureAwait(false);
                    lock (Sync)
                    {
                        CheckActive();
                        speaking.Token.ThrowIfCancellationRequested();
                        run = Owner.StartPlayback(this, take.Ids, voice.Output, Deadline(window), speaking.Token);
                        if (paused) run.Pause();
                        playback = run;
                        sentence = VoiceTags.Strip(take.Text).Trim();
                        if (sentence.Length > 0) saidAloud.Append(saidAloud.Length > 0 ? " " : "").Append(sentence);
                        playbackStartedAfter ??= Clock.GetElapsedTime(startedAt);
                        speechRequest = take.Ids.RequestId;
                        speechObservation = Owner.GeneratedSpeech?.Begin(run, frame.Format);
                        // Captions show the words only; the voice still hears its own tags ([laugh]...).
                        if (VoiceTags.Strip(take.Text).Trim() is { Length: > 0 } caption) Owner.SpokenText?.Post(caption, run.Completion);
                        PostCues(take, frame.Format.SampleRate, run.Completion);
                        take.Captioned = true;
                    }
                }
                await SubmitAsync(run, frame, window).ConfigureAwait(false);
            }
            CheckSpeaking(window);
            take.Error?.Throw();
            if (take.ProviderFailed)
            {
                StopSpeaking(ConversationFailure.ProviderFailed, take.Failure);
                return;
            }
            if (run is null || take.FinalSamples is not { } samples || !run.CompleteInput(samples))
                throw new ConversationException(ConversationFailure.PlaybackFailed);
            speechObservation?.CompleteInput(samples);
            var terminalPlayback = await run.Completion.WaitAsync(speaking.Token).ConfigureAwait(false);
            CheckSpeaking(window);
            if (terminalPlayback.State != PlaybackState.Completed)
                throw new ConversationException(ConversationFailure.PlaybackFailed);
        }
        finally
        {
            if (run is not null)
            {
                speechObservation?.Stop();
                // Never concurrently dispose a provider enumerator or release a native device.
                var finished = await run.StopAsync().ConfigureAwait(false);
                lock (Sync)
                {
                    lastPlayback = finished;
                    // A sentence that played entirely between two of the supervisor's looks was still heard, by now at the latest.
                    if (finished.MayHavePlayed) firstAudioAfter ??= Clock.GetElapsedTime(startedAt);
                    Emit(ConversationEventKind.Playback);
                }
                await run.DeviceRelease.ConfigureAwait(false);
                lock (Sync)
                {
                    var final = run.Snapshot;
                    accepted += final.AcceptedSamples;
                    submitted += final.SubmittedSamples;
                    consumed += final.DeviceConsumedSamples;
                    voiceWaits += final.Underruns;
                    voiceWaited += final.UnderrunTime;
                    mayHavePlayed |= final.MayHavePlayed;
                    quarantined |= !final.DeviceReleased || final.Error?.Code == ErrorCode.AudioPlaybackFailed;
                    lastPlayback = final;
                    playback = null;
                    sentence = null;
                    speechObservation = null;
                    speechRequest = null;
                    if (!invalidated && !textComplete)
                        SetState(synthesizing && !speaking.IsCancellationRequested ? ConversationState.Synthesizing : ConversationState.Generating);
                }
            }
        }
    }

    private async Task SubmitAsync(PlaybackRun run, PcmFrame frame, MonotonicWindow window)
    {
        while (true)
        {
            CheckSpeaking(window);
            var snapshot = run.Snapshot;
            if (run.Completion.IsCompleted)
                throw new ConversationException(ConversationFailure.PlaybackFailed);
            var capacity = (long)(frame.Format.SampleRate * Owner.PlaybackOptions.Capacity.TotalSeconds);
            if (snapshot.AcceptedSamples - snapshot.DeviceConsumedSamples + frame.SamplesPerChannel <= capacity &&
                snapshot.QueuedFrames < Owner.PlaybackOptions.MaximumQueuedFrames)
            {
                lock (Sync)
                {
                    CheckActive();
                    // PCM sequence/sample offsets are independent of TTS control event sequence 0/1.
                    var mapped = PlaybackFrameMapping.Map(frame, snapshot.Ids, Epoch, snapshot.Epoch);
                    if (run.Submit(mapped) != FrameAcceptance.Accepted)
                        throw new ConversationException(ConversationFailure.InvalidStream);
                    speechObservation?.Submit(mapped);
                    Emit(ConversationEventKind.Playback);
                }
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), Clock, speaking.Token).ConfigureAwait(false);
        }
    }

    // Waits for a quick sound still playing (PlayQuickSound); once the reply's own audio starts, none may begin.
    private async Task AfterQuickSoundAsync()
    {
        while (true)
        {
            Task? playing;
            lock (Sync)
            {
                playing = quickSound is { IsCompleted: false } running ? running : null;
                if (playing is null)
                {
                    ownAudioStarted = true;
                    return;
                }
            }
            await playing.WaitAsync(speaking.Token).ConfigureAwait(false);
        }
    }

    // Plays a quick sound on its own playback run, 20 ms a frame, and lets go of the speakers when it ends. It never fails the
    // reply: a quick sound that can't play (the reply stopped, the speakers failed) just ends, and the reply's own voice goes on.
    private async Task PlayQuickSoundAsync(ReadOnlyMemory<byte> pcm, SpeechOutput voice, CancellationToken token)
    {
        PlaybackRun? run = null;
        var ids = NewIds();
        var format = OpenAiSpeechSynthesisCatalog.PcmFormat;
        var window = new MonotonicWindow(Clock, voice.Limits.MaxRequestTime);
        try
        {
            lock (Sync)
            {
                CheckActive();
                token.ThrowIfCancellationRequested();
                run = Owner.StartPlayback(this, ids, voice.Output, Deadline(window), token);
                if (paused) run.Pause();
                quickRun = run;
                quickObservation = Owner.GeneratedSpeech?.Begin(run, format);
            }
            var frameBytes = format.SampleRate / 50 * format.BlockAlignment;
            long samples = 0, sequence = 0;
            for (var offset = 0; offset < pcm.Length; offset += frameBytes)
            {
                var frame = new PcmFrame(ids, Epoch, sequence++, samples, format, pcm.Span.Slice(offset, Math.Min(frameBytes, pcm.Length - offset)));
                await SubmitQuickAsync(run, frame, window, token).ConfigureAwait(false);
                samples += frame.SamplesPerChannel;
            }
            if (!run.CompleteInput(samples)) return;
            lock (Sync) quickObservation?.CompleteInput(samples);
            await run.Completion.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception) { }
        finally
        {
            if (run is not null)
            {
                GeneratedSpeechObservation? observation;
                lock (Sync) observation = quickObservation;
                observation?.Stop();
                // Never release a native device concurrently with its own run.
                await run.StopAsync().ConfigureAwait(false);
                await run.DeviceRelease.ConfigureAwait(false);
                lock (Sync)
                {
                    var final = run.Snapshot;
                    quarantined |= !final.DeviceReleased || final.Error?.Code == ErrorCode.AudioPlaybackFailed;
                    quickRun = null;
                    quickObservation = null;
                    Emit(ConversationEventKind.Playback);
                }
            }
        }
    }

    private async Task SubmitQuickAsync(PlaybackRun run, PcmFrame frame, MonotonicWindow window, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Check(window);
            var snapshot = run.Snapshot;
            if (run.Completion.IsCompleted) throw new ConversationException(ConversationFailure.PlaybackFailed);
            var capacity = (long)(frame.Format.SampleRate * Owner.PlaybackOptions.Capacity.TotalSeconds);
            if (snapshot.AcceptedSamples - snapshot.DeviceConsumedSamples + frame.SamplesPerChannel <= capacity &&
                snapshot.QueuedFrames < Owner.PlaybackOptions.MaximumQueuedFrames)
            {
                lock (Sync)
                {
                    CheckActive();
                    var mapped = PlaybackFrameMapping.Map(frame, snapshot.Ids, Epoch, snapshot.Epoch);
                    if (run.Submit(mapped) != FrameAcceptance.Accepted) throw new ConversationException(ConversationFailure.InvalidStream);
                    quickObservation?.Submit(mapped);
                }
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), Clock, token).ConfigureAwait(false);
        }
    }
}
