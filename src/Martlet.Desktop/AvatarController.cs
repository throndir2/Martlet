using System.IO;
using System.Security.Cryptography;
using Martlet.Audio;
using Martlet.Avatar.Audio2Face;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

internal sealed class AvatarController : IAsyncDisposable
{
    internal const string SourceId = Audio2FaceAdapter.SourceId;
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly bool allowControlledClock;
    private readonly GeneratedSpeechObserver observer = new();
    private IAvatarRenderer? renderer;
    private readonly Func<IAvatarRenderer> createRenderer;
    private AvatarProfile? profile;
    private CancellationTokenSource? activation;
    private Task? worker;
    private string status = "Avatar OFF. No renderer or analysis has run.";
    private long generation;
    internal GeneratedSpeechObserver Observer => observer;
    internal string Status => Volatile.Read(ref status);
    internal bool IsActive => activation is { IsCancellationRequested: false };
    internal RendererCapabilities? Capabilities => renderer?.Capabilities;
    internal AvatarProfile? InspectedProfile => profile;

    internal AvatarController(Func<IAvatarRenderer>? createRenderer = null, bool allowControlledClock = false)
    {
        this.createRenderer = createRenderer ?? (() => new AvatarRendererProcess());
        this.allowControlledClock = allowControlledClock;
    }
    private void Publish(string value) => Volatile.Write(ref status, value);

    internal async Task InspectAsync(AvatarProfile selected, CancellationToken token)
    {
        await StopAsync();
        await changes.WaitAsync(token);
        try
        {
            var snapshot = await LocalAvatarFiles.SnapshotAsync(selected, token);
            var next = createRenderer();
            renderer = next;
            await next.StartAsync(selected, snapshot.Revision, token);
            profile = selected with { ResourceRevision = snapshot.Revision };
            Publish("Renderer/model inspected locally. Analysis OFF; Audio2Face runtime NOT RUN. Review exact target mappings.");
        }
        catch
        {
            Publish("Avatar inspection failed; check local model, prepared SDK/Core, and installed WebView2. Voice is unaffected.");
            if (renderer is { } failed) await failed.DisposeAsync();
            renderer = null;
            profile = null;
            throw;
        }
        finally { changes.Release(); }
    }

    internal async Task ActivateAsync(AvatarProfile selected, bool approved, CancellationToken token)
    {
        if (!approved) throw new InvalidOperationException("Explicit session permission for local generated speech analysis is required.");
        await changes.WaitAsync(token);
        try
        {
            if (worker is { IsCompleted: false } || activation is not null)
                throw new InvalidOperationException("Stop the current avatar activation and await cleanup.");
            if (renderer is null || renderer.HasExited || profile is null || selected.ResourceRevision != profile.ResourceRevision)
                throw new InvalidOperationException("Inspect the selected resources before activation.");
            var snapshot = await LocalAvatarFiles.SnapshotAsync(selected, token);
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
            await renderer.SendAsync("configure", config, token);
            profile = selected;
            activation = new();
            var current = ++generation;
            observer.Enable();
            worker = RunAsync(settings, targets, config, current, activation.Token);
            Publish("Avatar armed for generated speech in this session. Audio2Face runtime awaiting its first real response; no microphone feed.");
        }
        finally { changes.Release(); }
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
        RendererConfiguration config, long current, CancellationToken token)
    {
        try
        {
            await foreach (var segment in observer.Segments.ReadAllAsync(token))
            {
                if (current != Volatile.Read(ref generation)) return;
                try { await AnimateAsync(segment, settings, targets, config, token); }
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
                    if (!token.IsCancellationRequested && renderer is not null)
                        await renderer.SendAsync("stop", new { }, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            Publish("Avatar renderer/transport unavailable. Voice continues; deactivate and inspect before an explicit retry.");
            observer.Disable();
        }
        finally
        {
            observer.Disable();
            if (renderer is { } owned)
            {
                using var clear = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await owned.SendAsync("stop", new { }, clear.Token); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
                {
                    Publish("Avatar could not acknowledge neutral controls; stopping the isolated renderer. Voice continues.");
                    await owned.DisposeAsync();
                }
            }
            if (token.IsCancellationRequested) Publish("Avatar revoked; analysis and pose delivery stopped. STOP releases renderer resources before reactivation.");
        }
    }

    private async Task AnimateAsync(GeneratedSpeechObservation segment, AvatarConfiguration settings,
        IReadOnlyList<ModelParameter> targets, RendererConfiguration config, CancellationToken caller)
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
        var halt = StopSegmentAsync(segment, stop);
        AvatarComposition? composition = null;
        try
        {
            await renderer!.SendAsync("reset", identity, caller);
            await foreach (var frame in adapter.AnimateAsync(stream, permission, stop.Token))
            {
                if (frame.SourceId != SourceId) throw new InvalidOperationException("Unexpected analyzer source identity.");
                if (composition is null)
                {
                    var model = new ModelCapabilities { ModelId = renderer.Capabilities!.ModelId, Renderer = profile.Renderer,
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
                await renderer.SendAsync("apply", new RendererParameters(identity, frame.Sequence, frame.SampleOffset,
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

    private static async Task StopSegmentAsync(GeneratedSpeechObservation segment, CancellationTokenSource stop)
    {
        await Task.WhenAny(segment.Stopped, Task.Delay(Timeout.Infinite, stop.Token));
        await stop.CancelAsync();
    }

    internal void Revoke()
    {
        Interlocked.Increment(ref generation);
        observer.Disable();
        if (activation is { } current) _ = current.CancelAsync();
    }

    internal async Task StopAsync()
    {
        Revoke();
        await changes.WaitAsync();
        try
        {
            if (renderer is not null)
            {
                await renderer.DisposeAsync();
                renderer = null;
            }
            if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(3));
            worker = null;
            activation?.Dispose();
            activation = null;
            profile = null;
            Publish("Avatar OFF. Configuration and user assets preserved; voice unaffected.");
        }
        finally { changes.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        observer.Dispose();
    }
}

internal sealed class AvatarOperationException(string message) : InvalidOperationException(message);
