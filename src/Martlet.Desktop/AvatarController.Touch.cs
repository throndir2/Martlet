using System.IO;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

internal sealed partial class AvatarController
{
    private CharacterTouch? lastTouch;

    /// <summary>The showing character was tapped (a left click that didn't drag or pan) and the renderer found it there.
    /// Raised off the UI thread, after the default reaction (<see cref="ReactToTouch"/>) has started.</summary>
    internal event Action<CharacterTouch>? Touched;

    /// <summary>Plays a tap's reaction in place of the built-in one (Touch zones); returns whether it handled the tap.</summary>
    internal Func<CharacterTouch, bool>? TouchRouter { get; set; }

    /// <summary>The last tap on the character, or null.</summary>
    internal CharacterTouch? LastTouch => Volatile.Read(ref lastTouch);

    /// <summary>A batch of a stroke across the locked character (its hit-tested samples), raised off the UI thread.</summary>
    internal event Action<CharacterStroke>? Stroked;

    /// <summary>The user moved, zoomed or panned the showing character and it settled, raised off the UI thread.</summary>
    internal event Action<RendererPhysical>? PhysicalChanged;

    private void OnTouched(CharacterTouch touch)
    {
        if (!IsShowing) return;
        Volatile.Write(ref lastTouch, touch);
        ErrorLog.Info($"The character was tapped on the {touch.CoarseZone}" +
            (touch.HitAreas.Count > 0 ? $" (hit areas {string.Join(", ", touch.HitAreas)})" : "") +
            (touch.Bone is { } bone ? $" (bone {bone}{(touch.Hair ? ", hair" : "")})" : "") + ".");
        ReactToTouch(touch);
        Touched?.Invoke(touch);
    }

    /// <summary>Martlet's built-in reaction to a tap: the model's own tap motion for that part when it has one (a group named
    /// like TapHead, Tap@Head, TouchBody or Tap), else a head tilt (or nod) for the head, hair and face and a surprised look
    /// (or a gasp or nod) elsewhere. Local only: it sends nothing to a model and never waits on a reply.</summary>
    private void ReactToTouch(CharacterTouch touch)
    {
        // Companion › Character › Touch zones routes the tap to the zone it landed in and plays that zone's reaction instead.
        if (TouchRouter?.Invoke(touch) == true) return;
        var zone = touch.CoarseZone;
        var headward = zone is "head" or "hair" or "face";
        var plays = new List<CharacterActionSource>();
        if (TouchMotion(Capabilities?.Model?.MotionGroups ?? [], zone) is { } group)
            plays.Add(new(group, CharacterActionKind.Motion, group, ""));
        foreach (var gesture in headward ? new[] { "tilt", "nod" } : ["surprise", "gasp", "nod"])
            plays.Add(new(gesture, CharacterActionKind.Gesture, gesture, ""));
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var play in plays)
                    if (await PlayActionAsync(play, $"a tap on the {zone}", null, cueLifetime.Token).ConfigureAwait(false)) return;
            }
            catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, cueLifetime.Token))
            {
                if (RendererFailures.Is(error, cueLifetime.Token)) RendererFailures.Log($"The character couldn't react to a tap on the {zone}", error);
            }
        });
    }

    /// <summary>The model's motion group for a tap on <paramref name="zone"/>, matched ignoring case and punctuation: Tap or
    /// Touch followed by the zone, then by Head (for the head, hair and face) or Body (elsewhere), then plain Tap or Touch.</summary>
    internal static string? TouchMotion(IReadOnlyList<string> groups, string zone)
    {
        static string Plain(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var part = zone is "head" or "hair" or "face" ? "head" : "body";
        foreach (var wanted in new[] { "tap" + zone, "touch" + zone, "tap" + part, "touch" + part, "tap", "touch" })
            if (groups.FirstOrDefault(group => Plain(group) == wanted) is { } found) return found;
        return null;
    }
}
