using System.Text.Json.Serialization;

namespace Martlet.Avatar.Hosting;

/// <summary>One point on a stroke's path: where (fractions 0..1 of the renderer page, +y down, like <see cref="CharacterTouch"/>),
/// when (milliseconds since the stroke began) and what of the character is there (null: the page's hit test found nothing).</summary>
public sealed record StrokeSample(double X, double Y, int Ms, CharacterTouch? Touch = null);

/// <summary>
/// A press and drag across the character while its position is locked: the overlay can't move, so the drag is a stroke across
/// the body. Sent unprompted on the renderer's request pipe as "stroke" messages in batches: "move" while the stroke goes on
/// (its newest hit-tested samples), then one "end" with the rest. <paramref name="Id"/> numbers the stroke; <paramref name="Aspect"/>
/// is the page's width over its height, so distances along x and y compare.
/// </summary>
public sealed record CharacterStroke(int Id, string Phase, double Aspect, IReadOnlyList<StrokeSample> Samples)
{
    public const int MaximumBatch = 64, MaximumSamples = 400, MaximumMs = 120_000;

    [JsonIgnore]
    public bool IsValid => Id > 0 && Phase is "move" or "end" && double.IsFinite(Aspect) && Aspect is > 0.05 and < 20 &&
        Samples is { Count: <= MaximumBatch } && Samples.All(s => s is not null && double.IsFinite(s.X) && double.IsFinite(s.Y) &&
            s.X is >= -0.5 and <= 1.5 && s.Y is >= -0.5 and <= 1.5 && s.Ms is >= 0 and <= MaximumMs && (s.Touch is null || s.Touch.IsValid));
}

/// <summary>What a stroke was: the zones it crossed in order (repeats of the zone before dropped), the zone it spent most samples
/// on, how long it took, how far it went (page heights), its speed (page heights a second), its pace ("slow", "steady" or
/// "quick"), how many passes it made (1 plus each turn back along its main direction), where it ended from where it began
/// (<see cref="Dx"/> and <see cref="Dy"/>, page heights, +y down) and whether it went mostly sideways (<see cref="Sideways"/>).</summary>
public sealed record StrokeSummary(IReadOnlyList<string> Zones, string? Main, int Ms, double Length, double Speed, string Pace, int Passes,
    int Hits, int Samples, double Dx = 0, double Dy = 0, bool Sideways = false)
{
    /// <summary>The zones in the order first crossed, without repeats.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Distinct => [.. Zones.Distinct(StringComparer.Ordinal)];

    /// <summary>Which way a stroke that didn't turn back went: "down" (toward the feet of an upright character) or "up" when it
    /// went mostly up or down the page, else null (across, or back and forth).</summary>
    [JsonIgnore]
    public string? Way => Passes > 1 || Math.Abs(Dy) < CharacterStrokes.TurnDistance || Math.Abs(Dy) < Math.Abs(Dx) * 0.75 ? null
        : Dy > 0 ? "down" : "up";

    /// <summary>The stroke's manner for the character's ear: "slowly", "quickly" or nothing, and "back and forth" when it
    /// turned back at least once.</summary>
    [JsonIgnore]
    public string? Manner => (Pace switch { "slow" => "slowly", "quick" => "quickly", _ => null }, Passes > 1) switch
    {
        ({ } pace, true) => pace + " back and forth",
        ({ } pace, false) => pace,
        (null, true) => "back and forth",
        _ => null
    };
}

/// <summary>Summarizing a stroke's path: zones crossed, passes (back-and-forth turns), length, speed and pace.</summary>
public static class CharacterStrokes
{
    /// <summary>A turn back counts once the pointer comes back this far (page heights) from its farthest point.</summary>
    public const double TurnDistance = 0.02;
    /// <summary>Slower than this (page heights a second) is a slow caress; faster than <see cref="QuickSpeed"/> a quick rub.</summary>
    public const double SlowSpeed = 0.45, QuickSpeed = 1.2;
    /// <summary>Passes a second from which a back-and-forth stroke is quick whatever its speed (a rub).</summary>
    public const double QuickPasses = 2.5;

    public static StrokeSummary Summarize(IReadOnlyList<StrokeSample> samples, double aspect, Func<CharacterTouch, string?> zoneOf)
    {
        var path = samples.OrderBy(s => s.Ms).ToArray();
        if (path.Length == 0) return new([], null, 0, 0, 0, "steady", 1, 0, 0);
        aspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 1;
        double length = 0, travelX = 0, travelY = 0;
        for (var i = 1; i < path.Length; i++)
        {
            var dx = (path[i].X - path[i - 1].X) * aspect;
            var dy = path[i].Y - path[i - 1].Y;
            length += Math.Sqrt(dx * dx + dy * dy);
            travelX += Math.Abs(dx);
            travelY += Math.Abs(dy);
        }
        var alongX = travelX >= travelY;
        var passes = 1 + Turns(path.Select(s => alongX ? s.X * aspect : s.Y));
        var ms = Math.Max(0, path[^1].Ms - path[0].Ms);
        var seconds = Math.Max(ms, 1) / 1000.0;
        var speed = length / seconds;
        var pace = speed > QuickSpeed || (passes >= 3 && passes / seconds >= QuickPasses) ? "quick" : speed < SlowSpeed ? "slow" : "steady";
        var zones = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var hits = 0;
        foreach (var sample in path)
        {
            if (sample.Touch is not { } touch) continue;
            hits++;
            if (zoneOf(touch) is not { Length: > 0 } zone) continue;
            counts[zone] = counts.GetValueOrDefault(zone) + 1;
            if (zones.Count == 0 || zones[^1] != zone) zones.Add(zone);
        }
        var main = counts.Count == 0 ? null : counts.OrderByDescending(c => c.Value).ThenBy(c => zones.IndexOf(c.Key)).First().Key;
        return new(zones.Take(16).ToArray(), main, ms, Math.Round(length, 4), Math.Round(speed, 3), pace, passes, hits, path.Length,
            Math.Round((path[^1].X - path[0].X) * aspect, 4), Math.Round(path[^1].Y - path[0].Y, 4), alongX);
    }

    // Direction changes along one axis, each counted once the pointer has come back TurnDistance from its farthest point.
    private static int Turns(IEnumerable<double> positions)
    {
        int turns = 0, direction = 0;
        double? extreme = null;
        foreach (var p in positions)
        {
            if (extreme is not { } far) { extreme = p; continue; }
            if (direction == 0)
            {
                if (Math.Abs(p - far) >= TurnDistance) { direction = Math.Sign(p - far); extreme = p; }
                continue;
            }
            if ((p - far) * direction > 0) extreme = p;
            else if (Math.Abs(p - far) >= TurnDistance)
            {
                turns++;
                direction = -direction;
                extreme = p;
            }
        }
        return turns;
    }
}
