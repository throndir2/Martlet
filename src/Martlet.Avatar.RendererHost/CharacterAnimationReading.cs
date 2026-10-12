using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Hosting;

namespace Martlet.Avatar.RendererHost;

/// <summary>
/// The page's answer about a VRMA clip (see app.js's animate): whether it keeps the clip and started it, its name and length,
/// every clip it keeps and why it didn't start. Kept bounded and typed: at most <see cref="VrmAnimationClip.MaximumKept"/>
/// valid clip names and a reason of at most 300 characters without control characters.
/// </summary>
internal static class CharacterAnimationReading
{
    /// <summary>The answer as a <see cref="RendererAnimationResult"/>, named <paramref name="name"/> when the page didn't say.</summary>
    internal static RendererAnimationResult From(JsonElement answer, string name)
    {
        if (answer.ValueKind != JsonValueKind.Object) return new(false, false, name, 0, [], "The renderer didn't answer.");
        bool Is(string property) => answer.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
        var named = answer.TryGetProperty("name", out var said) && said.ValueKind == JsonValueKind.String &&
            VrmAnimationClip.IsValidName(said.GetString()) ? said.GetString()! : name;
        var duration = answer.TryGetProperty("duration", out var length) && length.ValueKind == JsonValueKind.Number &&
            length.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds is >= 0 and <= VrmAnimationClip.MaximumSeconds
            ? Math.Round(seconds, 3) : 0;
        List<string> clips = [];
        if (answer.TryGetProperty("clips", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var clip in list.EnumerateArray().Take(VrmAnimationClip.MaximumKept))
                if (clip.ValueKind == JsonValueKind.String && VrmAnimationClip.IsValidName(clip.GetString())) clips.Add(clip.GetString()!);
        var reason = answer.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String
            ? new string([.. (why.GetString() ?? "").Take(300).Select(c => char.IsControl(c) ? ' ' : c)]).Trim() : null;
        return new(Is("kept"), Is("started"), named, duration, clips, string.IsNullOrEmpty(reason) ? null : reason);
    }

    /// <summary>The answer as JSON for UI Automation (MoveAvatar's "animation" field), numbered <paramref name="number"/>, with
    /// the file it came from when MCP gave one.</summary>
    internal static string Json(RendererAnimationResult result, int number, string? file = null)
    {
        var reading = new JsonObject
        {
            ["n"] = number, ["name"] = result.Name, ["kept"] = result.Kept, ["started"] = result.Started, ["duration"] = result.Duration,
            ["clips"] = new JsonArray([.. result.Clips.Select(c => (JsonNode?)JsonValue.Create(c))])
        };
        if (file is not null) reading["file"] = file;
        if (result.Reason is { } reason) reading["reason"] = reason;
        return reading.ToJsonString();
    }
}
