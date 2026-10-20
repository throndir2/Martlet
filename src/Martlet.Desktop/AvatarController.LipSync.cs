using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Windows;
using Martlet.Audio;
using Martlet.Avatar.Audio2Face;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>Lip-sync for each sentence: the voice's loudness always, and Audio2Face frames (from this PC, a paired host or the lip-sync pool) through one pipeline for Automatic and for an activated, reviewed mapping.</summary>
internal sealed partial class AvatarController
{
    /// <summary>A character's Audio2Face mapping, ready to play: the settings, the model controls it drives, the renderer's
    /// configuration and the Audio2Face service. Automatic lip-sync makes it from a reviewed saved mapping or the built-in mouth
    /// mapping; an activated mapping (Advanced lip-sync setup) from the reviewed mapping itself.</summary>
    private sealed record Audio2FaceMapping(AvatarConfiguration Settings, IReadOnlyList<ModelParameter> Targets,
        RendererConfiguration Config, Audio2FaceOptions Options);

    /// <summary>A reviewed saved mapping for this exact model, otherwise the built-in mouth mapping.</summary>
    private Audio2FaceMapping? AutoConfiguration(AvatarProfile inspected)
    {
        var capabilities = renderer!.Capabilities!;
        var saved = inspected.Settings with { Enabled = true };
        var candidates = new List<AvatarConfiguration>();
        if (saved.Assignments.Count > 0 && saved.Assignments.All(a => a.SourceId == SourceId) &&
            saved.MappingProfiles.All(p => p.ModelId == capabilities.ModelId) &&
            saved.RequestedAspects.Except(saved.OmittedAspects).All(a => a is AvatarAspect.Mouth or AvatarAspect.Expression))
            candidates.Add(saved);
        if (Audio2FaceAutoMapping.Create(capabilities, inspected.Renderer, SourceId) is { } fallback) candidates.Add(fallback);
        foreach (var settings in candidates)
        {
            try
            {
                var targets = Targets(settings);
                if (targets.Count == 0) continue;
                var mappingRevision = Convert.ToHexString(SHA256.HashData(AvatarJson.WriteConfiguration(settings)));
                var config = new RendererConfiguration(SourceId, inspected.ResourceRevision!, mappingRevision,
                    targets.Select(t => new RendererMapping(t.Id, t.Aspect.ToString())).ToArray());
                return new(settings, targets, config, new Audio2FaceOptions { Endpoint = new(inspected.Endpoint) });
            }
            catch (Exception error) when (error is InvalidOperationException or ContractException) { }
        }
        return null;
    }

    private void StartCharacter(IAvatarRenderer target, Audio2FaceMapping? automatic)
    {
        lock (stateGate)
        {
            var lifetime = new CancellationTokenSource();
            loudness = lifetime;
            observer.Enable();
            Volatile.Write(ref automaticNow, automatic);
            loudnessWorker = RunCharacterAsync(target, automatic, lifetime.Token);
        }
    }

    private async Task StopLoudnessAsync()
    {
        Task? running;
        CancellationTokenSource? lifetime;
        lock (stateGate)
        {
            if (loudness is null) return;
            lifetime = loudness;
            running = loudnessWorker;
            loudness = null;
            loudnessWorker = null;
            Volatile.Write(ref automaticNow, null);
            lifetime.Cancel();
            if (activation is null) observer.Disable();
        }
        if (running is not null)
            try { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { ErrorLog.Observe(running, "Character lip-sync after it was stopped"); }
            catch (Exception error) when (!ErrorLog.IsFatal(error))
            {
                // Stopping the character always finishes: the run reports its own sentences, so this is never a renderer's failure.
                if (error is not OperationCanceledException) ErrorLog.Error("Character lip-sync had failed before it was stopped.", error);
            }
        if (running is null || running.IsCompleted) lifetime.Dispose();
    }

    private async Task RunCharacterAsync(IAvatarRenderer target, Audio2FaceMapping? automatic, CancellationToken token)
    {
        CancellationTokenSource? speaking = null;
        Task? current = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var available = observer.Segments.WaitToReadAsync(token).AsTask();
                if (await Task.WhenAny(available, target.Exited) == target.Exited) break;
                if (!await available) break;
                while (observer.Segments.TryRead(out var segment))
                {
                    speaking?.Cancel();
                    if (current is not null) await Settle(current);
                    speaking?.Dispose();
                    speaking = CancellationTokenSource.CreateLinkedTokenSource(token);
                    current = SpeakAsync(segment, target, automatic, speaking.Token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            speaking?.Cancel();
            if (current is not null) await Settle(current);
            speaking?.Dispose();
        }

        static async Task Settle(Task task)
        {
            try { await task; }
            catch (Exception error) when (!ErrorLog.IsFatal(error)) { SentenceFailed(error); }
        }
    }

    /// <summary>One sentence's lip-sync ended with <paramref name="error"/>: a cancellation says nothing, a renderer failure one
    /// short line, anything else (a bug) a full entry. It never ends the character's lip-sync for the next sentences.</summary>
    private static void SentenceFailed(Exception error)
    {
        if (error is OperationCanceledException) return;
        if (RendererFailures.Is(error, CancellationToken.None))
            RendererFailures.Log("The character's lip-sync stopped for a sentence", error is CharacterRendererException { InnerException: { } inner } ? inner : error);
        else ErrorLog.Error("The character's lip-sync failed for a sentence.", error);
    }

    // A sentence that ends early (canceled or failed) may leave its other parts running: their end is still observed and reported.
    private static void ObserveSentence(Task? part) =>
        part?.ContinueWith(static ended => SentenceFailed(ended.Exception!.GetBaseException()), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private long lastAudio2FaceApply;
    private bool Audio2FaceAnimating =>
        Stopwatch.GetElapsedTime(Volatile.Read(ref lastAudio2FaceApply)) < TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Loudness lip-sync for one sentence, with Audio2Face taking over whenever a local service or the paired
    /// Martlet host delivers frames.
    /// </summary>
    private async Task SpeakAsync(GeneratedSpeechObservation segment, IAvatarRenderer target, Audio2FaceMapping? automatic,
        CancellationToken token)
    {
        var meter = new LoudnessMeter(segment.Format);
        var snapshot = segment.Playback.Snapshot;
        var mono = automatic is not null && segment.Format.Channels == 1;
        GeneratedSpeechStream? stream = null;
        PcmAccumulator? relay = null;
        IReadOnlyList<LipSyncMember> pool = [];
        var local = automatic?.Options;
        if (mono)
        {
            // Lip-sync's pool, in order (docs/AVATARS.md#the-lip-sync-pool): this PC's own Audio2Face service first when the list
            // starts with it, else the computers (each chunk to the first free one), then this PC's service; with none, the
            // voice's loudness.
            var current = profile;
            var places = lipSyncPool.Places(current);
            var own = places.FirstOrDefault(p => p.Host is null);
            local = LocalOptions(own, automatic!.Options);
            var hosts = places.Where(p => p.Host is not null).ToArray();
            if (own is not null && places[0] == own && await Audio2FaceProbe.IsListeningAsync(local, TimeSpan.FromMilliseconds(250), token))
                stream = new GeneratedSpeechStream(snapshot.Ids, snapshot.Epoch, segment.Format, 0, 0, segment.Format.SampleRate * 90L, 128);
            else if (hosts.Length > 0 && (pool = await lipSyncPool.ReadyAsync(hosts, LipSyncPool.Assigned(current)?.HostId,
                Volatile.Read(ref hostLink), token)).Count > 0)
                relay = new PcmAccumulator();
            else if (own is not null && places[0] != own && await Audio2FaceProbe.IsListeningAsync(local, TimeSpan.FromMilliseconds(250), token))
                stream = new GeneratedSpeechStream(snapshot.Ids, snapshot.Epoch, segment.Format, 0, 0, segment.Format.SampleRate * 90L, 128);
        }
        Task? tee = null, present = null;
        var joined = false;
        try
        {
            tee = TeeAsync(segment, meter, stream, relay, token);
            present = LoudnessLipSync.PresentAsync(segment, meter,
                level => target.SendAsync("mouth", new { level }, token), () => Audio2FaceAnimating, token);
            if (automatic is not null && (stream is not null || relay is not null))
            {
                var where = stream is not null ? local!.Endpoint.Authority
                    : "Martlet host " + string.Join(", ", pool.Select(m => m.Link.Authority));
                try
                {
                    var frames = stream is not null
                        ? LocalFramesAsync(stream, local!, token)
                        : RemoteFramesAsync(segment, relay!, lipSyncPool, pool, token);
                    await PlayFramesAsync(segment, frames, automatic, target, where, token);
                }
                catch (CharacterRendererException failure) when (!token.IsCancellationRequested)
                {
                    // The character renderer failed, not Audio2Face: this sentence's frames end, and the voice's loudness mouth
                    // (if the renderer still answers) goes on.
                    RendererFailures.Log("Audio2Face lip-sync stopped for a sentence", failure.InnerException ?? failure);
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is Audio2FaceException or
                    HostConnectionException or ContractException or IOException or InvalidOperationException or
                    OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException)
                {
                    // The pool already left out (for 30 s) each computer that failed or didn't answer; a busy one stays in.
                    Publish($"Audio2Face wasn't available: {(error is Audio2FaceException a ? a.Failure.ToString() :
                        error is HostConnectionException h ? h.Message : error is AvatarOperationException reason ? reason.Message :
                        "service or mapping failure")}. Mouth movement follows Martlet's voice.");
                }
            }
            joined = true;
            await Task.WhenAll(tee, present);
        }
        finally
        {
            stream?.Dispose();
            if (!joined)
            {
                ObserveSentence(tee);
                ObserveSentence(present);
            }
        }
    }

    private static async Task TeeAsync(GeneratedSpeechObservation segment, LoudnessMeter meter,
        GeneratedSpeechStream? stream, PcmAccumulator? relay, CancellationToken token)
    {
        var feeding = stream is not null;
        var completed = false;
        try
        {
            await foreach (var frame in segment.Frames.ReadAllAsync(token))
            {
                meter.Add(frame);
                relay?.Append(frame.Data.Span);
                if (feeding && stream!.TrySubmit(frame) != SpeechIngressResult.Accepted) feeding = false;
            }
            completed = segment.InputCompleted;
            if (segment.Failure == SpeechObservationFailure.QueueFull)
                ErrorLog.Warn("Lip-sync stopped for this sentence: the character didn't read the voice fast enough. The voice continues.");
            if (feeding && (!completed || stream!.CompleteInput(segment.SampleCount) != SpeechIngressResult.Completed)) feeding = false;
        }
        finally
        {
            if (!feeding) stream?.Dispose();
            relay?.Complete(failed: !completed);
        }
    }

    // This PC's own Audio2Face service: at the pool member's endpoint setting when that is a valid loopback address, else the
    // character's.
    private static Audio2FaceOptions LocalOptions(LipSyncPlace? own, Audio2FaceOptions character)
    {
        if (own?.Endpoint is not { } endpoint || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return character;
        var options = character with { Endpoint = uri };
        try
        {
            options.Validate();
            return options;
        }
        catch (ArgumentException) { return character; }
    }

    private static async IAsyncEnumerable<AvatarFrame> LocalFramesAsync(GeneratedSpeechStream stream, Audio2FaceOptions options,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var permission = new Audio2FaceAuthorization(stream, options, DateTimeOffset.UtcNow.AddSeconds(90), true);
        await foreach (var frame in new Audio2FaceAdapter(options).AnimateAsync(stream, permission, token))
            yield return frame;
    }

    /// <summary>Relays the sentence in short chunks to lip-sync's pool so animation starts before the sentence ends: each chunk
    /// goes to the first computer of <paramref name="members"/> that takes it (<see cref="LipSyncPool"/>).</summary>
    private static async IAsyncEnumerable<AvatarFrame> RemoteFramesAsync(GeneratedSpeechObservation segment, PcmAccumulator pcm,
        LipSyncPool pool, IReadOnlyList<LipSyncMember> members, [EnumeratorCancellation] CancellationToken token)
    {
        var snapshot = segment.Playback.Snapshot;
        var rate = segment.Format.SampleRate;
        var frames = Channel.CreateBounded<AvatarFrame>(new BoundedChannelOptions(1024) { SingleWriter = true, SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producer = Task.Run(async () =>
        {
            int chunks = 0, empty = 0;
            try
            {
                long sequence = 0, last = -1;
                await foreach (var (from, start, end) in RemoteChunks.PlanAsync(pcm, rate, stop.Token))
                {
                    var ids = new CorrelationIds { SessionId = snapshot.Ids.SessionId, TurnId = snapshot.Ids.TurnId, RequestId = Guid.NewGuid() };
                    var skipped = pool.Sharing.Skipped;
                    chunks++;
                    await foreach (var face in pool.AnimateAsync(members, ids, snapshot.Epoch, rate, pcm.Slice(from, end), end - start, stop.Token))
                    {
                        var offset = from + face.SampleOffset;
                        if (offset < start || offset <= last) continue;
                        var values = face.Blendshapes.Where(item => AvatarChannels.BlendshapeNames.Contains(item.Key))
                            .Take(52).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                        if (values.Count == 0) continue;
                        last = offset;
                        await frames.Writer.WriteAsync(new AvatarFrame
                        {
                            Version = ContractVersion.Current, Ids = snapshot.Ids, SourceId = SourceId, Epoch = snapshot.Epoch,
                            Sequence = sequence++, SampleRate = rate, SampleOffset = offset, Blendshapes = values, Semantics = []
                        }, stop.Token);
                    }
                    if (pool.Sharing.Skipped > skipped) empty++;
                }
                frames.Writer.TryComplete();
            }
            catch (Exception error) { frames.Writer.TryComplete(error); }
            finally
            {
                if (empty > 0)
                    ErrorLog.Info($"Lip-sync: every computer of the lip-sync pool was busy for {empty} of a sentence's {chunks} chunks; " +
                        "the voice's loudness moved the mouth for them.");
            }
        }, CancellationToken.None);
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(token))
                yield return frame;
        }
        finally
        {
            await stop.CancelAsync();
            try { await producer; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>The one Audio2Face pipeline for a sentence, for Automatic lip-sync and for an activated, reviewed mapping alike:
    /// <paramref name="frames"/> go through <paramref name="mapping"/>'s composition on the sentence's playback clock to the
    /// renderer as reset, apply and stop commands. Every command waits for the one before it (the renderer takes them in order),
    /// a frame that comes too late is skipped (the next catches up with the voice), and every target gets a value (its neutral
    /// one when the frame doesn't set it). The frames own the face while they play, so a song's mouth waits. A renderer failure
    /// becomes a <see cref="CharacterRendererException"/>, so only this sentence's frames end.</summary>
    private async Task PlayFramesAsync(GeneratedSpeechObservation segment, IAsyncEnumerable<AvatarFrame> frames,
        Audio2FaceMapping automatic, IAvatarRenderer target, string where, CancellationToken token)
    {
        var snapshot = segment.Playback.Snapshot;
        var ids = snapshot.Ids;
        var identity = new RendererIdentity(ids.SessionId, ids.TurnId, ids.RequestId, SourceId, snapshot.Epoch, segment.Format.SampleRate);
        var gate = new PlaybackFrameGate(new PlaybackBinding
            { Ids = ids, Epoch = snapshot.Epoch, SourceId = SourceId, SampleRate = segment.Format.SampleRate });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var halt = StopSegmentAsync(segment, stop, target.Exited);
        AvatarComposition? composition = null;
        var started = false;
        var rendererFailed = false;
        // This sentence's frames own the face while they play (a song's mouth waits for them).
        var owner = new object();
        try
        {
            await foreach (var frame in frames.WithCancellation(stop.Token))
            {
                if (frame.SourceId != SourceId) throw new InvalidOperationException("Unexpected analyzer source identity.");
                if (composition is null)
                {
                    var result = Compose(automatic, frame.Blendshapes.Keys.ToHashSet(StringComparer.Ordinal), target);
                    composition = result.Composition ?? throw new AvatarOperationException(
                        string.Join(" ", result.Issues.Select(i => i.Summary)));
                }
                if (!started)
                {
                    await FaceAsync(owner, () => RendererCommandAsync(() => target.SendAsync("reset", identity, token), token), token);
                    started = true;
                }
                PlaybackPosition? position;
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    position = Position(segment);
                    if (position is null)
                    {
                        if (segment.Playback.DeviceClock.State != PlaybackClockState.Waiting)
                            throw new AvatarOperationException("playback clock unavailable");
                    }
                    else if (frame.SampleOffset <= position.SampleOffset) break;
                    else if (frame.SampleOffset - position.SampleOffset > segment.Format.SampleRate)
                        throw new AvatarOperationException("animation ran too far ahead of playback");
                    await Task.Delay(10, stop.Token);
                }
                var composed = composition.Compose(frame, gate, position);
                if (composed.Disposition == FrameDisposition.TooLate) continue;
                if (composed.Disposition != FrameDisposition.Accepted)
                    throw new AvatarOperationException("frame cannot be synchronized: " + composed.Disposition);
                var parameters = automatic.Targets.ToDictionary(t => t.Id,
                    t => composed.Parameters.TryGetValue(t.Id, out var value) ? value : t.Neutral, StringComparer.Ordinal);
                await FaceAsync(owner, () => RendererCommandAsync(() => target.SendAsync("apply", new RendererParameters(identity,
                    frame.Sequence, frame.SampleOffset, position.SampleOffset, automatic.Config.ModelRevision, automatic.Config.MappingRevision,
                    parameters), stop.Token), stop.Token), stop.Token);
                if (Volatile.Read(ref lastAudio2FaceApply) == 0 || !Audio2FaceAnimating)
                    Publish($"Lip-sync is using Audio2Face at {where}.");
                Volatile.Write(ref lastAudio2FaceApply, Stopwatch.GetTimestamp());
            }
        }
        catch (CharacterRendererException)
        {
            rendererFailed = true;
            throw;
        }
        finally
        {
            gate.Stop();
            await stop.CancelAsync();
            try { await halt; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            // A renderer that just failed isn't asked again: the stop would only wait for its time limit.
            if (started && !rendererFailed && !target.HasExited && !token.IsCancellationRequested)
                try { await target.SendAsync("stop", new { }, token); }
                catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, token)) { }
            await ReleaseFaceAsync(owner);
        }
    }

    /// <summary>The composition of <paramref name="mapping"/> for the blendshape <paramref name="channels"/> a source gives: only
    /// the mapped channels it has are used, and the targets the others drive stay neutral. Speech and a song's mouth share it.</summary>
    private CompositionResult Compose(Audio2FaceMapping mapping, IReadOnlySet<string> channels, IAvatarRenderer target)
    {
        var settings = mapping.Settings with
        {
            MappingProfiles = [.. mapping.Settings.MappingProfiles.Select(p => p with
            {
                Mappings = [.. p.Mappings.Where(m => m.Source.Blendshape is { } name && channels.Contains(name))]
            })]
        };
        var model = new ModelCapabilities { ModelId = target.Capabilities!.ModelId, Renderer = profile!.Renderer,
            MetadataKnown = true, Readiness = RuntimeReadiness.Available, Parameters = mapping.Targets };
        var source = new SourceCapabilities { SourceId = SourceId, Backend = AvatarBackend.Audio2Face,
            ChannelsKnown = true, Readiness = RuntimeReadiness.Available,
            Channels = [.. channels.Select(name => new ChannelReference { Blendshape = name })] };
        return AvatarComposer.Create(settings, model, [source]);
    }

    /// <summary>One character renderer command for a sentence's Audio2Face frames: a failure of the renderer (not a cancellation
    /// by <paramref name="caller"/>) becomes a <see cref="CharacterRendererException"/>, so only this sentence's frames end.</summary>
    private static async Task RendererCommandAsync(Func<Task<RendererMessage>> send, CancellationToken caller)
    {
        try { await send().ConfigureAwait(false); }
        catch (Exception error) when (RendererFailures.Is(error, caller)) { throw new CharacterRendererException(error); }
    }

    private IReadOnlyList<ModelParameter> Targets(AvatarConfiguration settings)
    {
        var capabilities = renderer!.Capabilities!;
        var result = new List<ModelParameter>();
        foreach (var assignment in settings.Assignments)
        {
            var mapping = settings.MappingProfiles.SingleOrDefault(p => p.Id == assignment.MappingId)
                ?? throw new InvalidOperationException("Each aspect needs a mapping.");
            foreach (var item in mapping.Mappings.Where(m => AvatarChannels.Aspect(m.Source) == assignment.Aspect))
            {
                var target = capabilities.Parameters.SingleOrDefault(p => p.Id == item.TargetParameterId)
                    ?? throw new InvalidOperationException("The selected model control is no longer available.");
                if (!target.Aspects.Contains(assignment.Aspect.ToString(), StringComparer.Ordinal))
                    throw new InvalidOperationException("That model control doesn't support this mapping.");
                result.Add(new() { Id = target.Id, Aspect = assignment.Aspect, Minimum = target.Minimum,
                    Maximum = target.Maximum, Neutral = target.Neutral });
            }
        }
        if (result.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidOperationException("A model control can't be used by more than one mapping.");
        return result;
    }

    private async Task RunAsync(Audio2FaceMapping mapping, long current, IAvatarRenderer ownedRenderer, CancellationToken token)
    {
        var runtimeFailed = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var available = observer.Segments.WaitToReadAsync(token).AsTask();
                if (await Task.WhenAny(available, ownedRenderer.Exited) == ownedRenderer.Exited)
                    throw new IOException("Renderer exited.");
                if (!await available) break;
                if (!observer.Segments.TryRead(out var segment)) continue;
                if (current != Volatile.Read(ref generation)) return;
                try { await AnimateAsync(segment, mapping, ownedRenderer, token); }
                catch (Exception error) when (error is Audio2FaceException or ContractException or OperationCanceledException ||
                    RendererFailures.Is(error, token))
                {
                    if (token.IsCancellationRequested) break;
                    Publish($"Audio2Face couldn't animate this speech: {(error is Audio2FaceException a ? a.Failure.ToString() :
                        error is AvatarOperationException reason ? reason.Message :
                        segment.Failure != SpeechObservationFailure.None ? segment.Failure.ToString() : "runtime or mapping failure")}, and the voice continues.");
                }
                // The sentence's frames sent their own stop (PlayFramesAsync); a sentence without frames never took the face.
                finally { segment.Stop(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (RendererFailures.Is(error, token))
        {
            RendererFailures.Log("Audio2Face lip-sync stopped", error);
            Publish("Character lip-sync stopped and the voice continues; inspect again before you retry.");
            runtimeFailed = true;
            observer.Disable();
            lock (stateGate) if (activation is { } currentActivation) currentActivation.CancelAsync().Forget();
        }
        finally
        {
            observer.Disable();
            if (!ownedRenderer.HasExited)
            {
                using var clear = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await ownedRenderer.SendAsync("stop", new { }, clear.Token); }
                catch (Exception error) when (RendererFailures.Is(error, CancellationToken.None))
                {
                    Publish("Couldn't reset the character, so Martlet stopped the renderer; the voice continues.");
                    await ownedRenderer.DisposeAsync();
                }
            }
            if (token.IsCancellationRequested && !runtimeFailed) Publish("Audio2Face stopped; show the character again to restart it.");
        }
    }

    /// <summary>An activated mapping's sentence: the voice goes to this PC's Audio2Face service, and its frames play through
    /// <see cref="PlayFramesAsync"/>, as in Automatic lip-sync.</summary>
    private async Task AnimateAsync(GeneratedSpeechObservation segment, Audio2FaceMapping mapping, IAvatarRenderer ownedRenderer,
        CancellationToken caller)
    {
        var snapshot = segment.Playback.Snapshot;
        using var feeding = CancellationTokenSource.CreateLinkedTokenSource(caller);
        using var stream = new GeneratedSpeechStream(snapshot.Ids, snapshot.Epoch, segment.Format, 0, 0, segment.Format.SampleRate * 90L);
        var ingress = FeedAsync(segment, stream, feeding.Token);
        try
        {
            await PlayFramesAsync(segment, LocalFramesAsync(stream, mapping.Options, feeding.Token), mapping, ownedRenderer,
                mapping.Options.Endpoint.Authority, caller);
            await ingress;
        }
        finally
        {
            await feeding.CancelAsync();
            try { await ingress; }
            catch (OperationCanceledException) when (feeding.IsCancellationRequested) { }
        }
    }

    private PlaybackPosition? Position(GeneratedSpeechObservation segment)
    {
        var clock = segment.Playback.DeviceClock;
        if (clock.State != PlaybackClockState.Available || clock.SampleOffset is not { } offset ||
            !allowControlledClock && clock.Origin != PlaybackClockOrigin.NativeDevice ||
            segment.Playback.DeviceClockAge is not { } age || age > TimeSpan.FromMilliseconds(100))
            return null;
        return new() { Ids = clock.Ids, Epoch = clock.Epoch, SampleRate = clock.SampleRate, SampleOffset = offset };
    }

    private static async Task FeedAsync(GeneratedSpeechObservation segment, GeneratedSpeechStream stream, CancellationToken token)
    {
        try
        {
            await foreach (var frame in segment.Frames.ReadAllAsync(token))
                if (stream.TrySubmit(frame) != SpeechIngressResult.Accepted)
                    throw new InvalidOperationException("Animation PCM queue rejected input; voice is unaffected.");
            if (!segment.InputCompleted || stream.CompleteInput(segment.SampleCount) != SpeechIngressResult.Completed)
                throw new InvalidOperationException("Animation PCM input ended incompletely.");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task StopSegmentAsync(GeneratedSpeechObservation segment, CancellationTokenSource stop, Task exited)
    {
        await Task.WhenAny(segment.Stopped, exited, Task.Delay(Timeout.Infinite, stop.Token));
        await stop.CancelAsync();
    }
}
