using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

// ---------- VRMA clips ----------

internal sealed partial class AvatarController : ICharacterAnimations
{
    private readonly object animationGate = new();
    // The renderer the kept clips belong to and their names; another model, hiding or a renderer that ended forgets them.
    private IAvatarRenderer? clipsOn;
    private HashSet<string> clipNames = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public event Action? AnimationsForgotten;

    /// <inheritdoc/>
    public async Task<RendererAnimationResult?> LoadAnimationAsync(string name, ReadOnlyMemory<byte> clip, bool play = true,
        bool inPlace = true, CancellationToken token = default)
    {
        if (!VrmAnimationClip.IsValidName(name))
            throw new ArgumentException($"A clip name is 1 to {VrmAnimationClip.MaximumNameLength} characters without control characters.", nameof(name));
        var summary = VrmAnimationClip.Inspect(clip.Span);
        var result = await SendAnimationAsync(new RendererAnimation(name, Convert.ToBase64String(clip.Span), play, inPlace), token).ConfigureAwait(false);
        if (result is not null)
            ErrorLog.Info($"VRMA clip '{name}' ({summary.Duration:0.##} s, {summary.Bones.Count} bones{(summary.MovesHips ? ", hips" : "")}" +
                $"{(inPlace ? ", in place" : "")}) given to the character: {Said(result)}");
        return result;
    }

    /// <inheritdoc/>
    public async Task<RendererAnimationResult?> PlayAnimationAsync(string name, CancellationToken token = default)
    {
        var result = await SendAnimationAsync(new RendererAnimation(name), token).ConfigureAwait(false);
        if (result is not null) ErrorLog.Info($"VRMA clip '{name}' played again: {Said(result)}");
        return result;
    }

    /// <inheritdoc/>
    public bool HasAnimation(string name)
    {
        lock (animationGate)
            return renderer is { HasExited: false } current && ReferenceEquals(clipsOn, current) && clipNames.Contains(name);
    }

    private async Task<RendererAnimationResult?> SendAnimationAsync(RendererAnimation request, CancellationToken token)
    {
        if (renderer is not { HasExited: false } current || profile is null) return null;
        var reply = await current.SendAsync("animation", request, token, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var result = reply.Kind == "animation" ? RendererProtocol.Data<RendererAnimationResult>(reply) : null;
        result = result is null ? new(false, false, request.Name, 0, [], "The character renderer didn't answer.")
            : result with { Clips = result.Clips ?? [] };
        Remember(current, result.Clips);
        var when = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        Volatile.Write(ref lastAction, result.Started
            ? $"Played the VRMA clip \"{request.Name}\" at {when}."
            : $"The character couldn't play the VRMA clip \"{request.Name}\" ({when}){(result.Reason is { } why ? ": " + why : ".")}");
        ActionPlayed?.Invoke();
        return result;
    }

    private static string Said(RendererAnimationResult result) =>
        (result.Started ? "started" : result.Kept ? "kept" : "not kept") + (result.Reason is { } why ? $" ({why})" : "") + ".";

    // The renderer keeps these clips now (it says so in each answer); when it ends, they are gone.
    private void Remember(IAvatarRenderer on, IReadOnlyList<string> names)
    {
        bool watch;
        lock (animationGate)
        {
            watch = !ReferenceEquals(clipsOn, on);
            clipsOn = on;
            clipNames = new(names, StringComparer.Ordinal);
        }
        if (watch)
            _ = on.Exited.ContinueWith(_ => { if (on.HasExited && ReferenceEquals(Volatile.Read(ref clipsOn), on)) ForgetAnimations(); },
                TaskScheduler.Default);
    }

    /// <summary>The clips the character kept are gone (another model loads, the character hides or its renderer ended): raises
    /// <see cref="AnimationsForgotten"/> when it kept any.</summary>
    private void ForgetAnimations()
    {
        bool had;
        lock (animationGate)
        {
            had = clipNames.Count > 0;
            clipsOn = null;
            clipNames = new(StringComparer.Ordinal);
        }
        if (had) AnimationsForgotten?.Invoke();
    }
}
