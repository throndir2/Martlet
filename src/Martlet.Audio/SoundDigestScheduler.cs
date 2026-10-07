namespace Martlet.Audio;

/// <summary>One line the sound digest made: the text, who judged (never what was heard beyond the line), the clock timestamp of
/// the end of the clip it describes and how long judging took.</summary>
public sealed record SoundDigestLine(string Text, string Judge, SoundJudgeKind Kind, long At, TimeSpan Took);

/// <summary>What one tick of the sound digest did.</summary>
public enum SoundDigestStep
{
    /// <summary>Off, or nothing to do yet.</summary>
    Idle,
    /// <summary>The PC isn't being heard (no fresh sound in the buffer).</summary>
    NoSound,
    /// <summary>Martlet itself may be in what the PC plays (it speaks or sings and Windows can't leave it out).</summary>
    MartletSpeaking,
    /// <summary>A judge is still working on the last clip.</summary>
    Busy,
    /// <summary>A judge took too long; its clip was dropped.</summary>
    Dropped,
    /// <summary>Less than the shortest clip since the PC was heard (or Martlet spoke).</summary>
    TooShort,
    /// <summary>The clip was mostly silence.</summary>
    Silent,
    /// <summary>Neither an audio pool model nor the sound tagger can judge.</summary>
    NoJudge,
    /// <summary>A clip went to the judge.</summary>
    Started
}

/// <summary>How often the sound digest runs and on what.</summary>
public sealed record SoundDigestOptions
{
    /// <summary>How often the timer ticks while the digest is on (<see cref="Timeout.InfiniteTimeSpan"/>: never; the caller
    /// calls <see cref="SoundDigestScheduler.Tick"/> itself).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>The time between two clips sent to the judge while something plays.</summary>
    public TimeSpan Every { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The longest clip (the newest seconds of the buffer).</summary>
    public TimeSpan Clip { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The shortest clip worth judging.</summary>
    public TimeSpan MinimumClip { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>A judge that takes longer is canceled and its clip dropped: by then the line would be stale.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Sound older than this means the PC isn't being heard now.</summary>
    public TimeSpan Fresh { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>After Martlet was last audible in what the PC plays, its voice may still echo for this long.</summary>
    public TimeSpan Tail { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>The share of 100 ms windows that must be louder than silence (<see cref="SoundDigest.ActiveShare"/>).</summary>
    public double MinimumActive { get; init; } = 0.3;
    /// <summary>How soon to look again after a silent clip.</summary>
    public TimeSpan SilentRetry { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>How long a line stays useful for a reply (the context board's maximum age).</summary>
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromSeconds(45);
}

/// <summary>A snapshot of the sound digest: on or off, the judge it would use now, the last line and when it was made, how long
/// judging took, and how many clips were judged or skipped (and why the last was skipped). In memory only.</summary>
public sealed record SoundDigestStatus(bool On, string? Judge, SoundJudgeKind? JudgeKind, SoundDigestLine? Last, int Runs,
    int Lines, int Dropped, int Skipped, SoundDigestStep LastStep, TimeSpan? LastTook);

/// <summary>Runs the sound digest on a cadence while it is <see cref="On"/>: about every <see cref="SoundDigestOptions.Every"/>
/// it takes the newest clip from the buffer and gives it to the judge, but only when something plays (silence is skipped), when
/// none of the clip may hold Martlet's own voice, and when no judge is still busy (one clip at a time; a judge past the
/// deadline is canceled and its clip dropped). Each line goes to <paramref name="post"/>; nothing ever waits for it. Each clip
/// is cleared once judged.</summary>
public sealed class SoundDigestScheduler : IDisposable
{
    private readonly object gate = new();
    private readonly PcSoundBuffer buffer;
    private readonly Func<ISoundJudge?> judge;
    private readonly Func<bool> martletAudible;
    private readonly Action<SoundDigestLine> post;
    private readonly TimeProvider clock;
    private readonly SoundDigestOptions options;
    private ITimer? timer;
    private bool on, disposed;
    private long nextDue, lastAudible, jobStarted;
    private CancellationTokenSource? job;
    private Task running = Task.CompletedTask;
    private SoundDigestLine? last;
    private int runs, lines, dropped, skipped;
    private SoundDigestStep lastStep;
    private TimeSpan? lastTook;

    public SoundDigestScheduler(PcSoundBuffer buffer, Func<ISoundJudge?> judge, Func<bool> martletAudible,
        Action<SoundDigestLine> post, SoundDigestOptions? options = null)
    {
        this.buffer = buffer;
        this.judge = judge;
        this.martletAudible = martletAudible;
        this.post = post;
        clock = buffer.Clock;
        this.options = options ?? new();
    }

    public SoundDigestOptions Options => options;

    /// <summary>Raised (off the caller's thread) when the status changed: a line was made or dropped, or the digest went on or off.</summary>
    public event Action? Changed;

    /// <summary>Whether the digest runs: the buffer records and a timer ticks each second. Off clears the buffer and cancels a
    /// judge at work.</summary>
    public bool On
    {
        get { lock (gate) return on; }
        set
        {
            CancellationTokenSource? cancel = null;
            lock (gate)
            {
                if (disposed || on == value) return;
                on = value;
                buffer.Recording = value;
                if (value)
                {
                    nextDue = 0;
                    if (options.Interval != Timeout.InfiniteTimeSpan)
                        timer = clock.CreateTimer(_ => Tick(), null, options.Interval, options.Interval);
                }
                else
                {
                    timer?.Dispose();
                    timer = null;
                    cancel = job;
                    job = null;
                }
            }
            cancel?.Cancel();
            Changed?.Invoke();
        }
    }

    /// <summary>The judge a clip would go to now (an audio pool model first, else the sound tagger), or null.</summary>
    public ISoundJudge? Judge => judge();

    /// <summary>Completes once the judge at work (if any) is done.</summary>
    public Task Idle { get { lock (gate) return running; } }

    public SoundDigestStatus Status
    {
        get
        {
            var current = judge();
            lock (gate) return new(on, current?.Name, current?.Kind, last, runs, lines, dropped, skipped, lastStep, lastTook);
        }
    }

    /// <summary>The last line while it is younger than <see cref="SoundDigestOptions.MaximumAge"/>, else null.</summary>
    public SoundDigestLine? Fresh
    {
        get
        {
            lock (gate)
                return last is { } line && clock.GetElapsedTime(line.At) <= options.MaximumAge ? line : null;
        }
    }

    /// <summary>One step of the cadence (the timer calls it each second; tests call it directly).</summary>
    public SoundDigestStep Tick()
    {
        var step = Step(out var clip, out var chosen, out var token, out var started);
        lock (gate)
        {
            lastStep = step;
            if (step is not (SoundDigestStep.Started or SoundDigestStep.Busy or SoundDigestStep.Idle or SoundDigestStep.NoSound)) skipped++;
        }
        if (step == SoundDigestStep.Dropped) Changed?.Invoke();
        if (step != SoundDigestStep.Started) return step;
        var work = Task.Run(() => JudgeAsync(chosen!, clip!, token, started));
        lock (gate) running = work;
        return step;
    }

    private SoundDigestStep Step(out float[]? clip, out ISoundJudge? chosen, out CancellationToken token, out long started)
    {
        clip = null;
        chosen = null;
        token = default;
        started = 0;
        CancellationTokenSource? stale = null;
        try
        {
            lock (gate)
            {
                if (!on || disposed) return SoundDigestStep.Idle;
                var now = clock.GetTimestamp();
                if (martletAudible())
                {
                    lastAudible = now;
                    return SoundDigestStep.MartletSpeaking;
                }
                if (job is not null)
                {
                    if (clock.GetElapsedTime(jobStarted, now) < options.Deadline) return SoundDigestStep.Busy;
                    stale = job;
                    job = null;
                    dropped++;
                    return SoundDigestStep.Dropped;
                }
                if (now < nextDue) return SoundDigestStep.Idle;
                var appended = buffer.LastAppendedAt;
                if (appended == 0 || clock.GetElapsedTime(appended, now) > options.Fresh) return SoundDigestStep.NoSound;
                var notBefore = lastAudible == 0 ? 0 : lastAudible + (long)(options.Tail.TotalSeconds * clock.TimestampFrequency);
                var taken = buffer.Latest(options.Clip, notBefore);
                if (taken.Length < options.MinimumClip.TotalSeconds * PcSoundBuffer.SampleRate)
                {
                    Array.Clear(taken);
                    return SoundDigestStep.TooShort;
                }
                if (SoundDigest.ActiveShare(taken) < options.MinimumActive)
                {
                    Array.Clear(taken);
                    nextDue = now + (long)(options.SilentRetry.TotalSeconds * clock.TimestampFrequency);
                    return SoundDigestStep.Silent;
                }
                var current = judge();
                if (current is null)
                {
                    Array.Clear(taken);
                    nextDue = now + (long)(options.Every.TotalSeconds * clock.TimestampFrequency);
                    return SoundDigestStep.NoJudge;
                }
                nextDue = now + (long)(options.Every.TotalSeconds * clock.TimestampFrequency);
                job = new CancellationTokenSource();
                jobStarted = started = now;
                runs++;
                clip = taken;
                chosen = current;
                token = job.Token;
                return SoundDigestStep.Started;
            }
        }
        finally { stale?.Cancel(); }
    }

    private async Task JudgeAsync(ISoundJudge chosen, float[] clip, CancellationToken token, long started)
    {
        string? text = null;
        try { text = SoundDigest.Clean(await chosen.DescribeAsync(clip, token).ConfigureAwait(false)); }
        // A judge that fails or is canceled makes no line; the next clip tries again.
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally { Array.Clear(clip); }
        SoundDigestLine? made = null;
        lock (gate)
        {
            var took = clock.GetElapsedTime(started);
            // Only the job still current may post: one past its deadline or canceled (turned off) was dropped already.
            if (job is not { } current || current.Token != token) return;
            job = null;
            current.Dispose();
            lastTook = took;
            if (text is not null && !token.IsCancellationRequested && took <= options.Deadline)
            {
                made = last = new(text, chosen.Name, chosen.Kind, started, took);
                lines++;
            }
        }
        if (made is not null)
        {
            try { post(made); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        On = false;
        lock (gate) disposed = true;
    }
}
