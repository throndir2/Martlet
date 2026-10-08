using System.IO;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

internal sealed partial class AvatarController
{
    private readonly object eyesGate = new();
    private Func<string?, RendererEyes?>? eyesFor;
    // The hint last sent (or being sent) to which renderer, and what that renderer's eyes use since.
    private (IAvatarRenderer Renderer, RendererEyes? Hint)? eyesSent;
    private string? eyesFrom;

    /// <summary>What the showing character's eyes use now, from the renderer's answer to the last eye hint
    /// (<see cref="RendererEyesFrom"/>: "mesh", "bones", "vision" or "estimate"); null while it is hidden, before it answered or
    /// when it couldn't say.</summary>
    internal string? EyesFrom
    {
        get
        {
            lock (eyesGate) return IsShowing && eyesSent is { } sent && ReferenceEquals(sent.Renderer, renderer) ? eyesFrom : null;
        }
    }

    /// <summary>Raised (off the UI thread) when the showing character answered an eye hint.</summary>
    internal event Action? EyesChanged;

    /// <summary>Where the eye hint for a model comes from: the measurement saved for the model at a path, or null.</summary>
    internal void UseEyes(Func<string?, RendererEyes?> provider) => Volatile.Write(ref eyesFor, provider);

    /// <summary>Whether the showing character has (or is being sent) <paramref name="hint"/>.</summary>
    internal bool HasEyes(RendererEyes? hint)
    {
        lock (eyesGate) return eyesSent is { } sent && ReferenceEquals(sent.Renderer, renderer) && Equals(sent.Hint, hint);
    }

    /// <summary>Gives the showing character the eye hint (null clears it) unless it already has it. Returns what its eyes use
    /// then, or null while it is hidden or when its renderer couldn't say.</summary>
    internal async Task<string?> SendEyesAsync(RendererEyes? hint, CancellationToken token) =>
        renderer is { HasExited: false } current && profile is not null ? await SendEyesAsync(current, hint, token).ConfigureAwait(false) : null;

    // After each model load, and whenever the measurement changes. A renderer that can't take the hint is noted once in the log
    // and never fails the character: its eyes keep the model's own data or the estimate.
    private async Task<string?> SendEyesAsync(IAvatarRenderer target, RendererEyes? hint, CancellationToken token)
    {
        lock (eyesGate)
        {
            if (eyesSent is { } sent && ReferenceEquals(sent.Renderer, target) && Equals(sent.Hint, hint)) return eyesFrom;
            eyesSent = (target, hint);
            eyesFrom = null;
        }
        string? from;
        try
        {
            var reply = await target.SendAsync("eyes", hint ?? new RendererEyes(), token).ConfigureAwait(false);
            from = reply.Kind == "eyes" ? RendererEyesFrom.Read(RendererProtocol.Data<RendererEyesFrom>(reply).EyesFrom) : null;
            ErrorLog.Info(hint is null || hint.Clears
                ? $"The character's eyes have no vision measurement; they use {Describe(from)}."
                : $"The character's eyes got Martlet's vision measurement; they use {Describe(from)}.");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or TimeoutException or
            ObjectDisposedException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (error is OperationCanceledException && token.IsCancellationRequested)
            {
                lock (eyesGate) if (eyesSent is { } sent && ReferenceEquals(sent.Renderer, target)) eyesSent = null;
                throw;
            }
            ErrorLog.Info($"The character's renderer didn't take the eye hint ({error.Message}); its eyes use the model's own data or an estimate.");
            from = null;
        }
        lock (eyesGate)
        {
            if (eyesSent is not { } sent || !ReferenceEquals(sent.Renderer, target) || !Equals(sent.Hint, hint)) return from;
            eyesFrom = from;
        }
        EyesChanged?.Invoke();
        return from;
    }

    private static string Describe(string? from) => from switch
    {
        RendererEyesFrom.Mesh => "the model's own meshes",
        RendererEyesFrom.Bones => "the model's own eye bones and meshes",
        RendererEyesFrom.Vision => "the vision measurement",
        RendererEyesFrom.Estimate => "an estimate from the face",
        null => "what the renderer didn't say",
        _ => from
    };

    // A new renderer has no hint yet.
    private void ForgetEyes()
    {
        lock (eyesGate)
        {
            eyesSent = null;
            eyesFrom = null;
        }
    }
}
