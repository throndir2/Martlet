using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Puts a stroke's batches back together (<see cref="CharacterStroke"/>): which touches entered a new zone (the first
/// one starts the zone's reaction at once) and, at its end, the whole stroke's <see cref="StrokeSummary"/>. A batch of another
/// stroke starts over; batches of a stroke that already ended are ignored. Not thread-safe; feed it in order.</summary>
internal sealed class CharacterStrokeTracker
{
    private readonly List<StrokeSample> samples = [];
    private int id, ended;
    private double aspect = 1;
    private string? zone;

    /// <summary>The stroke now going on, or 0.</summary>
    internal int Current => id;

    /// <summary>Adds a batch. <paramref name="zoneOf"/> names the zone a touch is in (null: none). Returns the touches that
    /// entered another zone than the hit before them, and the summary when this batch ended the stroke.</summary>
    internal (IReadOnlyList<(CharacterTouch Touch, string Zone)> Entered, StrokeSummary? Ended) Add(CharacterStroke batch,
        Func<CharacterTouch, string?> zoneOf)
    {
        if (batch.Id == ended) return ([], null);
        if (batch.Id != id)
        {
            id = batch.Id;
            samples.Clear();
            zone = null;
        }
        aspect = batch.Aspect;
        var entered = new List<(CharacterTouch, string)>();
        foreach (var sample in batch.Samples)
        {
            if (samples.Count < CharacterStroke.MaximumSamples) samples.Add(sample);
            if (sample.Touch is not { } touch || zoneOf(touch) is not { Length: > 0 } name || name == zone) continue;
            zone = name;
            entered.Add((touch, name));
        }
        if (batch.Phase != "end") return (entered, null);
        var summary = CharacterStrokes.Summarize(samples, aspect, zoneOf);
        ended = id;
        id = 0;
        samples.Clear();
        zone = null;
        return (entered, summary);
    }
}
