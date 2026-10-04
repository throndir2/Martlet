using System.IO;
using Martlet.Avatar.Audio2Face;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>The character singing: a song's mouth track (made once from its vocals when the song was made) moves the mouth on
/// the song's playback clock. With the character's mouth mapping (Automatic lip-sync) its blendshapes go through the same
/// composition and reset/apply protocol as Audio2Face for speech, so VRM's aa/ih/ou/ee/oh and Live2D's mouth open and form
/// follow the sung vowels; otherwise its opening goes through the loudness mouth. A reply's Audio2Face frames own the face while
/// they play (the song's mouth waits), and either one resets the renderer's playback identity before it takes the face back.</summary>
internal sealed partial class AvatarController : ISongFace
{
    // The renderer's reset/apply playback identity has one owner at a time: a reply's Audio2Face frames or a song's mouth.
    private readonly SemaphoreSlim faceGate = new(1, 1);
    private object? faceOwner;
    private AutoAudio2Face? automaticNow;
    private SongFace? songFace;
    private string? songLipSync;

    /// <summary>How the song's mouth reached the character last: "mapped mouth shapes" or "mouth opening", or null.</summary>
    public string? Route => Volatile.Read(ref songLipSync);

    private sealed class SongFace(RendererIdentity identity, PlaybackFrameGate gate, AvatarComposition composition, AutoAudio2Face automatic,
        CorrelationIds ids, long epoch)
    {
        internal RendererIdentity Identity { get; } = identity;
        internal PlaybackFrameGate Gate { get; } = gate;
        internal AvatarComposition Composition { get; } = composition;
        internal AutoAudio2Face Automatic { get; } = automatic;
        internal CorrelationIds Ids { get; } = ids;
        internal long Epoch { get; } = epoch;
        internal long Sequence { get; set; }
        internal IReadOnlySet<string> Channels { get; init; } = new HashSet<string>();
    }

    /// <summary>The mouth for what the song plays now: <paramref name="shape"/> (blendshapes, 0 to 1) and its overall
    /// <paramref name="level"/> at playback frame <paramref name="output"/> (48 kHz, never going back). Does nothing while the
    /// character is hidden, and waits while a reply's Audio2Face frames move the mouth.</summary>
    public async Task SingAsync(long output, IReadOnlyDictionary<string, double> shape, double level, CancellationToken token)
    {
        if (renderer is not { HasExited: false } target || profile is null) return;
        var automatic = Volatile.Read(ref automaticNow);
        if (automatic is null || shape.Count == 0)
        {
            await target.SendAsync("mouth", new { level = Math.Clamp(level, 0, 1) }, token).ConfigureAwait(false);
            Volatile.Write(ref songLipSync, "mouth opening");
            return;
        }
        await faceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (faceOwner is not null and not SongFace) return;
            var face = songFace;
            if (face is null || !ReferenceEquals(faceOwner, face) || !ReferenceEquals(face.Automatic, automatic) ||
                !face.Channels.SetEquals(shape.Keys))
            {
                face = NewSongFace(target, automatic, shape.Keys, (songFace?.Epoch ?? -1) + 1);
                if (face is null)
                {
                    await target.SendAsync("mouth", new { level = Math.Clamp(level, 0, 1) }, token).ConfigureAwait(false);
                    Volatile.Write(ref songLipSync, "mouth opening");
                    return;
                }
                await target.SendAsync("reset", face.Identity, token).ConfigureAwait(false);
                songFace = face;
                faceOwner = face;
            }
            var frame = new AvatarFrame
            {
                Version = ContractVersion.Current, Ids = face.Ids, SourceId = SourceId, Epoch = face.Epoch, Sequence = face.Sequence++,
                SampleRate = face.Identity.SampleRate, SampleOffset = output,
                Blendshapes = shape.ToDictionary(item => item.Key, item => Math.Clamp(item.Value, 0, 1), StringComparer.Ordinal), Semantics = []
            };
            var position = new PlaybackPosition { Ids = face.Ids, Epoch = face.Epoch, SampleRate = face.Identity.SampleRate, SampleOffset = output };
            var composed = face.Composition.Compose(frame, face.Gate, position);
            if (composed.Disposition != FrameDisposition.Accepted) return;
            var parameters = automatic.Targets.ToDictionary(t => t.Id,
                t => composed.Parameters.TryGetValue(t.Id, out var value) ? value : t.Neutral, StringComparer.Ordinal);
            await target.SendAsync("apply", new RendererParameters(face.Identity, frame.Sequence, output, output,
                automatic.Config.ModelRevision, automatic.Config.MappingRevision, parameters), token).ConfigureAwait(false);
            Volatile.Write(ref songLipSync, "mapped mouth shapes");
        }
        finally { faceGate.Release(); }
    }

    // A playback identity and composition for the song's mouth channels on the character's mapping (only the mapped channels it
    // has), or null when the mapping uses none of them.
    private SongFace? NewSongFace(IAvatarRenderer target, AutoAudio2Face automatic, IEnumerable<string> names, long epoch)
    {
        var channels = names.ToHashSet(StringComparer.Ordinal);
        try
        {
            var settings = automatic.Settings with
            {
                MappingProfiles = [.. automatic.Settings.MappingProfiles.Select(p => p with
                {
                    Mappings = [.. p.Mappings.Where(m => m.Source.Blendshape is { } name && channels.Contains(name))]
                })]
            };
            if (settings.MappingProfiles.All(p => p.Mappings.Count == 0)) return null;
            var model = new ModelCapabilities { ModelId = target.Capabilities!.ModelId, Renderer = profile!.Renderer,
                MetadataKnown = true, Readiness = RuntimeReadiness.Available, Parameters = automatic.Targets };
            var source = new SourceCapabilities { SourceId = SourceId, Backend = AvatarBackend.Audio2Face,
                ChannelsKnown = true, Readiness = RuntimeReadiness.Available,
                Channels = [.. channels.Select(name => new ChannelReference { Blendshape = name })] };
            var result = AvatarComposer.Create(settings, model, [source]);
            if (result.Composition is not { } composition) return null;
            var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
            var bounded = Math.Clamp(epoch, 0, int.MaxValue);
            var gate = new PlaybackFrameGate(new PlaybackBinding { Ids = ids, Epoch = bounded, SourceId = SourceId, SampleRate = 48_000 });
            return new(new RendererIdentity(ids.SessionId, ids.TurnId, ids.RequestId, SourceId, bounded, 48_000), gate, composition,
                automatic, ids, bounded) { Channels = channels };
        }
        catch (Exception error) when (error is InvalidOperationException or ContractException or ArgumentException) { return null; }
    }

    /// <summary>The song stopped: the mouth closes, and a song that owned the face lets it go.</summary>
    public async Task RestAsync(CancellationToken token)
    {
        if (renderer is not { HasExited: false } target || profile is null) return;
        await faceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (faceOwner is SongFace face)
            {
                face.Gate.Stop();
                faceOwner = null;
                await target.SendAsync("stop", new { }, token).ConfigureAwait(false);
            }
            songFace = null;
            await target.SendAsync("mouth", new { level = 0 }, token).ConfigureAwait(false);
        }
        finally { faceGate.Release(); }
    }

    // A reply's Audio2Face frames take the face: the song's mouth (if it had it) waits until they let it go.
    private async Task FaceAsync(object owner, Func<Task> send, CancellationToken token)
    {
        await faceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            faceOwner = owner;
            await send().ConfigureAwait(false);
        }
        finally { faceGate.Release(); }
    }

    private async Task ReleaseFaceAsync(object owner)
    {
        await faceGate.WaitAsync().ConfigureAwait(false);
        try { if (ReferenceEquals(faceOwner, owner)) faceOwner = null; }
        finally { faceGate.Release(); }
    }

    /// <summary>Runs a song's vocals once through Audio2Face, offline, for its mouth track: this PC's Audio2Face service at
    /// <paramref name="endpoint"/> when it answers, otherwise the paired lip-sync host's relay when it is ready. Returns the
    /// blendshape frames in song time and where they were made, or null when no Audio2Face is reachable or it failed.</summary>
    internal async Task<(IReadOnlyList<(TimeSpan At, IReadOnlyDictionary<string, double> Weights)> Faces, string Where)?> AnalyzeSongAsync(
        short[] vocals, int sampleRate, Uri endpoint, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(vocals);
        var options = new Audio2FaceOptions { Endpoint = endpoint };
        try
        {
            if (await Audio2FaceProbe.IsListeningAsync(options, TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false))
                return (await Audio2FaceSong.AnalyzeAsync(vocals, sampleRate, options, token).ConfigureAwait(false), $"Audio2Face at {endpoint.Authority}");
            if (Volatile.Read(ref hostLink) is { } host && await host.ReadyAsync(token).ConfigureAwait(false))
                return (await HostFacesAsync(vocals, sampleRate, host, token).ConfigureAwait(false), $"Audio2Face on host {host.Authority}");
        }
        catch (Exception error) when (error is Audio2FaceException or Audio2FaceHostException or ContractException or IOException or
            InvalidOperationException or TimeoutException or ArgumentException or Grpc.Core.RpcException)
        {
            ErrorLog.Warn($"Singing: Audio2Face couldn't time the mouth to the song ({(error as Audio2FaceException)?.Failure.ToString() ?? error.GetType().Name}).");
        }
        return null;
    }

    // The paired host's relay, in the short chunks it takes for speech (with half a second of context before each).
    private static async Task<IReadOnlyList<(TimeSpan, IReadOnlyDictionary<string, double>)>> HostFacesAsync(short[] vocals, int rate,
        IAvatarHostLink host, CancellationToken token)
    {
        var faces = new List<(TimeSpan, IReadOnlyDictionary<string, double>)>();
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        long last = -1;
        for (long start = 0; start < vocals.Length; start += rate)
        {
            var from = Math.Max(0, start - rate / 2);
            var end = Math.Min(vocals.Length, start + rate);
            var pcm = System.Runtime.InteropServices.MemoryMarshal.AsBytes(vocals.AsSpan((int)from, (int)(end - from))).ToArray();
            await foreach (var face in host.AnimateAsync(ids with { RequestId = Guid.NewGuid() }, 0, rate, pcm, token).ConfigureAwait(false))
            {
                var offset = from + face.SampleOffset;
                if (offset < start || offset <= last) continue;
                last = offset;
                faces.Add((TimeSpan.FromSeconds((double)offset / rate), face.Blendshapes));
            }
        }
        return faces;
    }
}
