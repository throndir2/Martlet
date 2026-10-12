using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop;

// ---------- kept motions (docs/AVATARS.md#kept-motions) ----------

internal sealed partial class AvatarController
{
    /// <summary>The most kept motions given to a VRM ahead of their first use (the renderer keeps at most
    /// <see cref="VrmAnimationClip.MaximumKept"/> clips and lets the oldest go).</summary>
    internal const int PreloadedMotions = 8;

    /// <summary>Reads a kept motion's clip by its creation key (<see cref="CharacterMotions.ClipAsync"/>); null while motions
    /// aren't wired.</summary>
    internal Func<string, CancellationToken, Task<byte[]?>>? KeptMotionClip { get; set; }

    /// <summary>The name a kept motion's clip has in the renderer: <c>kept-</c> and its creation key.</summary>
    internal static string ClipName(string key) => "kept-" + key;

    // A kept motion plays its VRMA clip: given to the renderer the first time after each model load, played by name after that.
    private async Task<bool> PlayKeptMotionAsync(CharacterActionSource source, string key, string reason, CancellationToken token)
    {
        var name = ClipName(key);
        RendererAnimationResult? result;
        try
        {
            if (HasAnimation(name)) result = await PlayAnimationAsync(name, token).ConfigureAwait(false);
            else if (KeptMotionClip is { } read && await read(key, token).ConfigureAwait(false) is { } clip)
                result = await LoadAnimationAsync(name, clip, play: true, inPlace: true, token).ConfigureAwait(false);
            else
            {
                Volatile.Write(ref lastAction, $"The motion \"{source.Name}\" isn't on this computer yet ({reason}).");
                ActionPlayed?.Invoke();
                return false;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            ErrorLog.Warn($"Character motion '{source.Name}' ({name}) couldn't be read ({error.Message}).");
            Volatile.Write(ref lastAction, $"The motion \"{source.Name}\" couldn't be read ({reason}).");
            ActionPlayed?.Invoke();
            return false;
        }
        var started = result?.Started == true;
        ErrorLog.Info(started ? $"Character motion '{source.Name}' ({name}) played for {reason}."
            : $"Character motion '{source.Name}' ({name}) didn't play ({reason}){(result?.Reason is { } why ? ": " + why : ".")}");
        return started;
    }

    /// <summary>Gives the showing VRM the kept motions <paramref name="keys"/> (at most <see cref="PreloadedMotions"/>) without
    /// playing them, so a reply's tag for one rarely waits for its file. Returns how many it took.</summary>
    internal async Task<int> PreloadKeptMotionsAsync(IReadOnlyList<string> keys, CancellationToken token)
    {
        if (KeptMotionClip is not { } read || profile?.Renderer != AvatarRenderer.Vrm) return 0;
        var given = 0;
        foreach (var key in keys.Take(PreloadedMotions))
        {
            var name = ClipName(key);
            if (HasAnimation(name) || await read(key, token).ConfigureAwait(false) is not { } clip) continue;
            try
            {
                if (await LoadAnimationAsync(name, clip, play: false, inPlace: true, token).ConfigureAwait(false) is not { } result) break;
                if (result.Kept) given++;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            {
                ErrorLog.Warn($"Kept motion {name} couldn't be given to the character ({error.Message}).");
            }
        }
        return given;
    }
}
