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
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

internal sealed partial class AvatarController : IAsyncDisposable
{
    internal const string SourceId = Audio2FaceAdapter.SourceId;
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly object stateGate = new();
    private readonly bool allowControlledClock;
    private readonly GeneratedSpeechObserver observer = new();
    private IAvatarRenderer? renderer;
    private readonly Func<IAvatarRenderer> createRenderer;
    // Renderers that draw the touch zones picture: never on screen, never animated.
    private readonly Func<IAvatarRenderer> createStillRenderer;
    private readonly Func<AvatarRemoteHost, IAvatarHostLink?> openHost;
    private IAvatarHostLink? hostLink;
    // The other computers that run Audio2Face share lip-sync with the assigned one (hostLink).
    private readonly LipSyncPool lipSyncPool;
    private AvatarProfile? profile;
    private CancellationTokenSource? activation;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? loudness;
    private Task? loudnessWorker;
    private Task? worker;
    private string status = "Character hidden.";
    private long generation;
    internal GeneratedSpeechObserver Observer => observer;
    internal string Status => Volatile.Read(ref status);
    internal bool IsActive => activation is { IsCancellationRequested: false };
    /// <summary>The character window is open (with or without lip-sync).</summary>
    internal bool IsShowing => renderer is { HasExited: false } && profile is not null;
    internal RendererCapabilities? Capabilities => renderer?.Capabilities;
    internal AvatarProfile? InspectedProfile => profile;
    /// <summary>A choice from the showing character's menu ("hide", "open", "talk", "settings", "lock", "mute", "unmute" or
    /// "click-through-on"), raised off the UI thread.</summary>
    internal event Action<string>? Requested;

    internal AvatarController(Func<IAvatarRenderer>? createRenderer = null, bool allowControlledClock = false,
        Func<AvatarRemoteHost, IAvatarHostLink?>? openHost = null, TimeProvider? gazeClock = null,
        Func<IAvatarRenderer>? createStillRenderer = null, Func<AvatarProfile?, IReadOnlyList<LipSyncPlace>>? lipSyncPool = null)
    {
        this.createRenderer = createRenderer ?? SimulatedRendererFailure.Wrap(() => new AvatarRendererProcess(),
            Environment.GetEnvironmentVariable(SimulatedRendererFailure.Variable));
        this.createStillRenderer = createStillRenderer ?? (() => new AvatarRendererProcess(still: true));
        this.allowControlledClock = allowControlledClock;
        this.openHost = openHost ?? GatewayAvatarHostLink.Open;
        this.lipSyncPool = new(this.openHost, lipSyncPool);
        Gaze = new(this, gazeClock);
        _ = Task.Run(ActOnCuesAsync);
    }
    private void Publish(string value) => Volatile.Write(ref status, value);

    /// <summary>Where lip-sync's chunks went on the pool of computers that run Audio2Face (<see cref="LipSyncPool"/>).</summary>
    internal LipSyncSharing LipSyncSharing => lipSyncPool.Sharing;

    // ---------- pictures of the character ----------

    /// <summary>A PNG of the showing character (a head-and-shoulders square for a <paramref name="portrait"/>), or null while it is
    /// hidden or the picture couldn't be taken. Used for the Discord bot's picture and <c>/selfie</c>.</summary>
    internal async Task<byte[]?> SnapshotAsync(bool portrait, CancellationToken token)
    {
        if (renderer is not { HasExited: false } current || profile is null) return null;
        try
        {
            var reply = await current.SendAsync("snapshot", new RendererSnapshot(portrait), token, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (reply.Kind != "picture") return null;
            var picture = RendererProtocol.Data<RendererPicture>(reply);
            return picture.Png.Length == 0 ? null : Convert.FromBase64String(picture.Png);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or TimeoutException or
            ObjectDisposedException or OperationCanceledException or FormatException or System.Text.Json.JsonException)
        {
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            ErrorLog.Warn($"Couldn't take a picture of the character: {error.Message}");
            return null;
        }
    }

    // ---------- where the character looks ----------

    /// <summary>Where the character looks: its usual gaze (the mouse, the mouse when near, straight ahead or your window), what a
    /// reply or a touch changes, and what Martlet decides while it watches the screen.</summary>
    internal CharacterGazeService Gaze { get; }

    /// <summary>The showing character's renderer process (its windows are the character's own), or null.</summary>
    internal int? RendererProcessId => IsShowing && renderer is { } current ? current.ProcessId : null;

    /// <summary>Tells the showing character where to look (<see cref="RendererGaze"/>: a point or the mouse for a while, back to
    /// its usual gaze, or a new usual gaze). Returns what it looks at now, or null while the character is hidden.</summary>
    internal async Task<RendererLook?> GazeAsync(RendererGaze gaze, CancellationToken token)
    {
        if (renderer is not { HasExited: false } current || profile is null) return null;
        var reply = await current.SendAsync("gaze", gaze, token).ConfigureAwait(false);
        return reply.Kind == "look" ? RendererProtocol.Data<RendererLook>(reply) : null;
    }
}

internal sealed class AvatarOperationException(string message) : InvalidOperationException(message);
