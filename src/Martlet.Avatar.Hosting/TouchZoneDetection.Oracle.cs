using System.Globalization;
using System.Text.Json;

namespace Martlet.Avatar.Hosting;

public static partial class TouchZoneDetection
{
    /// <summary>FIXTURE - NOT AI: a stand-in for the vision model that answers every request from known zones
    /// (<paramref name="truth"/>, fractions of the whole snapshot) as a perfect model would: the parts around them, each
    /// close-up's zones (<paramref name="guess"/>'s instead, when given, so the checks have something to correct), what is
    /// special about the character (the known zones that are, <see cref="IsSpecial"/>, with their names), and each
    /// check (a box within <see cref="Noise"/> of the known one is right, others are corrected, unknown ones aren't there and
    /// known ones without a box are added). Its answers are JSON as the instructions ask.</summary>
    public static string Oracle(ZoneAsk ask, IReadOnlyList<CharacterTouchZone> truth, IReadOnlyList<CharacterTouchZone>? guess = null)
    {
        var known = Boxes(truth);
        TouchZoneBox? Seen(TouchZoneBox box) => Clip(box.Relative(ask.Region));
        var entries = new List<string>();
        switch (ask.Kind)
        {
            case ZoneAskKind.Parts:
                foreach (var region in Regions)
                {
                    var boxes = region.Zones.Where(known.ContainsKey).Select(id => known[id]).ToArray();
                    if (boxes.Length > 0 && Seen(Union(boxes)) is { } seen) entries.Add(Entry("id", region.Id, seen));
                }
                foreach (var extra in Extras.Where(ask.Ids.Contains))
                    if (known.TryGetValue(extra, out var box) && Seen(box) is { } seen) entries.Add(Entry("id", extra, seen));
                return "{\"parts\":[" + string.Join(",", entries) + "]}";
            case ZoneAskKind.Special:
                foreach (var zone in truth.Where(z => IsSpecial(z.Id)).DistinctBy(z => z.Id))
                    if (Seen(zone.Box) is { } seen) entries.Add(Entry("id", zone.Id, seen, $"\"name\":{JsonSerializer.Serialize(zone.Name.ToLowerInvariant())},"));
                return "{\"special\":[" + string.Join(",", entries) + "]}";
            case ZoneAskKind.Zones:
                var source = guess is null ? known : Boxes(guess);
                foreach (var id in ask.Ids)
                    if (source.TryGetValue(id, out var box) && Seen(box) is { } seen) entries.Add(Entry("id", id, seen));
                return "{\"zones\":[" + string.Join(",", entries) + "]}";
            default:
                var right = true;
                foreach (var mark in ask.Marks)
                {
                    var number = mark.Number.ToString(CultureInfo.InvariantCulture);
                    if (!known.TryGetValue(mark.Id, out var box) || Seen(box) is not { } target)
                    {
                        entries.Add($"{{\"n\":{number},\"visible\":false}}");
                        right = false;
                    }
                    else if (Moved(mark.Box, target) <= Noise) entries.Add($"{{\"n\":{number},\"ok\":true}}");
                    else
                    {
                        entries.Add(Entry("n", number, target, "\"ok\":false,"));
                        right = false;
                    }
                }
                foreach (var id in ask.Ids.Where(id => ask.Marks.All(m => m.Id != id)))
                    if (known.TryGetValue(id, out var box) && Seen(box) is { } seen)
                    {
                        entries.Add(Entry("id", id, seen));
                        right = false;
                    }
                return "{\"zones\":[" + string.Join(",", entries) + "],\"done\":" + (right ? "true" : "false") + "}";
        }
    }

    private static Dictionary<string, TouchZoneBox> Boxes(IReadOnlyList<CharacterTouchZone> zones) =>
        zones.GroupBy(z => z.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Box, StringComparer.Ordinal);

    private static TouchZoneBox Union(IReadOnlyList<TouchZoneBox> boxes)
    {
        double left = boxes.Min(b => b.X), top = boxes.Min(b => b.Y);
        return new(left, top, boxes.Max(b => b.X + b.Width) - left, boxes.Max(b => b.Y + b.Height) - top);
    }

    private static string Entry(string key, string value, TouchZoneBox box, string extra = "") =>
        string.Create(CultureInfo.InvariantCulture,
            $"{{\"{key}\":{(key == "n" ? value : "\"" + value + "\"")},{extra}\"left\":{box.X:0.###},\"top\":{box.Y:0.###},\"right\":{box.X + box.Width:0.###},\"bottom\":{box.Y + box.Height:0.###}}}");
}
