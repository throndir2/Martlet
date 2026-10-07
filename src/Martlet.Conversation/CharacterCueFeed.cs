using System.Threading.Channels;

namespace Martlet.Conversation;

/// <summary>One tag in a reply that can make the desktop character act: a character tag such as <c>{blush}</c> (removed from
/// the words) or the speaking engine's own voice tag such as <c>[laugh]</c> (which the voice also performs), written as the
/// catalog spells it, with how long after its sentence starts playing it falls.</summary>
public sealed record CharacterCue(string Tag, TimeSpan Delay);

/// <summary>The cues of one sentence as it starts playing (or, for a reply that isn't spoken, as its text arrives);
/// <see cref="Finished"/> completes when its playback ends. <see cref="ReachedAsync"/> waits for a cue's moment in the reply.</summary>
public sealed class CharacterCueLine
{
    private readonly CharacterCueClock clock;
    // Where on the reply's clock the sentence started playing.
    private readonly TimeSpan start;

    internal CharacterCueLine(IReadOnlyList<CharacterCue> cues, Task finished, CharacterCueClock clock)
    {
        Cues = cues;
        Finished = finished;
        this.clock = clock;
        start = clock.Played;
    }

    public IReadOnlyList<CharacterCue> Cues { get; }
    public Task Finished { get; }

    /// <summary>Waits until the reply has played <paramref name="cue"/>'s <see cref="CharacterCue.Delay"/> into the sentence.
    /// A pause holds the cue, so it keeps its place in the speech. Returns false, at once, when the reply was stopped or
    /// replaced first: then the character doesn't act the cue.</summary>
    public Task<bool> ReachedAsync(CharacterCue cue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cue);
        return clock.ReachedAsync(start + cue.Delay, cancellationToken);
    }
}

/// <summary>How long a reply has played, which the reply's character cues are timed by (one clock for each reply). It stands
/// still while the reply is paused (<see cref="ConversationTurn.Pause"/>) and stops for good when the reply is stopped,
/// replaced or fails, so a cue still waiting then is dropped. Muting the voice doesn't stop it: the reply goes on in the
/// captions. It never runs a waiting cue's code on the thread that pauses, resumes or stops it.</summary>
internal sealed class CharacterCueClock(TimeProvider time)
{
    private readonly object gate = new();
    private readonly long started = time.GetTimestamp();
    private long pausedAt;
    private TimeSpan pausedFor;
    private bool paused, stopped;
    // Completes, and is replaced, at each pause, resume and stop, so the waiting cues look again.
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The time since the clock started, less the time the reply was paused.</summary>
    internal TimeSpan Played { get { lock (gate) return PlayedAt(time.GetTimestamp()); } }

    internal void Pause()
    {
        lock (gate)
        {
            if (paused || stopped) return;
            paused = true;
            pausedAt = time.GetTimestamp();
            Changed();
        }
    }

    internal void Resume()
    {
        lock (gate)
        {
            if (!paused) return;
            paused = false;
            pausedFor += time.GetElapsedTime(pausedAt);
            Changed();
        }
    }

    internal void Stop()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            Changed();
        }
    }

    /// <summary>Waits until <see cref="Played"/> reaches <paramref name="at"/>, however long the reply is paused meanwhile;
    /// false, at once, when the reply is stopped first.</summary>
    internal async Task<bool> ReachedAsync(TimeSpan at, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task change;
            TimeSpan left;
            lock (gate)
            {
                if (stopped) return false;
                left = paused ? Timeout.InfiniteTimeSpan : at - PlayedAt(time.GetTimestamp());
                if (left != Timeout.InfiniteTimeSpan && left <= TimeSpan.Zero) return true;
                change = changed.Task;
            }
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(change, Task.Delay(left, time, wake.Token)).ConfigureAwait(false);
            wake.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private TimeSpan PlayedAt(long now) =>
        time.GetElapsedTime(started, now) - pausedFor - (paused ? time.GetElapsedTime(pausedAt, now) : TimeSpan.Zero);

    private void Changed()
    {
        var was = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        was.TrySetResult();
    }
}

/// <summary>A nonblocking tee of the character cues in replies, for the desktop character. Never invokes consumer code on the
/// producer.</summary>
public sealed class CharacterCueFeed
{
    private readonly Channel<CharacterCueLine> lines = Channel.CreateBounded<CharacterCueLine>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    public ChannelReader<CharacterCueLine> Lines => lines.Reader;

    /// <summary>Posts cues timed from now that nothing pauses or stops.</summary>
    public void Post(IReadOnlyList<CharacterCue> cues, Task finished) => Post(cues, finished, new CharacterCueClock(TimeProvider.System));

    /// <summary>Posts the cues of a sentence that starts playing now, timed by its reply's <paramref name="clock"/>.</summary>
    internal void Post(IReadOnlyList<CharacterCue> cues, Task finished, CharacterCueClock clock)
    {
        if (cues.Count > 0) lines.Writer.TryWrite(new(cues, finished, clock));
    }
}
