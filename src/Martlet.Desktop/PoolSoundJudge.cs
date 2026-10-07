using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The sound digest's judge in the Thinking pool: a Digest job with the clip as a 16 kHz mono WAV and
/// <see cref="SoundDigest.Prompt"/>, on a member whose model hears (never the conversation's own route). Its answer is one line
/// or "none". A job no member takes in time is dropped; a reply never waits for it.</summary>
internal sealed class PoolSoundJudge(ThinkingPool pool, BackgroundPlace member) : ISoundJudge
{
    internal const ThinkingCapability Needs = ThinkingCapability.Text | ThinkingCapability.Audio;

    /// <summary>A judge for the member a sound job would go to now, or null when no pool member hears.</summary>
    internal static PoolSoundJudge? For(ThinkingPool? pool) =>
        pool?.Find(ThinkingJobKind.Digest, Needs) is { } member ? new PoolSoundJudge(pool, member) : null;

    public string Name => member.Model is { Length: > 0 } model ? $"{model} on {member.Name}" : member.Name;
    public SoundJudgeKind Kind => SoundJudgeKind.Pool;

    public async Task<string?> DescribeAsync(float[] clip, CancellationToken cancellationToken)
    {
        var wave = SoundDigest.Wave(clip);
        try
        {
            var result = await pool.RunAsync(new ThinkingJob
            {
                Kind = ThinkingJobKind.Digest,
                Instructions = "You describe the sound of short audio clips in one line.",
                Text = SoundDigest.Prompt,
                Audio = BoundedWaveAudio.FromWave(wave),
                Needs = Needs,
                Timeout = TimeSpan.FromSeconds(12),
                DropWhenStale = true,
                MaxOutputTokens = 96,
                Reasoning = false
            }, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.Text : null;
        }
        finally { Array.Clear(wave); }
    }
}
