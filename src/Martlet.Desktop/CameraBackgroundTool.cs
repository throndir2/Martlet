using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Discord.Calls;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Changes the camera view's background (Martlet's webcam in the owner's Discord calls) for set_camera_background.</summary>
internal interface ICallCamera
{
    /// <summary>Whether Martlet is in the owner's Discord calls (the saved choice only, so the tool list and the start of every
    /// Thinking request stay the same).</summary>
    bool Offered { get; }

    /// <summary>Uses <paramref name="color"/> or the picture creation <paramref name="picture"/> (its key or ID; <paramref name="drawn"/>
    /// when Martlet just drew it for this) as the background and says what happened, for the model.</summary>
    Task<string> SetBackgroundAsync(DiscordCameraBackground? color, string? picture, bool drawn, CancellationToken token);
}

/// <summary>What set_camera_background asked for: a solid color, a kept picture, or a new picture to draw (16:9) and use.</summary>
internal sealed record CameraBackgroundArguments(DiscordCameraBackground? Color, string? Picture, DrawArguments? Draw);

/// <summary>set_camera_background: Martlet changes its own webcam background in a Discord call (the camera view behind its
/// character): a solid color for keying, one of its pictures, or a new picture it draws for it.</summary>
internal static class CameraBackgroundTool
{
    internal const string Name = "set_camera_background";

    internal static TextToolDefinition Definition { get; } = new(Name,
        "Change your webcam background in the owner's Discord call (the camera view behind your character). Give exactly one of: " +
        "color (a plain keying color), picture (the id of one of your pictures, from list_creations or a finished drawing) or draw " +
        "(describe a new picture; it's drawn in the background and becomes your background when it's ready). Use it when someone " +
        "asks for a different background, or now and then when a new one fits the conversation. Tell the user what the result says.",
        """{"type":"object","properties":{"color":{"type":"string","enum":["green","blue","magenta","black"]},"picture":{"type":"string","description":"The id of one of your pictures."},"draw":{"type":"string","description":"A vivid English description of a new background picture: setting, style, lighting, colours. No people in front."},"title":{"type":"string","description":"A short title for a new picture."}},"additionalProperties":false}""");

    internal static (CameraBackgroundArguments? Arguments, string? Problem) Parse(string argumentsJson)
    {
        JsonObject? arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (JsonException) { arguments = null; }
        var color = Read(arguments, "color");
        var picture = Read(arguments, "picture");
        var draw = Read(arguments, "draw");
        const string usage = "Give exactly one of color, picture or draw, like {\"color\": \"green\"}, {\"picture\": \"a1b2c3d4\"} or " +
            "{\"draw\": \"a cozy cabin interior at dusk, warm lamplight, watercolour\", \"title\": \"Cabin\"}.";
        if (arguments is null || new[] { color, picture, draw }.Count(text => !string.IsNullOrEmpty(text)) != 1) return (null, usage);
        if (!string.IsNullOrEmpty(color))
            return Enum.TryParse<DiscordCameraBackground>(color, ignoreCase: true, out var chosen) && Enum.IsDefined(chosen) &&
                chosen != DiscordCameraBackground.Picture && !char.IsDigit(color[0])
                ? (new(chosen, null, null), null)
                : (null, "The color is green, blue, magenta or black.");
        if (!string.IsNullOrEmpty(picture)) return (new(null, picture, null), null);
        var drawing = new JsonObject { ["description"] = draw, ["shape"] = "wide" };
        if (Read(arguments, "title") is { Length: > 0 } title) drawing["title"] = title;
        var (parsed, problem) = PictureTools.Parse(drawing.ToJsonString());
        return parsed is null ? (null, problem) : (new(null, null, parsed), null);
    }

    /// <summary>Added to draw_picture's started message when the picture becomes the background.</summary>
    internal const string WillUse = " When it's ready it becomes your webcam background in the call.";

    private static string? Read(JsonObject? arguments, string name) =>
        arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;
}
