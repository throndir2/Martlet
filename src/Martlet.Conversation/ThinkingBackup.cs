using System.Globalization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>Backup Thinking for one reply (Companion › Thinking pool › Backup Thinking; docs/CONVERSATION.md): when the reply's
/// first Thinking request has no first words <see cref="Delay"/> after it started, the same request also goes to a Thinking pool
/// member the owner allowed to answer for the conversation. Whichever stream has words first gives the reply, and the other is
/// stopped at once. This is a hedged request (Dean and Barroso, "The Tail at Scale", Communications of the ACM 56(2), 2013).
/// Only the reply's first request is raced: never the Thinking fallback, a tool round or a retry. The desktop sets it on
/// <see cref="ConversationRequest.Backup"/>; <see cref="ConversationTurn"/> runs the race.</summary>
public interface IThinkingBackup
{
    /// <summary>How long after the reply's Thinking request started, with no words yet, the backup is asked.</summary>
    TimeSpan Delay { get; }

    /// <summary>Opens the backup's stream of the same request: <paramref name="input"/> as <paramref name="reply"/> sends it, with
    /// <paramref name="ids"/> and <paramref name="epoch"/> on its events (<see cref="ConversationRuntime.OpenTextAsync"/>), or null
    /// when no member may take it now. <paramref name="held"/>: the reply was started early and isn't taken yet, so no paid
    /// cloud member may be asked. <paramref name="token"/> stops the stream too.</summary>
    Task<ThinkingBackupStream?> OpenAsync(ConversationRequest reply, BoundedTextInput input, CorrelationIds ids, long epoch, bool held,
        CancellationToken token);

    /// <summary>Told once per reply, when its first request has its answer (or ended) and a losing stream was stopped: what
    /// Backup Thinking did. Not told for a reply stopped before then.</summary>
    void Ended(ThinkingBackupResult result);
}

/// <summary>A backup's open stream: the member's <paramref name="Name"/> (where it runs and its model, as the log says them) and
/// what to let go once the stream is no longer read (<paramref name="Lease"/>: the member's slot and its place on the live
/// floor).</summary>
public sealed record ThinkingBackupStream(string Name, ITextGenerationStream Stream, IAsyncDisposable? Lease = null)
{
    public override string ToString() => $"{nameof(ThinkingBackupStream)} {Name}";
}

/// <summary>How Backup Thinking ended in one reply.</summary>
public enum ThinkingBackupOutcome
{
    /// <summary>The conversation's model had its first words before the delay: no member was asked.</summary>
    NotNeeded,
    /// <summary>The delay passed, but no member could take the request.</summary>
    NoMember,
    /// <summary>The member had words first and gave the reply; the conversation's model was stopped.</summary>
    Won,
    /// <summary>The conversation's model had words first; the member was stopped.</summary>
    Lost,
    /// <summary>The member failed (or ended without an answer); the conversation's model went on alone.</summary>
    Failed
}

/// <summary>What Backup Thinking did in one reply, in the turn's own time (from its start, like
/// <see cref="ConversationSnapshot.FirstTextAfter"/>): the delay it used, the member asked (null when none was), when it was
/// asked and when the stream that answered had its first words.</summary>
public sealed record ThinkingBackupResult(ThinkingBackupOutcome Outcome, TimeSpan Delay, string? Member = null,
    TimeSpan? AskedAfter = null, TimeSpan? FirstWordsAfter = null, string? Why = null)
{
    public bool Won => Outcome == ThinkingBackupOutcome.Won;
    public bool Asked => AskedAfter is not null;

    /// <summary>The desktop log's line about it, or null when nothing needed saying (the conversation's model was fast).</summary>
    public string? Describe() => Outcome switch
    {
        ThinkingBackupOutcome.Won => $"Backup Thinking: {Member} had words first, {Ms(FirstWordsAfter)} ms into the reply " +
            $"(asked at {Ms(AskedAfter)} ms after a {Ms(Delay)} ms wait); the conversation's model was stopped.",
        ThinkingBackupOutcome.Lost => $"Backup Thinking: asked {Member} at {Ms(AskedAfter)} ms after a {Ms(Delay)} ms wait; " +
            $"the conversation's model had words first at {Ms(FirstWordsAfter)} ms, so {Member} was stopped.",
        ThinkingBackupOutcome.Failed => $"Backup Thinking: asked {Member} at {Ms(AskedAfter)} ms, but it gave no answer" +
            (Why is null ? "" : $" ({Why})") + "; the conversation's model went on alone.",
        ThinkingBackupOutcome.NoMember => $"Backup Thinking: no first words after {Ms(Delay)} ms, but no member could take the " +
            "request" + (Why is null ? "." : $" ({Why})."),
        _ => null
    };

    private static string Ms(TimeSpan? value) => value is { } span ? span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) : "-";
}

/// <summary>How long the conversation's Thinking model took to its first words (from its request's start) in recent replies, for
/// Backup Thinking's automatic delay: the 95th percentile of the last <see cref="Window"/> replies, never under
/// <see cref="Minimum"/>, and <see cref="Starting"/> until <see cref="MinimumSamples"/> replies are known. Thread-safe.</summary>
public sealed class FirstWordTimes
{
    public const int Window = 20;
    public const int MinimumSamples = 3;
    /// <summary>The shortest automatic delay: a backup never races a model that is merely a little slow.</summary>
    public static TimeSpan Minimum { get; } = TimeSpan.FromMilliseconds(900);
    /// <summary>The automatic delay before enough replies are known.</summary>
    public static TimeSpan Starting { get; } = TimeSpan.FromMilliseconds(1500);
    private readonly object gate = new();
    private readonly Queue<TimeSpan> samples = new();

    /// <summary>Notes one reply's first words, counted from its Thinking request's start.</summary>
    public void Add(TimeSpan firstWords)
    {
        if (firstWords <= TimeSpan.Zero || firstWords > TimeSpan.FromMinutes(10)) return;
        lock (gate)
        {
            samples.Enqueue(firstWords);
            while (samples.Count > Window) samples.Dequeue();
        }
    }

    public void Clear() { lock (gate) samples.Clear(); }

    public int Count { get { lock (gate) return samples.Count; } }

    /// <summary>The 95th percentile (nearest rank) of the replies noted, or null when none was.</summary>
    public TimeSpan? Percentile95
    {
        get
        {
            TimeSpan[] sorted;
            lock (gate) sorted = [.. samples.Order()];
            return sorted.Length == 0 ? null : sorted[Math.Max(0, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
        }
    }

    /// <summary>The automatic delay now.</summary>
    public TimeSpan Delay => Count < MinimumSamples || Percentile95 is not { } high ? Starting : high > Minimum ? high : Minimum;

    /// <summary>The delay for a saved choice: <paramref name="fixedMs"/> when one is chosen, else the automatic one.</summary>
    public TimeSpan DelayFor(int? fixedMs) => fixedMs is { } chosen ? TimeSpan.FromMilliseconds(chosen) : Delay;

    public override string ToString() => $"{nameof(FirstWordTimes)} ({Count})";
}

/// <summary>Which Thinking pool member Backup Thinking asks: the first, in the pool's order (the one sharing least with the
/// conversation first), that the owner allowed to answer for the conversation (<see cref="ThinkingPoolSettings.AnswersForConversation"/>),
/// can run now, shares no hardware with the live conversation (<see cref="LiveResources.Shares"/>), can take the reply's request
/// as it is (its tools, picture and recording, and its length) and, while a reply started early isn't taken, isn't a paid cloud
/// provider. Nothing is asked while Backup Thinking is off, and a cloud member never unless the owner ticked it.</summary>
public static class ThinkingBackupMembers
{
    /// <summary>The member chosen (its spot in the pool's plan and its place) or none, and why in plain words.</summary>
    public sealed record Choice(DeepThinkingSpot? Spot, BackgroundPlace? Place, string Why)
    {
        public override string ToString() => $"{nameof(Choice)} {Place?.Name ?? "none"}";
    }

    /// <summary>Whether <paramref name="member"/> is a paid cloud provider: an endpoint neither on this PC nor on the home network.</summary>
    public static bool Paid(DeepThinkingSettings member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Place == DeepThinkingPlace.Endpoint && LiveResources.MachineOf(member.Origin) is null;
    }

    /// <param name="pool">The saved pool: Backup Thinking's switch and the members that may answer for the conversation.</param>
    /// <param name="plan">The pool's plan now (which members can run).</param>
    /// <param name="places">The members as the job board sees them (what each can do, its computer and graphics cards), in
    /// their order of standing.</param>
    /// <param name="live">What the live conversation runs on now.</param>
    /// <param name="input">The reply's request as it is sent.</param>
    /// <param name="held">The reply was started early and isn't taken yet.</param>
    /// <param name="cannotTake">Why a member can't take <paramref name="input"/> (too long for it), or null when it can.</param>
    public static Choice Choose(ThinkingPoolSettings pool, DeepThinkingPool plan, IReadOnlyList<BackgroundPlace> places, LiveResources live,
        BoundedTextInput input, bool held, Func<DeepThinkingSettings, string?>? cannotTake = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(input);
        if (!pool.BackupThinking) return new(null, null, "Backup Thinking is off");
        var allowed = plan.Spots.Where(spot => spot.Settings.Separate && pool.Answers(spot.Key)).ToArray();
        if (allowed.Length == 0) return new(null, null, "no member may answer for the conversation");
        List<string> why = [];
        foreach (var place in places.OrderBy(p => p.Standing))
        {
            if (allowed.FirstOrDefault(spot => spot.Key == place.Id) is not { } spot) continue;
            var member = spot.Settings;
            var name = member.Describe();
            string? problem = !spot.Plan.Available ? "can't run now"
                : live.Shares(place) ? "shares the conversation's computer"
                : held && Paid(member) ? "is a paid cloud provider and the reply isn't taken yet"
                : input.Tools.Count > 0 && member.Place != DeepThinkingPlace.Endpoint ? "can't use tools"
                : input.Image is not null && !place.Can.HasFlag(ThinkingCapability.Vision) ? "doesn't see pictures"
                : input.Audio is not null && !place.Can.HasFlag(ThinkingCapability.Audio) ? "doesn't hear recordings"
                : cannotTake?.Invoke(member);
            if (problem is null) return new(spot, place, $"{name} may answer for the conversation");
            why.Add($"{name} {problem}");
        }
        // Allowed members the job board doesn't list can't run here (left the pool, or kept for other companion PCs).
        if (why.Count == 0) why.Add("no member that may answer can run now");
        return new(null, null, string.Join("; ", why));
    }
}
