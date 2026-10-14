namespace Martlet.Core.Speakers;

/// <summary>A new voice made from voiceprints Martlet couldn't place one by one (<see cref="UnplacedVoices.Note"/>): their
/// average, their speech seconds together and how many utterances they came from.</summary>
public sealed record UnplacedGroup(float[] Voiceprint, double Seconds, int Utterances);

/// <summary>The voices Martlet heard in the last <see cref="Window"/> but couldn't place (docs/VOICES.md#voices-heard-only-a-little):
/// a short remark (too little clean speech to add a voice from) or a voice that sounded a little like someone known (an uncertain
/// match). One such utterance never adds a voice. When <see cref="MinimumUtterances"/> of them agree with each other, have
/// <see cref="MinimumSeconds"/> of speech together and sound clearly more like each other than like anyone in the voice list,
/// they become a new voice. Voiceprints only (256 numbers each, never audio), in memory only: nothing is saved or shared, and
/// what isn't used is forgotten after <see cref="Window"/>. Thread-safe.</summary>
public sealed class UnplacedVoices
{
    /// <summary>How long a voiceprint waits for others like it.</summary>
    public static TimeSpan Window { get; } = TimeSpan.FromMinutes(30);
    /// <summary>A new voice needs at least this many utterances...</summary>
    public const int MinimumUtterances = 3;
    /// <summary>...with at least this much speech together.</summary>
    public const double MinimumSeconds = 3.0;
    /// <summary>Each voiceprint of a group agrees at least this much with the group's average (cosine). Different people score
    /// below 0.45 (<see cref="VoiceMatchOptions.NewVoiceThreshold"/>).</summary>
    public const double Agreement = 0.60;
    /// <summary>A group that sounds a little like a known voice (an uncertain match) becomes a new voice only when its voiceprints
    /// agree with each other at least this much more than its average agrees with that voice.</summary>
    public const double Margin = 0.20;
    /// <summary>The most voiceprints kept; the oldest go first.</summary>
    public const int MaximumKept = 200;

    private readonly object gate = new();
    private readonly List<Entry> entries = [];

    private sealed record Entry(float[] Print, double Seconds, DateTimeOffset At);

    /// <summary>How many voiceprints wait now (counts only, for the status).</summary>
    public int Count
    {
        get { lock (gate) return entries.Count; }
    }

    /// <summary>Forgets every voiceprint (recognition turned off, or every voice forgotten).</summary>
    public void Clear()
    {
        lock (gate) entries.Clear();
    }

    /// <summary>Forgets the voiceprints that agree with <paramref name="voiceprint"/>: a voice was just added from it, so they are
    /// that voice and not a new one.</summary>
    public void Forget(float[] voiceprint)
    {
        var print = VoicePrints.Normalize(voiceprint);
        lock (gate) entries.RemoveAll(e => VoicePrints.Cosine(e.Print, print) >= Agreement);
    }

    /// <summary>Notes a voiceprint <paramref name="roster"/> couldn't place, heard at <paramref name="now"/> with
    /// <paramref name="seconds"/> of speech. Returns the new voice it completes (and forgets the voiceprints it used), or null
    /// while it waits for more. A group whose average is a known voice was that voice after all: its voiceprints are forgotten.</summary>
    public UnplacedGroup? Note(float[] voiceprint, double seconds, DateTimeOffset now, VoiceRoster roster, VoiceMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (!double.IsFinite(seconds) || seconds <= 0) return null;
        var print = VoicePrints.Normalize(voiceprint);
        lock (gate)
        {
            entries.RemoveAll(e => now - e.At > Window || e.At > now + Window);
            entries.Add(new(print, seconds, now));
            if (entries.Count > MaximumKept) entries.RemoveRange(0, entries.Count - MaximumKept);
            var group = entries.Where(e => VoicePrints.Cosine(e.Print, print) >= Agreement).ToList();
            if (group.Count < MinimumUtterances) return null;
            var centroid = Average(group);
            group = entries.Where(e => VoicePrints.Cosine(e.Print, centroid) >= Agreement).ToList();
            if (group.Count < MinimumUtterances || group.Sum(e => e.Seconds) < MinimumSeconds) return null;
            centroid = Average(group);
            var match = roster.Identify(centroid, options);
            if (match.Kind == VoiceMatchKind.Known)
            {
                entries.RemoveAll(group.Contains);
                return null;
            }
            var together = group.Average(e => VoicePrints.Cosine(e.Print, centroid));
            if (match.Kind == VoiceMatchKind.Unsure && together - match.Score < Margin) return null;
            entries.RemoveAll(group.Contains);
            return new(centroid, Math.Round(group.Sum(e => e.Seconds), 2), group.Count);
        }
    }

    private static float[] Average(IReadOnlyList<Entry> group)
    {
        var sum = new float[VoicePrints.Dimension];
        foreach (var entry in group)
            for (var i = 0; i < sum.Length; i++) sum[i] += entry.Print[i];
        return VoicePrints.Normalize(sum);
    }
}
