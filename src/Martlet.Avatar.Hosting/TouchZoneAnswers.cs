using System.Globalization;
using System.Text.Json;

namespace Martlet.Avatar.Hosting;

/// <summary>Reads what a vision model answered about boxes, leniently: JSON as asked, a bare list, boxes keyed by zone, boxes as
/// arrays or as named edges, and, when the JSON can't be read whole (cut off by the reply's length, a stray word), every flat
/// object in it.</summary>
internal static class ZoneAnswers
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 };

    /// <summary>The objects an answer lists, each one zone, part or check.</summary>
    internal static List<JsonElement> Entries(string? answer)
    {
        var list = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(answer)) return list;
        var start = answer.IndexOfAny(['{', '[']);
        var end = Math.Max(answer.LastIndexOf('}'), answer.LastIndexOf(']'));
        if (start >= 0 && end > start && Read(answer[start..(end + 1)]) is { } root)
        {
            Collect(root, list, 0);
            if (list.Count > 0) return list;
        }
        foreach (var text in FlatObjects(answer))
            if (Read(text) is { ValueKind: JsonValueKind.Object } item && (IsEntry(item) || RawBox(item) is not null)) list.Add(item);
        return list;
    }

    /// <summary>A true or false the answer's top-level object gives under one of <paramref name="names"/>, or null.</summary>
    internal static bool? RootBool(string? answer, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        return start >= 0 && end > start && Read(answer[start..(end + 1)]) is { ValueKind: JsonValueKind.Object } root ? Bool(root, names) : null;
    }

    private static JsonElement? Read(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, Options);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static void Collect(JsonElement element, List<JsonElement> list, int depth)
    {
        if (depth > 3) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) list.Add(item);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        if (IsEntry(element))
        {
            list.Add(element);
            return;
        }
        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;
            // {"hair":[x1,y1,x2,y2]} and {"hair":{"left":...}}: keyed by zone.
            if (value.ValueKind == JsonValueKind.Array && Numbers(value) is { Length: 4 }) list.Add(Keyed(property.Name, value));
            else if (value.ValueKind == JsonValueKind.Object && (RawBox(value) is not null || Property(value, "ok", "visible") is not null))
                list.Add(Keyed(property.Name, value));
            else if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) Collect(value, list, depth + 1);
        }
    }

    private static JsonElement Keyed(string name, JsonElement value)
    {
        var entry = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(name) };
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) entry.TryAdd(property.Name, property.Value.Clone());
        else entry["box"] = value.Clone();
        return JsonSerializer.SerializeToElement(entry);
    }

    private static bool IsEntry(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && Property(element, "id", "label", "zone", "name", "part", "n", "number") is not null;

    // Every {...} with no object inside it, strings respected.
    private static IEnumerable<string> FlatObjects(string text)
    {
        var open = new List<(int Start, bool Nested)>();
        bool quoted = false, escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == '{')
            {
                if (open.Count > 0) open[^1] = (open[^1].Start, true);
                open.Add((i, false));
            }
            else if (c == '}' && open.Count > 0)
            {
                var (start, nested) = open[^1];
                open.RemoveAt(open.Count - 1);
                if (!nested) yield return text[start..(i + 1)];
            }
        }
    }

    internal static JsonElement? Property(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    /// <summary>The name an entry gives its zone or part (id, label, zone, name or part), or null.</summary>
    internal static string? Name(JsonElement entry) =>
        Property(entry, "id", "label", "zone", "name", "part") is { ValueKind: JsonValueKind.String } name ? name.GetString() : null;

    internal static int? Int(JsonElement entry, params string[] names) => Property(entry, names) switch
    {
        { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var value) => value,
        { ValueKind: JsonValueKind.String } text when int.TryParse(text.GetString()?.Trim().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
        _ => null
    };

    internal static bool? Bool(JsonElement entry, params string[] names) => Property(entry, names) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } text => text.GetString()?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "ok" or "right" or "correct" => true,
            "false" or "no" or "wrong" => false,
            _ => null
        },
        _ => null
    };

    /// <summary>An entry's box as x1, y1, x2, y2 in the answer's own units: an array (box, bbox_2d, bbox) or named edges (left,
    /// top, right, bottom; x1, y1, x2, y2; xmin, ymin, xmax, ymax; or x, y, width, height).</summary>
    internal static double[]? RawBox(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var inner = Property(entry, "box", "bbox_2d", "bbox", "box_2d", "rect");
        if (Numbers(inner) is { Length: 4 } array) return array;
        if (inner is { ValueKind: JsonValueKind.Object } nested && RawBox(nested) is { } boxed) return boxed;
        if (Named(entry, "left", "top", "right", "bottom") is { } edges) return edges;
        if (Named(entry, "x1", "y1", "x2", "y2") is { } corners) return corners;
        if (Named(entry, "xmin", "ymin", "xmax", "ymax") is { } bounds) return bounds;
        if ((Named(entry, "x", "y", "width", "height") ?? Named(entry, "x", "y", "w", "h")) is { } size)
            return [size[0], size[1], size[0] + size[2], size[1] + size[3]];
        return null;
    }

    private static double[]? Named(JsonElement entry, string a, string b, string c, string d)
    {
        var values = new[] { a, b, c, d }.Select(name => Number(Property(entry, name))).ToArray();
        return values.All(v => v is not null) ? [.. values.Select(v => v!.Value)] : null;
    }

    private static double? Number(JsonElement? element)
    {
        double value;
        if (element is { ValueKind: JsonValueKind.Number } number) value = number.GetDouble();
        else if (element is { ValueKind: JsonValueKind.String } text &&
            double.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) value = parsed;
        else return null;
        return double.IsFinite(value) ? value : null;
    }

    internal static double[]? Numbers(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array) return null;
        var values = new List<double>();
        foreach (var item in array.EnumerateArray())
            if (Number(item) is { } value) values.Add(value);
            else return null;
        return [.. values];
    }

    /// <summary>The boxes as fractions of a <paramref name="width"/> by <paramref name="height"/> picture: fractions as asked,
    /// else pixels of the picture, else the 0..1000 grid some models use. The first box of each key is kept; boxes that end up
    /// with no size inside the picture are left out.</summary>
    internal static List<(string Key, TouchZoneBox Box)> Scale(List<(string Key, double[] Box)> raw, int width, int height)
    {
        var result = new List<(string, TouchZoneBox)>();
        if (raw.Count == 0 || width <= 0 || height <= 0) return result;
        var largest = raw.SelectMany(r => r.Box).Max();
        double sx, sy;
        if (largest <= 1.5) (sx, sy) = (1, 1);
        else if (raw.All(r => Math.Max(r.Box[0], r.Box[2]) <= width * 1.05 && Math.Max(r.Box[1], r.Box[3]) <= height * 1.05)) (sx, sy) = (width, height);
        else (sx, sy) = (1000, 1000);
        foreach (var (key, b) in raw)
        {
            if (result.Any(r => r.Item1 == key)) continue;
            double x1 = Math.Clamp(Math.Min(b[0], b[2]) / sx, 0, 1), x2 = Math.Clamp(Math.Max(b[0], b[2]) / sx, 0, 1);
            double y1 = Math.Clamp(Math.Min(b[1], b[3]) / sy, 0, 1), y2 = Math.Clamp(Math.Max(b[1], b[3]) / sy, 0, 1);
            if (x2 - x1 > 0.002 && y2 - y1 > 0.002) result.Add((key, new TouchZoneBox(x1, y1, x2 - x1, y2 - y1)));
        }
        return result;
    }
}
