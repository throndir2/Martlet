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

internal sealed partial class AvatarController : IAsyncDisposable
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
    private string status = "Character hidden.";
    private long generation;
    internal GeneratedSpeechObserver Observer => observer;
    internal string Status => Volatile.Read(ref status);
    internal bool IsActive => activation is { IsCancellationRequested: false };
    /// <summary>The character window is open (with or without lip-sync).</summary>
    internal bool IsShowing => renderer is { HasExited: false } && profile is not null;
    internal RendererCapabilities? Capabilities => renderer?.Capabilities;
    internal AvatarProfile? InspectedProfile => profile;
    /// <summary>A choice from the showing character's menu ("hide", "open", "talk", "settings", "lock", "mute" or "unmute"),
    /// raised off the UI thread.</summary>
    internal event Action<string>? Requested;

    internal AvatarController(Func<IAvatarRenderer>? createRenderer = null, bool allowControlledClock = false,
        Func<AvatarRemoteHost, IAvatarHostLink?>? openHost = null, TimeProvider? gazeClock = null)
    {
        this.createRenderer = createRenderer ?? (() => new AvatarRendererProcess());
        this.allowControlledClock = allowControlledClock;
        this.openHost = openHost ?? GatewayAvatarHostLink.Open;
        Gaze = new(this, gazeClock);
        _ = Task.Run(ActOnCuesAsync);
    }
    private void Publish(string value) => Volatile.Write(ref status, value);

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

    /// <summary>Where the character looks: the mouse, or what Martlet decides while it watches the screen.</summary>
    internal CharacterGazeService Gaze { get; }

    /// <summary>The showing character's renderer process (its windows are the character's own), or null.</summary>
    internal int? RendererProcessId => IsShowing && renderer is { } current ? current.ProcessId : null;

    /// <summary>Turns the showing character's head and eyes toward a point on the desktop for a while, or back to the mouse.
    /// Returns what it looks at now, or null while the character is hidden.</summary>
    internal async Task<RendererLook?> GazeAsync(RendererGaze gaze, CancellationToken token)
    {
        if (renderer is not { HasExited: false } current || profile is null) return null;
        var reply = await current.SendAsync("gaze", gaze, token).ConfigureAwait(false);
        return reply.Kind == "look" ? RendererProtocol.Data<RendererLook>(reply) : null;
    }

    // ---------- emotes and motions ----------

    private readonly CancellationTokenSource cueLifetime = new();
    private Func<string?, CharacterActionCatalog?>? actions;
    private CancellationTokenSource? expressionHold;
    private string? lastAction;

    /// <summary>The character cues of replies (their character tags and the voice's own tags), timed with their sentences.</summary>
    internal CharacterCueFeed Cues { get; } = new();

    /// <summary>What the last emote or motion was and why, or why it couldn't play (model-authored names only).</summary>
    internal string? LastAction => Volatile.Read(ref lastAction);

    /// <summary>Raised (off the UI thread) after an emote or motion plays or fails.</summary>
    internal event Action? ActionPlayed;

    /// <summary>Where the showing model's emotes and motions come from: the catalog for a model path, or null.</summary>
    internal void UseActions(Func<string?, CharacterActionCatalog?> provider) => Volatile.Write(ref actions, provider);

    private async Task ActOnCuesAsync()
    {
        try
        {
            await foreach (var line in Cues.Lines.ReadAllAsync(cueLifetime.Token).ConfigureAwait(false))
            {
                if (!IsShowing) continue;
                var catalog = Volatile.Read(ref actions)?.Invoke(profile?.ModelPath);
                foreach (var cue in line.Cues)
                {
                    // A screen glance's look tag turns the eyes; it is never an emote.
                    if (CharacterGaze.IsTag(cue.Tag)) _ = LookLaterAsync(line, cue);
                    else if (catalog?.Off(cue.Tag) is { } off) _ = StopLaterAsync(off, line, cue);
                    else if (catalog?.For(cue.Tag) is { Count: > 0 } sources) _ = ActLaterAsync(sources, line, cue, catalog);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Each cue waits for its moment in the reply (a pause holds it) and is dropped when the reply is stopped or replaced first.
    private async Task LookLaterAsync(CharacterCueLine line, CharacterCue cue)
    {
        try
        {
            if (await line.ReachedAsync(cue, cueLifetime.Token).ConfigureAwait(false)) Gaze.Chosen(cue.Tag);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ActLaterAsync(IReadOnlyList<CharacterActionSource> sources, CharacterCueLine line, CharacterCue cue,
        CharacterActionCatalog catalog)
    {
        try
        {
            if (!await line.ReachedAsync(cue, cueLifetime.Token).ConfigureAwait(false))
            {
                Dropped(cue);
                return;
            }
            // A reply's {tag} turns a lingering emote on until {/tag}; a voice's sound or tone only ever plays it a moment.
            foreach (var source in sources)
                await PlayActionAsync(source, cue.Tag, line.Finished, cueLifetime.Token, hold: cue.Tag.StartsWith('{') && catalog.Lingers(source))
                    .ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or
            InvalidDataException or TimeoutException or ObjectDisposedException) { }
    }

    private async Task StopLaterAsync(CharacterActionSource source, CharacterCueLine line, CharacterCue cue)
    {
        try
        {
            if (!await line.ReachedAsync(cue, cueLifetime.Token).ConfigureAwait(false))
            {
                Dropped(cue);
                return;
            }
            await StopActionAsync(source, cue.Tag, cueLifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or
            InvalidDataException or TimeoutException or ObjectDisposedException) { }
    }

    private static void Dropped(CharacterCue cue) =>
        ErrorLog.Info($"Character cue {cue.Tag} wasn't acted: the reply stopped before it got there.");

    /// <summary>The lingering emotes the character shows now (until <c>{/tag}</c>, Clear emotes or another model). They stay
    /// across replies and come back when the same model shows again.</summary>
    internal HeldEmotes Held { get; } = new();

    private static string KindOf(CharacterActionSource source) => source.Kind switch
    {
        CharacterActionKind.Expression => "expression", CharacterActionKind.Motion => "motion", _ => "gesture"
    };

    /// <summary>Plays one emote or motion on the showing character because of <paramref name="reason"/> (a reply's tag or
    /// "a try"). An expression shows for at least 4 seconds, until its sentence finishes (plus a second), at most 12 seconds,
    /// unless another one replaces it. With <paramref name="hold"/> it lingers instead (<see cref="Held"/>) until
    /// <see cref="StopActionAsync"/>; one already showing stays as it is. Returns whether the model started it (or it already
    /// showed); false while the character is hidden.</summary>
    internal async Task<bool> PlayActionAsync(CharacterActionSource source, string reason, Task? finished, CancellationToken token,
        bool hold = false)
    {
        if (renderer is not { HasExited: false } current || profile is null) return false;
        var kind = KindOf(source);
        if (hold && Held.Holds(source.Id)) return true;
        var reply = await current.SendAsync("action", new RendererAction(kind, source.Name, true, hold), token).ConfigureAwait(false);
        var started = reply.Data.ValueKind == System.Text.Json.JsonValueKind.Object && reply.Data.TryGetProperty("started", out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.True;
        var drawn = started ? Drawn(reply.Data) : null;
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        // A held gesture is one the renderer keeps (one at a time); a gesture it can't hold plays once.
        var heldGesture = HeldGestureOf(reply.Data);
        var holds = hold && started && (source.Kind != CharacterActionKind.Gesture || heldGesture == source.Name);
        Volatile.Write(ref lastAction, (started
            ? $"{(holds ? "Turned on" : "Played")} the {kind} \"{source.Name}\" for {reason} at {when}{drawn}."
            : $"The character couldn't play the {kind} \"{source.Name}\" ({reason}, {when}).") + GestureState(reply.Data));
        ErrorLog.Info(started ? $"Character {kind} '{source.Name}' {(holds ? "held" : "played")} for {reason}{drawn}." : $"Character {kind} '{source.Name}' didn't play ({reason}).");
        if (source.Kind == CharacterActionKind.Gesture && reply.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
            reply.Data.TryGetProperty("gesture", out _))
            // Another held gesture the renderer let go of is no longer on.
            foreach (var other in Held.Current.Where(h => h.Source.Kind == CharacterActionKind.Gesture && h.Source.Name != heldGesture))
                Held.Remove(other.Source.Id);
        if (holds)
        {
            if (Held.Add(source, profile?.ModelPath, DateTimeOffset.Now) is { } dropped)
                await SendOffAsync(current, dropped.Source, token).ConfigureAwait(false);
        }
        else if (started && source.Kind == CharacterActionKind.Expression) HoldExpression(current, source.Name, finished);
        ActionPlayed?.Invoke();
        return started;
    }

    /// <summary>", drawn by Martlet over the face at x, y (n pixels wide, tilted d°, how it follows the face)" when the renderer
    /// drew the action itself (an overlay such as the blush glow, for a model without its own), or null. The tilt is the head's
    /// roll, clockwise; the face is pinned to a Live2D model's own face meshes, follows a VRM's head bone, or is estimated
    /// from a Live2D model's head angles when it has no face meshes to pin to.</summary>
    internal static string? Drawn(System.Text.Json.JsonElement data)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object || !data.TryGetProperty("overlay", out var overlay) ||
            overlay.ValueKind != System.Text.Json.JsonValueKind.True) return null;
        if (data.TryGetProperty("face", out var face) && face.ValueKind == System.Text.Json.JsonValueKind.Object &&
            face.TryGetProperty("x", out var x) && x.TryGetDouble(out var left) && face.TryGetProperty("y", out var y) &&
            y.TryGetDouble(out var top) && face.TryGetProperty("width", out var width) && width.TryGetDouble(out var size))
        {
            var tilt = face.TryGetProperty("tilt", out var roll) && roll.TryGetDouble(out var degrees)
                ? System.FormattableString.Invariant($", tilted {degrees:0}°") : "";
            var follows = face.TryGetProperty("tracking", out var tracking) && tracking.ValueKind == System.Text.Json.JsonValueKind.String
                ? tracking.GetString() switch
                {
                    "mesh" => ", pinned to the face's meshes",
                    "bones" => ", following the head bone",
                    "estimate" => ", estimated from the head's angles",
                    _ => ""
                } : "";
            return System.FormattableString.Invariant($", drawn by Martlet over the face at {left:0}, {top:0} ({size:0} pixels wide{tilt}{follows})");
        }
        return ", drawn by Martlet over the face (not in view now)";
    }

    /// <summary>Turns off a lingering emote the character shows because of <paramref name="reason"/> (a reply's <c>{/tag}</c>,
    /// "a try" or a settings change). Returns whether it showed.</summary>
    internal async Task<bool> StopActionAsync(CharacterActionSource source, string reason, CancellationToken token)
    {
        if (!Held.Remove(source.Id)) return false;
        if (renderer is { HasExited: false } current && profile is not null) await SendOffAsync(current, source, token).ConfigureAwait(false);
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        Volatile.Write(ref lastAction, $"Turned off the {KindOf(source)} \"{source.Name}\" for {reason} at {when}.");
        ErrorLog.Info($"Character {KindOf(source)} '{source.Name}' turned off for {reason}.");
        ActionPlayed?.Invoke();
        return true;
    }

    /// <summary>Clear emotes: turns off every lingering emote. Returns how many showed.</summary>
    internal async Task<int> ClearHeldAsync(string reason, CancellationToken token)
    {
        var was = Held.Clear();
        if (was.Count == 0) return 0;
        if (renderer is { HasExited: false } current && profile is not null)
            foreach (var held in was) await SendOffAsync(current, held.Source, token).ConfigureAwait(false);
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        Volatile.Write(ref lastAction, $"Cleared {was.Count} lingering emote{(was.Count == 1 ? "" : "s")} for {reason} at {when}.");
        ErrorLog.Info($"Cleared {was.Count} lingering character emotes for {reason}.");
        ActionPlayed?.Invoke();
        return was.Count;
    }

    /// <summary>Turns off lingering emotes the owner turned off or made brief since they went on.</summary>
    internal async Task ReconcileHeldAsync(CancellationToken token)
    {
        if (Volatile.Read(ref actions)?.Invoke(profile?.ModelPath) is not { } catalog) return;
        foreach (var held in Held.Current)
            if (!catalog.Lingers(held.Source)) await StopActionAsync(held.Source, "a settings change", token).ConfigureAwait(false);
    }

    private static async Task SendOffAsync(IAvatarRenderer target, CharacterActionSource source, CancellationToken token)
    {
        try
        {
            if (!target.HasExited) await target.SendAsync("action", new RendererAction(KindOf(source), source.Name, false, true), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or TimeoutException or
            ObjectDisposedException) { }
    }

    /// <summary>Shows the lingering emotes again on a newly shown character of the same model; another model forgets them.</summary>
    private async Task RestoreHeldAsync(IAvatarRenderer target, string? modelPath, CancellationToken token)
    {
        var catalog = Volatile.Read(ref actions)?.Invoke(modelPath);
        foreach (var held in Held.Current)
        {
            var keep = catalog is not null && string.Equals(held.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
                catalog.Lingers(held.Source);
            if (keep)
                try
                {
                    var reply = await target.SendAsync("action", new RendererAction(KindOf(held.Source), held.Source.Name, true, true), token)
                        .ConfigureAwait(false);
                    keep = reply.Data.ValueKind == System.Text.Json.JsonValueKind.Object && reply.Data.TryGetProperty("started", out var value) &&
                        value.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or TimeoutException)
                {
                    keep = false;
                }
            if (!keep) Held.Remove(held.Source.Id);
        }
    }

    // The gesture the renderer's reply says it holds now, or null.
    private static string? HeldGestureOf(System.Text.Json.JsonElement reply) =>
        reply.ValueKind == System.Text.Json.JsonValueKind.Object && reply.TryGetProperty("gesture", out var state) &&
        state.ValueKind == System.Text.Json.JsonValueKind.Object && state.TryGetProperty("held", out var held) &&
        held.ValueKind == System.Text.Json.JsonValueKind.String ? held.GetString() : null;

    /// <summary>The renderer's reply to a gesture: which gesture now plays once and which is held (" Gestures now: wink
    /// playing, shy held."); empty for other replies.</summary>
    internal static string GestureState(System.Text.Json.JsonElement reply)
    {
        if (reply.ValueKind != System.Text.Json.JsonValueKind.Object || !reply.TryGetProperty("gesture", out var state) ||
            state.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
        string Name(string key) => state.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String &&
            CharacterActions.IsTag(value.GetString()) ? value.GetString()! : "none";
        return $" Gestures now: {Name("playing")} playing, {Name("held")} held.";
    }

    private void HoldExpression(IAvatarRenderer target, string name, Task? finished)
    {
        var hold = new CancellationTokenSource();
        lock (stateGate)
        {
            expressionHold?.Cancel();
            expressionHold = hold;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var cap = Task.Delay(TimeSpan.FromSeconds(12), hold.Token);
                await Task.Delay(TimeSpan.FromSeconds(4), hold.Token).ConfigureAwait(false);
                if (finished is { IsCompleted: false })
                    await Task.WhenAny(finished.ContinueWith(_ => Task.Delay(1000), TaskScheduler.Default).Unwrap(), cap).ConfigureAwait(false);
                hold.Token.ThrowIfCancellationRequested();
                if (!target.HasExited) await target.SendAsync("action", new RendererAction("expression", name, false), hold.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or
                InvalidDataException or TimeoutException or ObjectDisposedException) { }
            finally
            {
                lock (stateGate) if (expressionHold == hold) expressionHold = null;
                hold.Dispose();
            }
        });
    }

    internal async Task UpdateThemeAsync(CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && Application.Current is App app)
                await current.SendAsync("theme", new RendererTheme(app.SelectedTheme.IsDark(), app.ThemeColors), token);
        }
        finally { changes.Release(); }
    }

    /// <summary>Returns the character overlay to its default spot, size and zoom on the main screen, even when its position is
    /// locked (it then stays locked there). Hidden, it forgets the saved place, so the character next shows at its default spot,
    /// unlocked. Returns where the character now is (null once forgotten).</summary>
    internal async Task<RendererPlacement?> ResetPositionAsync(CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && profile is not null)
            {
                var reply = await current.SendAsync("home", new { }, token);
                var place = reply.Kind == "placement" ? RendererProtocol.Data<RendererPlacement>(reply) : null;
                if (place is not { IsValid: true }) throw new InvalidDataException("The character didn't confirm its position.");
                Placement = place;
            }
            else Placement = null;
            return Placement;
        }
        finally { changes.Release(); }
    }

    /// <summary>Asks the showing character where it is now (after it was moved or resized) and remembers it. Null while hidden.</summary>
    internal async Task<RendererPlacement?> ReadPlacementAsync(CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is not { HasExited: false } current || profile is null) return null;
            var reply = await current.SendAsync("where", new { }, token);
            var place = reply.Kind == "placement" ? RendererProtocol.Data<RendererPlacement>(reply) : null;
            if (place is not { IsValid: true }) throw new InvalidDataException("The character didn't report its position.");
            return Placement = place;
        }
        finally { changes.Release(); }
    }

    private RendererPlacement? placement;

    /// <summary>Where the character was last left on this PC's desktop (and on which monitor), locked or not, or null for its
    /// default spot. A newly shown character goes back there, locked again if it was.</summary>
    internal RendererPlacement? Placement
    {
        get => Volatile.Read(ref placement);
        set => Volatile.Write(ref placement, value is { IsValid: true } ? value : null);
    }

    /// <summary>Where the character is locked, or null while it moves freely.</summary>
    internal RendererPlacement? LockedPlacement => Placement is { Locked: true } locked ? locked : null;

    internal bool PlacementLocked => LockedPlacement is not null;

    private int voiceMuted;
    private RendererCamera? camera;

    /// <summary>The camera view (its background color, any picture and the framing it opens with) while the character shows in
    /// its own 16:9 window for OBS (Martlet in your Discord calls), or null for the usual overlay.</summary>
    internal RendererCamera? Camera => Volatile.Read(ref camera);

    /// <summary>Opens the camera view (the character on <paramref name="view"/>'s solid color or picture in an ordinary 16:9
    /// window, framed as asked when it opens; a change of background keeps the current framing) or closes it (null). Returns
    /// whether the showing character now shows it; hidden, it shows that way when it shows next.</summary>
    internal async Task<bool> SetCameraAsync(RendererCamera? view, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            view = view is { On: true } ? view : null;
            Volatile.Write(ref camera, view);
            if (renderer is not { HasExited: false } current || profile is null) return false;
            await current.SendAsync("camera", view ?? new RendererCamera(false), token);
            return view is not null;
        }
        finally { changes.Release(); }
    }

    /// <summary>Remembers how the character is framed in the open camera view, so it opens framed that way when it shows
    /// again.</summary>
    internal void RememberCameraFraming(double zoom, double x, double y)
    {
        if (Camera is { } open) Volatile.Write(ref camera, open with { Zoom = zoom, X = x, Y = y });
    }

    /// <summary>Martlet's voice is muted (Speak Martlet's replies aloud is off): a newly shown character's menu offers Unmute
    /// voice instead of Mute voice. Set it before showing; <see cref="SetVoiceMutedAsync"/> also tells a showing character.</summary>
    internal bool VoiceMuted
    {
        get => Volatile.Read(ref voiceMuted) != 0;
        set => Volatile.Write(ref voiceMuted, value ? 1 : 0);
    }

    /// <summary>Records whether Martlet's voice is muted and tells the showing character, so its menu offers the other choice.
    /// Hidden, the next showing starts with it.</summary>
    internal async Task SetVoiceMutedAsync(bool muted, CancellationToken token)
    {
        VoiceMuted = muted;
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && profile is not null)
                await current.SendAsync("voice", new RendererVoice(VoiceMuted), token);
        }
        finally { changes.Release(); }
    }

    /// <summary>
    /// Locks the showing character where it is, or unlocks it (also while it is hidden, keeping where it was). Returns where the
    /// character now is (null when hidden with no saved place). Locking needs the character showing: its place is wherever it is now.
    /// </summary>
    internal async Task<RendererPlacement?> LockPlacementAsync(bool locked, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && profile is not null)
            {
                var reply = await current.SendAsync("lock", new RendererLock(locked), token);
                var place = reply.Kind == "placement" ? RendererProtocol.Data<RendererPlacement>(reply) : null;
                if (place is null || place.Locked != locked || !place.IsValid)
                    throw new InvalidDataException("The character didn't confirm its position.");
                Placement = place;
            }
            else if (locked) throw new InvalidOperationException("Show the character first, then lock it where you want it.");
            else Placement = Placement is { } saved ? saved with { Locked = false } : null;
            return Placement;
        }
        finally { changes.Release(); }
    }

    /// <summary>
    /// Zooms the character overlay "in", "out", "reset"s it to the default size without moving it, or reads its
    /// "status". Returns the resulting view, or null while the character is hidden.
    /// </summary>
    internal async Task<RendererView?> ZoomAsync(string action, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is not { HasExited: false } current) return null;
            var reply = await current.SendAsync("zoom", new RendererZoom(action), token);
            return reply.Kind == "view" ? RendererProtocol.Data<RendererView>(reply) : null;
        }
        finally { changes.Release(); }
    }

    /// <summary>
    /// Shows (or with null text, hides) a speech bubble beside the character, placed as <paramref name="say"/> asks. A no-op
    /// while the character is hidden. Returns where the overlay put the bubble, or null when the character isn't showing.
    /// </summary>
    internal async Task<RendererBubble?> SayAsync(RendererSay say, CancellationToken token)
    {
        if (!IsShowing || renderer is not { HasExited: false } current) return null;
        var reply = await current.SendAsync("say", say, token);
        return reply.Kind == "bubble" ? RendererProtocol.Data<RendererBubble>(reply) : new("unknown", 0, 0, 0, 0);
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
                Publish("Character is showing. Review and activate Audio2Face below to start lip-sync.");
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
                    ? "Character is showing. No compatible mouth controls were found."
                    : "Character is showing. Mouth movement follows Martlet's voice.");
            else if (hostLink is { } assigned && await assigned.ReadyAsync(token))
                Publish($"Character is showing. Lip-sync is using host {assigned.Authority}.");
            else if (await Audio2FaceProbe.IsListeningAsync(automatic.Options, TimeSpan.FromMilliseconds(300), token))
                Publish($"Character is showing. Lip-sync is using Audio2Face at {automatic.Options.Endpoint.Authority}.");
            else if (hostLink is { } host)
                Publish($"Character is showing. Host {host.Authority} isn't ready, so mouth movement follows Martlet's voice.");
            else if (selected.RemoteHost is not null)
                Publish("Character is showing. Pair the host again to use it for lip-sync.");
            else
                Publish("Character is showing. Audio2Face isn't running, so mouth movement follows Martlet's voice.");
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
                    Publish($"Audio2Face wasn't available: {(error is Audio2FaceException a ? a.Failure.ToString() :
                        error is Audio2FaceHostException h ? h.Message : error is AvatarOperationException reason ? reason.Message :
                        "service or mapping failure")}. Mouth movement follows Martlet's voice.");
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
        // This sentence's frames own the face while they play (a song's mouth waits for them).
        var owner = new object();
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
                    await FaceAsync(owner, () => target.SendAsync("reset", identity, token), token);
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
                await FaceAsync(owner, () => target.SendAsync("apply", new RendererParameters(identity, frame.Sequence, frame.SampleOffset,
                    position.SampleOffset, automatic.Config.ModelRevision, automatic.Config.MappingRevision, parameters), stop.Token), stop.Token);
                if (Volatile.Read(ref lastAudio2FaceApply) == 0 || !Audio2FaceAnimating)
                    Publish($"Lip-sync is using Audio2Face at {where}.");
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
            await ReleaseFaceAsync(owner);
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
            next.Requested += action => { if (ReferenceEquals(Volatile.Read(ref renderer), next)) Requested?.Invoke(action); };
            next.Touched += touch => { if (ReferenceEquals(Volatile.Read(ref renderer), next)) OnTouched(touch); };
            next.Stroked += stroke => { if (ReferenceEquals(Volatile.Read(ref renderer), next) && IsShowing) Stroked?.Invoke(stroke); };
            next.Physical += change => { if (ReferenceEquals(Volatile.Read(ref renderer), next) && IsShowing) PhysicalChanged?.Invoke(change); };
            renderer = next;
            await next.StartAsync(selected, snapshot.Revision, Placement, VoiceMuted, attempt.Token);
            // A camera view that was open stays open when the character shows again.
            if (Camera is { } view) await next.SendAsync("camera", view, attempt.Token);
            // Lingering emotes come back on the same model; another model forgets them.
            await RestoreHeldAsync(next, selected.ModelPath, attempt.Token);
            lock (stateGate)
            {
                CheckAttempt(attempt, version);
                profile = selected with { ResourceRevision = snapshot.Revision };
                Publish("Model controls inspected. Review the mappings before activating Audio2Face.");
            }
        }
        catch
        {
            Publish("Couldn't inspect the model. Check the model file, SDK folder and WebView2, then try again.");
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
        if (!approved) throw new InvalidOperationException("Allow Audio2Face for this session first.");
        var (attempt, version) = BeginAttempt(token, replace: false);
        var entered = false;
        try
        {
            await changes.WaitAsync(attempt.Token);
            entered = true;
            CheckAttempt(attempt, version);
            if (worker is { IsCompleted: false } || activation is not null)
                throw new InvalidOperationException("Stop the current lip-sync action and wait a moment.");
            if (renderer is null || renderer.HasExited || profile is null || selected.ProfileId != profile.ProfileId ||
                selected.ResourceRevision != profile.ResourceRevision)
                throw new InvalidOperationException("Inspect this model before activating lip-sync.");
            var snapshot = await LocalAvatarFiles.SnapshotAsync(selected, attempt.Token);
            CheckAttempt(attempt, version);
            if (snapshot.Revision != selected.ResourceRevision)
                throw new InvalidOperationException("The model changed. Inspect it again.");
            var settings = selected.Settings with { Enabled = true };
            if (settings.Assignments.Any(a => a.SourceId != SourceId) ||
                settings.RequestedAspects.Except(settings.OmittedAspects).Any(a => a is not (AvatarAspect.Mouth or AvatarAspect.Expression)))
                throw new InvalidOperationException("Only mouth and expression mappings can be activated. Omit the rest.");
            if (settings.Assignments.Count == 0) throw new InvalidOperationException("Add at least one mapping first.");
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
                Publish("Audio2Face is ready for this session. It will start when Martlet speaks.");
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
                throw new InvalidOperationException("Another avatar action is still finishing. Wait a moment and try again.");
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
                throw new OperationCanceledException("Avatar action was replaced.", attempt.Token);
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
                    Publish($"Audio2Face couldn't animate this speech: {(error is Audio2FaceException a ? a.Failure.ToString() :
                        error is AvatarOperationException reason ? reason.Message :
                        segment.Failure != SpeechObservationFailure.None ? segment.Failure.ToString() : "runtime or mapping failure")}. Voice continues.");
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
            Publish("Character lip-sync stopped. Voice continues; inspect again before retrying.");
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
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
                {
                    Publish("Couldn't reset the character, so Martlet stopped the renderer. Voice continues.");
                    await ownedRenderer.DisposeAsync();
                }
            }
            if (token.IsCancellationRequested && !runtimeFailed) Publish("Audio2Face stopped. Show the character again to restart it.");
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
                            throw new AvatarOperationException("playback timing isn't available");
                    }
                    else if (frame.SampleOffset <= position.SampleOffset) break;
                    else if (frame.SampleOffset - position.SampleOffset > segment.Format.SampleRate)
                        throw new AvatarOperationException("animation is too far ahead of playback");
                    await Task.Delay(10, stop.Token);
                }
                var composed = composition.Compose(frame, gate, position);
                if (composed.Disposition != FrameDisposition.Accepted)
                    throw new AvatarOperationException("frame cannot be synchronized: " + composed.Disposition);
                await ownedRenderer.SendAsync("apply", new RendererParameters(identity, frame.Sequence, frame.SampleOffset,
                    position.SampleOffset, config.ModelRevision, config.MappingRevision, composed.Parameters), stop.Token);
                Publish("Audio2Face lip-sync is active.");
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
            if (pending is { } attempt) attempt.CancelAsync().Forget();
            if (activation is { } current) current.CancelAsync().Forget();
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
            Publish("Character hidden. Voice continues.");
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
                    ? $"Lip-sync uses host {next.Authority}."
                    : $"Host {next.Authority} isn't ready; mouth movement follows Martlet's voice for now.");
            else if (remote is not null)
                Publish("Pair the host again to use it for lip-sync.");
            else
                Publish("Lip-sync uses this PC when Audio2Face is available; otherwise it follows Martlet's voice.");
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
        if (worker is { } running)
        {
            // A worker that already ended (even with an error) is finished; only a still-running one blocks cleanup.
            try { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (running.IsCompleted)
            {
                ErrorLog.Warn("Avatar lip-sync analysis had ended with an error before it was stopped.", error);
            }
        }
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
        await cueLifetime.CancelAsync();
        await StopAsync();
        observer.Dispose();
    }
}

internal sealed class AvatarOperationException(string message) : InvalidOperationException(message);
