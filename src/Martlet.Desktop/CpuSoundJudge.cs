using Martlet.Audio;
using Martlet.Sherpa;

#if MARTLET_MCP
namespace Martlet.Mcp;
#else
namespace Martlet.Desktop;
#endif

/// <summary>The sound digest's judge on this PC's processor (when the Thinking pool has no model that hears): the bundled sound
/// tagger (<see cref="SoundTagger"/>) names what it hears and <see cref="SoundDigest.Line"/> turns the labels into one line. It
/// tags on one thread below normal priority, so a reply never waits for it. The clip stays in memory.</summary>
internal sealed class CpuSoundJudge(SoundTagger tagger) : ISoundJudge, IDisposable
{
    internal const string Label = "CPU sound tagger";
    public string Name => Label;
    public SoundJudgeKind Kind => SoundJudgeKind.Cpu;

    /// <summary>The tagger's labels for <paramref name="clip"/>, highest score first.</summary>
    internal Task<IReadOnlyList<SoundEvent>> TagAsync(float[] clip, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<IReadOnlyList<SoundEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                done.TrySetResult(tagger.Tag(clip, 15));
            }
            catch (OperationCanceledException) { done.TrySetCanceled(cancellationToken); }
            catch (Exception error) { done.TrySetException(error); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Martlet sound tagger" };
        thread.Start();
        return done.Task.WaitAsync(cancellationToken);
    }

    public async Task<string?> DescribeAsync(float[] clip, CancellationToken cancellationToken)
    {
        var tags = await TagAsync(clip, cancellationToken).ConfigureAwait(false);
        return SoundDigest.Line(tags.Select(t => new SoundTag(t.Name, t.Probability)));
    }

    public void Dispose() => tagger.Dispose();
}
