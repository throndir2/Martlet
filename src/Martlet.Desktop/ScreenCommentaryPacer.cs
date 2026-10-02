namespace Martlet.Desktop;

internal enum Chattiness { Quiet, Normal, Chatty }

internal enum PacerVerdict { Look, Busy, WarmingUp, AfterConversation, AfterComment, AfterLook, HourlyLimit, UserAway, NothingNew, BackingOff }

/// <summary>Decides when Martlet takes a look at the screen, the way a friend in the room would: not while you are
/// talking to it, not right after it said something, more likely when the picture just changed, rarely when nothing
/// moves, never to an empty room, and never more than an hourly budget of looks (each look is one model request).
/// Whether a look turns into a remark is then the model's call: it answers [pass] when nothing is worth saying.</summary>
internal sealed class ScreenCommentaryPacer
{
    internal sealed record Tuning(TimeSpan AfterComment, TimeSpan AfterLook, TimeSpan AfterConversation, int LooksPerHour,
        double BaseChance, TimeSpan Boredom);

    internal static TimeSpan Warmup => TimeSpan.FromSeconds(8);
    internal static TimeSpan AwayAfter => TimeSpan.FromMinutes(5);
    internal static TimeSpan Tick => TimeSpan.FromSeconds(3);

    internal static Tuning For(Chattiness chattiness) => chattiness switch
    {
        Chattiness.Quiet => new(TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30), 12, 0.02, TimeSpan.FromMinutes(8)),
        Chattiness.Chatty => new(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(12), 45, 0.10, TimeSpan.FromMinutes(2)),
        _ => new(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(20), 24, 0.04, TimeSpan.FromMinutes(4))
    };

    private readonly TimeProvider clock;
    private readonly Func<double> random;
    private readonly Queue<long> looks = new();
    private readonly long started;
    private long? lastLook, lastComment, lastConversation;
    private TimeSpan jitter;
    private double novelty;
    private int backoffs;
    private (long At, TimeSpan Wait)? backoffUntil;

    internal ScreenCommentaryPacer(Chattiness chattiness, TimeProvider? clock = null, Func<double>? random = null)
    {
        Chattiness = chattiness;
        Settings = For(chattiness);
        this.clock = clock ?? TimeProvider.System;
        this.random = random ?? Random.Shared.NextDouble;
        started = this.clock.GetTimestamp();
    }

    internal Chattiness Chattiness { get; }
    internal Tuning Settings { get; }
    internal double Novelty => novelty;
    internal int LooksThisHour { get { Evict(); return looks.Count; } }
    internal TimeSpan? SinceLastLook => lastLook is { } at ? clock.GetElapsedTime(at) : null;

    /// <summary>Feeds the change score of the newest capture (0 = identical, 1 = completely different).</summary>
    internal void ObserveFrame(double change) =>
        novelty = Math.Clamp(novelty * 0.8 + Math.Clamp(change, 0, 1) * 2.5, 0, 1);

    /// <summary>The user and Martlet are talking (speech heard, a typed or spoken turn, a reply playing).</summary>
    internal void NoteConversation() => lastConversation = clock.GetTimestamp();

    internal void NoteLook(bool commented)
    {
        var now = clock.GetTimestamp();
        lastLook = now;
        looks.Enqueue(now);
        novelty = 0;
        backoffs = 0;
        backoffUntil = null;
        if (!commented) return;
        lastComment = now;
        jitter = Settings.AfterComment * (random() * 0.5);
    }

    /// <summary>The provider limited the look's request or didn't answer it (the Thinking fallback too, when there is one).
    /// Looking waits a minute, then two, four and up to ten in a row, instead of stopping vision; a look that gets an answer
    /// resets it. Returns the wait.</summary>
    internal TimeSpan NoteBackoff()
    {
        var now = clock.GetTimestamp();
        lastLook = now;
        looks.Enqueue(now);
        var wait = TimeSpan.FromMinutes(Math.Min(10, Math.Pow(2, Math.Min(backoffs, 4))));
        backoffs++;
        backoffUntil = (now, wait);
        return wait;
    }

    internal TimeSpan? BackoffLeft => backoffUntil is { } until && clock.GetElapsedTime(until.At) < until.Wait
        ? until.Wait - clock.GetElapsedTime(until.At) : null;

    internal PacerVerdict Decide(bool busy, TimeSpan userIdle)
    {
        if (busy) return PacerVerdict.Busy;
        if (BackoffLeft is not null) return PacerVerdict.BackingOff;
        if (clock.GetElapsedTime(started) < Warmup) return PacerVerdict.WarmingUp;
        if (lastConversation is { } talked && clock.GetElapsedTime(talked) < Settings.AfterConversation)
            return PacerVerdict.AfterConversation;
        if (lastComment is { } said && clock.GetElapsedTime(said) < Settings.AfterComment + jitter)
            return PacerVerdict.AfterComment;
        if (lastLook is { } looked && clock.GetElapsedTime(looked) < Settings.AfterLook) return PacerVerdict.AfterLook;
        if (LooksThisHour >= Settings.LooksPerHour) return PacerVerdict.HourlyLimit;
        if (userIdle >= AwayAfter && novelty < 0.05) return PacerVerdict.UserAway;
        var chance = Settings.BaseChance + 0.5 * novelty;
        if (lastLook is null) chance += 0.5;
        else if (clock.GetElapsedTime(lastLook.Value) >= Settings.Boredom) chance += 0.25;
        return random() < Math.Min(chance, 0.95) ? PacerVerdict.Look : PacerVerdict.NothingNew;
    }

    private void Evict()
    {
        while (looks.TryPeek(out var at) && clock.GetElapsedTime(at) >= TimeSpan.FromHours(1)) looks.Dequeue();
    }
}
