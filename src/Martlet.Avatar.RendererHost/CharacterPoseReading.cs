using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Avatar.RendererHost;

/// <summary>
/// The page's reading of what the character's idle body does (Martlet's MCP character_pose reads it): for a VRM, how far into
/// a breath it is and how full the chest is, the breaths a minute, each arm's angle from straight down and its elbow's bend,
/// how far each hand's middle finger curls and the sway (degrees), and where its head, shoulders, hands and hips are, as
/// fractions of the character's surface (+y down), like a tap, with the VRMA clip that plays now (its name, seconds into it, its
/// length and how much of the body it has; null when none) and the clips kept for the model. Kept bounded and typed for UI
/// Automation.
/// </summary>
internal static class CharacterPoseReading
{
    internal static readonly string[] Bones = ["head", "neck", "leftShoulder", "rightShoulder", "leftUpperArm", "rightUpperArm",
        "leftHand", "rightHand", "leftUpperLeg", "rightUpperLeg"];

    /// <summary>The reading as JSON, numbered <paramref name="number"/>.</summary>
    internal static string From(JsonElement answer, int number)
    {
        var reading = new JsonObject { ["n"] = number };
        var found = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("found", out var value) && value.ValueKind == JsonValueKind.True;
        reading["found"] = found;
        if (answer.ValueKind != JsonValueKind.Object) return reading.ToJsonString();
        if (answer.TryGetProperty("renderer", out var renderer) && renderer.ValueKind == JsonValueKind.String &&
            renderer.GetString() is "Vrm" or "Live2D") reading["renderer"] = renderer.GetString();
        if (!found) return reading.ToJsonString();
        reading["idle"] = answer.TryGetProperty("idle", out var idle) && idle.ValueKind == JsonValueKind.True;
        if (answer.TryGetProperty("breathing", out var breathing) && breathing.ValueKind == JsonValueKind.Object)
            reading["breathing"] = Numbers(breathing, "phase", "inhale", "perMinute");
        reading["arms"] = Sides(answer, "arms", side => side.ValueKind == JsonValueKind.Object ? Numbers(side, "fromDown", "elbow") : null);
        reading["curl"] = Sides(answer, "curl", side => Number(side) is { } curl ? JsonValue.Create(curl) : null);
        if (answer.TryGetProperty("sway", out var sway) && Number(sway) is { } degrees) reading["sway"] = degrees;
        var bones = new JsonObject();
        if (answer.TryGetProperty("bones", out var points) && points.ValueKind == JsonValueKind.Object)
            foreach (var name in Bones)
                if (points.TryGetProperty(name, out var point) && point.ValueKind == JsonValueKind.Object &&
                    Numbers(point, "x", "y") is { Count: 2 } place) bones[name] = place;
        reading["bones"] = bones;
        // The VRMA clip that plays now (null when none) and the clips kept for the model.
        JsonObject? animation = null;
        if (answer.TryGetProperty("animation", out var playing) && playing.ValueKind == JsonValueKind.Object &&
            playing.TryGetProperty("name", out var clipName) && clipName.ValueKind == JsonValueKind.String &&
            Martlet.Avatar.Hosting.VrmAnimationClip.IsValidName(clipName.GetString()))
        {
            animation = new JsonObject { ["name"] = clipName.GetString() };
            foreach (var (property, measured) in Numbers(playing, "time", "duration", "weight")) animation[property] = measured?.DeepClone();
        }
        reading["animation"] = animation;
        var clips = new JsonArray();
        if (answer.TryGetProperty("clips", out var kept) && kept.ValueKind == JsonValueKind.Array)
            foreach (var clip in kept.EnumerateArray().Take(Martlet.Avatar.Hosting.VrmAnimationClip.MaximumKept))
                if (clip.ValueKind == JsonValueKind.String && Martlet.Avatar.Hosting.VrmAnimationClip.IsValidName(clip.GetString()))
                    clips.Add(clip.GetString());
        reading["clips"] = clips;
        return reading.ToJsonString();
    }

    private static JsonObject Sides(JsonElement owner, string property, Func<JsonElement, JsonNode?> read)
    {
        var node = new JsonObject();
        if (owner.TryGetProperty(property, out var sides) && sides.ValueKind == JsonValueKind.Object)
            foreach (var side in new[] { "left", "right" })
                if (sides.TryGetProperty(side, out var value) && read(value) is { } entry) node[side] = entry;
        return node;
    }

    private static JsonObject Numbers(JsonElement owner, params string[] properties)
    {
        var node = new JsonObject();
        foreach (var property in properties)
            if (owner.TryGetProperty(property, out var value) && Number(value) is { } number) node[property] = number;
        return node;
    }

    private static double? Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) &&
        Math.Abs(number) <= 1_000_000 ? Math.Round(number, 4) : null;
}
