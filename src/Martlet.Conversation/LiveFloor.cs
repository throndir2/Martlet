using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>How much the live conversation needs the hardware now (docs/CONVERSATION.md, Live floor). <see cref="Idle"/>: nobody
/// talks with Martlet, so background work runs everywhere. <see cref="Listening"/>: the microphone hears the user's voice, but it
/// isn't known to be words yet, so no new background work starts on the hardware the conversation uses. <see cref="Live"/>: the
/// user said real words or a reply is under way, so background work on that hardware stops and goes on later.</summary>
public enum LiveFloorLevel { Idle, Listening, Live }

/// <summary>One change of the live floor: when, from and to which level, and why in a few words (never what was said).</summary>
public sealed record LiveFloorChange(DateTimeOffset At, LiveFloorLevel From, LiveFloorLevel To, string Why);

/// <summary>How long each level of the live floor lasts on its own.</summary>
public sealed record LiveFloorTiming
{
    /// <summary>Listening ends this long after the user's voice was last heard, when no real words came.</summary>
    public TimeSpan ListeningHold { get; init; } = TimeSpan.FromSeconds(6);
    /// <summary>Live lasts this long after real words when no reply starts (the user talks on, or Martlet doesn't answer).</summary>
    public TimeSpan WordsHold { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>Live lasts this long after the last reply's voice was made, or it stopped, for a fast answer to it.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate() => ContractRules.Require(ListeningHold > TimeSpan.Zero && ListeningHold <= TimeSpan.FromMinutes(1) &&
        WordsHold > TimeSpan.Zero && WordsHold <= TimeSpan.FromMinutes(1) && Grace >= TimeSpan.Zero && Grace <= TimeSpan.FromMinutes(1),
        "The live floor's holds are at most a minute each.");
}

/// <summary>The live floor: whether the conversation needs its hardware now (<see cref="LiveFloorLevel"/>), fed by what the
/// microphone hears and by the replies. <see cref="Heard"/> (the user's voice, never Martlet's own or what this PC plays) makes it
/// Listening for <see cref="LiveFloorTiming.ListeningHold"/>; <see cref="NotWords"/> (the speech was only a sound or filler) ends
/// that at once. <see cref="Words"/> (real words, or Martlet's name) makes it Live for <see cref="LiveFloorTiming.WordsHold"/>, and
/// a reply (<see cref="BeginReply"/>) keeps it Live until the reply's voice is made or the reply stops, plus
/// <see cref="LiveFloorTiming.Grace"/>. The Thinking pool's job board follows it (<see cref="LiveFloorRules"/>). Cheap enough to
/// call for every 20 ms frame; thread-safe. <see cref="Changed"/> is raised in order, off the caller's locks, never twice at
/// once.</summary>
public sealed class LiveFloor : IDisposable
{
    /// <summary>How many changes <see cref="Recent"/> keeps.</summary>
    public const int Kept = 16;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Queue<LiveFloorChange> recent = new();
    private readonly Queue<LiveFloorChange> pending = new();
    private readonly HashSet<LiveFloorReply> replies = [];
    private ITimer? timer;
    private long heardUntil, wordsUntil, graceUntil, due;
    private int level, pumping;
    private long periods;
    private bool disposed;

    public LiveFloor(TimeProvider? clock = null, LiveFloorTiming? timing = null)
    {
        this.clock = clock ?? TimeProvider.System;
        Timing = timing ?? new();
        Timing.Validate();
    }

    public TimeProvider Clock => clock;
    public LiveFloorTiming Timing { get; }

    /// <summary>The level now. Lock-free, so any lock may read it.</summary>
    public LiveFloorLevel Level => (LiveFloorLevel)Volatile.Read(ref level);

    /// <summary>How many replies hold the floor now.</summary>
    public int Replies { get { lock (gate) return replies.Count; } }

    /// <summary>How many times the floor went Live; each Live period has its number (the first is 1).</summary>
    public long Periods => Interlocked.Read(ref periods);

    /// <summary>The last <see cref="Kept"/> changes, oldest first.</summary>
    public IReadOnlyList<LiveFloorChange> Recent { get { lock (gate) return [.. recent]; } }

    /// <summary>Raised for each change, in order, on the thread that caused it or the floor's timer.</summary>
    public event Action<LiveFloorChange>? Changed;

    /// <summary>The microphone heard the user's voice now (<see cref="Voice"/>: speech the speakers don't explain; never Martlet's
    /// own voice or what this PC plays): Listening, unless it is Live already.</summary>
    public void Heard()
    {
        lock (gate)
        {
            if (disposed) return;
            var now = clock.GetTimestamp();
            heardUntil = now + Ticks(Timing.ListeningHold);
            // Every frame of a voice calls this: only the first one (or one after a pause) changes anything.
            if (Level != LiveFloorLevel.Idle && due != 0) return;
            Recompute(now, "your voice");
        }
        Pump();
    }

    /// <summary>Whether the microphone frame <paramref name="detector"/> processed last is the user's voice for <see cref="Heard"/>:
    /// it is loud, the speakers don't explain it (<paramref name="speakers"/>: Martlet's own voice or what this PC plays), and the
    /// detector hears speech now, that is at least its <see cref="VoiceActivitySettings.MinimumSpeech"/> (200 ms) of sound. A
    /// click, a key press or a knock is shorter, so it never holds back background work.</summary>
    public static bool Voice(EnergyVoiceActivityDetector detector, bool speakers)
    {
        ArgumentNullException.ThrowIfNull(detector);
        return detector.LastFrameLoud && detector.Speaking && !speakers;
    }

    /// <summary>What was heard was only a sound or filler (a hum, a cough, laughter, "mm"): Listening ends at once. Live stays.</summary>
    public void NotWords(string why = "not words")
    {
        Update(now => heardUntil = 0, why);
    }

    /// <summary>The user said real words (<see cref="RealWords"/>), or Martlet's name: Live for <see cref="LiveFloorTiming.WordsHold"/>,
    /// or until a reply takes over. The voice that was heard is words now, so its Listening hold ends (more voice renews it).</summary>
    public void Words(string why = "real words")
    {
        Update(now =>
        {
            wordsUntil = Math.Max(wordsUntil, now + Ticks(Timing.WordsHold));
            heardUntil = 0;
        }, why);
    }

    /// <summary>A reply started (to what was said or typed, or a touch): Live until <see cref="LiveFloorReply.End"/>, then for
    /// <see cref="LiveFloorTiming.Grace"/>. End it once the reply's voice is made or the reply stopped.</summary>
    public LiveFloorReply BeginReply(string why = "a reply started")
    {
        var reply = new LiveFloorReply(this, why);
        Update(now =>
        {
            replies.Add(reply);
            // The reply holds the floor now; the words that led to it no longer need to.
            wordsUntil = 0;
        }, why);
        return reply;
    }

    internal void End(LiveFloorReply reply, string why) => Update(now =>
    {
        if (replies.Remove(reply) && replies.Count == 0) graceUntil = now + Ticks(Timing.Grace);
    }, why);

    /// <summary>The conversation ended or paused: the floor is Idle at once, whatever held it.</summary>
    public void Clear(string why = "the conversation stopped")
    {
        Update(now =>
        {
            replies.Clear();
            heardUntil = wordsUntil = graceUntil = 0;
        }, why);
    }

    /// <summary>Martlet won't answer what was heard (the participation policy turned it down): the user's words and voice no
    /// longer hold the floor. A reply still does.</summary>
    public void Dismiss(string why = "Martlet won't reply") => Update(now => heardUntil = wordsUntil = 0, why);

    /// <summary>Whether <paramref name="text"/> (a quick or full transcript) is real words: the word check keeps it
    /// (<see cref="UtteranceFilter"/>) and it is more than backchannel words ("yeah", "right", "mm-hmm"), or it names Martlet.
    /// Local and instant.</summary>
    public static bool RealWords(string? text, UtteranceContext context, ListeningSensitivity sensitivity)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!UtteranceFilter.Check(text, context, sensitivity).Keep) return false;
        var words = UtteranceFilter.Words(UtteranceFilter.WithoutSounds(text!, out _));
        if (UtteranceFilter.Addressed(words, context.Names)) return true;
        return !words.All(word => BargeInPolicy.IsBackchannel(word) || UtteranceFilter.IsFiller(word));
    }

    private void Update(Action<long> change, string why)
    {
        lock (gate)
        {
            if (disposed) return;
            var now = clock.GetTimestamp();
            change(now);
            Recompute(now, why);
        }
        Pump();
    }

    // The level the holds give now; queues a change for Pump and sets the timer for the next hold to lapse. Under the gate.
    private void Recompute(long now, string why)
    {
        var next = replies.Count > 0 || now < wordsUntil || now < graceUntil ? LiveFloorLevel.Live
            : now < heardUntil ? LiveFloorLevel.Listening : LiveFloorLevel.Idle;
        var was = Level;
        if (next != was)
        {
            Volatile.Write(ref level, (int)next);
            if (next == LiveFloorLevel.Live) Interlocked.Increment(ref periods);
            var made = new LiveFloorChange(clock.GetUtcNow(), was, next, why);
            recent.Enqueue(made);
            while (recent.Count > Kept) recent.Dequeue();
            pending.Enqueue(made);
        }
        Schedule(now);
    }

    // The timer wakes when the first hold still in the future lapses; an earlier one already set stays (it reschedules then).
    private void Schedule(long now)
    {
        long next = 0;
        foreach (var until in new[] { heardUntil, wordsUntil, graceUntil })
            if (until > now && (next == 0 || until < next)) next = until;
        if (next == 0 || due != 0 && due <= next && due > now) return;
        due = next;
        timer ??= clock.CreateTimer(_ => Lapse(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(TimeSpan.FromSeconds(Math.Max(0, next - now) / (double)clock.TimestampFrequency) + TimeSpan.FromMilliseconds(1),
            Timeout.InfiniteTimeSpan);
    }

    private void Lapse()
    {
        lock (gate)
        {
            if (disposed) return;
            due = 0;
            var now = clock.GetTimestamp();
            Recompute(now, Level switch
            {
                LiveFloorLevel.Live => graceUntil >= wordsUntil ? "the reply was done" : "no reply followed the words",
                _ => $"no words for {Timing.ListeningHold.TotalSeconds:0} s"
            });
        }
        Pump();
    }

    // Raises the queued changes in order, one thread at a time and outside the gate (an observer may take its own locks and
    // read the floor).
    private void Pump()
    {
        while (true)
        {
            if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0) return;
            try
            {
                while (true)
                {
                    LiveFloorChange? next;
                    lock (gate) next = pending.Count > 0 ? pending.Dequeue() : null;
                    if (next is null) break;
                    // Each observer on its own: one that fails never keeps the others (the job board's rules) from hearing it.
                    if (Changed is { } observers)
                        foreach (var observer in observers.GetInvocationList().Cast<Action<LiveFloorChange>>())
                        {
                            try { observer(next); }
                            catch (Exception error) when (error is not OutOfMemoryException) { }
                        }
                }
            }
            finally { Volatile.Write(ref pumping, 0); }
            lock (gate)
                if (pending.Count == 0) return;
        }
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * clock.TimestampFrequency);

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }

    public override string ToString() => $"{nameof(LiveFloor)} {Level}";
}

/// <summary>One reply's hold on the live floor (<see cref="LiveFloor.BeginReply"/>). <see cref="End"/> it once the reply's voice
/// is made or the reply stopped; ending it twice changes nothing.</summary>
public sealed class LiveFloorReply
{
    private readonly LiveFloor floor;
    private int ended;

    internal LiveFloorReply(LiveFloor floor, string why)
    {
        this.floor = floor;
        Why = why;
    }

    public string Why { get; }
    public bool Ended => Volatile.Read(ref ended) != 0;

    public void End(string why = "the reply's voice was made")
    {
        if (Interlocked.Exchange(ref ended, 1) == 0) floor.End(this, why);
    }

    public override string ToString() => $"{nameof(LiveFloorReply)} ({Why})";
}
