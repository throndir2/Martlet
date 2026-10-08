using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Avatar.RendererHost;

/// <summary>
/// The page's reading of who moves the character's mouth (Martlet's MCP character_mouth reads it): how much the voice has it
/// (voice, 0 to 1), whether the voice moved it within the last second (speaking), the voice's loudness (level), how far the
/// emotes open the mouth before the voice takes it (emote), how far it is open now (open), the Live2D parameter read
/// (parameter) and how much a VRM's expressions showing block the voice's mouth (blocked). Kept bounded and typed for UI
/// Automation.
/// </summary>
internal static class CharacterMouthReading
{
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
        if (answer.TryGetProperty("parameter", out var parameter) && parameter.ValueKind == JsonValueKind.String &&
            parameter.GetString() is { Length: > 0 and <= 64 } id && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            reading["parameter"] = id;
        reading["voice"] = Share(answer, "voice") ?? 0;
        reading["speaking"] = answer.TryGetProperty("speaking", out var speaking) && speaking.ValueKind == JsonValueKind.True;
        foreach (var name in new[] { "level", "emote", "open", "blocked" })
            if (Share(answer, name) is { } share) reading[name] = share;
        return reading.ToJsonString();
    }

    // A share from 0 to 1, rounded; null when it is missing or not a finite number.
    private static double? Share(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
        double.IsFinite(number) ? Math.Round(Math.Clamp(number, 0, 1), 4) : null;
}
