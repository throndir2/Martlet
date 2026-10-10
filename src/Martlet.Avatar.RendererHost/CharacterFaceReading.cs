using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Avatar.RendererHost;

/// <summary>
/// The page's reading of where Martlet draws over the face (Martlet's MCP character_face reads it): how the face is followed
/// (mesh, bones or estimate), its middle, width and tilt, and at each cheek how much shows, how wide it is for the face's width
/// and what of the character is there; the eyes, mouth and top of the head the overlay emotes sit on; the measured cheeks' size
/// (cheekSize) when vision measured the face; the eyes' irises and
/// openings (where they came from, each iris and its opening's box, size and whether the iris is inside it); with the overlays
/// showing and, for Live2D, how many mesh vertices the face is pinned to and the face's skin drawable when they are its skin.
/// Positions are fractions of the character's surface (+y down), like a tap. Kept bounded and typed for UI Automation.
/// </summary>
internal static class CharacterFaceReading
{
    internal const int MaximumName = 128, MaximumOverlays = 16, MaximumDrawables = 3, MaximumShapePoints = 512;

    /// <summary>The reading as JSON, numbered <paramref name="number"/>.</summary>
    internal static string From(JsonElement answer, int number)
    {
        var reading = new JsonObject { ["n"] = number };
        if (answer.ValueKind != JsonValueKind.Object) { reading["found"] = false; return reading.ToJsonString(); }
        reading["found"] = answer.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.True;
        if (Name(answer, "tracking") is "mesh" or "bones" or "estimate") reading["tracking"] = Name(answer, "tracking");
        foreach (var key in new[] { "x", "y", "width", "tilt" })
            if (Number(answer, key) is { } value) reading[key] = value;
        foreach (var key in new[] { "cheekLeft", "cheekRight" })
            if (answer.TryGetProperty(key, out var cheek) && cheek.ValueKind == JsonValueKind.Object) reading[key] = Cheek(cheek);
        // The eyes, mouth and top of the head the overlay emotes sit on.
        foreach (var key in new[] { "eyeLeft", "eyeRight", "mouth", "top" })
            if (answer.TryGetProperty(key, out var point) && point.ValueKind == JsonValueKind.Object &&
                Number(point, "x") is { } x && Number(point, "y") is { } y)
                reading[key] = new JsonObject { ["x"] = x, ["y"] = y };
        // The measured cheeks' radius in face widths, which sizes the blush (null without a face measured by vision).
        reading["cheekSize"] = Number(answer, "cheekSize") is { } size && size is > 0 and <= 1 ? size : null;
        if (Name(answer, "eyesFrom") is "mesh" or "bones" or "vision" or "estimate") reading["eyesFrom"] = Name(answer, "eyesFrom");
        foreach (var key in new[] { "irisLeft", "irisRight" })
            if (answer.TryGetProperty(key, out var iris))
                reading[key] = iris.ValueKind == JsonValueKind.Object ? Numbers(iris, "x", "y", "rx", "ry") : null;
        foreach (var key in new[] { "eyeLeftShape", "eyeRightShape" })
            if (answer.TryGetProperty(key, out var shape)) reading[key] = shape.ValueKind == JsonValueKind.Object ? Shape(shape) : null;
        reading["overlays"] = Names(answer, "overlays", MaximumOverlays);
        if (answer.TryGetProperty("pinned", out var pinned) && pinned.ValueKind == JsonValueKind.Object)
            reading["pinned"] = new JsonObject { ["carriers"] = Count(pinned, "carriers"), ["skin"] = Name(pinned, "skin"),
                ["milliseconds"] = Count(pinned, "milliseconds"), ["eyeMilliseconds"] = Count(pinned, "eyeMilliseconds") };
        return reading.ToJsonString();
    }

    private static JsonObject Numbers(JsonElement owner, params string[] keys)
    {
        var node = new JsonObject();
        foreach (var key in keys)
            if (Number(owner, key) is { } value) node[key] = value;
        return node;
    }

    // An eye's opening: how many points and triangles (null for an outline), its box and whether its iris's middle is in it.
    private static JsonObject Shape(JsonElement shape)
    {
        var node = new JsonObject { ["points"] = Math.Min(Count(shape, "points"), MaximumShapePoints) };
        node["triangles"] = shape.TryGetProperty("triangles", out var triangles) && triangles.ValueKind == JsonValueKind.Number
            ? Count(shape, "triangles") : null;
        foreach (var key in new[] { "left", "top", "right", "bottom" })
            if (Number(shape, key) is { } value) node[key] = value;
        node["irisInside"] = shape.TryGetProperty("irisInside", out var inside) && inside.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? inside.GetBoolean() : null;
        return node;
    }

    private static JsonObject Cheek(JsonElement cheek)
    {
        var node = new JsonObject();
        foreach (var key in new[] { "x", "y", "visible", "across" })
            if (Number(cheek, key) is { } value) node[key] = value;
        node["hit"] = cheek.TryGetProperty("hit", out var hit) && hit.ValueKind == JsonValueKind.True;
        node["drawables"] = Names(cheek, "drawables", MaximumDrawables);
        node["bone"] = Name(cheek, "bone");
        node["mesh"] = Name(cheek, "mesh");
        return node;
    }

    private static double? Number(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
        double.IsFinite(number) && Math.Abs(number) <= 1_000_000 ? Math.Round(number, 4) : null;

    private static int Count(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) &&
        count >= 0 ? count : 0;

    private static string? Name(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= MaximumName } text && !text.Any(char.IsControl) ? text : null;

    private static JsonArray Names(JsonElement owner, string property, int most) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? new JsonArray([.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).Where(text => text.Length is > 0 and <= MaximumName && !text.Any(char.IsControl))
                .Take(most).Select(text => (JsonNode?)text)])
            : [];
}
