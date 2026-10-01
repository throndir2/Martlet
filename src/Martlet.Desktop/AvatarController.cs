using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Windows;
using Martlet.Audio;
using Martlet.Avatar.Audio2Face;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

internal sealed class AvatarController : IAsyncDisposable
{
    internal const string SourceId = Audio2FaceAdapter.SourceId;
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly object stateGate = new();
    private readonly bool allowControlledClock;
    private readonly GeneratedSpeechObserver observer = new();
    private IAvatarRenderer? renderer;
    private readonly Func<IAvatarRenderer> createRenderer;
    private readonly Func<AvatarRemoteHost, IAvatarHostLink?> openHost;
    private IAvatarHostLink? hostLink;
    private AvatarProfile? profile;
    private CancellationTokenSource? activation;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? loudness;
    private Task? loudnessWorker;
    private Task? worker;
    private string status = "Avatar OFF. No renderer or analysis has run.";
    private long generation;
    internal GeneratedSpeechObserver Observer => observer;
    internal string Status => Volatile.Read(ref status);
    internal bool IsActive => activation is { IsCancellationRequested: false };
    /// <summary>The character window is open (with or without lip-sync).</summary>
    internal bool IsShowing => renderer is { HasExited: false } && profile is not null;
    internal RendererCapabilities? Capabilities => renderer?.Capabilities;
    internal AvatarProfile? InspectedProfile => profile;

    internal AvatarController(Func<IAvatarRenderer>? createRenderer = null, bool allowControlledClock = false,
        Func<AvatarRemoteHost, IAvatarHostLink?>? openHost = null)
    {
        this.createRenderer = createRenderer ?? (() => new AvatarRendererProcess());
        this.allowControlledClock = allowControlledClock;
        this.openHost = openHost ?? GatewayAvatarHostLink.Open;
    }
    private void Publish(string value) => Volatile.Write(ref status, value);

    internal async Task UpdateThemeAsync(CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current)
                await current.SendAsync("theme", new RendererTheme(
                    Application.Current is App { SelectedTheme: PinkTheme.Dark }), token);
        }
        finally { changes.Release(); }
    }

    /// <summary>Shows (or with null, hides) a speech bubble beside the character. A no-op while the character is hidden.</summary>
    internal async Task SayAsync(string? text, CancellationToken token)
    {
        if (!IsShowing || renderer is not { HasExited: false } current) return;
        await current.SendAsync("say", new RendererSay(text), token);
    }

    /// <summary>Opens the character with idle animation and the profile's lip-sync mode.</summary>
    internal async Task ShowAsync(AvatarProfile selected, CancellationToken token)
    {
        await InspectAsync(selected, token);
        await changes.WaitAsync(token);
        try
        {
            if (renderer is not { HasExited: false } current || profile is null)
                throw new InvalidOperationException("The character window closed before it finished loading.");
            if (selected.LipSync == AvatarLipSync.Audio2Face)
            {
                Publish("Character showing with idle animation. Audio2Face-only lip-sync needs a reviewed mapping and explicit activation below.");
                return;
            }
            AutoAudio2Face? automatic = null;
            if (selected.LipSync == AvatarLipSync.Auto)
            {
                automatic = AutoConfiguration(profile);
                if (automatic is not null) await current.SendAsync("configure", automatic.Config, token);
                if (automatic is not null && selected.RemoteHost is { } remote)
                {
                    try { Volatile.Write(ref hostLink, openHost(remote)); }
                    catch (Exception error) when (error is Audio2FaceHostException or ContractException) { }
                }
            }
            StartCharacter(current, automatic);
            if (automatic is null)
                Publish(selected.LipSync == AvatarLipSync.Auto
                    ? "Character showing. This model has no mouth control Audio2Face can drive; mouth follows Martlet's voice loudness."
                    : "Character showing. Idle animation on; mouth follows Martlet's voice (local loudness lip-sync).");
            else if (hostLink is { } assigned && await assigned.ReadyAsync(token))
                Publish($"Character showing. Audio2Face on Martlet host {assigned.Authority}; lip-sync uses it, falling back to voice loudness.");
            else if (await Audio2FaceProbe.IsListeningAsync(automatic.Options, TimeSpan.FromMilliseconds(300), token))
                Publish($"Character showing. Audio2Face service detected at {automatic.Options.Endpoint.Authority}; lip-sync uses it, falling back to voice loudness.");
            else if (hostLink is { } host)
                Publish($"Character showing. Martlet host {host.Authority} is not offering Audio2Face right now; lip-sync uses voice loudness and checks again later.");
            else if (selected.RemoteHost is not null)
                Publish("Character showing. The paired Martlet host's credential is missing; pair again in Character settings. Lip-sync uses voice loudness.");
            else
                Publish($"Character showing. No Audio2Face service at {automatic.Options.Endpoint.Authority}; lip-sync uses voice loudness and checks again whenever Martlet speaks.");
        }
        finally { changes.Release(); }
    }

    private sealed record AutoAudio2Face(AvatarConfiguration Settings, IReadOnlyList<ModelParameter> Targets,
        RendererConfiguration Config, Audio2FaceOptions Options);

    /// <summary>A reviewed saved mapping for this exact model, otherwise the built-in mouth mapping.</summary>
    private AutoAudio2Face? AutoConfiguration(AvatarProfile inspected)
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

    private void StartCharacter(IAvatarRenderer target, AutoAudio2Face? automatic)
    {
        lock (stateGate)
        {
            var lifetime = new CancellationTokenSource();
            loudness = lifetime;
            observer.Enable();
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
            lifetime.Cancel();
            if (activation is null) observer.Disable();
        }
        if (running is not null)
            try { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or InvalidOperationException) { }
        if (running is null || running.IsCompleted) lifetime.Dispose();
    }

    private async Task RunCharacterAsync(IAvatarRenderer target, AutoAudio2Face? automatic, CancellationToken token)
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
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or TimeoutException) { }
        }
    }

    private long lastAudio2FaceApply;
    private bool Audio2FaceAnimating =>
        Stopwatch.GetElapsedTime(Volatile.Read(ref lastAudio2FaceApply)) < TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Loudness lip-sync for one sentence, with Audio2Face taking over whenever a local service or the paired
    /// Martlet host delivers frames.
    /// </summary>
    private async Task SpeakAsync(GeneratedSpeechObservation segment, IAvatarRenderer target, AutoAudio2Face? automatic,
        CancellationToken token)
    {
        var meter = new LoudnessMeter(segment.Format);
        var snapshot = segment.Playback.Snapshot;
        var mono = automatic is not null && segment.Format.Channels == 1;
        GeneratedSpeechStream? stream = null;
        PcmAccumulator? relay = null;
        var host = Volatile.Read(ref hostLink);
        // The host assigned to lip-sync goes first; this PC's own Audio2Face service is the fallback.
        if (mono && host is not null && await host.ReadyAsync(token))
            relay = new PcmAccumulator();
        else if (mono && await Audio2FaceProbe.IsListeningAsync(automatic!.Options, TimeSpan.FromMilliseconds(250), token))
            stream = new GeneratedSpeechStream(snapshot.Ids, snapshot.Epoch, segment.Format, 0, 0, segment.Format.SampleRate * 90L, 128);
        try
        {
            var tee = TeeAsync(segment, meter, stream, relay, token);
            var present = LoudnessLipSync.PresentAsync(segment, meter,
                level => target.SendAsync("mouth", new { level }, token), () => Audio2FaceAnimating, token);
            if (automatic is not null && (stream is not null || relay is not null))
            {
                var where = stream is not null ? automatic.Options.Endpoint.Authority : "Martlet host " + host!.Authority;
                try
                {
                    var frames = stream is not null
                        ? LocalFramesAsync(stream, automatic.Options, token)
                        : RemoteFramesAsync(segment, relay!, host!, token);
                    await ApplyFramesAsync(segment, frames, automatic, target, where, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is Audio2FaceException or
                    Audio2FaceHostException or ContractException or IOException or InvalidOperationException or
                    OperationCanceledException or TimeoutException)
                {
                    if (error is Audio2FaceHostException) host?.Invalidate();
                    Publish($"Audio2Face unavailable for this sentence ({(error is Audio2FaceException a ? a.Failure.ToString() :
                        error is Audio2FaceHostException h ? h.Message : error is AvatarOperationException reason ? reason.Message :
                        "service or mapping failure")}); mouth follows voice loudness.");
                }
            }
            await Task.WhenAll(tee, present);
        }
        finally { stream?.Dispose(); }
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
            if (feeding && (!completed || stream!.CompleteInput(segment.SampleCount) != SpeechIngressResult.Completed)) feeding = false;
        }
        finally
        {
            if (!feeding) stream?.Dispose();
            relay?.Complete(failed: !completed);
        }
    }

    private static async IAsyncEnumerable<AvatarFrame> LocalFramesAsync(GeneratedSpeechStream stream, Audio2FaceOptions options,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var permission = new Audio2FaceAuthorization(stream, options, DateTimeOffset.UtcNow.AddSeconds(90), true);
        await foreach (var frame in new Audio2FaceAdapter(options).AnimateAsync(stream, permission, token))
            yield return frame;
    }

    /// <summary>Relays the sentence in short chunks to the paired host so animation starts before the sentence ends.</summary>
    private static async IAsyncEnumerable<AvatarFrame> RemoteFramesAsync(GeneratedSpeechObservation segment, PcmAccumulator pcm,
        IAvatarHostLink host, [EnumeratorCancellation] CancellationToken token)
    {
        var snapshot = segment.Playback.Snapshot;
        var rate = segment.Format.SampleRate;
        var frames = Channel.CreateBounded<AvatarFrame>(new BoundedChannelOptions(1024) { SingleWriter = true, SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producer = Task.Run(async () =>
        {
            try
            {
                long sequence = 0, last = -1;
                await foreach (var (from, start, end) in RemoteChunks.PlanAsync(pcm, rate, stop.Token))
                {
                    var ids = new CorrelationIds { SessionId = snapshot.Ids.SessionId, TurnId = snapshot.Ids.TurnId, RequestId = Guid.NewGuid() };
                    await foreach (var face in host.AnimateAsync(ids, snapshot.Epoch, rate, pcm.Slice(from, end), stop.Token))
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
                }
                frames.Writer.TryComplete();
            }
            catch (Exception error) { frames.Writer.TryComplete(error); }
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

    private async Task ApplyFramesAsync(GeneratedSpeechObservation segment, IAsyncEnumerable<AvatarFrame> frames,
        AutoAudio2Face automatic, IAvatarRenderer target, string where, CancellationToken token)
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
        try
        {
            await foreach (var frame in frames.WithCancellation(stop.Token))
            {
                if (frame.SourceId != SourceId) throw new InvalidOperationException("Unexpected analyzer source identity.");
                if (composition is null)
                {
                    var channels = frame.Blendshapes.Keys.ToHashSet(StringComparer.Ordinal);
                    // Use only mapped channels this service actually returns; unmapped targets stay neutral.
                    var settings = automatic.Settings with
                    {
                        MappingProfiles = automatic.Settings.MappingProfiles.Select(p => p with
                        {
                            Mappings = p.Mappings.Where(m => m.Source.Blendshape is { } name && channels.Contains(name)).ToArray()
                        }).ToArray()
                    };
                    var model = new ModelCapabilities { ModelId = target.Capabilities!.ModelId, Renderer = profile!.Renderer,
                        MetadataKnown = true, Readiness = RuntimeReadiness.Available, Parameters = automatic.Targets };
                    var source = new SourceCapabilities { SourceId = SourceId, Backend = AvatarBackend.Audio2Face,
                        ChannelsKnown = true, Readiness = RuntimeReadiness.Available,
                        Channels = channels.Select(name => new ChannelReference { Blendshape = name }).ToArray() };
                    var result = AvatarComposer.Create(settings, model, [source]);
                    composition = result.Composition ?? throw new AvatarOperationException(
                        string.Join(" ", result.Issues.Select(i => i.Summary)));
                }
                if (!started)
                {
                    await target.SendAsync("reset", identity, token);
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
                await target.SendAsync("apply", new RendererParameters(identity, frame.Sequence, frame.SampleOffset,
                    position.SampleOffset, automatic.Config.ModelRevision, automatic.Config.MappingRevision, parameters), stop.Token);
                if (Volatile.Read(ref lastAudio2FaceApply) == 0 || !Audio2FaceAnimating)
                    Publish($"Lip-sync: Audio2Face at {where} (voice loudness fallback ready).");
                Volatile.Write(ref lastAudio2FaceApply, Stopwatch.GetTimestamp());
            }
        }
        finally
        {
            gate.Stop();
            await stop.CancelAsync();
            try { await halt; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            if (started && !target.HasExited && !token.IsCancellationRequested)
                try { await target.SendAsync("stop", new { }, token); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException) { }
        }
    }

    internal async Task InspectAsync(AvatarProfile selected, CancellationToken token)
    {
        var (attempt, version) = BeginAttempt(token, replace: true);
        var entered = false;
        try
        {
            await changes.WaitAsync(attempt.Token);
            entered = true;
            await CleanupAsync();
            CheckAttempt(attempt, version);
            var snapshot = await LocalAvatarFiles.SnapshotAsync(selected, attempt.Token);
            CheckAttempt(attempt, version);
            var next = createRenderer();
            renderer = next;
            await next.StartAsync(selected, snapshot.Revision, attempt.Token);
            lock (stateGate)
            {
                CheckAttempt(attempt, version);
                profile = selected with { ResourceRevision = snapshot.Revision };
                Publish("Renderer/model inspected locally. Analysis OFF; Audio2Face runtime NOT RUN. Review exact target mappings.");
            }
        }
        catch
        {
            Publish("Avatar inspection failed; check local model, prepared SDK/Core, and installed WebView2 (Prerequisites on the home screen installs it). Voice is unaffected.");
            if (entered)
            {
                if (renderer is { } failed) await failed.DisposeAsync();
                renderer = null;
                profile = null;
            }
            throw;
        }
        finally
        {
            FinishAttempt(attempt);
            if (entered) changes.Release();
        }
    }

    internal async Task ActivateAsync(AvatarProfile selected, bool approved, CancellationToken token)
    {
        if (!approved) throw new InvalidOperationException("Explicit session permission for local generated speech analysis is required.");
        var (attempt, version) = BeginAttempt(token, replace: false);
        var entered = false;
        try
        {
            await changes.WaitAsync(attempt.Token);
            entered = true;
            CheckAttempt(attempt, version);
            if (worker is { IsCompleted: false } || activation is not null)
                throw new InvalidOperationException("Stop the current avatar activation and await cleanup.");
            if (renderer is null || renderer.HasExited || profile is null || selected.ProfileId != profile.ProfileId ||
                selected.ResourceRevision != profile.ResourceRevision)
                throw new InvalidOperationException("Inspect the selected resources before activation.");
            var snapshot = await LocalAvatarFiles.SnapshotAsync(selected, attempt.Token);
            CheckAttempt(attempt, version);
            if (snapshot.Revision != selected.ResourceRevision)
                throw new InvalidOperationException("Selected resources changed. Inspect and review them again.");
            var settings = selected.Settings with { Enabled = true };
            if (settings.Assignments.Any(a => a.SourceId != SourceId) ||
                settings.RequestedAspects.Except(settings.OmittedAspects).Any(a => a is not (AvatarAspect.Mouth or AvatarAspect.Expression)))
                throw new InvalidOperationException("Only the implemented Audio2Face mouth/expression route can activate; explicitly omit unavailable aspects.");
            if (settings.Assignments.Count == 0) throw new InvalidOperationException("Select at least one mapped aspect.");
            var targets = Targets(settings);
            var mappingRevision = Convert.ToHexString(SHA256.HashData(AvatarJson.WriteConfiguration(settings)));
            var config = new RendererConfiguration(SourceId, snapshot.Revision, mappingRevision,
                targets.Select(t => new RendererMapping(t.Id, t.Aspect.ToString())).ToArray());
            await renderer.SendAsync("configure", config, attempt.Token);
            await StopLoudnessAsync();
            lock (stateGate)
            {
                CheckAttempt(attempt, version);
                profile = selected;
                activation = attempt;
                pending = null;
                observer.Enable();
                worker = RunAsync(settings, targets, config, version, renderer, activation.Token);
                Publish("Avatar armed for generated speech in this session. Audio2Face runtime awaiting its first real response; no microphone feed.");
            }
        }
        finally
        {
            FinishAttempt(attempt);
            if (entered) changes.Release();
        }
    }

    private (CancellationTokenSource Source, long Version) BeginAttempt(CancellationToken token, bool replace)
    {
        lock (stateGate)
        {
            token.ThrowIfCancellationRequested();
            if (pending is not null || !replace && activation is not null)
                throw new InvalidOperationException("An avatar action still owns resources. STOP and wait for cleanup.");
            if (replace) Revoke();
            pending = CancellationTokenSource.CreateLinkedTokenSource(token);
            return (pending, ++generation);
        }
    }

    private void CheckAttempt(CancellationTokenSource attempt, long version)
    {
        lock (stateGate)
        {
            attempt.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(pending, attempt) || generation != version)
                throw new OperationCanceledException("Avatar action was superseded.", attempt.Token);
        }
    }

    private void FinishAttempt(CancellationTokenSource attempt)
    {
        lock (stateGate)
        {
            if (ReferenceEquals(pending, attempt)) pending = null;
            if (!ReferenceEquals(activation, attempt)) attempt.Dispose();
        }
    }

    private IReadOnlyList<ModelParameter> Targets(AvatarConfiguration settings)
    {
        var capabilities = renderer!.Capabilities!;
        var result = new List<ModelParameter>();
        foreach (var assignment in settings.Assignments)
        {
            var mapping = settings.MappingProfiles.SingleOrDefault(p => p.Id == assignment.MappingId)
                ?? throw new InvalidOperationException("Each aspect needs an explicit mapping profile.");
            foreach (var item in mapping.Mappings.Where(m => AvatarChannels.Aspect(m.Source) == assignment.Aspect))
            {
                var target = capabilities.Parameters.SingleOrDefault(p => p.Id == item.TargetParameterId)
                    ?? throw new InvalidOperationException("Mapping target is absent from the inspected model.");
                if (!target.Aspects.Contains(assignment.Aspect.ToString(), StringComparer.Ordinal))
                    throw new InvalidOperationException("Mapping aspect is not supported by this authored target.");
                result.Add(new() { Id = target.Id, Aspect = assignment.Aspect, Minimum = target.Minimum,
                    Maximum = target.Maximum, Neutral = target.Neutral });
            }
        }
        if (result.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidOperationException("A model parameter cannot have multiple writers.");
        return result;
    }

    private async Task RunAsync(AvatarConfiguration settings, IReadOnlyList<ModelParameter> targets,
        RendererConfiguration config, long current, IAvatarRenderer ownedRenderer, CancellationToken token)
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
                try { await AnimateAsync(segment, settings, targets, config, ownedRenderer, token); }
                catch (Exception error) when (error is Audio2FaceException or ContractException or IOException or
                    InvalidOperationException or OperationCanceledException or TimeoutException)
                {
                    if (token.IsCancellationRequested) break;
                    Publish($"Segment animation unavailable ({(error is Audio2FaceException a ? a.Failure.ToString() :
                        error is AvatarOperationException reason ? reason.Message :
                        segment.Failure != SpeechObservationFailure.None ? segment.Failure.ToString() : "runtime or mapping failure")}). Voice continues; no fallback.");
                }
                finally
                {
                    segment.Stop();
                    if (!token.IsCancellationRequested)
                        await ownedRenderer.SendAsync("stop", new { }, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            Publish("Avatar renderer/transport unavailable. Voice continues; deactivate and inspect before an explicit retry.");
            runtimeFailed = true;
            observer.Disable();
            lock (stateGate) if (activation is { } currentActivation) _ = currentActivation.CancelAsync();
        }
        finally
        {
            observer.Disable();
            if (!ownedRenderer.HasExited)
            {
                using var clear = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await ownedRenderer.SendAsync("stop", new { }, clear.Token); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
                {
                    Publish("Avatar could not acknowledge neutral controls; stopping the isolated renderer. Voice continues.");
                    await ownedRenderer.DisposeAsync();
                }
            }
            if (token.IsCancellationRequested && !runtimeFailed) Publish("Avatar revoked; analysis and pose delivery stopped. STOP releases renderer resources before reactivation.");
        }
    }

    private async Task AnimateAsync(GeneratedSpeechObservation segment, AvatarConfiguration settings,
        IReadOnlyList<ModelParameter> targets, RendererConfiguration config, IAvatarRenderer ownedRenderer, CancellationToken caller)
    {
        var snapshot = segment.Playback.Snapshot;
        var ids = snapshot.Ids;
        var identity = new RendererIdentity(ids.SessionId, ids.TurnId, ids.RequestId, SourceId, snapshot.Epoch, segment.Format.SampleRate);
        var binding = new PlaybackBinding { Ids = ids, Epoch = snapshot.Epoch, SourceId = SourceId, SampleRate = segment.Format.SampleRate };
        var gate = new PlaybackFrameGate(binding);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
        using var stream = new GeneratedSpeechStream(ids, snapshot.Epoch, segment.Format, 0, 0, segment.Format.SampleRate * 90L);
        var options = new Audio2FaceOptions { Endpoint = new(profile!.Endpoint) };
        using var permission = new Audio2FaceAuthorization(stream, options, DateTimeOffset.UtcNow.AddSeconds(90), true);
        var adapter = new Audio2FaceAdapter(options);
        var ingress = FeedAsync(segment, stream, stop.Token);
        var halt = StopSegmentAsync(segment, stop, ownedRenderer.Exited);
        AvatarComposition? composition = null;
        try
        {
            await ownedRenderer.SendAsync("reset", identity, caller);
            await foreach (var frame in adapter.AnimateAsync(stream, permission, stop.Token))
            {
                if (frame.SourceId != SourceId) throw new InvalidOperationException("Unexpected analyzer source identity.");
                if (composition is null)
                {
                    var model = new ModelCapabilities { ModelId = ownedRenderer.Capabilities!.ModelId, Renderer = profile.Renderer,
                        MetadataKnown = true, Readiness = RuntimeReadiness.Available, Parameters = targets };
                    var source = new SourceCapabilities { SourceId = SourceId, Backend = AvatarBackend.Audio2Face,
                        ChannelsKnown = true, Readiness = RuntimeReadiness.Available,
                        Channels = frame.Blendshapes.Keys.Select(name => new ChannelReference { Blendshape = name }).ToArray() };
                    var result = AvatarComposer.Create(settings, model, [source]);
                    composition = result.Composition ?? throw new InvalidOperationException(
                        string.Join(" ", result.Issues.Select(i => i.Summary)));
                }
                PlaybackPosition? position;
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    position = Position(segment);
                    if (position is null)
                    {
                        if (segment.Playback.DeviceClock.State != PlaybackClockState.Waiting)
                            throw new AvatarOperationException("actual source device clock unavailable: " + segment.Playback.DeviceClock.State);
                    }
                    else if (frame.SampleOffset <= position.SampleOffset) break;
                    else if (frame.SampleOffset - position.SampleOffset > segment.Format.SampleRate)
                        throw new AvatarOperationException("animation exceeds the bounded future window");
                    await Task.Delay(10, stop.Token);
                }
                var composed = composition.Compose(frame, gate, position);
                if (composed.Disposition != FrameDisposition.Accepted)
                    throw new AvatarOperationException("frame cannot be synchronized: " + composed.Disposition);
                await ownedRenderer.SendAsync("apply", new RendererParameters(identity, frame.Sequence, frame.SampleOffset,
                    position.SampleOffset, config.ModelRevision, config.MappingRevision, composed.Parameters), stop.Token);
                Publish("Avatar responding on observed device playback position. Physical synchronization is not yet qualified.");
            }

            await ingress;
        }
        finally
        {
            gate.Stop();
            await stop.CancelAsync();
            try { await Task.WhenAll(ingress, halt); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
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

    internal void Revoke()
    {
        lock (stateGate)
        {
            generation++;
            // Local loudness lip-sync is not a privileged analysis session; it survives pause/mute/config revocations.
            if (loudness is null) observer.Disable();
            if (pending is { } attempt) _ = attempt.CancelAsync();
            if (activation is { } current) _ = current.CancelAsync();
        }
    }

    internal async Task StopAsync()
    {
        Revoke();
        await changes.WaitAsync();
        try
        {
            Revoke();
            await CleanupAsync();
            Publish("Avatar OFF. Configuration and user assets preserved; voice unaffected.");
        }
        finally { changes.Release(); }
    }

    /// <summary>Hands lip-sync to another paired host (or back to this PC with null) while the character keeps showing;
    /// the next sentence uses the new host. A sentence already relaying to the old host falls back to voice loudness.</summary>
    internal async Task UseHostAsync(AvatarRemoteHost? remote, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            IAvatarHostLink? next = null;
            if (remote is not null && IsShowing)
                try { next = openHost(remote); }
                catch (Exception error) when (error is Audio2FaceHostException or ContractException) { }
            Interlocked.Exchange(ref hostLink, next)?.Dispose();
            if (profile is not null) profile = profile with { RemoteHost = remote };
            if (!IsShowing) return;
            if (next is not null)
                Publish(await next.ReadyAsync(token)
                    ? $"Lip-sync handed to Martlet host {next.Authority} (Audio2Face); voice loudness stays as the fallback."
                    : $"Lip-sync handed to Martlet host {next.Authority}. It is not offering Audio2Face yet; voice loudness until it does (checked every 30 s).");
            else if (remote is not null)
                Publish("The paired Martlet host's credential is missing; pair it again. Lip-sync uses this PC's Audio2Face or voice loudness.");
            else
                Publish("Lip-sync handed to this PC: its own Audio2Face service when running, otherwise voice loudness.");
        }
        finally { changes.Release(); }
    }

    private async Task CleanupAsync()
    {
        await StopLoudnessAsync();
        observer.Disable();
        Interlocked.Exchange(ref hostLink, null)?.Dispose();
        if (renderer is not null)
        {
            await renderer.DisposeAsync();
            renderer = null;
        }
        if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(3));
        worker = null;
        lock (stateGate)
        {
            activation?.Dispose();
            activation = null;
        }
        profile = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        observer.Dispose();
    }
}

internal sealed class AvatarOperationException(string message) : InvalidOperationException(message);
