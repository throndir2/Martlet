namespace Martlet.Core.Platforms;

/// <summary>Background work on this PC's processor that must never slow the live conversation (docs/CONVERSATION.md, Live
/// floor): each piece runs on a thread of its own below normal priority, so the reply's own work (speech-to-text, the voice,
/// playback) always comes first when the processor is busy. For short, bounded pieces of work (a picture to encode, a clip to
/// tag); the thread ends with the work.</summary>
public static class LowPriority
{
    /// <summary>Runs <paramref name="work"/> on its own below-normal thread and returns its result. Canceling
    /// <paramref name="token"/> before it starts skips it; once started it runs to the end (it should be short).</summary>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken token = default, string name = "Martlet background work")
    {
        ArgumentNullException.ThrowIfNull(work);
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                done.TrySetResult(work());
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { done.TrySetCanceled(token); }
            catch (Exception error) { done.TrySetException(error); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = name };
        thread.Start();
        return done.Task;
    }
}
