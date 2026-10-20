namespace Martlet.Core.Speakers;

/// <summary>A voice recognition engine on this PC (sherpa-onnx in the desktop), as <see cref="VoiceEngineKeeper{T}"/> keeps it.</summary>
public interface IVoiceEngine<out T> : IDisposable
{
    /// <summary>Who spoke in <paramref name="samples"/> (16 kHz mono). Throws when the engine can't.</summary>
    T Analyze(float[] samples);

    /// <summary>Loads the models (the keeper's check that the engine works again).</summary>
    void Load();
}

public enum VoiceEngineState { NotLoaded, Ready, Failed }

/// <summary>How this PC's engine is doing: <see cref="Problem"/> in plain words while it is <see cref="VoiceEngineState.Failed"/>,
/// how many times in a row it failed, when it tries again (<see cref="RetryAt"/>), how many times it was loaded again after a
/// failure and how long it takes for a second of speech (<see cref="MillisecondsPerSecond"/>, null until measured).</summary>
public sealed record VoiceEngineStatus(VoiceEngineState State, string? Problem, int Failures, DateTimeOffset? FailedAt,
    DateTimeOffset? RetryAt, DateTimeOffset? WorkedAt, int Reloads, double? MillisecondsPerSecond)
{
    public static VoiceEngineStatus Initial { get; } = new(VoiceEngineState.NotLoaded, null, 0, null, null, null, 0, null);
}

/// <summary>This PC's engine failed now, or rests after failures until it is checked again. <see cref="Exception.Message"/> is
/// in plain words.</summary>
public sealed class VoiceEngineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Keeps voice recognition on this PC working (docs/VOICES.md#keeping-it-running). The engine loads on first use, as before. When
/// an analysis fails, the engine is dropped and a new one loaded at once for one more try. When that fails too, the engine rests:
/// utterances skip it (the Voice recognition list's computers can still take them) while a background check loads a new one after
/// <see cref="Backoff"/> (5 seconds, then 30 seconds, 2 minutes and every 5 minutes) until it works again. <see cref="Changed"/>
/// and <see cref="Log"/> say so each time. Too much audio (<see cref="ArgumentException"/>) is the caller's mistake, not a failure.
/// Thread-safe; an analysis runs outside the gate (the engine serializes its own calls), and no handler runs under it.
/// </summary>
public sealed class VoiceEngineKeeper<T> : IDisposable
{
    public static IReadOnlyList<TimeSpan> Backoff { get; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    private const int SampleRate = 16_000;
    private readonly Func<IVoiceEngine<T>> load;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private IVoiceEngine<T>? engine;
    private bool disposed, paused;
    private VoiceEngineStatus status = VoiceEngineStatus.Initial;
    private CancellationTokenSource? retry;

    /// <param name="load">Makes a new engine (it loads its models on first use).</param>
    public VoiceEngineKeeper(Func<IVoiceEngine<T>> load, TimeProvider? clock = null)
    {
        this.load = load ?? throw new ArgumentNullException(nameof(load));
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised (off the caller's thread too) when the state changed: failed, works again, loaded or unloaded.</summary>
    public event Action? Changed;

    /// <summary>Log lines in plain words (a failure, a reload, working again), with the failure when there is one.</summary>
    public event Action<string, Exception?>? Log;

    public VoiceEngineStatus Status { get { lock (gate) return status; } }

    /// <summary>The engine is in memory now.</summary>
    public bool Loaded { get { lock (gate) return engine is not null; } }

    /// <summary>Who spoke in <paramref name="samples"/> on this PC. Throws <see cref="VoiceEngineException"/> while the engine
    /// rests after failures, or when it failed now (after one reload).</summary>
    public T Analyze(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var reloaded = false;
        for (var attempt = 0; ; attempt++)
        {
            IVoiceEngine<T>? current;
            var first = false;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!reloaded && status is { State: VoiceEngineState.Failed, RetryAt: { } at } && clock.GetUtcNow() < at)
                    throw new VoiceEngineException(status.Problem + $" Martlet tries it again at {at.ToLocalTime():t}.");
                current = engine;
            }
            if (current is null)
            {
                IVoiceEngine<T>? made;
                try { made = load(); }
                catch (Exception error) when (error is not OutOfMemoryException) { throw Failed(error); }
                lock (gate)
                {
                    if (engine is null)
                    {
                        engine = made;
                        made = null;
                        first = true;
                    }
                    current = engine;
                }
                made?.Dispose();
            }
            var started = clock.GetTimestamp();
            try
            {
                var result = current.Analyze(samples);
                Worked(samples.Length, first ? null : clock.GetElapsedTime(started));
                return result;
            }
            catch (ArgumentException) { throw; }
            // Free up this PC (or a failure on another thread) took this engine away while it waited: a new one analyzes.
            catch (ObjectDisposedException) when (attempt < 2) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Drop(current);
                if (!reloaded && Status.Failures == 0)
                {
                    reloaded = true;
                    lock (gate) status = status with { Reloads = status.Reloads + 1 };
                    Log?.Invoke("Voice recognition on this PC failed; Martlet loads its engine again and tries once more.", error);
                    continue;
                }
                throw Failed(error);
            }
        }
    }

    /// <summary>Loads a new engine and its models now (Home's Try again, and the background check). True when it works.</summary>
    public bool Check()
    {
        IVoiceEngine<T>? next = null;
        try
        {
            next = load();
            next.Load();
            IVoiceEngine<T>? old;
            bool was;
            lock (gate)
            {
                if (disposed) return false;
                old = engine;
                engine = next;
                was = status.State == VoiceEngineState.Failed;
                status = status with
                {
                    State = VoiceEngineState.Ready, Problem = null, Failures = 0, RetryAt = null, WorkedAt = clock.GetUtcNow(),
                    Reloads = status.Reloads + (was ? 1 : 0)
                };
                retry?.Cancel();
                retry = null;
            }
            next = null;
            old?.Dispose();
            if (was) Log?.Invoke("Voice recognition on this PC works again.", null);
            Changed?.Invoke();
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (gate)
                if (disposed) return false;
            Failed(error);
            return false;
        }
        finally { next?.Dispose(); }
    }

    /// <summary>Stops the background check while recognition is off; <see cref="Resume"/> starts it again (at once when the
    /// engine had failed).</summary>
    public void Pause()
    {
        lock (gate)
        {
            paused = true;
            retry?.Cancel();
            retry = null;
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (!paused || disposed) return;
            paused = false;
            if (status.State == VoiceEngineState.Failed) Schedule(TimeSpan.Zero);
        }
    }

    /// <summary>Home's Free up this PC: takes the engine out of memory; the next utterance loads it again. True when it was loaded.</summary>
    public bool Unload()
    {
        IVoiceEngine<T>? loaded;
        lock (gate)
        {
            loaded = engine;
            engine = null;
            if (loaded is not null && status.State == VoiceEngineState.Ready) status = status with { State = VoiceEngineState.NotLoaded };
        }
        // Unloading waits for an analysis still running, so it happens outside the gate.
        loaded?.Dispose();
        if (loaded is not null) Changed?.Invoke();
        return loaded is not null;
    }

    private void Worked(int samples, TimeSpan? took)
    {
        bool changed;
        lock (gate)
        {
            changed = status.State != VoiceEngineState.Ready;
            var rate = status.MillisecondsPerSecond;
            // Only utterances of a second or more, and not the one that also loaded the models.
            if (took is { } time && samples >= SampleRate)
            {
                var measured = time.TotalMilliseconds / (samples / (double)SampleRate);
                rate = rate is { } before ? before * 0.7 + measured * 0.3 : measured;
            }
            status = status with
            {
                State = VoiceEngineState.Ready, Problem = null, Failures = 0, RetryAt = null, WorkedAt = clock.GetUtcNow(),
                MillisecondsPerSecond = rate
            };
            if (changed)
            {
                retry?.Cancel();
                retry = null;
            }
        }
        if (changed) Changed?.Invoke();
    }

    // Records a failure, schedules the next check, logs it and says so; the exception the caller throws.
    private VoiceEngineException Failed(Exception error)
    {
        var problem = Problem(error);
        TimeSpan wait;
        int failures;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            failures = status.Failures + 1;
            wait = Backoff[Math.Min(failures, Backoff.Count) - 1];
            status = status with { State = VoiceEngineState.Failed, Problem = problem, Failures = failures, FailedAt = now, RetryAt = now + wait };
            if (!paused && !disposed) Schedule(wait);
        }
        Log?.Invoke($"Voice recognition on this PC failed ({failures} in a row): {problem} Martlet tries it again in {Words(wait)}.", error);
        Changed?.Invoke();
        return new VoiceEngineException(problem, error);
    }

    // Called under the gate. The wait starts now, so a test clock sees it at once.
    private void Schedule(TimeSpan wait)
    {
        retry?.Cancel();
        var stop = retry = new CancellationTokenSource();
        Task.Delay(wait, clock, stop.Token).ContinueWith(_ =>
        {
            if (!stop.IsCancellationRequested) Check();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void Drop(IVoiceEngine<T> failed)
    {
        lock (gate)
            if (ReferenceEquals(engine, failed)) engine = null;
        try { failed.Dispose(); }
        catch (Exception error) when (error is not OutOfMemoryException) { Log?.Invoke("Closing the failed voice recognition engine failed.", error); }
    }

    /// <summary>A failure in plain words.</summary>
    public static string Problem(Exception error) => error switch
    {
        VoiceEngineException engine => engine.Message,
        DllNotFoundException or BadImageFormatException or EntryPointNotFoundException =>
            "The voice recognition runtime couldn't be loaded; reinstall Martlet if this keeps happening.",
        _ when error.GetType().Name == "SherpaException" => error.Message,
        _ => $"The voice recognition engine stopped working ({error.GetType().Name}: {error.Message})."
    };

    private static string Words(TimeSpan wait) => wait.TotalMinutes >= 1
        ? $"{wait.TotalMinutes:0} minute{(wait.TotalMinutes >= 2 ? "s" : "")}"
        : $"{wait.TotalSeconds:0} seconds";

    public void Dispose()
    {
        IVoiceEngine<T>? loaded;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            retry?.Cancel();
            retry = null;
            loaded = engine;
            engine = null;
        }
        loaded?.Dispose();
    }
}
