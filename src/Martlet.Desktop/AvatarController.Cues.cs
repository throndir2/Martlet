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

/// <summary>Emotes, motions and gestures: the reply's cues played on time, and the emotes that stay on (held) across a new renderer.</summary>
internal sealed partial class AvatarController
{
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
                    // A look tag (a gaze, or a screen glance's ninth of the picture) turns the eyes; it is never an emote.
                    if (CharacterGaze.IsLookTag(cue.Tag)) _ = LookLaterAsync(line, cue);
                    else if (catalog?.Off(cue.Tag) is { Count: > 0 } off) _ = StopLaterAsync(off, line, cue, catalog);
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
            if (catalog.Combo(cue.Tag) is { } combo)
                await PlayComboAsync(combo.Tag, [.. sources.Select(source => (source, catalog.Lingers(source)))], "a reply", line.Finished,
                    cueLifetime.Token).ConfigureAwait(false);
            else
                foreach (var source in sources)
                    await PlayActionAsync(source, cue.Tag, line.Finished, cueLifetime.Token, hold: cue.Tag.StartsWith('{') && catalog.Lingers(source))
                        .ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, cueLifetime.Token))
        {
            if (RendererFailures.Is(error, cueLifetime.Token)) RendererFailures.Log($"Character cue {cue.Tag} wasn't acted", error);
        }
    }

    private async Task StopLaterAsync(IReadOnlyList<CharacterActionSource> sources, CharacterCueLine line, CharacterCue cue,
        CharacterActionCatalog catalog)
    {
        try
        {
            if (!await line.ReachedAsync(cue, cueLifetime.Token).ConfigureAwait(false))
            {
                Dropped(cue);
                return;
            }
            if (catalog.Combo(cue.Tag) is { } combo) await StopComboAsync(combo.Tag, sources, "a reply", cueLifetime.Token).ConfigureAwait(false);
            else
                foreach (var source in sources) await StopActionAsync(source, cue.Tag, cueLifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, cueLifetime.Token))
        {
            if (RendererFailures.Is(error, cueLifetime.Token)) RendererFailures.Log($"Character cue {cue.Tag} wasn't acted", error);
        }
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
        // A kept motion (a VRM's, docs/AVATARS.md#kept-motions) plays its VRMA clip once.
        if (source.Kind == CharacterActionKind.Motion && CharacterActionInventory.KeptMotionKey(source.Id) is { } key)
            return await PlayKeptMotionAsync(source, key, reason, token).ConfigureAwait(false);
        var kind = KindOf(source);
        if (hold && Held.Holds(source.Id)) return true;
        var reply = await current.SendAsync("action", new RendererAction(kind, source.Name, true, hold), token).ConfigureAwait(false);
        var started = reply.Data.ValueKind == System.Text.Json.JsonValueKind.Object && reply.Data.TryGetProperty("started", out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.True;
        var drawn = started ? Drawn(reply.Data) : null;
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        // A held gesture is one the renderer keeps (held gestures layer); a gesture it can't hold plays once.
        var heldGestures = HeldGesturesOf(reply.Data);
        var holds = hold && started && (source.Kind != CharacterActionKind.Gesture || heldGestures?.Contains(source.Name) == true);
        var gestures = GestureState(reply.Data);
        Volatile.Write(ref lastAction, (started
            ? $"{(holds ? "Turned on" : "Played")} the {kind} \"{source.Name}\" for {reason} at {when}{drawn}."
            : $"The character couldn't play the {kind} \"{source.Name}\" ({reason}, {when}).") + gestures);
        ErrorLog.Info((started ? $"Character {kind} '{source.Name}' {(holds ? "held" : "played")} for {reason}{drawn}." : $"Character {kind} '{source.Name}' didn't play ({reason}).") + gestures);
        if (source.Kind == CharacterActionKind.Gesture) ForgetLetGo(heldGestures);
        if (holds)
        {
            if (Held.Add(source, profile?.ModelPath, DateTimeOffset.Now, reason) is { } dropped)
                await SendOffAsync(current, dropped.Source, token).ConfigureAwait(false);
        }
        else if (started && source.Kind == CharacterActionKind.Expression) HoldExpression(current, source.Name, finished);
        ActionPlayed?.Invoke();
        return started;
    }

    // Held gestures the renderer let go of (a new held one moved a part of the face they move) are no longer on; the others stay.
    private void ForgetLetGo(IReadOnlyList<string>? heldGestures)
    {
        if (heldGestures is null) return;
        foreach (var other in Held.Current.Where(h => h.Source.Kind == CharacterActionKind.Gesture && !heldGestures.Contains(h.Source.Name)))
            Held.Remove(other.Source.Id);
    }

    /// <summary>", drawn by Martlet over the face at x, y (n pixels wide, tilted d°, how it follows the face)" when the renderer
    /// drew the action itself (an overlay such as the blush glow, for a model without its own, or a stronger blush level), or null. The tilt is the head's
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
    /// "a try" or a settings change); the others stay on. Returns whether it showed.</summary>
    internal async Task<bool> StopActionAsync(CharacterActionSource source, string reason, CancellationToken token)
    {
        if (!Held.Remove(source.Id)) return false;
        var reply = renderer is { HasExited: false } current && profile is not null
            ? await SendOffAsync(current, source, token).ConfigureAwait(false) : default;
        if (source.Kind == CharacterActionKind.Gesture) ForgetLetGo(HeldGesturesOf(reply));
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        var gestures = GestureState(reply);
        Volatile.Write(ref lastAction, $"Turned off the {KindOf(source)} \"{source.Name}\" for {reason} at {when}." + gestures);
        ErrorLog.Info($"Character {KindOf(source)} '{source.Name}' turned off for {reason}." + gestures);
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

    // The renderer's reply to turning it off (its gesture state for a gesture), or an undefined element when it couldn't answer.
    private static async Task<System.Text.Json.JsonElement> SendOffAsync(IAvatarRenderer target, CharacterActionSource source, CancellationToken token)
    {
        try
        {
            if (!target.HasExited)
                return (await target.SendAsync("action", new RendererAction(KindOf(source), source.Name, false, true), token).ConfigureAwait(false)).Data;
        }
        catch (Exception error) when (RendererFailures.Is(error, token))
        {
            RendererFailures.Log($"The character's {KindOf(source)} \"{source.Name}\" couldn't be turned off", error);
        }
        return default;
    }

    /// <summary>Shows the lingering emotes again on a newly shown character of the same model (all of them: held gestures
    /// layer); another model forgets them.</summary>
    private async Task RestoreHeldAsync(IAvatarRenderer target, string? modelPath, CancellationToken token)
    {
        var catalog = Volatile.Read(ref actions)?.Invoke(modelPath);
        IReadOnlyList<string>? heldGestures = null;
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
                    if (held.Source.Kind == CharacterActionKind.Gesture) heldGestures = HeldGesturesOf(reply.Data) ?? heldGestures;
                }
                catch (Exception error) when (RendererFailures.Is(error, token))
                {
                    RendererFailures.Log($"The character's lingering {KindOf(held.Source)} \"{held.Source.Name}\" couldn't come back", error);
                    keep = false;
                }
            if (!keep) Held.Remove(held.Source.Id);
        }
        // A gesture the renderer shows only once, or let go of for a later one, isn't on.
        ForgetLetGo(heldGestures);
    }

    // The gestures the renderer's reply says it holds now (its model's and the drawings over the face), or null when the reply
    // isn't about gestures.
    private static IReadOnlyList<string>? HeldGesturesOf(System.Text.Json.JsonElement reply) =>
        reply.ValueKind == System.Text.Json.JsonValueKind.Object && reply.TryGetProperty("gesture", out var state) &&
        state.ValueKind == System.Text.Json.JsonValueKind.Object
            ? state.TryGetProperty("held", out var held) && held.ValueKind == System.Text.Json.JsonValueKind.Array
                ? held.EnumerateArray().Where(h => h.ValueKind == System.Text.Json.JsonValueKind.String && CharacterActions.IsTag(h.GetString()))
                    .Select(h => h.GetString()!).Take(HeldEmotes.Maximum * 4).ToArray()
                : []
            : null;

    /// <summary>The renderer's reply to a gesture: which gesture now plays once and every one held (" Gestures now: wink
    /// playing, eyes_up, mouth_open, blush held."); empty for other replies.</summary>
    internal static string GestureState(System.Text.Json.JsonElement reply)
    {
        if (HeldGesturesOf(reply) is not { } held) return "";
        var playing = reply.GetProperty("gesture").TryGetProperty("playing", out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String && CharacterActions.IsTag(value.GetString()) ? value.GetString()! : "none";
        return $" Gestures now: {playing} playing, {(held.Count == 0 ? "none" : string.Join(", ", held))} held.";
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
            catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, hold.Token))
            {
                if (RendererFailures.Is(error, hold.Token)) RendererFailures.Log($"The character's expression \"{name}\" couldn't be turned off", error);
            }
            finally
            {
                lock (stateGate) if (expressionHold == hold) expressionHold = null;
                hold.Dispose();
            }
        });
    }
}
